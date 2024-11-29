namespace Nico.Rpc.Tests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Test1()
    {
        Assert.That(new Class1().Add(1, 2), Is.EqualTo(3));
    }
}
