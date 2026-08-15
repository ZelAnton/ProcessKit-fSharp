namespace ProcessKit.Tests

open System
open System.Text.Json
open NUnit.Framework
open ProcessKit

/// The opt-in JSONL report serializer (T-375): `Outcome` / `ProcessResult` / `ProcessGroupStats` /
/// `RunProfile` / `MemberInfo`, each as one self-describing line — a stable `"kind"` machine identifier,
/// an explicit `null` for every unavailable metric, and never a captured stdout/stderr/argv/environment
/// value on the wire.
[<TestFixture>]
type ReportJsonTests() =

    let counters: ProcessIoCounters =
        { ReadBytes = 101L
          WriteBytes = 202L
          ReadOperations = 3L
          WriteOperations = 4L }

    // -----------------------------------------------------------------------------------------------
    // Outcome
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``Outcome.Exited reports its stable kind and code, with a null signal_number``() =
        let json = JsonSerializer.Serialize(Outcome.Exited 7, ReportJson.OutcomeTypeInfo)
        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "exited")
        Assert.That(root.GetProperty("code").GetInt32(), Is.EqualTo 7)
        Assert.That(root.GetProperty("signal_number").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``Outcome.Signalled reports its signal_number and a null code, or a null signal_number when unknown``() =
        let known =
            JsonSerializer.Serialize(Outcome.Signalled(Some 9), ReportJson.OutcomeTypeInfo)

        use knownDoc = JsonDocument.Parse known
        let knownRoot = knownDoc.RootElement
        Assert.That(knownRoot.GetProperty("kind").GetString(), Is.EqualTo "signalled")
        Assert.That(knownRoot.GetProperty("code").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(knownRoot.GetProperty("signal_number").GetInt32(), Is.EqualTo 9)

        let unknown =
            JsonSerializer.Serialize(Outcome.Signalled None, ReportJson.OutcomeTypeInfo)

        use unknownDoc = JsonDocument.Parse unknown
        Assert.That(unknownDoc.RootElement.GetProperty("signal_number").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``Outcome.TimedOut reports its kind with a null code and a null signal_number``() =
        let json = JsonSerializer.Serialize(Outcome.TimedOut, ReportJson.OutcomeTypeInfo)
        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "timed_out")
        Assert.That(root.GetProperty("code").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("signal_number").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``Outcome.Unobserved reports its kind and reason``() =
        let json =
            JsonSerializer.Serialize(Outcome.Unobserved "reap race", ReportJson.OutcomeTypeInfo)

        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "unobserved")
        Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo "reap race")
        Assert.That(root.GetProperty("code").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("signal_number").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``Outcome deserialization is refused — the schema is write-only``() =
        Assert.Throws<NotSupportedException>(
            Action(fun () ->
                JsonSerializer.Deserialize(
                    """{"kind":"exited","code":0,"signal_number":null}""",
                    ReportJson.OutcomeTypeInfo
                )
                |> ignore)
        )
        |> ignore

    // -----------------------------------------------------------------------------------------------
    // ProcessResult
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``ProcessResult reports the run, honours the command's own ok_codes, and counts overflow totals when known``
        ()
        =
        let result =
            ProcessResult<string>(
                "tool",
                "out",
                "err",
                Outcome.Exited 3,
                TimeSpan.FromSeconds 1.5,
                false,
                [ 0; 3 ],
                overflowTotals = (0, 0)
            )

        let json = result.ToReportJson()
        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "process_result")
        Assert.That(root.GetProperty("program").GetString(), Is.EqualTo "tool")
        Assert.That(root.GetProperty("outcome").GetProperty("kind").GetString(), Is.EqualTo "exited")
        Assert.That(root.GetProperty("outcome").GetProperty("code").GetInt32(), Is.EqualTo 3)
        // 3 is in ok_codes, so this run succeeded — a consumer must not have to re-derive that itself.
        Assert.That(root.GetProperty("success").GetBoolean(), Is.True)

        let okCodes: int[] =
            root.GetProperty("ok_codes").EnumerateArray()
            |> Seq.map _.GetInt32()
            |> Array.ofSeq

        Assert.That(okCodes, Is.EqualTo<int>([| 0; 3 |]))
        Assert.That(root.GetProperty("duration_secs").GetDouble(), Is.EqualTo(1.5).Within 1e-9)
        Assert.That(root.GetProperty("truncated").GetBoolean(), Is.False)
        Assert.That(root.GetProperty("total_lines").GetInt32(), Is.EqualTo 0)
        Assert.That(root.GetProperty("total_bytes").GetInt32(), Is.EqualTo 0)

        // The extension method and the raw JsonTypeInfo overload must agree byte for byte.
        Assert.That(json, Is.EqualTo(JsonSerializer.Serialize(result, ReportJson.ProcessResultStringTypeInfo)))

    [<Test>]
    member _.``ProcessResult reports null totals for a producer that never counted them``() =
        let result =
            ProcessResult<string>("tool", "out", "err", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ])

        use doc = JsonDocument.Parse(result.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("total_lines").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("total_bytes").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``a timed-out run reports no exit code and no success``() =
        let result =
            ProcessResult<string>("tool", "", "", Outcome.TimedOut, TimeSpan.FromMilliseconds 500.0, false, [ 0 ])

        use doc = JsonDocument.Parse(result.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("outcome").GetProperty("kind").GetString(), Is.EqualTo "timed_out")
        Assert.That(root.GetProperty("success").GetBoolean(), Is.False)

    [<Test>]
    member _.``a bytes-capturing ProcessResult reports identically without emitting a byte array``() =
        let bytes =
            ProcessResult<byte[]>(
                "tool",
                [| 0xFFuy; 0xFEuy |],
                "",
                Outcome.Exited 0,
                TimeSpan.FromMilliseconds 250.0,
                true,
                [ 0 ],
                overflowTotals = (12, 2048)
            )

        let json = bytes.ToReportJson()
        use doc = JsonDocument.Parse json
        let root = doc.RootElement
        Assert.That(root.GetProperty("program").GetString(), Is.EqualTo "tool")
        Assert.That(root.GetProperty("success").GetBoolean(), Is.True)
        Assert.That(root.GetProperty("truncated").GetBoolean(), Is.True)
        Assert.That(root.GetProperty("total_lines").GetInt32(), Is.EqualTo 12)
        Assert.That(root.GetProperty("total_bytes").GetInt32(), Is.EqualTo 2048)

    // -----------------------------------------------------------------------------------------------
    // Secret hygiene — the load-bearing property of this whole feature.
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``ProcessResult never serializes captured stdout or stderr content``() =
        let result =
            ProcessResult<string>(
                "tool",
                "TOKEN-IN-STDOUT",
                "TOKEN-IN-STDERR",
                Outcome.Exited 0,
                TimeSpan.Zero,
                false,
                [ 0 ]
            )

        let json = result.ToReportJson()
        Assert.That(json.Contains "TOKEN-IN-STDOUT", Is.False, json)
        Assert.That(json.Contains "TOKEN-IN-STDERR", Is.False, json)
        Assert.That(json.Contains "stdout", Is.False, json)
        Assert.That(json.Contains "stderr", Is.False, json)

    [<Test>]
    member _.``a bytes-capturing ProcessResult never serializes captured stdout content either``() =
        let secretBytes = System.Text.Encoding.UTF8.GetBytes "binary TOKEN payload"

        let bytes =
            ProcessResult<byte[]>("tool", secretBytes, "TOKEN-IN-STDERR", Outcome.Exited 0, TimeSpan.Zero, true, [ 0 ])

        let json = bytes.ToReportJson()
        Assert.That(json.Contains "TOKEN", Is.False, json)

    [<Test>]
    member _.``MemberInfo never carries an args, cmdline, or env key``() =
        let memberInfo =
            MemberInfo(4242, Some 1, Some "worker.exe", Some(DateTime(2024, 1, 1)))

        let json = memberInfo.ToReportJson()
        Assert.That(json.Contains "args", Is.False, json)
        Assert.That(json.Contains "cmdline", Is.False, json)
        Assert.That(json.Contains "env", Is.False, json)

    // -----------------------------------------------------------------------------------------------
    // ProcessGroupStats
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``unavailable ProcessGroupStats metrics are null, never a fabricated zero``() =
        let stats = ProcessGroupStats(3, None, None, None, None)
        use doc = JsonDocument.Parse(stats.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "process_group_stats")
        Assert.That(root.GetProperty("active_process_count").GetInt32(), Is.EqualTo 3)
        Assert.That(root.GetProperty("total_cpu_time_secs").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("peak_memory_bytes").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("io_read_bytes").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("io_write_bytes").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("io_read_operations").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("io_write_operations").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("peak_process_count").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``a partially-accounting mechanism nulls only what it cannot measure, and a measured zero stays a zero``
        ()
        =
        let zeroCounters: ProcessIoCounters =
            { ReadBytes = 0L
              WriteBytes = 4096L
              ReadOperations = 0L
              WriteOperations = 1L }

        let stats =
            ProcessGroupStats(1, None, Some TimeSpan.Zero, Some 0L, Some zeroCounters)

        use doc = JsonDocument.Parse(stats.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("total_cpu_time_secs").GetDouble(), Is.EqualTo 0.0)
        Assert.That(root.GetProperty("peak_memory_bytes").GetInt64(), Is.EqualTo 0L)

        Assert.That(
            root.GetProperty("io_read_bytes").GetInt64(),
            Is.EqualTo 0L,
            "a measured zero must not be indistinguishable from an absent measurement"
        )

        Assert.That(root.GetProperty("io_write_bytes").GetInt64(), Is.EqualTo 4096L)
        Assert.That(root.GetProperty("peak_process_count").ValueKind, Is.EqualTo JsonValueKind.Null)

    [<Test>]
    member _.``ProcessGroupStats reports every available measurement``() =
        let stats =
            ProcessGroupStats(2, Some 5L, Some(TimeSpan.FromMilliseconds 1500.0), Some 65536L, Some counters)

        use doc = JsonDocument.Parse(stats.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("active_process_count").GetInt32(), Is.EqualTo 2)
        Assert.That(root.GetProperty("peak_process_count").GetInt64(), Is.EqualTo 5L)
        Assert.That(root.GetProperty("total_cpu_time_secs").GetDouble(), Is.EqualTo(1.5).Within 1e-9)
        Assert.That(root.GetProperty("peak_memory_bytes").GetInt64(), Is.EqualTo 65536L)
        Assert.That(root.GetProperty("io_read_bytes").GetInt64(), Is.EqualTo 101L)
        Assert.That(root.GetProperty("io_write_bytes").GetInt64(), Is.EqualTo 202L)
        Assert.That(root.GetProperty("io_read_operations").GetInt64(), Is.EqualTo 3L)
        Assert.That(root.GetProperty("io_write_operations").GetInt64(), Is.EqualTo 4L)

    // -----------------------------------------------------------------------------------------------
    // RunProfile
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``RunProfile reports the outcome, the telemetry, and the derived average CPU cores``() =
        let profile =
            RunProfile(
                Outcome.Signalled(Some 9),
                TimeSpan.FromSeconds 2.0,
                Some(TimeSpan.FromSeconds 1.0),
                Some 4096L,
                Some counters,
                8
            )

        use doc = JsonDocument.Parse(profile.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "run_profile")
        Assert.That(root.GetProperty("outcome").GetProperty("kind").GetString(), Is.EqualTo "signalled")
        Assert.That(root.GetProperty("outcome").GetProperty("signal_number").GetInt32(), Is.EqualTo 9)
        Assert.That(root.GetProperty("duration_secs").GetDouble(), Is.EqualTo(2.0).Within 1e-9)
        Assert.That(root.GetProperty("cpu_time_secs").GetDouble(), Is.EqualTo(1.0).Within 1e-9)
        Assert.That(root.GetProperty("peak_memory_bytes").GetInt64(), Is.EqualTo 4096L)
        Assert.That(root.GetProperty("samples").GetInt32(), Is.EqualTo 8)
        Assert.That(root.GetProperty("avg_cpu_cores").GetDouble(), Is.EqualTo(0.5).Within 1e-9)

    [<Test>]
    member _.``RunProfile reports a null avg_cpu_cores when it cannot be derived``() =
        let profile = RunProfile(Outcome.Exited 0, TimeSpan.Zero, None, None, None, 0)
        use doc = JsonDocument.Parse(profile.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("cpu_time_secs").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("peak_memory_bytes").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("avg_cpu_cores").ValueKind, Is.EqualTo JsonValueKind.Null)

    // -----------------------------------------------------------------------------------------------
    // MemberInfo
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``MemberInfo reports metadata and a null start_time is an explicit null``() =
        let start = DateTime(2024, 1, 1, 12, 30, 0, DateTimeKind.Utc)
        let memberInfo = MemberInfo(4242, Some 1, Some "worker.exe", Some start)
        use doc = JsonDocument.Parse(memberInfo.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo "member_info")
        Assert.That(root.GetProperty("pid").GetInt32(), Is.EqualTo 4242)
        Assert.That(root.GetProperty("ppid").GetInt32(), Is.EqualTo 1)
        Assert.That(root.GetProperty("exe_name").GetString(), Is.EqualTo "worker.exe")
        Assert.That(root.GetProperty("start_time").GetDateTime(), Is.EqualTo start)

    [<Test>]
    member _.``unreadable MemberInfo fields are null, never fabricated``() =
        let memberInfo = MemberInfo(7, None, None, None)
        use doc = JsonDocument.Parse(memberInfo.ToReportJson())
        let root = doc.RootElement
        Assert.That(root.GetProperty("pid").GetInt32(), Is.EqualTo 7)
        Assert.That(root.GetProperty("ppid").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("exe_name").ValueKind, Is.EqualTo JsonValueKind.Null)
        Assert.That(root.GetProperty("start_time").ValueKind, Is.EqualTo JsonValueKind.Null)

    // -----------------------------------------------------------------------------------------------
    // JSONL formatting — one compact object per line, no embedded newline.
    // -----------------------------------------------------------------------------------------------

    [<Test>]
    member _.``every report line is one compact object with no embedded newline, and a stream of them parses as JSONL``
        ()
        =
        let outcomeLine = Outcome.Exited 0 |> _.ToReportJson()

        let resultLine =
            ProcessResult<string>("tool", "", "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ]).ToReportJson()

        let statsLine = ProcessGroupStats(1, None, None, None, None).ToReportJson()

        let profileLine =
            RunProfile(Outcome.Exited 0, TimeSpan.Zero, None, None, None, 0).ToReportJson()

        let memberLine = MemberInfo(1, None, None, None).ToReportJson()

        for line in [ outcomeLine; resultLine; statsLine; profileLine; memberLine ] do
            Assert.That(line.Contains "\n", Is.False, line)
            Assert.That(line.Contains "\r", Is.False, line)

        let jsonl =
            String.Join("\n", [ outcomeLine; resultLine; statsLine; profileLine; memberLine ])

        for line in jsonl.Split '\n' do
            use doc = JsonDocument.Parse line
            Assert.That(doc.RootElement.GetProperty("kind").ValueKind, Is.EqualTo JsonValueKind.String)
