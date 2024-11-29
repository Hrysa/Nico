namespace Nico.Core;

public sealed class SPSCQueue<T>
{
    private readonly int _length;
    private readonly T[] _buffer;
    private int _head = 0;
    private int _tail = 0;

    public int Count => _tail - _head;

    public static SPSCQueue<T> Create(int size = 1024)
    {
        return new SPSCQueue<T>(size);
    }

    private SPSCQueue(int size)
    {
        _length = size;
        _buffer = new T[size];
    }

    private bool Empty => _head == _tail;
    private bool Full => (_tail + 1) % _length == _head;

    public bool Enqueue(T item)
    {
        if (Full)
        {
            return false;
        }

        _buffer[_tail] = item;
        _tail = (_tail + 1) % _length;
        return true;
    }

    public bool Dequeue(out T item)
    {
        if (Empty)
        {
            item = default!;
            return false;
        }

        item = _buffer[_head];
        _head = (_head + 1) % _length;

        return true;
    }

    public T[] GetAll()
    {
        if (Empty)
        {
            return [];
        }

        int copyTail = _tail;
        int cnt = _head < copyTail ? copyTail - _head : _length - _head + copyTail;
        T[] result = new T[cnt];
        if (_head < copyTail)
        {
            for (int i = _head; i < copyTail; i++)
            {
                result[i - _head] = _buffer[i];
            }
        }
        else
        {
            for (int i = _head; i < _length; i++)
            {
                result[i - _head] = _buffer[i];
            }

            for (int i = 0; i < copyTail; i++)
            {
                result[_length - _head + i] = _buffer[i];
            }
        }

        _head = copyTail;
        return result;
    }
}
