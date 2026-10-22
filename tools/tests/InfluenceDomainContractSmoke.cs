using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

// 仅作受管理程序集补充验证；指定 Unity Test Runner 验收仍需单独执行。
public static class InfluenceDomainContractSmoke
{
    private static string root;
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        string file = Path.Combine(root, "Temp", "bin", "Debug", name + ".dll");
        if (File.Exists(file)) return Assembly.LoadFrom(file);
        file = Path.Combine(root, "Library", "ScriptAssemblies", name + ".dll");
        if (File.Exists(file)) return Assembly.LoadFrom(file);
        if (name == "nunit.framework") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.ext.nunit@1.0.6\net35\unity-custom\nunit.framework.dll"));
        if (name == "Newtonsoft.Json") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.nuget.newtonsoft-json@3.2.1\Runtime\Newtonsoft.Json.dll"));
        return null;
    }
    public static int Main(string[] args)
    {
        root = args.Length == 0 ? Environment.CurrentDirectory : Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root, @"Temp\bin\Debug\YC.Tests.EditMode.dll"));
        int passed = 0, failed = 0;
        string[] fixtures = args.Length < 2 ? new[] { "YC.Tests.EditMode.ExpansionRules.InfluenceOperationDomainTests" } : args[1].Split(';');
        foreach (string fixtureName in fixtures)
        {
        Type fixtureType = assembly.GetType(fixtureName, true);
        object suite = Activator.CreateInstance(fixtureType);
        foreach (MethodInfo setup in fixtureType.GetMethods())
            foreach (object attribute in setup.GetCustomAttributes(false))
                if (attribute.GetType().FullName == "NUnit.Framework.OneTimeSetUpAttribute") setup.Invoke(suite, null);
        foreach (MethodInfo method in fixtureType.GetMethods())
        {
            var cases = new List<object[]>();
            foreach (object attribute in method.GetCustomAttributes(false))
            {
                if (attribute.GetType().FullName == "NUnit.Framework.TestAttribute") cases.Add(new object[0]);
                if (attribute.GetType().FullName == "NUnit.Framework.TestCaseAttribute") cases.Add((object[])attribute.GetType().GetProperty("Arguments").GetValue(attribute, null));
            }
            foreach (object[] arguments in cases)
            {
                try
                {
                    object fixture = Activator.CreateInstance(fixtureType);
                    foreach (MethodInfo setup in fixtureType.GetMethods())
                        foreach (object attribute in setup.GetCustomAttributes(false))
                            if (attribute.GetType().FullName == "NUnit.Framework.SetUpAttribute") setup.Invoke(fixture, null);
                    method.Invoke(fixture, arguments);
                    passed++;
                    Console.WriteLine("通过 " + fixtureName + "." + method.Name + "(" + string.Join(",", arguments) + ")");
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.WriteLine("失败 " + fixtureName + "." + method.Name + "\n" + (exception.InnerException ?? exception));
                }
            }
        }
        }
        Console.WriteLine("补充合同烟测：通过=" + passed + "，失败=" + failed + "；不替代 Unity Test Runner XML。");
        return failed > 0 || passed == 0 ? 1 : 0;
    }
}
