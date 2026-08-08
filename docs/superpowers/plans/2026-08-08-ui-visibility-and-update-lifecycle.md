# UI Visibility and Update Lifecycle Plan

## Goal

Give every retained UI element one consistent lifecycle contract so an inactive dock page does not
consume layout, paint, input, animation, binding-refresh, or offscreen-rendering work. Remove
panel-specific per-frame polling from `Editor/Program.cs` where possible.

This plan covers visibility, activation, and the agreed generated observation contract used to
replace Inspector polling.

## Problem

DockHost currently hides inactive tab content for retained UI painting, but work can still happen
outside paint traversal:

- application code can call panel methods such as `SceneInspector.RefreshValues()` every frame;
- a hidden viewport can continue submitting its FBO render queue;
- time-dependent descendants can keep a hybrid host ticking;
- services such as profiler capture can remain active after their panel becomes inactive.

Therefore `IsVisible` alone is not enough unless every kind of UI-owned work participates in the
same lifecycle.

## Visibility Model

Keep `UIElement` as a class with stable retained identity. Replace the overloaded visibility boolean
with an explicit state:

```csharp
public enum UIVisibility
{
    Visible,
    Hidden,
    Collapsed
}
```

The required behavior is:

| State | Layout | Paint | UI updates | Hit testing |
|---|---:|---:|---:|---:|
| `Visible` | Yes | Yes | Yes | Controlled by `IsHitTestVisible` |
| `Hidden` | Yes | No | No | No |
| `Collapsed` | No | No | No | No |

`IsEnabled` remains independent. It controls whether an otherwise visible and hit-testable control
responds to interaction. `IsHitTestVisible` remains independent for passive visual elements.

DockHost must set inactive pages to `Collapsed`; the selected page must be `Visible`.

## Effective State

`UIElement` will expose allocation-free effective-state queries that walk retained ancestors:

- effective layout participation;
- effective rendering visibility;
- effective hit-test eligibility;
- effective enabled state.

Layout, paint, input routing, accessibility, UI ticking, and viewport presentation must use these
shared rules rather than implementing separate ancestor checks.

## Update Ownership

Panel-owned recurring work must run through `UIHost`, not through unconditional calls in the Editor
game loop.

The host update sequence will be:

```text
dispatch queued work
    -> update active UI subtrees
    -> resolve dirty bindings
    -> layout if dirty
    -> paint if dirty
    -> submit if changed
```

Rules:

1. A collapsed or hidden subtree does not receive ordinary UI updates.
2. Dirty state is retained while inactive and synchronized once when the subtree becomes visible.
3. An inactive subtree does not contribute continuous-update demand to hybrid scheduling.
4. Components that must prepare while not painted need an explicit host-owned service. A tooltip
   delay, for example, belongs to the overlay/tick manager and is not a general exception allowing
   hidden panel trees to update.
5. Application code must not call component refresh methods every frame merely to discover whether
   values changed.

## External Rendering and Services

Work owned by a UI panel but performed outside `Engine.UI` must follow effective visibility:

- `ViewportPanel` submits Scene/Game FBO work only while effectively visible.
- A hidden viewport keeps its render invalidation pending; activation renders the latest state once.
- Profiler capture runs only while the Profiler page is visible and recording is enabled.
- Inspector binding synchronization runs only while the Inspector page is active.
- Detached windows apply the same rules through their independent `UIHost`.

Game simulation is not panel-owned work. Hiding the Game viewport may stop its FBO presentation
without automatically pausing gameplay.

## Migration Steps

1. Add `UIVisibility` and effective-state helpers while retaining `IsVisible` as a temporary
   compatibility adapter.
2. Update measure, arrange, paint, input, focus, accessibility, and DockHost selection to use the
   new contract.
3. Make UIHost ticking skip inactive subtrees and remove their hybrid scheduling demand.
4. Move delayed hidden-control behavior, beginning with tooltips, into explicit host/overlay timing
   ownership.
5. Gate viewport rendering and profiler capture by effective visibility.
6. Remove unconditional panel refresh calls from `Editor/Program.cs` after the value-update design
   below is settled.
7. Remove the compatibility `IsVisible` API after all call sites migrate.

## Required Tests

- Dock tab selection collapses the old page and activates exactly one new page.
- Collapsed content has zero desired layout size and emits no paint commands.
- Hidden content retains layout space but emits no paint commands.
- Neither hidden nor collapsed content receives hit tests or ordinary UI ticks.
- Inactive animated content does not keep hybrid scheduling continuous.
- A hidden viewport submits no FBO work and retains pending invalidation until activation.
- Inspector bindings and profiler capture perform no work while their pages are inactive.
- Detached and main-window DockHosts have identical lifecycle behavior.
- Hot-path effective-state checks allocate zero managed memory.

## Model Value Updates

Script fields use one opt-in declaration:

```csharp
[Observe(Editor)]
public partial float Speed { get; set; } = 5f;
```

The containing script class is also partial. A Roslyn source generator supplies the partial
property body, equality guard, stable property identifier, typed metadata, allocation-free value
accessors, and change notification. There is no separate `ShowInInspector` or `ReadOnly`
attribute. `Editor` scope means the property is listed and editable in the Inspector; `Runtime`
scope enables runtime observers, and both flags can be combined.

Only annotated properties pay notification cost. Ordinary node and script fields remain plain C#.
The Inspector subscribes only to the selected object's generated notifications. An inactive
Inspector retains a dirty bit and resolves current values once when reactivated, rather than
refreshing every binding each frame. UI-thread delivery is marshalled through `UIDispatcher` when
a script change originates elsewhere. Focused controls retain uncommitted input until commit or
focus loss, then reconcile with the latest model value.

Generated descriptors are also the schema for scene serialization and Inspector component
sections. `Node` owns an ordered component collection; `ScriptComponent` stores a script asset,
enabled state, and typed property overrides keyed by generated stable identifiers. Multiple scripts
can be attached to one node. Scene format 4 persists the component collection while format 3 loads
through the legacy single-`ScriptId` migration path. Play-mode cloning copies components and
overrides without sharing mutable state.

The Editor maintains a background compiled schema host so generated fields are available before
Play. During Play, the same Inspector fields bind to live script instances. User commits are written
to the component override and subsequently applied through generated setters when a runtime is
created. External generated changes update only their bound field. Core node, component, and
material changes use coarse events, and the unconditional `SceneInspector.RefreshValues()` call has
been removed from the Editor loop.
