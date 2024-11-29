using HelloWorld.Shared;
using Nico.Net.Abstractions;

namespace HelloWorld.App;

public class ConsoleHandler : IMessageHandler<Message>
{
    public void OnRead(IMessageContext context, Message request)
    {
        throw new NotImplementedException();
    }
}
