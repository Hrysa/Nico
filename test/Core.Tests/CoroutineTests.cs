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
        IEnumerable Fn1()
        {
            for (int i = 0; i < 5; i++)
            {
                Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] 1 {DateTime.Now}");
                yield return Coroutine.Wait(1000);
            }
        }

        IEnumerable Fn2()
        {
            for (int i = 0; i < 5; i++)
            {
                Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] 2 {DateTime.Now}");
                yield return Coroutine.Wait(1000);
            }
        }

        _co.StartTick();

        _co.Start(Fn1);
        _co.Start(Fn2);
        // await Task.Delay(100);
        // _co.StopTick();
    }
}
