using System.Runtime.InteropServices;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Small cross-platform command-line helper shared by the fixtures in this project, mirroring the
/// portable `isWindows`/`shell` helpers each F# fixture in `tests/ProcessKit.Tests` defines locally.
internal static class Shell
{
    internal static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// Build a `Command` that runs `script` through the platform shell (`cmd.exe /c` on Windows,
    /// `/bin/sh -c` elsewhere).
    internal static Command Run(string script) =>
        IsWindows ? new Command("cmd.exe").Args(["/c", script]) : new Command("/bin/sh").Args(["-c", script]);
}
