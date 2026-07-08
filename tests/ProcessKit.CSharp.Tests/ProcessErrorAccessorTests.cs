using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Covers reading a `ProcessError` off the base type without destructuring the F# union — the only
/// practical way to do it from C# (`ProcessError.fs`'s read-without-destructure accessors, plus the
/// compiler-generated `IsX` case testers). Each `option`-typed accessor is a reference-typed
/// `FSharpOption&lt;'T&gt;` that compiles `None` to `null`, so `err.Code is { Value: var code }` (or
/// `Is.Null` for the `None` case) reads it with no F# pattern matching at all.
[TestFixture]
public class ProcessErrorAccessorTests
{
    [Test]
    public void Exit_accessors_expose_program_code_and_streams_without_destructuring()
    {
        ProcessError error = ProcessError.NewExit("git", 128, "partial stdout", "fatal: not a git repository");

        Assert.That(error.IsExit, Is.True);
        Assert.That(error.Program is { Value: "git" });
        Assert.That(error.Code is { Value: 128 });
        Assert.That(error.Stdout is { Value: "partial stdout" });
        Assert.That(error.Stderr is { Value: "fatal: not a git repository" });
        Assert.That(error.Combined is { Value: "partial stdout\nfatal: not a git repository" });
        Assert.That(error.Signal, Is.Null); // an Exit never carries a signal
        Assert.That(error.Message, Does.Contain("exited with code 128"));
    }

    [Test]
    public void NotFound_accessor_carries_only_the_program_no_streams_or_code()
    {
        ProcessError error = ProcessError.NewNotFound("does-not-exist", FSharpOption<string>.None);

        Assert.That(error.IsNotFound, Is.True);
        Assert.That(error.Program is { Value: "does-not-exist" });
        Assert.That(error.Stdout, Is.Null);
        Assert.That(error.Code, Is.Null);
        Assert.That(error.IsTransient, Is.False);
    }

    [Test]
    public void Spawn_and_Io_errors_classify_as_transient_via_the_instance_member()
    {
        Assert.That(ProcessError.NewSpawn("git", "EAGAIN").IsTransient, Is.True);
        Assert.That(ProcessError.NewIo("disk full").IsTransient, Is.True);
        Assert.That(ProcessError.NewCancelled("git").IsTransient, Is.False);
    }

    [Test]
    public async Task a_real_spawn_of_a_missing_program_surfaces_a_NotFound_error()
    {
        var command = new Command("this-program-does-not-exist-xyz");

        var result = await command.OutputStringAsync();

        Assert.That(result.IsOk, Is.False);
        Assert.That(result.ErrorValue.IsNotFound, Is.True);
    }
}
