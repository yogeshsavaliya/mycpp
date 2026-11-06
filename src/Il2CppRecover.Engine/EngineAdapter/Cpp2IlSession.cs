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
}

public sealed class EngineTypeInfo
{
    public int MetadataIndex { get; init; } = -1;
    public required string Image { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public string? BaseType { get; init; }
    public string? DeclaringType { get; init; }
    public bool IsValueType { get; init; }
    public bool IsEnum { get; init; }
    public IReadOnlyList<EngineFieldInfo> Fields { get; init; } = [];
    public IReadOnlyList<string> Events { get; init; } = [];
}

public sealed class EngineFieldInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
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
                            fields.Add(new EngineFieldInfo
                            {
                                ConstantValue = constant,
                                IsLiteral = isLiteral,
                                Name = field.Name ?? "",
                                Type = field.FieldType?.ToString() ?? "unresolved",
                                Access = Metadata.MetadataNames.Access((int)attr & 7),
                                IsStatic = isStatic,
                                Offset = offset
                            });
                            fieldOrdinal++;
                        }
                        var events = new List<string>();
                        foreach (var ev in type.Events ?? [])
                        {
                            if (!string.IsNullOrWhiteSpace(ev.Name))
                                events.Add(ev.Name);
                        }
                        types.Add(new EngineTypeInfo
                        {
                            MetadataIndex = type.TypeIndex.IsNonNull ? type.TypeIndex.Value : -1,
                            Image = image.Name ?? "",
                            Namespace = type.Namespace ?? "",
                            Name = type.Name ?? "",
                            BaseType = type.BaseType?.ToString(),
                            DeclaringType = type.DeclaringType?.Name,
                            IsValueType = type.IsValueType,
                            IsEnum = type.IsEnumType,
                            Fields = fields,
                            Events = events
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
                            var returnType = "unresolved";
                            try
                            {
                                if (method.ReturnType is not null)
                                    returnType = method.ReturnType.ToString() ?? "unresolved";
                                if (method.Parameters is not null)
                                    parameterTypes.AddRange(method.Parameters.Select(p => p.Type?.ToString() ?? "unresolved"));
                            }
                            catch (Exception ex)
                            {
                                pointerError = (pointerError is null ? "" : pointerError + " ") + ex.Message;
                            }

                            methods.Add(new EngineMethod
                            {
                                Name = method.Name ?? "",
                                DeclaringType = type.Name ?? "",
                                Namespace = type.Namespace ?? "",
                                Image = image.Name ?? "",
                                MetadataIndex = method.MethodIndex.IsNonNull ? method.MethodIndex.Value : -1,
                                VirtualAddress = address,
                                IsStatic = method.IsStatic,
                                ReturnType = returnType,
                                ParameterTypes = parameterTypes
                            });
                            _ = pointerError;
                        }
                    }
                }

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
                LibCpp2IlMain.Binary = null;
                LibCpp2IlMain.TheMetadata = null;
                LibCpp2IlMain.Reset();
                LibLogger.Writer = previous;
            }
        }
    }

    private sealed class SilentLog : LogWriter
    {
        public override void Info(string message) { }
        public override void Warn(string message) { }
        public override void Error(string message) { }
        public override void Verbose(string message) { }
    }
}
