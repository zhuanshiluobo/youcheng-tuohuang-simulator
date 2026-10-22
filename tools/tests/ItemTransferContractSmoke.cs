using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

public static class ItemTransferContractSmoke
{
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        string root = @"G:\NoMadCity";
        string file = Path.Combine(root, "Temp", "bin", "Debug", name + ".dll");
        if (File.Exists(file)) return Assembly.LoadFrom(file);
        file = Path.Combine(root, "Library", "ScriptAssemblies", name + ".dll");
        if (File.Exists(file)) return Assembly.LoadFrom(file);
        if (name == "nunit.framework") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.ext.nunit@1.0.6\net35\unity-custom\nunit.framework.dll"));
        if (name == "Newtonsoft.Json") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.nuget.newtonsoft-json@3.2.1\Runtime\Newtonsoft.Json.dll"));
        return null;
    }
    public static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Assembly assembly = Assembly.LoadFrom(@"G:\NoMadCity\Temp\bin\Debug\YC.Tests.EditMode.dll");
        Type fixtureType = assembly.GetType("YC.Tests.EditMode.ExpansionRules.ItemTransferEffectTests", true);
        object fixture = Activator.CreateInstance(fixtureType);
        fixtureType.GetMethod("LoadProductionFacilityDefinitions").Invoke(fixture, null);
        int passed = 0, failed = 0;
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
                try { method.Invoke(fixture, arguments); passed++; Console.WriteLine("通过 " + method.Name); }
                catch (Exception exception) { failed++; Console.WriteLine("失败 " + method.Name + "\n" + (exception.InnerException ?? exception)); }
            }
        }
        Console.WriteLine("补充合同烟测：通过=" + passed + "，失败=" + failed + "；不替代 Unity Test Runner XML。");
        return failed > 0 || passed == 0 ? 1 : 0;
    }
}
