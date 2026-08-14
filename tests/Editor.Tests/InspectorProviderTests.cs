using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using System.Numerics;
using Xunit;

namespace Editor.Tests;

public sealed class InspectorProviderTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nico-inspector-provider-").FullName;

    /// <summary>Describes supported assets with persistent importer metadata.</summary>
    [Fact]
    public void FileSystemProvider_SupportedFile_ProvidesAssetProperties()
    {
        var path = Path.Combine(_directory, "Ground.nmat");
        File.WriteAllBytes(path, [1, 2, 3]);
        var database = new AssetDatabase(_directory, EditorAssetImporters.Select);
        var provider = new FileSystemInspectorProvider(database);

        var accepted = provider.TryCreate(new FileSystemNode(path, false), out var document);

        Assert.True(accepted);
        Assert.NotNull(document);
        var content = Assert.IsType<PropertyInspectorContent>(document.Content);
        Assert.Contains(content.Children.OfType<TextField>(),
            field => field.Text == "standard-material");
        Assert.Contains(content.Children.OfType<Label>(), label => label.Text == "Asset ID");
    }

    /// <summary>Describes unsupported physical files through the generic fallback.</summary>
    [Fact]
    public void FileSystemProvider_UnsupportedFile_StillProvidesFileProperties()
    {
        var path = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(path, "hello");
        var database = new AssetDatabase(_directory, EditorAssetImporters.Select);
        var registry = new InspectorProviderRegistry();
        registry.Register(new FileSystemInspectorProvider(database));

        var accepted = registry.TryCreate(new FileSystemNode(path, false), out var document);

        Assert.True(accepted);
        Assert.NotNull(document);
        var content = Assert.IsType<PropertyInspectorContent>(document.Content);
        Assert.Contains(content.Children.OfType<TextField>(), field => field.Text == "Unsupported");
    }

    /// <summary>Loads editable material content and persists changed numeric values.</summary>
    [Fact]
    public void StandardMaterialProvider_EditProperty_SavesMaterialContent()
    {
        var path = Path.Combine(_directory, "Ground.nmat");
        MaterialAuthoring.Save(path, new StandardMaterialAsset
        {
            BaseColor = Vector4.One,
            Metallic = 0.2f,
            Roughness = 0.8f
        });
        var saved = false;
        var database = new AssetDatabase(_directory, EditorAssetImporters.Select);
        var documents = new AssetDocumentService(_ => saved = true);
        documents.Register(new StandardMaterialDocumentFactory());
        var editableSources = new EditableAssetSourceRegistry();
        editableSources.Register("nico/standard-material", "standard-material");
        var editors = new AssetEditorRegistry(documents, _ =>
        {
            var record = database.FindByPath(path)!;
            return new ResolvedAssetDocument(
                "nico/standard-material",
                new AssetDocumentLocation(
                    new AssetReference(record.Id, "main"),
                    () => path,
                    () => File.OpenRead(path),
                    write => AssetDocumentStorage.WriteAtomic(path, write)));
        }, reference => reference.ToString());
        editors.Register(new StandardMaterialInspectorFactory());
        var provider = new AssetContentInspectorProvider(database, editors, () => 300f);

        Assert.True(provider.TryCreate(new FileSystemNode(path, false), out var document));
        Assert.IsType<StandardMaterialInspectorContent>(document!.Content);
        var record = database.FindByPath(path);
        Assert.NotNull(record);
        var location = new AssetDocumentLocation(
            new AssetReference(record.Id, "main"), () => path, () => File.OpenRead(path),
            write => AssetDocumentStorage.WriteAtomic(path, write));
        var materialDocument = Assert.IsType<StandardMaterialDocument>(
            documents.GetOrLoad(location, "nico/standard-material"));
        materialDocument.Value.Metallic = 0.65f;
        materialDocument.Save();

        using var stream = File.OpenRead(path);
        var material = StandardMaterialAssetCodec.Load(stream);
        Assert.Equal(0.65f, material.Metallic);
        Assert.True(saved);
    }

    /// <summary>Reuses one document for direct and embedded views of the same material.</summary>
    [Fact]
    public void AssetDocumentService_SameReference_ReturnsSharedMaterialDocument()
    {
        var path = Path.Combine(_directory, "Shared.nmat");
        MaterialAuthoring.SaveDefault(path);
        var reference = new AssetReference(AssetId.New(), "main");
        var documents = new AssetDocumentService(_ => { });
        documents.Register(new StandardMaterialDocumentFactory());

        var location = CreateEditableLocation(reference, path);
        var direct = documents.GetOrLoad(location, "nico/standard-material");
        var embedded = documents.GetOrLoad(location, "nico/standard-material");

        Assert.Same(direct, embedded);
    }

    /// <summary>Uses the current source location after a project asset is renamed while open.</summary>
    [Fact]
    public void StandardMaterialDocument_RenamedSource_SavesCurrentPath()
    {
        var oldPath = Path.Combine(_directory, "Before.nmat");
        var currentPath = oldPath;
        MaterialAuthoring.SaveDefault(oldPath);
        var reference = new AssetReference(AssetId.New(), "main");
        StandardMaterialAsset value;
        using (var stream = File.OpenRead(oldPath))
            value = StandardMaterialAssetCodec.Load(stream);
        var location = new AssetDocumentLocation(reference,
            () => Path.GetFileName(currentPath), () => File.OpenRead(currentPath),
            write => AssetDocumentStorage.WriteAtomic(currentPath, write));
        var document = new StandardMaterialDocument(location, value, _ => { });
        currentPath = Path.Combine(_directory, "After.nmat");
        File.Move(oldPath, currentPath);

        document.Value.Metallic = 0.7f;
        document.MarkDirty();
        document.Save();

        Assert.Equal("After.nmat", document.DisplayName);
        using var saved = File.OpenRead(currentPath);
        Assert.Equal(0.7f, StandardMaterialAssetCodec.Load(saved).Metallic);
    }

    /// <summary>Keeps multiple material editor hosts synchronized through one shared document.</summary>
    [Fact]
    public void StandardMaterialContent_SharedDocument_SynchronizesEditors()
    {
        var path = Path.Combine(_directory, "Synchronized.nmat");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var source = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(source);
        var reference = new AssetReference(AssetId.New(), "main");
        var document = new StandardMaterialDocument(
            CreateEditableLocation(reference, path), resource, _ => { });
        var direct = new StandardMaterialInspectorContent(300f, document,
            reference => reference.ToString());
        var embedded = new StandardMaterialInspectorContent(300f, document,
            reference => reference.ToString());
        direct.Activate();
        embedded.Activate();

        document.Value.Roughness = 0.35f;
        document.Save();

        Assert.Equal("0.35", Assert.IsType<TextField>(
            FindByName<TextField>(direct, "MaterialRoughness")).Text);
        Assert.Equal("0.35", Assert.IsType<TextField>(
            FindByName<TextField>(embedded, "MaterialRoughness")).Text);
    }

    /// <summary>Stops document notifications while reusable Inspector content is detached.</summary>
    [Fact]
    public void StandardMaterialContent_Deactivate_UnsubscribesDocument()
    {
        var path = Path.Combine(_directory, "Detached.nmat");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var stream = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(stream);
        var reference = new AssetReference(AssetId.New(), "main");
        var document = new StandardMaterialDocument(
            CreateEditableLocation(reference, path), resource, _ => { });
        var content = new StandardMaterialInspectorContent(
            300f, document, value => value.ToString());
        content.Activate();
        content.Deactivate();

        document.Value.Roughness = 0.2f;
        document.MarkDirty();

        Assert.Equal("0.5", Assert.IsType<TextField>(
            FindByName<TextField>(content, "MaterialRoughness")).Text);
    }

    /// <summary>Assigns a texture through the common material asset-reference field.</summary>
    [Fact]
    public void StandardMaterialContent_TextureField_UpdatesSharedDocument()
    {
        var path = Path.Combine(_directory, "Textured.nmat");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var source = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(source);
        var reference = new AssetReference(AssetId.New(), "main");
        var document = new StandardMaterialDocument(
            CreateEditableLocation(reference, path), resource, _ => { });
        var content = new StandardMaterialInspectorContent(
            300f, document, reference => reference.ToString());
        var baseColorTexture = new AssetReference(AssetId.New(), "base");
        var normalTexture = new AssetReference(AssetId.New(), "normal");
        var metallicRoughnessTexture = new AssetReference(AssetId.New(), "metal-rough");
        var baseColorField = Assert.IsType<AssetReferenceField>(
            FindByName<AssetReferenceField>(content, "MaterialBaseColorTexture"));
        var normalField = Assert.IsType<AssetReferenceField>(
            FindByName<AssetReferenceField>(content, "MaterialNormalTexture"));
        var metallicRoughnessField = Assert.IsType<AssetReferenceField>(
            FindByName<AssetReferenceField>(content, "MaterialMetallicRoughnessTexture"));

        Assert.Equal("nico/texture2d", baseColorField.AcceptedContentType);
        Assert.True(baseColorField.Assign(baseColorTexture));
        Assert.True(normalField.Assign(normalTexture));
        Assert.True(metallicRoughnessField.Assign(metallicRoughnessTexture));
        Assert.Equal(baseColorTexture, document.Value.BaseColorTexture);
        Assert.Equal(normalTexture, document.Value.NormalTexture);
        Assert.Equal(metallicRoughnessTexture, document.Value.MetallicRoughnessTexture);
        using var stream = File.OpenRead(path);
        var saved = StandardMaterialAssetCodec.Load(stream);
        Assert.Equal(baseColorTexture, saved.BaseColorTexture);
        Assert.Equal(normalTexture, saved.NormalTexture);
        Assert.Equal(metallicRoughnessTexture, saved.MetallicRoughnessTexture);
    }

    /// <summary>Edits and persists base color through the shared color picker.</summary>
    [Fact]
    public void StandardMaterialContent_ColorPicker_UpdatesSharedDocument()
    {
        var path = Path.Combine(_directory, "Colored.nmat");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var source = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(source);
        var reference = new AssetReference(AssetId.New(), "main");
        var document = new StandardMaterialDocument(
            CreateEditableLocation(reference, path), resource, _ => { });
        var content = new StandardMaterialInspectorContent(
            300f, document, value => value.ToString());
        var picker = Assert.IsType<ColorPicker>(
            FindByName<ColorPicker>(content, "MaterialBaseColor"));
        var expected = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);

        picker.Value = expected;

        Assert.Equal(expected, document.Value.BaseColor);
        using var savedSource = File.OpenRead(path);
        Assert.Equal(expected, StandardMaterialAssetCodec.Load(savedSource).BaseColor);
    }

    /// <summary>Edits and persists terrain-layer color through the shared color picker.</summary>
    [Fact]
    public void TerrainLayerContent_ColorPicker_UpdatesSharedDocument()
    {
        var path = Path.Combine(_directory, "Colored.nterrainlayer");
        TerrainMaterialAuthoring.SaveDefaultLayer(path);
        TerrainLayerAsset resource;
        using (var source = File.OpenRead(path))
            resource = TerrainMaterialAssetCodec.LoadLayer(source);
        var reference = new AssetReference(AssetId.New(), "main");
        var document = new TerrainLayerDocument(
            CreateEditableLocation(reference, path), resource, _ => { });
        var content = new TerrainLayerInspectorContent(
            300f, document, value => value.ToString());
        var picker = Assert.IsType<ColorPicker>(
            FindByName<ColorPicker>(content, "TerrainLayerColor"));
        var expected = new Vector4(0.8f, 0.6f, 0.4f, 0.2f);

        picker.Value = expected;

        Assert.Equal(expected, document.Value.BaseColor);
        using var savedSource = File.OpenRead(path);
        Assert.Equal(expected, TerrainMaterialAssetCodec.LoadLayer(savedSource).BaseColor);
    }

    /// <summary>Keeps imported material content visible while disabling every mutating control.</summary>
    [Fact]
    public void StandardMaterialContent_ReadOnlyArtifact_DisablesEditing()
    {
        var path = Path.Combine(_directory, "Imported.material");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var stream = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(stream);
        var reference = new AssetReference(AssetId.New(), "material/0");
        var location = new AssetDocumentLocation(reference, () => "Imported / material/0",
            () => File.OpenRead(path));
        var document = new StandardMaterialDocument(location, resource, _ => { });
        var content = new StandardMaterialInspectorContent(
            300f, document, value => value.ToString());
        content.Activate();

        Assert.True(Assert.IsType<TextField>(
            FindByName<TextField>(content, "MaterialMetallic")).IsReadOnly);
        Assert.False(Assert.IsType<ColorPicker>(
            FindByName<ColorPicker>(content, "MaterialBaseColor")).IsEnabled);
        Assert.False(Assert.IsType<AssetReferenceField>(
            FindByName<AssetReferenceField>(content, "MaterialBaseColorTexture")).IsEnabled);
        Assert.Null(FindByName<Button>(content, "MaterialBaseColorTextureClear"));
        Assert.Equal("Read-only imported material", Assert.IsType<Label>(
            FindByName<Label>(content, "MaterialDocumentStatus")).Text);
    }

    /// <summary>Reflows shared material controls when their Inspector host changes width.</summary>
    [Fact]
    public void StandardMaterialContent_Arrange_UsesCurrentWidth()
    {
        var path = Path.Combine(_directory, "Responsive.nmat");
        MaterialAuthoring.SaveDefault(path);
        StandardMaterialAsset resource;
        using (var stream = File.OpenRead(path))
            resource = StandardMaterialAssetCodec.Load(stream);
        var reference = new AssetReference(AssetId.New(), "main");
        var content = new StandardMaterialInspectorContent(300f,
            new StandardMaterialDocument(CreateEditableLocation(reference, path), resource, _ => { }),
            value => value.ToString());
        content.Width = 0f;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;

        content.Measure(new Vector2(420f, 266f));
        content.Arrange(Vector2.Zero, new Vector2(420f, 266f));

        Assert.Equal(338f, Assert.IsType<TextField>(
            FindByName<TextField>(content, "MaterialMetallic")).Width);
        Assert.Equal(338f, Assert.IsType<ColorPicker>(
            FindByName<ColorPicker>(content, "MaterialBaseColor")).Width);
        Assert.Equal(280f, Assert.IsType<AssetReferenceField>(
            FindByName<AssetReferenceField>(content, "MaterialBaseColorTexture")).Width);
    }

    /// <summary>Resolves compatible physical files through the common typed drop pipeline.</summary>
    [Fact]
    public void AssetDropResolver_PhysicalMaterial_ResolvesMainArtifact()
    {
        var path = Path.Combine(_directory, "Dropped.nmat");
        MaterialAuthoring.SaveDefault(path);
        var database = new AssetDatabase(_directory, EditorAssetImporters.Select);
        var importers = new AssetImporterRegistry();
        importers.Register(new StandardMaterialAssetImporter());
        var resolver = new AssetDropResolver(database,
            new AssetImportPipeline(database, importers));

        var accepted = resolver.TryResolve(new FileSystemNode(path, false),
            "nico/standard-material", out var reference);

        Assert.True(accepted);
        Assert.Equal(database.FindByPath(path)!.Id, reference.Asset);
        Assert.Equal("main", reference.SubAsset);
    }

    /// <summary>Rejects imported outputs whose runtime type does not match the receiving field.</summary>
    [Fact]
    public void AssetDropResolver_ImportedSubAsset_FiltersByContentType()
    {
        var database = new AssetDatabase(_directory, EditorAssetImporters.Select);
        var resolver = new AssetDropResolver(database,
            new AssetImportPipeline(database, new AssetImporterRegistry()));
        var imported = new ImportedSubAssetNode("model.glb",
            new AssetReference(AssetId.New(), "material/0"), "nico/standard-material", "Material");

        Assert.False(resolver.TryResolve(imported, "nico/texture2d", out _));
        Assert.True(resolver.TryResolve(imported, "nico/standard-material", out var reference));
        Assert.Equal(imported.Reference, reference);
    }

    /// <summary>Removes temporary asset files and generated metadata.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Finds a descendant by stable element name.</summary>
    /// <typeparam name="TElement">Requested element type.</typeparam>
    /// <param name="root">Subtree root.</param>
    /// <param name="name">Stable element name.</param>
    /// <returns>Matching descendant, or null.</returns>
    private static TElement? FindByName<TElement>(Engine.UI.UIElement root, string name)
        where TElement : Engine.UI.UIElement
    {
        if (root is TElement typed && root.Name == name)
            return typed;
        foreach (var child in root.Children)
        {
            if (child is Engine.UI.UIElement element &&
                FindByName<TElement>(element, name) is { } match)
                return match;
        }
        return null;
    }

    /// <summary>Creates editable test storage for one material document.</summary>
    /// <param name="reference">Persistent test reference.</param>
    /// <param name="path">Material source path.</param>
    /// <returns>Editable document location.</returns>
    private static AssetDocumentLocation CreateEditableLocation(
        AssetReference reference, string path) => new(
            reference, () => path, () => File.OpenRead(path),
            write => AssetDocumentStorage.WriteAtomic(path, write));
}
