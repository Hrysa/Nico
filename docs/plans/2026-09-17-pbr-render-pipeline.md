# PBR render pipeline

Status: implemented and validated within the scope below. The forward PBR material
path and Scene2D/UI separation have CPU, offscreen GPU, and native capture evidence.

The supplied render architecture report is the reference: retain extraction and
backend-neutral preparation, organize rendering by pass and material with static
and skinned geometry variants, separate camera-dependent Scene2D from UI/HUD,
and distinguish persistent resources from per-frame data. Its render graph,
shadow, TAA, and Forward+ examples describe future evolution rather than an
instruction to implement all those systems now.

## Intended implementation

`nico-assets` owns immutable geometry and material inputs. Preserve authored
normals, generate area-weighted normals for geometry without them, and carry
metallic/roughness material factors and texture semantics into presentation.
Material texture color spaces must distinguish base color/emissive from linear
normal, metallic/roughness, and occlusion data. Preserve glTF alpha modes,
cutoff, and double-sided behavior.

`nico-presentation` owns Scene3D camera/light/material snapshots, camera-dependent
Scene2D, and a separate screen-space UI collection. Model preparation preserves
material and geometry data without importing GPU types or runtime ownership.
World-anchored UI projects through the scene camera into UI coordinates.

`nico-render` validates views before acquiring a surface, prepares persistent
mesh/texture/material caches, and updates camera, lights, transforms, and skin
palettes per frame. Scene3D renders opaque/masked materials before sorted
transparent materials, with static/skinned variants inside each pass. PBR uses
a metallic/roughness microfacet BRDF, correct world-space normal transforms,
camera-dependent specular response, and explicit linear lighting/output handling.
Texture normal mapping must include a valid tangent frame. Transparent materials
test depth without writing it. Scene2D follows 3D and UI draws last, sharing the
canvas implementation while keeping camera and sorting semantics separate.

## Current frame procedure (2026-09-17)

The native 3D path follows
[`MeshRenderPipeline::render`](../../crates/nico-render/src/meshes.rs), with the
final two passes recorded by the shared
[`QuadRenderPipeline`](../../crates/nico-render/src/quads.rs). The sequence below
describes the implemented call order for one presentation frame.

```text
Game presentation extraction
├─ Scene3D: camera, lighting, meshes, materials, skin poses
├─ Scene2D: camera-dependent world quads
└─ UiScene: screen-space HUD, text, projected world anchors
                         ↓
Validate and prepare CPU draw data
  • Target size/format and resource limits
  • Cameras, lighting, materials, geometry, skin palettes
  • Model / normal / clip transforms and canvas geometry validation
  • Transparent draw order: back-to-front posed centroids
                         ↓
Acquire surface image
  └─ Zero-sized / timeout / occluded → skip frame
                         ↓
Prepare targets and GPU residency
  • Refresh 3D pipeline variants if surface format changed
  • Create / resize depth target
  • Write camera and lighting buffer
  • Retire unused cached resources
                         ↓
Prepare each 3D draw
  • Reuse / upload mesh and material textures and bindings
  • Write model / normal / clip transforms and tint
  • Write skin palette when present
                         ↓
3D opaque + masked pass
  • Clear color and depth
  • Static or skinned PBR variant
  • Depth testing + depth writes
                         ↓
3D transparent pass
  • Preserve color and depth
  • Static or skinned PBR variant
  • Back-to-front alpha blending
  • Depth testing, no depth writes
                         ↓
Submit 3D commands
                         ↓
Prepare shared canvas
  • Build world/UI quad vertices and texture batches
  • Refresh canvas pipeline if surface format changed
  • Reuse / upload textures and write vertex buffer
                         ↓
Scene2D pass
  • World quads transformed through Camera2D
  • Preserve color; no depth attachment
                         ↓
UI / HUD pass
  • Logical-pixel quads, drawn last
  • Preserve color; no depth attachment
                         ↓
Submit canvas commands
                         ↓
Present surface image
```

Persistent resources include mesh buffers, material bindings, and texture caches.
Camera/light, per-draw transform/tint, skin palette, and canvas vertex contents
are updated for the frame. Canvas batches preserve extraction order and never
cross the Scene2D/UI pass boundary. The renderer currently submits 3D and canvas
commands separately.

Both static and skinned geometry share metallic/roughness PBR shading with normal
mapping, directional light, diffuse ambient modulated by occlusion, and emissive.
Lighting is computed in linear space; the sRGB attachment encodes output. Pipeline
variants also account for alpha blending, double-sided materials, and mirrored
winding.

There is no depth prepass, shadow pass, HDR tone mapping, image-based lighting, or
render graph in this path. A successful presentation API call does not establish
GPU completion or display scanout.

## Acceptance evidence required

- Asset and presentation regressions for authored/generated normals, material
  factors, texture semantics, alpha modes, invalid data, and scene/UI separation.
- Renderer regressions for pass ordering, static/skinned parity, normal transforms,
  transparency sorting/depth policy, resource retirement, and frame validation.
- Generated Slang artifacts checked with `nico-shaderc --check`; workspace check,
  tests, formatting, and Clippy pass.
- Bounded GPU rendering/readback scenarios showing dielectric/metal and roughness
  variation, lit skinned geometry, normal maps, alpha mask/blend, and independent
  Scene2D/UI behavior. Record the tested adapter/platform and inspect captures.
- Native operations use discovered bridge instances; any user-watched success
  claim additionally requires the user's confirmation per repository guidelines.

## Current implementation boundary

The forward PBR path now consumes shared material assets with five texture slots,
linear/sRGB interpretation, factors, sampler wrapping/filtering at mip zero,
alpha modes and double-sided pipelines. Camera/light data is separate from draw
and material data. Static and skinned geometry share the fragment BRDF. Opaque
and masked passes precede sorted alpha blending. Imported model loaders decode
all referenced PNG material images; JPEG remains unsupported by those loaders.

GPU readback on macOS 26.6.2 / Apple M4 / Metal passed the eight opt-in renderer
checks, including roughness, colored metal specular, derivative normal mapping,
sRGB round-trip, alpha mask/opaque/blend, and lit affine skinning compared with
CPU geometry under scale/shear. Rough, smooth, metal, blended, and skinned captures
were inspected from `/tmp/nico-pbr-captures`. This is offscreen GPU evidence,
not native-window visibility or character-art acceptance. Tests can regenerate
PPM captures by setting `NICO_PBR_CAPTURE_DIR` when running the ignored GPU tests.
Shaders were generated with Slang 2026.18 for macOS ARM64.

Review regressions additionally verify complete back-face normal-map reversal
for both geometry variants and inverse-transpose skin normals independent of
common asset-unit scale. The singularity test is scale-relative; singular blends
retain the transformed-input fallback. See the [roadmap](../roadmap.md#7-complete-the-player-experience)
for the tested scales and validation evidence.

Separate Scene2D and UiScene extraction and render passes are implemented.
CPU regressions cover independent snapshot ownership/retirement, UI-only camera
independence, and clipped world-anchor projection. GPU readback verifies 3D then
Scene2D then UI, shared texture identity across passes, camera pan/zoom isolation,
and invalid UI rejection before acquisition. The combined-layer capture was
inspected from `/tmp/nico-pbr-captures/scene2d-ui.png`.

Transparent sorting now uses indexed-vertex centroids transformed by the current
skin pose and instance placement. Per-joint weighted centroids are cached in mesh
assets; static offsets and animated offsets have CPU and GPU regression cases.
Per-draw sorting does not resolve intersecting transparent surfaces.

Mirrored winding now follows the mesh node global transform determinant for both
geometry variants, including double-sided shading. GPU regressions cover mirrored
static/skinned materials across alpha modes. Model extraction checks affine node
matrices and computes determinant signs in f64. Pre-acquisition validation now
includes target format/extent, texture dimensions for all three scenes, geometry
bounds, and device binding requirements. A real-GPU fixture verifies that rejected
frames do not acquire, submit presentation, or change the prior rendered image;
skipped/device-error acquisitions can be followed by a valid render. CPU asset
references remain unpinned by residency, and replacement/removal affects readback
without stale material bindings. Shader-channel regressions additionally cover
linear metallic/roughness and occlusion, plus sRGB emissive textures.

Native capture acceptance completed on 2026-09-17 using the user-launched arena
client, PID 28028, bridge instance `24210-18d61c3b86506230-1`. The initial capture
failed while occluded; a bridge focus request was observed applied and presentation
resumed. Captures 2 and 3 produced 2560-by-1440 PNGs, preserved locally as
`/tmp/nico-pbr-captures/native/arena-overview.png` and `hero-close.png`. Inspection
showed the textured, shaded hero, weapon, arena geometry, and readable HUD; the
close view followed a run restart and camera change. A later client-state sample
confirmed camera command 1 and run 4, but had already reached death: it is not
the full-health capture's frame. The original camera was restored afterward.
These captures establish native rendered output, not desktop visibility or
user-observed approval. No production art-quality claim is made.

The current target is SDR linear lighting on RGBA8/BGRA8 sRGB attachments; HDR
tone mapping and image-based lighting are future extensions. Production PBR
fidelity is not claimed.
