// STS2Weaver — 把 Harmony 风格的 prefix/postfix 钩子静态织入 sts2.dll (iOS NativeAOT 无 JIT,不能运行时打补丁)
// 用法: STS2Weaver <sts2.dll> <hooks.dll> <manifest.json> <output.dll>
//
// manifest.json: { "patches": [ { "targetType", "targetMethod", "kind": "prefix"|"postfix",
//                                 "hookType", "hookMethod" } ] }
//
// 语义(与 HarmonyLib 对齐,仅实现本项目补丁用到的子集):
//   prefix 返回 void  → 原方法开头插入调用
//   prefix 返回 bool  → 开头插入调用; 返回 false 则跳过原方法体直接 return
//                       (返回值方法: 返回 __result 局部变量,未绑定 __result 时返回 default)
//   postfix           → 每个 return 点插入调用(经单出口改写)
//   钩子参数绑定: __instance → this; __result / ref __result → 返回值局部变量;
//                 其余按参数名匹配原方法参数(支持 ref)
// 不支持: transpiler / finalizer / __state / ___field 注入 / 泛型目标 / 重载目标 — 遇到即报错退出。

using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: STS2Weaver <sts2.dll> <hooks.dll> <manifest.json> <output.dll>");
    return 2;
}

string targetPath = args[0], hooksPath = args[1], manifestPath = args[2], outputPath = args[3];

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath)));
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(hooksPath)));

var readParams = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
var targetAsm = AssemblyDefinition.ReadAssembly(targetPath, readParams);
var hooksAsm = AssemblyDefinition.ReadAssembly(hooksPath, readParams);
var module = targetAsm.MainModule;

var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("manifest 解析失败");

// stripReadonly: 去掉指定静态字段的 initonly 标记(否则 .NET8 运行时反射 SetValue 会抛 FieldAccessException)
foreach (var s in manifest.StripReadonly ?? new())
{
    var t = module.GetType(s.Type) ?? throw new InvalidOperationException($"stripReadonly 类型不存在: {s.Type}");
    foreach (var fname in s.Fields)
    {
        var f = t.Fields.FirstOrDefault(x => x.Name == fname)
            ?? throw new InvalidOperationException($"stripReadonly 字段不存在: {s.Type}.{fname}");
        f.IsInitOnly = false;
        Console.WriteLine($"[OK] strip-ro {s.Type}.{fname}");
    }
}

// patchConst: 替换指定类型(含嵌套状态机)方法体内的 int 字面量 — 用于 const 内联后无法反射改的值(如 AutoSlayer maxFloor=49)
foreach (var pc in manifest.PatchConsts ?? new())
{
    var t = module.GetType(pc.Type) ?? throw new InvalidOperationException($"patchConst 类型不存在: {pc.Type}");
    int count = 0;
    var allTypes = new List<TypeDefinition> { t };
    allTypes.AddRange(t.NestedTypes);
    foreach (var td in allTypes)
        foreach (var m in td.Methods.Where(x => x.HasBody))
        {
            if (pc.MethodContains != null && !td.Name.Contains(pc.MethodContains) && !m.Name.Contains(pc.MethodContains)) continue;
            foreach (var ins in m.Body.Instructions)
            {
                bool hit = (ins.OpCode == OpCodes.Ldc_I4_S && (sbyte)ins.Operand == pc.FromInt)
                        || (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == pc.FromInt);
                if (!hit) continue;
                ins.OpCode = OpCodes.Ldc_I4; ins.Operand = pc.ToInt; count++;
                Console.WriteLine($"[OK] patch-const {td.Name}.{m.Name}: {pc.FromInt}→{pc.ToInt}");
            }
        }
    if (count == 0) throw new InvalidOperationException($"patchConst 没有命中任何 {pc.FromInt}: {pc.Type} ({pc.MethodContains})");
}

// redirectCalls: 把指定命名空间下所有对某方法的调用改指到钩子(签名须一致) — 如 AutoSlay 的 Task.Delay → 1ms 版
foreach (var rc in manifest.RedirectCalls ?? new())
{
    var hookTypeDef = hooksAsm.MainModule.GetType(rc.HookType)
        ?? throw new InvalidOperationException($"redirect 钩子类型不存在: {rc.HookType}");
    var hook = hookTypeDef.Methods.SingleOrDefault(m => m.Name == rc.HookMethod && m.IsStatic && m.IsPublic)
        ?? throw new InvalidOperationException($"redirect 钩子方法不存在: {rc.HookMethod}");
    var hookRef = module.ImportReference(hook);
    int n = 0;
    foreach (var td in module.GetTypes())   // 含嵌套类型(async 状态机)
    {
        if (!td.FullName.StartsWith(rc.NamespacePrefix)) continue;
        foreach (var m in td.Methods.Where(x => x.HasBody))
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
                if (ins.Operand is not MethodReference mr) continue;
                bool match;
                if (rc.GenericArg != null)
                {
                    // 泛型实例调用(如 Rng.NextItem<NEventOptionButton>): 按元方法+泛型实参匹配。
                    // 实例方法→静态钩子: 栈型一致(this 变第一个参数), 调用方自查签名对齐。
                    match = mr is GenericInstanceMethod gim
                        && gim.ElementMethod.Name == rc.TargetMethod
                        && gim.ElementMethod.DeclaringType.FullName == rc.TargetType
                        && gim.GenericArguments.Count == 1
                        && gim.GenericArguments[0].FullName == rc.GenericArg;
                }
                else
                {
                    // 参数类型逐个比对: 避免误伤重载(如 Delay(TimeSpan,ct) vs Delay(int,ct))
                    match = mr.DeclaringType.FullName == rc.TargetType && mr.Name == rc.TargetMethod
                        && mr.Parameters.Count == hook.Parameters.Count
                        && mr.Parameters.Select((pp, i) => pp.ParameterType.FullName == hook.Parameters[i].ParameterType.FullName).All(x => x);
                }
                if (!match) continue;
                ins.Operand = hookRef;
                ins.OpCode = OpCodes.Call;   // 静态钩子必须用 call(callvirt 对静态方法是非法 IL)
                n++;
            }
    }
    Console.WriteLine($"[OK] redirect {rc.TargetType}.{rc.TargetMethod}({string.Join(",", hook.Parameters.Select(x => x.ParameterType.Name))}) → {rc.HookMethod} x{n} (范围: {rc.NamespacePrefix})");
    if (n == 0) throw new InvalidOperationException($"redirectCalls 没有命中任何调用: {rc.TargetType}.{rc.TargetMethod}");
}

int woven = 0, failed = 0;
foreach (var p in manifest.Patches)
{
    try
    {
        WeaveOne(p);
        woven++;
        Console.WriteLine($"[OK] {p.Kind,-7} {p.TargetType}.{p.TargetMethod} <= {p.HookType}.{p.HookMethod}");
    }
    catch (Exception e)
    {
        failed++;
        Console.Error.WriteLine($"[FAIL] {p.Kind} {p.TargetType}.{p.TargetMethod}: {e.Message}");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"织入失败 {failed} 处,拒绝写出产物 (要么全对要么不出货)");
    return 1;
}

targetAsm.Write(outputPath);
Console.WriteLine($"完成: {woven} 处织入 → {outputPath}");
return 0;

void WeaveOne(PatchEntry p)
{
    var targetType = module.GetType(p.TargetType)
        ?? throw new InvalidOperationException($"目标类型不存在: {p.TargetType}");
    var candidates = targetType.Methods.Where(m => m.Name == p.TargetMethod).ToList();
    if (candidates.Count == 0) throw new InvalidOperationException($"目标方法不存在: {p.TargetMethod}");
    if (candidates.Count > 1 && p.TargetParams != null)
    {
        // 按参数类型名消歧(匹配简单名或 FullName)
        candidates = candidates.Where(m =>
            m.Parameters.Count == p.TargetParams.Count &&
            m.Parameters.Select((pr, i) => pr.ParameterType.Name == p.TargetParams[i] || pr.ParameterType.FullName == p.TargetParams[i]).All(x => x)
        ).ToList();
    }
    if (candidates.Count > 1) throw new InvalidOperationException($"目标方法有重载({candidates.Count}个),需 targetParams 消歧: {p.TargetMethod}");
    var target = candidates[0];
    if (target.HasGenericParameters) throw new InvalidOperationException("不支持泛型目标方法");
    if (!target.HasBody) throw new InvalidOperationException("目标方法无方法体");

    var hookTypeDef = hooksAsm.MainModule.GetType(p.HookType)
        ?? throw new InvalidOperationException($"钩子类型不存在: {p.HookType}");
    var hook = hookTypeDef.Methods.SingleOrDefault(m => m.Name == p.HookMethod && m.IsStatic && m.IsPublic)
        ?? throw new InvalidOperationException($"钩子方法不存在或非 public static: {p.HookMethod}");

    bool isPrefix = p.Kind.Equals("prefix", StringComparison.OrdinalIgnoreCase);
    bool isPostfix = p.Kind.Equals("postfix", StringComparison.OrdinalIgnoreCase);
    if (!isPrefix && !isPostfix) throw new InvalidOperationException($"未知 kind: {p.Kind}");

    var hookReturnsBool = hook.ReturnType.MetadataType == MetadataType.Boolean;
    var hookReturnsVoid = hook.ReturnType.MetadataType == MetadataType.Void;
    if (isPrefix && !hookReturnsBool && !hookReturnsVoid)
        throw new InvalidOperationException("prefix 钩子必须返回 bool 或 void");
    if (isPostfix && !hookReturnsVoid)
        throw new InvalidOperationException("postfix 钩子必须返回 void");

    var body = target.Body;
    body.InitLocals = true;
    body.SimplifyMacros();
    var il = body.GetILProcessor();
    var returnsValue = target.ReturnType.MetadataType != MetadataType.Void;

    // __result 局部变量(按需创建)
    VariableDefinition resultVar = null;
    VariableDefinition EnsureResultVar()
    {
        if (!returnsValue) throw new InvalidOperationException("目标方法返回 void,钩子不能绑定 __result");
        resultVar ??= AddLocal(body, target.ReturnType);
        return resultVar;
    }

    // 生成钩子实参加载指令
    List<Instruction> BuildArgLoads(bool inPostfixEpilogue)
    {
        var loads = new List<Instruction>();
        foreach (var hp in hook.Parameters)
        {
            var name = hp.Name;
            if (name == "__instance")
            {
                if (target.IsStatic) throw new InvalidOperationException("静态目标方法无 __instance");
                loads.Add(il.Create(OpCodes.Ldarg_0));
            }
            else if (name == "__result")
            {
                var v = EnsureResultVar();
                if (hp.ParameterType.IsByReference) loads.Add(il.Create(OpCodes.Ldloca, v));
                else
                {
                    if (!inPostfixEpilogue) throw new InvalidOperationException("prefix 的 __result 必须是 ref");
                    loads.Add(il.Create(OpCodes.Ldloc, v));
                }
            }
            else
            {
                var op = target.Parameters.FirstOrDefault(x => x.Name == name)
                    ?? throw new InvalidOperationException($"钩子参数 '{name}' 在目标方法中找不到同名参数");
                loads.Add(hp.ParameterType.IsByReference && !op.ParameterType.IsByReference
                    ? il.Create(OpCodes.Ldarga, op)
                    : il.Create(OpCodes.Ldarg, op));
            }
        }
        return loads;
    }

    var hookRef = module.ImportReference(hook);

    if (isPrefix)
    {
        var first = body.Instructions[0];
        var seq = BuildArgLoads(inPostfixEpilogue: false);
        seq.Add(il.Create(OpCodes.Call, hookRef));
        if (hookReturnsBool)
        {
            seq.Add(il.Create(OpCodes.Brtrue, first)); // true → 继续原方法
            if (returnsValue) seq.Add(il.Create(OpCodes.Ldloc, resultVar ?? EnsureResultVar()));
            seq.Add(il.Create(OpCodes.Ret));
        }
        foreach (var ins in seq) il.InsertBefore(first, ins);
        // 注意: 原有跳转若指向 first 会绕过 prefix — 语义正确(prefix 只在入口执行一次)
    }
    else // postfix: 单出口改写
    {
        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (body.HasExceptionHandlers)
        {
            foreach (var h in body.ExceptionHandlers)
                foreach (var r in rets)
                    if (InRange(body, r, h.TryStart, h.TryEnd) || InRange(body, r, h.HandlerStart, h.HandlerEnd))
                        throw new InvalidOperationException("postfix 目标的 return 位于 try/catch 内,当前不支持 — 需要 leave 语义,报出来再处理");
        }
        // 尾部构建 epilogue: [stloc result] [args] call hook [ldloc result] ret
        var epi = new List<Instruction>();
        if (returnsValue) { EnsureResultVar(); epi.Add(il.Create(OpCodes.Stloc, resultVar)); }
        epi.AddRange(BuildArgLoads(inPostfixEpilogue: true));
        epi.Add(il.Create(OpCodes.Call, hookRef));
        if (returnsValue) epi.Add(il.Create(OpCodes.Ldloc, resultVar));
        epi.Add(il.Create(OpCodes.Ret));
        var epiHead = epi[0];
        foreach (var ins in epi) body.Instructions.Add(ins);
        foreach (var r in rets) { r.OpCode = OpCodes.Br; r.Operand = epiHead; }
    }

    body.OptimizeMacros();
}

static VariableDefinition AddLocal(MethodBody body, TypeReference type)
{
    var v = new VariableDefinition(type);
    body.Variables.Add(v);
    return v;
}

static bool InRange(MethodBody body, Instruction ins, Instruction start, Instruction end)
{
    if (start == null) return false;
    int i = body.Instructions.IndexOf(ins), s = body.Instructions.IndexOf(start);
    int e = end == null ? body.Instructions.Count : body.Instructions.IndexOf(end);
    return i >= s && i < e;
}

record Manifest(List<PatchEntry> Patches, List<StripEntry>? StripReadonly = null, List<ConstEntry>? PatchConsts = null, List<RedirectEntry>? RedirectCalls = null);
// GenericArg: 可选 — 匹配泛型实例调用(如 NextItem<NEventOptionButton>), 按泛型实参全名过滤
record RedirectEntry(string NamespacePrefix, string TargetType, string TargetMethod, string HookType, string HookMethod, string? GenericArg = null);
record StripEntry(string Type, List<string> Fields);
// MethodContains: 匹配方法名或嵌套类型名(async 状态机如 <PlayRunAsync>d__N)
record ConstEntry(string Type, int FromInt, int ToInt, string? MethodContains = null);
// TargetParams: 可选, 重载消歧用 — 按参数类型名(FullName 或简单名)匹配。null=方法名唯一时直接用。
record PatchEntry(string TargetType, string TargetMethod, string Kind, string HookType, string HookMethod, List<string>? TargetParams = null);
