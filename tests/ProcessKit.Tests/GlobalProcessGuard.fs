namespace ProcessKit.Tests

// FS3265 fires when the generic `Marshal.PtrToStructure<'T>` — the AOT-safe overload Microsoft recommends
// over the `[<RequiresDynamicCode>]` non-generic one — is instantiated with a value type: F# forms a
// `'T | null` return whose nullness it cannot track precisely. A struct can never be null, so the lost
// precision is harmless (the only such read here is the Job-Object limit struct below). Same suppression,
// for the same reason, as `src/ProcessKit/Native.Windows.fs`.
#nowarn "3265"

open System
open System.ComponentModel
open System.Runtime.InteropServices

/// Whether the assembly-wide "nothing this run spawned outlives the test host" safety net
/// (`GlobalProcessGuard`) is in force for this run.
[<RequireQualifiedAccess>]
type internal ProcessGuardState =
    /// `GlobalProcessGuard.install` has not run yet — no `[<SetUpFixture>]` has executed.
    | NotInstalled
    /// Windows: this test host is enrolled in a kill-on-close Job Object; every process the run spawns
    /// dies with the host.
    | Guarded
    /// Windows: the Job Object could not be created or joined. The run continues unguarded (a safety net
    /// must never fail a suite by its own absence); the reason is kept so a test can report it.
    | Unavailable of reason: string
    /// Not Windows: deliberately a no-op — see the module comment.
    | NotApplicable of reason: string

/// Assembly-wide safety net: no process this test run spawns may outlive the test host.
///
/// The suite spawns real children by design, and two classes of them are outside the library's own
/// containment BY CONTRACT, so no amount of per-test discipline can reach them if a test dies before its
/// own `finally`:
///
///   * `Command.LaunchDetached` — the single documented opt-out from kill-on-dispose. Such a child
///     belongs to no `ProcessGroup`: there is no handle, no group and no teardown that could reap it
///     (`DetachedLaunchTests`, `ExtraFdTests`, `WhichResolutionTests`).
///   * the ConPTY sidecar — `CreatePseudoConsole` spins up a headless conhost/OpenConsole helper OUTSIDE
///     the Job the child is placed in (an honest, documented divergence; see the ConPTY section of
///     `src/ProcessKit/Native.Windows.fs`).
///
/// A test that is killed by a timeout, cancelled, or that throws between spawning such a child and its
/// cleanup therefore leaves a live process — with its console window or modal dialog — behind for the
/// rest of the operator's session. Windows in the middle of a run are acceptable; survivors of the run
/// are not.
///
/// **Mechanism (Windows).** `install` puts the TEST HOST ITSELF into a Job Object carrying
/// `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and nothing else. Every process the host spawns is placed in
/// that Job by the kernel (a child of a job-bound process joins its parent's job), and when the last
/// handle to the Job closes, the OS terminates every process still in it. The only handle lives in this
/// process and is DELIBERATELY NEVER CLOSED by us: the kernel closes it during process rundown, so the
/// net fires when the host exits for ANY reason — a clean exit, an unhandled exception, a `TerminateProcess`
/// from the runner's timeout, or a crash where no managed teardown runs at all. This is precisely the
/// guarantee the library itself relies on for `Command.KillOnParentDeath` on Windows (see
/// `Native.Windows.createWindowsJob`), applied one level up to the harness.
///
/// **Why this variant.** The alternative — snapshot the child processes before the run and kill the
/// survivors in `[<OneTimeTearDown>]` — is less invasive but strictly weaker: a teardown hook does not
/// run when the host is killed or crashes, which is exactly the case that strands a child today. Its
/// cost was measured rather than assumed (T-394): the four fixtures most exposed to Job nesting
/// (`LimitsTests`, `MemberInfoTests`, `ContainmentBugTests`, `DetachedLaunchTests`) were run with the
/// whole test host wrapped in an external kill-on-close Job and were byte-for-byte identical to the
/// unwrapped baseline (102 passed / 19 skipped / 0 failed). That is the expected result: nested Jobs
/// have been supported since Windows 8, this Job carries no limits of its own to intersect with the
/// library's (`the guard job carries exactly one limit flag` asserts that), and `ProcessGroup.Members`,
/// `MembersInfo` and `Mechanism` are all read from the group's OWN Job handle, which an ancestor Job
/// does not change.
///
/// **What it does NOT change.** A detached child is still outside every `ProcessGroup`: it is in no
/// group's membership and still survives the disposal that reaps a contained sibling
/// (`DetachedLaunchTests` continues to assert exactly that). It is only the HOST's exit — not any
/// group's disposal — that this Job ties it to.
///
/// **Where it would not hold, and how you find out.** A `Guarded` status only says the host joined a Job;
/// it does not by itself prove that the Job actually holds the run's children. If some ancestor Job set
/// `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`, the kernel would create this host's children outside the whole
/// job chain, and the net would protect nothing while still reporting itself installed (empirically
/// reproduced on this host by launching the same experiment from a shell that happened to sit in such an
/// ambient Job). That is why the proof tests assert MEMBERSHIP of a real, deliberately stranded child
/// rather than the guard's own status: an environment that defeats the inheritance turns
/// `a child stranded by a test that never reaches its own cleanup is still in the guard Job` red instead
/// of leaving a false sense of containment.
///
/// **POSIX.** Deliberately a no-op. The problem being solved is a Windows one (a stranded child holding
/// a console window or a modal dialog on the operator's desktop), and POSIX offers no equivalent
/// primitive that could contain the host's own descendants without disturbing the containment the suite
/// is testing: the process group and the cgroup ARE the library's POSIX mechanisms, and tests assert on
/// both. The gap that leaves is real and unhedged: off Windows nothing at the harness level reaps a child
/// a test stranded, so the per-fixture discipline (`use group = …`, `killQuietly` in a `finally`) is the
/// only net a POSIX run has. It is accepted because the survivor there is an idle process rather than a
/// window on someone's desktop — NOT because some outer mechanism is known to collect it. Whether any
/// given run happens to dispose of survivors anyway is a property of how that run is launched, not of
/// this harness; if you need the guarantee off Windows, this is the gap to close.
module internal GlobalProcessGuard =

    // JOBOBJECTINFOCLASS values used below.
    [<Literal>]
    let private JobObjectBasicProcessIdList = 3

    [<Literal>]
    let private JobObjectExtendedLimitInformation = 9

    /// The one and only limit this harness Job carries: terminate everything still in the Job when its
    /// last handle closes. No memory/CPU/affinity/UI limit is set, so nothing here can intersect with (and
    /// silently tighten) the limits `LimitsTests` applies to the library's own nested Jobs.
    [<Literal>]
    let private JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000u

    // Access rights: `IsProcessInJob` needs a query right; `AssignProcessToJobObject` needs to set the
    // target's quota and to terminate it (a Job owns its members' lifetime).
    [<Literal>]
    let private PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

    [<Literal>]
    let private PROCESS_TERMINATE = 0x0001u

    [<Literal>]
    let private PROCESS_SET_QUOTA = 0x0100u

    // `QueryInformationJobObject` reports a too-small buffer for the pid list either way round: FALSE with
    // this last-error, or TRUE with an assigned count larger than the list that fitted.
    [<Literal>]
    let private ERROR_MORE_DATA = 234

    // Two DWORDs (NumberOfAssignedProcesses, NumberOfProcessIdsInList) followed by the pid array, which is
    // pointer-aligned — on 64-bit it starts right after the 8-byte header.
    [<Literal>]
    let private processIdListHeaderSize = 8

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_BASIC_LIMIT_INFORMATION =
        struct
            val mutable PerProcessUserTimeLimit: int64
            val mutable PerJobUserTimeLimit: int64
            val mutable LimitFlags: uint32
            val mutable MinimumWorkingSetSize: unativeint
            val mutable MaximumWorkingSetSize: unativeint
            val mutable ActiveProcessLimit: uint32
            val mutable Affinity: unativeint
            val mutable PriorityClass: uint32
            val mutable SchedulingClass: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private IO_COUNTERS =
        struct
            val mutable ReadOperationCount: uint64
            val mutable WriteOperationCount: uint64
            val mutable OtherOperationCount: uint64
            val mutable ReadTransferCount: uint64
            val mutable WriteTransferCount: uint64
            val mutable OtherTransferCount: uint64
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_EXTENDED_LIMIT_INFORMATION =
        struct
            val mutable BasicLimitInformation: JOBOBJECT_BASIC_LIMIT_INFORMATION
            val mutable IoInfo: IO_COUNTERS
            val mutable ProcessMemoryLimit: unativeint
            val mutable JobMemoryLimit: unativeint
            val mutable PeakProcessMemoryUsed: unativeint
            val mutable PeakJobMemoryUsed: unativeint
        end

    // The harness deliberately calls Win32 itself instead of reusing the library's own Job helpers: a
    // safety net that is built out of the code under test stops protecting the run exactly when that code
    // regresses, which is when it is needed most.
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private CreateJobObjectW(nativeint lpJobAttributes, nativeint lpName)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetInformationJobObject(nativeint hJob, int infoClass, nativeint lpInfo, uint32 cbInfo)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private QueryInformationJobObject(
        nativeint hJob,
        int infoClass,
        nativeint lpInfo,
        uint32 cbInfo,
        uint32& returnLength
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private AssignProcessToJobObject(nativeint hJob, nativeint hProcess)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private IsProcessInJob(nativeint hProcess, nativeint hJob, bool& result)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private OpenProcess(uint32 dwDesiredAccess, bool bInheritHandle, uint32 dwProcessId)

    [<DllImport("kernel32.dll")>]
    extern nativeint private GetCurrentProcess()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private CloseHandle(nativeint handle)

    let private gate = obj ()

    /// The harness Job. Never closed while the host lives: see the module comment — the kernel closing it
    /// during process rundown IS the trigger that reaps whatever the run left behind.
    let mutable private guardJobHandle = IntPtr.Zero

    let mutable private guardState = ProcessGuardState.NotInstalled

    let private lastError () =
        Win32Exception(Marshal.GetLastWin32Error()).Message

    /// Create a Job Object that terminates every process still in it once its last handle closes, and
    /// carries no other limit. Shared by `install` and by the regression tests, so a test exercises the
    /// same creation path the live guard uses.
    ///
    /// The Job is unnamed and created with no security attributes, so its handle is NOT inheritable: a
    /// child can never end up holding a handle that would keep the Job — and with it every survivor —
    /// alive past the host's death.
    let createKillOnCloseJob () : Result<nativeint, string> =
        let job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero)

        if job = IntPtr.Zero then
            Error $"CreateJobObject failed: {lastError ()}"
        else
            let mutable info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
            info.BasicLimitInformation.LimitFlags <- JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
            let buffer = Marshal.AllocHGlobal size

            try
                Marshal.StructureToPtr<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(info, buffer, false)

                if SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size) then
                    Ok job
                else
                    let message = $"SetInformationJobObject failed: {lastError ()}"
                    CloseHandle job |> ignore
                    Error message
            finally
                Marshal.FreeHGlobal buffer

    /// Close a Job handle. Only the regression tests call this — on a Job they created themselves, where
    /// closing the last handle is the very effect under test. The guard's own Job is never closed here.
    let closeJob (job: nativeint) = CloseHandle job |> ignore

    /// Put `pid` into `job`. Used by the regression tests to model a process the harness Job would have
    /// picked up by inheritance; the live guard needs no equivalent, because the kernel enrols every
    /// descendant of the host for it.
    let assignPidToJob (job: nativeint) (pid: int) : Result<unit, string> =
        let handle =
            OpenProcess(
                PROCESS_SET_QUOTA ||| PROCESS_TERMINATE ||| PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                uint32 pid
            )

        if handle = IntPtr.Zero then
            Error $"OpenProcess({pid}) failed: {lastError ()}"
        else
            try
                if AssignProcessToJobObject(job, handle) then
                    Ok()
                else
                    Error $"AssignProcessToJobObject({pid}) failed: {lastError ()}"
            finally
                CloseHandle handle |> ignore

    /// Whether `pid` is a member of `job`. `Error` only for a question that could not be asked (the
    /// process is gone, or its handle could not be opened) — never a fabricated `false`.
    let isPidInJob (job: nativeint) (pid: int) : Result<bool, string> =
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, uint32 pid)

        if handle = IntPtr.Zero then
            Error $"OpenProcess({pid}) failed: {lastError ()}"
        else
            try
                let mutable result = false

                if IsProcessInJob(handle, job, &result) then
                    Ok result
                else
                    Error $"IsProcessInJob({pid}) failed: {lastError ()}"
            finally
                CloseHandle handle |> ignore

    /// The pids currently in `job`. A point-in-time snapshot: a member can exit, and a new one join, the
    /// moment it is taken.
    let jobMemberPids (job: nativeint) : Result<int list, string> =
        let rec read (capacity: int) (attempt: int) : Result<int list, string> =
            let size = processIdListHeaderSize + capacity * IntPtr.Size
            let buffer = Marshal.AllocHGlobal size

            // Decide while the buffer is still alive, act after it is freed: `Choice1Of2` = grow and
            // retry, `Choice2Of2` = done.
            let decision =
                try
                    let mutable returnLength = 0u

                    if
                        QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, uint32 size, &returnLength)
                    then
                        let assigned = Marshal.ReadInt32(buffer, 0)

                        if assigned > capacity && attempt < 8 then
                            Choice1Of2(assigned + assigned / 2 + 16)
                        else
                            let count = min (Marshal.ReadInt32(buffer, 4)) capacity

                            Choice2Of2(
                                Ok
                                    [ for i in 0 .. count - 1 ->
                                          int (Marshal.ReadIntPtr(buffer, processIdListHeaderSize + i * IntPtr.Size)) ]
                            )
                    else
                        let errno = Marshal.GetLastWin32Error()

                        if errno = ERROR_MORE_DATA && attempt < 8 then
                            Choice1Of2(capacity * 2)
                        else
                            Choice2Of2(Error $"QueryInformationJobObject failed: {Win32Exception(errno).Message}")
                finally
                    Marshal.FreeHGlobal buffer

            match decision with
            | Choice1Of2 grown -> read grown (attempt + 1)
            | Choice2Of2 result -> result

        read 512 1

    /// The Job's limit flags — read back so a test can prove the harness added exactly one limit and no
    /// resource cap that could intersect with the library's own nested Jobs.
    let jobLimitFlags (job: nativeint) : Result<uint32, string> =
        let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
        let buffer = Marshal.AllocHGlobal size

        try
            let mutable returnLength = 0u

            if
                QueryInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size, &returnLength)
            then
                let info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION> buffer

                Ok info.BasicLimitInformation.LimitFlags
            else
                Error $"QueryInformationJobObject failed: {lastError ()}"
        finally
            Marshal.FreeHGlobal buffer

    /// The single limit flag the harness Job is expected to carry.
    let expectedLimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

    /// The harness Job's handle, or `IntPtr.Zero` when the guard is not in force.
    let guardJob () = lock gate (fun () -> guardJobHandle)

    let state () = lock gate (fun () -> guardState)

    /// Enrol this test host — and with it every process the run spawns — in a kill-on-close Job.
    /// Idempotent, and never throws: a harness that cannot install its safety net reports that (see
    /// `ProcessGuardState.Unavailable`) instead of failing a suite that would otherwise have run.
    let install () =
        lock gate (fun () ->
            match guardState with
            | ProcessGuardState.NotInstalled ->
                if not (OperatingSystem.IsWindows()) then
                    guardState <-
                        ProcessGuardState.NotApplicable
                            "a stranded child window is a Windows problem, and POSIX has no containment primitive that would not collide with the process-group/cgroup mechanisms under test; off Windows a stranded child is left to per-fixture cleanup"
                else
                    try
                        match createKillOnCloseJob () with
                        | Error reason -> guardState <- ProcessGuardState.Unavailable reason
                        | Ok job ->
                            // The pseudo-handle from `GetCurrentProcess` carries full access, so no
                            // `OpenProcess` is needed to enrol ourselves.
                            if AssignProcessToJobObject(job, GetCurrentProcess()) then
                                guardJobHandle <- job
                                guardState <- ProcessGuardState.Guarded
                            else
                                let reason = $"AssignProcessToJobObject(self) failed: {lastError ()}"
                                // Nothing was enrolled, so closing this Job kills nothing; not closing it
                                // would leak a kernel object for the life of the run.
                                CloseHandle job |> ignore
                                guardState <- ProcessGuardState.Unavailable reason
                    with error ->
                        // The interop above is expected to report failure through its return values, so
                        // reaching here means something outside that contract went wrong (an allocation
                        // failure, a host whose kernel32 does not expose one of these entry points). This
                        // runs from `[<OneTimeSetUp>]`, where a throw would fail every fixture in the
                        // assembly, and the guard is a safety net rather than a subject: record why it is
                        // absent and let the suite run.
                        guardState <- ProcessGuardState.Unavailable $"{error.GetType().Name}: {error.Message}"
            | _ ->
                // Already decided: `install` is called once per assembly from the `[<SetUpFixture>]`, and
                // re-running it would create a second Job and leak the first.
                ())
