using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Core;
using Nico.Discovery;
using Nico.Net;

await new HostBuilder()
    .ConfigureServices(x =>
    {
        x.AddSingleton<IMessageReceiver, R2Udp>(_ => new R2Udp(new IPEndPoint(IPAddress.Any, 10001)));
        x.AddHostedService<R2UdpRunner>();
        x.AddHostedService<NicoHostedService>();
    })
    .UseMessageHandler(null)
    .UseLocalhostClustering(options => { }).Build().RunAsync();
