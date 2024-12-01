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
    })
    .UseLocalhostClustering(options => { }).Build().RunAsync();
