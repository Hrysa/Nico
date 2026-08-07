#!/usr/bin/env bash
set -euo pipefail

readonly REQUIRED_SLANG_VERSION="2026.5.2"
readonly PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SHADER_DIRECTORY="$PROJECT_ROOT/src/Shaders"

SLANG_COMPILER="${SLANGC:-}"
if [[ -z "$SLANG_COMPILER" ]]; then
    if [[ -x "$PROJECT_ROOT/tools/slang-$REQUIRED_SLANG_VERSION/bin/slangc.exe" ]]; then
        SLANG_COMPILER="$PROJECT_ROOT/tools/slang-$REQUIRED_SLANG_VERSION/bin/slangc.exe"
    elif [[ -x "$PROJECT_ROOT/tools/slang-$REQUIRED_SLANG_VERSION/bin/slangc" ]]; then
        SLANG_COMPILER="$PROJECT_ROOT/tools/slang-$REQUIRED_SLANG_VERSION/bin/slangc"
    fi
fi
if [[ -z "$SLANG_COMPILER" ]]; then
    SLANG_COMPILER="$(command -v slangc || true)"
fi

if [[ -z "$SLANG_COMPILER" || ! -x "$SLANG_COMPILER" ]]; then
    echo "slangc $REQUIRED_SLANG_VERSION is required." >&2
    echo "Set SLANGC to the compiler path or add slangc to PATH." >&2
    exit 1
fi

SLANG_VERSION="$($SLANG_COMPILER -version 2>&1)"
if [[ "$SLANG_VERSION" != "$REQUIRED_SLANG_VERSION" ]]; then
    echo "Expected slangc $REQUIRED_SLANG_VERSION, found $SLANG_VERSION." >&2
    exit 1
fi

compile_shader() {
    local source_file="$1"
    local entry_point="$2"
    local stage="$3"
    local output_file="$4"

    "$SLANG_COMPILER" \
        "$SHADER_DIRECTORY/$source_file" \
        -entry "$entry_point" \
        -stage "$stage" \
        -target spirv \
        -profile glsl_450 \
        -matrix-layout-row-major \
        -O2 \
        -o "$SHADER_DIRECTORY/$output_file"
}

compile_shader basic.slang vertexMain vertex basic.vert.spv
compile_shader basic.slang fragmentMain fragment basic.frag.spv
compile_shader grid.slang vertexMain vertex grid.vert.spv
compile_shader grid.slang fragmentMain fragment grid.frag.spv
compile_shader texture.slang vertexMain vertex texture.vert.spv
compile_shader texture.slang fragmentMain fragment texture.frag.spv
compile_shader model.slang vertexMain vertex model.vert.spv
compile_shader model.slang fragmentMain fragment model.frag.spv

echo "Compiled shaders with $SLANG_COMPILER"
