namespace ProcessKit

/// The one place a union case's **stable machine identifier** is spelled — the short, lower snake case
/// name a report line, a structured log field, or a sibling implementation in another language uses to
/// refer to that case without depending on .NET's own `.ToString()` rendering or on a union tag ordinal.
///
/// Every function below is a `match` with **no wildcard arm**. Adding a case to `Outcome`, `Mechanism`,
/// `Signal`, or `ProcessError` without spelling its identifier here therefore fails to compile
/// (incomplete matches are warnings, and this repository builds with `TreatWarningsAsErrors`), so the
/// dictionary cannot fall behind the type it claims to describe. The companion machine readable
/// dictionary `spec/identifiers.json` is generated from these very functions by
/// `tests/ProcessKit.Tests/IdentifiersManifestTests.fs`, which enumerates the union cases by reflection
/// rather than from a second hand written list: a case that is added and named here but forgotten there
/// is impossible, and a stale committed manifest fails that test.
///
/// **Stability invariant (load bearing).** An identifier that has shipped is frozen: it is never
/// renamed, respelled, or reused for a different case. A new case gets a new identifier appended to the
/// dictionary; that is the only additive change this vocabulary takes. Readers in other languages pin
/// these strings, so a rename is a breaking change to them even when the .NET API is untouched.
///
/// **Never carries runtime data.** An identifier names a *case*, never a payload: no program name, no
/// argv, no environment value, no captured stream, no path ever passes through here or reaches
/// `spec/identifiers.json`.
///
/// **Not the span and metric labels.** `Diag.outcomeLabel` carries its own `processkit.outcome` label
/// set, documented in docs/observability.md, which shipped before this dictionary existed and spells
/// `Outcome.TimedOut` as `timedout` rather than `timed_out`. The two agree on the other three cases and
/// are deliberately left as they are: both are published, so respelling either to match the other would
/// break the consumers keyed on it — precisely the rename this dictionary's own invariant forbids.
[<RequireQualifiedAccess>]
module internal StableIdentifiers =

    /// How a run concluded — the same identifier `ReportJson` writes as an outcome object's `"kind"`,
    /// so the report wire form and the dictionary cannot disagree.
    let outcome (value: Outcome) : string =
        match value with
        | Outcome.Exited _ -> "exited"
        | Outcome.Signalled _ -> "signalled"
        | Outcome.TimedOut -> "timed_out"
        | Outcome.Unobserved _ -> "unobserved"

    /// The OS primitive a `ProcessGroup` contains its tree with.
    let mechanism (value: Mechanism) : string =
        match value with
        | Mechanism.JobObject -> "job_object"
        | Mechanism.CgroupV2 -> "cgroup_v2"
        | Mechanism.ProcessGroup -> "process_group"

    /// A curated signal's identifier, or `None` for `Signal.Other`.
    ///
    /// `Other` is the raw escape hatch (`Signal.Other 28` is `SIGWINCH` on Linux): its meaning lives in
    /// the number the caller supplied, not in a name this library could publish, so it is deliberately
    /// the one case with no dictionary entry. It still has to be matched here, so a future curated
    /// variant cannot slip in unnamed behind it.
    let signal (value: Signal) : string option =
        match value with
        | Signal.Term -> Some "term"
        | Signal.Kill -> Some "kill"
        | Signal.Int -> Some "int"
        | Signal.Hup -> Some "hup"
        | Signal.Quit -> Some "quit"
        | Signal.Usr1 -> Some "usr1"
        | Signal.Usr2 -> Some "usr2"
        | Signal.Other _ -> None

    /// Which failure a `ProcessError` is, as a stable identifier — the case, never its fields. A
    /// consumer that classifies failures across languages keys on this; the human readable
    /// `ProcessError.Message` is for logs and is free to be reworded.
    let processError (value: ProcessError) : string =
        match value with
        | ProcessError.Spawn _ -> "spawn"
        | ProcessError.NotFound _ -> "not_found"
        | ProcessError.Exit _ -> "exit"
        | ProcessError.Signalled _ -> "signalled"
        | ProcessError.Timeout _ -> "timeout"
        | ProcessError.Unobserved _ -> "unobserved"
        | ProcessError.Cancelled _ -> "cancelled"
        | ProcessError.NotReady _ -> "not_ready"
        | ProcessError.Parse _ -> "parse"
        | ProcessError.RetryPredicate _ -> "retry_predicate"
        | ProcessError.JsonRpc _ -> "json_rpc"
        | ProcessError.OutputTooLarge _ -> "output_too_large"
        | ProcessError.OutputIncomplete _ -> "output_incomplete"
        | ProcessError.Stdin _ -> "stdin"
        | ProcessError.ResourceLimit _ -> "resource_limit"
        | ProcessError.Adopt _ -> "adopt"
        | ProcessError.CassetteMiss _ -> "cassette_miss"
        | ProcessError.Io _ -> "io"
        | ProcessError.Unsupported _ -> "unsupported"
