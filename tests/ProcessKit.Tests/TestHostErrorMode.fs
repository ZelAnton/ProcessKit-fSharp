namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open NUnit.Framework

module internal TestHostErrorMode =

    [<Literal>]
    let private SEM_FAILCRITICALERRORS = 0x00000001u

    [<Literal>]
    let private SEM_NOGPFAULTERRORBOX = 0x00000002u

    [<Literal>]
    let private SEM_NOOPENFILEERRORBOX = 0x00008000u

    [<Literal>]
    let SuppressedDialogModes =
        SEM_FAILCRITICALERRORS ||| SEM_NOGPFAULTERRORBOX ||| SEM_NOOPENFILEERRORBOX

    [<DllImport("kernel32.dll")>]
    extern uint32 SetErrorMode(uint32 uMode)

    [<DllImport("kernel32.dll")>]
    extern uint32 GetErrorMode()

    let suppressModalDialogs () =
        if OperatingSystem.IsWindows() then
            SetErrorMode SuppressedDialogModes |> ignore

    let getErrorMode () = GetErrorMode()

/// The one assembly-wide setup for the test host itself (NUnit runs it before any fixture in this
/// namespace). Both steps are about what the run leaves on the operator's desktop: no modal dialog from a
/// child that fails to start, and no process at all once the host is gone.
[<SetUpFixture>]
type TestHostSetUp() =

    [<OneTimeSetUp>]
    member _.PrepareTestHost() =
        TestHostErrorMode.suppressModalDialogs ()
        // Deliberately NOT paired with a `[<OneTimeTearDown>]`: the guard's Job handle must stay open
        // until the kernel closes it during process rundown — that is what reaps a child stranded by a
        // test that never reached its own cleanup. See `GlobalProcessGuard`.
        GlobalProcessGuard.install ()

[<TestFixture>]
type TestHostErrorModeTests() =

    [<Test>]
    member _.``Windows test host suppresses modal child-process error dialogs``() =
        if OperatingSystem.IsWindows() then
            let actual = TestHostErrorMode.getErrorMode ()

            Assert.That(
                actual &&& TestHostErrorMode.SuppressedDialogModes,
                Is.EqualTo TestHostErrorMode.SuppressedDialogModes
            )
        else
            Assert.Pass "SetErrorMode is Windows-only"
