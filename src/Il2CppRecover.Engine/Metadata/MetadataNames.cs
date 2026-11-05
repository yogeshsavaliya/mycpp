namespace Il2CppRecover.Metadata;

public static class MetadataNames
{
    public static string Access(int mask) => mask switch
    {
        1 => "private",
        2 => "private protected",
        3 => "internal",
        4 => "protected",
        5 => "protected internal",
        6 => "public",
        _ => "unresolved"
    };
}
