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

[SetUpFixture]
public sealed class TestHostSetUp
{
    [OneTimeSetUp]
    public void SuppressModalDialogs() => TestHostErrorMode.SuppressModalDialogs();
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
