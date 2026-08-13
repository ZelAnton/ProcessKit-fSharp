namespace ProcessKit.Mutation

open System
open System.IO
open System.Text.Json

/// Which parts of a compiled assembly the mutation tier is allowed to mutate.
///
/// The scope is read from the SAME committed file the ratchet baseline lives in
/// (`mutation-baseline.json`), on purpose: the score only means something relative to the set of
/// mutants it was computed over, so widening the scope and moving the baseline have to be one
/// reviewable diff rather than two independent ones.
///
/// `IncludeTypes` / `ExcludeTypes` hold CIL type-name PREFIXES matched with a type-boundary rule
/// (see `MutationScope.matchesType`), never a bare `StartsWith`: `ProcessKit.Backoff` must not
/// silently pull in a future `ProcessKit.BackoffPolicy`. Nested types (an F# module's inner classes
/// and its compiler-generated closures/state machines) use Cecil's `/` separator, so naming the
/// enclosing module is enough to cover the closures its functions compile into.
type MutationScope =
    {
        /// File name of the assembly under mutation, resolved against the test output directory.
        Assembly: string

        /// CIL type-name prefixes that ARE mutated. Empty means "nothing", never "everything" —
        /// an accidentally empty scope must produce zero mutants (a loud, detectable state) rather
        /// than silently mutating the whole library.
        IncludeTypes: string list

        /// Prefixes carved back out of `IncludeTypes`.
        ExcludeTypes: string list

        /// Method names never mutated, whatever type they live on (see the default list in
        /// `mutation-baseline.json`: structural equality/hash/compare/format members and static
        /// initialisers, whose mutants are either equivalent or break module init wholesale).
        ExcludeMethods: string list

        /// Skip the closure and async state-machine types the F# compiler generates (their names
        /// carry an `@`, e.g. `ProcessKit.Timeouts/raceTimeoutWithCts@150`).
        ///
        /// Two reasons, both about signal rather than convenience. Their logic is asynchronous
        /// orchestration, whose mutants are decided by timing and therefore produce Timeout verdicts
        /// and flaky kills instead of statements about assertion strength. And their names embed the
        /// source LINE they were generated at, so naming them individually in `excludeTypes` would
        /// silently stop matching the moment the file above them grows — a scope that widens without
        /// anyone noticing. A single structural rule cannot drift that way.
        ExcludeGeneratedClosureTypes: bool

        /// Skip methods the compiler generated (`[CompilerGenerated]`), whatever type they sit on.
        ///
        /// For F# that covers the structural `Equals`/`GetHashCode`/`CompareTo` members AND the union
        /// case testers (`LineTerminator.IsLf`, ...). The case testers are the reason this exists as a
        /// rule rather than a name list: F# INLINES them at every F# call site, so the emitted
        /// property bodies are never executed by this repo's own tests. Mutating them yields mutants
        /// that no F# test can possibly kill — permanent, misleading survivors that depress the score
        /// without pointing at a single weak assertion. Measured, not assumed: eight such mutants
        /// survived a full local run whose boundary test asserts the entire four-by-four
        /// case/predicate matrix.
        ExcludeCompilerGeneratedMethods: bool

        /// Seed for the deterministic catalog shuffle. A time-budgeted run evaluates a PREFIX of the
        /// shard's mutant list, so an unshuffled catalog would always sample the same alphabetically
        /// first types; shuffling with a committed seed keeps the sample representative AND exactly
        /// reproducible.
        Seed: int
    }

module MutationScope =

    /// The scope a caller gets when the file names none — deliberately empty, so a malformed or
    /// missing `scope` object yields zero mutants instead of an unbounded run.
    let empty =
        { Assembly = "ProcessKit.dll"
          IncludeTypes = []
          ExcludeTypes = []
          ExcludeMethods = []
          ExcludeGeneratedClosureTypes = true
          ExcludeCompilerGeneratedMethods = true
          Seed = 0 }

    /// Prefix match on a CIL type name, respecting type boundaries: `prefix` matches the type itself
    /// and its nested types (`prefix/Inner`), but not an unrelated type that merely starts with the
    /// same characters (`prefixOther`).
    let private matchesPrefix (prefix: string) (typeFullName: string) =
        String.Equals(typeFullName, prefix, StringComparison.Ordinal)
        || typeFullName.StartsWith(prefix + "/", StringComparison.Ordinal)

    /// Is this CIL type in scope?
    let matchesType (scope: MutationScope) (typeFullName: string) =
        let isGeneratedClosure =
            scope.ExcludeGeneratedClosureTypes && typeFullName.Contains '@'

        not isGeneratedClosure
        && scope.IncludeTypes |> List.exists (fun p -> matchesPrefix p typeFullName)
        && not (scope.ExcludeTypes |> List.exists (fun p -> matchesPrefix p typeFullName))

    /// Is this method excluded by name?
    let excludesMethod (scope: MutationScope) (methodName: string) =
        scope.ExcludeMethods
        |> List.exists (fun n -> String.Equals(n, methodName, StringComparison.Ordinal))

    let private stringList (parent: JsonElement) (name: string) =
        match parent.TryGetProperty name with
        | true, element when element.ValueKind = JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    match item.GetString() with
                    | null -> None
                    | value -> Some value
                else
                    None)
            |> List.ofSeq
        | _ -> []

    let private stringValue (parent: JsonElement) (name: string) (fallback: string) =
        match parent.TryGetProperty name with
        | true, element when element.ValueKind = JsonValueKind.String ->
            match element.GetString() with
            | null -> fallback
            | value -> value
        | _ -> fallback

    let private boolValue (parent: JsonElement) (name: string) (fallback: bool) =
        match parent.TryGetProperty name with
        | true, element when element.ValueKind = JsonValueKind.True -> true
        | true, element when element.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private intValue (parent: JsonElement) (name: string) (fallback: int) =
        match parent.TryGetProperty name with
        | true, element when element.ValueKind = JsonValueKind.Number ->
            match element.TryGetInt32() with
            | true, value -> value
            | _ -> fallback
        | _ -> fallback

    /// Load the `scope` object out of the committed baseline file. Unknown fields are ignored so the
    /// PowerShell driver and the ratchet can keep their own keys in the same document.
    let load (path: string) : Result<MutationScope, string> =
        if not (File.Exists path) then
            Error $"mutation scope file not found: {path}"
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    Error $"mutation scope file is not a JSON object: {path}"
                else
                    match root.TryGetProperty "scope" with
                    | true, scope when scope.ValueKind = JsonValueKind.Object ->
                        Ok
                            { Assembly = stringValue scope "assembly" empty.Assembly
                              IncludeTypes = stringList scope "includeTypes"
                              ExcludeTypes = stringList scope "excludeTypes"
                              ExcludeMethods = stringList scope "excludeMethods"
                              ExcludeGeneratedClosureTypes =
                                boolValue scope "excludeGeneratedClosureTypes" empty.ExcludeGeneratedClosureTypes
                              ExcludeCompilerGeneratedMethods =
                                boolValue scope "excludeCompilerGeneratedMethods" empty.ExcludeCompilerGeneratedMethods
                              Seed = intValue scope "seed" empty.Seed }
                    | _ -> Error $"mutation scope file has no 'scope' object: {path}"
            with
            | :? JsonException as ex ->
                // A hand-edited baseline is the expected way this file changes, so malformed JSON is
                // a realistic input: report it as a configuration fault the caller can print, rather
                // than letting the exception escape as a crash with a stack trace.
                Error $"mutation scope file is not valid JSON ({path}): {ex.Message}"
            | :? IOException as ex -> Error $"mutation scope file could not be read ({path}): {ex.Message}"
