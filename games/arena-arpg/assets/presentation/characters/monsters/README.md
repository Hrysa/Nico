# Bestiary monster presentation

`grunt.char-vis.toml` selects Imp at 1.65 m; `brute.char-vis.toml` selects Puglin at
2.05 m. Both use the `ual2` humanoid mapping and the weapons already skinned into
their model. No separate procedural sword is attached.

The runtime GLBs derive from `../../quaternius/bestiary/`. Reproduce them from
the repository root with:

```powershell
python games/arena-arpg/tools/prepare_bestiary.py
python apps/nico-character-preview/tools/extract_sword_clips.py games/arena-arpg/assets/presentation/quaternius/animation-library-2/UAL2_Standard.glb games/arena-arpg/assets/presentation/characters/monsters --clips Zombie_Idle_Loop Zombie_Walk_Fwd_Loop Sword_Regular_C --prefix UAL2-
```

Preparation removes unsupported vertex-color and secondary-UV bindings, plus
the optional emissive-strength extension. Geometry, skin weights, skeleton,
primary UVs, and embedded image bytes are retained. Nico currently displays
base-color textures; normal maps, emissive glow, and full PBR shading are not
rendered. Original source packs remain intact.

Idle, pursuit, and attack use Library 2 clips. Death uses the existing RPG
`Death1` fallback. The five-motion character contract also binds the RPG roll to
dodge, although current monster logic does not dodge. Animation only follows
owned simulation snapshots; it does not drive collision or damage. Attack
contact uses a provisional 0.686666667 s marker with authoritative windup/active/
recovery timing; per-monster weapon-contact and foot-contact polish remain.

Hit reactions are not bound or inferred from health decreases. Combat logic has
no injury/stun state; nonlethal damage leaves action and movement playback intact.

Models remain under **Quaternius Asset License v1.0**, retained in
`Bestiary-License.txt`; they are not CC0. Library 2 clips are CC0, retained in
`UAL2-License.txt`. The fallback clip's provenance remains in `../hero/LICENSE.md`.
The source archive hashes are in `../../quaternius/import-manifest.json`.
