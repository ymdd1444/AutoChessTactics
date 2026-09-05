using System.Reflection;

string root = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

string gameDir = Path.Combine(root, "data_sts2_windows_x86_64");
string modProjectDir = Path.Combine(root, "moddev", "AutoChessTactics");
string modDll = Path.Combine(modProjectDir, ".godot", "mono", "temp", "bin", "Release", "AutoChessTactics.dll");

if (!File.Exists(modDll))
{
    Console.Error.WriteLine("找不到已构建的 AutoChessTactics.dll: " + modDll);
    return 2;
}

// 离线 runner 没有真正加载 Godot GDExtension。
// 新增的 SL 恢复用例会触发日志，因此这里让 mod 跳过游戏日志，
// 避免测试环境在 Godot.OS 静态初始化时崩溃；游戏内不会设置这个变量。
Environment.SetEnvironmentVariable("AUTOCHESS_SUPPRESS_GAME_LOG", "1");

// 这个 runner 目标框架是 net9.0，和游戏一致。
// 不用 PowerShell 直接反射调用，是因为当前 PowerShell 跑在 CLR 10，
// 游戏自带 Harmony 在 CLR 10 下会拒绝打补丁，容易把测试环境问题误判成 mod 问题。
AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string dllName = new AssemblyName(eventArgs.Name).Name + ".dll";
    foreach (string dir in new[] { gameDir, modProjectDir })
    {
        string candidate = Path.Combine(dir, dllName);
        if (File.Exists(candidate))
        {
            return Assembly.LoadFrom(candidate);
        }
    }

    return null;
};

foreach (string dllName in new[] { "sts2.dll", "0Harmony.dll", "GodotSharp.dll" })
{
    string path = Path.Combine(gameDir, dllName);
    if (File.Exists(path))
    {
        Assembly.LoadFrom(path);
    }
}

Assembly modAssembly = Assembly.LoadFrom(modDll);
Type selfTest = modAssembly.GetType("AutoChessTactics.SelfTest", throwOnError: true)!;
MethodInfo runAll = selfTest.GetMethod("RunAll", BindingFlags.Public | BindingFlags.Static)!;
string result = (string)runAll.Invoke(null, Array.Empty<object>())!;

Console.Write(result);
return result.Contains("ALL TESTS PASSED", StringComparison.Ordinal)
    ? 0
    : 1;
