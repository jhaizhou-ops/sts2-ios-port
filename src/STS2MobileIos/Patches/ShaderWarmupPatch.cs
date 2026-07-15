using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace STS2MobileIos.Patches;

// 首次启动预编译全部着色器,消除游戏中"遇到新画面时现场编译"的卡顿。
// 移植自安卓 ShaderWarmupScreen,但做了三处 iOS 适配:
//  1. 不继承 Node/Control —— 本补丁在独立 assembly(STS2MobileIos),不在 Godot 主 assembly
//     的脚本注册表里,做成 Godot 节点其生命周期回调不会触发。改为纯静态逻辑 + SceneTree 信号驱动。
//  2. 去掉全部 UI(进度条/面板依赖 Launcher.Components),改用 PatchHelper.Log 输出进度。
//  3. 触发点: postfix NGame._EnterTree(游戏根节点进树,SceneTree+资源系统就绪),async 后台跑,
//     不阻塞(安卓是阻塞式预热屏,我们无 Launcher,改后台;首次会卡一阵,写 marker 后永久跳过)。
public static class ShaderWarmupPatch
{
    private const int WarmupVersion = 5;
    private const int BatchSize = 8;
    private static bool _started = false;

    // postfix on MegaCrit.Sts2.Core.Nodes.NGame._EnterTree
    public static void EnterTreePostfix(Node __instance)
    {
        if (_started)
            return;
        _started = true;
        try
        {
            if (!NeedsWarmup())
            {
                PatchHelper.Log("[ShaderWarmup] 已预热过,跳过");
                return;
            }
            var tree = __instance.GetTree();
            if (tree == null)
            {
                PatchHelper.Log("[ShaderWarmup] SceneTree 不可用,跳过");
                return;
            }
            // fire-and-forget 后台预热
            _ = RunWarmup(tree);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 触发失败: {ex.Message}");
        }
    }

    private static bool NeedsWarmup()
    {
        try
        {
            var markerPath = Path.Combine(OS.GetUserDataDir(), "shader_warmup_version");
            if (File.Exists(markerPath))
                return File.ReadAllText(markerPath).Trim() != WarmupVersion.ToString();
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void WriteVersionMarker()
    {
        try
        {
            File.WriteAllText(
                Path.Combine(OS.GetUserDataDir(), "shader_warmup_version"),
                WarmupVersion.ToString()
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 写 marker 失败: {ex.Message}");
        }
    }

    private static async Task RunWarmup(SceneTree tree)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 等主菜单先加载显示,减少与游戏启动的资源竞争
            for (int f = 0; f < 30; f++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(
                RenderingServer.Singleton,
                RenderingServer.SignalName.FramePostDraw
            );

            var materials = await CollectMaterialsAsync(tree);
            PatchHelper.Log($"[ShaderWarmup] 收集到 {materials.Count} 个待编译着色器");

            if (materials.Count == 0)
            {
                WriteVersionMarker();
                return;
            }

            var viewport = new SubViewport
            {
                Size = new Vector2I(64, 64),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = true,
            };
            tree.Root.AddChild(viewport);

            var whiteImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            whiteImage.SetPixel(0, 0, Colors.White);
            var whiteTex = ImageTexture.CreateFromImage(whiteImage);

            int total = materials.Count;
            for (int i = 0; i < total; i += BatchSize)
            {
                var batchNodes = new List<Node>();
                int batchEnd = Math.Min(i + BatchSize, total);
                for (int j = i; j < batchEnd; j++)
                {
                    try
                    {
                        var node = CreateWarmupNode(materials[j].mat, whiteTex);
                        if (node != null)
                        {
                            viewport.AddChild(node);
                            batchNodes.Add(node);
                        }
                    }
                    catch (Exception ex)
                    {
                        PatchHelper.Log($"[ShaderWarmup] 建节点失败 {materials[j].path}: {ex.Message}");
                    }
                }

                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

                foreach (var node in batchNodes)
                    node.QueueFree();

                if (i % 64 == 0)
                    PatchHelper.Log($"[ShaderWarmup] 进度 {batchEnd}/{total}");
            }

            viewport.QueueFree();
            WriteVersionMarker();
            PatchHelper.Log(
                $"[ShaderWarmup] 完成: {total} 个着色器,耗时 {sw.ElapsedMilliseconds}ms。下次启动将跳过。"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 预热失败: {ex}");
        }
    }

    private static Node CreateWarmupNode(Material mat, ImageTexture whiteTex)
    {
        if (mat is ParticleProcessMaterial particleMat)
        {
            return new GpuParticles2D
            {
                ProcessMaterial = particleMat,
                Amount = 1,
                Emitting = true,
                OneShot = false,
                Texture = whiteTex,
            };
        }
        return new Sprite2D { Texture = whiteTex, Material = mat };
    }

    // 每加载 N 个资源就让一帧, 防止首次从冷 pck 批量加载时主线程连续卡 >10s 触发看门狗。
    private const int YieldEvery = 4;

    private static async Task<List<(string path, Material mat)>> CollectMaterialsAsync(
        SceneTree tree
    )
    {
        // 边加载边按 shader 去重: 每个材质刚加载出来(必然还活着)时立刻算 key 入表。
        // 不能先攒进 dict、末尾再统一算 key —— 高频让帧期间 Godot 会驱逐已加载资源,
        // 到统一去重时材质已被释放, GetShaderKey 抛 ObjectDisposedException 使整个预热中止。
        var unique = new Dictionary<string, (string path, Material mat)>();
        int seen = 0;

        // 1) 先只列路径(纯目录遍历, 便宜), 再异步逐个加载 + 即时去重, 高频让帧。
        var matPaths = new List<string>();
        CollectMaterialPaths("res://", matPaths);
        PatchHelper.Log($"[ShaderWarmup] 扫描 {matPaths.Count} 个材质/着色器资源");
        for (int i = 0; i < matPaths.Count; i++)
        {
            if (LoadAndAdd(matPaths[i], unique))
                seen++;
            if (i % YieldEvery == 0)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        var scenePaths = new List<string>();
        CollectScenePaths("res://scenes", scenePaths);
        PatchHelper.Log($"[ShaderWarmup] 扫描 {scenePaths.Count} 个场景");

        for (int i = 0; i < scenePaths.Count; i++)
        {
            try
            {
                var packed = ResourceLoader.Load<PackedScene>(
                    scenePaths[i],
                    null,
                    ResourceLoader.CacheMode.Reuse
                );
                if (packed != null)
                    seen += ExtractMaterialsFromSceneState(packed, scenePaths[i], unique);
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[ShaderWarmup] 提取场景失败 {scenePaths[i]}: {ex.Message}");
            }
            if (i % YieldEvery == 0)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        PatchHelper.Log($"[ShaderWarmup] {seen} 个材质 → {unique.Count} 个唯一着色器");
        return unique.Values.ToList();
    }

    // 刚加载出的材质立刻算 key 入 unique 表; 单个被回收/异常只跳过不致命。
    // 返回是否成功计入(仅用于统计)。
    private static bool TryAddUnique(
        Dictionary<string, (string path, Material mat)> unique,
        string path,
        Material mat
    )
    {
        try
        {
            unique.TryAdd(GetShaderKey(mat), (path, mat));
            return true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 去重跳过 {path}: {ex.Message}");
            return false;
        }
    }

    private static string GetShaderKey(Material mat)
    {
        if (mat is ShaderMaterial sm && sm.Shader != null)
            return sm.Shader.ResourcePath ?? sm.Shader.GetRid().ToString();
        if (mat is ParticleProcessMaterial)
            return $"particle#{mat.GetRid()}";
        return mat.ResourcePath ?? mat.GetRid().ToString();
    }

    // 只列候选资源路径(便宜的目录遍历, 不加载), 实际加载交给异步循环高频让帧。
    private static void CollectMaterialPaths(string dirPath, List<string> outPaths)
    {
        try
        {
            using var dir = DirAccess.Open(dirPath);
            if (dir == null)
                return;
            dir.ListDirBegin();
            string fileName;
            while ((fileName = dir.GetNext()) != "")
            {
                if (fileName == "." || fileName == "..")
                    continue;
                var fullPath = $"{dirPath}/{fileName}";
                if (dir.CurrentIsDir())
                {
                    if (fileName == "debug")
                        continue;
                    CollectMaterialPaths(fullPath, outPaths);
                    continue;
                }
                var cleanName = fileName.Replace(".remap", "");
                if (
                    !cleanName.EndsWith(".tres")
                    && !cleanName.EndsWith(".gdshader")
                    && !cleanName.EndsWith(".material")
                )
                    continue;
                outPaths.Add($"{dirPath}/{cleanName}");
            }
            dir.ListDirEnd();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 枚举失败 {dirPath}: {ex.Message}");
        }
    }

    // 加载单个材质/着色器资源, 成功则立刻算 key 入 unique 表(趁其还活着)。返回是否计入。
    private static bool LoadAndAdd(
        string cleanPath,
        Dictionary<string, (string path, Material mat)> unique
    )
    {
        try
        {
            if (!ResourceLoader.Exists(cleanPath))
                return false;
            Material mat = null;
            if (cleanPath.EndsWith(".tres"))
            {
                mat =
                    ResourceLoader.Load(cleanPath, "Material", ResourceLoader.CacheMode.Reuse)
                    as Material;
                if (mat == null)
                {
                    var shader =
                        ResourceLoader.Load(cleanPath, "Shader", ResourceLoader.CacheMode.Reuse)
                        as Shader;
                    if (shader != null)
                        mat = new ShaderMaterial { Shader = shader };
                }
            }
            else
            {
                var res = ResourceLoader.Load(cleanPath, null, ResourceLoader.CacheMode.Reuse);
                if (res is Material resMat)
                    mat = resMat;
                else if (res is Shader resShader)
                    mat = new ShaderMaterial { Shader = resShader };
            }
            if (mat != null)
                return TryAddUnique(unique, cleanPath, mat);
            return false;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 加载失败 {cleanPath}: {ex.Message}");
            return false;
        }
    }

    private static void CollectScenePaths(string dirPath, List<string> paths)
    {
        try
        {
            using var dir = DirAccess.Open(dirPath);
            if (dir == null)
                return;
            dir.ListDirBegin();
            string fileName;
            while ((fileName = dir.GetNext()) != "")
            {
                if (fileName == "." || fileName == "..")
                    continue;
                var fullPath = $"{dirPath}/{fileName}";
                if (dir.CurrentIsDir())
                {
                    if (fileName == "debug")
                        continue;
                    CollectScenePaths(fullPath, paths);
                    continue;
                }
                var cleanName = fileName.Replace(".remap", "");
                if (!cleanName.EndsWith(".tscn"))
                    continue;
                var cleanPath = $"{dirPath}/{cleanName}";
                if (ResourceLoader.Exists(cleanPath))
                    paths.Add(cleanPath);
            }
            dir.ListDirEnd();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[ShaderWarmup] 枚举场景失败 {dirPath}: {ex.Message}");
        }
    }

    // 从场景状态里提取材质, 每个刚取出即算 key 入 unique(趁其还活着)。返回计入数量。
    private static int ExtractMaterialsFromSceneState(
        PackedScene packed,
        string scenePath,
        Dictionary<string, (string path, Material mat)> unique
    )
    {
        int added = 0;
        var state = packed.GetState();
        int nodeCount = state.GetNodeCount();
        for (int n = 0; n < nodeCount; n++)
        {
            int propCount = state.GetNodePropertyCount(n);
            for (int p = 0; p < propCount; p++)
            {
                var propName = state.GetNodePropertyName(n, p).ToString();
                if (
                    propName != "material"
                    && propName != "process_material"
                    && propName != "surface_material_override/0"
                )
                    continue;
                try
                {
                    var val = state.GetNodePropertyValue(n, p);
                    Material mat = null;
                    if (val.Obj is Material m)
                        mat = m;
                    else if (val.Obj is Shader shader)
                        mat = new ShaderMaterial { Shader = shader };
                    if (mat != null && TryAddUnique(unique, $"{scenePath}#node{n}#{propName}", mat))
                        added++;
                }
                catch (Exception ex)
                {
                    PatchHelper.Log($"[ShaderWarmup] 读属性失败 {propName}@{scenePath}: {ex.Message}");
                }
            }
        }
        return added;
    }
}
