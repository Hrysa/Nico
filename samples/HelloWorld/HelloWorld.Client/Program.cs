using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nico.Client;
using Nico.Core;

await new HostBuilder().ConfigureServices(x => { x.AddLogging(builder => builder.AddConsole()); })
    .UseNicoClient(builder => { builder.UseLocalhostClustering(); }).Build().RunAsync();
