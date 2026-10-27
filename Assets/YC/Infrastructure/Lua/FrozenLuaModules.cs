using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;

namespace YC.Infrastructure.Lua
{
    /// <summary>内容装载时冻结共享函数；运行时只见同一沙箱内的Shared别名，不安装文件访问或require。</summary>
    public static class FrozenLuaModuleProtocol
    {
        public const string Version = "lua_frozen_modules.v1";
        public const int MaximumModulesPerScript = 128;
    }
    public sealed partial class ExternalContentPack
    {
        private sealed class FrozenModule
        {
            internal string Id, Version, Path, Hash, Source;
        }
        private void LoadFrozenModules(JObject manifest)
        {
            var catalogToken = manifest["luaModules"]; var bindingsToken = manifest["scriptDependencies"];
            if (catalogToken == null && bindingsToken == null) return;
            if (SchemaVersion != 3 || catalogToken is not JArray catalog || bindingsToken is not JArray bindings)
                throw new InvalidDataException("frozen_lua_module_tables_required");
            var modules = new Dictionary<string, FrozenModule>(StringComparer.Ordinal);
            foreach (var token in catalog)
            {
                var record = ModuleShape(token, "moduleId", "version", "script", "sha256");
                string id = ModuleText(record, "moduleId"), version = ModuleText(record, "version"), path = ModuleText(record, "script"), hash = ModuleText(record, "sha256");
                if (!Regex.IsMatch(id, @"\A[a-zA-Z][a-zA-Z0-9_.-]{0,95}\z") || !Regex.IsMatch(version, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z") ||
                    !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || modules.ContainsKey(id + "\n" + version))
                    throw new InvalidDataException("frozen_lua_module_identity_invalid");
                ValidateModulePath(path); LoadScript(path); string source = scripts[path];
                if (LuaContentHasher.ComputeSha256(source) != hash) throw new InvalidDataException("frozen_lua_module_hash_mismatch:" + id);
                modules.Add(id + "\n" + version, new FrozenModule { Id = id, Version = version, Path = path, Hash = hash, Source = source });
            }
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in bindings)
            {
                var binding = ModuleShape(token, "protocolVersion", "script", "modules");
                string target = ModuleText(binding, "script");
                ValidateModulePath(target);
                if (binding["protocolVersion"]?.Type != JTokenType.Integer || (int)binding["protocolVersion"] != 1 ||
                    binding["modules"] is not JArray dependencies || dependencies.Count == 0 || dependencies.Count > FrozenLuaModuleProtocol.MaximumModulesPerScript ||
                    !targets.Add(target) || !scripts.ContainsKey(target)) throw new InvalidDataException("frozen_lua_script_binding_invalid");
            }
            foreach (var token in bindings)
            {
                var binding = (JObject)token; string target = ModuleText(binding, "script");
                var aliases = new HashSet<string>(StringComparer.Ordinal); var used = new HashSet<string>(StringComparer.Ordinal);
                var source = new StringBuilder("-- " + FrozenLuaModuleProtocol.Version + "\nlocal Shared = {}\n");
                foreach (var dependency in (JArray)binding["modules"])
                {
                    var reference = ModuleShape(dependency, "moduleId", "version", "sha256", "alias");
                    string key = ModuleText(reference, "moduleId") + "\n" + ModuleText(reference, "version"), alias = ModuleText(reference, "alias");
                    if (!Regex.IsMatch(alias, @"\A[a-zA-Z][a-zA-Z0-9_]{0,63}\z") || !aliases.Add(alias) || !used.Add(key) ||
                        !modules.TryGetValue(key, out var module) || ModuleText(reference, "sha256") != module.Hash || targets.Contains(module.Path))
                        throw new InvalidDataException("frozen_lua_module_reference_invalid");
                    // v1只装配独立函数模块；拒绝把另一个装配入口作为模块，避免隐式递归和循环依赖。
                    source.Append("-- ").Append(module.Id).Append('@').Append(module.Version).Append(' ').Append(module.Hash).Append('\n');
                    source.Append("Shared.").Append(alias).Append(" = (function()\n").Append(module.Source.TrimStart('\ufeff')).Append("\nend)()\n");
                }
                source.Append(scripts[target].TrimStart('\ufeff'));
                string assembled = source.ToString();
                new Script(CoreModules.None).LoadString(assembled, null, target); // 只编译，执行仍受正式宿主的同一沙箱/预算约束。
                scripts[target] = assembled;
            }
        }
        private static JObject ModuleShape(JToken token, params string[] fields)
        {
            if (token is not JObject record || record.Properties().Count() != fields.Length || fields.Any(f => record[f] == null))
                throw new InvalidDataException("frozen_lua_module_shape_invalid");
            return record;
        }
        private static string ModuleText(JObject record, string field)
        {
            if (record[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)record[field]))
                throw new InvalidDataException("frozen_lua_module_text_invalid:" + field);
            return (string)record[field];
        }
        private void ValidateModulePath(string relative)
        {
            if (!relative.StartsWith("lua/", StringComparison.Ordinal) || !relative.EndsWith(".lua", StringComparison.Ordinal) ||
                relative.Contains('\\') || relative.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
                throw new InvalidDataException("frozen_lua_module_path_invalid");
            string path = ResolvePath(relative);
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("frozen_lua_module_reparse_point");
                if (string.Equals(current, RootPath, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }
}
