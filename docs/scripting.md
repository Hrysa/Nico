# C# scripting

Opening a game project creates `<Project>.slnx` and `Scripts/<Project>.Scripts.csproj`. The Editor refreshes the generated project's engine references without replacing user properties or package references.

Create a script in `Scripts`, for example:

```csharp
using System.Numerics;
using Engine.Scripting;

namespace ExampleGame;

public sealed class RotateObject : SceneScript
{
    public override void OnUpdate(double deltaTime)
    {
        Owner.Rotation += Vector3.UnitY * (float)deltaTime;
    }
}
```

Attach the compiled type to a node with its `scriptType` scene property:

```json
{
  "type": "cube",
  "name": "SceneCube",
  "scriptType": "ExampleGame.RotateObject, example_game.Scripts"
}
```

The assembly suffix is optional because the Editor currently resolves scripts only from the game's primary script assembly.

## Play mode

Scripts never execute while the Editor is in normal edit mode. Press **Play** in the title bar to create an isolated in-memory clone of the authored scene and compile the game project in the background. A modal progress dialog blocks editing while the window continues repainting. When compilation finishes, the Game viewport, Scene viewport, hierarchy, selection, gizmo, and Inspector switch to the runtime clone. Play-time Inspector and gizmo edits affect only that clone; saving continues to use the authored scene.

Press **Stop** to invoke script destruction, unload the game assembly, and discard all runtime changes. The Game viewport then returns to the authored scene.

## Lifecycle

- `OnReady()` runs after every script in the play-mode scene has been attached.
- `OnUpdate(deltaTime)` runs before each rendered frame.
- `OnDestroy()` runs when the scene changes or the Editor exits.

Use `Owner` to modify the attached node. Use `Scene.FindNode(...)` to query the graph and `Scene.CreateNode<T>(...)` to construct an unattached node before adding it to a parent.

Script compilation errors are logged and the Editor remains in edit mode. A runtime script exception stops play mode instead of crashing the renderer.
