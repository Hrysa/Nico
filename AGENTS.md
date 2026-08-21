# Repository Guidelines

## Project Structure & Module Organization

Nico is a Rust 2024 workspace. Engine crates live under `crates/`: `nico-runtime`
is the headless application kernel and depends on the `hecs`-backed `nico-ecs`
world crate. `nico-presentation`, `nico-assets`, `nico-physics`, and
`nico-devtools` provide optional capabilities; `nico-launch` owns native CLI and
diagnostics startup. Keep the dependency direction headless: runtime must not
depend on presentation or launch policy, and `nico-ecs` must not depend on
runtime.

Executable development tools belong in `apps/` when a concrete tool justifies a
standalone package. Games live in `games/<game>/` with `shared`, `client`, and
`server` packages. Put authoritative assets in `assets/logic` and client-only
content in `assets/presentation`. Architecture decisions and reviews belong in
`docs/`.

## Build, Test, and Development Commands

- `cargo check --workspace`: type-check every workspace package quickly.
- `cargo test --workspace`: run all unit and integration tests.
- `cargo fmt --all -- --check`: verify standard Rust formatting.
- `cargo clippy --workspace --all-targets -- -D warnings`: enforce lint-clean code.
- `cargo run -p minimal-game-client`: run the bounded client smoke example.
- `cargo run -p minimal-game-server`: run the continuous 60 Hz headless server;
  stop it with Ctrl+C.

## Coding Style & Naming Conventions

Use `rustfmt` defaults and four-space indentation. Name modules, functions, and
tests with `snake_case`; types and traits with `UpperCamelCase`; constants with
`SCREAMING_SNAKE_CASE`. The workspace forbids unsafe code and enables Clippy's
`all` lint group. Keep public contracts small, document lifecycle and ownership,
and prevent backend-specific types from leaking into engine-facing APIs.

## Testing Guidelines

Tests currently use Rust's built-in test framework in colocated `#[cfg(test)]`
modules. Give tests behavioral names such as
`headless_run_has_deterministic_stage_order`. Add tests beside the owning module;
use a package-level `tests/` directory only for true public-API integration tests.
Cover success, failure, shutdown, and deterministic timing paths. No numeric
coverage target is established, but every behavior change should have focused
regression coverage.

## Commit & Pull Request Guidelines

History is currently minimal, with `chore: establish Nico architecture skeleton`
as the available convention. Continue concise imperative subjects with a scoped
prefix such as `feat:`, `fix:`, `docs:`, `test:`, or `chore:`. Keep commits
focused. Pull requests should explain motivation, architectural impact, and
validation commands; link relevant issues and include screenshots only for
visible presentation changes. Do not mix unrelated formatting or generated-file
changes into a feature PR.
