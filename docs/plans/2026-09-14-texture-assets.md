# Texture loading and asset leases

Status: texture loading, leases, and the 2D sprite/HUD consumer implemented, 2026-09-14;
mesh loading and the 3D consumer are implemented in the
[mesh extension](2026-09-14-mesh-assets.md). Phase outcomes belong
in the [roadmap](../roadmap.md#3-load-game-assets); implementation actions belong in
[TODO](../../TODO.md#next-define-the-reference-game).

## First consumer and format

The paired samples start with one PNG used by a movable 2D world sprite and a fixed
HUD icon. The later 3D sample reuses texture loading and HUD drawing and adds a mesh.
PNG ships directly: read and decode it in the background, then publish owned decoded
pixels at the runtime service boundary. An intermediate raw-pixel file adds no useful
conversion for this first consumer. GPU-compressed offline formats remain a future
extension of the same asset API.

The first texture representation is dimensions plus RGBA8 pixels, sRGB color,
straight alpha, and one mip level. Static grayscale, RGB, RGBA, and indexed PNGs are
expanded; 16-bit channels are reduced to 8 bits. APNG is rejected. Color bytes are
interpreted as sRGB without ICC/gamma conversion.
PNG is a shipping input; separate authoring project files are not required at runtime.

## Identity and ownership

Keep the existing copyable `Handle<T>` as stable identity. Add a separate, cloneable
`AssetLease<T>` that keeps a loaded asset or pending request alive. A handle alone does
not retain content. The asset store owns CPU data; the renderer owns GPU resources.
Neither asset identity nor decoded texture data contains backend GPU types.

Requests for the same typed asset share one store entry and in-flight load. Each caller
receives a lease. Dropping one lease does not affect other consumers. Dropping the last
lease makes the entry eligible for release at a runtime-owned boundary, including
best-effort cancellation of pending work. Lease drops must not mutate the world or
block on a bounded request queue; the implementation must provide lossless release
accounting that the runtime reconciles.

At reconciliation, an entry with no owners is retired. Reacquisition before retirement
may retain the current entry; reacquisition afterward creates a new generation. Each
active load records its entry generation and service request identity. Retired or replaced
requests cannot publish into a new entry for the same asset ID.

## State, failure, and shutdown

An owned entry exposes loading, ready, or failed state. Ready means CPU content is
available, not that GPU upload or presentation has completed. Failed entries retain
structured errors while leased. Retry is explicit and replaces the request generation;
there is no automatic retry loop. Submission overload must produce an explicit result
without leaving an entry permanently loading.

The existing runtime service API provides bounded requests, cancellation flags, and
runtime-thread completion publication. Asset generations supplement its request
identity; cancellation alone is not sufficient to reject stale completion races.
Background readers/decoders operate on owned data and never receive the world.

Shutdown rejects new requests, cancels pending work, and prevents further publication.
Active file reads/decodes finish before the worker is joined;
cancellation is cooperative and does not imply immediate worker termination. Leases
may outlive the store without accessing destroyed state.

GPU retirement follows renderer ownership and outstanding rendering work. Releasing
CPU content must not invalidate an immutable presentation snapshot or a submitted GPU
operation. The render integration must explicitly retain what each needs.

## Implementation bounds

The `nico-assets/loading` feature depends on runtime, `png`, and the mesh extension's
`gltf` decoder. `TextureStore::install`
accepts a trusted content root and immutable ID/relative-path pairs. It rejects duplicate
IDs and absolute/parent paths. This is local content routing, not a security sandbox.
The catalog is host configuration; limits apply to requested resident entries.

Default limits are 64 entries, 16 MiB input files, 4096 per dimension, and 64 MiB each
for decoded/output buffers. The PNG decoder also receives a 64 MiB internal limit.
Buffers can coexist, so these are not a combined memory budget. The store serializes
dispatch through one worker and a capacity-one service. It consumes each completion
before dispatching another request, including after cancellation. This also works with
an event history capacity of one. Completion events contain no pixel data; the active
request owns a transferable mailbox that the runtime takes on publication.

Leases own an `Arc<()>` token; entries retain only a `Weak`. Runtime reconciliation
observes final release without a drop-time queue or world mutation. Ready content is an
`Arc<Texture>`: consumers may explicitly pin it past entry retirement. Shutdown and store
drop close the service and join the worker, including when the app is never started.

Renderer upload and the 2D world/HUD consumer are implemented. Presentation snapshots
pin immutable CPU textures while GPU cache entries retain weak CPU references and
provider resources. The provider retains submitted resource uses after wrapper release.
The headless loader sample still makes no GPU claims; rendering evidence belongs in
[phase 4](../roadmap.md#4-display-the-game-world).

Regression coverage must exercise shared requests, last-owner release, cancellation,
release/reacquisition and retry races, malformed and oversized files, overload, and
shutdown with outstanding requests and leases. Sample validation must distinguish CPU
readiness, GPU preparation, and successful presentation.
