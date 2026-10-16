using System;
using System.Reflection;
using System.IO;
using System.Collections;
using System.Collections.Generic;
public static class EnterpriseContentContractSmoke {
    private static string Root = @"G:\NoMadCity";
    private static Assembly Resolve(object sender, ResolveEventArgs args) {
        string name = new AssemblyName(args.Name).Name;
        foreach (string dir in new[] { @"Library\Bee\artifacts\1900b0aE.dag", @"Library\ScriptAssemblies" }) {
            string file = Path.Combine(Root, dir, name+".dll"); if(File.Exists(file)) return Assembly.LoadFrom(file);
        }
        foreach(string file in File.ReadAllLines(Path.Combine(Root,@"Logs\ExpansionValidation\EnterpriseFacilityStyleEvent\smoke\assembly-paths.txt")))
            if(Path.GetFileNameWithoutExtension(file)==name) return Assembly.LoadFrom(file);
        return null;
    }
    public static int Main() {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try {
            var assembly = Assembly.LoadFrom(Path.Combine(Root,@"Library\Bee\artifacts\1900b0aE.dag\YC.Tests.EditMode.dll"));
            var type = assembly.GetType("YC.Tests.EditMode.ExpansionRules.EnterpriseFacilityStyleEventTests",true);
            var fixture = Activator.CreateInstance(type);
            type.GetMethod("LoadRealDefinitionsThroughProductionLoader").Invoke(fixture,null);
            int passed=0, failed=0;
            foreach(var method in type.GetMethods()) {
                var cases = new List<object[]>();
                foreach(object attribute in method.GetCustomAttributes(false)) {
                    var at=attribute.GetType();
                    if(at.FullName=="NUnit.Framework.TestAttribute") cases.Add(new object[0]);
                    if(at.FullName=="NUnit.Framework.TestCaseAttribute") cases.Add((object[])at.GetProperty("Arguments").GetValue(attribute,null));
                    if(at.FullName=="NUnit.Framework.TestCaseSourceAttribute") {
                        string member=(string)at.GetProperty("SourceName").GetValue(attribute,null);
                        var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
                        var field=type.GetField(member,flags);
                        var source=(IEnumerable)(field!=null?field.GetValue(null):type.GetMethod(member,flags).Invoke(null,null));
                        foreach(object c in source) {
                            var prop=c.GetType().GetProperty("Arguments");
                            cases.Add(prop!=null?(object[])prop.GetValue(c,null):new object[]{c});
                        }
                    }
                }
                foreach(var args in cases) {
                    string title=method.Name+"("+string.Join(",",args)+")";
                    try { method.Invoke(fixture,args);passed++;Console.WriteLine("PASS "+title); }
                    catch(Exception ex) {failed++;Console.WriteLine("FAIL "+title+"\n"+(ex.InnerException??ex));}
                }
            }
            Console.WriteLine("补充烟测 passed="+passed+" failed="+failed+"；不替代 Unity Test Runner XML。");
            return failed>0||passed==0?1:0;
        } catch(Exception ex) {Console.WriteLine(ex.InnerException??ex);return 1;}
    }
}