# Nico TODO

Detailed tasks are kept only for the current milestone. Completed foundations
and provisional later directions are summarized in
[`docs/roadmap.md`](docs/roadmap.md).

## Real client host

- [ ] Record the required desktop lifecycle and supported development platforms.
- [ ] Evaluate a concrete window/event-loop provider against those requirements.
- [ ] Write down event-loop ownership before defining presentation traits.
- [ ] Replace the bounded client loop with provider-driven startup, ticks,
      redraws, and orderly shutdown.
- [ ] Normalize the minimum keyboard or controller input needed by minimal-game.
- [ ] Convert device input into a game-owned semantic command.
- [ ] Handle close, resize, focus, suspend/resume, and redraw behavior supported
      by the selected provider.
- [ ] Preserve deterministic headless execution and the no-device smoke path.
- [ ] Add focused lifecycle tests and an executable smoke check where practical.

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
