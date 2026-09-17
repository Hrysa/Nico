# First character asset selection

This is the original Ch03/RPG asset investigation and provenance record. Its
selection tables describe that inspection. Current bindings are defined by the
[character contract](2026-09-17-character-definitions.md) and the
[hero notice](../../games/arena-arpg/assets/presentation/characters/hero/LICENSE.md);
Bestiary additions are documented in the
[monster notice](../../games/arena-arpg/assets/presentation/characters/monsters/README.md).

The [model/animation contract](2026-09-16-model-animation.md) owns engine support;
the [character milestone](../roadmap.md#client-character-milestone) owns acceptance.
Original RPG source/license verification and visual polish remain in
[TODO](../../TODO.md#client-humanoid-models-and-animation).

## Supplied assets and provenance

On 2026-09-16 the user supplied `tmp/Ch03_nonPBR.glb` and 64 animation GLBs under
`tmp/animations/`, together with importer `.meta` sidecars. No license files were
included. The initial inspection used that staging directory; runtime copies now live under
the game's presentation assets.

The user identifies Ch03 as a Mixamo character and the animations as coming from
a GitHub user. The exact animation repository and its asset license are pending.
The [Adobe Mixamo FAQ](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html)
permits royalty-free use of characters and animations in personal, commercial,
and nonprofit projects, including games. This is not evidence of an open-source
license or blanket permission to redistribute the raw files in a public repository.
Record the animation repository and original asset attribution before finalizing
the redistribution decision. The `.meta` files contain importer identifiers and
settings, not provenance or license information.

**Source investigation:** The user could not recover the GitHub URL. A likely
upstream is Explosive LLC's
[RPG Animations GLB FREE](https://store.godotengine.org/asset/explosive-llc/rpg-character-animations-pack-free/):
the publisher lists 64 unarmed clips covering the same action categories and labels
the listing MIT. Its [FAQ](https://www.explosive.ws/pages/faq) permits commercial
use of its free assets. This is a candidate attribution, not a verified match to
the supplied bytes. The official archive download failed through the web tool,
so archive comparison and the packaged license notice remain unverified. Do not
invent a copyright notice or treat the separate converter's license as proof of
the local animation files' license.

## Structural inspection

### Arena content location update (2026-09-16)

The user authorized copying the selected model and six arena clips into
`games/arena-arpg/assets/presentation/characters/hero/`. `model.glb` is a byte-for-byte
copy of Ch03; `animations/` retains the six original clip filenames listed below.
The client and native MCP harness now use this folder by default; `--procedural-hero`
retains the original visuals and paired path overrides support experiments.
The original staging collection is unchanged. This local organization change does
not resolve the source/redistribution questions above. The game folder's
[provenance notice](../../games/arena-arpg/assets/presentation/characters/hero/LICENSE.md)
links back to this source investigation.

| Property | Ch03 | RPG animation files |
| --- | --- | --- |
| Files | 1 | 64 |
| Skin joints | 65, `mixamorig:*` | 53, `Motion` and `B_*` |
| Mesh | 16,340 vertices, 28,106 triangles | Same 1,256-vertex, 628-triangle mesh in every file |
| Images | Four embedded PNGs | None |
| Clips | One static two-sample pose | One clip per file |
| Sampling | STEP | LINEAR and STEP |
| External buffer/image references | None | None |
| Sparse accessors | None | None |

All 64 RPG files have matching joint names, parent relationships, local rest
transforms, inverse bind matrices, vertex attributes, and triangle indices. They
are structurally compatible with their included mesh. This does not establish
visual quality, correct export orientation, or compatibility with Ch03.

Ch03 and the RPG files use different joint hierarchies and rest transforms.
Renaming bones alone is insufficient. The initial inspection suggested matching
Mixamo clips or offline retargeting. The subsequent user direction on 2026-09-16
includes an engine-owned humanoid conversion layer in the first milestone, as
described below. Where converted clips are cached or baked remains a design choice.

The included RPG mesh is an alternative importer/animation fixture that avoids
retargeting. It has not been visually inspected or selected as the playable hero.

## Candidate clip mapping

Current paths are relative to the game's hero `animations/` directory. Durations are last minus first
sample time, rounded to three decimals; they are not gameplay action durations.
These candidates now have sampled visual evidence in the review linked above;
selection remains provisional until the recorded art-acceptance gaps are closed.

| Role | File | Duration (seconds) |
| --- | --- | --- |
| Idle | `Quaternius-Sword_Idle.glb` | 1.667 |
| Locomotion | `RPG-Character@Unarmed-Run-Forward.glb` | 0.800 |
| Melee | `Quaternius-Sword_Attack.glb` | 1.500 |
| Dodge | `RPG-Character@Unarmed-Roll-Forward.glb` | 0.867 |
| Hit reaction | `RPG-Character@Unarmed-GetHit-F1.glb` | 0.500 |
| Death | `RPG-Character@Unarmed-Death1.glb` | 1.300 |

The sword files were selected on 2026-09-17 from a pinned CC0 Quaternius mirror;
their [notice](../../games/arena-arpg/assets/presentation/characters/hero/LICENSE.md#previous-quaternius-sword-selection-2026-09-17)
records exact provenance, conversion and revision limitations. They replace the
original RPG idle/attack selection at runtime; those two original files remain
for comparison. The game supplies an explicit DEF-rig profile and Ch03 equipment
finger reference. Existing simulation timings and damage rules remain authoritative.

## Import implications

Both sets use JOINTS_0 and WEIGHTS_0 alongside positions, normals, and UVs. The
loader must preserve ancestor transforms: both armature roots have scale 0.01,
and Ch03 additionally has a root rotation. Do not infer final scene units or axes
from raw joint translation values alone.

The RPG clips share the internal name `Armature|Take 001|BaseLayer`, so use explicit
asset identities and role mappings rather than treating internal names as unique.
Their first sample is approximately 0.033333 seconds; playback needs a defined
time origin and endpoint policy. Run, forward roll, and death have nonconstant
`Motion` translations. Establish an in-place conversion policy that preserves
pose movement while leaving world displacement to simulation.

Material extensions include KHR_materials_specular for the RPG files and additionally
KHR_materials_ior for Ch03; none is declared required. The initial material subset
and fallback behavior must be explicit. Existing mesh-only loading cannot consume
either character as an animated asset.

## Humanoid conversion direction

The requested source-to-canonical-to-target CPU path is implemented through the
public import interface, generic model bundles, and `nico-animation`. Source
skinning joints are retained; userland profiles supply semantic mappings and
reference corrections. The initial body profiles now bridge the supplied RPG and
Mixamo rigs. The [model/animation contract](2026-09-16-model-animation.md) owns the
exact supported subset, budgets, mapping policies, and limitations.

The original mismatch still matters: clips cannot be copied directly onto Ch03.
The implementation performs explicit conversion and keeps unmapped finger/helper
nodes at their reference local poses. Native rendering and gameplay integration
are recorded in the roadmap. Full clip visual acceptance remains outstanding as
detailed in the review linked above.

## Validation evidence

The initial read-only Python inspection on Windows checked all 65 GLB headers and embedded
JSON/buffer data. Joint indices were in range, skin weights were finite and
nonnegative, and the maximum observed weight-sum error was approximately 1.27e-7.
Inverse bind counts matched joint counts. Animation input times were finite and
strictly increasing, with matching input/output counts for the observed LINEAR
and STEP samplers. All 195 Ch03 channels contained constant values.

The generated local inventory at `target/character-asset-audit/inventory.json`
records SHA-256 file hashes, structural signatures, counts, clip ranges, and root
translation samples. It is an ignored inspection artifact, not a shipping manifest
or a complete glTF validator report. No native rendering, skin deformation, visual
clip review, or engine loading was tested during that initial inspection.

Subsequent engine import and CPU retargeting validation on the same files is
recorded in the [roadmap](../roadmap.md#client-character-milestone).
