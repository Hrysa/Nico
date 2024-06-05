namespace Nico.Core;

public class ObjectPool<T> where T : new()
{
    private static readonly ObjectPool<T> s_default = new();
    public static ObjectPool<T> Default => s_default;

    private Stack<T> _stack = new();

    T Borrow()
    {
        if (_stack.TryPop(out T? item))
        {
            return item;
        }

        return new T();
    }

    void Return(T item)
    {
        _stack.Push(item);
    }
}
