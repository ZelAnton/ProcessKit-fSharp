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

[<SetUpFixture>]
type TestHostSetUp() =

    [<OneTimeSetUp>]
    member _.SuppressModalDialogs() =
        TestHostErrorMode.suppressModalDialogs ()

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
