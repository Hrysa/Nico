using System.Reflection;
using Engine.Core;
using Engine.Scripting;

namespace Editor;

/// <summary>Maps compiler-discovered script asset identities to collectible runtime types.</summary>
public sealed class CompiledScriptTypeCatalog : IScriptTypeCatalog
{
    private readonly Dictionary<AssetId, Type> _types;

    /// <summary>Gets the number of valid compiled script assets.</summary>
    public int Count => _types.Count;

    /// <summary>Creates and validates a catalog against one compiled game assembly.</summary>
    /// <param name="assembly">Collectible compiled game assembly.</param>
    /// <param name="descriptors">Compiler-discovered script descriptors.</param>
    public CompiledScriptTypeCatalog(
        Assembly assembly,
        IEnumerable<ScriptAssetDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(descriptors);
        _types = new Dictionary<AssetId, Type>();
        foreach (var descriptor in descriptors)
        {
            var type = assembly.GetType(descriptor.TypeName, throwOnError: false, ignoreCase: false)
                ?? throw new InvalidDataException(
                    $"Compiled script type '{descriptor.TypeName}' was not found for '{descriptor.SourcePath}'.");
            if (!typeof(SceneScript).IsAssignableFrom(type) || type.IsAbstract ||
                type.IsGenericTypeDefinition || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidDataException(
                    $"Compiled type '{descriptor.TypeName}' is not an attachable SceneScript.");
            }
            if (!_types.TryAdd(descriptor.Asset, type))
                throw new InvalidDataException($"Script asset '{descriptor.Asset}' is duplicated.");
        }
    }

    /// <inheritdoc/>
    public bool TryResolve(AssetId asset, out Type? scriptType)
    {
        return _types.TryGetValue(asset, out scriptType);
    }
}
