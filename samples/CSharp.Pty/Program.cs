using System;
using System.Text;
using System.Threading.Tasks;
using ProcessKit;

static Command CreatePromptCommand()
{
    if (OperatingSystem.IsWindows())
    {
        return new Command("powershell.exe").Args(
        [
            "-NoProfile",
            "-Command",
            "$null = Read-Host -Prompt 'Password' -AsSecureString; Write-Output OK",
        ]);
    }

    if (OperatingSystem.IsLinux())
    {
        return new Command("/bin/sh").Args(
        ["-c", "printf 'Password: '; IFS= read -r password; printf 'OK\\n'"]);
    }

    throw new PlatformNotSupportedException(
        "This sample needs Windows ConPTY or Linux openpty plus setsid --ctty."
    );
}

try
{
    var command = CreatePromptCommand()
        .Pty(new PtyConfig(80, 24, false))
        .KeepStdinOpen()
        .Timeout(TimeSpan.FromSeconds(30));

    await using var process = (await command.StartAsync()).GetValueOrThrow();

    if (process.TakeStdin() is not { Value: var stdin })
    {
        Console.Error.WriteLine("PTY stdin was not available.");
        return 1;
    }

    var outputTask = Task.Run(async () =>
    {
        await foreach (var line in process.StdoutLinesAsync())
        {
            Console.WriteLine(line);
        }
    });

    if (OperatingSystem.IsWindows())
        await stdin.WriteAsync(Encoding.UTF8.GetBytes("sample-password\r"));
    else
        await stdin.WriteLineAsync("sample-password");

    await stdin.FlushAsync();
    await stdin.FinishAsync();

    await outputTask;

    var finished = await process.FinishAsync();

    if (finished is { IsOk: true, ResultValue: var completed }
        && completed.Outcome is Outcome.Exited { ExitCode: 0 })
    {
        return 0;
    }

    Console.Error.WriteLine(finished switch
    {
        { IsOk: true, ResultValue: var value } => $"Prompt exited with {value.Outcome}.",
        { IsOk: false, ErrorValue: var error } => error.Message,
    });
    return 1;
}
catch (PlatformNotSupportedException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
