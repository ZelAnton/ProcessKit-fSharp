using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// The opt-in JSONL report serializer (T-375), exercised from C#: the `ToReportJson()` extension
/// methods and the raw `ReportJson.*TypeInfo` overload agree, and secrets never reach the wire.
[TestFixture]
public class ReportJsonTests
{
    [Test]
    public void Outcome_ToReportJson_carries_its_stable_kind_and_code()
    {
        var json = Outcome.NewExited(2).ToReportJson();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("exited"));
        Assert.That(root.GetProperty("code").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public void ProcessResult_ToReportJson_agrees_with_the_raw_JsonTypeInfo_overload()
    {
        var result = ProcessResult.Success("ready");

        var viaExtension = result.ToReportJson();
        var viaTypeInfo = JsonSerializer.Serialize(result, ReportJson.ProcessResultStringTypeInfo);

        Assert.That(viaExtension, Is.EqualTo(viaTypeInfo));

        using var document = JsonDocument.Parse(viaExtension);
        Assert.That(document.RootElement.GetProperty("success").GetBoolean(), Is.True);
    }

    [Test]
    public void ProcessResult_ToReportJson_never_carries_captured_stdout_or_stderr()
    {
        var result = ProcessResult.Create("TOKEN-OUT", "TOKEN-ERR", Outcome.NewExited(0), TimeSpan.Zero);

        var json = result.ToReportJson();

        Assert.That(json, Does.Not.Contain("TOKEN-OUT"));
        Assert.That(json, Does.Not.Contain("TOKEN-ERR"));
        Assert.That(json, Does.Not.Contain("stdout"));
        Assert.That(json, Does.Not.Contain("stderr"));
    }

    [Test]
    public void ReportJson_deserialization_is_refused_by_every_converter()
    {
        AssertWriteOnly(ReportJson.OutcomeTypeInfo);
        AssertWriteOnly(ReportJson.ProcessResultStringTypeInfo);
        AssertWriteOnly(ReportJson.ProcessResultBytesTypeInfo);
        AssertWriteOnly(ReportJson.ProcessGroupStatsTypeInfo);
        AssertWriteOnly(ReportJson.RunProfileTypeInfo);
        AssertWriteOnly(ReportJson.MemberInfoTypeInfo);
    }

    [Test]
    public void Outcome_ToReportJson_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ((Outcome)null!).ToReportJson());
    }

    private static void AssertWriteOnly<T>(JsonTypeInfo<T> typeInfo)
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("null", typeInfo));
    }
}
