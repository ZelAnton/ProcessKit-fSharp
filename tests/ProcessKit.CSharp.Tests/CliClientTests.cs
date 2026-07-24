using System;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

[TestFixture]
public class CliClientTests
{
    [Test]
    public void WithDefaults_null_return_throws_ArgumentNullException_naming_configure()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new CliClient("tool").WithDefaults(_ => null!));

        Assert.That(exception!.ParamName, Is.EqualTo("configure"));
        Assert.That(exception.Message, Does.Contain("configure"));
    }
}
