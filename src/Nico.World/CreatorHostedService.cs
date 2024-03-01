using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nico.Core.Attributes;

namespace Nico.Core;

public class CreatorHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CreatorHostedService> _logger;
    private readonly Dictionary<string, AssemblyLoadContext> _contexts = new();
    private readonly List<ICreator> _creators = new();

    public CreatorHostedService(ILogger<CreatorHostedService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        GC.Collect();

        var world = LoadModule("Single");
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            world.Update();
            Thread.Sleep(40);
        }
    }

    private World LoadModule(string module)
    {
        return LoadState(module, LoadSystem(module));
    }

    private Assembly LoadSystem(string module)
    {
        var assembly = LoadAssembly($"{module}.System");
        _logger.LogInformation($"load System, {assembly.GetTypes().Length} types found");
        return assembly;
    }

    private World LoadState(string module, Assembly systemAssembly)
    {
        var assembly = LoadAssembly($"{module}.State");
        var type = assembly.GetTypes().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(ICreator)));
        if (type is null)
        {
            throw new Exception("Creator not found");
        }

        var instance = Activator.CreateInstance(type) as ICreator ??
                       throw new Exception("activate creator instance failed");
        _creators.Add(instance);
        var world = instance.Build();
        foreach (var t in systemAssembly.GetTypes().Where(x =>
                     x.GetInterfaces().Contains(typeof(ISystem)) &&
                     x.GetCustomAttribute<AutoRegisterAttribute>() is not null))
        {
            world.RegisterSystem(t);
        }

        return world;
    }

    private Assembly LoadAssembly(string name)
    {
        if (_contexts.TryGetValue(name, out var value))
        {
            value.Unload();
        }

        var context = new AssemblyLoadContext(name, true);
        _contexts[name] = context;
        byte[] dllBytes = File.ReadAllBytes($"{context.Name}.dll");
        byte[] pdbBytes = File.ReadAllBytes($"{context.Name}.pdb");
        return context.LoadFromStream(new MemoryStream(dllBytes), new MemoryStream(pdbBytes));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var creator in _creators)
        {
            await creator.OnExit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;

        Console.WriteLine("Dispose");
    }
}
