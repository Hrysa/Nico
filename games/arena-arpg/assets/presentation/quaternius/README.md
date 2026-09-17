# Quaternius source assets

Extracted from the locally downloaded Standard ZIPs on 2026-09-17, then cleaned
to one flat directory per pack. All 141 FBX files were removed; 20 byte-identical
Nature texture duplicates were merged. GLB, glTF, BIN, OBJ, MTL, Blender source,
textures, setup images, and license files remain. OBJ material texture paths now
refer to adjacent files instead of the exporter's absolute paths. The mannequin's
readme is named `Mannequin_F_README.txt` to preserve both animation readmes.

`import-manifest.json` retains original archive SHA-256 hashes and extraction
counts, plus a separate cleanup record with current counts and per-file hashes.
There are 327 versioned pack files after cleanup; Windows `desktop.ini` metadata
is excluded. Model and texture bytes are unchanged;
only MTL texture references were edited.

| Folder | Source | Included license |
| --- | --- | --- |
| `bestiary` | [Bestiary Dungeon Monsters Kit](https://quaternius.itch.io/bestiary-dungeon-monsters-kit) | `bestiary/License_Standard.txt` (QAL v1.0) |
| `animation-library-2` | [Universal Animation Library 2](https://quaternius.itch.io/universal-animation-library-2) | `animation-library-2/License.txt` (CC0 1.0) |
| `nature` | [Stylized Nature MegaKit](https://quaternius.itch.io/stylized-nature-megakit) | `nature/License_Standard.txt` (CC0 1.0) |

The downloaded Bestiary Standard archive contains Imp and Puglin, rather than all
seven monsters advertised for the complete pack. Nature Standard contains 68 of
the full pack's 116 models, according to its included license note.

The hero uses an extracted Library 2 `Sword_Regular_C` clip; other hero motions
retain compatible fallbacks. See `../characters/hero/LICENSE.md` for reproduction
and selection details. Bestiary runtime copies and extracted monster animations
are documented in `../characters/monsters/README.md`; both monster types are used
in world and arena rendering. Nature is not yet connected to world rendering.
Bestiary retains its own QAL terms, including the
restriction on redistribution as standalone assets; it is not CC0 or covered by
the repository's code license.
