# Retained UI system

`Engine.UI` is a renderer-independent retained UI toolkit shared by Editor windows and Player interfaces. It depends on graphics abstractions for input, draw commands, textures, and text layout; only `Engine.Graphics.Silk` translates those contracts to native events and Vulkan work.

## Element model

`UIElement` owns hierarchy, measure/arrange state, retained painting, clipping, opacity, resources, styles, animation, visibility, enabled state, and hit-testing policy.

- `IsVisible = false` removes the entire subtree from layout, painting, hit testing, and retained time updates.
- `IsEnabled = false` keeps layout and painting but suppresses interaction for the inherited subtree.
- `IsHitTestVisible = false` keeps layout and painting but removes the element itself as an input target.
- Backgrounds are transparent by default. Assigning `BackgroundColor` enables background painting; callers should not add opaque defaults merely to make a component visible.
- `Padding` defines one content rectangle used by measurement, arrangement, and painting. `PaintContent` is clipped to that rectangle, so individual focus or interaction states must not recalculate their own content clip.
- `Style` and `StyleKey` are available on every element. Styling a non-`Control` visual such as `Label` does not require a separate code path.

`Box` adds rounded-background and corner-selection presentation. `Surface` adds border presentation. Controls that need a visual rectangle should derive from the narrowest suitable type or an existing box-derived control. Single-child controls use `ContentControl`.

`TextElement` owns the shared `Text`, `FontSize`, and `TextStyle` lifecycle for `Label` and `TextBlock`. Text content, icons, and images paint through `PaintContent`; control chrome remains in `Paint`. This separation keeps content padding and clipping identical across visual states.

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

Use `PointerCaptureGesture` for ordinary primary-button hold and drag interactions. It owns capture, release, capture-loss completion, local positions, and deltas. Direct capture remains appropriate for interactions with additional policy, such as text selection and conditional tree-column resizing.

## Shared control foundations

Common behavior belongs in the foundation type rather than in each component or visual state:

| Foundation | Responsibility | Consumers |
|---|---|---|
| `UIInteractionState` / `UIInteractionColors` | Resolve enabled, hover, press, and selected colors with one priority | buttons, item rows, thumbs, toggles |
| `SelectableButton` | Persistent selected state and visual invalidation | list rows, tree rows, toggle buttons |
| `UISelectionModel` | Bounded single, multiple, toggle, and range selection | item controls, lists, combo boxes, tabs |
| `RangeBase<T>` | Bounds, clamping, value invalidation, and notification | numeric fields, sliders, scrollbars, progress bars |
| `TextElement` / `UITextStyle` | Text storage, metrics, typography, and invalidation | labels and text blocks |
| `PointerCaptureGesture` | Primary-pointer capture lifecycle | thumbs, repeat buttons, color-picker surfaces |

Popup placement is resolved by one edge-aware algorithm shared by ordinary overlays and nested context menus. Grapheme-safe fitting and ellipsis are likewise shared by text blocks and column text. Do not add component-local versions of these algorithms.

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

Current dock metrics are theme-owned:

- host margin: 4 px;
- splitter thickness: `UITheme.DockSplitterThickness` (4 px by default);
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
| `ControlCornerRadius` | `5` | Standard interactive-control corners |
| `ControlHorizontalPadding` | `7` | Standard interactive-control content inset |
| `TextContentPadding` | `4` | Text-bearing control content inset |
| `ItemRowHeight` | `30` | Hierarchy and filesystem rows |
| `ItemRowPadding` | `5` | Row horizontal inset |
| `TreeIndent` | `14` | Additional inset per tree depth |
| `ScrollBarThickness` | `10` | Scrollbar cross-axis thickness |
| `SliderThumbSize` | `14` | Slider thumb length along its track |
| `MenuItemHeight` | `26` | Context-menu row height |
| `MenuSeparatorHeight` | `9` | Context-menu separator height |
| `DockSplitterThickness` | `4` | Draggable dock splitter thickness |

Use semantic `UITextRole` values through `UITheme.GetTextStyle(...)` rather than pairing theme font sizes and colors at each call site. Use `SectionHeader` when standalone content needs its own title. Dock tabs use content-sized flex headers within 95% of the pane width, leaving the content's rounded top-right corner exposed. Header-style toggle buttons remain transparent when idle and use shared interaction colors.

## Scene HUDs

Add one `HudRoot` to a scene through **Hierarchy → Add UI → Add HUD Root**. A scene may contain only one HUD root. It is persisted and cloned with the scene, but its `Content` is a runtime retained tree rather than a 3D child hierarchy.

Attach a scene script to the HUD root and assign declarative content during `OnReady`:

```csharp
public override void OnReady()
{
    if (Owner is not HudRoot hud)
        throw new InvalidOperationException("This script requires a HUD root.");

    var score = new Label("Score: 0")
    {
        Margin = new Thickness(16f),
        Padding = new Thickness(10f, 0f, 0f, 0f),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        TextStyle = UITheme.Dark.GetTextStyle(UITextRole.Body),
        IsHitTestVisible = false
    };
    var root = UI.Overlay([score]);
    root.IsHitTestVisible = false;
    hud.Content = root;
}
```

The Editor mounts this content above the Game viewport during play, including when the viewport moves to another native window. Player creates a `UIHost` for the same content and does not install its fallback interface when a scene HUD exists. HUD layout always matches the logical game viewport, uses top-left-origin screen coordinates, clips to the viewport, and renders after its 3D texture. Non-interactive full-screen roots should set `IsHitTestVisible = false`; interactive descendants such as buttons remain independently hittable.

## Rendering and accessibility

`UIDrawList` contains semantic commands for solid/rounded shapes, strokes, text, images, and viewport textures. The Silk backend performs clipping, batching, glyph shaping/rasterization, and texture binding. Fonts use on-demand glyph atlases. Windows prefers native DirectWrite hinted RGB subpixel coverage and falls back to the cross-platform TrueType rasterizer; other platforms use the cross-platform path. Coverage format is explicit so Windows subpixel filtering does not alter other platforms.

The retained tree exposes semantic snapshots and actions for accessibility adapters. New interactive controls must provide an appropriate role, label, state/value data, and semantic actions.

## Performance verification

Allocation-sensitive UI tests cover cached painting, unchanged pointer routing, large list/tree virtualization, scrolling, text layout, and host timing. The executable benchmark suite is:

```bash
dotnet run --project tools/Engine.UI.Benchmarks -c Release
```

The named baseline is stored in `docs/ui-performance-baseline.json`.
