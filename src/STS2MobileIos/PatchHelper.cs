using System;
using System.Reflection;

namespace STS2MobileIos;

// Logging + reflection helpers for the statically-woven iOS patch set.
// Field()/Method() mirror HarmonyLib.AccessTools semantics: search the type
// AND its base types with all binding flags (plain Type.GetField does NOT
// return private fields declared on base classes).
public static class PatchHelper
{
    private const BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static FieldInfo Field(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, AllFlags | BindingFlags.DeclaredOnly);
            if (f != null)
                return f;
        }
        return null;
    }

    public static MethodInfo Method(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(name, AllFlags | BindingFlags.DeclaredOnly);
            if (m != null)
                return m;
        }
        return null;
    }

    public static void Log(string msg)
    {
        Console.Error.WriteLine($"[STS2MobileIos] {msg}");
    }
}
