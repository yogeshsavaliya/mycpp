namespace Il2CppRecover.Recovery;

/// <summary>
/// Arm64 field offsets inside <c>Il2CppClass</c>. The numbers come from
/// <c>il2cpp-class-internals.h</c> in Unity 2022.3 (the 2022.3.8f1 header, which has the same
/// member order as 2022.3.4f1): two 16-byte <c>Il2CppType</c> values, then pointers through
/// <c>static_fields</c> at 0xB8, then <c>cctor_finished_or_no_cctor</c> at 0xE0.
/// SnakeWarzOnline (metadata 29, Unity 2022.3.4f1) loads those exact offsets after a type slot.
/// A metadata version that is not in this table is not given either offset.
/// </summary>
public static class Il2CppClassLayout
{
    public static bool TryGet(int metadataVersion, out int staticFields, out int cctorFinished)
    {
        if (metadataVersion == 29)
        {
            staticFields = 0xB8;
            cctorFinished = 0xE0;
            return true;
        }
        staticFields = 0;
        cctorFinished = 0;
        return false;
    }
}

/// <summary>AAPCS64 homogeneous float aggregates the lifter can split into scalar lanes.</summary>
public static class HfaType
{
    public static bool TryComponents(string? declared, out string[] names)
    {
        names = declared switch
        {
            "UnityEngine.Vector2" => ["x", "y"],
            "UnityEngine.Vector3" => ["x", "y", "z"],
            "UnityEngine.Vector4" or "UnityEngine.Quaternion" => ["x", "y", "z", "w"],
            "UnityEngine.Color" => ["r", "g", "b", "a"],
            _ => []
        };
        return names.Length > 0;
    }
}
