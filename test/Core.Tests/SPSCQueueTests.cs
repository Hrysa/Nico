using Nico.Core;

namespace Core.Tests;

public class SPSCQueueTests
{
    private SPSCQueue<int> _queue = SPSCQueue<int>.Create();

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Test_SPSCQueue_Cosume()
    {
        int l = 0;
        int r = 0;

        var task1 = Task.Run(() =>
        {
            for (int i = 0; i < 100000; i++)
            {
                l += i;
                while (!_queue.Enqueue(i))
                {
                }
            }
        });

        var task2 = Task.Run(() =>
        {
            for (int i = 0; i < 100000; i++)
            {
                bool success = false;
                int result = 0;

                while (!success)
                {
                    success = _queue.Dequeue(out result);
                }

                r += result;
            }
        });

        Task.WaitAll(task1, task2);

        Assert.That(r, Is.EqualTo(l));
    }
}
