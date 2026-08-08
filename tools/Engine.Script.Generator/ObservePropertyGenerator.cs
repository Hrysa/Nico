using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Engine.Script.Generator;

/// <summary>Generates allocation-free storage, metadata, and notifications for observed properties.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ObservePropertyGenerator : IIncrementalGenerator
{
    private const string ObserveAttributeName = "Engine.Scripting.ObserveAttribute";
    private const string SceneScriptName = "Engine.Scripting.SceneScript";

    private static readonly DiagnosticDescriptor PropertyMustBePartial = new(
        "OBS001", "Observed property must be partial",
        "Observed property '{0}' must be declared partial",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor TypeMustBePartial = new(
        "OBS002", "Observed property type must be partial",
        "Type '{0}' must be declared partial because it contains observed properties",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "OBS003", "Observed property type is unsupported",
        "Observed property '{0}' uses unsupported type '{1}'",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor AccessorsRequired = new(
        "OBS004", "Observed property requires get and set accessors",
        "Observed property '{0}' must declare both get and set accessors",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor SceneScriptRequired = new(
        "OBS005", "Observed property requires SceneScript",
        "Type '{0}' must derive from SceneScript to contain observed properties",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor TopLevelTypeRequired = new(
        "OBS006", "Observed script must be top-level",
        "Observed script type '{0}' must be top-level in the initial generator implementation",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor InstancePropertyRequired = new(
        "OBS007", "Observed property must be an instance property",
        "Observed property '{0}' cannot be static, abstract, or an indexer",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor ScopeRequired = new(
        "OBS008", "Observed property requires a scope",
        "Observed property '{0}' must request Editor, Runtime, or both",
        "Engine.Scripting", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var properties = context.SyntaxProvider.ForAttributeWithMetadataName(
            ObserveAttributeName,
            static (node, _) => node is PropertyDeclarationSyntax,
            static (attributeContext, _) => CreateCandidate(attributeContext));
        context.RegisterSourceOutput(properties.Collect(),
            static (productionContext, candidates) => Execute(productionContext, candidates));
    }

    /// <summary>Creates one semantically resolved observed-property candidate.</summary>
    /// <param name="context">Roslyn attribute discovery context.</param>
    /// <returns>Resolved candidate.</returns>
    private static PropertyCandidate CreateCandidate(GeneratorAttributeSyntaxContext context)
    {
        var property = (IPropertySymbol)context.TargetSymbol;
        var declaration = (PropertyDeclarationSyntax)context.TargetNode;
        var scope = 0;
        var attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length > 0)
        {
            var scopes = attribute.ConstructorArguments[0];
            if (scopes.Kind == TypedConstantKind.Array)
            {
                for (var index = 0; index < scopes.Values.Length; index++)
                    scope |= Convert.ToInt32(scopes.Values[index].Value, CultureInfo.InvariantCulture);
            }
            else if (scopes.Value is not null)
            {
                scope = Convert.ToInt32(scopes.Value, CultureInfo.InvariantCulture);
            }
        }
        return new PropertyCandidate(property, declaration, scope);
    }

    /// <summary>Validates, groups, and emits all observed properties in the compilation.</summary>
    /// <param name="context">Source-production context.</param>
    /// <param name="candidates">Discovered observed properties.</param>
    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<PropertyCandidate> candidates)
    {
        var groups = new List<TypeGroup>();
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var group = FindGroup(groups, candidate.Property.ContainingType);
            group.Properties.Add(candidate);
        }

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (!ValidateType(context, group.Type, group.Properties[0].Declaration))
                continue;
            var valid = new List<GeneratedProperty>();
            for (var propertyIndex = 0; propertyIndex < group.Properties.Count; propertyIndex++)
            {
                var generated = ValidateProperty(context, group.Properties[propertyIndex]);
                if (generated is not null)
                    valid.Add(generated);
            }
            if (valid.Count == 0)
                continue;
            valid.Sort(static (left, right) => left.Id.CompareTo(right.Id));
            var source = GenerateType(group.Type, valid);
            context.AddSource(CreateHintName(group.Type), SourceText.From(source, Encoding.UTF8));
        }
    }

    /// <summary>Finds or creates the group for one containing script type.</summary>
    /// <param name="groups">Existing groups.</param>
    /// <param name="type">Containing type.</param>
    /// <returns>Matching group.</returns>
    private static TypeGroup FindGroup(List<TypeGroup> groups, INamedTypeSymbol type)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            if (SymbolEqualityComparer.Default.Equals(groups[index].Type, type))
                return groups[index];
        }
        var group = new TypeGroup(type);
        groups.Add(group);
        return group;
    }

    /// <summary>Validates the containing script type contract.</summary>
    /// <param name="context">Diagnostic receiver.</param>
    /// <param name="type">Containing type.</param>
    /// <param name="declaration">Property declaration used for diagnostic location.</param>
    /// <returns>True when source can be generated for the type.</returns>
    private static bool ValidateType(
        SourceProductionContext context,
        INamedTypeSymbol type,
        PropertyDeclarationSyntax declaration)
    {
        if (type.ContainingType is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                TopLevelTypeRequired, declaration.Identifier.GetLocation(), type.Name));
            return false;
        }
        if (!DerivesFrom(type, SceneScriptName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SceneScriptRequired, declaration.Identifier.GetLocation(), type.Name));
            return false;
        }
        var syntaxReferences = type.DeclaringSyntaxReferences;
        for (var index = 0; index < syntaxReferences.Length; index++)
        {
            if (syntaxReferences[index].GetSyntax() is TypeDeclarationSyntax typeDeclaration &&
                typeDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                return true;
        }
        context.ReportDiagnostic(Diagnostic.Create(
            TypeMustBePartial, declaration.Identifier.GetLocation(), type.Name));
        return false;
    }

    /// <summary>Validates one property and maps it to the generated value contract.</summary>
    /// <param name="context">Diagnostic receiver.</param>
    /// <param name="candidate">Observed-property candidate.</param>
    /// <returns>Generated property data, or null after a diagnostic.</returns>
    private static GeneratedProperty? ValidateProperty(
        SourceProductionContext context,
        PropertyCandidate candidate)
    {
        var property = candidate.Property;
        var declaration = candidate.Declaration;
        var location = declaration.Identifier.GetLocation();
        if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                PropertyMustBePartial, location, property.Name));
            return null;
        }
        if (property.IsStatic || property.IsAbstract || property.IsIndexer)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InstancePropertyRequired, location, property.Name));
            return null;
        }
        if (property.GetMethod is null || property.SetMethod is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AccessorsRequired, location, property.Name));
            return null;
        }
        if (candidate.Scope == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(ScopeRequired, location, property.Name));
            return null;
        }
        if (!TryMapType(property.Type, out var mapping))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedType, location, property.Name,
                property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            return null;
        }
        var identity = $"{property.ContainingType.ToDisplayString()}.{property.MetadataName}";
        return new GeneratedProperty(
            property,
            declaration,
            candidate.Scope,
            unchecked((int)StableHash(identity)),
            mapping);
    }

    /// <summary>Checks one base-type chain by metadata name.</summary>
    /// <param name="type">Candidate type.</param>
    /// <param name="metadataName">Required base metadata name.</param>
    /// <returns>True when the base appears in the chain.</returns>
    private static bool DerivesFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == metadataName)
                return true;
        }
        return false;
    }

    /// <summary>Maps one Roslyn type to allocation-free generated read and write expressions.</summary>
    /// <param name="type">Observed property type.</param>
    /// <param name="mapping">Mapped value contract.</param>
    /// <returns>True for supported property types.</returns>
    private static bool TryMapType(ITypeSymbol type, out TypeMapping mapping)
    {
        var display = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        mapping = display switch
        {
            "bool" => new("Boolean", "From({0})", "TryGetBoolean", "{0}"),
            "byte" => new("UnsignedInteger", "From((ulong){0})", "TryGetUnsignedInteger", "checked((byte){0})"),
            "sbyte" => new("SignedInteger", "From((long){0})", "TryGetSignedInteger", "checked((sbyte){0})"),
            "short" => new("SignedInteger", "From((long){0})", "TryGetSignedInteger", "checked((short){0})"),
            "ushort" => new("UnsignedInteger", "From((ulong){0})", "TryGetUnsignedInteger", "checked((ushort){0})"),
            "int" => new("SignedInteger", "From((long){0})", "TryGetSignedInteger", "checked((int){0})"),
            "uint" => new("UnsignedInteger", "From((ulong){0})", "TryGetUnsignedInteger", "checked((uint){0})"),
            "long" => new("SignedInteger", "From({0})", "TryGetSignedInteger", "{0}"),
            "ulong" => new("UnsignedInteger", "From({0})", "TryGetUnsignedInteger", "{0}"),
            "float" => new("Number", "From((double){0})", "TryGetNumber", "(float){0}"),
            "double" => new("Number", "From({0})", "TryGetNumber", "{0}"),
            "string" or "string?" => new("String", "From({0})", "TryGetString", "{0}"),
            "global::System.Numerics.Vector2" => new("Vector2", "From({0})", "TryGetVector2", "{0}"),
            "global::System.Numerics.Vector3" => new("Vector3", "From({0})", "TryGetVector3", "{0}"),
            "global::System.Numerics.Vector4" => new("Vector4", "From({0})", "TryGetVector4", "{0}"),
            _ => default
        };
        return mapping.Kind is not null;
    }

    /// <summary>Generates one partial script implementation.</summary>
    /// <param name="type">Script type.</param>
    /// <param name="properties">Validated observed properties.</param>
    /// <returns>Complete generated C# source.</returns>
    private static string GenerateType(INamedTypeSymbol type, List<GeneratedProperty> properties)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString())
                .AppendLine(";").AppendLine();
        }
        builder.Append(GetAccessibility(type.DeclaredAccessibility)).Append(" partial class ")
            .Append(Escape(type.Name)).AppendLine().AppendLine("{");
        AppendDescriptors(builder, properties);
        for (var index = 0; index < properties.Count; index++)
            AppendProperty(builder, properties[index]);
        AppendReadMethod(builder, properties);
        AppendWriteMethod(builder, properties);
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>Appends generated property descriptors and their override.</summary>
    /// <param name="builder">Generated source builder.</param>
    /// <param name="properties">Observed properties.</param>
    private static void AppendDescriptors(StringBuilder builder, List<GeneratedProperty> properties)
    {
        builder.AppendLine("    private static readonly global::Engine.Scripting.ObservedPropertyDescriptor[] __observedProperties =")
            .AppendLine("    [");
        for (var index = 0; index < properties.Count; index++)
        {
            var property = properties[index];
            builder.Append("        new(").Append(property.Id.ToString(CultureInfo.InvariantCulture))
                .Append(", \"").Append(EscapeString(property.Property.Name)).Append("\", ")
                .Append("global::Engine.Scripting.ObservedValueKind.").Append(property.Mapping.Kind)
                .Append(", (global::Engine.Scripting.ObserveScope)")
                .Append(property.Scope.ToString(CultureInfo.InvariantCulture)).AppendLine("),");
        }
        builder.AppendLine("    ];").AppendLine()
            .AppendLine("    private global::System.Collections.Generic.IReadOnlyList<global::Engine.Scripting.ObservedPropertyDescriptor>? __combinedObservedProperties;")
            .AppendLine()
            .AppendLine("    /// <inheritdoc/>")
            .AppendLine("    public override global::System.Collections.Generic.IReadOnlyList<global::Engine.Scripting.ObservedPropertyDescriptor> ObservedProperties")
            .AppendLine("    {")
            .AppendLine("        get")
            .AppendLine("        {")
            .AppendLine("            var inherited = base.ObservedProperties;")
            .AppendLine("            return inherited.Count == 0")
            .AppendLine("                ? __observedProperties")
            .AppendLine("                : __combinedObservedProperties ??= CombineObservedProperties(inherited, __observedProperties);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    /// <summary>Appends one field-backed observed partial property implementation.</summary>
    /// <param name="builder">Generated source builder.</param>
    /// <param name="property">Observed property.</param>
    private static void AppendProperty(StringBuilder builder, GeneratedProperty property)
    {
        var symbol = property.Property;
        var typeName = symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.Append("    ").Append(GetAccessibility(symbol.DeclaredAccessibility))
            .Append(" partial ").Append(typeName).Append(' ').Append(Escape(symbol.Name))
            .AppendLine().AppendLine("    {")
            .AppendLine("        get => field;");
        builder.Append("        ");
        if (symbol.SetMethod!.DeclaredAccessibility != symbol.DeclaredAccessibility)
            builder.Append(GetAccessibility(symbol.SetMethod.DeclaredAccessibility)).Append(' ');
        builder.AppendLine("set")
            .AppendLine("        {")
            .Append("            if (global::System.Collections.Generic.EqualityComparer<")
            .Append(typeName).AppendLine(">.Default.Equals(field, value))")
            .AppendLine("                return;")
            .AppendLine("            field = value;")
            .Append("            NotifyObservedPropertyChanged(")
            .Append(property.Id.ToString(CultureInfo.InvariantCulture))
            .Append(", (global::Engine.Scripting.ObserveScope)")
            .Append(property.Scope.ToString(CultureInfo.InvariantCulture)).AppendLine(");")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    /// <summary>Appends the generated allocation-free observed-value reader.</summary>
    /// <param name="builder">Generated source builder.</param>
    /// <param name="properties">Observed properties.</param>
    private static void AppendReadMethod(StringBuilder builder, List<GeneratedProperty> properties)
    {
        builder.AppendLine("    /// <inheritdoc/>")
            .AppendLine("    public override bool TryGetObservedValue(int propertyId, out global::Engine.Scripting.ObservedValue value)")
            .AppendLine("    {")
            .AppendLine("        switch (propertyId)")
            .AppendLine("        {");
        for (var index = 0; index < properties.Count; index++)
        {
            var property = properties[index];
            var expression = string.Format(CultureInfo.InvariantCulture,
                property.Mapping.ReadExpression, Escape(property.Property.Name));
            builder.Append("            case ").Append(property.Id.ToString(CultureInfo.InvariantCulture))
                .AppendLine(":")
                .Append("                value = global::Engine.Scripting.ObservedValue.")
                .Append(expression).AppendLine(";")
                .AppendLine("                return true;");
        }
        builder.AppendLine("            default:")
            .AppendLine("                return base.TryGetObservedValue(propertyId, out value);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    /// <summary>Appends the generated observed-value writer.</summary>
    /// <param name="builder">Generated source builder.</param>
    /// <param name="properties">Observed properties.</param>
    private static void AppendWriteMethod(StringBuilder builder, List<GeneratedProperty> properties)
    {
        builder.AppendLine("    /// <inheritdoc/>")
            .AppendLine("    public override bool TrySetObservedValue(int propertyId, global::Engine.Scripting.ObservedValue value)")
            .AppendLine("    {")
            .AppendLine("        switch (propertyId)")
            .AppendLine("        {");
        for (var index = 0; index < properties.Count; index++)
        {
            var property = properties[index];
            var localName = "typed" + index.ToString(CultureInfo.InvariantCulture);
            var assignment = string.Format(CultureInfo.InvariantCulture,
                property.Mapping.WriteExpression, localName);
            builder.Append("            case ").Append(property.Id.ToString(CultureInfo.InvariantCulture))
                .AppendLine(":")
                .Append("                if (!value.").Append(property.Mapping.TryReadMethod)
                .Append("(out var ").Append(localName).AppendLine("))")
                .AppendLine("                    return false;")
                .AppendLine("                try")
                .AppendLine("                {")
                .Append("                    ").Append(Escape(property.Property.Name)).Append(" = ")
                .Append(assignment).AppendLine(";")
                .AppendLine("                    return true;")
                .AppendLine("                }")
                .AppendLine("                catch (global::System.OverflowException)")
                .AppendLine("                {")
                .AppendLine("                    return false;")
                .AppendLine("                }");
        }
        builder.AppendLine("            default:")
            .AppendLine("                return base.TrySetObservedValue(propertyId, value);")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    /// <summary>Computes a stable FNV-1a identifier from a fully qualified property name.</summary>
    /// <param name="value">Stable property identity.</param>
    /// <returns>32-bit identifier.</returns>
    private static uint StableHash(string value)
    {
        const uint Offset = 2166136261;
        const uint Prime = 16777619;
        var hash = Offset;
        for (var index = 0; index < value.Length; index++)
        {
            hash ^= value[index];
            hash *= Prime;
        }
        return hash;
    }

    /// <summary>Formats a C# accessibility modifier.</summary>
    /// <param name="accessibility">Roslyn accessibility.</param>
    /// <returns>C# modifier text.</returns>
    private static string GetAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        _ => "internal"
    };

    /// <summary>Escapes one identifier when it is a C# keyword.</summary>
    /// <param name="identifier">Source identifier.</param>
    /// <returns>Safe generated identifier.</returns>
    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

    /// <summary>Escapes one string for a generated literal.</summary>
    /// <param name="value">Raw string.</param>
    /// <returns>Literal content without surrounding quotes.</returns>
    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Creates a deterministic source hint name.</summary>
    /// <param name="type">Generated script type.</param>
    /// <returns>Hint filename.</returns>
    private static string CreateHintName(INamedTypeSymbol type) =>
        type.ToDisplayString().Replace('.', '_').Replace('+', '_') + ".Observe.g.cs";

    /// <summary>Groups observed properties belonging to one script type.</summary>
    private sealed class TypeGroup
    {
        /// <summary>Gets the script type.</summary>
        internal INamedTypeSymbol Type { get; }
        /// <summary>Gets discovered properties.</summary>
        internal List<PropertyCandidate> Properties { get; } = new();

        /// <summary>Creates a type group.</summary>
        /// <param name="type">Script type.</param>
        internal TypeGroup(INamedTypeSymbol type) => Type = type;
    }

    /// <summary>Stores one discovered property and its syntax.</summary>
    private sealed class PropertyCandidate
    {
        /// <summary>Gets the property symbol.</summary>
        internal IPropertySymbol Property { get; }
        /// <summary>Gets the declaring syntax.</summary>
        internal PropertyDeclarationSyntax Declaration { get; }
        /// <summary>Gets combined scope flags.</summary>
        internal int Scope { get; }

        /// <summary>Creates a property candidate.</summary>
        /// <param name="property">Property symbol.</param>
        /// <param name="declaration">Declaring syntax.</param>
        /// <param name="scope">Combined scope flags.</param>
        internal PropertyCandidate(
            IPropertySymbol property,
            PropertyDeclarationSyntax declaration,
            int scope)
        {
            Property = property;
            Declaration = declaration;
            Scope = scope;
        }
    }

    /// <summary>Stores validated data used to generate one property.</summary>
    private sealed class GeneratedProperty
    {
        /// <summary>Gets the property symbol.</summary>
        internal IPropertySymbol Property { get; }
        /// <summary>Gets the declaration syntax.</summary>
        internal PropertyDeclarationSyntax Declaration { get; }
        /// <summary>Gets combined scope flags.</summary>
        internal int Scope { get; }
        /// <summary>Gets the stable identifier.</summary>
        internal int Id { get; }
        /// <summary>Gets value conversion mapping.</summary>
        internal TypeMapping Mapping { get; }

        /// <summary>Creates generated-property data.</summary>
        internal GeneratedProperty(
            IPropertySymbol property,
            PropertyDeclarationSyntax declaration,
            int scope,
            int id,
            TypeMapping mapping)
        {
            Property = property;
            Declaration = declaration;
            Scope = scope;
            Id = id;
            Mapping = mapping;
        }
    }

    /// <summary>Maps one supported type to generated value operations.</summary>
    private readonly struct TypeMapping
    {
        /// <summary>Gets the observed kind member name.</summary>
        internal string? Kind { get; }
        /// <summary>Gets the formatted value-reader expression.</summary>
        internal string ReadExpression { get; }
        /// <summary>Gets the ObservedValue try-read method.</summary>
        internal string TryReadMethod { get; }
        /// <summary>Gets the formatted property-write expression.</summary>
        internal string WriteExpression { get; }

        /// <summary>Creates one type mapping.</summary>
        internal TypeMapping(
            string kind,
            string readExpression,
            string tryReadMethod,
            string writeExpression)
        {
            Kind = kind;
            ReadExpression = readExpression;
            TryReadMethod = tryReadMethod;
            WriteExpression = writeExpression;
        }
    }
}
