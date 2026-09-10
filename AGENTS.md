# Repository Guidelines

## Project Structure & Module Organization

Nico is a Rust 2024 workspace. Engine crates live under `crates/`: `nico-runtime`
is the headless application kernel and depends on the hecs-backed `nico-ecs`
world crate. `nico-input` owns device state, `nico-presentation` owns the
immutable presentation boundary, and `nico-render` uses `nico-rhi` contracts.
`nico-rhi-wgpu` implements those contracts; `nico-winit` composes the native
client. `nico-assets` owns asset identity and `nico-launch` owns native CLI and
diagnostics startup. Physics and broader devtools have no placeholder crates.
Runtime must not depend on presentation, providers, or launch policy, and
`nico-ecs` must not depend on runtime.

Executable development tools belong in `apps/` when a concrete tool justifies a
standalone package. Games live in `games/<game>/` with `shared`, `client`, and
`server` packages. Put authoritative assets in `assets/logic` and client-only
content in `assets/presentation` within each game. Engine bootstrap shaders use
the repository-level `assets/presentation/shaders` root. Architecture decisions
and reviews belong in `docs/`.

## Build, Test, and Development Commands

- `cargo check --workspace`: type-check every workspace package quickly.
- `cargo test --workspace`: run all unit and integration tests.
- `cargo fmt --all -- --check`: verify standard Rust formatting.
- `cargo clippy --workspace --all-targets -- -D warnings`: enforce lint-clean code.
- `cargo run -p minimal-game-client`: run the native client until its window closes.
- `cargo run -p minimal-game-client -- --smoke-frames 3`: run bounded client-session
  frames; this counter does not guarantee three successful GPU presentations.
- `cargo run -p nico-shaderc -- --check`: verify generated shaders; requires `slangc`.
- `cargo run -p minimal-game-server`: run the continuous 60 Hz headless server;
  stop it with Ctrl+C.

## Coding Style & Naming Conventions

Use `rustfmt` defaults and four-space indentation. Name modules, functions, and
tests with `snake_case`; types and traits with `UpperCamelCase`; constants with
`SCREAMING_SNAKE_CASE`. The workspace forbids unsafe code and enables Clippy's
`all` lint group. Keep public contracts small, document lifecycle and ownership,
and prevent backend-specific types from leaking into engine-facing APIs.

## Measurement and AI operation requirements

Every library must support measurement and profiling of its meaningful work.
Use structured spans, timings, and counters where appropriate; keep collection
and export policy in the host. Measure suspected bottlenecks before optimizing,
and keep instrumentation overhead controllable without changing behavior.

Client and server operations must be accessible to AI tooling through structured,
discoverable interfaces, with an MCP adapter as a planned integration. Provide
machine-readable status, diagnostics, profiling results, and explicit operation
results. Keep transport and process control outside the headless runtime;
apply simulation commands at runtime-owned boundaries rather than mutating the
world from a tooling thread. New operational features should include an
automation path alongside any human-facing interface.

## Testing Guidelines

Tests currently use Rust's built-in test framework in colocated `#[cfg(test)]`
modules. Give tests behavioral names such as
`headless_run_has_deterministic_stage_order`. Add tests beside the owning module;
use a package-level `tests/` directory only for true public-API integration tests.
Cover success, failure, shutdown, and deterministic timing paths. No numeric
coverage target is established, but every behavior change should have focused
regression coverage.

## Commit & Pull Request Guidelines

Use concise imperative subjects with a prefix such as `feat:`, `fix:`,
`docs:`, `test:`, or `chore:`. Keep commits focused. Pull requests should explain
motivation, architectural impact, and validation commands; link relevant issues
and include screenshots only for visible presentation changes. Do not mix
unrelated formatting or generated-file changes into a feature PR.

## Documentation ownership

Keep [README](README.md) focused on current capabilities and commands,
[architecture](docs/architecture.md) on ownership and contracts,
[roadmap](docs/roadmap.md) on milestone status, and [TODO](TODO.md) on actionable
work. Use [the review guide](docs/review.md) for change assessment. Preserve ADR
decision history and label implementation updates. Distinguish implemented,
unverified, and deferred work; planned profiling or MCP interfaces must not be
documented as available.
