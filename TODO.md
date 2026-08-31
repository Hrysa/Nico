# Nico TODO

Detailed tasks are kept only for the current milestone. Completed foundations
and provisional later directions are summarized in
[`docs/roadmap.md`](docs/roadmap.md).

## Real client host

### Lifecycle and provider

- [x] Record desktop lifecycle requirements and supported development platforms.
- [x] Compare a concrete event-loop provider against those requirements.
- [x] Record event-loop ownership before defining presentation traits.
- [x] Make Winit's `ApplicationHandler` own the permanent native client loop.
- [x] Create the window on `resumed` and tolerate redundant resume/suspend events.
- [x] Drive simulation from monotonic host time and presentation from
      `RedrawRequested`.
- [x] Handle close exactly once and shut down after callback or loop failures.
- [x] Observe resize and focus without committing to renderer-facing contracts.
- [x] Keep a bounded `--smoke-frames` mode for executable validation.
- [x] Extract the proven concrete host and Winit adapter into `nico-winit`.
- [x] Keep the example client limited to game construction, bindings, and
      semantic command mapping.

### Multi-device input and semantic mapping

- [x] Add an engine-owned, provider-neutral `nico-input` crate for multiple
      keyboards, pointers, gamepads, and touch devices.
- [x] Track held and transitional buttons, scalar axes, persistent vectors, and
      frame-local motion.
- [x] Normalize Winit keyboard, pointer button, cursor, wheel, raw motion, and
      touch events into the manager.
- [x] Convert aggregate device state into the game-owned `PlayerCommand` and
      normalized `MovementVector` types.
- [x] Keep Winit key and device types out of runtime and shared gameplay APIs.
- [x] Release held and continuous controls on focus loss or device disconnect.
- [ ] Select and connect the first native gamepad provider to `nico-input`.

### Verification and extraction gate

- [x] Preserve deterministic server and headless runtime tests.
- [x] Test client session startup, ticks, and idempotent shutdown without a GUI.
- [x] Run an executable native-window smoke check where platform automation
      permits it.
- [x] Extract the concrete Winit provider without inventing a provider-neutral
      host trait.
- [ ] Define a provider-neutral host contract only if another provider proves it
      necessary.

## Decision gates after the client host

- [ ] Choose the first asset required for a visible frame.
- [ ] Use the typed service bridge for its runtime byte-loading path.
- [ ] Introduce only the manifest and artifact vocabulary required by that path.
- [ ] Reassess whether presentation provider boundaries justify additional
      modules or crates after one provider is working.

## Deferred decisions

- Native async executor or worker implementation.
- Graphics, physics, audio, UI, networking, and persistence providers.
- Asset importer, cache, serialization, and bundle formats.
- Devtools structure and any standalone tool applications.
