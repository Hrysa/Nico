# ADR 0001: Winit owns the first native client loop

- Status: accepted
- Date: 2026-08-28
- Updated: 2026-08-31

## Context

Nico's runtime is headless and host-driven. The first real desktop client needs
a window, a permanent event loop, monotonic frame timing, redraw scheduling,
focus and resize observation, suspension handling, and orderly shutdown. It must
not push native window or key types into `nico-runtime` or shared gameplay.

The supported development targets for this milestone are Windows 10 or newer,
current macOS, and Linux through Wayland or X11. Windows is the initial smoke
platform. Web, mobile, and consoles remain later host investigations, but the
desktop design should not rely on polling APIs that are known to conflict with
their lifecycle models.

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
8. Resize and focus are observed now; renderer surfaces and semantic focus-loss
   input policy are introduced with their first consumers.
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

The Winit repository currently advertises a 0.31 beta. Nico selects stable
0.30.13 rather than adopting a prerelease event-loop API.

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
supplies its title, smoke policy, and `InputState` to semantic-command mapper.

Use `ApplicationHandler` as follows:

- `resumed`: create the window if absent, start the Nico session once, reset the
  frame clock, and request a redraw;
- `about_to_wait`: wait until the next frame deadline, then request redraw;
- `RedrawRequested`: tick from monotonic time and present the no-device frame;
- `Resized` and `Focused`: record observable lifecycle state;
- `suspended`: pause ticks and reset timing without shutting down the game;
- `CloseRequested` and `exiting`: converge on idempotent orderly shutdown.

## Consequences

The game client gains a real platform-owned loop without containing platform
host machinery or changing runtime dependency direction. Winit is a dependency
only of `nico-winit`, so its types do not leak into the game, runtime, input, or
presentation APIs. The first window may show undefined client-area contents
because no graphics provider is selected; drawing is the next presentation
concern, not part of this event-loop decision.

Revisit this decision after one working native host if mobile, web, embedding,
or multiple-window requirements contradict it.

## Primary references

- [Winit crate documentation](https://docs.rs/winit/0.30.13/winit/)
- [`ApplicationHandler` lifecycle](https://docs.rs/winit/0.30.13/winit/application/trait.ApplicationHandler.html)
- [`EventLoop::run_app`](https://docs.rs/winit/0.30.13/winit/event_loop/struct.EventLoop.html#method.run_app)
- [`ControlFlow`](https://docs.rs/winit/0.30.13/winit/event_loop/enum.ControlFlow.html)
- [Winit platform scope](https://github.com/rust-windowing/winit/blob/master/FEATURES.md)
- [SDL3 Rust `EventPump`](https://docs.rs/sdl3/latest/sdl3/struct.EventPump.html)
