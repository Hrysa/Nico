using Editor;
using Engine.Core;
using Engine.Graphics;
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
            File.WriteAllText(Path.Combine(directory, "Scripts", "MoveScript.cs"), """
                using System.Numerics;
                using Engine.Scripting;

                public sealed class MoveScript : SceneScript
                {
                    public override void OnUpdate(double deltaTime)
                    {
                        Owner.Position += Vector3.UnitX * (float)deltaTime;
                    }
                }
                """);
            var root = new Node3D { Name = "Scene" };
            var owner = new Node3D { Name = "Mover", ScriptType = "MoveScript" };
            root.AddChild(owner);

            using var host = GameScriptHost.BuildAndLoad(workspace);
            host.LoadScene(root);
            host.Update(0.5);

            Assert.Equal(0.5f, owner.Position.X);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
