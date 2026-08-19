namespace ProcessKit.Tests

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open FSharp.Reflection
open NUnit.Framework
open ProcessKit

/// Rebuilds the committed `spec/identifiers.json` dictionary from the **live** cases of `Mechanism`,
/// `Signal`, `Outcome`, `ProcessError`, `LimitVerdict`, `SupervisionEventKind`, `RlimitResource`, and
/// `IoPriorityClass`.
///
/// **Adding a public vocabulary is a deliberate step here.** The reflection guard below keeps each type
/// already in the dictionary complete, but nothing detects a brand-new public type with a stable string
/// identifier that was never added to it at all — no match goes non-exhaustive, no test turns red. When a
/// task introduces one (as T-387 did with `IoPriorityClass`), it belongs in `dictionary`, in `published`,
/// and in the class assertion, in the same change.
///
/// Two independent guards keep the dictionary and the types in step, and neither can be satisfied by
/// editing a list maintained by hand:
///
///  1. **The names come from the library.** Every identifier is read from the same function the library
///     itself emits that name through: `StableIdentifiers` for the five unions (wildcard free matches, so
///     a case added without an identifier fails the library build, warnings being errors),
///     `SupervisionEventPayload.eventName` for the supervision event kinds, and the public
///     `RlimitResource.Name` / `IoPriorityClass.Name` for the rlimit resources and the I/O scheduling
///     classes, which spell themselves next to their cases
///     because that same string is what `TryFromName` parses back. Nothing in this file spells
///     a wire name, so the dictionary and what ProcessKit emits are one vocabulary rather than two
///     copies of one: `ReportJson` writes an `Outcome`'s and a `LimitEvidence` axis's identifier from
///     those functions, `SupervisionEvent.FailureKind` is a `ProcessError`'s, `SupervisionEvent.Name`
///     is a kind's, and a resource's is the one `Rlimit.ToString` renders and `TryFromName` accepts —
///     each tied back to this dictionary by a test below.
///  2. **The case list comes from reflection.** The variants of each type are enumerated with
///     `FSharpType.GetUnionCases` (or `Enum.GetValues` for the one enum), not from a list kept here, so a
///     newly added case is picked up automatically and immediately makes the generated text differ from
///     the committed file — which is what the drift test below fails on. A generator carrying its own
///     copy of the case list is precisely how a manifest and its generator go stale together and keep a
///     drift test green while comparing two equally stale artifacts. It is also the completeness guard
///     for `SupervisionEventKind`, where the compiler cannot be one: F# requires a wildcard arm when
///     matching a .NET enum, so a kind added without a name reaches this generator and fails here.
///
/// A case's fields never reach the manifest: a representative value is built only so that a naming
/// function can be applied to it, and only the case *name* and its identifier are written out.
[<RequireQualifiedAccess>]
module internal IdentifiersManifest =

    /// The committed manifest's file name. `tests/ProcessKit.Tests/ProcessKit.Tests.fsproj` copies
    /// `spec/identifiers.json` next to the test assembly under this name, the same way it copies the
    /// public API baselines `ApiSurfaceTests` reads.
    [<Literal>]
    let FileName = "identifiers.json"

    /// Where a regenerated manifest is written when the committed one no longer matches.
    [<Literal>]
    let ReceivedFileName = "identifiers.received.json"

    /// The manifest's own `maintenance` note — what the file is and how it is kept current, for whoever
    /// opens it without this test file at hand.
    [<Literal>]
    let private Maintenance =
        "Canonical dictionary of ProcessKit's stable machine identifiers, generated from the live union cases and enum values by tests/ProcessKit.Tests/IdentifiersManifestTests.fs; never edited by hand. A shipped identifier is frozen: new variants are appended, existing ones are never renamed or reused. Update docs/jsonl-reports.md together with this file."

    /// One dictionary entry: a type, how a consumer meets it, and how one of its values is named.
    type private EnumSpec =
        {
            /// The type's F# path, the cross language counterpart of the Rust crate's `processkit::Name`.
            Path: string

            /// `configurable` for a value the caller supplies to ProcessKit, `report_only` for one
            /// ProcessKit reports back. The same two classes, and the same class per type, as the Rust
            /// crate's own `spec/identifiers.json`.
            Class: string

            /// The union or enum type whose cases are enumerated.
            Type: Type

            /// The case's stable identifier, or `None` for a case deliberately left out of the
            /// dictionary (only `Signal.Other`, whose meaning is the raw number the caller passed).
            /// Takes `objnull` because that is what `box` and `FSharpValue.MakeUnion` produce.
            Identifier: objnull -> string option
        }

    /// A representative value of `fieldType`, used only as the argument a naming function is applied to.
    ///
    /// A scalar gets a zero or empty value and a nested union gets its first case, built the same way.
    /// These values are never written anywhere: they exist so that an identifier can come from the live
    /// naming function instead of from a string copied into this file. A field type with no
    /// representative here raises rather than guessing, so the failure is a loud one and never a
    /// silently missing variant.
    ///
    /// `objnull`, not `obj`: `box` and `FSharpValue.MakeUnion` both produce the nullable object type,
    /// and an `'a option` field's representative genuinely is `null` at runtime (that is `None`).
    let rec private representative (fieldType: Type) : objnull =
        if fieldType = typeof<int> then
            box 0
        elif fieldType = typeof<int64> then
            box 0L
        elif fieldType = typeof<float> then
            box 0.0
        elif fieldType = typeof<bool> then
            box false
        elif fieldType = typeof<string> then
            box ""
        elif fieldType = typeof<TimeSpan> then
            box TimeSpan.Zero
        elif FSharpType.IsUnion fieldType then
            // An option field lands here too: `None` is the first case of `'T option` and carries no
            // fields, so this yields `None` without a special case for it.
            let case = (FSharpType.GetUnionCases fieldType)[0]

            FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun field -> representative field.PropertyType))
        else
            raise (
                NotSupportedException
                    $"IdentifiersManifest has no representative value for the field type '{fieldType.FullName}'; add one so that the new union case can be named."
            )

    /// Every case of `vocabularyType`, in declaration order, as `(case name, representative value)`.
    ///
    /// A union case is constructed from representative field values; an enum value is itself, so
    /// `SupervisionEventKind` needs no representative machinery. Enum values come back ordered by their
    /// underlying number, which is declaration order for an enum whose cases are numbered in the order
    /// they are written — the discipline `SupervisionEventKind` documents on itself.
    let caseValues (vocabularyType: Type) : (string * objnull)[] =
        if vocabularyType.IsEnum then
            Enum.GetValues vocabularyType
            |> Seq.cast<obj>
            |> Seq.map (fun value ->
                match Enum.GetName(vocabularyType, value) with
                | null ->
                    raise (
                        NotSupportedException
                            $"'{vocabularyType.FullName}' has an unnamed value; the manifest publishes named cases only."
                    )
                | name -> name, (value: objnull))
            |> Seq.toArray
        else
            FSharpType.GetUnionCases vocabularyType
            |> Array.map (fun case ->
                let fields =
                    case.GetFields() |> Array.map (fun field -> representative field.PropertyType)

                case.Name, FSharpValue.MakeUnion(case, fields))

    /// The dictionary, in manifest order: the two types a caller configures first, then the four
    /// ProcessKit reports, then every vocabulary added after that first set. This matches how the Rust
    /// crate's manifest groups the same vocabularies.
    /// A type is appended, never inserted, so the entries a reader already parsed keep their positions —
    /// which is why `RlimitResource`, a `configurable` type, sits after the `report_only` ones instead of
    /// beside the two configurable types it belongs with. Position is not part of the contract; `path` is.
    let private dictionary: EnumSpec list =
        [ { Path = "ProcessKit.Mechanism"
            Class = "configurable"
            Type = typeof<Mechanism>
            Identifier = fun value -> Some(StableIdentifiers.mechanism (unbox<Mechanism> value)) }
          { Path = "ProcessKit.Signal"
            Class = "configurable"
            Type = typeof<Signal>
            Identifier = fun value -> StableIdentifiers.signal (unbox<Signal> value) }
          { Path = "ProcessKit.Outcome"
            Class = "report_only"
            Type = typeof<Outcome>
            Identifier = fun value -> Some(StableIdentifiers.outcome (unbox<Outcome> value)) }
          { Path = "ProcessKit.ProcessError"
            Class = "report_only"
            Type = typeof<ProcessError>
            Identifier = fun value -> Some(StableIdentifiers.processError (unbox<ProcessError> value)) }
          { Path = "ProcessKit.LimitVerdict"
            Class = "report_only"
            Type = typeof<LimitVerdict>
            Identifier = fun value -> Some(StableIdentifiers.limitVerdict (unbox<LimitVerdict> value)) }
          { Path = "ProcessKit.SupervisionEventKind"
            Class = "report_only"
            Type = typeof<SupervisionEventKind>
            Identifier = fun value -> Some(SupervisionEventPayload.eventName (unbox<SupervisionEventKind> value)) }
          // `RlimitResource` names itself: its stable identifier is the public `Name` member, declared
          // next to the cases in `Limits.fs` rather than in `StableIdentifiers`, because it is also the
          // spelling `TryFromName`/`FromName` parse back and the one `Rlimit.ToString` renders. Reading it
          // here keeps that single point of spelling, on the same terms as `eventName` above.
          { Path = "ProcessKit.RlimitResource"
            Class = "configurable"
            Type = typeof<RlimitResource>
            Identifier = fun value -> Some (unbox<RlimitResource> value).Name }
          // `IoPriorityClass` names itself for exactly the same reason `RlimitResource` does: its `Name`
          // is the spelling `TryFromName`/`FromName` parse back and the one an `IoPriority` renders, so
          // it is declared next to the cases in `IoPriority.fs` rather than in `StableIdentifiers`. The
          // levelled classes' LEVEL is not part of this vocabulary — it is a number the caller supplies,
          // not a case that could be named — so the dictionary publishes the three classes only.
          { Path = "ProcessKit.IoPriorityClass"
            Class = "configurable"
            Type = typeof<IoPriorityClass>
            Identifier = fun value -> Some (unbox<IoPriorityClass> value).Name } ]

    /// The `(path, class, [| variant, identifier |])` rows the manifest publishes — the structure the
    /// text below renders, and what the structural assertions read.
    let rows () : (string * string * (string * string)[]) list =
        dictionary
        |> List.map (fun spec ->
            let variants =
                caseValues spec.Type
                |> Array.choose (fun (name, value) ->
                    spec.Identifier value |> Option.map (fun identifier -> name, identifier))

            spec.Path, spec.Class, variants)

    /// A JSON string literal. Every value this manifest writes is a plain identifier today; the escaping
    /// is here so that a future one cannot silently produce invalid JSON.
    let private appendJsonString (builder: StringBuilder) (value: string) : unit =
        builder.Append '"' |> ignore

        for character in value do
            match character with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | control when Char.IsControl control ->
                builder.Append("\\u").Append((int control).ToString("x4", CultureInfo.InvariantCulture))
                |> ignore
            | ordinary -> builder.Append ordinary |> ignore

        builder.Append '"' |> ignore

    /// The manifest text the committed `spec/identifiers.json` must equal: LF line endings and a
    /// trailing newline on every platform, so the same text is produced on every leg of the CI matrix.
    let generate () : string =
        let builder = StringBuilder()
        let append (text: string) = builder.Append text |> ignore

        append "{\n  \"schema_version\": 1,\n  \"maintenance\": "
        appendJsonString builder Maintenance
        append ",\n  \"enums\": [\n"

        let entries = rows ()

        entries
        |> List.iteri (fun index (path, className, variants) ->
            append "    {\n      \"path\": "
            appendJsonString builder path
            append ",\n      \"class\": "
            appendJsonString builder className
            append ",\n      \"variants\": [\n"

            variants
            |> Array.iteri (fun variantIndex (variant, identifier) ->
                append "        { \"variant\": "
                appendJsonString builder variant
                append ", \"identifier\": "
                appendJsonString builder identifier

                append (
                    if variantIndex + 1 = variants.Length then
                        " }\n"
                    else
                        " },\n"
                ))

            append (
                if index + 1 = entries.Length then
                    "      ]\n    }\n"
                else
                    "      ]\n    },\n"
            ))

        append "  ]\n}\n"
        builder.ToString()

/// Guards the machine readable dictionary of stable identifiers, `spec/identifiers.json`: that it still
/// matches the live types, that it parses, that the names already published are still the names it
/// publishes, and that it carries nothing but those names.
[<TestFixture>]
type IdentifiersManifestTests() =

    /// The identifiers already published for each type, spelled out here rather than read back from the
    /// code that produced them. Renaming a shipped identifier breaks every consumer that pinned it, so
    /// this list is the mechanical form of that promise: a new variant appends a line, and a rename
    /// fails here until somebody deliberately edits the expectation.
    static let published =
        [ "ProcessKit.Mechanism", [ "JobObject=job_object"; "CgroupV2=cgroup_v2"; "ProcessGroup=process_group" ]
          "ProcessKit.Signal",
          [ "Term=term"
            "Kill=kill"
            "Int=int"
            "Hup=hup"
            "Quit=quit"
            "Usr1=usr1"
            "Usr2=usr2" ]
          "ProcessKit.Outcome",
          [ "Exited=exited"
            "Signalled=signalled"
            "TimedOut=timed_out"
            "Unobserved=unobserved" ]
          "ProcessKit.ProcessError",
          [ "Spawn=spawn"
            "NotFound=not_found"
            "Exit=exit"
            "Signalled=signalled"
            "Timeout=timeout"
            "Unobserved=unobserved"
            "Cancelled=cancelled"
            "NotReady=not_ready"
            "Parse=parse"
            "RetryPredicate=retry_predicate"
            "JsonRpc=json_rpc"
            "OutputTooLarge=output_too_large"
            "OutputIncomplete=output_incomplete"
            "Stdin=stdin"
            "ResourceLimit=resource_limit"
            "Adopt=adopt"
            "CassetteMiss=cassette_miss"
            "Io=io"
            "Unsupported=unsupported" ]
          "ProcessKit.LimitVerdict", [ "Tripped=tripped"; "NotTripped=not_tripped"; "Unknown=unknown" ]
          "ProcessKit.SupervisionEventKind",
          [ "IncarnationStarted=incarnation_started"
            "IncarnationFinished=incarnation_finished"
            "IncarnationFailed=incarnation_failed"
            "RestartScheduled=restart_scheduled"
            "StormPaused=storm_paused"
            "HealthCheckFailed=health_check_failed"
            "GaveUp=gave_up"
            "Stopped=stopped"
            "SupervisionFailed=supervision_failed"
            "EventsDropped=events_dropped" ]
          "ProcessKit.RlimitResource",
          [ "Cpu=cpu"
            "Core=core"
            "Data=data"
            "FileSize=file_size"
            "NoFile=no_file"
            "Stack=stack" ]
          "ProcessKit.IoPriorityClass", [ "Idle=idle"; "BestEffort=best_effort"; "RealTime=real_time" ] ]

    /// A JSON string property, with the `string | null` the BCL declares narrowed to a plain string —
    /// every string this document carries is written by the generator and is never null.
    static let text (element: JsonElement) (name: string) : string =
        match element.GetProperty(name).GetString() with
        | null -> ""
        | value -> value

    /// An object's property names, sorted and joined, so an expectation is one unambiguous string
    /// comparison rather than a collection constraint.
    static let keysOf (element: JsonElement) : string =
        element.EnumerateObject()
        |> Seq.map (fun property -> property.Name)
        |> Seq.sort
        |> String.concat ","

    /// The `case name -> identifier` map the dictionary publishes for one type, for the tests that tie a
    /// published identifier to the string the library actually emits.
    static let identifiersOf (path: string) : Map<string, string> =
        IdentifiersManifest.rows ()
        |> List.pick (fun (candidate, _, variants) ->
            if candidate = path then
                Some(Map.ofArray variants)
            else
                None)

    static let enumsOf (document: JsonDocument) =
        document.RootElement.GetProperty("enums").EnumerateArray() |> Seq.toList

    static let renderVariants (variants: (string * string)[]) : string =
        variants
        |> Array.map (fun (variant, identifier) -> variant + "=" + identifier)
        |> String.concat ", "

    static let isLowerSnakeCase (value: string) : bool =
        value.Length > 0
        && value[0] >= 'a'
        && value[0] <= 'z'
        && value
           |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_')

    /// The drift guard. The committed dictionary is compared with one rebuilt from the live union cases,
    /// so a variant added, named, or renamed in the library fails here until `spec/identifiers.json` is
    /// regenerated in the same change — the F# counterpart of the Rust crate's identifiers diff.
    [<Test>]
    member _.``The committed identifiers manifest matches the live stable identifiers``() =
        let generated = IdentifiersManifest.generate ()

        let committedPath =
            Path.Combine(AppContext.BaseDirectory, IdentifiersManifest.FileName)

        let committed =
            if File.Exists committedPath then
                File.ReadAllText(committedPath).Replace("\r\n", "\n")
            else
                ""

        if committed <> generated then
            let receivedPath =
                Path.Combine(AppContext.BaseDirectory, IdentifiersManifest.ReceivedFileName)

            File.WriteAllText(receivedPath, generated)

            Assert.Fail(
                sprintf
                    "spec/identifiers.json no longer matches the live stable identifiers.\n\nCommitted copy: %s\nRegenerated:    %s\n\nIf the change is intentional, copy the regenerated file over spec/identifiers.json and review the diff; keep the published identifiers additive, because a shipped identifier is never renamed. The committed copy above is the build output's copy of that file, so confirm the fix with a full test run rather than a --no-build one, which would still read the stale copy."
                    committedPath
                    receivedPath
            )

    /// The cross language consumer's view: the generated document is valid JSON and still publishes the
    /// exact wire names each type shipped with.
    [<Test>]
    member _.``The generated manifest parses and publishes the expected wire names``() =
        use document = JsonDocument.Parse(IdentifiersManifest.generate ())

        Assert.That(document.RootElement.GetProperty("schema_version").GetInt32(), Is.EqualTo 1)

        let enums = enumsOf document

        Assert.That(List.length enums, Is.EqualTo(List.length published))

        let byPath =
            enums
            |> List.map (fun element ->
                let variants =
                    element.GetProperty("variants").EnumerateArray()
                    |> Seq.map (fun variant -> text variant "variant" + "=" + text variant "identifier")
                    |> String.concat ", "

                text element "path", variants)
            |> Map.ofList

        for path, expected in published do
            Assert.That(byPath.ContainsKey path, Is.True)
            Assert.That(byPath[path], Is.EqualTo(String.concat ", " expected))

    /// The classes are the ones the Rust crate uses for the same vocabularies, so a conformance harness
    /// can read either manifest with one rule.
    [<Test>]
    member _.``Each type is classified as configurable or report only``() =
        use document = JsonDocument.Parse(IdentifiersManifest.generate ())

        let classes =
            enumsOf document
            |> List.map (fun element -> text element "path", text element "class")
            |> Map.ofList

        Assert.That(classes["ProcessKit.Mechanism"], Is.EqualTo "configurable")
        Assert.That(classes["ProcessKit.Signal"], Is.EqualTo "configurable")
        Assert.That(classes["ProcessKit.Outcome"], Is.EqualTo "report_only")
        Assert.That(classes["ProcessKit.ProcessError"], Is.EqualTo "report_only")
        Assert.That(classes["ProcessKit.LimitVerdict"], Is.EqualTo "report_only")
        Assert.That(classes["ProcessKit.SupervisionEventKind"], Is.EqualTo "report_only")
        Assert.That(classes["ProcessKit.RlimitResource"], Is.EqualTo "configurable")
        Assert.That(classes["ProcessKit.IoPriorityClass"], Is.EqualTo "configurable")

    /// Paths are unique across the dictionary, and identifiers are unique and lower snake case within
    /// each type — a duplicate would make the dictionary ambiguous for the consumers that key on it.
    [<Test>]
    member _.``Paths and identifiers are unique and lower snake case``() =
        let entries = IdentifiersManifest.rows ()
        let paths = entries |> List.map (fun (path, _, _) -> path)

        Assert.That(List.length (List.distinct paths), Is.EqualTo(List.length paths))

        for path, _, variants in entries do
            Assert.That(variants.Length, Is.GreaterThan 0)

            let identifiers = variants |> Array.map snd |> Array.toList

            Assert.That(List.length (List.distinct identifiers), Is.EqualTo(List.length identifiers))

            for variant, identifier in variants do
                if not (isLowerSnakeCase identifier) then
                    Assert.Fail(
                        sprintf "%s.%s has identifier '%s', which is not lower snake case." path variant identifier
                    )

    /// The manifest is a dictionary of case names, never a place runtime data can appear. Its object keys
    /// are a closed set, so a future field carrying an argument vector, an environment value, a program
    /// name, or a path cannot be added without failing here first.
    [<Test>]
    member _.``The manifest carries only identifier vocabulary, never runtime data``() =
        let generated = IdentifiersManifest.generate ()
        use document = JsonDocument.Parse generated

        Assert.That(keysOf document.RootElement, Is.EqualTo "enums,maintenance,schema_version")

        for element in enumsOf document do
            Assert.That(keysOf element, Is.EqualTo "class,path,variants")
            Assert.That((text element "path").StartsWith("ProcessKit.", StringComparison.Ordinal), Is.True)

            for variant in element.GetProperty("variants").EnumerateArray() do
                Assert.That(keysOf variant, Is.EqualTo "identifier,variant")
                Assert.That(variant.GetProperty("variant").ValueKind, Is.EqualTo JsonValueKind.String)
                Assert.That(variant.GetProperty("identifier").ValueKind, Is.EqualTo JsonValueKind.String)

        // The generator applies the naming functions to constructed values whose fields are exactly the
        // ones that would carry a program name, a searched path, or a captured stream. None of those
        // field names, and no word naming that class of data, may appear in the rendered dictionary —
        // which is what "the manifest names cases, never payloads" means concretely.
        for marker in
            [ "program"
              "Program"
              "argv"
              "env"
              "Detail"
              "Searched"
              "stdout"
              "stderr" ] do
            Assert.That(generated.Contains(marker, StringComparison.Ordinal), Is.False)

    /// Ties the dictionary to the wire: for every `Outcome` case, the identifier the manifest publishes
    /// is the `"kind"` `ReportJson` actually writes. Enumerated by reflection, so a new case is covered
    /// the moment it exists rather than when somebody remembers to extend a list here.
    [<Test>]
    member _.``Every Outcome identifier is the kind ReportJson writes``() =
        let outcomeIdentifiers = identifiersOf "ProcessKit.Outcome"

        for name, value in IdentifiersManifest.caseValues typeof<Outcome> do
            let outcome = unbox<Outcome> value

            use document =
                JsonDocument.Parse(JsonSerializer.Serialize(outcome, ReportJson.OutcomeTypeInfo))

            Assert.That(document.RootElement.GetProperty("kind").GetString(), Is.EqualTo outcomeIdentifiers[name])

    /// The same tie for `LimitVerdict`: every axis of a `limit_evidence` line is written as the exact
    /// identifier the manifest publishes for that verdict. All three axes are checked, so a converter
    /// that spelled one of them by hand would fail here rather than pass on the other two.
    [<Test>]
    member _.``Every LimitVerdict identifier is the verdict ReportJson writes``() =
        let verdictIdentifiers = identifiersOf "ProcessKit.LimitVerdict"

        for name, value in IdentifiersManifest.caseValues typeof<LimitVerdict> do
            let verdict = unbox<LimitVerdict> value

            use document =
                JsonDocument.Parse(LimitEvidence(verdict, verdict, verdict).ToReportJson())

            for axis in [ "memory"; "processes"; "cpu" ] do
                Assert.That(document.RootElement.GetProperty(axis).GetString(), Is.EqualTo verdictIdentifiers[name])

    /// The tie for `ProcessError`: the identifier the manifest publishes is the string a consumer
    /// actually receives, on the one path by which a failure's class leaves the library as text —
    /// `SupervisionEvent.FailureKind`. This is what makes the dictionary a description of what
    /// ProcessKit emits rather than of a naming function nothing reads.
    [<Test>]
    member _.``Every ProcessError identifier is the FailureKind a supervision event carries``() =
        let errorIdentifiers = identifiersOf "ProcessKit.ProcessError"

        for name, value in IdentifiersManifest.caseValues typeof<ProcessError> do
            let error = unbox<ProcessError> value
            let failed = SupervisionEvent.IncarnationFailed("tool", 1, error)
            let terminal = SupervisionEvent.SupervisionFailed("tool", error)

            Assert.That(failed.FailureKind, Is.EqualTo(Some errorIdentifiers[name]))
            Assert.That(terminal.FailureKind, Is.EqualTo(Some errorIdentifiers[name]))

    /// The tie for `SupervisionEventKind`: the identifier the manifest publishes for a kind is the
    /// `SupervisionEvent.Name` an event of that kind carries. Enumerated over the live enum values, so a
    /// kind added without a name fails here — the guard the compiler cannot give an enum match.
    [<Test>]
    member _.``Every SupervisionEventKind identifier is the Name an event carries``() =
        let kindIdentifiers = identifiersOf "ProcessKit.SupervisionEventKind"

        for name, value in IdentifiersManifest.caseValues typeof<SupervisionEventKind> do
            let kind = unbox<SupervisionEventKind> value
            let event = SupervisionEvent(SupervisionEventPayload.create kind "tool")

            Assert.That(event.Name, Is.EqualTo kindIdentifiers[name])
            Assert.That(event.Kind, Is.EqualTo kind)

    /// The tie for `RlimitResource`, the one published vocabulary the library **parses** as well as
    /// spells: every identifier the manifest publishes is accepted by `TryFromName` and comes back as the
    /// very case it was published for. That is what makes the file usable as the config layer's source of
    /// accepted spellings — a consumer that reads a resource name out of the dictionary and feeds it to
    /// ProcessKit cannot be handed a string the parser rejects.
    [<Test>]
    member _.``Every RlimitResource identifier is a name TryFromName parses back``() =
        let resourceIdentifiers = identifiersOf "ProcessKit.RlimitResource"

        for name, value in IdentifiersManifest.caseValues typeof<RlimitResource> do
            let resource = unbox<RlimitResource> value
            let identifier = resourceIdentifiers[name]

            Assert.That(resource.Name, Is.EqualTo identifier)
            Assert.That(RlimitResource.TryFromName identifier, Is.EqualTo(Some resource))

    /// The same tie for `IoPriorityClass`, the second published vocabulary the library parses as well as
    /// spells: a config layer that reads a class name out of the dictionary and hands it to
    /// `IoPriorityClass.FromName` can never be given a string the parser rejects.
    [<Test>]
    member _.``Every IoPriorityClass identifier is a name TryFromName parses back``() =
        let classIdentifiers = identifiersOf "ProcessKit.IoPriorityClass"

        for name, value in IdentifiersManifest.caseValues typeof<IoPriorityClass> do
            let ioClass = unbox<IoPriorityClass> value
            let identifier = classIdentifiers[name]

            Assert.That(ioClass.Name, Is.EqualTo identifier)
            Assert.That(IoPriorityClass.TryFromName identifier, Is.EqualTo(Some ioClass))

    /// The rendered form the drift guard compares is the same one the structural assertions read, so a
    /// row that renders differently from what it publishes cannot pass both.
    [<Test>]
    member _.``Every published row renders the identifiers it carries``() =
        for path, _, variants in IdentifiersManifest.rows () do
            match published |> List.tryFind (fun (candidate, _) -> candidate = path) with
            | Some(_, expected) -> Assert.That(renderVariants variants, Is.EqualTo(String.concat ", " expected))
            | None -> Assert.Fail(sprintf "%s is in the dictionary but has no published identifier list." path)
