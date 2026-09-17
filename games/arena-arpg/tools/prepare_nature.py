"""Embed selected Nature glTF buffers/images into runtime GLBs; run from any directory."""
import json
from pathlib import Path
import struct

root = Path(__file__).resolve().parents[1] / 'assets/presentation'
source = root / 'quaternius/nature'
destination = root / 'worlds/nature'
destination.mkdir(parents=True, exist_ok=True)
names = ['CommonTree_1', 'Pine_1', 'Rock_Medium_1', 'Rock_Medium_2',
         'Bush_Common', 'Grass_Common_Short', 'Flower_3_Group', 'CommonTree_3',
         'TwistedTree_1', 'Fern_1', 'Mushroom_Common', 'Flower_4_Group',
         'Bush_Common_Flowers']
for name in names:
    data = json.loads((source / f'{name}.gltf').read_text(encoding='utf-8'))
    assert len(data['buffers']) == 1 and not data.get('extensionsRequired')
    def read_local(uri):
        path = (source / uri).resolve()
        assert path.parent == source.resolve()
        return path.read_bytes()
    blob = bytearray(read_local(data['buffers'][0]['uri']))
    assert len(blob) == data['buffers'][0]['byteLength']
    for image in data.get('images', []):
        raw = read_local(image.pop('uri'))
        blob.extend(b'\0' * (-len(blob) % 4))
        image['bufferView'] = len(data['bufferViews'])
        data['bufferViews'].append({'buffer': 0, 'byteOffset': len(blob), 'byteLength': len(raw)})
        blob.extend(raw)
    # The renderer uses core PBR textures and UV0, without vertex-color modulation.
    for mesh in data['meshes']:
        for primitive in mesh['primitives']:
            for key in list(primitive['attributes']):
                if key.startswith('COLOR_'):
                    del primitive['attributes'][key]
    data['buffers'] = [{'byteLength': len(blob)}]
    blob.extend(b'\0' * (-len(blob) % 4))
    metadata = json.dumps(data, separators=(',', ':')).encode()
    metadata += b' ' * (-len(metadata) % 4)
    output = struct.pack('<III', 0x46546C67, 2, 28 + len(metadata) + len(blob))
    output += struct.pack('<II', len(metadata), 0x4E4F534A) + metadata
    output += struct.pack('<II', len(blob), 0x004E4942) + blob
    (destination / f'{name}.glb').write_bytes(output)
    print(name)
(destination / 'License.txt').write_bytes((source / 'License_Standard.txt').read_bytes())
