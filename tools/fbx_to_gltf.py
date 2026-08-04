#!/usr/bin/env python3
"""Convert FBX assets to glTF 2.0 by running Blender in background mode."""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


def create_parser(*, include_blender: bool) -> argparse.ArgumentParser:
    """Create the command-line parser shared by the launcher and Blender worker."""
    parser = argparse.ArgumentParser(
        description="Convert one FBX file or a directory of FBX files with Blender."
    )
    parser.add_argument("input", type=Path, help="FBX file or directory to convert")
    parser.add_argument(
        "-o", "--output", type=Path,
        help="Output file for one input, or output directory (default: beside input)",
    )
    parser.add_argument(
        "--format", choices=("glb", "gltf"), default="glb",
        help="Write one binary GLB or separate glTF files (default: glb)",
    )
    parser.add_argument(
        "--recursive", action="store_true",
        help="Search input directories recursively and preserve relative directories",
    )
    parser.add_argument(
        "--overwrite", action="store_true", help="Replace existing output files"
    )
    parser.add_argument(
        "--keep-going", action="store_true",
        help="Continue after a file fails and return failure after the batch",
    )
    if include_blender:
        parser.add_argument(
            "--blender", type=Path,
            help="Blender executable (default: locate blender on PATH)",
        )
    return parser


def find_blender(requested: Path | None) -> str:
    """Resolve the Blender executable supplied by the user or available on PATH."""
    if requested is not None:
        executable = requested.expanduser().resolve()
        if executable.is_file():
            return str(executable)
        raise FileNotFoundError(f"Blender executable does not exist: {executable}")

    discovered = shutil.which("blender") or shutil.which("blender.exe")
    if discovered:
        return discovered
    raise FileNotFoundError(
        "Blender was not found on PATH. Install Blender or pass --blender PATH."
    )


def launcher_arguments(arguments: list[str]) -> int:
    """Launch Blender once and forward conversion arguments to the worker."""
    parser = create_parser(include_blender=True)
    options = parser.parse_args(arguments)
    try:
        blender = find_blender(options.blender)
    except FileNotFoundError as error:
        parser.error(str(error))

    forwarded = [str(options.input), "--format", options.format]
    if options.output is not None:
        forwarded.extend(("--output", str(options.output)))
    if options.recursive:
        forwarded.append("--recursive")
    if options.overwrite:
        forwarded.append("--overwrite")
    if options.keep_going:
        forwarded.append("--keep-going")
    command = [
        blender,
        "--background",
        "--factory-startup",
        "--python",
        str(Path(__file__).resolve()),
        "--",
        "--worker",
        *forwarded,
    ]
    return subprocess.run(command, check=False).returncode


def collect_jobs(options: argparse.Namespace) -> list[tuple[Path, Path]]:
    """Resolve input FBX files and deterministic output paths."""
    source = options.input.expanduser().resolve()
    extension = ".glb" if options.format == "glb" else ".gltf"
    if source.is_file():
        if source.suffix.lower() != ".fbx":
            raise ValueError(f"Input file must have an .fbx extension: {source}")
        if options.output is None:
            destination = source.with_suffix(extension)
        else:
            requested = options.output.expanduser().resolve()
            destination = (requested / (source.stem + extension)
                           if requested.is_dir() else requested)
            if destination.suffix.lower() not in (".glb", ".gltf"):
                destination = destination.with_suffix(extension)
        return [(source, destination)]

    if not source.is_dir():
        raise FileNotFoundError(f"Input does not exist: {source}")
    destination_root = (options.output.expanduser().resolve()
                        if options.output is not None else source)
    pattern = "**/*.fbx" if options.recursive else "*.fbx"
    inputs = sorted(source.glob(pattern), key=lambda path: str(path).lower())
    return [
        (item, destination_root / item.relative_to(source).with_suffix(extension))
        for item in inputs
    ]


def convert(source: Path, destination: Path, output_format: str) -> None:
    """Import one FBX scene and export it through Blender's glTF exporter."""
    import bpy  # type: ignore[import-not-found]

    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = bpy.ops.import_scene.fbx(filepath=str(source))
    if "FINISHED" not in result:
        raise RuntimeError(f"Blender could not import {source}")

    destination.parent.mkdir(parents=True, exist_ok=True)
    result = bpy.ops.export_scene.gltf(
        filepath=str(destination),
        export_format="GLB" if output_format == "glb" else "GLTF_SEPARATE",
        export_yup=True,
        export_apply=True,
    )
    if "FINISHED" not in result:
        raise RuntimeError(f"Blender could not export {destination}")


def worker(arguments: list[str]) -> int:
    """Execute conversions inside Blender's Python runtime."""
    parser = create_parser(include_blender=False)
    options = parser.parse_args(arguments)
    try:
        jobs = collect_jobs(options)
    except (FileNotFoundError, ValueError) as error:
        parser.error(str(error))
    if not jobs:
        print("No FBX files found.", file=sys.stderr)
        return 1

    failures = 0
    for source, destination in jobs:
        if destination.exists() and not options.overwrite:
            print(f"skip: {destination} already exists (use --overwrite)")
            continue
        try:
            print(f"convert: {source} -> {destination}")
            convert(source, destination, options.format)
        except Exception as error:  # Blender operators expose varied exception types.
            failures += 1
            print(f"error: {source}: {error}", file=sys.stderr)
            if not options.keep_going:
                return 1
    return 1 if failures else 0


def main() -> int:
    """Dispatch to the host launcher or the Blender-resident worker."""
    arguments = sys.argv[1:]
    if "--" in arguments:
        arguments = arguments[arguments.index("--") + 1:]
    if arguments and arguments[0] == "--worker":
        return worker(arguments[1:])
    return launcher_arguments(arguments)


if __name__ == "__main__":
    raise SystemExit(main())
