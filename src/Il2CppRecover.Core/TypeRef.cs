namespace Il2CppRecover;

/// <summary>
/// A metadata type reference. A definition is identified by its type-definition index,
/// which is unique in one global-metadata.dat. The display text is for messages only.
/// </summary>
public sealed class TypeRef
{
    public TypeRefKind Kind { get; init; }
    public int? DefinitionIndex { get; init; }
    public string Display { get; init; } = "unresolved";
    public IReadOnlyList<TypeRef> Arguments { get; init; } = [];
    public TypeRef? Element { get; init; }
    public int Rank { get; init; }
    public string? GenericParameterName { get; init; }
    public int? GenericParameterIndex { get; init; }
    public bool MethodGenericParameter { get; init; }

    public bool SameAs(TypeRef? other)
    {
        if (other is null || Kind != other.Kind)
            return false;
        return Kind switch
        {
            TypeRefKind.Definition => DefinitionIndex is int id && id == other.DefinitionIndex,
            TypeRefKind.GenericInstance => DefinitionIndex is int id && id == other.DefinitionIndex
                && Arguments.Count == other.Arguments.Count
                && Arguments.Zip(other.Arguments).All(pair => pair.First.SameAs(pair.Second)),
            TypeRefKind.Array => Rank == other.Rank && Element is not null && Element.SameAs(other.Element),
            TypeRefKind.Pointer or TypeRefKind.ByRef => Element is not null && Element.SameAs(other.Element),
            TypeRefKind.GenericParameter => MethodGenericParameter == other.MethodGenericParameter
                && GenericParameterIndex == other.GenericParameterIndex
                && GenericParameterName == other.GenericParameterName,
            _ => false
        };
    }
}

public enum TypeRefKind
{
    Unresolved,
    Definition,
    GenericInstance,
    Array,
    Pointer,
    ByRef,
    GenericParameter
}

public static class TypeIdentity
{
    /// <summary>
    /// Two metadata types are the same only when both definition indexes are present and equal.
    /// A pair with no indexes is a synthetic fixture, whose display text is its only identity.
    /// </summary>
    public static bool Same(int? leftId, string? leftName, int? rightId, string? rightName)
    {
        if (leftId is int left && rightId is int right)
            return left == right;
        if (leftId is not null || rightId is not null)
            return false;
        return string.Equals(leftName, rightName, StringComparison.Ordinal);
    }
}
