# Coverage-guided fuzzing

The `ProcessKit.Fuzz` executable contains two SharpFuzz/libFuzzer targets:

- `pump` drives `Pump.readLines` and `Pump.LineBuffer` across encodings, line terminators, caps, and
  overflow modes.
- `cassette` loads arbitrary cassette bytes and, when parsing succeeds, exercises string, byte, and
  streaming replay so base64 and terminal-state reconstruction are covered too.

Both targets cap libFuzzer inputs at 64 KiB. Expected validation failures stay typed; an escaped
exception, hang, or violated buffer invariant is a crash. The seed corpus is intentionally small so
coverage feedback, rather than a hand-built generator, explores the state space.

Install or download a `libfuzzer-dotnet` driver for your platform, then run:

```powershell
pwsh scripts/fuzz.ps1 -Target Pump -LibFuzzer C:\tools\libfuzzer-dotnet-windows.exe
pwsh scripts/fuzz.ps1 -Target Cassette -LibFuzzer C:\tools\libfuzzer-dotnet-windows.exe
```

Use `-DurationSeconds` for a bounded smoke run. Crashes are written under
`artifacts/fuzz/<target>/`; convert every confirmed crash into a regression test in
`tests/ProcessKit.Tests` before clearing it.
