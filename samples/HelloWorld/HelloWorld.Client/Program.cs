using System.Net;
using HelloWorld.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Core;
using Nico.Discovery;
using Nico.Net;
using Nico.Net.Abstractions;

await new HostBuilder()
    .ConfigureServices(x =>
    {
        x.AddSingleton<ISocketReceiver, R2Udp>(_ => new R2Udp());
        x.AddHostedService<R2UdpRunner>();
        x.AddHostedService<ConsoleHostedService>();
    })
    .Build().RunAsync();
