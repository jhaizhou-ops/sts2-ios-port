using System;
using System.IO;
using Godot;

namespace STS2MobileIos.Patches;

// 战斗/全局 4 倍速。用 Godot 引擎的全局时间缩放 Engine.TimeScale——游戏自带的
// DebugModifyTimescale 就是改这个值,且钳制上限正好 4.0,说明 4x 是官方认可的安全上限
// (不改任何战斗数值:伤害/血量/概率/AI 全不变,只是动画与计时整体播快)。
//
// 注意:这与游戏设置里的 FastMode(靠缩短动画时长实现)是两套机制,会叠加。若嫌过快,
// 玩家把游戏内 FastMode 设回 Normal 即可,只用本 4x。倍率可调(TARGET_SCALE)。
//
// 游戏内除调试快捷键外还有一处会写 Engine.TimeScale:NHitStop(打击停顿慢动作演出,
// CeremonialBeast 犁地/MechaKnight 重斩/Vantom 断肢=一幕 Boss 二阶段转场用)。它先把
// 倍率压到 0.1 再缓升、结束时硬编码写回 1.0,会覆盖我们的 4x——这就是"Boss 二阶段后
// 倍速失效"的根因。解法:织入 NHitStop.SetTimeScale,把它写的值乘上 TARGET_SCALE,
// 慢动作演出相对比例原样保留(0.1→0.4 起步),结束时"恢复"到的正是 4x,两者不再打架。
public static class TimeScalePatch
{
    private const double TARGET_SCALE = 4.0;

    // postfix on MegaCrit.Sts2.Core.Nodes.NGame._Ready
    // NGame 每次进入(启动、从主菜单进对局)都会 _Ready,在此设定全局倍率并保持。
    public static void ReadyPostfix()
    {
        try
        {
            Engine.TimeScale = TARGET_SCALE;
            PatchHelper.Log($"[TimeScale] 全局时间缩放设为 {TARGET_SCALE}x");
        }
        catch (Exception e)
        {
            PatchHelper.Log($"[TimeScale] 设置失败: {e.Message}");
        }
        try
        {
            // 内存额度自检(诊断用, 默认关闭): 容器 Documents 放一个 memcheck_on 文件即开启,
            // 删除即关闭(文件App可控, 无需进游戏)。开启时启动记录一行到 memcheck.txt。
            // os_proc_available_memory = 系统还允许本进程用多少内存;
            // 有 increased-memory-limit 权限 ≈ 7GB+, 没有 ≈ 3GB — 签名是否保住权限一眼可判。
            var docs = OS.GetUserDataDir();
            if (File.Exists(Path.Combine(docs, "memcheck_on")))
            {
                var line =
                    $"{DateTime.Now:MM-dd HH:mm:ss} 剩余内存额度 {OsProcAvailableMemory() / 1048576} MB";
                PatchHelper.Log($"[MemCheck] {line}");
                File.AppendAllText(Path.Combine(docs, "memcheck.txt"), line + "\n");
            }
        }
        catch (Exception e)
        {
            PatchHelper.Log($"[MemCheck] 自检失败: {e.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport(
        "/usr/lib/libSystem.B.dylib",
        EntryPoint = "os_proc_available_memory"
    )]
    private static extern ulong OsProcAvailableMemory();

    // bool-prefix on MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.NHitStop.SetTimeScale(float timeScale)
    // 替换原方法体(返回 false 跳过原体)。原体是 Engine.SetTimeScale(timeScale);
    // 这里改为乘上我们的倍率,慢动作演出与 4x 叠加而非互相覆盖。
    // 该方法是游戏内打击停顿写时间倍率的唯一咽喉(起步 0.1/逐帧缓升/结尾 1.0 三处
    // 全走它),织入一处即可覆盖全部路径,含动画轨道直接调用的情况。
    public static bool HitStopSetTimeScalePrefix(float timeScale)
    {
        try
        {
            Engine.TimeScale = timeScale * TARGET_SCALE;
        }
        catch (Exception e)
        {
            PatchHelper.Log($"[TimeScale] HitStop 叠加失败: {e.Message}");
        }
        return false;
    }
}
