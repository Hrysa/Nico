"""Prepare local Bestiary GLBs for Nico's base-color, single-UV renderer.

Run from any directory. Originals and license notices remain in quaternius/.
Only metadata changes: binary geometry, skinning, images, and rig stay intact.
"""
import json
from pathlib import Path
import struct

assets = Path(__file__).resolve().parents[1] / 'assets/presentation'
destination = assets / 'characters/monsters'
destination.mkdir(parents=True, exist_ok=True)
for name in ['Imp', 'Puglin']:
    source = assets / 'quaternius/bestiary' / f'{name}.glb'
    raw = source.read_bytes()
    size, kind = struct.unpack_from('<II', raw, 12)
    assert kind == 0x4E4F534A
    document = json.loads(raw[20:20 + size])
    assert not document.get('extensionsRequired')
    assert document.get('extensionsUsed', []) == ['KHR_materials_emissive_strength']
    document.pop('extensionsUsed')
    for material in document['materials']:
        material.pop('extensions', None)
        for value in material.values():
            if isinstance(value, dict) and 'index' in value:
                assert value.get('texCoord', 0) == 0
        for value in material.get('pbrMetallicRoughness', {}).values():
            if isinstance(value, dict) and 'index' in value:
                assert value.get('texCoord', 0) == 0
    for mesh in document['meshes']:
        for primitive in mesh['primitives']:
            attributes = primitive['attributes']
            for key in list(attributes):
                if (key.startswith('TEXCOORD_') and key != 'TEXCOORD_0') or key.startswith('COLOR_'):
                    del attributes[key]
    metadata = json.dumps(document, separators=(',', ':')).encode()
    metadata += b' ' * (-len(metadata) % 4)
    binary_chunk = raw[20 + size:]
    output = struct.pack('<III', 0x46546C67, 2, 20 + len(metadata) + len(binary_chunk))
    output += struct.pack('<II', len(metadata), 0x4E4F534A) + metadata + binary_chunk
    (destination / f'{name}.glb').write_bytes(output)
    print(f'{name}: prepared single-UV/core-material GLB')
