using Microsoft.Extensions.Hosting;

namespace Nico.Net;

public class MessageHandler : IHostedService
{
    private readonly IMessageReceiver _messageReceiverReceiver;
    private readonly IMessageDispatcher _messageDispatcher;

    public MessageHandler(IMessageReceiver messageReceiverReceiver)
    {
        _messageReceiverReceiver = messageReceiverReceiver;
        _messageReceiverReceiver.OnMessage = OnMessage;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("message handler start");
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("message handler stop");
        await Task.CompletedTask;
    }

    public void OnMessage(IConnection connection, byte[] buffer, int length)
    {
        _messageDispatcher.Read(buffer.AsSpan().Slice(0, length), out var type, out var output);
        Console.WriteLine("new message");
    }
}
