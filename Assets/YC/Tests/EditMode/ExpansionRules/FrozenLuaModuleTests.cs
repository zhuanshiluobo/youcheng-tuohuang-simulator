using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只测冻结模块与普通Effect执行；不是企业家正文或正常页面验收。
    public sealed class FrozenLuaModuleTests
    {
        private const string Main = "lua/qy007_module_main.lua", Module = "lua/facilities/resource_reward.lua";
        private const string Token = "infected.component.gavial_token", Handler = "qy007.module.handler";
        private IsolatedTestArtifacts artifacts;
        [SetUp] public void Start() => artifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void Finish() => artifacts?.Finish();
        private string Prepare(Action<JObject, string> change = null, string mainOverride = null)
        {
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string module = File.ReadAllText(Path.Combine(installed, Module));
            string hash = LuaContentHasher.ComputeSha256(module);
            string main = mainOverride ?? "return function(ctx) return Shared.entry(ctx) end";
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json")));
            manifest["luaModules"] = new JArray(new JObject { ["moduleId"] = "facility.resource", ["version"] = "1.0.0", ["script"] = Module, ["sha256"] = hash });
            manifest["scriptDependencies"] = new JArray(new JObject { ["protocolVersion"] = 1, ["script"] = Main,
                ["modules"] = new JArray(new JObject { ["moduleId"] = "facility.resource", ["version"] = "1.0.0", ["sha256"] = hash, ["alias"] = "entry" }) });
            change?.Invoke(manifest, main);
            var token = JObject.Parse(File.ReadAllText(Path.Combine(installed, "component/" + Token + ".json")));
            token["abilitySources"] = new JArray();
            token["abilityHandlers"] = new JArray(new JObject { ["handlerId"] = Handler, ["version"] = "1.0.0", ["script"] = Main, ["completionHandlerId"] = "" });
            return artifacts.CopyContent(installed, "模块隔离私有输入", new Dictionary<string, string> {
                ["pack.json"] = manifest.ToString(), ["component/" + Token + ".json"] = token.ToString(), [Main] = main });
        }
        [Test] public void RealFacilityModuleProducesTheSameOrdinaryResourceEffectAndFreezesItsHash()
        {
            string input = Prepare(); var pack = ExternalContentPack.Load(input);
            artifacts.RecordContent("模块实际装载", input, pack.ContentHash);
            string source = pack.GetScript(Main), hash = LuaContentHasher.ComputeSha256(source);
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.0.0");
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("rewardResourceType", LuaSnapshotValue.String("iron")),
                new LuaSnapshotEntry("rewardAmount", LuaSnapshotValue.Integer(2)) });
            var context = new LuaInvocationContext("module.test", "FacilityEntry", "p1", 0, "1.0.0", 2, 0, 0, snapshot);
            var host = new MoonSharpLuaRuntimeHost();
            var result = host.Invoke(new LuaScriptDefinition(Token, Handler, Handler, "1.0.0", source, hash), context);
            string original = pack.GetScript(Module);
            var direct = host.Invoke(new LuaScriptDefinition(Token, Handler, Handler, "1.0.0", original, LuaContentHasher.ComputeSha256(original)), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(direct.IsSuccess, Is.True, direct.Diagnostic);
            Assert.That(result.Effects.Count, Is.EqualTo(1));
            Assert.That(result.Effects[0].EffectTypeId, Is.EqualTo(direct.Effects[0].EffectTypeId));
            Assert.That(result.Effects[0].ResourceTypeId, Is.EqualTo(direct.Effects[0].ResourceTypeId));
            Assert.That(result.Effects[0].RecipientPlayerId, Is.EqualTo("p1")); Assert.That(result.Effects[0].Amount, Is.EqualTo(2));
            var compiler = new LuaEffectSpecCompiler();
            Assert.That(compiler.TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1, SourceId = Token }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(specs.Single().ContinuationContentHash, Is.EqualTo(hash));
            var state = new GameState { GameId = "qy007.module.isolated", ContentPackHash = pack.ContentHash,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var registry = new EffectRegistry { ContentPackHash = pack.ContentHash }; ResourceEffectExecutor.Register(registry);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(specs.Single());
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2)); Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
            artifacts.WriteLabeledText("真实共享正文生成普通Effect", JsonConvert.SerializeObject(state));
        }
        [Test] public void DependencyVersionChangeChangesTheFrozenHandlerHashEvenWithIdenticalModuleText()
        {
            string first = Prepare(); var a = ExternalContentPack.Load(first);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(first, "pack.json")));
            manifest["luaModules"][0]["version"] = "1.0.1";
            manifest["scriptDependencies"][0]["modules"][0]["version"] = "1.0.1";
            string second = artifacts.CopyContent(first, "模块显式版本变化", new Dictionary<string, string> { ["pack.json"] = manifest.ToString() });
            var b = ExternalContentPack.Load(second);
            artifacts.RecordContent("模块版本一", first, a.ContentHash); artifacts.RecordContent("模块版本二", second, b.ContentHash);
            Assert.That(LuaContentHasher.ComputeSha256(a.GetScript(Main)), Is.Not.EqualTo(LuaContentHasher.ComputeSha256(b.GetScript(Main))));
            Assert.That(a.ContentHash, Is.Not.EqualTo(b.ContentHash));
        }
        [TestCase("hash")] [TestCase("version")] [TestCase("alias")] [TestCase("escape")] [TestCase("cycle")] [TestCase("field")]
        public void InvalidFrozenReferenceIsRejectedBeforeAnyBodyRuns(string kind)
        {
            string input = Prepare((manifest, main) => {
                var module = manifest["luaModules"][0]; var reference = manifest["scriptDependencies"][0]["modules"][0];
                switch (kind)
                {
                    case "hash": module["sha256"] = new string('0', 64); break;
                    case "version": reference["version"] = "missing"; break;
                    case "alias": reference["alias"] = "entry.with.path"; break;
                    case "escape": module["script"] = "lua/../../outside.lua"; break;
                    case "cycle": module["script"] = Main; module["sha256"] = LuaContentHasher.ComputeSha256(main); reference["sha256"] = module["sha256"].DeepClone(); break;
                    case "field": ((JObject)module).Add("Unexpected", true); break;
                }
            });
            var error = Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(input));
            artifacts.WriteLabeledText("冻结模块拒绝原因", JsonConvert.SerializeObject(new { kind, reason = error.Message }));
        }
        [TestCase("require")] [TestCase("loadfile")] [TestCase("dofile")]
        public void FrozenModuleDoesNotEnableDynamicFileImports(string api)
        {
            string input = Prepare(mainOverride: "return function(ctx) return " + api + "('unregistered') end");
            var pack = ExternalContentPack.Load(input); artifacts.RecordContent("动态导入拒绝输入", input, pack.ContentHash);
            string source = pack.GetScript(Main);
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Token, Handler, Handler, "1.0.0", source,
                LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("module.test", "FacilityEntry", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.ForbiddenApi));
            artifacts.WriteLabeledText("运行时文件导入拒绝", JsonConvert.SerializeObject(new { api, result.FailureCode, result.Diagnostic }));
        }
    }
}
