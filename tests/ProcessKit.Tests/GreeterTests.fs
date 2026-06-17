namespace ProcessKit.Tests

open NUnit.Framework
open ProcessKit

[<TestFixture>]
type GreeterTests() =

    [<Test>]
    member _.``greet returns greeting with name``() =
        Assert.That(Greeter.greet "World", Is.EqualTo "Hello, World!")
