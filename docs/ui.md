# Retained UI system

`Engine.UI` is a renderer-independent retained UI toolkit shared by Editor windows and Player interfaces. It depends on graphics abstractions for input, draw commands, textures, and text layout; only `Engine.Graphics.Silk` translates those contracts to native events and Vulkan work.

## Element model

`UIElement` owns hierarchy, measure/arrange state, retained painting, clipping, opacity, resources, animation, visibility, enabled state, and hit-testing policy.

- `IsVisible = false` removes the entire subtree from layout, painting, hit testing, and retained time updates.
- `IsEnabled = false` keeps layout and painting but suppresses interaction for the inherited subtree.
- `IsHitTestVisible = false` keeps layout and painting but removes the element itself as an input target.
- Backgrounds are transparent by default. Assigning `BackgroundColor` enables background painting; callers should not add opaque defaults merely to make a component visible.

`Box` adds background, border, and corner-radius presentation. Controls that need a visual rectangle should derive from `Box` or an existing box-derived control. Single-child controls use `ContentControl`.

## Layout

Available containers include `Canvas`, `StackPanel`, `FlexPanel`, `Grid`, and `OverlayPanel`.

- Prefer `FlexPanel` for ordinary rows, columns, wrapping, growth, shrinking, and content-sized layout.
- Use `Grid` when multiple rows or columns must share track boundaries.
- Use `Canvas` for explicit coordinates such as overlays and viewport tools.
- Wrap overflowing content in `ScrollViewer`; controls such as `TreeView` do not own scrolling themselves.
- Use `Margin`, `Padding`, minimum/maximum dimensions, and flex properties instead of control-specific spacing logic.

Layout, paint, input, scrolling, and retained-time loops are hot paths. Avoid LINQ and interface-typed enumeration in those paths.

## Input and focus

`UIEventRouter` performs clipped hit testing and stable preview/target/bubble routing for pointer, wheel, keyboard, text, composition, navigation, drag/drop, and command events. It owns pointer capture, focus traversal, modal scopes, and mutation-safe dispatch snapshots.

Interactive child labels and icons should set `IsHitTestVisible = false`; behavior remains on the containing control. Controls should use routed commands and semantic events rather than application code inspecting router internals.

## Host scheduling

Each native window owns one `UIHost` and `UIDispatcher`.

| Mode | Ownership |
|---|---|
| `ExternallyManaged` | A game/application loop explicitly calls update and refresh. |
| `EventDriven` | Input or invalidation requests frames; the host sleeps while idle. |
| `Continuous` | The host advances every frame. |
| `Hybrid` | Event-driven while idle, continuous while retained timers or interactions are active. |

Hybrid timing is required for key repeat, progress animation, caret blinking, tooltips, toasts, submenu delays, drag/drop, and similar features. Components invalidate retained state; application code should not poll every component each frame to discover changes.

## Docking

`DockWorkspace` is the authoritative model. `DockSession` coordinates main and floating `DockHost` instances, native window lifecycle, cross-window drops, persistence, and reconciliation.

Docked panel content is registered without nested panel chrome because the dock tab is already its title and drag surface. Each tab page is wrapped by `ScrollViewer`. Inactive content receives `IsVisible = false` and must perform no painting or retained update work.

Current dock metrics are:

- host margin: 4 px;
- splitter thickness: 4 px;
- panel corner radius: `UITheme.PanelCornerRadius` (6 px by default);
- tab/control height: `UITheme.ControlHeight` (30 px by default).

## Styling standards

Use shared `UITheme` tokens and typed styles instead of component-local colors or behavior changes.

| Token | Default | Purpose |
|---|---:|---|
| `Surface` | `#191A1C` | Panel, selected-tab, and tab-content background |
| `PanelCornerRadius` | `6` | Docked panel corners |
| `PanelHeaderHeight` | `32` | Standalone `SectionHeader` height |
| `PanelTitleFontSize` | `16` | Standalone panel-title typography |
| `PanelHeaderPadding` | `10` | Standalone title inset |
| `ControlHeight` | `30` | Standard controls and dock tabs |
| `ItemRowHeight` | `30` | Hierarchy and filesystem rows |
| `ItemRowPadding` | `5` | Row horizontal inset |
| `TreeIndent` | `14` | Additional inset per tree depth |

Use `SectionHeader` when standalone content needs its own title. Dock tabs use content-sized flex headers within 95% of the pane width, leaving the content's rounded top-right corner exposed. Header-style toggle buttons remain transparent when idle and use shared hover and pressed surface colors.

## Rendering and accessibility

`UIDrawList` contains semantic commands for solid/rounded shapes, strokes, text, images, and viewport textures. The Silk backend performs clipping, batching, glyph shaping/rasterization, and texture binding.

The retained tree exposes semantic snapshots and actions for accessibility adapters. New interactive controls must provide an appropriate role, label, state/value data, and semantic actions.

## Performance verification

Allocation-sensitive UI tests cover cached painting, unchanged pointer routing, large list/tree virtualization, scrolling, text layout, and host timing. The executable benchmark suite is:

```bash
dotnet run --project tools/Engine.UI.Benchmarks -c Release
```

The named baseline is stored in `docs/ui-performance-baseline.json`.
