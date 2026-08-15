using System;
using System.Linq;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Covers reading the containment capability snapshot (`ProcessGroup.Capabilities`) from C#, which is the
/// consumer half of the honest-report contract: a `Capability` is a three-valued F# union, and it has to
/// be as usable from a C# orchestrator picking a policy as from F#. The compiler-generated `IsAvailable`
/// / `IsQualified` / `IsUnsupported` testers plus the named case properties (`Qualification`, `Requires`)
/// read it with no F# pattern matching at all, and `Mechanism` is an `option` that compiles `None` to
/// `null`, so `is { Value: var mechanism }` unwraps it.
[TestFixture]
public class CapabilitySnapshotTests
{
    [Test]
    public void the_snapshot_reports_a_mechanism_and_reads_case_by_case_from_CSharp()
    {
        var capabilities = ProcessGroup.Capabilities();

        // A default group is creatable on every platform ProcessKit supports, so the snapshot names the
        // mechanism it would get rather than a refusal.
        Assert.That(capabilities.Mechanism is { Value: var mechanism }
                    && (mechanism.IsJobObject || mechanism.IsCgroupV2 || mechanism.IsProcessGroup));

        var description = Describe(capabilities.Adoption);
        Assert.That(description, Is.Not.Empty);
    }

    [Test]
    public void every_axis_that_is_not_plainly_available_explains_itself()
    {
        var capabilities = ProcessGroup.Capabilities();

        Capability[] axes =
        [
            capabilities.Creation,
            capabilities.Adoption,
            capabilities.Pty,
            capabilities.PtyResize,
            capabilities.KillOnParentDeath,
            capabilities.Signals.Kill,
            capabilities.Signals.SoftStop,
            capabilities.Signals.Arbitrary,
            capabilities.ResourceLimits.MemoryMax,
            capabilities.ResourceLimits.OomGroupKill,
            capabilities.ResourceLimits.MaxProcesses,
            capabilities.ResourceLimits.CpuQuota,
            capabilities.ResourceLimits.CpuTimeMax,
            capabilities.ResourceLimits.CpuAffinity,
            capabilities.ResourceLimits.IoMax,
            capabilities.ResourceLimits.UiRestrictions,
            capabilities.ResourceLimits.LiveUpdate,
        ];

        foreach (var axis in axes)
        {
            // Never a bare "no": an unavailable or qualified axis always carries its reason, and `Detail`
            // is the same text the case itself holds.
            if (axis.IsAvailable)
            {
                Assert.That(axis.Detail, Is.Null);
            }
            else
            {
                Assert.That(axis.Detail is { Value: var detail } && !string.IsNullOrWhiteSpace(detail));
                Assert.That(axis.Detail!.Value, Is.EqualTo(Describe(axis)));
            }
        }
    }

    [Test]
    public void a_refused_option_set_reports_no_mechanism_and_names_the_precondition()
    {
        // Whole-tree OOM kill is a Linux cgroup v2 policy. Off Linux it is refused outright; on Linux it
        // needs a usable cgroup v2 hierarchy. Either way the snapshot must agree with `Create`.
        var options = new ProcessGroupOptions().WithOomGroupKill();
        var capabilities = ProcessGroup.Capabilities(options);
        var mechanism = capabilities.Mechanism;
        var created = ProcessGroup.Create(options);

        if (mechanism is null)
        {
            Assert.That(capabilities.Creation.IsUnsupported, Is.True);
            Assert.That(((Capability.Unsupported)capabilities.Creation).Requires, Is.Not.Empty);
            Assert.That(created.IsOk, Is.False);
        }
        else if (created.IsOk)
        {
            using var group = created.ResultValue;
            Assert.That(group.Mechanism, Is.EqualTo(mechanism.Value));
        }
        else
        {
            // The only honest way a named mechanism can still fail to be created is the qualification the
            // snapshot already stated (a cgroup hierarchy whose controllers cannot be delegated here).
            Assert.That(capabilities.Creation.IsQualified, Is.True);
        }
    }

    [Test]
    public void the_helper_list_names_each_binary_and_what_it_is_for()
    {
        var capabilities = ProcessGroup.Capabilities();

        Assert.That(capabilities.Helpers, Is.Not.Empty);
        Assert.That(capabilities.Helpers.All(helper =>
            !string.IsNullOrWhiteSpace(helper.Name) && !string.IsNullOrWhiteSpace(helper.Purpose)));

        if (OperatingSystem.IsWindows())
        {
            Assert.That(capabilities.Helpers.Select(helper => helper.Name), Does.Contain("cmd.exe"));
            Assert.That(capabilities.KillOnParentDeathScope.IsWholeTree, Is.True);
        }
        else
        {
            Assert.That(capabilities.Helpers.Select(helper => helper.Name), Does.Contain("/bin/sh"));
        }
    }

    /// The reason text, read the way a C# status page would: the case testers, then the named field of
    /// whichever case carries it.
    private static string Describe(Capability capability) => capability switch
    {
        { IsAvailable: true } => "available",
        Capability.Qualified qualified => qualified.Qualification,
        Capability.Unsupported unsupported => unsupported.Requires,
        _ => throw new InvalidOperationException("unreachable: Capability has exactly three cases"),
    };
}
