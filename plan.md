# Collider Architecture Plan

## Goal

Replace the single shape-switched `ColliderComponent` with explicit collider component types. Collision geometry must be deliberately authored and serialized; runtime physics must not silently treat render geometry as collision geometry.

## Ownership

### Engine

- Define collider components and their shared material/trigger properties.
- Translate collider components into native physics shapes.
- Load explicit collision-mesh and terrain assets.
- Validate unsupported combinations, such as dynamic triangle-mesh colliders.
- Provide editor/import APIs for fitting or generating collision geometry.
- Provide collision queries, raycasts, layers, and terrain height sampling.

### Game or project

- Decide which objects and map regions collide.
- Store generated collision data as project assets.
- Add the desired collider components to scene objects.
- Configure collision layers, triggers, materials, and generation quality.

Generation may reuse engine tooling, but generated map collision data belongs to the project and should not be regenerated implicitly at game startup.

### Editor visualization

- Provide a general Scene viewport preview pass for objects and components that have no ordinary visible render geometry.
- Treat colliders as one preview provider alongside cameras, lights, audio sources, empty nodes, navigation data, joints, and other editor-only diagnostics.
- Keep previews editor-only; they must not add renderable scene nodes, appear in the Game viewport, or affect scene serialization and runtime behavior.
- Define renderer-independent preview primitives in `Engine.Graphics`, such as lines, wire meshes, translucent meshes, icons, frustums, and picking identifiers.
- Let the Editor register preview builders for node and component types instead of adding Editor or graphics dependencies to `Engine.Core` domain objects.
- Cache reusable preview meshes and loaded referenced assets instead of rebuilding or uploading them every frame.
- Keep Vulkan preview-pipeline and GPU-resource ownership in `Engine.Graphics.Silk`.

## Component Model

Introduce an abstract shared component:

```csharp
public abstract class ColliderComponent : Component
{
    public Vector3 Center { get; set; }
    public bool IsTrigger { get; set; }
    public float Friction { get; set; } = 0.5f;
    public float Restitution { get; set; }
}
```

Add these concrete components:

- `BoxColliderComponent`: `Size`
- `SphereColliderComponent`: `Radius`
- `CapsuleColliderComponent`: `Radius`, `Height`
- `CylinderColliderComponent`: `Radius`, `Height`
- `PlaneColliderComponent`: optional finite dimensions if required by the backend
- `MeshColliderComponent`: explicit collision `Mesh` asset reference
- `TerrainColliderComponent`: heightfield/terrain-data reference and world dimensions

The component type replaces `ColliderShape`. Inspector fields should therefore always be relevant to the selected collider type.

Nodes should support multiple collider components so compound collision shapes can be authored without requiring a special compound shape type.

## Editor Workflow

Expose explicit component commands:

```text
Add Component
└─ Physics
   ├─ Rigid Body
   ├─ Box Collider
   ├─ Sphere Collider
   ├─ Capsule Collider
   ├─ Cylinder Collider
   ├─ Plane Collider
   ├─ Mesh Collider
   └─ Terrain Collider
```

When a primitive collider is added to an object with render geometry, the editor may initialize its dimensions from the object's combined local-space bounds. This is a one-time authoring convenience: the fitted values are stored in the component and are not recomputed by runtime physics.

When a mesh collider is added, the editor may initially select the object's render mesh, but it must save that choice as an explicit asset reference. Missing mesh references should produce an editor validation warning and leave the collider inactive rather than invoking an implicit fallback.

## Scene Viewport Preview System

Scene viewport previews are a common editor facility for objects that are invisible, difficult to select, or need diagnostic geometry. The preview system should support:

- A registry that maps node or component types to allocation-conscious preview builders.
- World-space line, wire-mesh, translucent-mesh, icon, frustum, and bounds primitives.
- Stable picking identifiers that map a preview hit back to its owning node and optional component.
- Selected, hovered, warning, and globally-enabled diagnostic presentation states.
- Per-preview visibility toggles and category filters without changing scene visibility.
- Cached static geometry with per-frame transforms and colors, avoiding regenerated vertex arrays in render hot paths.
- Depth-tested and always-visible modes so each tool can choose the appropriate diagnostic behavior.

Initial common providers should include:

- Empty `Node3D`: origin marker and selectable icon.
- `PerspectiveCamera`: camera icon, forward direction, and projection frustum.
- Collider components: exact authored collision geometry.
- Future light and audio components: influence volumes and direction indicators.

The preview framework is diagnostic rendering only. It must not mutate scene content, serialize preview geometry, register runtime systems, or cause an invisible object to become a normal renderable.

## Collider Preview Provider

The Scene viewport must visualize the collision geometry authored on scene nodes:

- Primitive colliders render their actual box, sphere, capsule, cylinder, or plane dimensions after node and collider transforms are applied.
- `MeshColliderComponent` renders the triangles from its explicit collision-mesh reference, including the same transform used by physics.
- `TerrainColliderComponent` renders its heightfield or chunk boundaries from the explicit terrain-data reference.
- Selected colliders use a prominent translucent or wireframe color; unselected colliders may use a dimmer diagnostic color when the collider preview category is enabled.
- Invalid or missing collision assets render a distinct warning marker or bounds indicator rather than falling back to render geometry.
- Multiple colliders on one node are previewed independently so compound collision authoring remains understandable.
- Preview picking should identify the owning collider component, allowing the Inspector to edit the exact collider that was clicked.

Collider preview builders must read stored collider properties and explicit collision assets directly. They must not ask runtime physics to infer geometry or make render geometry participate in collision implicitly.

## Mesh Collider Rules

- A mesh collider represents triangle collision geometry, never a fitted primitive.
- Remove the runtime behavior that expands a model-root mesh collider across descendant render meshes when `Mesh` is null.
- Require every mesh collider to reference collision geometry explicitly.
- Initially support triangle meshes only on static rigid bodies.
- Prefer dedicated simplified collision meshes for large or detailed models.
- Allow import tooling to recognize project-defined collision nodes or established naming conventions.
- Consider convex mesh support separately for movable rigid bodies.

## Terrain Collider

`TerrainColliderComponent` is a built-in engine feature for true heightfield terrain. Its authored terrain data remains part of the game project.

Initial properties should include:

- Heightmap or terrain-data asset reference
- Horizontal dimensions
- Height range or vertical scale
- Center/offset inherited from the collider base
- Collision layer and material settings when those systems are available

Engine support should include:

- Native heightfield integration when supported by the physics backend
- Chunking for large terrain
- Efficient dirty-region rebuilds for editable terrain
- Terrain raycasts and height queries
- Serialization and editor inspection
- Static-only validation initially

A heightfield cannot represent caves, overhangs, or multiple surfaces at the same horizontal coordinate. Those map regions must use explicit, preferably chunked, mesh colliders.

## Map Collision Pipeline

```text
Map source/render geometry
        ↓ editor or import generation
Dedicated collision-mesh or terrain asset
        ↓ explicit scene reference
MeshColliderComponent or TerrainColliderComponent
        ↓ runtime
Engine.Physics native shape
```

Large map collision should be spatially chunked so loading, broad-phase culling, streaming, and rebuilds operate on bounded regions instead of one monolithic collider.

## Migration Plan

1. Add the collider base class and concrete primitive component types in `Engine.Core`.
2. Update `PhysicsWorld` to dispatch by concrete component type and attach every collider on a node.
3. Add explicit `MeshColliderComponent` resolution and remove descendant render-mesh inference.
4. Update scene serialization with distinct collider type records and migration for existing `ColliderShape` data.
5. Update the inspector and Add Component menu for the new component types.
6. Add one-time bounds fitting for primitive colliders in editor tooling.
7. Add the general Scene viewport preview framework and initial empty-node and camera providers.
8. Add collider providers for primitive, mesh, compound, and terrain collision geometry.
9. Update physics and scene-file tests, including compound colliders and invalid mesh references.
10. Design the terrain asset format and implement `TerrainColliderComponent` using the best native Bepu heightfield representation available.
11. Add chunked map-collision generation/import tooling and allocation/performance regression tests.

## Acceptance Criteria

- Runtime physics never selects render geometry implicitly.
- Users choose collider behavior by adding a concrete collider component.
- Multiple colliders can participate on one node.
- Primitive colliders can be fitted once from model bounds and then edited independently.
- Mesh collider references are explicit, serialized, and validated.
- Triangle mesh colliders reject unsupported movable-body configurations clearly.
- Terrain collision is engine-supported while terrain content remains project-owned.
- Invisible nodes and diagnostic components use one reusable Scene viewport preview and picking system.
- Cameras display a selectable icon, facing direction, and accurate projection frustum in the Scene viewport.
- The Scene viewport previews the exact authored collision geometry without adding it to normal scene rendering.
- Invalid collision references are visibly diagnosed and never preview render geometry as an implicit substitute.
- Existing scenes have a documented and tested migration path.
