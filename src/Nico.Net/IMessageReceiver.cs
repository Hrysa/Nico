using System.Net;

namespace Nico.Net;

public interface IMessageReceiver
{
    Action<IConnection, byte[], int> OnMessage { get; set; }
}

public interface IConnection
{
    EndPoint RemoteEndPoint { get; }
    void Send(byte[] buffer);
}
