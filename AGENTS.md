# Repository Guidelines

## Project Structure & Module Organization

Nico is a Rust 2024 workspace. Engine crates live under `crates/`: `nico-runtime`
is the headless application kernel and depends on the hecs-backed `nico-ecs`
world crate. `nico-input` owns device state, `nico-presentation` owns the
immutable presentation boundary, and `nico-render` uses `nico-rhi` contracts.
`nico-rhi-wgpu` implements those contracts; `nico-winit` composes the native
client. `nico-assets` owns asset identity and `nico-launch` owns native CLI and
diagnostics startup; its `server` feature owns the headless host and MCP lifecycle.
`nico-ops` provides host status/stop control and an optional
`mcp` feature for a stdio adapter; its default build has no external dependencies.
Physics and broader devtools have no placeholder crates.
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
- `cargo run -p minimal-game-server --example controlled`: demonstrate in-process
  readiness observation and orderly stop with the real server runner.

## Coding Style & Naming Conventions

Use `rustfmt` defaults and four-space indentation. Name modules, functions, and
tests with `snake_case`; types and traits with `UpperCamelCase`; constants with
`SCREAMING_SNAKE_CASE`. The workspace forbids unsafe code and enables Clippy's
`all` lint group. Keep public contracts small, document lifecycle and ownership,
and prevent backend-specific types from leaking into engine-facing APIs.

## Measurement and AI operation requirements

Measurement and profiling remain requirements across all libraries. Future deep
profiling will use experimental Rust/LLVM XRay, aiming for automatic function
capture, a nested call tree, inclusive/self time, and invocation counts without
per-method annotations. Profiling implementation and compatibility investigation
are deferred: do not add profiler code, per-method instrumentation, custom
collectors/viewers, or toolchain configuration for this work now. XRay has not
been integrated or validated for Nico.

Existing tracing remains for diagnostics and semantic context. Measure suspected
bottlenecks before optimizing, distinguish elapsed scope time from actual CPU
execution time, and preserve authoritative behavior. Profiling work does not
block AI-accessible client/server operations.

Client and server operations must be accessible to AI tooling through structured,
discoverable interfaces. The server exposes MCP `status`/`stop`; client integration
and broader process operations remain planned. Provide
machine-readable status, diagnostics, and explicit operation results. Expose
profiling results later when that capability is available. Keep transport and
process control outside the headless runtime;
apply simulation commands at runtime-owned boundaries rather than mutating the
world from a tooling thread. New operational features should include an
automation path alongside any human-facing interface.

MCP service and host lifecycle implementations belong under engine crates' `src/`,
not `games/`. Games compose the engine host and may register additional tools;
they must not own transport, service threads, or replace built-in lifecycle tools.

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
