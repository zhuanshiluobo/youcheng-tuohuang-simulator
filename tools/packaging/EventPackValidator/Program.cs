using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：EventPackValidator <游戏 Managed 目录> <内容包根目录>");
    return 2;
}

try
{
    string managed = Path.GetFullPath(args[0]);
    string root = Path.GetFullPath(args[1]);
    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        string path = Path.Combine(managed, name.Name + ".dll");
        return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
    };
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "YC.Infrastructure.Lua.dll"));
    _ = assembly.GetType("YC.Infrastructure.Lua.EventChoiceContract", throwOnError: true);
    var type = assembly.GetType("YC.Infrastructure.Lua.ExternalContentPack", throwOnError: true)!;
    var method = type.GetMethod("Load", new[] { typeof(string) }) ?? throw new MissingMethodException(type.FullName, "Load");
    var pack = method.Invoke(null, new object[] { root }) ?? throw new InvalidOperationException("内容加载器没有返回结果。");
    string hash = (string?)type.GetProperty("ContentHash")?.GetValue(pack) ?? throw new InvalidOperationException("内容加载器没有返回指纹。");
    Console.WriteLine("正式加载器校验通过，内容指纹：" + hash);
    Console.WriteLine("加载器程序集身份：" + assembly.ManifestModule.ModuleVersionId);
    return 0;
}
catch (Exception error)
{
    var cause = error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
    Console.Error.WriteLine("正式加载器校验失败：" + cause.Message);
    return 1;
}
