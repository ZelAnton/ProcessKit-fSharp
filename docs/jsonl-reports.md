# JSONL reports

[Previous: Overview](./)

`ReportJson` is an **opt-in** `System.Text.Json` serializer for ProcessKit's own report types —
`Outcome`, `ProcessResult<string>` / `ProcessResult<byte[]>`, `ProcessGroupStats`, `RunProfile`, and
`MemberInfo` — so a finished run, a group's resource snapshot, or a member enumeration can be logged as
one self-describing JSON object per line (JSONL), without hand-copying fields or hand-calling `.ToString()`
on an enum. It ports the **shape** of ProcessKit-rs's `report-serde` feature; see
[Coming from ProcessKit-rs](from-rust.md) for the wider vocabulary map.

Nothing on `ProcessResult`/`ProcessGroupStats`/`RunProfile`/`MemberInfo` changed to add this — it is a
separate serializer you reach for explicitly, either through the `ToReportJson()` extension methods or by
passing one of `ReportJson`'s `JsonTypeInfo<'T>` properties to `JsonSerializer.Serialize` yourself.

- [The schema](#the-schema)
- [Secret hygiene](#secret-hygiene)
- [AOT / trimming](#aot--trimming)
- [Versioning](#versioning)
- [Writing a JSONL stream](#writing-a-jsonl-stream)
- [Reading a JSONL stream](#reading-a-jsonl-stream)

## The schema

Every line is one JSON object tagged with a stable `"kind"` identifier — never a raw union-case ordinal or
a `.ToString()` spelling — and every optional metric is present on **every** line, `null` when the
platform or the run could not report it. Time is always a number of **fractional seconds**
(`duration_secs`, `total_cpu_time_secs`, `cpu_time_secs`, `elapsed_secs`, …), never milliseconds.

| `kind` | Source type | Fields |
|---|---|---|
| `exited` / `signalled` / `timed_out` / `unobserved` | `Outcome` | `code` (int, `exited` only), `signal_number` (int, `signalled` only), `reason` (string, `unobserved` only) — the other two are `null` on every case that does not carry them |
| `process_result` | `ProcessResult<string>` / `ProcessResult<byte[]>` | `program`, `outcome`, `success`, `ok_codes` (int array), `duration_secs`, `truncated`, `total_lines`, `total_bytes` |
| `process_group_stats` | `ProcessGroupStats` | `active_process_count`, `peak_process_count`, `total_cpu_time_secs`, `peak_memory_bytes`, `io_read_bytes`, `io_write_bytes`, `io_read_operations`, `io_write_operations` |
| `run_profile` | `RunProfile` | `outcome`, `duration_secs`, `cpu_time_secs`, `peak_memory_bytes`, `io_read_bytes`, `io_write_bytes`, `io_read_operations`, `io_write_operations`, `samples`, `avg_cpu_cores` |
| `member_info` | `MemberInfo` | `pid`, `ppid`, `exe_name`, `start_time` (ISO-8601) |

An embedded `Outcome` (inside `process_result` / `run_profile`) is the same tagged object as the top-level
one, under the `outcome` key. Each of a `process_result` line's `total_lines` and `total_bytes` fields is
independently `null` when that dimension was not counted (for example, raw pipeline captures count bytes
but not lines); a measured zero remains `0`. `success` is the run's own `Command.OkCodes` verdict, so a
consumer never has to re-derive it from `outcome`/`ok_codes` itself.

Example: a run whose exit code `3` is in its own accepted-code set, as one line —

```json
{"kind":"process_result","program":"tool","outcome":{"kind":"exited","code":3,"signal_number":null},"success":true,"ok_codes":[0,3],"duration_secs":1.5,"truncated":false,"total_lines":null,"total_bytes":null}
```

## Secret hygiene

No converter in this feature ever reads captured stdout/stderr content, argv, or environment values — the
same exclusion the logging/tracing seam keeps (see [Observability](observability.md)). A `ProcessResult`
line reports the run — program name, outcome, timings, truncation totals — and leaves the streams to the
caller, who already holds them; a `MemberInfo` line carries no `args`/`cmdline`/`env` key at all, on any
platform. Every test in `ReportJsonTests.fs` / `ReportJsonTests.cs` that plants a token in a captured
stream or a member's argv asserts it never reaches the wire.

## AOT / trimming

`ReportJson`'s `JsonTypeInfo<'T>` properties are built with `JsonMetadataServices.CreateValueInfo` over
hand-written `JsonConverter<'T>`s — **not** `System.Text.Json`'s reflection-based default resolver, and
not a source-generated `JsonSerializerContext` either: that generator is a Roslyn C# source generator and
does not run against F# projects, which is exactly why this library builds its own metadata by hand
instead. The result is the same guarantee a `JsonSerializerContext` gives a C# library — safe for a
trimmed or NativeAOT app — reached by the "or equivalent explicit `JsonTypeInfo` metadata" route.

## Versioning

Every one of these report types is `[<Sealed>]` with an internal constructor and grows fields across
minor releases without breaking this schema's readers. That makes the promise the same one any
self-describing JSONL format needs: **a consumer must ignore keys it does not recognize.** A field's
spelling and unit, once shipped, are never renamed, repurposed, or given a different unit without a major
release.

**Serialize only — deliberately no `Deserialize`.** These are values ProcessKit *reports*, never values a
caller supplies back to it. Every converter's `Read` throws `NotSupportedException`; a
`JsonSerializer.Deserialize` call against one of `ReportJson`'s `JsonTypeInfo<'T>` values fails loudly
instead of fabricating a value. Read a JSONL stream generically (`JsonDocument` /
`System.Text.Json.Nodes.JsonNode`, or your own DTOs), the same way you would read any external JSONL
format — see [Reading a JSONL stream](#reading-a-jsonl-stream) below.

## Writing a JSONL stream

**F#**

<!-- docsnippet:imports System.IO -->
```fsharp
task {
    match! cmd.OutputStringAsync() with
    | Ok result ->
        use writer = new StreamWriter("run-report.jsonl", append = true)
        do! writer.WriteLineAsync(result.ToReportJson())
    | Error error -> fail error
}
```

**C#**

<!-- docsnippet:imports System.IO -->
```csharp
var result = await cmd.OutputStringAsync();

if (result is { IsOk: true, ResultValue: var value })
{
    await using var writer = new StreamWriter("run-report.jsonl", append: true);
    await writer.WriteLineAsync(value.ToReportJson());
}
```

`ToReportJson()` returns one compact object with no embedded newline, so appending `\n` (a plain
`WriteLine`) after it is always a valid JSONL line. Mixing report types in one file is fine — every line
carries its own `"kind"`, so a reader dispatches on it without knowing which type produced which line.

## Reading a JSONL stream

Because the schema is serialize-only, read a line back with `System.Text.Json`'s ordinary
document/element API (or your own record types), dispatching on `"kind"`:

**F#**

```fsharp
let readReportLine (line: string) : unit =
    use doc = System.Text.Json.JsonDocument.Parse line
    let root = doc.RootElement

    match root.GetProperty("kind").GetString() with
    | "process_result" ->
        let program = root.GetProperty("program").GetString()
        let success = root.GetProperty("success").GetBoolean()
        printfn "%s -> success=%b" program success
    | "process_group_stats" -> printfn "active=%d" (root.GetProperty("active_process_count").GetInt32())
    | other -> printfn "unrecognized report kind: %s" other
```

**C#**

<!-- docsnippet:imports System.Text.Json -->
```csharp
void ReadReportLine(string line)
{
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;

    switch (root.GetProperty("kind").GetString())
    {
        case "process_result":
            var program = root.GetProperty("program").GetString();
            var success = root.GetProperty("success").GetBoolean();
            Console.WriteLine($"{program} -> success={success}");
            break;
        case "process_group_stats":
            Console.WriteLine($"active={root.GetProperty("active_process_count").GetInt32()}");
            break;
        default:
            Console.WriteLine($"unrecognized report kind: {root.GetProperty("kind").GetString()}");
            break;
    }
}
```

A future minor release may add a key to any of these objects; reading by name (`GetProperty("kind")`, …)
rather than binding the whole line to a frozen shape is what keeps a reader forward-compatible with that.

---

Next: [Dependency injection](dependency-injection.md)
