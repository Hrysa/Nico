using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Core;
using Nico.Discovery;
using Nico.Net;
using Nico.Net.Abstractions;

await new HostBuilder()
    .ConfigureServices(x =>
    {
        x.AddSingleton<ISocketReceiver, R2Udp>(_ => new R2Udp(new IPEndPoint(IPAddress.Any, 10001)));
        x.AddHostedService<R2UdpRunner>();
        // x.AddHostedService<NicoHostedService>();
    })
    .UseMessageHandler(x => { x.Scan(Assembly.GetEntryAssembly()!); })
    .UseLocalhostClustering(options => { }).Build().RunAsync();
