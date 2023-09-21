namespace Nico.Core;

public class Ref<T>
{
    private T _value;
    public T Value => _value;

    public ref T DeRef => ref _value;

    public Ref(T value)
    {
        _value = value;
    }
}

