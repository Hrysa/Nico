#!/bin/bash
# Game Engine run script
# Sets up Vulkan environment automatically on macOS

if [[ "$OSTYPE" == "darwin"* ]]; then
    export VK_ICD_FILENAMES="${VK_ICD_FILENAMES:-/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json}"
    export DYLD_FALLBACK_LIBRARY_PATH="${DYLD_FALLBACK_LIBRARY_PATH:-/opt/homebrew/lib:/usr/local/lib}"
fi

if [[ $# -ne 1 ]]; then
    echo "Usage: ./run.sh <game-project-root>" >&2
    exit 2
fi

dotnet build src/Editor/Editor.csproj

exec src/Editor/bin/Debug/net11.0/Editor "$1"
