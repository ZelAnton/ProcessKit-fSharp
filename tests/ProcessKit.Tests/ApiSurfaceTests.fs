namespace ProcessKit.Tests

open System
open System.IO
open System.Reflection
open System.Text
open NUnit.Framework

/// Locks the PUBLIC API surface of the shipped assemblies. The exported surface is rendered to a
/// deterministic, sorted text snapshot and compared against a checked-in baseline; any add / remove /
/// rename / signature change to the public API fails the test until the baseline is deliberately
/// updated. This is the F# stand-in for Roslyn's PublicApiAnalyzers, which does not support F#.
///
/// To update a baseline after an intentional API change: run the test, take the `*.received.txt`
/// written next to the test assembly, review the diff, and copy it over the matching
/// `*.approved.txt` in `tests/ProcessKit.Tests/`.
[<TestFixture>]
type ApiSurfaceTests() =

    static let memberFlags =
        BindingFlags.Public
        ||| BindingFlags.Instance
        ||| BindingFlags.Static
        ||| BindingFlags.DeclaredOnly

    static let fullName (t: Type) : string =
        match t.FullName with
        | null -> t.Name
        | n -> n

    // Culture-invariant (ordinal) ordering so the snapshot is byte-identical across every CI OS.
    static let ordinal (a: string) (b: string) : int = String.CompareOrdinal(a, b)

    /// Readable, deterministic rendering of a type reference: namespace-qualified, generics as
    /// `<...>`, arrays as `[]`, byrefs as `&`.
    static let rec niceName (t: Type) : string =
        if t.IsByRef then
            match t.GetElementType() with
            | null -> fullName t
            | e -> niceName e + "&"
        elif t.IsArray then
            match t.GetElementType() with
            | null -> fullName t
            | e -> niceName e + "[" + String(',', t.GetArrayRank() - 1) + "]"
        elif t.IsGenericParameter then
            t.Name
        elif t.IsGenericType then
            let gdef = t.GetGenericTypeDefinition()
            let raw = gdef.Name
            let tick = raw.IndexOf '`'
            let bare = if tick >= 0 then raw.Substring(0, tick) else raw

            let ns =
                match gdef.Namespace with
                | null -> ""
                | s -> s + "."

            let args = t.GetGenericArguments() |> Array.map niceName |> String.concat ", "
            ns + bare + "<" + args + ">"
        else
            fullName t

    static let renderMethod (m: MethodInfo) : string option =
        let n = m.Name

        let isAccessor =
            m.IsSpecialName
            && (n.StartsWith "get_"
                || n.StartsWith "set_"
                || n.StartsWith "add_"
                || n.StartsWith "remove_")

        if isAccessor then
            None
        else
            let prefix = if m.IsStatic then "static member " else "member "

            let generics =
                if m.IsGenericMethodDefinition then
                    "<"
                    + (m.GetGenericArguments() |> Array.map (fun a -> a.Name) |> String.concat ", ")
                    + ">"
                else
                    ""

            let ps =
                m.GetParameters()
                |> Array.map (fun p -> niceName p.ParameterType)
                |> String.concat ", "

            Some(sprintf "%s%s%s(%s) : %s" prefix n generics ps (niceName m.ReturnType))

    static let renderProperty (p: PropertyInfo) : string =
        let isPublic (m: MethodInfo | null) =
            match m with
            | null -> false
            | m -> m.IsPublic

        let acc =
            [ if isPublic p.GetMethod then
                  "get"
              if isPublic p.SetMethod then
                  "set" ]
            |> String.concat "; "

        let index =
            match p.GetIndexParameters() with
            | [||] -> ""
            | ps ->
                "["
                + (ps |> Array.map (fun p -> niceName p.ParameterType) |> String.concat ", ")
                + "]"

        sprintf "property %s%s : %s { %s }" p.Name index (niceName p.PropertyType) acc

    static let renderField (f: FieldInfo) : string =
        let modifier =
            if f.IsLiteral then "literal "
            elif f.IsInitOnly then "readonly "
            else ""

        sprintf "field %s%s : %s" modifier f.Name (niceName f.FieldType)

    static let renderEvent (e: EventInfo) : string =
        let handler =
            match e.EventHandlerType with
            | null -> "?"
            | t -> niceName t

        sprintf "event %s : %s" e.Name handler

    static let renderMember (m: MemberInfo) : string option =
        match m with
        | :? MethodInfo as mi -> renderMethod mi
        | :? PropertyInfo as pi -> Some(renderProperty pi)
        | :? FieldInfo as fi -> Some(renderField fi)
        | :? EventInfo as ei -> Some(renderEvent ei)
        | :? ConstructorInfo as ci ->
            let ps =
                ci.GetParameters()
                |> Array.map (fun p -> niceName p.ParameterType)
                |> String.concat ", "

            Some(sprintf "new(%s)" ps)
        | _ ->
            // Nested types are dumped as their own top-level entries (GetExportedTypes lists them).
            None

    static let typeKind (t: Type) : string =
        if t.IsInterface then "interface"
        elif t.IsEnum then "enum"
        elif typeof<Delegate>.IsAssignableFrom t then "delegate"
        elif t.IsValueType then "struct"
        elif t.IsAbstract && t.IsSealed then "module" // an F# module compiles to a static class
        else "class"

    static let dumpType (sb: StringBuilder) (t: Type) =
        let baseLabel =
            match t.BaseType with
            | null -> ""
            | b when b = typeof<obj> || b = typeof<ValueType> || b = typeof<Enum> -> ""
            | b -> " : " + niceName b

        sb.AppendLine(sprintf "%s %s%s" (typeKind t) (fullName t) baseLabel) |> ignore

        t.GetInterfaces()
        |> Array.map niceName
        |> Array.sortWith ordinal
        |> Array.iter (fun i -> sb.AppendLine("  :> " + i) |> ignore)

        t.GetMembers memberFlags
        |> Array.choose renderMember
        |> Array.sortWith ordinal
        |> Array.iter (fun m -> sb.AppendLine("  " + m) |> ignore)

        sb.AppendLine() |> ignore

    /// The deterministic public-API snapshot of an assembly (LF line endings).
    static let dumpApi (assembly: Assembly) : string =
        let sb = StringBuilder()

        assembly.GetExportedTypes()
        |> Array.sortWith (fun a b -> ordinal (fullName a) (fullName b))
        |> Array.iter (dumpType sb)

        sb.ToString().Replace("\r\n", "\n")

    static let summarizeDiff (approved: string) (actual: string) : string =
        let lines (s: string) = s.Split('\n') |> Set.ofArray
        let approvedSet = lines approved
        let actualSet = lines actual

        let removed =
            Set.difference approvedSet actualSet
            |> Set.toList
            |> List.filter (fun l -> l <> "")

        let added =
            Set.difference actualSet approvedSet
            |> Set.toList
            |> List.filter (fun l -> l <> "")

        let cap n (xs: string list) = xs |> List.truncate n

        let block label (xs: string list) =
            if List.isEmpty xs then
                ""
            else
                "\n"
                + label
                + "\n"
                + (cap 40 xs |> List.map (fun l -> "  " + l) |> String.concat "\n")

        block (sprintf "Removed from public API (%d):" (List.length removed)) removed
        + block (sprintf "Added to public API (%d):" (List.length added)) added

    static let verify (assembly: Assembly) (approvedName: string) =
        let actual = dumpApi assembly
        let baseDir = AppContext.BaseDirectory
        let approvedPath = Path.Combine(baseDir, approvedName)

        let approved =
            if File.Exists approvedPath then
                File.ReadAllText(approvedPath).Replace("\r\n", "\n")
            else
                ""

        if actual <> approved then
            let receivedPath =
                Path.Combine(baseDir, approvedName.Replace(".approved.txt", ".received.txt"))

            File.WriteAllText(receivedPath, actual)

            Assert.Fail(
                sprintf
                    "Public API of %s changed.%s\n\nApproved baseline: %s\nReceived (actual): %s\nIf the change is intentional, copy the received file over tests/ProcessKit.Tests/%s and review the diff."
                    (assembly.GetName().Name)
                    (summarizeDiff approved actual)
                    approvedPath
                    receivedPath
                    approvedName
            )

    [<Test>]
    member _.``ProcessKit public API matches the approved baseline``() =
        verify typeof<ProcessKit.Command>.Assembly "PublicApi.ProcessKit.approved.txt"

    [<Test>]
    member _.``DI extensions public API matches the approved baseline``() =
        verify
            typeof<ProcessKit.Extensions.DependencyInjection.ServiceCollectionExtensions>.Assembly
            "PublicApi.ProcessKit.Extensions.DependencyInjection.approved.txt"

    [<Test>]
    member _.``Testing package public API matches the approved baseline``() =
        verify typeof<ProcessKit.Testing.ScriptedRunner>.Assembly "PublicApi.ProcessKit.Testing.approved.txt"
