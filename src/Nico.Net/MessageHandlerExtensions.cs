using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Nico.Net;

public static class MessageHandlerExtensions
{
    public static IHostBuilder UseMessageHandler(this IHostBuilder builder,
        Action<MessageHandlerBuildOptions> configureOptions)
    {
        builder.ConfigureServices(x => { x.AddHostedService<MessageHandler>(); });
        return builder;
    }
}
