using System.Text;
using Engine.Assets;
using Engine.Core;
using Xunit;

namespace Engine.Assets.Tests;

public class RuntimeResourceManagerTests
{
    /// <summary>Verifies asynchronous acquisition exposes a fallback then publishes a loaded value.</summary>
    [Fact]
    public async Task Acquire_LoadSuccess_PublishesIntoStableHandle()
    {
        var fixture = new ResourceFixture("loaded");
        using var manager = fixture.CreateManager();
        var fallback = new TestResource("fallback");

        var handle = manager.Acquire(fixture.Reference, fallback);
        Assert.Same(fallback, manager.Get(handle));
        await manager.WaitAsync(handle);

        Assert.Equal(ResourceLoadState.Ready, manager.GetState(handle));
        Assert.Equal("loaded", manager.Get(handle).Value);
        Assert.Null(manager.GetError(handle));
    }

    /// <summary>Verifies repeated acquisitions share a handle and retire only after final release.</summary>
    [Fact]
    public async Task Acquire_SameReferenceAndType_SharesReferenceCount()
    {
        var fixture = new ResourceFixture("loaded");
        using var manager = fixture.CreateManager();
        var first = manager.Acquire(fixture.Reference, new TestResource("fallback-1"));
        var second = manager.Acquire(fixture.Reference, new TestResource("fallback-2"));
        await manager.WaitAsync(first);

        Assert.Equal(first, second);
        manager.Release(first);
        Assert.Equal("loaded", manager.Get(second).Value);
        manager.Release(second);

        Assert.Throws<KeyNotFoundException>(() => manager.Get(first));
        Assert.Single(fixture.Retirement.Retired,
            resource => resource is TestResource { Value: "loaded" });
    }

    /// <summary>Verifies reload replaces a resource without changing its stable typed handle.</summary>
    [Fact]
    public async Task Reload_Success_ReplacesResourceAndRetiresPreviousValue()
    {
        var fixture = new ResourceFixture("first");
        using var manager = fixture.CreateManager();
        var handle = manager.Acquire(fixture.Reference, new TestResource("fallback"));
        await manager.WaitAsync(handle);
        var first = manager.Get(handle);
        fixture.SetContent("second", "generation-2");

        await manager.ReloadAsync(fixture.Reference);

        Assert.Equal("second", manager.Get(handle).Value);
        Assert.Contains(first, fixture.Retirement.Retired);
        Assert.Equal(ResourceLoadState.Ready, manager.GetState(handle));
    }

    /// <summary>Verifies failed reloads retain the previous ready resource and expose the error.</summary>
    [Fact]
    public async Task Reload_Failure_PreservesPreviousReadyResource()
    {
        var fixture = new ResourceFixture("first");
        using var manager = fixture.CreateManager();
        var handle = manager.Acquire(fixture.Reference, new TestResource("fallback"));
        await manager.WaitAsync(handle);
        var first = manager.Get(handle);
        fixture.Loader.Fail = true;
        fixture.SetContent("broken", "generation-2");

        await manager.ReloadAsync(fixture.Reference);

        Assert.Same(first, manager.Get(handle));
        Assert.Equal(ResourceLoadState.Failed, manager.GetState(handle));
        Assert.IsType<InvalidDataException>(manager.GetError(handle));
        Assert.DoesNotContain(first, fixture.Retirement.Retired);
    }

    /// <summary>Verifies initial failures leave the caller-owned fallback available.</summary>
    [Fact]
    public async Task Acquire_LoadFailure_PreservesFallback()
    {
        var fixture = new ResourceFixture("broken");
        fixture.Loader.Fail = true;
        using var manager = fixture.CreateManager();
        var fallback = new TestResource("fallback");

        var handle = manager.Acquire(fixture.Reference, fallback);
        await manager.WaitAsync(handle);

        Assert.Same(fallback, manager.Get(handle));
        Assert.Equal(ResourceLoadState.Failed, manager.GetState(handle));
        Assert.Empty(fixture.Retirement.Retired);
    }

    /// <summary>Owns fake resolution, storage, loader, and retirement for resource tests.</summary>
    private sealed class ResourceFixture
    {
        private readonly MemoryVirtualFileSystem _memory = new();
        private readonly MountedVirtualFileSystem _mounted = new();
        private readonly TestResolver _resolver;

        /// <summary>Gets the persistent test reference.</summary>
        internal AssetReference Reference { get; } = new(AssetId.New());

        /// <summary>Gets the configurable test loader.</summary>
        internal TestLoader Loader { get; } = new();

        /// <summary>Gets the resource retirement recorder.</summary>
        internal RecordingRetirement Retirement { get; } = new();

        /// <summary>Creates fixture content and resolution.</summary>
        /// <param name="content">Initial virtual artifact text.</param>
        internal ResourceFixture(string content)
        {
            _mounted.Mount("game", _memory);
            _resolver = new TestResolver(new ResolvedAsset(
                new VirtualFileAssetLocation("game/resource.txt"),
                TestLoader.SupportedContentType,
                "generation-1"));
            SetContent(content, "generation-1");
        }

        /// <summary>Creates a manager registered with the fixture loader.</summary>
        /// <returns>The configured runtime resource manager.</returns>
        internal RuntimeResourceManager CreateManager()
        {
            var manager = new RuntimeResourceManager(_resolver,
                new AssetStorageRouter(_mounted), Retirement);
            manager.RegisterLoader(Loader);
            return manager;
        }

        /// <summary>Replaces virtual artifact content and its resolved generation.</summary>
        /// <param name="content">New artifact text.</param>
        /// <param name="generation">New artifact generation.</param>
        internal void SetContent(string content, string generation)
        {
            _memory.Set("resource.txt", Encoding.UTF8.GetBytes(content));
            _resolver.Resolved = _resolver.Resolved with { Generation = generation };
        }
    }

    /// <summary>Resolves one mutable test artifact.</summary>
    private sealed class TestResolver : IAssetResolver
    {
        /// <summary>Gets or sets the current resolution.</summary>
        internal ResolvedAsset Resolved { get; set; }

        /// <summary>Creates a resolver with one current artifact.</summary>
        /// <param name="resolved">Initial resolved artifact.</param>
        internal TestResolver(ResolvedAsset resolved)
        {
            Resolved = resolved;
        }

        /// <inheritdoc/>
        public ResolvedAsset Resolve(AssetReference reference)
        {
            return Resolved;
        }
    }

    /// <summary>Loads UTF-8 test resources with an asynchronous boundary.</summary>
    private sealed class TestLoader : IRuntimeResourceLoader
    {
        /// <summary>Stable test content type.</summary>
        internal const string SupportedContentType = "test/text";

        /// <summary>Gets or sets whether loading fails.</summary>
        internal bool Fail { get; set; }

        /// <inheritdoc/>
        public string ContentType => SupportedContentType;

        /// <inheritdoc/>
        public Type ResourceType => typeof(TestResource);

        /// <inheritdoc/>
        public async ValueTask<object> LoadAsync(
            Stream stream,
            ResolvedAsset resolved,
            CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            if (Fail)
                throw new InvalidDataException("Controlled resource failure.");
            using var reader = new StreamReader(stream);
            return new TestResource(await reader.ReadToEndAsync(cancellationToken));
        }
    }

    /// <summary>Simple loaded resource value used by tests.</summary>
    /// <param name="Value">Decoded artifact text.</param>
    private sealed record TestResource(string Value);

    /// <summary>Records resources handed to subsystem-aware retirement.</summary>
    private sealed class RecordingRetirement : IRuntimeResourceRetirement
    {
        /// <summary>Gets retired resources.</summary>
        internal List<object> Retired { get; } = [];

        /// <inheritdoc/>
        public void Retire(object resource)
        {
            Retired.Add(resource);
        }
    }
}
