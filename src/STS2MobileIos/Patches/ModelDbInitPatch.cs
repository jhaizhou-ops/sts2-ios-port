using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace STS2MobileIos.Patches;

// Replaces ModelDb.Init() with a two-phase initialization to avoid circular dependency
// crashes. Phase 1 pre-populates the registry with uninitialized objects so cross-type
// references resolve during construction. Phase 2 runs the actual constructors.
//
// iOS port note: the Android original applied/removed a Harmony prefix on
// ModelDb.Contains AT RUNTIME inside InitPrefix. Under static weaving that is
// impossible, so ContainsPrefix is woven permanently and gated on the
// _suppressContains flag instead (the flag logic already existed; the dynamic
// patch/unpatch was redundant belt-and-suspenders).
public static class ModelDbInitPatch
{
    private static bool _suppressContains = false;

    // prefix on MegaCrit.Sts2.Core.Models.ModelDb.Contains — woven permanently,
    // only active while InitPrefix's Phase 2 is running.
    public static bool ContainsPrefix(ref bool __result)
    {
        if (_suppressContains)
        {
            __result = false;
            return false;
        }
        return true;
    }

    // prefix on MegaCrit.Sts2.Core.Models.ModelDb.Init — replaces it entirely.
    public static bool InitPrefix()
    {
        PatchHelper.Log("Running patched ModelDb.Init()");

        var modelDbType = typeof(ModelDb);

        var allSubtypesProp = modelDbType.GetProperty(
            "AllAbstractModelSubtypes",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        );
        var types = (Type[])allSubtypesProp.GetValue(null);

        var getIdMethod = modelDbType.GetMethod(
            "GetId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(Type) },
            null
        );

        var contentByIdField = modelDbType.GetField(
            "_contentById",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        var contentById = contentByIdField.GetValue(null);

        var dictType = contentById.GetType();
        var setItemMethod = dictType.GetMethod("set_Item");

        // Phase 1: Pre-populate dictionary with uninitialized objects
        PatchHelper.Log(
            $"Phase 1: Pre-registering {types.Length} types with uninitialized objects"
        );

        var typeObjects = new Dictionary<Type, object>();
        int preRegCount = 0;

        // iOS 卡死定位: 每步写进度文件。用纯 .NET 路径(不依赖 Godot 接口),首次失败响亮报出。
        string progressPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
            "modeldb_progress.txt"
        );
        try { File.WriteAllText(progressPath, "INIT_START"); PatchHelper.Log($"progress → {progressPath}"); }
        catch (Exception e) { PatchHelper.Log($"progress 写失败: {e.Message}"); progressPath = null; }
        void Prog(string s) { try { if (progressPath != null) File.WriteAllText(progressPath, s); } catch { } }

        for (int i = 0; i < types.Length; i++)
        {
            try
            {
                var type = types[i];
                Prog($"P1 {i}/{types.Length} {type.FullName} :getId");
                var id = getIdMethod.Invoke(null, new object[] { type });
                Prog($"P1 {i}/{types.Length} {type.FullName} :getUninit");
                var model = RuntimeHelpers.GetUninitializedObject(type);
                Prog($"P1 {i}/{types.Length} {type.FullName} :setItem");
                setItemMethod.Invoke(contentById, new[] { id, model });
                typeObjects[type] = model;
                preRegCount++;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"Phase 1 - Failed to pre-register {types[i].Name}: {ex.Message}");
            }
        }
        Prog($"P1 COMPLETE {preRegCount}/{types.Length}");

        PatchHelper.Log($"Phase 1 complete: {preRegCount} types pre-registered");

        // Phase 2: Run constructors on pre-allocated objects. Contains() is
        // suppressed via the permanently woven ContainsPrefix so constructors
        // don't short-circuit when they check if their type is already registered.
        PatchHelper.Log("Phase 2: Running constructors");

        _suppressContains = true;

        int successCount = 0;
        var failed = new List<Type>();

        try
        {
            foreach (var type in types)
            {
                if (!typeObjects.ContainsKey(type))
                    continue;

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);

                    var ctor = type.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null
                    );
                    if (ctor != null)
                    {
                        ctor.Invoke(typeObjects[type], null);
                    }

                    successCount++;
                }
                catch (Exception ex)
                {
                    failed.Add(type);
                    var inner = ex;
                    while (inner.InnerException != null)
                        inner = inner.InnerException;
                    PatchHelper.Log(
                        $"Phase 2 - Failed {type.Name}: {inner.GetType().Name}: {inner.Message}"
                    );
                }
            }
        }
        finally
        {
            _suppressContains = false;
        }

        if (failed.Count > 0)
        {
            PatchHelper.Log(
                $"WARNING: {failed.Count}/{types.Length} types had constructor errors:"
            );
            foreach (var type in failed)
                PatchHelper.Log($"  - {type.FullName}");
        }
        else
        {
            PatchHelper.Log($"All {successCount} model types registered successfully");
        }

        return false;
    }
}
