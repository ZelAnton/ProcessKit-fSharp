using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace ProcessKit.CSharp.Tests;

internal static class TestHostErrorMode
{
    private const uint SemFailCriticalErrors = 0x00000001;
    private const uint SemNoGpFaultErrorBox = 0x00000002;
    private const uint SemNoOpenFileErrorBox = 0x00008000;

    internal const uint SuppressedDialogModes =
        SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    [DllImport("kernel32.dll")]
    internal static extern uint GetErrorMode();

    internal static void SuppressModalDialogs()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetErrorMode(SuppressedDialogModes);
        }
    }
}

/// The one assembly-wide setup for the test host itself (NUnit runs it before any fixture here). Both
/// steps are about what the run leaves behind on the operator's desktop: no modal dialog from a child that
/// fails to start, and no process at all once the host is gone.
[SetUpFixture]
public sealed class TestHostSetUp
{
    [OneTimeSetUp]
    public void PrepareTestHost()
    {
        TestHostErrorMode.SuppressModalDialogs();

        // Deliberately NOT paired with a [OneTimeTearDown]: the guard's Job handle must stay open until
        // the kernel closes it during process rundown — that is what reaps a child stranded by a test
        // that never reached its own cleanup. See GlobalProcessGuard.
        GlobalProcessGuard.Install();
    }
}

[TestFixture]
public sealed class GlobalProcessGuardTests
{
    [Test]
    public void TestHostIsEnrolledInTheGuardJobOrHonestlyReportsWhyNot()
    {
        switch (GlobalProcessGuard.Status)
        {
            case ProcessGuardStatus.NotInstalled:
                Assert.Fail("the [SetUpFixture] that installs the process guard did not run");
                break;

            case ProcessGuardStatus.NotApplicable:
                Assert.That(OperatingSystem.IsWindows(), Is.False, "Windows must not report the guard as inapplicable");
                Assert.That(GlobalProcessGuard.Reason, Is.Not.Empty, "a no-op guard must explain itself");
                Assert.That(GlobalProcessGuard.GuardJob, Is.EqualTo(IntPtr.Zero));
                break;

            case ProcessGuardStatus.Unavailable:
                Assert.Fail($"the test host is running unguarded: {GlobalProcessGuard.Reason}");
                break;

            case ProcessGuardStatus.Guarded:
                Assert.That(OperatingSystem.IsWindows(), Is.True, "only Windows has a Job Object to be guarded by");
                Assert.That(GlobalProcessGuard.GuardJob, Is.Not.EqualTo(IntPtr.Zero));

                // Enrolment of the host is what this project asserts; that the children then join the Job
                // through the kernel, and that closing such a Job kills what is left in it, are proven on
                // real children by ProcessGuardTests in tests/ProcessKit.Tests.
                Assert.That(
                    GlobalProcessGuard.IsPidInJob(GlobalProcessGuard.GuardJob, Environment.ProcessId),
                    Is.True,
                    "the test host is not a member of the Job it is supposed to be guarded by");
                break;

            default:
                Assert.Fail($"unknown guard status: {GlobalProcessGuard.Status}");
                break;
        }
    }
}

[TestFixture]
public sealed class TestHostErrorModeTests
{
    [Test]
    public void WindowsTestHostSuppressesModalChildProcessErrorDialogs()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Pass("SetErrorMode is Windows-only");
        }

        var actual = TestHostErrorMode.GetErrorMode();
        Assert.That(actual & TestHostErrorMode.SuppressedDialogModes,
            Is.EqualTo(TestHostErrorMode.SuppressedDialogModes));
    }
}
