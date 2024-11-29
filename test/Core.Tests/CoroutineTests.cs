using System.Collections;
using Nico.Core;

namespace Core.Tests;

public class CoroutineTests
{
    private Coroutine _co;

    [SetUp]
    public void Setup()
    {
        _co = new Coroutine();
    }

    [Test]
    public void Test()
    {
        IEnumerable Fn()
        {
            yield return null;
        }

        _co.Start(Fn);
        _co.Tick();
        _co.Tick();
        _co.Tick();
    }
}
