# ProcessKit API reference

This is the generated **API reference** for [ProcessKit](https://github.com/ZelAnton/ProcessKit-fSharp) —
async child-process management for .NET with a kernel-backed no-orphan guarantee. It is built from the
XML documentation comments on the public surface of the two shipping packages:

- [`ProcessKit`](reference/processkit.html) — the core library (`Command`, `Runner`, `ProcessGroup`,
  `RunningProcess`, `Pipeline`, `Supervisor`, `CliClient`, the top-level `Exec` helpers, …).
- [`ProcessKit.Extensions.DependencyInjection`](reference/processkit-extensions-dependencyinjection.html)
  — the `Microsoft.Extensions.DependencyInjection` integration (`AddProcessKit`).

This site is a member-by-member **reference**, generated straight from the doc comments — it favors
completeness over narrative. For task-oriented guides ("I want to …" → working snippet, in both F# and
C#), start with the [documentation guides](https://github.com/ZelAnton/ProcessKit-fSharp/tree/main/docs)
in the repository instead, particularly the [Cookbook](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/docs/cookbook.md)
and [Running commands](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/docs/commands.md).

Signatures are rendered in their native F# form (e.g. curried module functions read as
`Command.arg value command`). A C# reader calls the same member through normal .NET syntax — a curried
F# function becomes a multi-argument static method, and an F# extension member such as `CommandVerbs`
(the C#-facing `RunAsync`/`OutputStringAsync`/`ExitCodeAsync`/… verbs on `Command`) is called with
ordinary C# extension-method dot-syntax (`command.RunAsync()`) even though this generator lists it as a
static call (`CommandVerbs.RunAsync(command, …)`); the
[Cookbook](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/docs/cookbook.md) shows both call
styles side by side for every capability.

Use the **Reference** menu above to browse by namespace, or the search box to jump straight to a type or
member.
