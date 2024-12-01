using HelloWorld.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await new HostBuilder()
    .ConfigureServices(x =>
    {
        x.AddHostedService<ConsoleHostedService>();
        x.AddLogging(builder => builder.AddConsole());
    })
    .Build().RunAsync();
