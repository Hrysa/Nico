# Complete UI System Implementation Plan

## Goal

Build a renderer-independent retained-mode UI system suitable for both the Editor and Player. The
system must provide predictable layout, routed input, keyboard and text editing, overlays, scalable
collections, styling, docking, and testable accessibility semantics without introducing Silk.NET
outside `Engine.Graphics.Silk`.

“Complete” means the Editor can be built entirely from reusable `Engine.UI` services and controls,
without input dispatch, focus management, popup management, drag handling, or ad-hoc widget logic
in `Editor/Program.cs`.

The same controls and retained tree must support event-driven Editor chrome and continuously updated
game HUDs. Runtime scheduling is a host policy; controls must never branch on an Editor/game mode.

## Current Baseline

The repository already has a useful retained-mode foundation:

- `UIElement` owns measure/arrange, retained painting, visibility, hit testing, and basic state.
- `Canvas`, `StackPanel`, and `Grid` provide initial layout containers.
- `Button`, `Label`, `TextField`, `ListView`, `TreeView`, dialogs, menus, and tool panels provide
  initial controls.
- `UIDrawList` is a renderer-independent semantic command stream.
- `Engine.UI.UIHost` connects one native window, renderer, input source, dispatcher, and UI tree.
- `Engine.UI.UIEventRouter` provides simple topmost hit testing, hover, focus, press, and click dispatch.
- `UIDispatcher` marshals worker callbacks to the owning UI thread and rejects cross-thread host access.

The largest limitations are architectural rather than the number of widgets:

- Pointer events do not include position, button, modifiers, timestamp, click count, or handled state.
- There is no routed event model, pointer capture, focus scopes, tab navigation, or command system.
- Keyboard/text support lacks selection, clipboard, IME/composition, undo, and platform shortcuts.
- Layout lacks clipping, scrolling primitives, transforms, shared sizing, and virtualization contracts.
- Rendering lacks clip commands, images/icons, strokes, opacity, and explicit stacking contexts.
- Popups, menus, tooltips, modal focus, and drag/drop are coordinated manually by the Editor.
- Control state and styling are embedded in individual controls rather than resolved consistently.
- Several layout, paint, and text paths allocate and need explicit hot-path budgets.

## Architectural Boundaries

The dependency direction remains:

```text
Engine.Core
    ↑
Engine.Graphics       (input and renderer-independent draw contracts)
    ↑
Engine.UI             (UI tree, layout, input routing, controls, UI host)
    ↑
Editor / Engine       (composition and application behavior)

Engine.Graphics.Silk  (native input, clipboard/IME adapter, Vulkan UI renderer)
```

Rules:

1. `Engine.UI` never references Silk.NET or Vulkan types.
2. Native services are expressed as small interfaces in `Engine.Graphics` or `Engine.UI`.
   Interfaces implemented by `Engine.Graphics.Silk` live in `Engine.Graphics` so the Silk project does
   not acquire a reverse dependency on `Engine.UI`; purely UI-owned services may live in `Engine.UI`.
3. Editor behavior uses public UI commands and events rather than reaching into router state.
4. Input, layout, update, and paint hot paths use indexed loops or concrete collections and avoid
   LINQ, iterator interfaces, transient delegates, and per-frame string construction.
5. Every retained cache has explicit invalidation ownership and a regression test.

## Target Architecture

### UI application and host

Move `UIHost` and `UIEventRouter` from `Editor` into `Engine.UI` and split responsibilities:

- `UIHost`: window-sized root, input subscription, layout scheduling, paint submission, DPI scale.
- `UIInputManager`: hit testing, routed event dispatch, pointer state, pointer capture.
- `UIFocusManager`: keyboard focus, focus scopes, tab order, focus restoration.
- `UIOverlayManager`: popups, menus, tooltips, drag visuals, modal layers.
- `UIDragDropManager`: drag threshold, capture, effects, enter/over/leave/drop routing.
- `UICommandManager`: commands, key gestures, enabled state, routed execution.
- `UITickManager`: caret blinking, tooltip delays, animation clocks, deferred callbacks.

Each native window owns one `UIHost` and therefore one independent set of these managers.

### Threading and dispatch

Each `UIHost` has exactly one owning UI thread. Tree mutation, input routing, command routing, focus
changes, layout, animation application, paint generation, and overlay changes occur only on that
thread. Debug builds reject cross-thread access with a host/thread assertion.

Worker completion, file watching, timers, compilation progress, and asset loading post work through a
host-owned `UIDispatcher`. The dispatcher provides immediate, queued, and next-frame priorities,
cancellation, and shutdown rejection. Posted work is drained at a deterministic point before update
and layout; it never mutates the tree concurrently with input or paint traversal.

Native event-loop wakes may originate on any thread, but they only schedule dispatcher or frame work.
They do not invoke controls directly. Host disposal cancels pending callbacks and prevents later timer
or worker completion from retaining or re-entering the UI tree.

### Editor and game execution modes

`UIHost` supports three scheduling policies without changing control behavior:

```csharp
public enum UIUpdateMode
{
    EventDriven,
    Continuous,
    Hybrid
}
```

- `EventDriven` is intended for idle tools and utility windows. Input, model changes, timers, or
  invalidation request a frame; the host sleeps when no work remains.
- `Continuous` is intended for Player HUDs and menus hosted by the game loop. The host receives an
  update every frame but performs layout and paint only when dirty.
- `Hybrid` is the normal Editor policy. It is event-driven while idle and temporarily continuous
  while a viewport, drag, profiler capture, progress animation, caret, or transition requires ticks.

The host lifecycle is logically separated:

```text
Update(delta) → LayoutIfDirty() → PaintIfDirty() → SubmitIfChanged()
```

Calling `Update` every game frame must not rebuild layout or draw commands every frame. Controls
invalidate measure, arrange, or paint independently. Active animations register with `UITickManager`;
when its last animation completes, a hybrid host returns to event-driven sleep.

Editor event-driven integration exposes a frame-request callback. Game integration uses the existing
main loop and does not create a separate UI timer. Timed UI behavior uses unscaled monotonic time by
default so pause menus, tooltips, and transitions continue while game simulation is paused. Controls
may explicitly opt into scaled game time for gameplay-related effects.

The scheduling policy is the only fundamental Editor/game execution difference. Input routing,
focus, capture, layout, styling, popup semantics, accessibility, and draw generation remain shared.

### Runtime HUD and world rendering boundaries

`Engine.UI` supplies HUDs, menus, dialogue, inventory, settings, overlays, and optionally world-space
interface surfaces for both 2D and 3D games:

```text
2D sprites or 3D meshes
        ↓
world-space UI (optional)
        ↓
screen-space HUD and menus
        ↓
present
```

The UI system is not the general 2D scene renderer. Sprites, tilemaps, particles, 2D animation,
lighting, camera culling, and depth/layer sorting belong to a dedicated renderer. Game entities should
not pay for measure/arrange, focus, styling, or accessibility merely because they are two-dimensional.

Runtime hosts support:

- screen-space roots independent of the world camera;
- safe-area insets, aspect-ratio changes, anchors, and user-configurable UI scale;
- pixel-perfect scaling as an explicit policy for pixel-art games;
- world-space roots transformed through a 2D or 3D camera for nameplates, health bars, prompts, and
  speech bubbles;
- configurable render ordering relative to world rendering and post-processing;
- gameplay and UI input contexts, including an explicit policy for whether gameplay input continues
  beneath non-modal HUDs;
- mouse, keyboard, gamepad, controller navigation, and touch-ready pointer IDs;
- unscaled UI animation while simulation is paused;
- frequently changing HUD values that invalidate only their visual subtree;
- retained GPU geometry reuse for static HUD regions.

`Player` continues to depend on the `Engine` facade. `Engine` exposes UI composition and host services
without exposing `Engine.Graphics.Silk`:

```text
Player → Engine facade → Engine.UI + Engine.Graphics abstractions
```

World-space UI begins as ordinary UI layout rendered into a transformed draw surface. It must not
silently inherit screen-space assumptions for clipping, hit testing, DPI, or pointer coordinates.

### Routed input

Replace parameterless mouse events with reusable event-data objects and three routing phases:

```text
root → target       Preview (tunnel)
target              Target
target → root       Bubble
```

Core event data:

- `PointerEvent`: pointer ID, logical position, local position, delta, device kind, modifiers.
- `PointerButtonEvent`: button, click count, pressed buttons.
- `PointerWheelEvent`: horizontal and vertical deltas.
- `KeyEvent`: physical key, modifiers, repeat state.
- `TextInputEvent`: committed Unicode text.
- `TextCompositionEvent`: IME pre-edit text, selection, commit/cancel state.
- `FocusEvent`, `DragEvent`, and `CommandEvent`.

All routed events expose `Handled`. Event objects are reused per dispatch where safe. Existing events
remain as compatibility adapters for one migration phase and are then removed.

Dispatch is synchronous. A handler must not retain event-data references after it returns. Each event
builds a stable target-to-root route before invoking handlers, so removing, reparenting, disabling, or
hiding elements during dispatch cannot redirect the current event. Removed elements later in the
snapshot are skipped. Nested dispatch uses a separate event-data instance or stack slot; one shared
mutable event object is never reused recursively.

The native input migration expands `IInputSource` or replaces it with versioned device-neutral input
contracts that provide modifiers, pointer IDs, pressed buttons, horizontal and vertical wheel deltas,
repeat state, text composition, touch, pen, and gamepad data. Compatibility adapters preserve the
existing mouse/key events while Editor call sites migrate. Silk.NET mappings and platform clipboard,
cursor, and IME adapters remain in `Engine.Graphics.Silk`.

Pointer capture must support:

- implicit capture during button press;
- explicit capture for sliders, splitters, selection rectangles, and viewport tools;
- capture loss when controls are hidden, removed, disabled, or their host closes;
- multiple pointer IDs in the data model, even if the first backend only emits a mouse pointer.

### Focus, navigation, and commands

Add these `UIElement` properties:

- `IsEnabled`, `Focusable`, `IsTabStop`, `TabIndex`;
- `IsKeyboardFocused`, `IsKeyboardFocusWithin`;
- `Cursor`, `ToolTip`, `AutomationId`;
- `ClipToBounds`, `Opacity`, `ZIndex`;
- `DataContext` only after the binding phase.

Focus behavior:

- Tab and Shift+Tab traverse the active focus scope.
- Arrow navigation is available for menus, lists, trees, tabs, radio groups, and toolbars.
- Modal dialogs establish a focus scope and restore prior focus when closed.
- Default and cancel buttons respond to Enter and Escape.
- Detached windows maintain independent focus without sharing router globals.
- The host tracks the most recent input modality so keyboard/gamepad focus indicators are visible
  without forcing mouse-focused controls to display the same treatment.

Commands replace scattered global key checks. Initial commands include save, undo, redo, copy, cut,
paste, select all, delete, rename, open, close, accept, and cancel. Key gestures are configurable and
resolved from focused control through its ancestors to the host.

`UICommandManager` belongs to `UIHost`, not the root panel. The root is a visual/layout element and
may participate in routed command handling, but it does not own application input policy. Host
ownership keeps shortcut behavior independent of root-panel type and gives detached windows isolated
focus, modal, and binding state.

Shortcut resolution order is deterministic:

```text
native key event
    ↓
focused control binding
    ↓
focused control ancestors
    ↓
active focus scope or modal
    ↓
active viewport/tool scope
    ↓
host/window bindings
    ↓
active-host application bindings
```

The first enabled binding that handles the command stops routing. Text editing bindings such as copy,
cut, paste, undo, redo, word navigation, and select all take precedence when an editable control has
focus. Global bindings must not inspect concrete control types to enforce this rule.

Bindings have explicit scopes:

- control scope for editing and control-local actions;
- focus-scope/modal scope for accept, cancel, and dialog commands;
- viewport/tool scope for scene navigation and active editor tools;
- window scope for tabs, panels, and window operations;
- application scope for save, quit, preferences, and project actions;
- gameplay, HUD, and menu scopes for runtime input-context arbitration.

Each command exposes `CanExecute`, `Execute`, stable identity, display text, and zero or more default
gestures. User bindings are stored separately from defaults, support platform-specific modifiers, and
are validated for conflicts within overlapping scopes. Conflict reporting names both commands and
allows replace, unbind, or cancel behavior.

An active modal suppresses underlying window, viewport, and control scopes while allowing only its
own bindings and explicitly permitted application commands. Opening and closing a modal restores the
previous active scope and focus. For multiple native windows, each host owns window bindings; only the
active host participates in application-command resolution unless a command is explicitly registered
as process-global.

The minimal routed command type, handler lookup, and built-in editing commands are part of the input
foundation in Phase 1. Configurable gestures, user rebinding, conflict resolution, and application
command migration are completed in Phase 6.

### Layout and visual tree

Retain the existing measure/arrange model and add:

- `Visibility` with Visible, Hidden, and Collapsed states;
- layout rounding and DPI-aware logical/device coordinate conversion;
- `MinWidth`, `MaxWidth`, margins, padding, and alignments with complete constraint tests;
- `Border`, corner radius, and per-side thickness;
- clip rectangles propagated through hit testing and painting;
- `RenderTransform` for visual-only translation/scale and inverse-transformed hit testing;
- `ZIndex` and stable sibling ordering;
- desired-size invalidation separated from arrange and paint invalidation;
- diagnostic layout assertions for invalid sizes, cycles, and mutation during traversal.

Containers to add or complete:

- `DockPanel` for editor chrome;
- `WrapPanel` for tag/icon collections;
- `ScrollViewer` with reusable scroll state and clipping;
- `VirtualizingStackPanel` for large lists and trees;
- `Splitter`/`GridSplitter` with pointer capture and minimum constraints;
- `ViewBox` only if scale-to-fit content is required by Player UI.

`Grid` gains span support, min/max tracks, shared-size groups, and deterministic star sizing.

### Rendering contract

Extend `UIDrawList` with semantic commands needed by general controls:

- push/pop clip rectangle;
- filled and stroked rectangles with corner radii;
- lines and polylines;
- image/icon commands with source rectangle and tint;
- glyph-run commands rather than only whole strings;
- opacity groups or opacity carried by each command;
- stable content/overlay/debug layers.

The Vulkan implementation performs clip batching with scissor rectangles and batches compatible
geometry without changing UI ordering. Text shaping and glyph lookup are cached by font, size, text,
and culture. Paint caches must not retain stale clip or DPI state.

Do not add general vector paths until a concrete Editor feature requires them. Icons should first use
an atlas or signed-distance-field glyph set.

### Styling and resources

Evolve `UITheme` into immutable design tokens plus control styles:

- colors, typography, spacing, corner radii, border widths, control heights;
- state-specific values for normal, hovered, pressed, focused, selected, and disabled;
- semantic tokens such as danger, warning, success, accent, surface, and selection;
- per-host theme replacement with subtree invalidation;
- DPI-aware font and spacing scale.

Controls expose logical properties; styles determine presentation. Avoid a full CSS selector engine.
Use typed styles such as `ButtonStyle`, `TextFieldStyle`, and `TreeViewStyle`, resolved from a
`UIResourceDictionary` by type and optional style key.

### Binding, templates, and validation

Use a small, explicit binding system rather than a WPF-scale dependency-property engine. Binding is
opt-in and complements direct property assignment; ordinary game HUD code does not need `DataContext`.

The binding model supports:

- one-way, two-way, and one-time bindings;
- `INotifyPropertyChanged`-style source notifications and observable collection changes;
- typed or precompiled property access where possible, avoiding reflection in steady-state updates;
- converters, fallback values, null handling, and explicit update triggers;
- validation results with error message, severity, and command `CanExecute` integration;
- deterministic detachment when target, source, host, or generated item container is removed;
- dispatcher marshalling for source notifications raised by worker threads.

`DataContext` inherits through the logical tree, not renderer-created visual children. `ControlTemplate`
defines a control's visual tree, while `DataTemplate` creates item presentation. Templates are typed
factories compiled to delegates; the first release does not include markup parsing or runtime string
expression evaluation. Template-generated visual children retain a logical owner for resources,
bindings, focus, commands, and accessibility.

Validation presentation is styleable and accessible. Text and numeric controls expose current value,
pending edit text, parse/validation state, and commit/cancel behavior without application code
inspecting concrete visual children.

### Text system

Split text display, measurement, shaping, and editing:

- `ITextShaper` produces cached glyph runs and caret positions.
- `TextBlock` supports wrapping, trimming, alignment, and selectable text when requested.
- `TextBox` supports multiline editing; `TextField` becomes the single-line specialization.
- editing uses Unicode text elements/graphemes rather than moving by UTF-16 code unit;
- selection, mouse placement, drag selection, word movement, word deletion, Home/End variants;
- clipboard operations through `IClipboardService`;
- undo/redo coalescing and configurable maximum history;
- IME composition through an optional `ITextInputMethod` host service;
- password entry avoids exposing rendered text through ordinary text properties.

The text editor should use a gap buffer or piece table only when profiling shows ordinary strings are
insufficient. Correctness and IME support come first.

### Animation and localization

`UITickManager` owns active animations and schedules hybrid-host frames only while work exists.
Animations have explicit owner, clock, duration, easing, cancellation, fill behavior, and completion.
Paint-only properties such as opacity and transform do not invalidate layout; size and margin
animations explicitly opt into layout invalidation. Replacing or removing an owner cancels its
animations. Steady-state animation ticking allocates nothing.

Animations choose scaled or unscaled time. UI interaction, caret, tooltip, modal, and pause-menu
animation defaults to unscaled time. Reduced-motion policy disables or shortens nonessential motion
without changing final state or command behavior.

Localization provides resource lookup by stable key, culture fallback, and per-host culture changes.
A culture change invalidates affected text measurement and layout once. Text shaping supports font
fallback, bidirectional runs, and right-to-left text. Layout direction is inherited and controls define
whether icons, navigation order, padding, and horizontal alignment mirror in RTL mode. Numeric and
date controls parse and format with their configured culture rather than the process default.

Localization tests cover translated text expansion, mixed-direction text, grapheme navigation, font
fallback, locale-specific numbers, and runtime culture changes.

### Ownership and lifetime

The visual/logical parent owns child lifetime, while application models remain externally owned.
Removing, collapsing, disabling, reparenting, or transferring an element triggers well-defined cleanup
of focus, pointer capture, drag state, commands, bindings, animations, popups, and accessibility links.

Host managers use explicit registrations with disposable tokens or weak ownership where appropriate;
ordinary event subscriptions must not keep removed subtrees alive. Closing a host cancels dispatcher
work, timers, composition, animation, and pending popup operations before releasing renderer caches.
Text, glyph, image, and geometry caches have bounded policies and device-loss recreation paths.
Detached-window transfer is transactional: an element cannot belong to two hosts, and host-specific
state is cleared before attachment to the destination.

### Control set

Build controls only after routed input, focus, clipping, and styling are stable.

Foundation:

- `Border`, `Image`, `Icon`, `TextBlock`, `ContentPresenter`, `ItemsPresenter`;
- `Control`, `ContentControl`, `ItemsControl`, and `Selector` base classes;
- `ScrollBar`, `ScrollViewer`, `Thumb`, `RepeatButton`.

Input controls:

- `CheckBox`, `RadioButton`, `ToggleButton`;
- `Slider`, numeric field/spinner, progress bar;
- `ComboBox`, editable combo box;
- single-line `TextField` and multiline `TextBox`.

Collections and navigation:

- virtualized `ListView` and `TreeView` with multi-selection;
- `TableView`/data grid with resizable and sortable columns;
- `TabControl`, breadcrumb, toolbar, menu bar;
- keyboard type-ahead search and selection anchors.

Overlays:

- tooltip with delay and screen-edge placement;
- popup with placement strategies, dismissal policy, monitor work-area clamping, and DPI-boundary
  conversion;
- context menu and nested menu navigation;
- modal dialog service;
- toast/notification host;
- drag preview and drop indicator.

Editor-specific controls remain in `Editor` unless reusable:

- asset browser, inspector property rows, scene hierarchy behavior;
- docking workspace and viewport chrome may begin in Editor, then move to `Engine.UI` when their
  contracts stabilize.

### Docking and multi-window behavior

Implement docking after popups, capture, focus scopes, splitters, and drag/drop are complete:

- dock tree containing split, tab, and leaf nodes;
- tab reorder, docking previews, floating tool windows;
- serializable layout with versioned restoration and missing-panel recovery;
- minimum panel sizes and safe collapse behavior;
- transfer between hosts without sharing focus/capture state;
- viewport resources remain owned by rendering code, not docking controls.

### Accessibility and automation

Create a renderer-independent semantic tree alongside the visual tree:

- role, accessible name, description, value, state, and supported actions;
- relationships for label/control, parent/child, selection, and expanded state;
- keyboard-only operation for every control;
- high-contrast theme and visible focus indicators;
- automation queries usable in tests before native OS accessibility adapters exist.

Native UI Automation and macOS accessibility adapters are a final platform phase, not a prerequisite
for the semantic model.

## First Stable Release Non-goals

The initial stable UI release intentionally excludes:

- CSS selectors, runtime stylesheet parsing, and a general dependency-property system;
- markup languages, runtime expression parsing, and visual UI designers;
- arbitrary vector paths and a complete retained vector-graphics scene;
- using UI elements as the general 2D sprite, tilemap, particle, or world renderer;
- unrestricted cross-thread tree mutation;
- native accessibility parity before the renderer-independent semantic tree is complete;
- advanced world-space UI occlusion, curved surfaces, and physics interaction;
- distributed/process-global input hooks outside the active application window.

Optional sound and haptic feedback use a small host service invoked by semantic control actions. They
do not introduce audio or device dependencies into controls and are not required for the first stable
release.

## Delivery Phases

### Implementation status

- Completed: move `UIHost` and `UIEventRouter` from `Editor` into `Engine.UI`.
- Completed: remove the host dependency on `EditorUI` projection helpers.
- Completed: add host-owned `UIDispatcher`, UI-thread verification, deterministic update draining, and
  shutdown rejection.
- Completed: add compatible versioned input events with logical pointer data, modifiers, held buttons,
  horizontal wheel input, device kind, and device-neutral key transitions.
- Completed: wire Silk.NET and `UIHost` to prefer versioned input while retaining the legacy fallback.
- Completed: add explicit pointer capture, capture-loss notification, and removal/visibility cleanup.
- Completed: add routed pointer preview/target/bubble phases, handled propagation, local coordinates,
  stable target-to-root snapshots, mutation safety, and reentrant dispatch storage.
- Completed: route device-neutral keyboard transitions and committed Unicode text through stable
  preview/target/bubble routes while retaining compatible control callbacks.
- Completed: normalize native held-key transitions as `IsRepeat`, synthesize configurable UI key repeat
  when a backend supplies only down/up, keep event-driven hosts awake only while repeat is pending, and
  allow caret navigation plus Backspace/Delete editing to repeat without repeating ordinary shortcuts.
- Completed: add explicit tab stops, deterministic `TabIndex` ordering, forward/reverse traversal,
  public focus control, and hidden/detached focus cleanup.
- Completed: add inherited enabled-state enforcement across hit testing, routing, capture, focus, and
  sequential navigation.
- Completed: make the topmost visible modal the active pointer, keyboard, focus, and command scope.
- Completed: add minimal target-to-scope routed commands with parameters and `CanExecute` filtering.
- Completed: add exact-modifier key gestures with focused-control-first scoped resolution and repeat
  suppression.
- Completed: migrate select-all and forward/backward deletion into built-in text-editing commands,
  including selection replacement and read-only `CanExecute` behavior.
- Completed: propagate intersected logical clip rectangles through retained draw snapshots and hit
  testing while preserving cached child paint commands.
- Completed: batch UI geometry by clip and apply DPI-scaled, framebuffer-clamped Vulkan scissors;
  restore the full-window scissor before viewport and overlay rendering.
- Completed: add clipped two-axis `ScrollViewer` layout with fractional offsets, clamping, automatic
  bar visibility, and synchronized extent/viewport state.
- Completed: route horizontal/vertical wheel and touchpad deltas with nested limit bubbling and a
  zero-allocation steady-state regression test.
- Completed: add interactive scroll bars whose pointer drag requests capture through routed event
  data and releases it deterministically.
- Completed: extract capture-safe `Thumb` drag behavior and compose scroll-bar thumbs from it.
- Completed: add host-time-driven `RepeatButton` behavior with immediate invocation, configurable
  delay/interval, capture, and deterministic release.
- Completed: add clipped semantic stroked lines and Vulkan quad tessellation as the first richer
  drawing primitive.
- Completed: add horizontal/vertical `Slider` controls with composed thumbs, track presses, captured
  dragging, arbitrary ranges, and routed arrow/Home/End keyboard input.
- Completed: add clipped determinate and host-time-driven indeterminate `ProgressBar` controls.
- Completed: add shared persistent `ToggleButton` state, labeled `CheckBox`, sibling/group-scoped
  `RadioButton`, and track/knob `ToggleSwitch` controls.
- Completed: add invariant-culture `NumericField` composition with bounded parsing, Up/Down stepping,
  and held decrement/increment repeat buttons.
- Completed: add overlay-backed `ComboBox` selection with pointer rows and Up/Down/Space/Escape
  keyboard behavior.
- Completed: add retained-page `TabControl` selection with checked headers and
  Left/Right/Home/End navigation.
- Completed: add horizontal `ToolBar` layout and semantic stroked separators.
- Completed: add owned `Popup` surfaces with topmost outside-press and Escape dismissal in the active
  modal scope; migrate combo and context menus onto the shared policy.
- Completed: add owned `MenuBar`/`ContextMenu` composition with action dismissal.
- Completed: add overlay-canvas `ToolTip` ownership with host-time hover delay, positioning, closing,
  and deterministic detachment.
- Completed: add declarative typed drag sources, movement-threshold initiation, effect-constrained routed
  enter/over/leave/drop/cancel events, pointer capture, and capture-loss recovery.
- Completed: clamp popup positions and oversized popup slots to their overlay canvas bounds while
  retaining an opt-out for deliberately overflowing surfaces.
- Completed: add a host-local overlay manager that follows router drag state with a pointer-offset preview
  and accepted-target drop outline, removing both deterministically after drop or cancellation.
- Completed: add owner-relative below/above/left/right and pointer popup placement, edge-aware directional
  flipping, offsets, final-placement reporting, and overlay-bound clamping.
- Completed: add keyboard menu traversal and wrapping, Enter/Space activation, nested submenu ownership,
  Right/Left navigation, Escape owner-focus restoration, and native Enter-key mapping.
- Completed: add host-local severity-themed toast stacks with deterministic layout, explicit dismissal,
  bounded lifetimes, and automatic or manually forwarded host-time expiration.
- Completed: add culture-aware menu type-ahead with repeated-letter cycling, ampersand mnemonics,
  allocation-free A-Z key mapping, and Alt+letter activation.
- Completed: add actionable notifications with optional callbacks, explicit close buttons, and immediate
  host-local removal after either action.
- Completed: add semantic menu separators and disabled action rows with layout, pointer exclusion, and
  consistent skipping across arrows, type-ahead, mnemonics, and initial submenu focus.
- Completed: bound host-local toast queues with oldest-first eviction and pause/resume notification
  lifetimes while any element in the notification subtree is hovered.
- Completed: add check menu rows with pre-callback toggling and named radio rows with menu-local group
  exclusivity, shared across pointer, Enter/Space, and mnemonic activation paths.
- Completed: add keyed toast deduplication, in-place text/severity updates, retained element identity,
  and optional lifetime replacement/reset for progress-style notifications.
- Completed: add retained right-aligned menu accelerator hints and deterministic platform-neutral
  formatting for modifier/key gestures without mixing hints into searchable labels.
- Completed: add determinate and host-time-animated indeterminate toast progress, normalized clamping,
  keyed in-place updates, and automatic compact/expanded notification sizing.
- Completed: add arbitrary retained menu icon elements with a stable leading icon column that composes
  with check/radio markers, labels, and right-aligned accelerator hints.
- Completed: add configurable host-time submenu hover delay with leave/close cancellation while keeping
  keyboard submenu activation immediate; advance the complete main Editor UI tree once per update.
- Completed: add geometric submenu hover-corridor grace timing that defers accidental sibling switching
  during diagonal travel while cancelling pending switches after entering the open submenu.
- Completed: add clipped row viewports, maximum visible menu height, wheel/programmatic scrolling, and
  automatic scroll-to-focused-row behavior for arrows, type-ahead, and submenu entry.
- Completed: add disabled-aware Home/End/PageUp/PageDown menu navigation with page-sized movement and
  automatic focused-row visibility, including native Page key mapping.
- Completed: place owned submenus against their top-level host bounds, flip from right to left at the
  horizontal edge, clamp vertically, and expose the resolved placement for visuals/tests.
- Completed: restore focus through closed popup ancestry after pointer or keyboard action activation, choosing
  the first still-visible eligible owner across nested menu chains.
- Completed: add a host-pluggable monitor work-area provider, logical work-area placement/clamping, and
  explicit reversible logical/physical DPI conversion contracts for detached and multi-monitor hosts.
- Completed: add renderer-level display and clipboard abstractions, Win32 monitor work-area/DPI resolution,
  native GLFW monitor work-area/content-scale resolution on macOS and Linux, Silk keyboard clipboard access,
  and automatic UIHost/Editor wiring.
- Completed: add routed Copy/Cut/Paste commands with Control/Super gestures, selection-aware eligibility,
  read-only protection, single-line paste sanitization, and host-local clipboard injection for tests.
- Completed: extract shared text editing into multiline `TextBox` with `TextField` as its single-line
  specialization; add Enter insertion, newline-preserving clipboard normalization, logical-line
  Home/End/Up/Down navigation, caret-line scrolling, and retained per-line rendering.
- Completed: add reversible text edit history with routed Control/Super undo/redo gestures, stable
  selection anchors, Shift+arrow/Home/End extension, captured pointer-drag caret hit testing, and
  retained selection highlights across single-line and multiline editors.
- Completed: add Unicode grapheme-boundary Left/Right and forward/backward deletion, Control word
  navigation with Shift extension, double-click word/separator selection, and adjacent typing or
  deletion undo coalescing that breaks on navigation and selection changes.
- Completed: retain a grapheme-based preferred column across repeated vertical movement, add
  triple-click logical-line selection including terminating newlines, and configurable oldest-first
  bounded undo/redo history with a zero-history mode.
- Completed: add visible-page Up/Down navigation with Shift extension, captured pointer selection
  autoscroll with clamped line hit testing, and a `PasswordField` with index-preserving masking,
  configurable mask character, and never/while-focused/always reveal policies.
- Completed: add captured horizontal single-line selection autoscroll, opt-in configurable Tab and
  Shift+Tab multiline indentation without breaking default focus traversal, and reusable validator,
  validation-message/event state with semantic error-border styling.
- Completed: add grapheme-counted maximum input length, Unicode-scalar typing/paste filters, complete
  committed-string handling, allocation-free semantic snapshots with protected password values and
  validation state, and migrate the filesystem-create form onto shared field validation.
- Completed: add focus-baselined edit transactions, routed Enter commit and Escape cancel commands,
  validation-aware command eligibility, dirty/committed/canceled state and events, and migrate
  `NumericField` to pending text with valid Enter/blur commit and invalid blur restoration.
- Completed: add property-changed/commit/lost-focus/explicit model update triggers, explicit validated
  update requests, disposable `UIEditForm` dirty/validation aggregation with routed form commit/cancel
  commands, migrate `NumericField` model updates onto the shared commit trigger, and move Inspector
  name/vector/material bindings onto shared update requests with numeric validation.
- Completed: add reusable form-bound commit/cancel button eligibility with deterministic detachment,
  clear/re-register cached form editors, scope the Inspector's active editable controls, and migrate
  vector/material numeric model writes to validated lost-focus/Enter commit boundaries.
- Completed: add deterministic first-invalid-editor discovery/focus, caller-owned validation-message
  collection, disposable retained validation summaries, and expose the Inspector's active form scope
  for host-chosen Apply/Revert placement without baking shortcut policy into its root panel.
- Completed: add cancellable generation-safe asynchronous validators, pending semantic state,
  pending-aware edit/form command eligibility, stale-result suppression after text mutation, and
  aggregate asynchronous form validation.
- Completed: inherit the host dispatcher through retained UI ancestry and marshal asynchronous
  validation success, failure, cancellation, pending-state cleanup, and notifications onto the owning
  UI thread while safely rejecting completion after host dispatcher disposal.
- Completed: add cancellable automatic asynchronous-validation debounce that wakes event-driven hosts,
  starts validation on the UI dispatcher, collapses rapid edits to the latest generation, and maps
  validator exceptions to a configurable safe failure message.
- Completed: add optional device-neutral input-method source contracts, stable preview/target/bubble
  composition routing, host subscription lifecycle, transient pre-edit/caret state and rendering,
  completion through ordinary input policies, and cancellation/blur cleanup without stored-text mutation.
- Completed: carry clamped candidate/conversion selection ranges through device-neutral and routed
  composition events, expose them on text editors, and render the active range behind transient text.
- Completed: add form-bound Inspector Apply/Revert header actions, keep live name edits outside the
  transaction scope, and batch validated vector/material numeric changes until Enter or Apply while
  Revert restores pending display values without mutating scene models.
- Completed: extract host-inheritable, renderer-independent span-based text measurement and caret hit
  testing, retain approximate startup metrics as the unhosted fallback, and migrate labels and text editors onto the
  shared service without per-measure substrings.
- Completed: place the text layout contract at the graphics abstraction boundary and implement it on
  Silk windows using the same system-font glyph advances and kerning as rasterization, including exact hosted
  caret hit testing without UI-to-Silk dependency inversion.
- Completed: support explicit subtree text-layout overrides with inherited-cache invalidation and wire
  both the legacy main Editor tree and independent detached hosts to renderer-backed font metrics.
- Completed: add retained `TextBlock` display with explicit-line handling, whitespace-aware wrapping,
  character ellipsis, horizontal alignment, configurable line height, and cached unchanged painting.
- Completed: make text-block wrapping and trimming Unicode-grapheme-safe and add bounded line counts
  with final-line ellipsis for constrained labels and notifications.
- Completed: cache bounded renderer-local decoded glyph runs by text and physical font size, retaining
  UTF-16 source indices plus scaled advances and kerning for tessellation and caret placement.
- Completed: add an opt-in retained UI diagnostic overlay for element bounds, effective clips, pointer
  targets, keyboard focus, and pointer capture with router-driven cache invalidation.
- Completed: add semantic renderer-owned image commands, a clipped retained `Image` element, ordered
  textured UI batching, and per-image descriptor binding through the existing texture pipeline.
- Completed: add intrinsic image measurement and centered none/fill/aspect-fit/aspect-crop stretch
  policies, with retained clipping for cropped image content.
- Completed: add reusable texture-backed and resolution-independent symbolic `Icon` content for
  checks, chevrons, close/add/remove/search actions using shared image and stroke primitives.
- Completed: remove LINQ and interface-enumerator allocations from base/grid/stack/title/tool/list/tree
  layout paths, reuse resolved grid track storage, and cover repeated invalid measurement with a hard
  zero-allocation regression.
- Completed: recycle a viewport-sized `ListViewItem` pool across scrolling, selection, and item rebinding,
  with retained container identity, 100,000-item visual bounds, zero-allocation no-op scrolling, and
  bounded text-rebinding allocation guarded by regression tests.
- Completed: reuse the tree's flattened visible-node buffer across scrolling and keyboard navigation,
  eliminating repeated whole-tree list allocation as the prerequisite for tree-row container recycling.
- Completed: suppress tree row rebuilding for boundary/no-op scrolling, preserving visible container
  identity with a zero-allocation boundary-scroll regression while expansion remains refresh-capable.
- Completed: suppress list row rebinding and layout for boundary/no-op wheel input, matching the tree's
  retained-container behavior at scroll limits.
- Completed: recycle a viewport-sized `TreeViewItem` pool across scrolling and hierarchy refreshes,
  retaining row identity and stable input callbacks while rebinding node, depth, expansion, and columns.
- Completed: retain the flattened expanded-tree index between hierarchy mutations so wheel scrolling is
  proportional to visible rows rather than all logical nodes, with a 100,000-node visual-bound regression.
- Completed: retain node-to-logical-row indices with the flattened tree cache, making large-tree keyboard
  selection and parent/child navigation constant-time without predicate enumerator allocation.
- Completed: route tree column fitting and right alignment through the inherited text-layout service,
  using renderer-exact metrics when hosted and span-based fit probes without substring allocation.
- Completed: add multiplicative retained subtree opacity across solid geometry, strokes, glyphs, images,
  and viewport textures using RGBA vertices and source-alpha Vulkan pipeline blending.
- Completed: replace scalar decoding/kerning with cross-platform HarfBuzz OpenType shaping in the Silk
  backend, sharing shaped advances and clusters across measurement, caret hit testing, and rendering.
- Completed: resolve installed platform UI, international, symbol, and emoji faces; split directional
  text into grapheme-safe font runs; and retain face-aware shaping, caching, rasterization, and native
  lifetime ownership without shipping font binaries.
- Completed: add inherited left-to-right/right-to-left flow direction and logical Start/End text
  alignment, preserving explicit physical Left/Right placement for fixed editor layouts.
- Completed: add a renderer-independent dock tree model with tab wells, bounded splits, floating roots,
  duplicate-safe normalization, and fail-closed versioned JSON workspace persistence.
- Completed: materialize dock trees through a retained `DockHost` with registered panel resolution,
  tab selection write-through, recursive split layout, and captured draggable splitter ratios.
- Completed: add dock mutation for tab reordering/moves, close, float, redock, and automatic empty-split
  collapse while preserving stable panel identities and minimum floating-window geometry.
- Completed: persist dock workspaces through adjacent temporary-file replacement and recover from absent,
  corrupt, inaccessible, or incompatible state through caller-owned safe defaults without destroying evidence.
- Completed: add lazy stable panel registration and a `DockSession` that coordinates main/floating hosts,
  preserves content identity across float/redock, reconciles closed windows, and deterministically disposes hosts.
- Completed: add center and four-edge dock drop operations with nested split insertion, bounded new-pane
  shares, reference-safe target replacement, and invalid sole-tab self-drop rejection.
- Completed: add a retained overlay-layer dock target visual with five-zone pointer mapping, active target
  styling, and live center/edge insertion previews sized from the destination bounds.
- Completed: connect dock tab headers to typed routed move drags and make each tab group negotiate,
  preview, cancel, and commit five-zone drops through the workspace model.
- Completed: map header-strip drops to stable tab insertion indices with a retained white insertion
  marker, supporting reorder within a well and indexed transfer between wells.
- Completed: make center and edge dock mutations resolve destination groups across both the main tree
  and floating roots, enabling one authoritative workspace to support cross-window transfers.
- Completed: bind main and floating `DockHost` presentations to the same authoritative workspace and
  reconcile every surviving host/window after a routed dock mutation.
- Completed: define stable Editor panel identifiers, a complete safe default dock tree, and retained
  panel registration for hierarchy, files, Scene, Game, Inspector, and Profiler migration.
- Completed: add project-scoped `.nico/editor-workspace.json` restoration and atomic persistence with
  complete-default fallback that preserves corrupt evidence for diagnosis.
- Completed: add an Editor native dock-window factory over the shared Silk window group with stable-ID
  open/close lifecycle hooks for viewport render-resource transfer and ordinary tool-window reuse.
- Completed: add a tested Editor mount seam that replaces the legacy workspace cell with
  `DockSession.MainHost` while preserving all six retained panel instances and outer chrome.
- Completed: treat an unaccepted drag release as routed cancellation and convert dock-tab releases
  beyond a host into session-owned floating roots with deterministic initial geometry.
- Completed: add stable per-panel floating policy so viewport tabs retain docking/reordering while
  drag-outside creation is gated until their native render-resource lifecycle is registered.
- Completed: mount the live Editor through a restored `DockSession`, disable conflicting legacy detach
  gestures, reconcile native floating tools during updates, and persist the workspace on shutdown.
- Completed: migrate Scene and Game floating to stable-ID lifecycle hooks that transfer Vulkan render
  views and detached renderers, enable viewport drag-outside/restoration, and remove legacy detach paths.
- Completed: place Profiler in the Game tab well by default and add workspace-wide stable-ID tab
  selection queries/activation for dock-aware Editor commands and capture scheduling.
- Completed: add registered panel reopen/activation with stable sibling anchors and fallback tab wells,
  allowing commands such as Profiler to restore panels removed by floating-window closure.
- Completed: restore native floating windows at persisted logical screen positions and synchronize live
  logical position/size back into floating models during session reconciliation and disposal.
- Completed: add host-level close mutation and focused-header Control/Super+W gestures with repeat
  suppression, automatic split collapse, session reconciliation, and stable-ID reopening compatibility.
- Completed: compose visible per-tab close buttons inside dock headers while retaining distinct
  select/drag hit regions and routing pointer closure through the same host mutation path.
- Completed: support delayed persisted floating-window materialization so the authoritative main dock
  can perform initial layout before renderer resources exist, then restore native hosts safely afterward.
- Completed: replace EditorUI's legacy left/viewport/inspector bootstrap grids and separators with a
  default `DockHost`, removing obsolete workspace fields while preserving standalone reference layout.
- Completed: register inner tool content rather than nested `ToolPanel` chrome and remove the redundant
  Game section header, leaving dock tabs as the single title/close/drag surface for every panel.
- Completed: replace placeholder bottom-strip labels with stable-ID activation/reopen commands for all
  six Editor panels, using preferred sibling anchors and viewport resize reconciliation.
- Completed: validate the integrated UI, docking, renderer, profiler weaving, assets, scripting, and
  Editor migration across the full solution suite with clean diff checks.
- Completed: remove redundant logging-abstractions package references flagged by .NET 11 package
  pruning, leaving only the preview-SDK support-policy notice as an environment warning.
- Completed: serialize Graphics test collections so strict thread-local allocation assertions are not
  contaminated by concurrent xUnit work scheduled on the same process thread.
- Completed: expose retained `EngineApplication.SetUI`, mount `UIHost` over the Player's continuous 3D
  presentation, and add a lightweight screen-space HUD root reusable by 2D and 3D scenes.
- Completed: expose host-space tab-well discovery and a session-authoritative cross-host transfer API,
  establishing the DPI/coordinate bridge boundary without leaking internal dock presenters.
- Completed: map logical client positions through shared physical screen pixels and automatically
  center-dock an outside tab release into another native session host across differing DPI scales.
- Completed: coordinate active dock drags across independent host routers, submit live target overlays
  in destination windows, and commit cross-window edge splits or indexed tab-strip insertion drops.
- Completed: add runtime reference-resolution layout with aspect-preserving logical expansion, user
  scale, framebuffer-aware pixel snapping, safe-area root bounds, transformed pointer input, and
  synchronized Player scene/UI projection with explicit policy refresh.
- Completed: add Silk gamepad D-pad/submit/cancel/menu mapping, spatial focus navigation with focused-
  control arrow precedence, unscaled held-direction repeat, and gameplay-only/shared/UI-exclusive
  input arbitration exposed through the runtime Engine facade.
- Completed: split inherited UI timing into default unscaled and opt-in simulation-scaled clocks, feed
  the same scale to Player scripts, and add a reusable centered pause layer whose controller menu,
  keyboard cancel, controller menu/cancel, resume, and quit flow switches modal input and restores
  the previous gameplay context.
- Completed: add an allocation-free camera-projected retained UI layer shared by orthographic 2D and
  perspective 3D scenes, expose runtime attachment through `EngineApplication`, and demonstrate a
  world-origin label alongside the shared Player HUD and pause menu.
- Completed: add explicit external, event-driven, continuous, and hybrid host scheduling; make hybrid
  scheduling follow retained progress, repeat, tooltip, toast, submenu, and type-ahead timers; and run
  Player UI under explicit continuous ownership.
- Completed: add inherited runtime culture and reduced-motion policies, culture-derived RTL flow,
  deterministic exact/parent/invariant localization lookup, retained localized labels, a maximum-
  contrast theme, and stable reduced-motion indeterminate progress presentation.
- Completed: expand allocation-free semantic snapshots across text, buttons, toggles, ranges, choices,
  lists, trees, tabs, menus, images, dialogs, and toolbars, with adapter-invokable actions for invoke,
  toggle, selection, expansion, increment/decrement, and bounded value changes.
- Completed: add inherited application data and nearest-scope resource lookup, typed based-on styles,
  replaceable control templates, typed data templates, and reflection-free one-time/one-way/two-way
  bindings with context rebinding, generation-safe dispatcher marshaling, explicit disposal, and
  automatic host-shutdown detachment.
- Completed: add typed item-container generation with binding cleanup, shared sorted selection state,
  single/multiple/extended selection, anchored modifier ranges, culture-aware type-ahead, and migrate
  viewport-bounded ListView and TreeView selection without regressing their 100,000-item visual bounds.
- Completed: add captured TreeView column-divider resizing with clamped widths and hierarchy-preserving
  cached ascending/descending column sorting that leaves authored scene-node order unchanged.
- Completed: separate virtual-dispatched visual and logical ancestry, preserve visual routing/layout
  while inheriting state through effective ownership, make template presenters visual-only, and make
  generated item containers logically owned with deterministic lifecycle cleanup.
- Completed: add reusable keyed scalar/vector/color animation ownership with easing, independent
  scaled/unscaled clocks, deterministic replacement/cancellation, reduced-motion completion, hybrid
  host wake-up, host/recycling cleanup, and zero-allocation unchanged time traversal.
- Completed: add an executable Release benchmark harness for the six Phase 0 CPU/allocation scenes,
  commit a named-machine 31-sample p50/p95 baseline, enforce absolute budgets plus same-machine
  15-percent median regression checks, and add exact cached-paint and unchanged-pointer allocation tests.
- Completed: cache inactive retained-time branches and active modal input scopes behind explicit
  invalidation/version contracts, reducing unchanged 2,000-element update and routing work to constant time.
- Completed: integrate Unicode 17 UAX #9 bidirectional resolution, paired-bracket/isolate handling,
  visual run shaping, explicit/automatic paragraph direction, grapheme-safe visual carets, and disjoint
  mixed-direction selection ranges; verify both official Unicode conformance vector sets.
- Completed: expose immutable full-tree accessibility snapshots with stable identity, hierarchy, screen
  bounds, labels, descriptions, automation IDs, focus/state/value data, and UI-thread semantic actions;
  publish them through Windows MSAA/UI Automation and native macOS AppKit accessibility elements.
- Completed: preserve native system IME candidate/composition behavior through Silk/GLFW committed Unicode
  input on Windows, macOS, and Linux while retaining the optional pre-edit composition contract for a
  future backend that exposes transient marked text directly.
- Completed: close Unicode editing edge cases around supplementary-letter word navigation, narrow
  single-line editing windows, and tree-column ellipsis without splitting grapheme or surrogate boundaries.
- Completed: pass the final 522-test solution run and all six Release CPU/allocation benchmark budgets.
- Completed: add the standalone `src/UIShowcase` component gallery with scrollable interactive examples,
  overlay/menu/dialog/tooltip/toast demonstrations, a headless composition smoke mode, and native startup
  verification. Run it with `dotnet run --project src/UIShowcase`.
- Completed: migrate the main Editor window from legacy manual UI routing onto `UIHost`, retaining
  explicit preview/processed interception for native chrome, fly camera, gizmos, scene picking, and asset
  operations while sharing versioned pointer/text input, navigation, dispatcher, accessibility, viewport
  policy, retained timing, key repeat, routed global commands, and host lifecycle with detached windows.

### Phase 0 — Contracts, diagnostics, and performance baseline

1. Record current layout, paint, input, and allocation behavior in tests and profiler samples.
2. Define event-data structs/classes, service interfaces, invalidation rules, and ownership diagrams.
3. Add allocation regression tests for pointer move, idle repaint, cached build, and list scrolling.
4. Add a UI debug overlay for bounds, clips, focus, hit target, and invalidation reasons.

Exit criteria: contracts are reviewed; existing Editor behavior is covered; budgets are executable.

### Phase 1 — Reusable host and routed input

1. Move `UIHost` and `UIEventRouter` into `Engine.UI` with compatibility shims.
2. Add `UIDispatcher`, UI-thread assertions, shutdown cancellation, and stable dispatch snapshots.
3. Evolve native input contracts and add compatibility and Silk.NET platform adapters.
4. Implement routed pointer/key/text events, handled propagation, and reentrant dispatch.
5. Implement pointer capture, disabled state, modality, focus manager, scopes, and tab traversal.
6. Implement minimal routed commands and editing-command precedence.
7. Migrate ordinary Editor input wiring from `Program.cs` into routed handlers; defer configurable
   application shortcuts to Phase 6.

Exit criteria: buttons, text fields, trees, dialogs, detached windows, and viewport interactions use the
new router; no Editor code reads `HoveredElement` to implement ordinary control behavior; cross-thread
mutation and retained event-data misuse fail in tests; compatibility input adapters are isolated.

### Phase 2 — Clipping, scrolling, and rendering primitives

1. Add clip commands and clip-aware hit testing.
2. Implement `ScrollViewer`, scroll bars, wheel routing, and touchpad delta handling.
3. Add image/icon, stroke, opacity, and glyph-run draw commands.
4. Implement Vulkan batching and scissor handling with golden draw-list tests.

Exit criteria: nested scrolling clips correctly; popups can overflow parents; no rendering-order
regressions occur around viewport textures.

### Phase 3 — Text editing and platform services

1. Add text shaping abstraction and caret hit testing.
2. Implement selection, clipboard, shortcuts, undo/redo, and multiline editing.
3. Add optional IME composition contracts while preserving system-native candidate UI and committed
   Unicode input on Windows, macOS, and Linux.
4. Add Unicode, grapheme, DPI, and long-text tests.
5. Implement localization resources, font fallback, bidi shaping, RTL layout, and culture-aware input.

Exit criteria: all Editor text entry is keyboard-complete and international text can be composed.

### Phase 4 — Control foundation and styling

1. Introduce `Control`, `ItemsControl`, `Selector`, presenters, and typed styles.
2. Introduce the logical/visual tree contract, typed control/data templates, and UI resources.
3. Add lightweight binding, observable collections, validation, and deterministic detachment.
4. Establish semantic roles/actions before implementing the complete control set.
5. Implement animation ownership, scaled/unscaled clocks, cancellation, and reduced-motion policy.
6. Implement check/radio/toggle, slider, numeric field, combo box, tabs, toolbar, and progress controls.
7. Apply state styling, validation presentation, and accessibility semantics consistently.
8. Replace duplicated Editor control construction with reusable styles and factories.

Exit criteria: controls share state and style behavior; disabled/focus/hover behavior is consistent;
bindings detach without leaks; worker notifications marshal through the dispatcher; validation affects
commands and semantics; logical and visual ancestry have deterministic tests.

### Phase 5 — Virtualized collections

1. Implement item-container generation and recycling.
2. Virtualize list, tree, and table rows.
3. Add multi-selection, range selection, type-ahead, column resizing, and sorting hooks.
4. Test 100,000 logical items with bounded visual children and stable frame allocations.

Exit criteria: large asset and hierarchy collections do not create one UI element per item.

### Phase 6 — Overlay, command, and drag/drop system

1. Implement host overlay manager and popup placement.
2. Migrate context menus, dialogs, tooltips, and drag previews.
3. Add host-owned routed commands, scoped bindings, and configurable key gestures.
4. Add editing precedence, modal suppression, active-host application routing, and conflict detection.
5. Implement typed drag data, allowed effects, drop indicators, and capture-loss recovery.
6. Validate popup placement across monitors, work areas, DPI scales, and detached hosts.

Exit criteria: overlay behavior is host-owned; menus are fully keyboard navigable; drag/drop works
across panels and detached windows where supported; root panels own no shortcut policy; editing
gestures override global bindings; modal scopes suppress underlying shortcuts; detached windows route
window commands independently; binding conflicts and user rebinding are covered by tests.

### Phase 7 — Runtime/game UI integration

1. Expose screen-space `UIHost` composition through the `Engine` facade used by Player.
2. Implement continuous and hybrid tick policies without forcing layout or paint each frame.
3. Add safe-area, resolution, aspect-ratio, user-scale, and pixel-perfect layout policies.
4. Add gamepad navigation and gameplay/UI input-context arbitration.
5. Add an unscaled UI clock and validate pause-menu behavior.
6. Add a minimal world-space host path for 2D and 3D labels, prompts, and health bars.
7. Build representative runtime samples: a 3D HUD, a 2D HUD, a pause menu, and world-space labels.
8. Validate animation clocks, reduced-motion, localization, RTL layout, and runtime culture changes in
   continuous Player hosts.

Exit criteria: the same controls run in Editor event-driven mode and Player continuous mode; a static
HUD allocates nothing and rebuilds neither layout nor paint while unchanged; 2D and 3D samples share
the screen-space UI implementation.

### Phase 8 — Editor docking and workspace migration

1. Implement dock model, tab wells, splitters, previews, and floating hosts.
2. Migrate hierarchy, inspector, profiler, asset browser, scene, and game panels.
3. Add versioned workspace persistence and safe restoration.
4. Remove remaining manual layout/input coordination from `Editor/Program.cs`.

Exit criteria: the Editor workspace is composed through UI services and survives layout round trips.

### Phase 9 — Accessibility, polish, and stabilization

1. Complete the semantic roles/actions established in Phase 4 and audit keyboard coverage.
2. Add high-contrast, scaling, reduced-motion, and screen-edge tests.
3. Add native accessibility adapters where platform APIs permit.
4. Stress test mutation, window closure, device loss, DPI changes, and long sessions.

Exit criteria: accessibility audit passes, performance budgets pass, and public APIs are documented.

## Testing Strategy

Every phase adds tests at four levels:

- Unit: layout arithmetic, routing order, focus traversal, selection, text edits, popup placement.
- Snapshot: exact `UIDrawCommand` ordering, clips, layers, and state styling.
- Integration: `UIHost` with fake window/input/renderer and multiple independent hosts.
- Allocation/performance: steady-state pointer movement, scrolling, layout, paint, and virtualization.

Required invariants include:

- removed or hidden elements cannot retain focus or pointer capture;
- input dispatch tolerates tree mutation without invoking removed targets;
- clips affect paint and hit testing identically;
- modal overlays prevent interaction with content beneath them;
- cached trees generate no draw changes until invalidated;
- hot-path enumeration does not box interface enumerators;
- popup and detached-window state never leaks between hosts;
- continuous game updates do not imply continuous layout or paint rebuilds;
- pausing game simulation does not stop unscaled UI interaction or transitions;
- gameplay input is suppressed or retained according to the active UI input context;
- safe-area and UI-scale changes invalidate layout exactly once;
- equivalent 2D and 3D HUD roots produce the same screen-space layout;
- routed-event mutation cannot redirect the stable route being dispatched;
- nested dispatch does not overwrite outer event data;
- disposed hosts reject worker/timer callbacks and release all manager registrations;
- generated containers release bindings, commands, and animations when recycled;
- culture and RTL changes preserve logical focus and invalidate affected layout once.

## Performance Budgets

Initial budgets should be measured on the existing Editor and tightened after Phase 2:

- zero steady-state managed allocation for unchanged layout and paint submission;
- zero allocation for pointer movement when the hover target does not change;
- zero allocation for wheel scrolling after containers and text are cached;
- bounded work proportional to visible virtualized items, not logical item count;
- one layout pass per invalidation batch;
- one retained paint rebuild per dirty subtree, with unchanged siblings reused;
- no device idle waits introduced by UI updates.

Phase 0 creates repeatable benchmark scenes and records p50/p95 CPU duration on a named reference
machine. Initial CPU targets, excluding renderer presentation waits, are:

- unchanged continuous-host update below 0.05 ms for a 2,000-element retained tree;
- pointer move with unchanged target below 0.05 ms and changed-target routing below 0.20 ms;
- cached draw-list composition below 0.20 ms for 10,000 retained commands;
- dirty-subtree layout and paint below 1.0 ms for a representative 2,000-element Editor tree;
- virtualized scrolling below 1.0 ms for 100 visible rows backed by 100,000 logical items.

Absolute targets are calibrated when the Phase 0 reference benchmark is committed. Continuous
integration also fails a benchmark when its median regresses by more than 15 percent across repeated
runs, provided the change exceeds timer noise. Allocation budgets remain hard zero/bounded assertions
rather than statistical benchmarks.

The executable harness is `tools/Engine.UI.Benchmarks`. The committed reference run is
`docs/ui-performance-baseline.json`, captured on the named machine recorded in that file. Run the
absolute budgets on any machine with:

```powershell
dotnet run --project tools/Engine.UI.Benchmarks -c Release
```

On the recorded reference machine, run the absolute and relative regression gates together with:

```powershell
dotnet run --project tools/Engine.UI.Benchmarks -c Release -- --verify-baseline docs/ui-performance-baseline.json
```

Relative comparisons are deliberately skipped when the machine name differs; absolute CPU and
allocation budgets still apply. Refresh the baseline only after an intentional performance review by
using `--write-baseline docs/ui-performance-baseline.json`.

## Migration Rules

1. Do not rewrite the Editor UI in one change.
2. Introduce new infrastructure behind adapters, migrate one behavior, then remove the adapter.
3. Preserve current visual output with draw-list snapshots during foundation phases.
4. Do not add a control whose required primitive is missing; implement the primitive first.
5. Keep application models out of controls. Controls consume items, commands, and templates.
6. Prefer explicit ownership over global UI state.
7. Treat breaking public API cleanup as a named migration phase rather than accumulating permanent
   compatibility overloads.

## Recommended First Milestone

The first implementation milestone should contain only:

1. `UIHost`, `UIInputManager`, and `UIFocusManager` moved into `Engine.UI`.
2. `UIDispatcher`, UI-thread assertions, and host shutdown cancellation.
3. Versioned input contracts plus current-input compatibility adapters.
4. Routed pointer events with position/button/modifiers and handled state.
5. Stable route snapshots, nested-dispatch safety, pointer capture, and capture-loss handling.
6. Tab traversal, modality tracking, modal focus scopes, and minimal routed commands.
7. Event-driven, continuous, and hybrid host scheduling contracts.
8. Clip rectangles in layout, hit testing, `UIDrawList`, and Vulkan submission.
9. Allocation tests for pointer movement, continuous unchanged updates, and cached painting.

This milestone unlocks reliable sliders, splitters, scrolling, menus, drag/drop, text selection, and
docking. Building more controls before it would multiply workarounds in every widget.
