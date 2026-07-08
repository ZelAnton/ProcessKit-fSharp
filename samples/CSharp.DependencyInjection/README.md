# C# Dependency Injection Sample

Runnable C# console app showing `ProcessKit.Extensions.DependencyInjection`:

- `AddProcessKit` registers `IProcessRunner` without coupling the core library to a container.
- A pre-registered `ScriptedRunner` is preserved, so the sample is subprocess-free.
- `AddProcessKitClient` registers a keyed `CliClient`.

Run from the repository root after building the library:

```bash
dotnet run --project samples/CSharp.DependencyInjection/CSharp.DependencyInjection.csproj --framework net10.0
```
