using System.Net;

namespace Nico.Net.Abstractions;

public interface IConnection
{
    EndPoint RemoteEndPoint { get; }
    void Send(byte[] buffer);
}

public interface ISocketReceiver
{
    Action<IConnection, byte[], int> OnMessage { get; set; }
}
