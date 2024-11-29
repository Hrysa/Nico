using Microsoft.Extensions.Hosting;
using Nico.Net.Abstractions;

namespace Nico.Net;

public class MessageHandler : IHostedService
{
    private readonly ISocketReceiver _socketReceiver;
    private readonly IMessageDispatcher _messageDispatcher;

    public MessageHandler(ISocketReceiver socketReceiver)
    {
        _socketReceiver = socketReceiver;
        _socketReceiver.OnMessage = OnMessage;
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
