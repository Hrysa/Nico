using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Engine.Profiler.Weaver;

/// <summary>Injects allocation-free profiler entry and exit calls into managed assemblies.</summary>
internal static class Program
{
    private const string InstrumentedResourceName = "Engine.Profiler.Instrumented";
    private const string ProfilerTypeName = "Engine.Graphics.CpuProfiler";

    /// <summary>Instruments the requested assemblies in place.</summary>
    /// <param name="args">Profiler runtime assembly followed by target assembly paths.</param>
    /// <returns>Zero when every existing target was instrumented successfully.</returns>
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: Engine.Profiler.Weaver <Engine.Graphics.dll> <assembly.dll> [...]");
            return 2;
        }

        var runtimePath = Path.GetFullPath(args[0]);
        using var runtime = AssemblyDefinition.ReadAssembly(
            runtimePath,
            new ReaderParameters { InMemory = true });
        var profilerType = runtime.MainModule.GetType(ProfilerTypeName)
            ?? throw new InvalidOperationException($"Could not find {ProfilerTypeName} in {runtimePath}.");
        var enter = profilerType.Methods.Single(method => method.Name == "Enter" && method.Parameters.Count == 1);
        var leave = profilerType.Methods.Single(method => method.Name == "Leave" && method.Parameters.Count == 1);

        foreach (var path in args.Skip(1).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(path))
                InstrumentAssembly(path, runtimePath, enter, leave);
        }
        return 0;
    }

    /// <summary>Instruments all supported method bodies in one assembly.</summary>
    /// <param name="path">Assembly path to rewrite.</param>
    /// <param name="runtimePath">Assembly containing the managed profiler hooks.</param>
    /// <param name="runtimeEnter">Runtime entry hook definition.</param>
    /// <param name="runtimeLeave">Runtime exit hook definition.</param>
    private static void InstrumentAssembly(
        string path,
        string runtimePath,
        MethodDefinition runtimeEnter,
        MethodDefinition runtimeLeave)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(runtimePath)!);
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        var hasSymbols = File.Exists(pdbPath);
        var reader = new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = hasSymbols
        };
        using var assembly = AssemblyDefinition.ReadAssembly(path, reader);
        if (assembly.MainModule.Resources.Any(resource => resource.Name == InstrumentedResourceName))
            return;

        MethodReference enter;
        MethodReference leave;
        if (Path.GetFullPath(path).Equals(runtimePath, StringComparison.Ordinal))
        {
            var localProfiler = assembly.MainModule.GetType(ProfilerTypeName)!;
            enter = localProfiler.Methods.Single(method => method.Name == "Enter" && method.Parameters.Count == 1);
            leave = localProfiler.Methods.Single(method => method.Name == "Leave" && method.Parameters.Count == 1);
        }
        else
        {
            enter = assembly.MainModule.ImportReference(runtimeEnter);
            leave = assembly.MainModule.ImportReference(runtimeLeave);
        }

        var instrumentedCount = 0;
        foreach (var type in AllTypes(assembly.MainModule.Types))
        {
            if (type.FullName == ProfilerTypeName || type.FullName.StartsWith(ProfilerTypeName + "/", StringComparison.Ordinal))
                continue;
            foreach (var method in type.Methods)
            {
                if (CanInstrument(method))
                {
                    InstrumentMethod(method, enter, leave, FormatMethodName(method));
                    instrumentedCount++;
                }
            }
        }

        assembly.MainModule.Resources.Add(new EmbeddedResource(
            InstrumentedResourceName,
            ManifestResourceAttributes.Private,
            BitConverter.GetBytes(instrumentedCount)));
        assembly.Write(path, new WriterParameters { WriteSymbols = hasSymbols });
        Console.WriteLine($"Instrumented {instrumentedCount} methods in {Path.GetFileName(path)}");
    }

    /// <summary>Enumerates top-level and nested types recursively.</summary>
    /// <param name="types">Types at the current nesting level.</param>
    /// <returns>All reachable type definitions.</returns>
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes))
                yield return nested;
        }
    }

    /// <summary>Determines whether a method body can safely receive return hooks.</summary>
    /// <param name="method">Candidate method.</param>
    /// <returns>True when the method is supported.</returns>
    private static bool CanInstrument(MethodDefinition method)
    {
        return method.HasBody && method.Body.Instructions.Count > 0 && !method.IsConstructor &&
            !method.IsPInvokeImpl && !method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Tail);
    }

    /// <summary>Adds an entry hook and a named exit hook to every normal return.</summary>
    /// <param name="method">Method to rewrite.</param>
    /// <param name="enter">Imported entry hook.</param>
    /// <param name="leave">Imported exit hook.</param>
    /// <param name="displayName">Name embedded in the entry hook.</param>
    private static void InstrumentMethod(
        MethodDefinition method,
        MethodReference enter,
        MethodReference leave,
        string displayName)
    {
        var body = method.Body;
        var processor = body.GetILProcessor();
        var originalFirst = body.Instructions[0];
        processor.InsertBefore(originalFirst, processor.Create(OpCodes.Ldstr, displayName));
        processor.InsertBefore(originalFirst, processor.Create(OpCodes.Call, enter));

        VariableDefinition? returnValue = null;
        if (!IsVoid(method.ReturnType))
        {
            returnValue = new VariableDefinition(method.ReturnType);
            body.Variables.Add(returnValue);
            body.InitLocals = true;
        }

        var returns = body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();
        foreach (var instruction in returns)
        {
            if (returnValue is null)
            {
                instruction.OpCode = OpCodes.Ldstr;
                instruction.Operand = displayName;
                var call = processor.Create(OpCodes.Call, leave);
                processor.InsertAfter(instruction, call);
                processor.InsertAfter(call, processor.Create(OpCodes.Ret));
            }
            else
            {
                instruction.OpCode = OpCodes.Stloc;
                instruction.Operand = returnValue;
                var name = processor.Create(OpCodes.Ldstr, displayName);
                var call = processor.Create(OpCodes.Call, leave);
                var load = processor.Create(OpCodes.Ldloc, returnValue);
                processor.InsertAfter(instruction, name);
                processor.InsertAfter(name, call);
                processor.InsertAfter(call, load);
                processor.InsertAfter(load, processor.Create(OpCodes.Ret));
            }
        }
    }

    /// <summary>Recognizes void returns wrapped in required or optional custom modifiers.</summary>
    /// <param name="type">Return type to inspect.</param>
    /// <returns>True when the effective return type is void.</returns>
    private static bool IsVoid(TypeReference type)
    {
        if (type.MetadataType == MetadataType.Void)
            return true;
        return type switch
        {
            RequiredModifierType required => IsVoid(required.ElementType),
            OptionalModifierType optional => IsVoid(optional.ElementType),
            _ => false
        };
    }

    /// <summary>Formats a compact stable method name for the call-tree UI.</summary>
    /// <param name="method">Method being instrumented.</param>
    /// <returns>Namespace, type, and method name.</returns>
    private static string FormatMethodName(MethodDefinition method)
    {
        var genericParameters = method.HasGenericParameters
            ? $"<{string.Join(", ", method.GenericParameters.Select(parameter => parameter.Name))}>"
            : string.Empty;
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        return $"{method.DeclaringType.FullName.Replace('/', '.')}.{method.Name}{genericParameters}({parameters})";
    }

    /// <summary>Formats one parameter including its by-reference direction.</summary>
    /// <param name="parameter">Parameter to format.</param>
    /// <returns>Compact C#-style parameter type.</returns>
    private static string FormatParameter(ParameterDefinition parameter)
    {
        if (parameter.ParameterType is not ByReferenceType byReference)
            return FormatType(parameter.ParameterType);
        var modifier = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
        return modifier + FormatType(byReference.ElementType);
    }

    /// <summary>Formats a Cecil type reference as a compact C#-style name.</summary>
    /// <param name="type">Type reference to format.</param>
    /// <returns>Readable type name without assembly qualification.</returns>
    private static string FormatType(TypeReference type)
    {
        if (TryGetAlias(type, out var alias))
            return alias;
        return type switch
        {
            GenericParameter parameter => parameter.Name,
            GenericInstanceType generic =>
                $"{RemoveArity(generic.ElementType.Name)}<{string.Join(", ", generic.GenericArguments.Select(FormatType))}>",
            ArrayType array => $"{FormatType(array.ElementType)}[{new string(',', array.Rank - 1)}]",
            PointerType pointer => $"{FormatType(pointer.ElementType)}*",
            ByReferenceType byReference => $"ref {FormatType(byReference.ElementType)}",
            RequiredModifierType required => FormatType(required.ElementType),
            OptionalModifierType optional => FormatType(optional.ElementType),
            PinnedType pinned => FormatType(pinned.ElementType),
            SentinelType sentinel => FormatType(sentinel.ElementType),
            _ => RemoveArity(type.Name)
        };
    }

    /// <summary>Returns the C# keyword alias for a built-in type when available.</summary>
    /// <param name="type">Type reference to inspect.</param>
    /// <param name="alias">Receives the keyword alias.</param>
    /// <returns>True when the type has a C# keyword alias.</returns>
    private static bool TryGetAlias(TypeReference type, out string alias)
    {
        alias = type.MetadataType switch
        {
            MetadataType.Void => "void",
            MetadataType.Boolean => "bool",
            MetadataType.Char => "char",
            MetadataType.SByte => "sbyte",
            MetadataType.Byte => "byte",
            MetadataType.Int16 => "short",
            MetadataType.UInt16 => "ushort",
            MetadataType.Int32 => "int",
            MetadataType.UInt32 => "uint",
            MetadataType.Int64 => "long",
            MetadataType.UInt64 => "ulong",
            MetadataType.Single => "float",
            MetadataType.Double => "double",
            MetadataType.String => "string",
            MetadataType.Object => "object",
            MetadataType.IntPtr => "nint",
            MetadataType.UIntPtr => "nuint",
            _ => string.Empty
        };
        return alias.Length > 0;
    }

    /// <summary>Removes a metadata generic-arity suffix from a type name.</summary>
    /// <param name="name">Metadata type name.</param>
    /// <returns>Name without its backtick suffix.</returns>
    private static string RemoveArity(string name)
    {
        var arity = name.IndexOf('`');
        return arity < 0 ? name : name[..arity];
    }
}
