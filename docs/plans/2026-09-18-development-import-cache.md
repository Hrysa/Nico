# Development import cache

Implemented on 2026-09-18. This document owns the on-disk development cache contract.
Usage belongs in [README](../../README.md#development-asset-cache), engine ownership
in [architecture](../architecture.md).

## Scope and lifecycle

Debug-assertion builds use `nico_assets::cache::load_file` and `load_bytes` for arena
character/environment startup, character preview, and native asset-store workers.
Startup eagerly visits the game's configured models and embedded textures; stores
visit catalog sources when requested. This is not a recursive vendor-directory scan
or a live file watcher. Direct importer calls intentionally remain uncached.
Release loaders continue importing source content. No packaged release format is
introduced by this work.

The nearest ancestor named `assets` owns `.nico`; an external override without that
ancestor uses the source's parent. The source must exist and resolve inside its root.
Files outside the root and inside `.nico` cannot be indexed as sources. File loaders
compare source size, high-resolution modification time, creation time when available,
and (on Unix) device/inode and change-time metadata with the index before reading
source bytes. Matching metadata skips source reading and hashing entirely. A missing
stamp or changed metadata triggers bounded source reading and SHA-256 verification.
If content is unchanged, the loader reuses the object and refreshes the stamp without
reimporting. Unix identity/change time detects same-size edits with restored mtime
and replaced files. Non-Unix platforms use portable size/modified/created metadata;
this change has been validated on macOS, not on other platforms or filesystems.

Cached objects have independent metadata stamps. Unchanged objects skip integrity
rehashing, while changed or unknown metadata triggers full SHA-256 verification.
Object bytes must still be read and decoded to construct CPU assets. Stamps refresh
only after successful verification; a source modified during loading is not stamped
as verified. Existing indexes without stamps receive one full verification and then
use the quick path; existing binary objects need no conversion or forced reimport.

Explicit byte inputs, including embedded PNGs, have no independent filesystem stat.
Their quick check is unavailable, so the supplied encoded bytes require a content
hash. Their much larger decoded cache objects still use the metadata quick path.
The owning GLB's metadata is not sufficient proof that arbitrary supplied bytes
belong to that exact file version. GPU uploads, CPU asset validation, TOML parsing,
and procedural world generation remain unchanged.

## Format and invalidation

`index.json` contains a schema version and entries keyed by relative source path,
subresource name, importer ID, and recipe hash. Each entry records source hash, recipe hash,
object hash, decoded-byte charge, source input length, and optional source/object
metadata stamps. Embedded PNGs use their owning model path and
image index; their exact encoded bytes form the source hash. GLBs are self-contained;
external glTF buffers/images are still unsupported.

The recipe includes cache format, importer ID/version, a codec-specific schema and
serialized settings, target architecture, and both input/output budgets. Changing any of these causes a
fresh import. Multiple recipe variants coexist, so alternating texture budgets do
not repeatedly evict each other. Objects contain binary CPU data encoded with fixed-width bincode 1.3:
model builder data, static mesh attributes/indices, or RGBA8 dimensions/pixels. They
are addressed by SHA-256 as `objects/<first-two-hex>/<remaining-hex>`. The index is
bounded to 8 MiB; object reads and binary decoding are bounded. Hash mismatches,
missing objects, invalid decoded values, and invalid index versions rebuild from
source. Model/mesh/texture constructors validate restored data.

Per-recipe OS file locks coalesce identical imports across threads and processes.
A separate index lock serializes short read/merge/publication operations; source
parsing and binary decoding do not block unrelated recipes. Waiting
checks cancellation and has a 60-second timeout. The lock releases on process exit;
existing files in `locks/` or `index.lock` does not imply an active writer. Objects publish before
index entries using same-directory temporary files, file synchronization and rename.
A crash can leave an orphan object or temporary file, but not a partially published
index. This is a rebuildable cache, not a power-loss-durable database.

A reimport removes the previous entry before invoking the parser; import failure
cannot fall back to stale output. Missing source files fail loading. Subsequent
successful visits prune entries whose sources were deleted. Unrequested embedded
subresources and unreferenced objects may remain on disk; garbage collection is
not implemented. Deleting `.nico` with loaders stopped forces a clean rebuild.
Cache filesystem errors fail the load rather than reporting a cache success.

## Extension and observation

`AssetImporter::cache_settings` defaults to `None`, preserving uncached behavior for
existing custom importers. Opted-in importers supply `cache_encode` and `cache_decode`,
include all output-affecting settings and codec schema in the key, and validate
restored output. As with import callbacks, these are trusted native code. A custom
importer reading external dependencies must include their current content digests
in its settings key; automatic external dependency discovery is not implemented.

`ImportProgress` owns a scoped reporting thread for startup phases. Every three
seconds it prints `loading completed/total (label; cache hits N, imported N, shared N)`
through `tracing::info!`, using the host’s timestamps and log filter, even when a
single load blocks. Models/animations and referenced textures
have separate fixed totals. Model completion is counted immediately after import;
textures complete individually, including explicit in-memory reuse in scenery.
Environment models load first so the texture total is known before texture loading.
Successful phases print final counts immediately. Error unwinding stops and joins
the reporter without claiming completion. Progress cache counters observe the originating loading thread, so unrelated
concurrent loaders do not inflate a phase’s counts. The separate `cache::stats` API
continues to expose process-wide totals.

Warm-load investigation on 2026-09-18 confirmed the hero model plus three referenced
textures were four cache hits and zero imports. The old bundle log hid the texture
work at `5/6`. On the local macOS debug build, this headless sequence took about
17.2 seconds before correction and 1.2 seconds afterward (elapsed wall time, not CPU
profiling or full game startup). A 64 MiB SHA-256 operation fell from about 2.37 s
to 0.118 s after optimizing only the `sha2` dependency in the development profile.
The RGBA codec now copies pixels in bulk rather than deserializing each byte. Its
16-byte dimensions/length header and pixel payload are byte-compatible with existing
`rgba8-v1` objects, so this change does not force texture reimports. Integrity hashes remain the full-check fallback; dimensions, lengths, and budgets
remain checked on every restored object. Regression tests cover old-format
compatibility and invalid/truncated pixel data, as well as reporting and shutdown.

`cache::stats` exposes process-wide hit/import/rebuild counters plus `source_checks`
and `object_checks` for actual full content verifications (recipe hashing and newly
created object addressing are excluded). The headless
`inspect_model` example emits these in structured JSON, providing an automation path
without launching a game or modifying a running session. These are operational
counts, not CPU profiling or GPU completion evidence.

## Validation

Focused tests cover warm parser avoidance, content/settings/version invalidation,
stricter budgets, missing/corrupt objects and index, failed reimport, cancellation,
concurrent writers, deleted source pruning, PNG pixel preservation, and validated
model graph/skin/animation binary round trips. Native window rendering is not part
of this cache validation.

On the 2026-09-18 macOS development build, `cargo test --workspace`,
`cargo clippy --workspace --all-targets -- -D warnings`,
`cargo fmt --all -- --check`, and builds of `arena-arpg-client` and
`nico-character-preview` passed. The workspace test rerun needed local socket
permissions outside the sandbox. Cache-only and runtime-only feature configurations
also type-checked. A separate headless inspector process restored the real Imp model
with `hits: 1, imports: 0, rebuilds: 0`. The initial shared-lock implementation timed
out under parallel asset tests; the per-recipe/index-lock split resolved that failure
and the full parallel suite passed. Those original checks did not measure startup speed or native rendering; the
subsequent headless warm-load measurement is recorded above.

Quick-stat validation (2026-09-18, macOS): focused regressions verify zero full
checks on unchanged files, metadata-only changes followed by stamp refresh, preserved
mtime edits and replacement on Unix, corrupted-object recovery, and unstamped-index
migration. Two separate inspector processes loaded the real hero model: the first
reported one source check and one object check while upgrading its unstamped entry;
the next reported one hit, zero imports, zero source checks, and zero object checks.

Full CPU preparation follow-up (2026-09-18, macOS): an ignored headless test now
loads all three default characters and environment assets, then binds the meadow
zone to prepare procedural scenery. It does not open a window or connect a host:

```sh
cargo test -p arena-arpg-client startup_asset_preparation_measurement -- --ignored --nocapture
```

With warm objects, the initial run took 5.26 seconds (54 hits, zero imports). A bulk
byte-buffer codec for encoded images inside cached models reduced the same sequence
to 2.22 seconds: hero 0.194 s, grunt 0.112 s, brute 0.077 s, environment asset loading
0.245 s, and procedural scenery 1.586 s. These are elapsed wall times from local
runs, not CPU self-time or time to a displayed frame. The codec preserves the
existing bincode layout and supports JSON round trips. Regression coverage checks
legacy binary compatibility and truncation. The remaining procedural generation,
GPU initialization, and uploads are not eliminated by the import cache.
