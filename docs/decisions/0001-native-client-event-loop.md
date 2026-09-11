# ADR 0001: Winit owns the first native client loop

- Status: accepted
- Date: 2026-08-28
- Updated: 2026-09-10

## Context

Nico's runtime is headless and host-driven. The first real desktop client needs
a window, a permanent event loop, monotonic frame timing, redraw scheduling,
focus and resize observation, suspension handling, and orderly shutdown. It must
not push native window or key types into `nico-runtime` or shared gameplay.

The intended desktop targets at the time of this decision were Windows 10 or
newer, macOS, and Linux through Wayland or X11. Windows is the initial smoke
platform; this target list is not a record of validation on every platform.
Web, mobile, and consoles remain later host investigations, but the desktop
design should preserve their resume/suspend lifecycle requirements.

Lifecycle requirements:

1. The native event loop runs on the process main thread and owns native windows.
2. The application creates its window only after the provider reports `resumed`.
3. Duplicate `resumed` and `suspended` notifications are harmless.
4. Suspension pauses frame ticks and resets the frame-time origin, preventing a
   large catch-up delta after resume.
5. Authoritative simulation receives monotonic elapsed host deltas.
6. Presentation work occurs only for the owned window's redraw notification.
7. Close requests, runtime failures, callback failures, and loop exit converge
   on exactly one orderly runtime and presentation shutdown.
8. Resize and focus are observed by the host and forwarded to graphics and input
   consumers as those capabilities are introduced.
9. A bounded smoke mode can exercise startup, redraw, simulation, and shutdown
   without changing the permanent-loop default.
10. Headless tests and the dedicated server remain free of window dependencies.

## Options considered

### Winit 0.30.13

Winit is a Rust window and event-loop library supporting Windows, macOS, X11,
and Wayland. Its stable `ApplicationHandler` API explicitly models `resumed`,
`suspended`, window events, redraw requests, and loop exit. `EventLoop::run_app`
owns dispatch on the calling thread, matching Nico's existing host-owned runtime.
Winit deliberately does not provide rendering, so selecting it does not select a
graphics API.

Nico selected Winit 0.30.13 for the initial implementation. This decision does
not track newer upstream releases.

### SDL3 Rust bindings

SDL3 exposes a conventional event pump and broader media facilities. That model
would work for a desktop-only loop, but it introduces an SDL native-library
distribution decision before Nico needs audio, controllers, or rendering. Its
polling shape also provides less direct evidence for the resume/suspend ownership
model Nico wants to preserve.

## Decision

Use Winit 0.30.13 through the concrete `nico-winit` engine provider. The initial
experiment lived in `minimal-game-client`; after its lifecycle and input adapter
were proven, that machinery moved into the provider crate. Do not add a
provider-neutral window or presentation-host trait before a second provider
establishes shared requirements.

`nico-winit` owns Winit types, native lifecycle, frame scheduling, normalized
input adaptation, and runtime/presentation session coordination. A game client
supplies its title, bootstrap shader path, smoke policy, and a mapper from
`InputState` to semantic commands.

Use `ApplicationHandler` as follows:

- `resumed`: create the window and graphics resources if absent, start the Nico
  session once, reset the frame clock, and request a redraw;
- `about_to_wait`: wait until the next frame deadline, then request redraw;
- `RedrawRequested`: map input, tick from monotonic time, advance the presentation
  lifecycle, and drive the renderer;
- `Resized`: update the native surface extent;
- `Focused`: record focus and release input controls on focus loss;
- `suspended`: pause ticks and reset timing without shutting down the game;
- `CloseRequested` and `exiting`: converge on idempotent orderly shutdown.

## Consequences

The game client gains a real platform-owned loop without containing platform
host machinery or changing runtime dependency direction. Winit is a dependency
only of `nico-winit`, so its types do not leak into the game, runtime, input, or
presentation APIs. Graphics ownership is a separate decision, now implemented
through [ADR 0002](0002-nico-rhi-wgpu-backend.md) and
[ADR 0003](0003-render-pipeline-layer.md).

Revisit this decision after one working native host if mobile, web, embedding,
or multiple-window requirements contradict it.

## Implementation status (2026-09-10)

The core host is implemented with automated lifecycle tests and a recorded
bounded Windows GPU smoke run. Interactive Windows/macOS resize and
minimize/restore validation remains open. Suspension and minimization are
different lifecycle cases and require separate evidence.

The smoke limit counts client-session frames, including frames for which GPU
presentation may be skipped. Native gamepad integration is deferred. Shared
profiling capture is deferred to future experimental XRay integration.
AI-accessible host operations are next and keep protocol dependencies out of runtime. See
[the roadmap](../roadmap.md) and [current tasks](../../TODO.md).

## Implementation update (2026-09-11)

Independent-game MCP operations, structured diagnostics, and separate successful
presentation counts are implemented. The bridge discovers host/game tools from
connection registrations and never launches games or stops them on disconnect.
The earlier direct server MCP transport was removed. Protocol dependencies remain
outside runtime. Windows/Vulkan smoke and live MCP results are recorded in
[roadmap phase 2](../roadmap.md#2-control-clients-and-servers-with-ai).
Interactive platform checks remain in [TODO](../../TODO.md#native-host-validation).
This updates implementation status without changing the event-loop decision.

## Primary references

- [Winit crate documentation](https://docs.rs/winit/0.30.13/winit/)
- [`ApplicationHandler` lifecycle](https://docs.rs/winit/0.30.13/winit/application/trait.ApplicationHandler.html)
- [`EventLoop::run_app`](https://docs.rs/winit/0.30.13/winit/event_loop/struct.EventLoop.html#method.run_app)
- [`ControlFlow`](https://docs.rs/winit/0.30.13/winit/event_loop/enum.ControlFlow.html)
- [Winit platform scope](https://github.com/rust-windowing/winit/blob/master/FEATURES.md)
- [SDL3 Rust `EventPump`](https://docs.rs/sdl3/latest/sdl3/struct.EventPump.html)
