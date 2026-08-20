using System;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

[TestFixture]
public class RetryValidationTests
{
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void Retry_builders_reject_negative_maxAttempts(int maxAttempts)
    {
        var fixedError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Command("tool").Retry(maxAttempts, TimeSpan.Zero, _ => true));
        var backoffError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Command("tool").RetryBackoff(maxAttempts, TimeSpan.Zero, 1.0, TimeSpan.Zero, false, _ => true));

        Assert.That(fixedError!.ParamName, Is.EqualTo("maxAttempts"));
        Assert.That(backoffError!.ParamName, Is.EqualTo("maxAttempts"));
    }

    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void WithDefaults_rejects_negative_maxAttempts(int maxAttempts)
    {
        var fixedError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CliClient("tool").WithDefaults(command =>
                command.Retry(maxAttempts, TimeSpan.Zero, _ => true)));
        var backoffError = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CliClient("tool").WithDefaults(command =>
                command.RetryBackoff(maxAttempts, TimeSpan.Zero, 1.0, TimeSpan.Zero, false, _ => true)));

        Assert.That(fixedError!.ParamName, Is.EqualTo("maxAttempts"));
        Assert.That(backoffError!.ParamName, Is.EqualTo("maxAttempts"));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Retry_builders_accept_single_run_boundaries(int maxAttempts)
    {
        new Command("tool").Retry(maxAttempts, TimeSpan.Zero, _ => true);
        new Command("tool").RetryBackoff(maxAttempts, TimeSpan.Zero, 1.0, TimeSpan.Zero, false, _ => true);

        new CliClient("tool").WithDefaults(command =>
            command.Retry(maxAttempts, TimeSpan.Zero, _ => true));
        new CliClient("tool").WithDefaults(command =>
            command.RetryBackoff(maxAttempts, TimeSpan.Zero, 1.0, TimeSpan.Zero, false, _ => true));
    }
}
