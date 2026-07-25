#!/bin/bash
# Game Engine run script
# Sets up Vulkan environment automatically on macOS

if [[ "$OSTYPE" == "darwin"* ]]; then
    export VK_ICD_FILENAMES="${VK_ICD_FILENAMES:-/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json}"
    export DYLD_FALLBACK_LIBRARY_PATH="${DYLD_FALLBACK_LIBRARY_PATH:-/opt/homebrew/lib:/usr/local/lib}"
fi

dotnet run --project src/Editor "$@"
