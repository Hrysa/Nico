# Repository Guidelines

## Project Structure & Module Organization

Nico is a Rust 2024 workspace. Engine crates live under `crates/`: `nico-runtime` is the
headless application kernel and depends on the hecs-backed `nico-ecs` world crate.
`nico-input` owns device state, `nico-presentation` owns the immutable presentation
boundary. `nico-presentation-control` owns camera control, coordinate helpers, and
cached bitmap text; `nico-spatial` owns headless queries and bounded sliding.
`nico-render` uses `nico-rhi` contracts. `nico-rhi-wgpu` implements those
contracts; `nico-winit` composes the native client. `nico-assets` owns asset identity,
leases, and optional runtime-owned PNG/GLB loading through its `loading` feature,
and `nico-launch` owns native CLI and diagnostics startup; its `client` and `server`
features own native host transport composition and lifecycle. `nico-ops` provides
dependency-free host control, command bookkeeping, and owned snapshot publication.
Its optional `mcp` feature owns host/game tool catalogs
and handlers; `bridge` owns registration, routing, and reconnects. `apps/nico-bridge` is
the executable entry point. Physics and broader devtools have no placeholder crates.
Runtime must not depend on presentation, providers, or launch policy, and `nico-ecs`
must not depend on runtime.

Executable development tools belong in `apps/` when a concrete tool justifies a
standalone package. Games live in `games/<game>/` with `shared`, `client`, and `server`
packages. Put authoritative assets in `assets/logic` and client-only content in
`assets/presentation` within each game. Engine bootstrap shaders use the
repository-level `assets/presentation/shaders` root. Architecture decisions and reviews
belong in `docs/`.

## Build, Test, and Development Commands

- `cargo check --workspace`: type-check every workspace package quickly.
- `cargo test --workspace`: run all unit and integration tests.
- `cargo fmt --all -- --check`: verify standard Rust formatting.
- `cargo clippy --workspace --all-targets -- -D warnings`: enforce lint-clean code.
- `cargo build -p nico-bridge -p minimal-game-client -p minimal-game-server`: build
  the bridge and both hosts.
- `cargo run -p minimal-game-client`: run the native client until its window closes.
- `cargo run -p minimal-game-client -- --smoke-frames 3`: run bounded client-session
  frames; this counter does not guarantee three successful GPU presentations.
- `cargo run -p nico-shaderc -- --check`: verify generated shaders; requires `slangc`.
- `cargo run -p minimal-game-server`: run the continuous 60 Hz headless server;
  stop it with Ctrl+C.
- `cargo run -p minimal-game-server --example controlled`: demonstrate in-process
  readiness observation and orderly stop with the real server runner.

For isolated native validation, build those three packages with `--target-dir
target/bridge-validation`, then run `python apps/nico-bridge/tests/native_smoke.py
--bin-dir target/bridge-validation/debug`. This test opens a client window, uses its own
bridge port, and cleans up only its own processes. Do not replace or stop a user's
running session to run tests. On Windows, running executables can lock build outputs;
use an isolated target directory for validation when needed.

## Coding Style & Naming Conventions

Use `rustfmt` defaults and four-space indentation. Name modules, functions, and tests
with `snake_case`; types and traits with `UpperCamelCase`; constants with
`SCREAMING_SNAKE_CASE`. The workspace forbids unsafe code and enables Clippy's `all`
lint group. Keep public contracts small, document lifecycle and ownership, and prevent
backend-specific types from leaking into engine-facing APIs.

## Measurement and AI operation requirements

Measurement and profiling remain requirements across all libraries. Future deep
profiling will use experimental Rust/LLVM XRay, aiming for automatic function capture, a
nested call tree, inclusive/self time, and invocation counts without per-method
annotations. Profiling implementation and compatibility investigation are deferred: do
not add profiler code, per-method instrumentation, custom collectors/viewers, or
toolchain configuration for this work now. XRay has not been integrated or validated for
Nico.

Existing tracing remains for diagnostics and semantic context. Measure suspected
bottlenecks before optimizing, distinguish elapsed scope time from actual CPU execution
time, and preserve authoritative behavior. Profiling work does not block AI-accessible
client/server operations.

Client and server operations must be accessible through structured, discoverable
interfaces. Codex connects only to `nico-bridge`. Users launch games independently; the
bridge never launches games, and bridge/Codex disconnect must not stop them. Games
attempt connections to `127.0.0.1:47631` by default; `--no-bridge` disables attempts and
`--bridge ADDRESS` overrides the endpoint. Client/server executables do not expose
direct MCP transports or support `--mcp-stdio`.

All host and game tools come from connection registrations. Engine hosts supply
`status`, `stop`, and native `diagnostics`; games may add their own tools. Preserve the
built-in names. Discover connected instances before calling tools; cached schemas do not
imply availability, and reconnects receive new instance IDs. Use `list_game_tools` and
`call_game_tool` when dynamic tool exposure is unavailable. It was not observed in the
tested Codex session; do not assume automatic refresh.

Keep status distinctions explicit: connection state, snapshot age, activity, readiness,
session progress, and successful presentation counts have different meanings.
Presentation API success is not proof of GPU completion or display scanout. Stop
acceptance is not shutdown completion or process exit. Timed-out mutations may have
executed; do not retry them blindly.

Native diagnostic capture uses bounded tracing events, exclusive process-local cursors,
eviction counts, and truncation flags. It shares the host logging filter, excludes span
timing, and survives bridge reconnects but not game exit. This is operational
diagnostics, not profiling. Protocol bounds and deployment scope live in the [bridge
contract](docs/plans/2026-09-11-mcp-bridge.md).

Keep transport and process control outside the headless runtime. Apply simulation
commands at runtime-owned boundaries; tooling threads read owned snapshots or queue
bounded requests, never mutate the world. New operational features need an automation
path alongside any human-facing interface.

Use MCP as the preferred interface for interacting with running clients and servers.
Discover and use existing tools first. When a task needs an operation that is missing,
first try to implement and test the smallest appropriate MCP tool and its host/runtime
integration, then use it to complete the task. Keep arguments, results, and failures
structured and respect the ownership rules below. If MCP cannot support the task yet,
explain the concrete limitation before using a fallback. Build and test commands may
continue to use Cargo directly. This guidance does not change the deferral of profiling
work.

MCP service and host lifecycle implementations belong under engine crates' `src/`, not
`games/`. Games compose the engine host and may register additional tools; they must not
own transport, service threads, or replace built-in lifecycle tools.

## Testing Guidelines

Tests currently use Rust's built-in test framework in colocated `#[cfg(test)]` modules.
Give tests behavioral names such as `headless_run_has_deterministic_stage_order`. Add
tests beside the owning module; use a package-level `tests/` directory only for true
public-API integration tests. Cover success, failure, shutdown, and deterministic timing
paths. No numeric coverage target is established, but every behavior change should have
focused regression coverage.

## Commit & Pull Request Guidelines

Use concise imperative subjects with a prefix such as `feat:`, `fix:`, `docs:`, `test:`,
or `chore:`. Keep commits focused. Pull requests should explain motivation,
architectural impact, and validation commands; link relevant issues and include
screenshots only for visible presentation changes. Do not mix unrelated formatting or
generated-file changes into a feature PR.

## Documentation ownership

Each document has one primary purpose:

- [README](README.md): current capabilities, setup, and commands.
- [Architecture](docs/architecture.md): dependency direction, ownership, and contracts.
- [Roadmap](docs/roadmap.md): phase goals, scope, status, completion criteria, and
  validation evidence. Do not put task checklists here.
- [TODO](TODO.md): concrete unfinished actions linked to roadmap phases. Remove
  completed actions after recording meaningful results in the roadmap. Do not
  copy phase scope or completion criteria into TODO.
- [Review guide](docs/review.md): criteria for assessing proposed changes.
- ADRs: accepted decisions and their rationale. Preserve history; append dated
  implementation updates rather than rewriting earlier status as current fact.
- Plans: detailed scoped designs. Mark superseded plans clearly and link to the
  replacement; they must not appear to describe current capabilities.

When behavior changes, update the owning document and use links elsewhere. Distinguish
implemented, unverified, and deferred work. Keep validation claims specific to the
tested build, platform, and scenario; do not turn a session-specific observation into a
general compatibility claim. Check relative links and anchors when renaming headings.
Keep paragraphs readable and avoid accumulating repeated progress notes or completed
checklists across documents.
