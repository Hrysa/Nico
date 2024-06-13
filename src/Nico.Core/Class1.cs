// using Microsoft.Extensions.Hosting;
// using Nico.Discovery.Abstractions;
//
// namespace Nico.Core;
//
// public class NicoHostedService(IDiscoveryClient discoveryClient) : IHostedService
// {
//     private readonly IDiscoveryClient _discoveryClient = discoveryClient;
//
//     public Task StartAsync(CancellationToken cancellationToken)
//     {
//         _discoveryClient.Connect();
//         return Task.CompletedTask;
//     }
//
//     public Task StopAsync(CancellationToken cancellationToken)
//     {
//         _discoveryClient.Disconnect();
//         return Task.CompletedTask;
//     }
// }
