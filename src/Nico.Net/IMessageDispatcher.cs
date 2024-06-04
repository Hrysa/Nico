namespace Nico.Net;

public interface IMessageDispatcher
{
    bool Read(Span<byte> source, out Type key, out Span<byte> output);
    public bool Write<T>(Span<byte> source, out Span<byte> output);
}
