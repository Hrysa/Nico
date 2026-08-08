using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class GameScriptHostTests
{
    /// <summary>Verifies a generated project can compile and execute a scene script.</summary>
    [Fact]
    public void BuildAndLoad_ValidGameScript_UpdatesOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"game-script-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var workspace = ProjectSolutionScaffolder.Ensure(directory, typeof(Node).Assembly.Location);
            var scriptPath = Path.Combine(directory, "Scripts", "MoveScript.cs");
            File.WriteAllText(scriptPath, """
                using System.Numerics;
                using Engine.Scripting;

                public sealed partial class MoveScript : SceneScript
                {
                    [Observe(Editor)]
                    public partial float Speed { get; set; } = 2f;

                    public override void OnUpdate(double deltaTime)
                    {
                        Owner.Position += Vector3.UnitX * Speed * (float)deltaTime;
                    }
                }
                """);
            var database = new AssetDatabase(directory, EditorAssetImporters.Select);
            var script = Assert.IsType<AssetMetadataRecord>(database.FindByPath(scriptPath));
            var root = new Node3D { Name = "Scene" };
            var owner = new Node3D { Name = "Mover", ScriptId = script.Id };
            root.AddChild(owner);

            using var compiler = new GameScriptCompiler(workspace, database);
            using (var host = compiler.BuildAndLoad())
            {
                Assert.NotNull(host.Catalog);
                Assert.True(host.Catalog.TryResolve(script.Id, out _));
                var inspector = new SceneInspector(320f, 620f)
                {
                    ResolveScriptType = id =>
                        host.Catalog.TryResolve(id, out var type) ? type : null
                };
                inspector.Bind(owner);
                var speed = Assert.Single(inspector.Children.OfType<TextField>(),
                    field => field.Name == "ScriptProperty0_Speed");
                Assert.Equal("2", speed.Text);
                speed.SetFocus(true);
                speed.InvokeKeyDown((int)InputKey.Backspace);
                speed.InvokeTextInput('3');
                Assert.True(inspector.EditForm.CommitAll());
                host.LoadScene(root);
                host.Update(0.5);
            }

            using var cachedHost = compiler.BuildAndLoad();

            var assemblyPath = Path.Combine(directory, "Scripts", "bin", "EditorPlay",
                $"{Path.GetFileName(directory)}.Scripts.dll");
            File.Delete(CompiledScriptCatalog.GetCatalogPath(assemblyPath));
            using var runtimeCatalog = CompiledScriptCatalog.RecoverDevelopmentCatalog(
                assemblyPath, [(script.Id, "MoveScript")]);
            Assert.True(File.Exists(CompiledScriptCatalog.GetCatalogPath(assemblyPath)));
            using var runtime = new SceneScriptRuntime();
            runtime.Attach(root, runtimeCatalog);
            runtime.Start();
            runtime.Update(0.25);

            Assert.Equal(2.25f, owner.Position.X);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies compiler failures expose structured source diagnostics.</summary>
    [Fact]
    public void BuildAndLoad_InvalidGameScript_ReportsStructuredDiagnostic()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"game-script-error-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var workspace = ProjectSolutionScaffolder.Ensure(directory, typeof(Node).Assembly.Location);
            var sourcePath = Path.Combine(directory, "Scripts", "BrokenScript.cs");
            File.WriteAllText(sourcePath, "public sealed class BrokenScript { missing }");
            using var compiler = new GameScriptCompiler(workspace);

            var exception = Assert.Throws<ScriptBuildException>(() => compiler.BuildAndLoad());

            var diagnostic = Assert.Single(exception.Diagnostics,
                item => item.File.EndsWith("BrokenScript.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("error", diagnostic.Severity);
            Assert.StartsWith("CS", diagnostic.Code);
            Assert.True(diagnostic.Line > 0);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
