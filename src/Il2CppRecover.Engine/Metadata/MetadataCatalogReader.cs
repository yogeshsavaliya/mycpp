using System.Reflection;
using AssetRipper.Primitives;
using LibCpp2IL;
using LibCpp2IL.Logging;
using LibCpp2IL.Metadata;

namespace Il2CppRecover.Metadata;

public static class MetadataCatalogReader
{
    private static readonly object Gate = new();

    public static MetadataCatalog Read(byte[] metadata, string? detectedUnityVersion, string? detectedSource)
    {
        if (metadata.Length < 8 || BitConverter.ToUInt32(metadata, 0) != 0xFAB11BAF)
        {
            return new MetadataCatalog
            {
                Diagnostics = ["Metadata magic is not 0xFAB11BAF. The catalog was not parsed and no version was invented."]
            };
        }

        var headerVersion = BitConverter.ToInt32(metadata, 4);
        if (!TrySelectLayout(headerVersion, detectedUnityVersion, out var layout, out var layoutText, out var layoutSource, out var error))
        {
            return new MetadataCatalog
            {
                HeaderVersion = headerVersion,
                LayoutUnityVersionSource = layoutSource,
                Diagnostics = [error ?? "Metadata catalog was not parsed."]
            };
        }

        lock (Gate)
        {
            var previous = LibLogger.Writer;
            LibLogger.Writer = SilentLog.Instance;
            try
            {
                LibCpp2IlMain.Reset();
                LibCpp2IlMain.Binary = null;
                LibCpp2IlMain.TheMetadata = null;
                var parsed = Il2CppMetadata.ReadFrom(metadata, layout);
                LibCpp2IlMain.TheMetadata = parsed;
                var images = new List<CatalogImage>();
                var diagnostics = new List<string>
                {
                    "Parameter and return types are unresolved unless a native binary was loaded. Type indices point at the Il2CppType table in the binary, which is not part of global-metadata.dat.",
                    "Layout selector: " + layoutSource
                };
                foreach (var image in parsed.imageDefinitions)
                {
                    var types = new List<CatalogType>();
                    var imageTypes = image.Types ?? [];
                    for (var typeOrdinal = 0; typeOrdinal < imageTypes.Length; typeOrdinal++)
                    {
                        var type = imageTypes[typeOrdinal];
                        var fields = ReadFields(metadata, parsed, type);
                        var methods = new List<CatalogMethod>();
                        foreach (var method in type.Methods ?? [])
                        {
                            methods.Add(new CatalogMethod
                            {
                                Name = method.Name ?? "",
                                MetadataIndex = method.MethodIndex.Value,
                                Token = method.token,
                                IsStatic = method.IsStatic,
                                IsAbstract = (method.Attributes & MethodAttributes.Abstract) != 0,
                                IsVirtual = (method.Attributes & MethodAttributes.Virtual) != 0 && (method.Attributes & MethodAttributes.Abstract) == 0,
                                Access = MetadataNames.Access((int)method.Attributes & 7),
                                IsExternal = (method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
                                             (method.MethodImplAttributes & MethodImplAttributes.InternalCall) != 0,
                                IsGeneric = method.genericContainerIndex.IsNonNull,
                                ParameterCount = method.parameterCount,
                                DeclaringTypeIndex = method.declaringTypeIdx.Value,
                                Parameters = ReadParameters(metadata, parsed, method),
                                ReturnType = "unresolved"
                            });
                        }
                        types.Add(new CatalogType
                        {
                            Name = type.Name ?? "",
                            Namespace = type.Namespace ?? "",
                            MetadataIndex = image.firstTypeIndex.Value + typeOrdinal,
                            Token = type.Token,
                            IsValueType = type.IsValueType,
                            IsEnum = type.IsEnumType,
                            Fields = fields,
                            Methods = methods
                        });
                    }
                    for (var typeOrdinal = 0; typeOrdinal < imageTypes.Length; typeOrdinal++)
                    {
                        var parent = imageTypes[typeOrdinal];
                        if (parent.NestedTypeCount <= 0)
                            continue;
                        var parentIndex = image.firstTypeIndex.Value + typeOrdinal;
                        foreach (var childIndex in parsed.GetNestedTypeIndicesFromIndexAndCount(parent.NestedTypesStart, parent.NestedTypeCount))
                        {
                            var child = types.FirstOrDefault(t => t.MetadataIndex == childIndex);
                            if (child is not null)
                                child.DeclaringTypeIndex = parentIndex;
                        }
                    }
                    images.Add(new CatalogImage { Name = image.Name ?? "", Types = types });
                }

                return new MetadataCatalog
                {
                    HeaderVersion = headerVersion,
                    LibCpp2IlLayoutVersion = parsed.MetadataVersion,
                    LayoutUnityVersionUsed = layoutText,
                    LayoutUnityVersionSource = layoutSource,
                    Images = images,
                    Diagnostics = diagnostics,
                    TypesResolvedFromBinary = false
                };
            }
            catch (Exception ex)
            {
                return new MetadataCatalog
                {
                    HeaderVersion = headerVersion,
                    LayoutUnityVersionUsed = layoutText,
                    LayoutUnityVersionSource = layoutSource,
                    Diagnostics = ["LibCpp2IL could not read this metadata file: " + ex.GetType().Name + ": " + ex.Message]
                };
            }
            finally
            {
                LibCpp2IlMain.TheMetadata = null;
                LibCpp2IlMain.Binary = null;
                LibCpp2IlMain.Reset();
                LibLogger.Writer = previous;
            }
        }
    }

    public static bool TrySelectLayout(int headerVersion, string? detectedUnityVersion, out UnityVersion layout, out string? layoutText, out string source, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(detectedUnityVersion))
        {
            layout = UnityVersion.Parse(detectedUnityVersion);
            layoutText = detectedUnityVersion;
            source = "detected-unity-version";
            if (headerVersion is < 23 or > 106)
            {
                error = $"Metadata header version {headerVersion} is outside the pinned LibCpp2IL range 23-106. The header value was not changed.";
                return false;
            }
            return true;
        }

        if (headerVersion == 31)
        {
            layout = UnityVersion.Parse("2022.3.0f1");
            layoutText = "2022.3.0f1";
            source = "internal-selector-for-header-31-not-a-detected-unity-version";
            return true;
        }
        if (headerVersion == 29)
        {
            layout = UnityVersion.Parse("2021.3.0f1");
            layoutText = "2021.3.0f1";
            source = "internal-selector-for-header-29-not-a-detected-unity-version";
            return true;
        }

        layout = default;
        layoutText = null;
        source = "none";
        error = headerVersion is 24 or 27
            ? $"Metadata header version {headerVersion} has more than one table layout. No Unity version string was found, so the tables were not parsed and no sub-revision was guessed."
            : $"Metadata header version {headerVersion} was read. A Unity version string is required before this revision's tables are parsed. None was found, so the catalog was not parsed.";
        return false;
    }

    private static List<CatalogParameter> ReadParameters(byte[] metadata, Il2CppMetadata parsed, Il2CppMethodDefinition method)
    {
        if (method.parameterCount == 0 || method.parameterStart.IsNull)
            return [];
        var list = new List<CatalogParameter>();
        var element = parsed.MetadataVersion >= 38f ? 0 : 12;
        if (element == 0)
            return [];
        for (var i = 0; i < method.parameterCount; i++)
        {
            var offset = parsed.metadataHeader.parameters.Offset + (method.parameterStart.Value + i) * element;
            if (offset < 0 || offset + 4 > metadata.Length)
                break;
            var nameIndex = BitConverter.ToInt32(metadata, offset);
            list.Add(new CatalogParameter
            {
                Name = parsed.GetStringFromIndex(nameIndex),
                MetadataIndex = method.parameterStart.Value + i,
                Type = "unresolved"
            });
        }
        return list;
    }

    private static List<CatalogField> ReadFields(byte[] metadata, Il2CppMetadata parsed, Il2CppTypeDefinition type)
    {
        if (type.FieldCount == 0 || type.FirstFieldIdx.IsNull || parsed.MetadataVersion >= 38f)
            return [];
        var list = new List<CatalogField>();
        for (var i = 0; i < type.FieldCount; i++)
        {
            var offset = parsed.metadataHeader.fields.Offset + (type.FirstFieldIdx.Value + i) * 12;
            if (offset < 0 || offset + 12 > metadata.Length)
                break;
            var nameIndex = BitConverter.ToInt32(metadata, offset);
            var token = BitConverter.ToUInt32(metadata, offset + 8);
            list.Add(new CatalogField
            {
                Name = parsed.GetStringFromIndex(nameIndex),
                MetadataIndex = type.FirstFieldIdx.Value + i,
                Token = token,
                Type = "unresolved"
            });
        }
        return list;
    }

    private sealed class SilentLog : LogWriter
    {
        public static readonly SilentLog Instance = new();
        public override void Info(string message) { }
        public override void Warn(string message) { }
        public override void Error(string message) { }
        public override void Verbose(string message) { }
    }
}
