using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nico.Core;

public class CreatorHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CreatorHostedService> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    private readonly Dictionary<string, AssemblyLoadContext> _contexts = new();
    private readonly List<ICreator> _creators = new();

    public CreatorHostedService(ILogger<CreatorHostedService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GC.Collect();
        LoadHotfix("Single");
        LoadModel("Single");
    }

    private void LoadHotfix(string module)
    {
        var assembly = LoadAssembly($"{module}.Hotfix");
        _logger.LogInformation($"load hotfix, total types: {assembly.GetTypes().Length}");
    }

    private void LoadModel(string module)
    {
        var assembly = LoadAssembly($"{module}.Model");
        var type = assembly.GetTypes().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(ICreator)));
        if (type is null)
        {
            throw new Exception("Creator not found");
        }

        var instance = Activator.CreateInstance(type) as ICreator ??
                       throw new Exception("activate creator instance failed");
        _creators.Add(instance);
        instance.Build();
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
        _cancellationTokenSource?.Cancel();
        foreach (var creator in _creators)
        {
            creator.OnExit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("Dispose");
    }
}