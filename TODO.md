# Nico TODO

Current tasks and the client host completion record are kept here. Completed
foundations and provisional later directions are summarized in
[`docs/roadmap.md`](docs/roadmap.md).

## Measurement and AI operations — next milestone

- [ ] Define common span names, timing units, counters, and correlation fields
      across libraries using the existing diagnostics foundation where suitable.
- [ ] Add host-controlled profile capture and machine-readable export with
      bounded retention and controllable collection overhead.
- [ ] Break down startup into window creation, shader read, graphics instance,
      adapter/device creation, surface configuration, shader module/pipeline
      creation, and first successful presentation; measure the reported delay.
- [ ] Instrument runtime stages and systems, service queue behavior, rendering,
      client frames, and server ticks; distinguish CPU and GPU measurements.
- [ ] Define structured client/server operations for capabilities, readiness,
      diagnostics, profile capture, and orderly shutdown.
- [ ] Implement an MCP adapter that launches local development client/server
      processes and exercises those operations with explicit results and timeouts.
- [ ] Verify failure, timeout, shutdown, bounded collection, and unchanged
      authoritative behavior with tooling enabled and disabled.

## Real client host — core implementation complete

Interactive Windows/macOS resize and minimize/restore validation remains open.
Gamepad integration and reflection/asset-backed shaders are deferred below.

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

### Verification and extraction gate

- [x] Preserve deterministic server and headless runtime tests.
- [x] Test client session startup, ticks, and idempotent shutdown without a GUI.
- [x] Run an executable native-window smoke check where platform automation
      permits it.
- [x] Extract the concrete Winit provider without inventing a provider-neutral
      host trait.
- [ ] Define a provider-neutral host contract only if another provider proves it
      necessary.

### First GPU surface

- [x] Add a backend-neutral `nico-rhi` surface lifecycle contract.
- [x] Add the first concrete `nico-rhi-wgpu` provider.
- [x] Configure non-zero surfaces and clear every acquired frame.
- [x] Reconfigure on resize and recover outdated, suboptimal, and lost surfaces.
- [x] Preserve zero-size, timeout, and occlusion as non-fatal frame outcomes.
- [x] Add adapter capabilities, resources, bindings, graphics and compute
      pipelines, queue uploads, transfer commands, and render/compute passes.
- [x] Add `nico-render` and move bootstrap pipeline creation, frame recording,
      triangle drawing, submission, and presentation out of the wgpu backend.
- [x] Express the native clear through frame acquisition, a render pass, command
      submission, and explicit presentation rather than a special RHI call.
- [x] Run a bounded native GPU smoke check on Windows.
- [ ] Validate interactive resize and minimize/restore on Windows and macOS.
- [x] Draw a bootstrap triangle through an RHI-created shader and graphics pipeline.
- [x] Author the bootstrap shader in Slang and compile its WGSL artifact offline.
- [x] Compile shaders with a standalone `nico-shaderc` executable and load the
      bootstrap artifact without rebuilding Rust crates.

## Deferred host and shader follow-ups

- [ ] Select and connect the first native gamepad provider to `nico-input`.
- [ ] Add Slang reflection and asset-backed shaders for the first real primitive.

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
- Broader devtools structure beyond the measurement and AI operation baseline.
