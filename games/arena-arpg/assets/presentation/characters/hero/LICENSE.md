# Hero asset provenance

The original Ch03/RPG inputs are user-supplied third-party assets, copied byte-for-byte
on 2026-09-16 for local arena development. This document records provenance; it does not grant
a license or establish permission to redistribute the raw assets.

- `model.glb`: supplied as `tmp/Ch03_nonPBR.glb`, identified by the user as a
  Mixamo character.
- `animations/RPG-Character@Unarmed-*.glb`: six clips selected from the supplied
  `tmp/animations/` collection: Idle, Run-Forward, Attack-R1, Roll-Forward,
  GetHit-F1, and Death1.
- No original license files accompanied these inputs. Exact animation source and
  redistribution terms remain unverified; the workspace code license does not
  establish the license of these files.

The [asset provenance record](../../../../../../docs/plans/2026-09-16-character-assets.md)
owns source investigation and the outstanding attribution work. Retain the actual
upstream notices when that work is resolved, before public distribution.

## Library 2 attack migration (2026-09-17)

The current attack is `animations/UAL2-Sword_Regular_C.glb`, extracted from the
locally imported `quaternius/animation-library-2/UAL2_Standard.glb`.
Its CC0 notice is retained at `../../quaternius/animation-library-2/License.txt`.
Archive hashes are recorded in `../../quaternius/import-manifest.json`.
The source clip samples, skeleton hierarchy, and reference transforms are
preserved; other clips and mesh data are omitted.

Reproduce from the repository root:

```powershell
python apps/nico-character-preview/tools/extract_sword_clips.py games/arena-arpg/assets/presentation/quaternius/animation-library-2/UAL2_Standard.glb games/arena-arpg/assets/presentation/characters/hero/animations --clips Sword_Regular_C --prefix UAL2-
```

The Standard archive contains 43 clips, with no equivalent normal run, roll,
death, or sword-idle clip. Those motions retain previous assets. No hit-reaction
clip is selected: the game has no authoritative injury/stun state. The previous
sword attack file is retained as source
history but is no longer bound. The new attack's contact marker is 0.686666667 s
of a 2 s clip; authoritative damage timing and reach remain unchanged.

## Previous Quaternius sword selection (2026-09-17)

`animations/Quaternius-Sword_Attack.glb` and `Quaternius-Sword_Idle.glb` are derived
from **Universal Animation Library**, created by Quaternius. The publisher lists
the pack under [CC0 1.0](https://quaternius.com/packs/universalanimationlibrary.html).
The accompanying [CC0 notice](animations/Quaternius-CC0.txt) is retained verbatim.

Retrieved from the [glTF mirror](https://github.com/J-Ponzo/gltf-universal-animation-library)
at commit `e24c23cf2a1323488a3faa226ea7ea21f644b73e` (2025-06-10), whose README
identifies the original author and free Standard archive. The current official
itch.io download timed out in this environment; these are explicitly the older
export, not the publisher's 2026 elbow-fix/root-motion revision.

Source files under `glTF/`, SHA-256:

- `AnimationLibrary_Godot_Standard.gltf`: `0ff075c7ad6855c5c2c37a171592ee8f0d6ab2f58259e2be77a9b63dd8027765`
- `AnimationLibrary_Godot_Standard.bin`: `6e65377d81558333c4093dbb144a48fd19019343d82b1a3a7992a98ec0e0543c`

The repository's
[extractor](../../../../../../apps/nico-character-preview/tools/extract_sword_clips.py)
retains each selected clip's original samples, hierarchy, node indices and reference
transforms. It removes meshes and unused clips/buffers, and embeds the remaining
data in GLB. Reproduce from the repository root:

```sh
python apps/nico-character-preview/tools/extract_sword_clips.py SOURCE/glTF/AnimationLibrary_Godot_Standard.gltf games/arena-arpg/assets/presentation/characters/hero/animations
```

Output SHA-256:

- `Quaternius-Sword_Attack.glb`: `495120d20296b1478a5f8473d04990336a194a7913a9716ccde53d8118472ecf`
- `Quaternius-Sword_Idle.glb`: `a9161f0382c479de61515f4c1ceeabb784b7951fb52c61a0edf56f05ecbc87a2`

The older sword idle remains selected; Library 2 has replaced this attack.
Unused RPG and older attack files remain for historical comparison. This attribution does not resolve the separate
Ch03 or RPG provenance questions above.
