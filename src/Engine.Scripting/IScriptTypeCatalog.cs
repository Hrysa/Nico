using Engine.Core;

namespace Engine.Scripting;

/// <summary>Resolves persistent script asset identities to validated compiled runtime types.</summary>
public interface IScriptTypeCatalog
{
    /// <summary>Attempts to resolve one persistent script asset.</summary>
    /// <param name="asset">Persistent C# script source identity.</param>
    /// <param name="scriptType">Validated compiled SceneScript type when found.</param>
    /// <returns>True when the catalog contains a valid compiled type.</returns>
    bool TryResolve(AssetId asset, out Type? scriptType);
}
