using Il2CppRecover.Recovery;
using LibCpp2IL;

namespace Il2CppRecover.EngineAdapter;

/// <summary>
/// Reads an IL2CPP metadata-usage slot from the loaded binary. v27 and later store an encoded
/// token. On the Unity 2022.3 / metadata 29.1 SnakeWarzOnline binary the instruction loads a
/// pointer, and the token is the word at that pointer (slot 0x1C7AAA0 holds 0x1CF26B8, which
/// decodes as a MethodRef). A direct token is tried first. A read that does not decode is left
/// unresolved; no name is invented.
/// </summary>
public static class MetadataSlotResolver
{
    public static MetadataSlot? TryResolve(ulong address)
    {
        if (LibCpp2IlMain.Binary is null || LibCpp2IlMain.TheMetadata is null)
            return null;
        try
        {
            var direct = LibCpp2IlMain.GetAnyGlobalByAddress(address);
            if (direct is { IsValid: true })
                return Describe(direct, valueAtAddress: true);
            if (!LibCpp2IlMain.Binary.TryMapVirtualAddressToRaw(address, out _))
                return null;
            var pointer = LibCpp2IlMain.Binary.ReadPointerAtVirtualAddress(address);
            if (pointer == 0 || pointer == address)
                return null;
            var indirect = LibCpp2IlMain.GetAnyGlobalByAddress(pointer);
            return indirect is { IsValid: true } ? Describe(indirect, valueAtAddress: false) : null;
        }
        catch (Exception ex)
        {
            _ = ex;
            return null;
        }
    }

    private static MetadataSlot? Describe(MetadataUsage usage, bool valueAtAddress)
    {
        var kind = usage.Type switch
        {
            MetadataUsageType.Type or MetadataUsageType.TypeInfo => MetadataSlotKind.Type,
            MetadataUsageType.MethodDef or MetadataUsageType.MethodRef => MetadataSlotKind.Method,
            MetadataUsageType.FieldInfo => MetadataSlotKind.Field,
            MetadataUsageType.StringLiteral => MetadataSlotKind.StringLiteral,
            _ => (MetadataSlotKind?)null
        };
        if (kind is null)
            return null;
        string display;
        try
        {
            display = usage.Value?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            _ = ex;
            return null;
        }
        if (string.IsNullOrWhiteSpace(display))
            return null;
        return new MetadataSlot(kind.Value, display, valueAtAddress);
    }
}
