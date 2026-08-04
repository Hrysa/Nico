using Editor;
using Engine.Assets;
using Engine.Scripting;
using Xunit;

namespace Editor.Tests;

public class CSharpScriptAnalyzerTests
{
    /// <summary>Verifies semantic analysis discovers indirect inheritance and namespace aliases.</summary>
    [Fact]
    public void Analyze_IndirectAliasedSceneScript_ReturnsAssetDescriptor()
    {
        var directory = Directory.CreateTempSubdirectory("nico-script-analysis-");
        try
        {
            var path = Path.Combine(directory.FullName, "Mover.cs");
            File.WriteAllText(path, """
                using ScriptBase = Editor.Tests.AnalyzerScriptBase;
                namespace Example.Gameplay;
                public sealed class Mover : ScriptBase { }
                """);
            var database = new AssetDatabase(directory.FullName, EditorAssetImporters.Select);
            var asset = Assert.IsType<AssetMetadataRecord>(database.FindByPath(path));

            var result = CSharpScriptAnalyzer.Analyze(database, AppContext.BaseDirectory);

            Assert.Empty(result.Diagnostics);
            var script = Assert.Single(result.Scripts);
            Assert.Equal(asset.Id, script.Asset);
            Assert.Equal("Example.Gameplay.Mover", script.TypeName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies ambiguous and non-instantiable script assets receive actionable diagnostics.</summary>
    [Fact]
    public void Analyze_InvalidScriptShapes_ReturnsDiagnostics()
    {
        var directory = Directory.CreateTempSubdirectory("nico-script-analysis-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Ambiguous.cs"), """
                using Engine.Scripting;
                public sealed class First : SceneScript { }
                public sealed class Second : SceneScript { }
                """);
            File.WriteAllText(Path.Combine(directory.FullName, "Abstract.cs"), """
                using Engine.Scripting;
                public abstract class AbstractScript : SceneScript { }
                """);
            var database = new AssetDatabase(directory.FullName, EditorAssetImporters.Select);

            var result = CSharpScriptAnalyzer.Analyze(database, AppContext.BaseDirectory);

            Assert.Empty(result.Scripts);
            Assert.Equal(2, result.Diagnostics.Count);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("exactly one", StringComparison.Ordinal));
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("public, concrete", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

/// <summary>Provides a referenced indirect SceneScript base for semantic discovery tests.</summary>
public abstract class AnalyzerScriptBase : SceneScript
{
}
