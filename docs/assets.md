# Assets and Inspector

## Identity and import

Every supported project source has adjacent metadata containing a stable `AssetId` and importer ID. Importers publish one or more artifacts identified by a stable key and runtime content type. They also declare dependencies, diagnostics, and optional browsable source objects.

Current authored/imported families include GLB meshes, skins, animations, materials and textures; PNG/JPEG textures; `.nmat` standard materials; `.nanimset` animation sets; collision meshes; and terrain.

The import pipeline stages output in an isolated directory and atomically publishes a fingerprinted generation. `RuntimeResourceManager` loads typed CPU resources through an `IAssetResolver`; renderers separately create and own GPU handles.

## Standard materials

`StandardMaterialAsset` is the only persistent standard-material model. It owns:

- linear base-color multiplier;
- optional base-color texture reference;
- metallic and roughness values;
- double-sided state.

`StandardMaterialAssetCodec` owns the `NMATL002` source/artifact contract. The codec does not read obsolete material versions. Texture sampling and other GPU state are added only when resolving a material for a renderer.

The base-color texture multiplies the base-color factor; it does not replace the factor. Referenced textures are declared as material import dependencies.

Create a material from the File System context menu or from the command line:

```bash
dotnet run --project tools/Engine.AssetTool -- material create path/to/material.nmat
```

## Inspector composition

The Inspector is a host, not a switch over asset types:

```text
selection
  -> InspectorProviderRegistry
  -> InspectorDocument with one UI content tree
  -> AssetEditorRegistry by runtime content type
  -> shared typed IAssetDocument
  -> reusable domain editor
```

Selecting a standalone `.nmat` and inspecting the same material through a mesh instance embed the same `StandardMaterialInspectorContent` concept and share one cached document. Standalone sources are editable; materials imported from a GLB are displayed read-only.

Documents resolve their current path dynamically, track dirty/error state, save atomically, reload, notify active views, and invalidate/reload runtime resources after persistence. Inspector content subscribes only while hosted.

## Asset-reference fields and drag/drop

`AssetReferenceField` declares an accepted runtime content type. `AssetDropResolver` handles both physical files and imported sub-assets:

- an imported row is accepted when its content type matches;
- a physical source is imported and its matching `main` artifact is preferred;
- when no `main` exists, exactly one matching artifact is required;
- ambiguous or mismatched drops are rejected.

This is shared infrastructure. New asset consumers should not add extension-specific drop branches.

## Extension checklist

To add a new editable asset family:

1. define persistent data and one authoritative codec below the renderer layer;
2. register an importer that publishes a stable content type and dependencies;
3. register the runtime resource loader;
4. implement an `IAssetDocumentFactory` for editable content;
5. implement one reusable `IAssetInspectorFactory` content component;
6. register editability by source importer when the source itself stores the artifact;
7. use typed `AssetReferenceField` controls for references;
8. add codec, import, document lifecycle, direct/embedded editor, and drag/drop tests.

Do not add legacy readers, scene-property duplicates, or per-consumer editors unless explicitly required.
