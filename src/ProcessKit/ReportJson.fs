namespace ProcessKit

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata

/// The write-only wire form shared by every `ReportJson` converter: how a value that has NO curated
/// machine name (there is none of those here — `Outcome` is the only enum-shaped payload this feature
/// serializes) would be spelled, and the one helper every converter shares, so a measurement a platform
/// cannot report reaches the wire as an explicit `null` on every single field, never a silently omitted
/// key and never a fabricated `0`.
///
/// Ports the shape (not the code) of ProcessKit-rs's `report_serde` feature: `Serialize`-only, one stable
/// `"kind"` identifier per tagged shape, one time unit (`*_secs`, fractional seconds), and no captured
/// stdout/stderr/argv/environment anywhere on the wire.
[<RequireQualifiedAccess>]
module internal ReportJsonWrite =

    /// Writes an `Outcome` as `{"kind": "<identifier>", "code": <int|null>, "signal_number": <int|null>,
    /// ["reason": "<string>"]}`. `kind` is the outcome's own stable identifier — `exited` / `signalled` /
    /// `timed_out` / `unobserved` — never a raw union-case ordinal or `.ToString()` spelling. `code` and
    /// `signal_number` are always present, `null` on every case that does not carry them, so a JSONL reader
    /// never has to special-case a missing key; `reason` is present only on `unobserved`, the one case with
    /// a payload the other three have no field for.
    let outcome (writer: Utf8JsonWriter) (value: Outcome) : unit =
        writer.WriteStartObject()

        match value with
        | Outcome.Exited code ->
            writer.WriteString("kind", "exited")
            writer.WriteNumber("code", code)
            writer.WriteNull "signal_number"
        | Outcome.Signalled signal ->
            writer.WriteString("kind", "signalled")
            writer.WriteNull "code"

            match signal with
            | Some number -> writer.WriteNumber("signal_number", number)
            | None -> writer.WriteNull "signal_number"
        | Outcome.TimedOut ->
            writer.WriteString("kind", "timed_out")
            writer.WriteNull "code"
            writer.WriteNull "signal_number"
        | Outcome.Unobserved reason ->
            writer.WriteString("kind", "unobserved")
            writer.WriteNull "code"
            writer.WriteNull "signal_number"
            writer.WriteString("reason", reason)

        writer.WriteEndObject()

    /// An optional `int64` metric: the measurement when the platform reported one, an explicit JSON
    /// `null` — never an omitted key, never a fabricated `0` — when it did not.
    let optionalInt64 (writer: Utf8JsonWriter) (name: string) (value: int64 option) : unit =
        match value with
        | Some measured -> writer.WriteNumber(name, measured)
        | None -> writer.WriteNull name

    /// An optional `TimeSpan` metric, written as fractional seconds (this schema's one time unit) under
    /// `name` — an explicit `null` when the platform did not report it.
    let optionalSeconds (writer: Utf8JsonWriter) (name: string) (value: TimeSpan option) : unit =
        match value with
        | Some elapsed -> writer.WriteNumber(name, elapsed.TotalSeconds)
        | None -> writer.WriteNull name

    /// An optional `double` metric — an explicit `null` when the platform did not report it (or, for
    /// `RunProfile.AvgCpuCores`, when it cannot be derived at all).
    let optionalDouble (writer: Utf8JsonWriter) (name: string) (value: double option) : unit =
        match value with
        | Some measured -> writer.WriteNumber(name, measured)
        | None -> writer.WriteNull name

/// Serializes an `Outcome` on its own — see `ReportJsonWrite.outcome` for the wire shape. `Read` always
/// throws: this schema is deliberately `Serialize`-only (see `ReportJson`'s own doc comment for why).
[<Sealed>]
type internal OutcomeJsonConverter() =
    inherit JsonConverter<Outcome>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : Outcome =
        raise (
            NotSupportedException
                "ReportJson serializes an Outcome for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: Outcome, _options: JsonSerializerOptions) : unit =
        ReportJsonWrite.outcome writer value

/// Serializes a `ProcessResult<'T>` as one JSONL report line: `program`, its `outcome`, whether it
/// `success`-ed under the command's own `ok_codes`, the run's `duration_secs`, whether the capture was
/// `truncated`, and — only when this run actually counted them — the `total_lines`/`total_bytes` an
/// `OutputTooLarge` refusal would have quoted. Payload-agnostic like the Rust impl it ports the shape of:
/// `'T` never appears on the wire, because the whole point of this feature is secret hygiene — captured
/// `Stdout`/`Stderr` (and `Combined`) are never read here, whatever `'T` is.
[<Sealed>]
type internal ProcessResultJsonConverter<'T>() =
    inherit JsonConverter<ProcessResult<'T>>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : ProcessResult<'T> =
        raise (
            NotSupportedException
                "ReportJson serializes a ProcessResult for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: ProcessResult<'T>, _options: JsonSerializerOptions) : unit =
        writer.WriteStartObject()
        writer.WriteString("kind", "process_result")
        writer.WriteString("program", value.Program)
        writer.WritePropertyName "outcome"
        ReportJsonWrite.outcome writer value.Outcome
        writer.WriteBoolean("success", value.IsSuccess)
        writer.WritePropertyName "ok_codes"
        writer.WriteStartArray()

        for code in value.AcceptedCodes do
            writer.WriteNumberValue code

        writer.WriteEndArray()
        writer.WriteNumber("duration_secs", value.Duration.TotalSeconds)
        writer.WriteBoolean("truncated", value.Truncated)

        let totalLines, totalBytes = value.OverflowTotals

        match totalLines with
        | Some measured -> writer.WriteNumber("total_lines", measured)
        | None -> writer.WriteNull "total_lines"

        match totalBytes with
        | Some measured -> writer.WriteNumber("total_bytes", measured)
        | None -> writer.WriteNull "total_bytes"

        writer.WriteEndObject()

/// Serializes a `ProcessGroupStats` snapshot: the live `active_process_count` (always known — it is the
/// group's own membership count) plus every optional platform metric, each `null` on its own when the
/// mechanism cannot report it (see `ProcessGroupStats`'s own doc comment for which mechanism reports what).
[<Sealed>]
type internal ProcessGroupStatsJsonConverter() =
    inherit JsonConverter<ProcessGroupStats>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : ProcessGroupStats =
        raise (
            NotSupportedException
                "ReportJson serializes a ProcessGroupStats for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: ProcessGroupStats, _options: JsonSerializerOptions) : unit =
        writer.WriteStartObject()
        writer.WriteString("kind", "process_group_stats")
        writer.WriteNumber("active_process_count", value.ActiveProcessCount)
        ReportJsonWrite.optionalInt64 writer "peak_process_count" value.PeakProcessCount
        ReportJsonWrite.optionalSeconds writer "total_cpu_time_secs" value.TotalCpuTime
        ReportJsonWrite.optionalInt64 writer "peak_memory_bytes" value.PeakMemoryBytes
        ReportJsonWrite.optionalInt64 writer "io_read_bytes" value.IoReadBytes
        ReportJsonWrite.optionalInt64 writer "io_write_bytes" value.IoWriteBytes
        ReportJsonWrite.optionalInt64 writer "io_read_operations" value.IoReadOperations
        ReportJsonWrite.optionalInt64 writer "io_write_operations" value.IoWriteOperations
        writer.WriteEndObject()

/// Serializes a `RunProfile` — one finished run's resource summary from `RunningProcess.ProfileAsync`:
/// its `outcome`, `duration_secs`, the optional CPU/memory/I/O telemetry (each `null` when the platform or
/// containment shape could not report it), the sampling tick count, and the derived `avg_cpu_cores`.
[<Sealed>]
type internal RunProfileJsonConverter() =
    inherit JsonConverter<RunProfile>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : RunProfile =
        raise (
            NotSupportedException
                "ReportJson serializes a RunProfile for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: RunProfile, _options: JsonSerializerOptions) : unit =
        writer.WriteStartObject()
        writer.WriteString("kind", "run_profile")
        writer.WritePropertyName "outcome"
        ReportJsonWrite.outcome writer value.Outcome
        writer.WriteNumber("duration_secs", value.Duration.TotalSeconds)
        ReportJsonWrite.optionalSeconds writer "cpu_time_secs" value.CpuTime
        ReportJsonWrite.optionalInt64 writer "peak_memory_bytes" value.PeakMemoryBytes
        ReportJsonWrite.optionalInt64 writer "io_read_bytes" value.IoReadBytes
        ReportJsonWrite.optionalInt64 writer "io_write_bytes" value.IoWriteBytes
        ReportJsonWrite.optionalInt64 writer "io_read_operations" value.IoReadOperations
        ReportJsonWrite.optionalInt64 writer "io_write_operations" value.IoWriteOperations
        writer.WriteNumber("samples", value.Samples)
        ReportJsonWrite.optionalDouble writer "avg_cpu_cores" value.AvgCpuCores
        writer.WriteEndObject()

/// Serializes a `MemberInfo` snapshot: the always-present `pid` plus `ppid` / `exe_name` / `start_time`,
/// each `null` where the platform could not report it. There is deliberately no `args`/`cmdline`/`env`
/// key — `MemberInfo` itself never holds a command line or environment (see its own doc comment), so
/// there is nothing here for a future field to accidentally fill in.
[<Sealed>]
type internal MemberInfoJsonConverter() =
    inherit JsonConverter<MemberInfo>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : MemberInfo =
        raise (
            NotSupportedException
                "ReportJson serializes a MemberInfo for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: MemberInfo, _options: JsonSerializerOptions) : unit =
        writer.WriteStartObject()
        writer.WriteString("kind", "member_info")
        writer.WriteNumber("pid", value.Pid)

        match value.Ppid with
        | Some ppid -> writer.WriteNumber("ppid", ppid)
        | None -> writer.WriteNull "ppid"

        match value.ExeName with
        | Some exeName -> writer.WriteString("exe_name", exeName)
        | None -> writer.WriteNull "exe_name"

        match value.StartTime with
        | Some startTime -> writer.WriteString("start_time", startTime)
        | None -> writer.WriteNull "start_time"

        writer.WriteEndObject()

/// Serializes a `LimitEvidence` report: one `LimitVerdict` per axis (`memory`/`processes`/`cpu`), each
/// written as its stable machine identifier — `"tripped"` / `"not_tripped"` / `"unknown"` — never a raw
/// union-case ordinal or a BCL `.ToString()` spelling, matching every other converter in this schema.
[<Sealed>]
type internal LimitEvidenceJsonConverter() =
    inherit JsonConverter<LimitEvidence>()

    override _.HandleNull = true

    override _.Read(_reader, _typeToConvert, _options) : LimitEvidence =
        raise (
            NotSupportedException
                "ReportJson serializes a LimitEvidence for a report line; it deliberately never deserializes one back (write-only report schema — see docs/jsonl-reports.md)."
        )

    override _.Write(writer: Utf8JsonWriter, value: LimitEvidence, _options: JsonSerializerOptions) : unit =
        let verdictName (verdict: LimitVerdict) : string =
            match verdict with
            | LimitVerdict.Tripped -> "tripped"
            | LimitVerdict.NotTripped -> "not_tripped"
            | LimitVerdict.Unknown -> "unknown"

        writer.WriteStartObject()
        writer.WriteString("kind", "limit_evidence")
        writer.WriteString("memory", verdictName value.Memory)
        writer.WriteString("processes", verdictName value.Processes)
        writer.WriteString("cpu", verdictName value.Cpu)
        writer.WriteEndObject()

/// AOT-safe `System.Text.Json` metadata for the opt-in JSONL report serializer: one self-describing JSON
/// object per line for `Outcome`, `ProcessResult&lt;string&gt;`/`ProcessResult&lt;byte[]&gt;`,
/// `ProcessGroupStats`, `RunProfile`, `MemberInfo`, and `LimitEvidence` — the shapes this port has today.
/// Ports the **shape**, not the code, of ProcessKit-rs's `report-serde` feature; see
/// `docs/jsonl-reports.md` for the full schema, the versioning promise, and C#/F# consumer examples
/// reading a JSONL stream.
///
/// **Opt-in.** Nothing on `ProcessResult`/`ProcessGroupStats`/`RunProfile`/`MemberInfo`/`LimitEvidence`
/// themselves changed — this is a separate serializer you reach for explicitly, via
/// `JsonSerializer.Serialize(value, ReportJson.OutcomeTypeInfo)` or the `ToReportJson()` extension methods
/// on `ReportJsonExtensions`.
///
/// **Four rules, matching the source feature:**
///  1. **A tagged shape carries a `"kind"` identifier**, spelled as this schema's own stable, documented
///     machine name — never a raw union-case ordinal or a BCL `.ToString()`. `Outcome` is `exited` /
///     `signalled` / `timed_out` / `unobserved`; each report line's own envelope is `process_result` /
///     `process_group_stats` / `run_profile` / `member_info` / `limit_evidence`.
///  2. **`Serialize` only — deliberately no `Deserialize`.** These are values the library *reports*, never
///     values a caller supplies back to it; every converter's `Read` throws `NotSupportedException`. Every
///     `JsonTypeInfo&lt;'T&gt;` below is still safe to pass to a `JsonSerializer.Deserialize` call by a
///     caller who ignores this and does so anyway — it simply throws instead of fabricating a value.
///  3. **Reports *about* processes, never what a process produced.** No converter here ever reads captured
///     stdout/stderr content, argv, or environment values — `ProcessResult.Stdout`/`Stderr`/`Combined` are
///     never touched, whatever `'T` is.
///  4. **Fields are additive; a field's spelling and unit are frozen.** Every one of these report types is
///     `[&lt;Sealed&gt;]` with an internal constructor and grows fields across minor releases without
///     breaking this schema's readers — a JSONL consumer must ignore keys it does not recognize, the same
///     discipline any self-describing format needs. Time is always a number of fractional seconds
///     (`duration_secs`, `total_cpu_time_secs`, `cpu_time_secs`, …); a measurement the platform cannot
///     report is `null`, never a fabricated `0`.
[<RequireQualifiedAccess>]
module ReportJson =

    // `System.Text.Json` requires a `TypeInfoResolver` before a `JsonSerializerOptions` can be used at
    // all, even though every `JsonTypeInfo` below is hand-built (via `JsonMetadataServices.CreateValueInfo`)
    // and never asks `options` to resolve anything. An explicitly empty resolver chain satisfies that
    // requirement without pulling in `DefaultJsonTypeInfoResolver`'s reflection-based fallback — the one
    // thing this feature must not depend on for trimming/NativeAOT.
    let private options =
        let o = JsonSerializerOptions()
        o.TypeInfoResolver <- JsonTypeInfoResolver.Combine()
        o

    /// AOT-safe metadata for serializing an `Outcome` on its own, e.g. from a supervision or streaming
    /// callback that only has the outcome in hand.
    let OutcomeTypeInfo: JsonTypeInfo<Outcome> =
        JsonMetadataServices.CreateValueInfo<Outcome>(options, OutcomeJsonConverter())

    /// AOT-safe metadata for serializing a `ProcessResult&lt;string&gt;` — the text capture verbs'
    /// (`OutputStringAsync`, `RunAsync`, …) result type.
    let ProcessResultStringTypeInfo: JsonTypeInfo<ProcessResult<string>> =
        JsonMetadataServices.CreateValueInfo<ProcessResult<string>>(options, ProcessResultJsonConverter<string>())

    /// AOT-safe metadata for serializing a `ProcessResult&lt;byte[]&gt;` — `OutputBytesAsync`'s result type.
    let ProcessResultBytesTypeInfo: JsonTypeInfo<ProcessResult<byte[]>> =
        JsonMetadataServices.CreateValueInfo<ProcessResult<byte[]>>(options, ProcessResultJsonConverter<byte[]>())

    /// AOT-safe metadata for serializing a `ProcessGroupStats` snapshot.
    let ProcessGroupStatsTypeInfo: JsonTypeInfo<ProcessGroupStats> =
        JsonMetadataServices.CreateValueInfo<ProcessGroupStats>(options, ProcessGroupStatsJsonConverter())

    /// AOT-safe metadata for serializing a `RunProfile`.
    let RunProfileTypeInfo: JsonTypeInfo<RunProfile> =
        JsonMetadataServices.CreateValueInfo<RunProfile>(options, RunProfileJsonConverter())

    /// AOT-safe metadata for serializing a `MemberInfo` snapshot.
    let MemberInfoTypeInfo: JsonTypeInfo<MemberInfo> =
        JsonMetadataServices.CreateValueInfo<MemberInfo>(options, MemberInfoJsonConverter())

    /// AOT-safe metadata for serializing a `LimitEvidence` report.
    let LimitEvidenceTypeInfo: JsonTypeInfo<LimitEvidence> =
        JsonMetadataServices.CreateValueInfo<LimitEvidence>(options, LimitEvidenceJsonConverter())

/// C#-friendly `ToReportJson()` overloads over `ReportJson`'s metadata — one compact JSON object per call,
/// with no embedded newline, so appending `Environment.NewLine` (or `\n`) after it is a valid JSONL line.
/// F# callers can use these too, or call `JsonSerializer.Serialize(value, ReportJson.OutcomeTypeInfo)`
/// (etc.) directly.
[<Extension>]
type ReportJsonExtensions =

    /// This outcome as one JSONL report line (`ReportJson.OutcomeTypeInfo`).
    [<Extension>]
    static member ToReportJson(outcome: Outcome) : string =
        ArgumentNullException.ThrowIfNull outcome
        JsonSerializer.Serialize(outcome, ReportJson.OutcomeTypeInfo)

    /// This result as one JSONL report line (`ReportJson.ProcessResultStringTypeInfo`) — never the
    /// captured stdout/stderr.
    [<Extension>]
    static member ToReportJson(result: ProcessResult<string>) : string =
        ArgumentNullException.ThrowIfNull result
        JsonSerializer.Serialize(result, ReportJson.ProcessResultStringTypeInfo)

    /// This result as one JSONL report line (`ReportJson.ProcessResultBytesTypeInfo`) — never the
    /// captured stdout/stderr.
    [<Extension>]
    static member ToReportJson(result: ProcessResult<byte[]>) : string =
        ArgumentNullException.ThrowIfNull result
        JsonSerializer.Serialize(result, ReportJson.ProcessResultBytesTypeInfo)

    /// This snapshot as one JSONL report line (`ReportJson.ProcessGroupStatsTypeInfo`).
    [<Extension>]
    static member ToReportJson(stats: ProcessGroupStats) : string =
        ArgumentNullException.ThrowIfNull stats
        JsonSerializer.Serialize(stats, ReportJson.ProcessGroupStatsTypeInfo)

    /// This profile as one JSONL report line (`ReportJson.RunProfileTypeInfo`).
    [<Extension>]
    static member ToReportJson(profile: RunProfile) : string =
        ArgumentNullException.ThrowIfNull profile
        JsonSerializer.Serialize(profile, ReportJson.RunProfileTypeInfo)

    /// This member snapshot as one JSONL report line (`ReportJson.MemberInfoTypeInfo`).
    [<Extension>]
    static member ToReportJson(memberInfo: MemberInfo) : string =
        ArgumentNullException.ThrowIfNull memberInfo
        JsonSerializer.Serialize(memberInfo, ReportJson.MemberInfoTypeInfo)

    /// This resource-limit evidence as one JSONL report line (`ReportJson.LimitEvidenceTypeInfo`).
    [<Extension>]
    static member ToReportJson(evidence: LimitEvidence) : string =
        ArgumentNullException.ThrowIfNull evidence
        JsonSerializer.Serialize(evidence, ReportJson.LimitEvidenceTypeInfo)
