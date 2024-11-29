namespace Nico.Net.Abstractions;

public interface IMessageDispatcher
{
    bool Read(Span<byte> source, out Type key, out Span<byte> output);
    public bool Write<T>(Span<byte> source, out Span<byte> output);
}

public interface IRawMessageHandler<T>
{
    void OnRead(IMessageContext context, byte[] buffer);
}

public interface IMessageHandler<T>
{
    void OnRead(IMessageContext context, T request);
}

public interface IMessageContext
{
}
