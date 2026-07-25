#!/bin/bash
# Compile shaders from src/Shaders/ to SPIR-V
set -e

SHADER_DIR="$(dirname "$0")/src/Shaders"

echo "Compiling shaders..."
for vert in "$SHADER_DIR"/*.vert; do
    [ -f "$vert" ] || continue
    spv="${vert}.spv"
    echo "  $vert -> $spv"
    glslc "$vert" -o "$spv"
done

for frag in "$SHADER_DIR"/*.frag; do
    [ -f "$frag" ] || continue
    spv="${frag}.spv"
    echo "  $frag -> $spv"
    glslc "$frag" -o "$spv"
done

echo "Done."
