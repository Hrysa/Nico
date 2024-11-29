using Nico.Net.Abstractions;

namespace Nico.Net;

public class MessageDispatcher : IMessageDispatcher
{
    private Dictionary<int, Type> _map = new();

    public bool Read(Span<byte> source, out Type? key, out Span<byte> output)
    {
        var no = BitConverter.ToInt32(source.Slice(0));

        if (!_map.ContainsKey(no))
        {
            key = default;
            output = default;
            return false;
        }

        key = _map[no];
        output = source.Slice(4);
        return true;
    }

    public bool Write<T>(Span<byte> source, out Span<byte> output)
    {
        var type = typeof(T);
        if (!_map.ContainsValue(type))
        {
            output = default;
            return false;
        }

        Span<byte> buff = new byte[source.Length + 4];
        var no = _map.First(x => x.Value == type).Key;
        BitConverter.GetBytes(no).CopyTo(buff);
        source.CopyTo(buff.Slice(4));
        output = buff;

        return true;
    }
}
