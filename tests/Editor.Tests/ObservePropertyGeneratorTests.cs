using System.Reflection;
using System.Runtime.Loader;
using Engine.Script.Generator;
using Engine.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies generated observed script properties and diagnostics.</summary>
public sealed class ObservePropertyGeneratorTests
{
    /// <summary>Verifies generated properties expose metadata, typed access, and notifications.</summary>
    [Fact]
    public void Generator_PartialObservedProperty_EmitsRuntimeContract()
    {
        const string Source = """
            using Engine.Scripting;

            public partial class PlayerController : SceneScript
            {
                [Observe(ObserveScope.Editor, ObserveScope.Runtime)]
                public partial float Health { get; set; } = 100f;
            }
            """;
        var result = Compile(Source);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var emit = result.Compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("ObservedScriptTest", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var script = Assert.IsAssignableFrom<SceneScript>(
                Activator.CreateInstance(assembly.GetType("PlayerController")!));
            var descriptor = Assert.Single(script.ObservedProperties);
            Assert.Equal("Health", descriptor.Name);
            Assert.Equal(ObservedValueKind.Number, descriptor.Kind);
            Assert.Equal(ObserveScope.Editor | ObserveScope.Runtime, descriptor.Scope);
            var changes = new List<ObservedPropertyChange>();
            script.ObservedPropertyChanged += changes.Add;

            Assert.True(script.TryGetObservedValue(descriptor.Id, out var initial));
            Assert.True(initial.TryGetNumber(out var initialHealth));
            Assert.Equal(100d, initialHealth);
            Assert.True(script.TrySetObservedValue(descriptor.Id, ObservedValue.From(75d)));
            Assert.True(script.TryGetObservedValue(descriptor.Id, out var updated));
            Assert.True(updated.TryGetNumber(out var updatedHealth));
            Assert.Equal(75d, updatedHealth);
            var change = Assert.Single(changes);
            Assert.Equal(descriptor.Id, change.PropertyId);
            Assert.Equal(ObserveScope.Editor | ObserveScope.Runtime, change.Scope);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Verifies invalid declarations receive an actionable generator diagnostic.</summary>
    [Fact]
    public void Generator_NonPartialProperty_ReportsObs001()
    {
        const string Source = """
            using Engine.Scripting;

            public partial class PlayerController : SceneScript
            {
                [Observe(ObserveScope.Editor)]
                public float Health { get; set; }
            }
            """;

        var result = Compile(Source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "OBS001");
    }

    /// <summary>Verifies Editor observation alone supplies Inspector-facing metadata.</summary>
    [Fact]
    public void Generator_EditorScope_EmitsEditableInspectorContract()
    {
        const string Source = """
            using Engine.Scripting;

            public partial class LightController : SceneScript
            {
                [Observe(Editor)]
                public partial float Intensity { get; set; } = 1f;
            }
            """;
        var result = Compile(Source);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var emit = result.Compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("EditorObservedScriptTest", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var script = Assert.IsAssignableFrom<SceneScript>(
                Activator.CreateInstance(assembly.GetType("LightController")!));
            var descriptor = Assert.Single(script.ObservedProperties);

            Assert.Equal(ObserveScope.Editor, descriptor.Scope);
            Assert.True(script.TrySetObservedValue(descriptor.Id, ObservedValue.From(2.5d)));
            Assert.True(script.TryGetObservedValue(descriptor.Id, out var value));
            Assert.True(value.TryGetNumber(out var intensity));
            Assert.Equal(2.5d, intensity);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Preserves inherited descriptors while routing reads and writes through base scripts.</summary>
    [Fact]
    public void Generator_DerivedObservedScript_CombinesInheritedMetadata()
    {
        const string Source = """
            using Engine.Scripting;

            public partial class CharacterController : SceneScript
            {
                [Observe(Editor)]
                public partial float Health { get; set; } = 100f;
            }

            public partial class PlayerController : CharacterController
            {
                [Observe(Editor, Runtime)]
                public partial float Speed { get; set; } = 4f;
            }
            """;
        var result = Compile(Source);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var emit = result.Compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("InheritedObservedScriptTest", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var script = Assert.IsAssignableFrom<SceneScript>(
                Activator.CreateInstance(assembly.GetType("PlayerController")!));

            Assert.Equal(2, script.ObservedProperties.Count);
            var health = Assert.Single(script.ObservedProperties,
                descriptor => descriptor.Name == "Health");
            var speed = Assert.Single(script.ObservedProperties,
                descriptor => descriptor.Name == "Speed");
            Assert.True(script.TrySetObservedValue(health.Id, ObservedValue.From(75d)));
            Assert.True(script.TrySetObservedValue(speed.Id, ObservedValue.From(6d)));
            Assert.True(script.TryGetObservedValue(health.Id, out var healthValue));
            Assert.True(script.TryGetObservedValue(speed.Id, out var speedValue));
            Assert.True(healthValue.TryGetNumber(out var currentHealth));
            Assert.True(speedValue.TryGetNumber(out var currentSpeed));
            Assert.Equal(75d, currentHealth);
            Assert.Equal(6d, currentSpeed);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Compiles source with the observed-property incremental generator.</summary>
    /// <param name="source">User script source.</param>
    /// <returns>Updated compilation and combined diagnostics.</returns>
    private static GeneratorCompilationResult Compile(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trusted))
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
                paths.Add(path);
        }
        paths.Add(typeof(SceneScript).Assembly.Location);
        var references = paths.Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            $"ObservedScripts_{Guid.NewGuid():N}",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ObservePropertyGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var updated, out var generatorDiagnostics);
        var diagnostics = updated.GetDiagnostics().Concat(generatorDiagnostics).ToArray();
        return new GeneratorCompilationResult((CSharpCompilation)updated, diagnostics);
    }

    /// <summary>Contains generated compilation output and diagnostics.</summary>
    /// <param name="Compilation">Updated compilation.</param>
    /// <param name="Diagnostics">Compiler and generator diagnostics.</param>
    private sealed record GeneratorCompilationResult(
        CSharpCompilation Compilation,
        IReadOnlyList<Diagnostic> Diagnostics);
}
