module ProcessKit.Mutation.Program

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open Mono.Cecil
open Mono.Cecil.Cil

module private TestHostErrorMode =

    [<Literal>]
    let private SEM_FAILCRITICALERRORS = 0x00000001u

    [<Literal>]
    let private SEM_NOGPFAULTERRORBOX = 0x00000002u

    [<Literal>]
    let private SEM_NOOPENFILEERRORBOX = 0x00008000u

    [<Literal>]
    let private SuppressedDialogModes =
        SEM_FAILCRITICALERRORS ||| SEM_NOGPFAULTERRORBOX ||| SEM_NOOPENFILEERRORBOX

    [<DllImport("kernel32.dll")>]
    extern uint32 SetErrorMode(uint32 uMode)

    let suppressModalDialogs () =
        if OperatingSystem.IsWindows() then
            SetErrorMode SuppressedDialogModes |> ignore

/// Threads the `Result<_, string>` failures of argument parsing, scope loading and catalog
/// resolution through one linear block, so every verb below reads as its own happy path and every
/// failure reaches `main` as a printable message rather than a stack trace.
type private ResultBuilder() =
    member _.Bind(value, binder) = Result.bind binder value
    member _.Return value = Ok value
    member _.ReturnFrom(value: Result<'a, 'b>) = value
    member _.Zero() = Ok()

    member _.Using(resource: 'T :> IDisposable, body: 'T -> Result<'a, 'b>) =
        try
            body resource
        finally
            resource.Dispose()

let private result = ResultBuilder()

/// The IL mutation engine behind `scripts/mutate.ps1`.
///
/// Two verbs, deliberately stateless — the PowerShell driver owns the loop, the scheduling and the
/// verdicts, this owns only "what can be mutated" and "produce that one mutant":
///
///   list  --assembly <dll> --scope <mutation-baseline.json> --output <catalog.json>
///   apply --assembly <dll> --scope <mutation-baseline.json> --id <mutant-id> --output <mutated.dll>
///
/// Splitting it this way keeps the engine trivially exercisable from a shell (`list` is a pure
/// function of the assembly plus the scope) and leaves the driver free to shard, budget and retry
/// without the engine holding state between mutants.
let private usage =
    String.Join(
        Environment.NewLine,
        [ "usage:"
          "  ProcessKit.Mutation list  --assembly <dll> --scope <json> --output <catalog.json>"
          "  ProcessKit.Mutation apply --assembly <dll> --scope <json> --id <mutant-id> --output <dll>" ]
    )

let private parseArgs (argv: string list) =
    let rec loop acc remaining =
        match remaining with
        | [] -> Ok acc
        | (key: string) :: _ when not (key.StartsWith("--", StringComparison.Ordinal)) ->
            Error $"unexpected argument '{key}' (expected '--name value')"
        | key :: value :: rest -> loop (Map.add (key.Substring 2) value acc) rest
        | key :: [] -> Error $"missing value for argument '{key}'"

    loop Map.empty argv

let private require (args: Map<string, string>) (name: string) =
    match Map.tryFind name args with
    | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
    | _ -> Error $"required argument --{name} is missing"

let private ensureParentDirectory (path: string) =
    match Path.GetDirectoryName(Path.GetFullPath path) with
    | null
    | "" -> ()
    | directory -> Directory.CreateDirectory directory |> ignore

/// Symbols are optional and read best-effort: a mutant is addressed by IL, so a missing or
/// mismatched PDB costs only the source file/line decoration in the report, never a mutation point.
/// This is also the one place a deterministic CI build shows up — see `Catalog.normalizeSourcePath`.
let private readModule (path: string) (withSymbols: bool) =
    let plain () =
        ModuleDefinition.ReadModule(path, ReaderParameters(ReadingMode.Immediate))

    if withSymbols then
        try
            ModuleDefinition.ReadModule(path, ReaderParameters(ReadingMode.Immediate, ReadSymbols = true))
        with
        | :? SymbolsNotFoundException
        | :? SymbolsNotMatchingException ->
            // No PDB next to the assembly, or one left over from a different build. Both are normal
            // (a published or stripped output, an incremental rebuild) and neither affects which
            // mutants exist, so degrade to an IL-only read instead of failing the catalog.
            plain ()
    else
        plain ()

let private writeCatalog
    (output: string)
    (assembly: string)
    (scopeFile: string)
    (scope: MutationScope)
    (points: MutantPoint[])
    =
    ensureParentDirectory output
    use stream = File.Create output
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))

    writer.WriteStartObject()
    writer.WriteString("assembly", assembly)
    writer.WriteString("scopeFile", scopeFile)
    writer.WriteNumber("seed", scope.Seed)
    writer.WriteNumber("count", points.Length)
    writer.WriteStartArray("mutants")

    for point in points do
        writer.WriteStartObject()
        writer.WriteString("id", point.Id)
        writer.WriteString("key", point.Key)
        writer.WriteString("type", point.TypeName)
        writer.WriteString("method", point.MethodName)
        writer.WriteNumber("instructionIndex", point.InstructionIndex)
        writer.WriteNumber("offset", point.Offset)
        writer.WriteString("kind", point.Kind)
        writer.WriteString("description", point.Description)
        writer.WriteString("sourceFile", point.SourceFile)
        writer.WriteNumber("sourceLine", point.SourceLine)
        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()

let private runList (args: Map<string, string>) =
    result {
        let! assembly = require args "assembly"
        let! scopeFile = require args "scope"
        let! output = require args "output"
        let! scope = MutationScope.load scopeFile

        use module' = readModule assembly true
        let! points = Catalog.build scope module'
        writeCatalog output assembly scopeFile scope points
        printfn $"cataloged {points.Length} mutant(s) from {assembly} -> {output}"
        return ()
    }

let private runApply (args: Map<string, string>) =
    result {
        let! assembly = require args "assembly"
        let! scopeFile = require args "scope"
        let! id = require args "id"
        let! output = require args "output"
        let! scope = MutationScope.load scopeFile

        // No symbols here: `apply` addresses the mutant purely by IL, and a module read with symbols
        // would additionally have to keep a rewritten PDB consistent, for no gain.
        use module' = readModule assembly false
        let! point, mutation, method = Catalog.resolve scope module' id
        do! Catalog.applyMutation point mutation method
        ensureParentDirectory output
        module'.Write output

        printfn $"applied {point.Kind} ({point.Description}) at {point.MethodName}#{point.InstructionIndex} -> {output}"

        return ()
    }

[<EntryPoint>]
let main argv =
    TestHostErrorMode.suppressModalDialogs ()

    let outcome =
        match List.ofArray argv with
        | "list" :: rest -> parseArgs rest |> Result.bind runList
        | "apply" :: rest -> parseArgs rest |> Result.bind runApply
        | _ -> Error usage

    match outcome with
    | Ok() -> 0
    | Error message ->
        eprintfn $"ProcessKit.Mutation: {message}"
        1
