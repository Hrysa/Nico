using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nico.Runtime.Hosting;

await new HostBuilder().ConfigureServices(x => { x.AddLogging(builder => builder.AddConsole()); })
    .UseNico(builder => { builder.UseLocalhostClustering(); }).Build().RunAsync();
