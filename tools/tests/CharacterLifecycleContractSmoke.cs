using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

/// <summary>开发期反射烟测；正式交付仍须Unity Test Runner的定向XML。</summary>
public static class CharacterLifecycleContractSmoke
{
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        string root = Directory.GetCurrentDirectory();
        foreach (string directory in new[] { @"Temp\bin\Debug", @"Library\ScriptAssemblies" })
        {
            string file = Path.Combine(root, directory, name + ".dll");
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        if (name == "nunit.framework") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.ext.nunit@1.0.6\net35\unity-custom\nunit.framework.dll"));
        if (name == "Newtonsoft.Json") return Assembly.LoadFrom(Path.Combine(root, @"Library\PackageCache\com.unity.nuget.newtonsoft-json@3.2.1\Runtime\Newtonsoft.Json.dll"));
        return null;
    }
    private static bool HasAttribute(MethodInfo method, string name)
    {
        foreach (object attribute in method.GetCustomAttributes(false)) if (attribute.GetType().FullName == "NUnit.Framework." + name) return true;
        return false;
    }
    public static int Main(string[] fixtureNames)
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        var assembly = Assembly.LoadFrom(Path.Combine(Directory.GetCurrentDirectory(), @"Temp\bin\Debug\YC.Tests.EditMode.dll"));
        int passed = 0, failed = 0;
        if (fixtureNames.Length == 0) fixtureNames = new[] { "YC.Tests.EditMode.ExpansionRules.CharacterLifecycleTests" };
        else
        {
            Type bootstrap = assembly.GetType("YC.Tests.EditMode.ExpansionRules.CharacterLifecycleTests", true);
            bootstrap.GetMethod("LoadCoreDefinitions").Invoke(Activator.CreateInstance(bootstrap), null);
            var infrastructure = Assembly.LoadFrom(Path.Combine(Directory.GetCurrentDirectory(), @"Temp\bin\Debug\YC.Infrastructure.Lua.dll"));
            var domain = Assembly.LoadFrom(Path.Combine(Directory.GetCurrentDirectory(), @"Temp\bin\Debug\YC.Domain.dll"));
            var packType = infrastructure.GetType("YC.Infrastructure.Lua.ExternalContentPack", true);
            var pack = packType.GetMethod("Load").Invoke(null, new object[] { Path.Combine(Directory.GetCurrentDirectory(), @"Assets\StreamingAssets\Content\core") });
            domain.GetType("YC.Domain.Facilities.FacilityCardDatabase", true).GetMethod("InitializeExternal").Invoke(null, new[] { packType.GetMethod("CreateFacilities").Invoke(pack, null) });
            domain.GetType("YC.Domain.Cards.EventCardDatabase", true).GetMethod("InitializeExternal").Invoke(null, new[] { packType.GetMethod("CreateEvents").Invoke(pack, null) });
            domain.GetType("YC.Domain.CityStyles.CityStyleDatabase", true).GetMethod("InitializeExternal").Invoke(null, new[] { packType.GetMethod("CreateCityStyles").Invoke(pack, null) });
            domain.GetType("YC.Domain.SpecialActions.SpecialActionDatabase", true).GetMethod("InitializeExternal").Invoke(null, new[] { packType.GetMethod("CreateSpecialActions").Invoke(pack, null) });
        }
        foreach (string fixtureName in fixtureNames)
        {
        Type type = assembly.GetType(fixtureName, true);
        object fixture = Activator.CreateInstance(type);
        foreach (MethodInfo method in type.GetMethods()) if (HasAttribute(method, "OneTimeSetUpAttribute")) method.Invoke(fixture, null);
        foreach (MethodInfo method in type.GetMethods())
        {
            var cases = new List<object[]>();
            foreach (object attribute in method.GetCustomAttributes(false))
            {
                if (attribute.GetType().FullName == "NUnit.Framework.TestAttribute") cases.Add(new object[0]);
                if (attribute.GetType().FullName == "NUnit.Framework.TestCaseAttribute") cases.Add((object[])attribute.GetType().GetProperty("Arguments").GetValue(attribute, null));
            }
            foreach (object[] arguments in cases)
            {
                string label = type.Name + "." + method.Name + "(" + string.Join(",", Array.ConvertAll(arguments, argument => Convert.ToString(argument))) + ")";
                try {
                    foreach (MethodInfo setup in type.GetMethods()) if (HasAttribute(setup, "SetUpAttribute")) setup.Invoke(fixture, null);
                    method.Invoke(fixture, arguments); passed++; Console.WriteLine("通过 " + label);
                }
                catch (Exception exception) { failed++; Console.WriteLine("失败 " + label + "\n" + (exception.InnerException ?? exception)); }
                finally { foreach (MethodInfo teardown in type.GetMethods()) if (HasAttribute(teardown, "TearDownAttribute")) teardown.Invoke(fixture, null); }
            }
        }
        foreach (MethodInfo method in type.GetMethods()) if (HasAttribute(method, "OneTimeTearDownAttribute")) method.Invoke(fixture, null);
        }
        Console.WriteLine("补充合同烟测：通过=" + passed + "，失败=" + failed + "；不替代Unity Test Runner XML。");
        return passed == 0 || failed > 0 ? 1 : 0;
    }
}
