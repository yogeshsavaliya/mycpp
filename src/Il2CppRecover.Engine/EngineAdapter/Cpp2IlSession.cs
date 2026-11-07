using AssetRipper.Primitives;
using LibCpp2IL;
using LibCpp2IL.Logging;

namespace Il2CppRecover.EngineAdapter;

public sealed class EngineLoadResult
{
    public bool Loaded { get; init; }
    public string Diagnostic { get; init; } = "";
    public IReadOnlyList<EngineMethod> Methods { get; init; } = [];
    public IReadOnlyList<EngineTypeInfo> Types { get; init; } = [];
}

public sealed class EngineMethod
{
    public required string Name { get; init; }
    public required string DeclaringType { get; init; }
    public required string Namespace { get; init; }
    public required string Image { get; init; }
    public int MetadataIndex { get; init; }
    public ulong VirtualAddress { get; init; }
    public bool IsStatic { get; init; }
    public string ReturnType { get; init; } = "unresolved";
    public IReadOnlyList<string> ParameterTypes { get; init; } = [];
    public int DeclaringTypeId { get; init; } = -1;
    public TypeRef? ReturnTypeRef { get; init; }
    public IReadOnlyList<TypeRef> ParameterTypeRefs { get; init; } = [];
    /// <summary>Interface.Method when a private virtual final method's vtable slot names an interface method.</summary>
    public string? ExplicitInterfaceName { get; init; }
}

public sealed class EngineTypeInfo
{
    public int MetadataIndex { get; init; } = -1;
    public required string Image { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public string? BaseType { get; init; }
    public TypeRef? BaseTypeRef { get; init; }
    public string? DeclaringType { get; init; }
    public int? DeclaringTypeId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool IsValueType { get; init; }
    public bool IsEnum { get; init; }
    public bool IsOpenGeneric { get; init; }
    public bool IsExplicitLayout { get; init; }
    public IReadOnlyList<EngineFieldInfo> Fields { get; init; } = [];
    public IReadOnlyList<string> Events { get; init; } = [];
    public IReadOnlyList<TypeRef> Interfaces { get; init; } = [];
}

public sealed class EngineFieldInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public TypeRef? TypeReference { get; init; }
    public string Access { get; init; } = "unresolved";
    public bool IsStatic { get; init; }
    public int? Offset { get; init; }
    public object? ConstantValue { get; init; }
    public bool IsLiteral { get; init; }
}

/// <summary>
/// Calls the pinned LibCpp2IL loader. Dummy-DLL and dll_il_recovery output are
/// intentionally not invoked: in Cpp2IL 2022.1.0-pre-release.21 those writers
/// emit stub bodies, including ldnull/throw for the format named dll_il_recovery.
/// </summary>
public static class Cpp2IlSession
{
    private static readonly object Gate = new();

    public static EngineLoadResult TryLoad(byte[] binary, byte[] metadata, string unityVersion)
    {
        lock (Gate)
        {
            var previous = LibLogger.Writer;
            LibLogger.Writer = new SilentLog();
            var keep = false;
            try
            {
                LibCpp2IlMain.Reset();
                LibCpp2IlMain.Binary = null;
                LibCpp2IlMain.TheMetadata = null;
                var version = UnityVersion.Parse(unityVersion);
                if (!LibCpp2IlMain.Initialize(binary, metadata, version))
                {
                    return new EngineLoadResult { Loaded = false, Diagnostic = "LibCpp2IL Initialize returned false." };
                }

                var methods = new List<EngineMethod>();
                var types = new List<EngineTypeInfo>();
                var meta = LibCpp2IlMain.TheMetadata;
                if (meta is null)
                    return new EngineLoadResult { Loaded = false, Diagnostic = "LibCpp2IL did not retain a metadata object." };

                foreach (var image in meta.imageDefinitions)
                {
                    foreach (var type in image.Types ?? [])
                    {
                        var fields = new List<EngineFieldInfo>();
                        var fieldOrdinal = 0;
                        var openGeneric = IsOpenGeneric(type);
                        foreach (var field in type.Fields ?? [])
                        {
                            var attr = type.FieldAttributes is { } attrs && fieldOrdinal < attrs.Length ? attrs[fieldOrdinal] : 0;
                            var isStatic = (attr & System.Reflection.FieldAttributes.Static) != 0;
                            int? offset = null;
                            try
                            {
                                if (LibCpp2IlMain.Binary is not null)
                                {
                                    var value = LibCpp2IlMain.Binary.GetFieldOffsetFromIndex(type.TypeIndex, fieldOrdinal, field.FieldIndex, type.IsValueType, isStatic);
                                    if (value >= 0)
                                        offset = value;
                                }
                            }
                            catch (Exception)
                            {
                                offset = null;
                            }
                            object? constant = null;
                            var isLiteral = (attr & System.Reflection.FieldAttributes.Literal) != 0;
                            if (isLiteral)
                            {
                                try
                                {
                                    constant = field.DefaultValue?.Value;
                                }
                                catch (Exception)
                                {
                                    constant = null;
                                }
                            }
                            var fieldRef = ToRef(field.FieldType, field.RawFieldType?.Byref == 1);
                            fields.Add(new EngineFieldInfo
                            {
                                ConstantValue = constant,
                                IsLiteral = isLiteral,
                                Name = field.Name ?? "",
                                Type = fieldRef.Display,
                                TypeReference = fieldRef,
                                Access = Metadata.MetadataNames.Access((int)attr & 7),
                                IsStatic = isStatic,
                                Offset = openGeneric && !isStatic ? null : offset
                            });
                            fieldOrdinal++;
                        }
                        var events = new List<string>();
                        foreach (var ev in type.Events ?? [])
                        {
                            if (!string.IsNullOrWhiteSpace(ev.Name))
                                events.Add(ev.Name);
                        }
                        var interfaces = new List<TypeRef>();
                        try
                        {
                            foreach (var implemented in type.Interfaces ?? [])
                                interfaces.Add(ToRef(implemented, false));
                        }
                        catch (Exception)
                        {
                            interfaces.Clear();
                        }
                        types.Add(new EngineTypeInfo
                        {
                            MetadataIndex = type.TypeIndex.IsNonNull ? type.TypeIndex.Value : -1,
                            Image = image.Name ?? "",
                            Namespace = type.Namespace ?? "",
                            Name = type.Name ?? "",
                            BaseType = type.BaseType?.ToString(),
                            BaseTypeRef = type.BaseType is null ? null : ToRef(type.BaseType, type.RawBaseType?.Byref == 1),
                            DeclaringType = type.DeclaringType?.Name,
                            DeclaringTypeId = IndexOf(type.DeclaringType),
                            DisplayName = type.FullName ?? "",
                            IsValueType = type.IsValueType,
                            IsEnum = type.IsEnumType,
                            IsOpenGeneric = openGeneric,
                            IsExplicitLayout = (type.Attributes & System.Reflection.TypeAttributes.ExplicitLayout) != 0,
                            Fields = fields,
                            Events = events,
                            Interfaces = interfaces
                        });
                        foreach (var method in type.Methods ?? [])
                        {
                            ulong address = 0;
                            string? pointerError = null;
                            try
                            {
                                address = method.MethodPointer;
                            }
                            catch (Exception ex)
                            {
                                pointerError = ex.Message;
                            }

                            var parameterTypes = new List<string>();
                            var parameterRefs = new List<TypeRef>();
                            var returnType = "unresolved";
                            TypeRef? returnRef = null;
                            try
                            {
                                returnRef = ToRef(method.ReturnType, method.RawReturnType?.Byref == 1);
                                returnType = returnRef.Display;
                                if (method.Parameters is not null)
                                {
                                    foreach (var parameter in method.Parameters)
                                    {
                                        var parameterRef = ToRef(parameter.Type, parameter.RawType?.Byref == 1);
                                        parameterRefs.Add(parameterRef);
                                        parameterTypes.Add(parameterRef.Display);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                pointerError = (pointerError is null ? "" : pointerError + " ") + ex.Message;
                            }

                            methods.Add(new EngineMethod
                            {
                                Name = method.Name ?? "",
                                ExplicitInterfaceName = ExplicitInterfaceName(type, method),
                                DeclaringType = type.Name ?? "",
                                Namespace = type.Namespace ?? "",
                                Image = image.Name ?? "",
                                MetadataIndex = method.MethodIndex.IsNonNull ? method.MethodIndex.Value : -1,
                                DeclaringTypeId = type.TypeIndex.IsNonNull ? type.TypeIndex.Value : -1,
                                VirtualAddress = address,
                                IsStatic = method.IsStatic,
                                ReturnType = returnType,
                                ReturnTypeRef = returnRef,
                                ParameterTypes = parameterTypes,
                                ParameterTypeRefs = parameterRefs
                            });
                            _ = pointerError;
                        }
                    }
                }

                keep = true;
                return new EngineLoadResult
                {
                    Loaded = true,
                    Diagnostic = "LibCpp2IL loaded the binary and metadata. Method-body status still comes from Il2CppRecover's lifter, not from Cpp2IL dummy DLL output.",
                    Methods = methods,
                    Types = types
                };
            }
            catch (Exception ex)
            {
                return new EngineLoadResult
                {
                    Loaded = false,
                    Diagnostic = "Pinned LibCpp2IL did not finish loading this binary. " + ex.GetType().Name + ": " + ex.Message
                };
            }
            finally
            {
                if (!keep)
                    ReleaseUnlocked();
                LibLogger.Writer = previous;
            }
        }
    }

    /// <summary>Drops the LibCpp2IL load kept alive so metadata slots can be resolved during lifting.</summary>
    public static void Release()
    {
        lock (Gate)
            ReleaseUnlocked();
    }

    private static void ReleaseUnlocked()
    {
        LibCpp2IlMain.Binary = null;
        LibCpp2IlMain.TheMetadata = null;
        LibCpp2IlMain.Reset();
    }

    private static string? ExplicitInterfaceName(LibCpp2IL.Metadata.Il2CppTypeDefinition type, LibCpp2IL.Metadata.Il2CppMethodDefinition method)
    {
        if (string.IsNullOrWhiteSpace(method.Name) || method.Name.Contains('.', StringComparison.Ordinal))
            return null;
        var flags = method.Attributes;
        var access = flags & System.Reflection.MethodAttributes.MemberAccessMask;
        if (access != System.Reflection.MethodAttributes.Private)
            return null;
        if ((flags & System.Reflection.MethodAttributes.Virtual) == 0 || (flags & System.Reflection.MethodAttributes.Final) == 0 || (flags & System.Reflection.MethodAttributes.Abstract) != 0)
            return null;
        MetadataUsage?[]? table;
        try
        {
            table = type.VTable;
        }
        catch (Exception)
        {
            return null;
        }
        if (table is null || method.slot >= table.Length)
            return null;
        if (table[method.slot]?.Value is not LibCpp2IL.Metadata.Il2CppMethodDefinition implemented || string.IsNullOrWhiteSpace(implemented.Name))
            return null;
        LibCpp2IL.Metadata.Il2CppTypeDefinition? owner;
        try
        {
            owner = implemented.DeclaringType;
        }
        catch (Exception)
        {
            return null;
        }
        if (owner is null)
            return null;
        bool isInterface;
        try
        {
            isInterface = owner.IsInterface;
        }
        catch (Exception)
        {
            return null;
        }
        if (isInterface && !string.IsNullOrWhiteSpace(owner.FullName))
            return owner.FullName + "." + implemented.Name;
        // The slot names the implementing method. The interface comes from the vtable range that contains it.
        try
        {
            var offsets = type.InterfaceOffsets;
            if (offsets is null || offsets.Length == 0 || method.slot < 0)
                return null;
            var ordered = offsets.OrderBy(item => item.offset).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var start = ordered[i].offset;
                var end = i + 1 < ordered.Length ? ordered[i + 1].offset : table.Length;
                if (method.slot < start || method.slot >= end)
                    continue;
                var data = ordered[i].Type;
                var definition = data?.baseType;
                if (data is null || data.isGenericType || definition is null || !definition.IsInterface || definition.Methods is null)
                    return null;
                var index = method.slot - start;
                if (index >= definition.Methods.Length)
                    return null;
                var ifaceMethod = definition.Methods[index];
                if (!string.Equals(ifaceMethod.Name, method.Name, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(definition.FullName))
                    return null;
                return definition.FullName + "." + ifaceMethod.Name;
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private static int? IndexOf(LibCpp2IL.Metadata.Il2CppTypeDefinition? type) =>
        type is { TypeIndex.IsNonNull: true } ? type.TypeIndex.Value : null;

    private static bool IsOpenGeneric(LibCpp2IL.Metadata.Il2CppTypeDefinition type)
    {
        var current = type;
        var guard = 0;
        while (guard++ < 32)
        {
            if (current.GenericContainer is not null)
                return true;
            var outer = current.DeclaringType;
            if (outer is null)
                return false;
            current = outer;
        }
        return false;
    }

    private static TypeRef ToRef(LibCpp2IL.Reflection.Il2CppTypeReflectionData? data, bool byref)
    {
        if (data is null)
            return new TypeRef { Kind = TypeRefKind.Unresolved, Display = "unresolved" };
        TypeRef core;
        if (data.isArray)
        {
            core = new TypeRef
            {
                Kind = TypeRefKind.Array,
                Element = ToRef(data.arrayType, false),
                Rank = data.arrayRank,
                Display = data.ToString() ?? "unresolved"
            };
        }
        else if (!data.isType)
        {
            int? position = null;
            var methodGeneric = false;
            try
            {
                var parameter = data.GenericParameter;
                if (parameter is not null)
                {
                    position = parameter.genericParameterIndexInOwner;
                    methodGeneric = parameter.Type == LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_MVAR;
                }
            }
            catch (Exception)
            {
                position = data.variableGenericParamIndex.IsNonNull ? data.variableGenericParamIndex.Value : null;
            }
            core = new TypeRef
            {
                Kind = TypeRefKind.GenericParameter,
                GenericParameterName = data.variableGenericParamName,
                GenericParameterIndex = position ?? (data.variableGenericParamIndex.IsNonNull ? data.variableGenericParamIndex.Value : null),
                MethodGenericParameter = methodGeneric,
                Display = data.ToString() ?? data.variableGenericParamName ?? "unresolved"
            };
        }
        else if (data.isGenericType)
        {
            core = new TypeRef
            {
                Kind = TypeRefKind.GenericInstance,
                DefinitionIndex = IndexOf(data.baseType),
                Arguments = (data.genericParams ?? []).Select(argument => ToRef(argument, false)).ToList(),
                Display = data.ToString() ?? "unresolved"
            };
        }
        else
        {
            core = new TypeRef
            {
                Kind = TypeRefKind.Definition,
                DefinitionIndex = IndexOf(data.baseType),
                Display = data.ToString() ?? "unresolved"
            };
        }
        if (data.isPointer)
            core = new TypeRef { Kind = TypeRefKind.Pointer, Element = core, Display = data.ToString() ?? core.Display + "*" };
        if (byref)
            core = new TypeRef { Kind = TypeRefKind.ByRef, Element = core, Display = core.Display + "&" };
        return core;
    }

    private sealed class SilentLog : LogWriter
    {
        public override void Info(string message) { }
        public override void Warn(string message) { }
        public override void Error(string message) { }
        public override void Verbose(string message) { }
    }
}
