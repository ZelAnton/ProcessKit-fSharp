# C# PTY sample

Demonstrates a merged PTY stream and a password-style prompt with `PtyConfig.Echo = false`. It
selects a Windows PowerShell prompt or a Linux `/bin/sh` prompt at runtime, writes a sample
credential through `TakeStdin()`, closes stdin, and prints the child's `OK` response.

Run it from the repository root:

```powershell
dotnet run --project samples/CSharp.Pty --framework net10.0
```

Expected output includes `Password:` followed by `OK`. The sample intentionally does not print the
credential. On Windows PowerShell's secure prompt suppresses its own echo; on Linux ProcessKit clears
the PTY slave echo bit. Windows needs ConPTY (Windows 10 1809+); Linux needs `openpty` and
`setsid --ctty`.
