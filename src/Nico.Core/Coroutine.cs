using System.Collections;
using System.Collections.Concurrent;

namespace Nico.Core;

public class Coroutine
{
    private LinkedList<IEnumerator> _enumerators = new();

    private BlockingCollection<IEnumerator> _blockingCollection = new();

    private bool _ticking = false;

    public void Start(Func<IEnumerable> action)
    {
        var enumerator = action().GetEnumerator();
        _blockingCollection.Add(enumerator);
    }


    public void StartTick()
    {
        _ticking = true;

        Task.Run(() =>
        {
            while (_ticking || _enumerators.FirstOrDefault() is not null)
            {
                Tick();
            }

            Console.WriteLine("stop tick");
        });
    }

    private void Tick()
    {
        while (_blockingCollection.TryTake(out var e))
        {
            _enumerators.AddLast(e);
        }

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (var enumerator in _enumerators)
        {
            if (enumerator.Current is Waiter waiter)
            {
                if (waiter.At > now)
                {
                    continue;
                }
            }

            var r = enumerator.MoveNext();

            if (!r)
            {
                _enumerators.Remove(enumerator);
            }
        }
    }

    public static Waiter Wait(int ms)
    {
        return new Waiter { At = DateTimeOffset.Now.AddMilliseconds(ms) };
    }

    public struct Waiter
    {
        public DateTimeOffset At;
    }

    public void StopTick()
    {
        _ticking = false;
    }
}
