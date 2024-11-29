using System.Collections;

namespace Nico.Core;

public class Coroutine
{
    private List<IEnumerator> _enumerators = new();
    private Queue<IEnumerator> _queue = new();

    public bool Empty => !_enumerators.Any();

    public void Start(Func<Coroutine, IEnumerable> action)
    {
        _enumerators.Add(action(this).GetEnumerator());
    }

    public void Start(Func<IEnumerable> action)
    {
        _enumerators.Add(action().GetEnumerator());
    }

    public void Tick()
    {
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

            if (!enumerator.MoveNext())
            {
                _queue.Enqueue(enumerator);
            }
        }

        while (_queue.TryDequeue(out var enumerator))
        {
            _enumerators.Remove(enumerator);
        }
    }

    public Waiter Wait(int ms)
    {
        return new Waiter { At = DateTimeOffset.Now.AddMilliseconds(ms) };
    }

    public struct Waiter
    {
        public DateTimeOffset At;
    }
}
