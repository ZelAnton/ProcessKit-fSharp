using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProcessKit.CSharp.Tests;

/// Whether the assembly-wide safety net below is in force for this run.
internal enum ProcessGuardStatus
{
    /// `GlobalProcessGuard.Install` has not run yet — no `[SetUpFixture]` has executed.
    NotInstalled,

    /// Windows: this test host is enrolled in a kill-on-close Job Object.
    Guarded,

    /// Windows: the Job Object could not be created or joined. The run continues unguarded — a safety net
    /// must never fail a suite by its own absence — and `Reason` says why.
    Unavailable,

    /// Not Windows: deliberately a no-op.
    NotApplicable,
}

/// Assembly-wide safety net: no process this test host spawns may outlive it.
///
/// The C# counterpart of `tests/ProcessKit.Tests/GlobalProcessGuard.fs`, which carries the full rationale
/// and the proof tests. Short version: on Windows the host puts ITSELF in a Job Object with
/// `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and nothing else. Every process the run spawns joins that Job
/// through the kernel's parent→child inheritance, and the OS terminates whatever is still in it once the
/// last handle closes — which happens during the host's own rundown, so it fires on a clean exit, an
/// unhandled exception, and a `TerminateProcess` from the runner alike. The handle is therefore never
/// closed by us and there is no `[OneTimeTearDown]` counterpart.
///
/// This project's fixtures spawn lingering children (`ping`/`sleep`) that the library's own `ProcessGroup`
/// normally reaps. The guard is deliberately independent of that: the library's containment is exactly
/// what these tests exercise, so a harness net built on top of it would stop protecting the run precisely
/// when a regression makes it necessary.
///
/// Off Windows this is a no-op: the stranded-window problem is a Windows one, and the POSIX primitives
/// that could contain the host's descendants (process group, cgroup) are the library's own mechanisms,
/// which the suite asserts on. The Linux runs are hermetic through their container instead.
///
/// A `Guarded` status says the host joined a Job, which is not by itself proof that the run's children
/// join it too — an ancestor Job carrying `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK` would have the kernel
/// create them outside the whole chain. That half is asserted on real children, by `ProcessGuardTests` in
/// `tests/ProcessKit.Tests`.
internal static class GlobalProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;

    /// The one and only limit the harness Job carries: terminate everything still in it when the last
    /// handle closes. No resource cap is set, so nothing here intersects with the limits the library
    /// applies to its own (nested) Jobs.
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly object Gate = new();

    internal static ProcessGuardStatus Status { get; private set; } = ProcessGuardStatus.NotInstalled;

    /// Why the guard is not in force, when it is not. Empty while it is.
    internal static string Reason { get; private set; } = string.Empty;

    /// The harness Job handle, or `IntPtr.Zero` when the guard is not in force. Never closed: the kernel
    /// closing it as the host dies IS the trigger that reaps whatever the run left behind.
    internal static IntPtr GuardJob { get; private set; } = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    /// Enrol this test host — and with it every process the run spawns — in a kill-on-close Job.
    /// Idempotent, and never throws.
    internal static void Install()
    {
        lock (Gate)
        {
            if (Status != ProcessGuardStatus.NotInstalled)
            {
                // Already decided. Re-running would create a second Job and leak the first.
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                Status = ProcessGuardStatus.NotApplicable;
                Reason = "a stranded child window is a Windows problem; the POSIX runs are hermetic through their container";
                return;
            }

            try
            {
                // Unnamed, with no security attributes, so the handle is NOT inheritable: a child can never
                // hold a copy that would keep the Job — and with it every survivor — alive past our death.
                var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);

                if (job == IntPtr.Zero)
                {
                    Status = ProcessGuardStatus.Unavailable;
                    Reason = $"CreateJobObject failed: {LastError()}";
                    return;
                }

                var info = default(JobObjectExtendedLimitInformationStruct);
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(info, buffer, false);

                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    {
                        Status = ProcessGuardStatus.Unavailable;
                        Reason = $"SetInformationJobObject failed: {LastError()}";
                        _ = CloseHandle(job);
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // The pseudo-handle from `GetCurrentProcess` carries full access, so no `OpenProcess` is
                // needed to enrol ourselves.
                if (AssignProcessToJobObject(job, GetCurrentProcess()))
                {
                    GuardJob = job;
                    Status = ProcessGuardStatus.Guarded;
                }
                else
                {
                    Status = ProcessGuardStatus.Unavailable;
                    Reason = $"AssignProcessToJobObject(self) failed: {LastError()}";

                    // Nothing was enrolled, so closing this Job kills nothing; leaving it open would leak a
                    // kernel object for the life of the run.
                    _ = CloseHandle(job);
                }
            }
            catch (Exception error)
            {
                // The interop above reports failure through its return values, so reaching here means
                // something outside that contract went wrong (an allocation failure, a host whose kernel32
                // does not expose one of these entry points). This runs from [OneTimeSetUp], where a throw
                // would fail every fixture in the assembly, and the guard is a safety net rather than a
                // subject: record why it is absent and let the suite run.
                Status = ProcessGuardStatus.Unavailable;
                Reason = $"{error.GetType().Name}: {error.Message}";
            }
        }
    }

    /// Whether `pid` is a member of `job`. `null` for a question that could not be asked (the process is
    /// gone, or its handle could not be opened) — never a fabricated `false`.
    internal static bool? IsPidInJob(IntPtr job, int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IsProcessInJob(handle, job, out var result) ? result : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }
}
