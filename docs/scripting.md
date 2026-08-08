# C# scripting

Opening a game project creates `<Project>.slnx` and `Scripts/<Project>.Scripts.csproj`. The Editor maintains engine references and the observed-property source generator without replacing user properties or package references.

## Creating and attaching a script

Scripts derive from `SceneScript`:

```csharp
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

public sealed partial class RotateObject : SceneScript
{
    public override void OnUpdate(double deltaTime)
    {
        Owner.Rotation += new Vector3(0f, (float)deltaTime, 0f);
    }
}
```

Every script source has an adjacent `.meta` file containing its stable `AssetId`. Scene format 4 attaches that asset through the node's component list:

```json
{
  "components": [
    {
      "type": "script",
      "enabled": true,
      "scriptId": "019fcbdc-bff3-7205-80fc-add216624bc2",
      "properties": []
    }
  ]
}
```

A node may contain multiple `ScriptComponent` instances.

## Lifecycle

- `OnReady()` runs after all script instances are attached and authored overrides are applied.
- `OnUpdate(deltaTime)` runs before the fixed physics update. Submit gameplay intent here.
- `OnLateUpdate(deltaTime)` runs after physics and interpolation. Follow cameras and presentation logic belong here.
- `OnDestroy()` runs before the scene or script runtime is discarded.

`Owner` is the node containing the script component. `Scene.FindNode(...)` queries the active graph, while `Scene.CreateNode<T>(...)` constructs a new unattached node.

## Inspector properties

`[Observe(Editor)]` exposes a generated partial property in the Inspector and persists authored overrides without runtime reflection:

```csharp
public sealed partial class Mover : SceneScript
{
    [Observe(Editor)]
    public partial float Speed { get; set; } = 4f;
}
```

Supported values are Boolean, signed/unsigned integers, floating-point numbers, strings, and `Vector2`/`Vector3`/`Vector4`. Add `Runtime` when runtime consumers also need change notification:

```csharp
[Observe(Editor, Runtime)]
public partial float Health { get; set; } = 100f;
```

The source generator creates storage, descriptors, typed read/write dispatch, and change notification. Inspector edits are stored as typed property overrides on `ScriptComponent`.

## Input

`Scene.Input` provides frame-stable keyboard state:

```csharp
if (Scene.Input.IsKeyDown(InputKey.W))
    velocity += forward * Speed;

if (Scene.Input.WasKeyPressed(InputKey.Space))
    Jump();
```

Use `IsKeyDown` for continuous controls and `WasKeyPressed`/`WasKeyReleased` for one-update transitions. Automatic native key-repeat events do not create additional press transitions.

## Physics movement

Dynamic bodies are moved by physics. Scripts should change their velocity rather than assigning `Owner.Position` every frame:

```csharp
var body = Owner.GetComponent<RigidBodyComponent>()!;
body.LinearVelocity = new Vector3(inputX, body.LinearVelocity.Y, inputZ);
```

Use a kinematic body for script-authored transforms. Directly setting a dynamic body's transform is a teleport and should normally reset its velocity.

## Play mode

Scripts do not execute in normal Editor edit mode. Play mode:

1. clones the authored scene and all components;
2. compiles the game script project in the background;
3. applies property overrides and calls `OnReady`;
4. runs scripts and physics against the clone;
5. calls `OnDestroy` and discards the clone on Stop.

Compilation errors leave the Editor in edit mode. An unhandled runtime script exception stops Play mode instead of crashing the renderer.
