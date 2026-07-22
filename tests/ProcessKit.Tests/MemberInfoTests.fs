namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

/// `ProcessGroup.MembersInfo` — the enriched, point-in-time member snapshot (T-185): same membership as
/// `Members`, each pid carrying its parent pid / image name / start time where the platform can report
/// them (`None` otherwise, never fabricated), a vanished pid omitted rather than invented, and the
/// member's argv / environment never exposed on any platform.
[<TestFixture>]
type MemberInfoTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    // A pid far above any the OS will assign in the test window, so it is never in a real process
    // snapshot / `/proc`: the deterministic "already gone" pid for the omission tests (no reliance on a
    // just-reaped pid the OS might recycle).
    let vanishedPid = 0x7FFFFFF0

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A single-process sleeper that spawns NO grandchildren, so the group's membership is a stable,
    // one-element set for the whole test. The earlier `cmd.exe /c ping ...` wrapper launched a transient
    // `ping.exe` grandchild that could join the Job Object between two independent member reads, making
    // `MembersInfo` a superset of a just-taken `Members` snapshot under load (F-05); invoking the sleeper
    // directly removes that transient child entirely. Windows `ping`'s stdout is discarded to the null
    // device so no pipe/console is touched. Both sleep ~3s; every test kills it well before that.
    let sleeper =
        if isWindows then
            Command.create "ping"
            |> Command.args [ "-n"; "4"; "127.0.0.1" ]
            |> Command.stdout StdioMode.Null
        else
            Command.create "sleep" |> Command.args [ "3" ]

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    [<Test>]
    member _.``MembersInfo reports the same membership as Members for a live child``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let members =
                    match group.Members() with
                    | Ok pids -> Set.ofSeq pids
                    | Error error -> failwith $"Members failed: {error}"

                let infoPids =
                    match group.MembersInfo() with
                    | Ok infos -> infos |> Seq.map (fun i -> i.Pid) |> Set.ofSeq
                    | Error error -> failwith $"MembersInfo failed: {error}"

                // The single-process child spawns no grandchildren, so the group is a stable one-element set
                // and the two independent snapshots (Members, then MembersInfo) observe EXACTLY the same pids
                // — no TOCTOU window for a transient child to join or drop between the reads. MembersInfo
                // reports precisely the membership Members does, never a fabricated or divergent set.
                Assert.That((infoPids = members), Is.True, "MembersInfo membership diverged from Members")
                Assert.That(infoPids, Is.Not.Empty)

                match running.Pid with
                | Some pid ->
                    Assert.That(members.Contains pid, Is.True, "the child is missing from Members")
                    Assert.That(infoPids.Contains pid, Is.True, "the child is missing from MembersInfo")
                | None -> Assert.Fail "expected a pid"

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``MembersInfo enriches a live child with parent pid, image name, and start time``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some childPid ->
                    let infos =
                        match group.MembersInfo() with
                        | Ok infos -> infos
                        | Error error -> failwith $"MembersInfo failed: {error}"

                    match infos |> Seq.tryFind (fun i -> i.Pid = childPid) with
                    | None -> Assert.Fail "the started child is missing from MembersInfo"
                    | Some info ->
                        // The child was spawned directly by this test process, so its recorded parent is us.
                        let currentPid = Process.GetCurrentProcess().Id
                        Assert.That(info.Ppid, Is.EqualTo(Some currentPid), "parent pid should be this process")

                        // Every enriching field is an option; where the platform can report it (Windows /
                        // Linux / macOS all can here) it is a real, non-empty value — never fabricated.
                        match info.ExeName with
                        | Some name -> Assert.That(String.IsNullOrWhiteSpace name, Is.False, "empty image name")
                        | None -> Assert.Fail "expected an image name on this platform"

                        Assert.That(info.StartTime.IsSome, Is.True, "expected a start time on this platform")

                        match info.StartTime with
                        | Some started -> Assert.That(started, Is.LessThanOrEqualTo(DateTime.Now.AddMinutes 1.0))
                        | None -> ()

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``the enrichment reader omits a vanished pid instead of fabricating its fields``() =
        // Drive the platform reader directly with a live pid (this process) plus a pid that is guaranteed
        // absent from the system snapshot / `/proc`. The absent pid must be dropped — never returned with
        // invented parent/image/time fields.
        let currentPid = Process.GetCurrentProcess().Id

        if isWindows then
            let infos = Native.Windows.readMembersInfo [ currentPid; vanishedPid ]
            let pids = infos |> List.map (fun i -> i.Pid) |> Set.ofList
            Assert.That(pids.Contains currentPid, Is.True, "the live pid was dropped")
            Assert.That(pids.Contains vanishedPid, Is.False, "a vanished pid was fabricated, not omitted")
        else
            match Native.Posix.readMemberInfo vanishedPid with
            | None -> ()
            | Some _ -> Assert.Fail "a vanished pid was fabricated, not omitted"

            match Native.Posix.readMemberInfo currentPid with
            | Some info -> Assert.That(info.Pid, Is.EqualTo currentPid)
            | None -> Assert.Fail "the live pid should enrich"

    [<Test>]
    member _.``MembersInfo never exposes the member's command line or environment``() : Task =
        task {
            use group = create ()

            // A distinctive secret placed in BOTH the child's command line and its environment. It must
            // appear in NO MemberInfo field — the snapshot excludes argv/env by construction, on every
            // platform (the same guarantee logs/spans/metrics give).
            let secret = "PROCESSKITSECRET9f83c2a1b7"

            let child =
                if isWindows then
                    // `rem <secret>` keeps the child alive ~5s while embedding the secret in cmd's command line.
                    shell $"ping -n 6 127.0.0.1 >nul & rem {secret}"
                else
                    shell $"sleep 6 # {secret}"
                |> Command.env "PROCESSKIT_TEST_SECRET" secret

            match! group.StartAsync child with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let infos =
                    match group.MembersInfo() with
                    | Ok infos -> infos
                    | Error error -> failwith $"MembersInfo failed: {error}"

                let leaks (info: MemberInfo) =
                    match info.ExeName with
                    | Some name -> name.Contains(secret, StringComparison.OrdinalIgnoreCase)
                    | None -> false

                Assert.That(infos |> Seq.exists leaks, Is.False, "argv/env leaked into a MemberInfo field")

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``MembersInfo returns a typed error once the group is released``() : Task =
        task {
            let group = create ()
            do! (group :> IAsyncDisposable).DisposeAsync()

            // Enumeration flows through the same lifecycle gate as Members: a released group is a typed,
            // non-transient Unsupported, never a throw or a fabricated empty snapshot.
            match group.MembersInfo() with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for a released group, got {other}"
        }
        :> Task
