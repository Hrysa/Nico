"""Extract Quaternius Sword_Attack/Sword_Idle as self-contained skeleton GLBs.

Input: AnimationLibrary_Godot_Standard.gltf and its adjacent .bin from the
CC0 Universal Animation Library. Output retains original animation samples,
node indices, hierarchy and reference transforms; strips mesh and other clips.
Run: python apps/nico-character-preview/tools/extract_sword_clips.py INPUT OUTPUT_DIR
Also accepts a GLB, --clips NAME [NAME ...], and --prefix PREFIX for Library 2.
"""
import argparse
import copy
import json
from pathlib import Path
import struct


def extract(source: Path, destination: Path, names=None, prefix="Quaternius-") -> None:
    if source.suffix.lower() == ".glb":
        raw = source.read_bytes()
        magic, version, size = struct.unpack_from("<III", raw)
        if (magic, version, size) != (0x46546C67, 2, len(raw)):
            raise ValueError("invalid GLB header")
        length, kind = struct.unpack_from("<II", raw, 12)
        if kind != 0x4E4F534A:
            raise ValueError("expected JSON chunk")
        data = json.loads(raw[20:20 + length])
        offset = 20 + length
        length, kind = struct.unpack_from("<II", raw, offset)
        if kind != 0x004E4942 or offset + 8 + length != len(raw):
            raise ValueError("expected binary chunk")
        blob = raw[offset + 8:offset + 8 + length]
    else:
        data = json.loads(source.read_text(encoding="utf-8"))
        buffer_path = (source.parent / data["buffers"][0]["uri"]).resolve()
        if buffer_path.parent != source.parent.resolve():
            raise ValueError("buffer must be adjacent to source")
        blob = buffer_path.read_bytes()
    if len(data["buffers"]) != 1:
        raise ValueError("expected one buffer")
    if not 0 <= len(blob) - data["buffers"][0]["byteLength"] <= 3:
        raise ValueError("buffer size mismatch")
    destination.mkdir(parents=True, exist_ok=True)
    for name in names or ("Sword_Attack", "Sword_Idle"):
        if Path(prefix + name).name != prefix + name or any(c in prefix + name for c in '\\/:'):
            raise ValueError("clip output must be a filename")
        matches = [a for a in data["animations"] if a["name"] == name]
        if len(matches) != 1:
            raise ValueError(f"expected one {name} clip")
        animation = copy.deepcopy(matches[0])
        result = {k: copy.deepcopy(data[k]) for k in ("asset", "scene", "scenes", "nodes")}
        for node in result["nodes"]:
            node.pop("mesh", None)
            node.pop("skin", None)
        result.update(animations=[animation], accessors=[], bufferViews=[])
        output = bytearray()
        remap = {}
        for sampler in animation["samplers"]:
            for key in ("input", "output"):
                old = sampler[key]
                if old not in remap:
                    accessor = copy.deepcopy(data["accessors"][old])
                    if "sparse" in accessor:
                        raise ValueError("sparse accessors are unsupported")
                    view = copy.deepcopy(data["bufferViews"][accessor["bufferView"]])
                    if view.get("buffer", 0) != 0:
                        raise ValueError("unexpected buffer")
                    offset, size = view.get("byteOffset", 0), view["byteLength"]
                    if offset < 0 or size < 0 or offset + size > len(blob):
                        raise ValueError("invalid buffer view")
                    output.extend(b"\0" * (-len(output) % 4))
                    view["byteOffset"] = len(output)
                    output.extend(blob[offset:offset + size])
                    accessor["bufferView"] = len(result["bufferViews"])
                    result["bufferViews"].append(view)
                    remap[old] = len(result["accessors"])
                    result["accessors"].append(accessor)
                sampler[key] = remap[old]
        output.extend(b"\0" * (-len(output) % 4))
        result["buffers"] = [{"byteLength": len(output)}]
        metadata = json.dumps(result, separators=(",", ":")).encode("utf-8")
        metadata += b" " * (-len(metadata) % 4)
        glb = struct.pack("<III", 0x46546C67, 2, 28 + len(metadata) + len(output))
        glb += struct.pack("<II", len(metadata), 0x4E4F534A) + metadata
        glb += struct.pack("<II", len(output), 0x004E4942) + output
        (destination / f"{prefix}{name}.glb").write_bytes(glb)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--clips", nargs="+")
    parser.add_argument("--prefix", default="Quaternius-")
    args = parser.parse_args()
    extract(args.source, args.destination, args.clips, args.prefix)
