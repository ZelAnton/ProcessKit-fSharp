namespace ProcessKit.Mutation

open System
open System.Security.Cryptography
open System.Text
open Mono.Cecil
open Mono.Cecil.Rocks

/// One mutation the engine can make, addressed well enough to reproduce it later.
///
/// `InstructionIndex` — not the IL byte offset — is the stable coordinate. Offsets move as soon as a
/// constant changes width, whereas the instruction sequence of a given method build is fixed, so an
/// index survives the `SimplifyMacros` normalization that both `list` and `apply` perform.
type MutantPoint =
    { Id: string
      Key: string
      TypeName: string
      MethodName: string
      InstructionIndex: int
      Offset: int
      Kind: string
      Description: string
      SourceFile: string
      SourceLine: int }

module Catalog =

    /// Deterministic, self-contained pseudo-randomness for the catalog shuffle (xorshift64). Written
    /// out rather than taken from `System.Random` so the shard assignment is reproducible by
    /// construction — pinned to this source, not to a runtime implementation detail.
    let private nextState (state: uint64) =
        let x = state ^^^ (state <<< 13)
        let x = x ^^^ (x >>> 7)
        x ^^^ (x <<< 17)

    /// Fisher-Yates with the xorshift stream above. A time-budgeted shard evaluates a PREFIX of its
    /// list, so an unshuffled catalog would repeatedly sample the same alphabetically first types and
    /// never reach the rest; shuffling with the committed seed keeps a truncated run representative
    /// while staying exactly reproducible.
    let private shuffle (seed: int) (items: MutantPoint[]) =
        // Seed 0 would freeze xorshift at zero (it has no non-trivial orbit through 0), so fold the
        // seed into a non-zero constant instead of using it raw.
        let mutable state = uint64 (uint32 seed) ^^^ 0x9E3779B97F4A7C15UL

        for i in (items.Length - 1) .. -1 .. 1 do
            state <- nextState state
            let j = int (state % uint64 (i + 1))
            let swap = items[i]
            items[i] <- items[j]
            items[j] <- swap

        items

    /// A short, command-line-safe identifier for a mutant, derived from its full key so it is stable
    /// across runs and machines (a positional index would not be: it moves whenever the scope, the
    /// seed or the compiler output changes).
    let private shortId (key: string) =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes key)
        "M" + Convert.ToHexString(digest).Substring(0, 12).ToLowerInvariant()

    /// Deterministic builds (`ContinuousIntegrationBuild=true`, which this repo turns on for every
    /// GitHub Actions build) rewrite PDB document paths to the SourceLink form `/_/src/...`, which
    /// does not exist on the runner. That is the same mechanism behind this repo's silent empty
    /// coverage incident, so it is handled explicitly here: the deterministic prefix is normalized
    /// back to a repo-relative path, and a path that cannot be interpreted is reported verbatim.
    ///
    /// Source mapping is decoration for the report, never an input to a verdict — a build with no PDB
    /// at all still produces the full catalog, with empty source fields.
    let private normalizeSourcePath (path: string) =
        if String.IsNullOrEmpty path then
            ""
        elif path.StartsWith("/_/", StringComparison.Ordinal) then
            // The remaining leading separators are trimmed too: when the SourceRoot MSBuild resolves
            // already carries a trailing separator the emitted path is `/_//rest`, and leaving that
            // second slash on would turn a repo-relative path back into an absolute-looking one.
            path.Substring(3).TrimStart('/')
        else
            path.Replace('\\', '/')

    /// Sequence points are keyed by the ORIGINAL IL offsets, so they are snapshotted before
    /// `SimplifyMacros` runs. The nearest preceding sequence point wins, which is what a debugger
    /// does; hidden sequence points (line 0xFEEFEE) carry no source location and are skipped.
    let private sourceLookup (method: MethodDefinition) =
        let points =
            if isNull (box method.DebugInformation) then
                [||]
            else
                method.DebugInformation.SequencePoints
                |> Seq.filter (fun point -> not point.IsHidden)
                |> Seq.sortBy (fun point -> point.Offset)
                |> Array.ofSeq

        fun (offset: int) ->
            match points |> Array.filter (fun point -> point.Offset <= offset) with
            | [||] -> "", 0
            | candidates ->
                let point = Array.last candidates
                normalizeSourcePath point.Document.Url, point.StartLine

    let private compilerGeneratedAttribute =
        "System.Runtime.CompilerServices.CompilerGeneratedAttribute"

    let private isCompilerGenerated (method: MethodDefinition) =
        method.HasCustomAttributes
        && method.CustomAttributes
           |> Seq.exists (fun attribute ->
               String.Equals(attribute.AttributeType.FullName, compilerGeneratedAttribute, StringComparison.Ordinal))

    let private isMutable (scope: MutationScope) (method: MethodDefinition) =
        method.HasBody
        && not method.IsAbstract
        && not method.IsPInvokeImpl
        && not method.IsRuntime
        && not method.IsInternalCall
        && not (MutationScope.excludesMethod scope method.Name)
        && not (scope.ExcludeCompilerGeneratedMethods && isCompilerGenerated method)

    /// The single traversal both `build` and `resolve` are expressed over.
    ///
    /// Order is fixed by (type full name, method full name, instruction index, operator order) — all
    /// ordinal — so two runs over the same assembly enumerate identically on any platform, which is
    /// what makes an id resolvable later. Every admitted method body is normalized through
    /// `SimplifyMacros` here, so both callers see the same canonical instruction sequence.
    let private traverse (scope: MutationScope) (module': ModuleDefinition) =
        let found = ResizeArray<MutantPoint * Mutation * MethodDefinition>()

        let types =
            module'.GetTypes()
            |> Seq.filter (fun t -> MutationScope.matchesType scope t.FullName)
            |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.FullName, b.FullName))
            |> Array.ofSeq

        for type' in types do
            let methods =
                type'.Methods
                |> Seq.filter (isMutable scope)
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.FullName, b.FullName))
                |> Array.ofSeq

            for method in methods do
                let body = method.Body
                // Snapshot offsets BEFORE normalizing: SimplifyMacros rewrites instructions in place,
                // so the stored offsets stop describing the emitted body while the sequence points
                // still refer to the original ones.
                let originalOffsets =
                    body.Instructions |> Seq.map (fun i -> i.Offset) |> Array.ofSeq

                let locate = sourceLookup method

                // Expand the short/implicit forms (`ldc.i4.1`, `blt.s`, ...) so the operator table
                // sees one canonical shape per operation.
                body.SimplifyMacros()

                body.Instructions
                |> Seq.iteri (fun index instruction ->
                    for mutation in Mutators.candidates instruction do
                        let key = $"{method.FullName}#{index}:{mutation.Kind}"
                        let offset = originalOffsets[index]
                        let sourceFile, sourceLine = locate offset

                        found.Add(
                            { Id = shortId key
                              Key = key
                              TypeName = type'.FullName
                              MethodName = method.FullName
                              InstructionIndex = index
                              Offset = offset
                              Kind = mutation.Kind
                              Description = mutation.Description
                              SourceFile = sourceFile
                              SourceLine = sourceLine },
                            mutation,
                            method
                        ))

        found

    /// Every mutation point the scope admits, deterministically shuffled.
    let build (scope: MutationScope) (module': ModuleDefinition) : Result<MutantPoint[], string> =
        let points =
            traverse scope module' |> Seq.map (fun (point, _, _) -> point) |> Array.ofSeq

        let duplicates =
            points
            |> Seq.countBy (fun point -> point.Id)
            |> Seq.filter (fun (_, count) -> count > 1)
            |> Seq.map fst
            |> List.ofSeq

        match duplicates with
        | [] -> Ok(shuffle scope.Seed points)
        | ids ->
            // 48 bits of digest over a few thousand mutants makes this essentially impossible, but a
            // silent collision would make `apply` mutate the wrong instruction and quietly corrupt
            // every downstream verdict, so it is checked rather than assumed.
            Error $"""mutant id collision ({ids.Length}): {String.Join(", ", ids)}"""

    /// Find the single mutation point with this id, together with the mutation and the method it
    /// belongs to.
    ///
    /// The lookup re-derives the catalog from the assembly rather than trusting a stored index, so an
    /// `apply` against an assembly that no longer matches the catalog fails loudly instead of
    /// mutating whatever now sits at that position.
    let resolve
        (scope: MutationScope)
        (module': ModuleDefinition)
        (id: string)
        : Result<MutantPoint * Mutation * MethodDefinition, string> =
        let matches =
            traverse scope module'
            |> Seq.filter (fun (point, _, _) -> String.Equals(point.Id, id, StringComparison.Ordinal))
            |> List.ofSeq

        match matches with
        | [ single ] -> Ok single
        | [] -> Error $"mutant id not found in this assembly: {id}"
        | many -> Error $"mutant id is ambiguous in this assembly ({many.Length} matches): {id}"

    /// Apply `mutation` to the instruction the point addresses, then re-compact the body.
    ///
    /// `OptimizeMacros` is what makes a widened constant safe: re-running the peephole pass lets
    /// Cecil re-pick short forms and recompute every branch displacement, so a body that grew by four
    /// bytes still writes out with valid short branches instead of failing at write time.
    let applyMutation (point: MutantPoint) (mutation: Mutation) (method: MethodDefinition) =
        let body = method.Body

        if point.InstructionIndex < 0 || point.InstructionIndex >= body.Instructions.Count then
            Error $"instruction index {point.InstructionIndex} is out of range for {method.FullName}"
        else
            mutation.Apply body.Instructions[point.InstructionIndex]
            body.OptimizeMacros()
            Ok()
