using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离规则／真实Host恢复，不计正常开局、页面或多局验收。
    public sealed class MinimumResourceGainTests
    {
        private PaymentTestFixture f;
        [SetUp] public void Begin() => f = new PaymentTestFixture();
        [TearDown] public void End() => f.Dispose();
        private static NormalizedValue Args() => Object(
            "executingPlayer", NormalizedValue.CreateStableReference("player", "p1"),
            "resourceType", NormalizedValue.CreateString("Iron"), "amount", NormalizedValue.CreateInteger(4),
            "apiVersion", NormalizedValue.CreateInteger(2), "qualification", Object(
                "kind", NormalizedValue.CreateString("minimum_balance"),
                "resourceTypes", Strings(new[] { "originium", "originium_shard", "iron" })));
        private static LuaInvocationResult Invoke(string fields)
        {
            string source = "return function(ctx) return {Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=4," + fields + "})} end";
            var script = new LuaScriptDefinition("content.minimum_gain", "ability.minimum_gain", "handler.minimum_gain", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            return new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("minimum_gain.event", "test", "p1", 0, "1.0.0", 2, 0, 0));
        }

        [TestCase(0, 0, 0, true)]
        [TestCase(2, 2, 2, true)]
        [TestCase(3, 3, 1, true)]
        [TestCase(3, 1, 2, false)]
        public void ActualGainRechecksCurrentWalletAndKeepsTiedMinimum(int ore, int shard, int iron, bool eligible)
        {
            var wallet = f.State.FindPlayer(1).Resources;
            wallet.Originium = ore; wallet.OriginiumShard = shard; wallet.Iron = iron;
            var node = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Gain, Args(), 1));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + (eligible ? 4 : 0)));
            Assert.That(f.State.FindPlayer(1).Resources.Originium, Is.EqualTo(ore));
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shard));
            Assert.That(node.Status, Is.EqualTo(eligible ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
            Assert.That(node.DefinitionVersion, Is.EqualTo(ResourceEffectExecutor.GainDefinitionVersion));
            f.Restore("并列最少实际收益及失败后恢复");
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(iron + (eligible ? 4 : 0)));
        }

        [Test]
        public void LuaAndCompilerPreserveQualificationWithoutDroppingIt()
        {
            var invocation = Invoke("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'originium','originium_shard','iron'}}");
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(Number(specs[0].NormalizedArguments, "apiVersion"), Is.EqualTo(2));
            Assert.That(Text(Value(specs[0].NormalizedArguments, "qualification"), "kind"), Is.EqualTo("minimum_balance"));
            f.State.FindPlayer(1).Resources.Iron = 1;
            f.Run(specs[0]); Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(5));
        }

        [TestCase("apiVersion=1,qualification={kind='minimum_balance',resourceTypes={'iron','originium'}}")]
        [TestCase("apiVersion=2")]
        [TestCase("qualification={kind='minimum_balance',resourceTypes={'iron','originium'}}")]
        [TestCase("apiVersion=2,qualification={kind='maximum_balance',resourceTypes={'iron','originium'}}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'iron'}}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'iron','Iron'}}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'iron','unknown'}}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'originium','originium_shard'}}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes={'iron','originium'},extra=true}")]
        [TestCase("apiVersion=2,qualification={kind='minimum_balance',resourceTypes='iron'}")]
        [TestCase("apiVersion=2.5,qualification={kind='minimum_balance',resourceTypes={'iron','originium'}}")]
        public void LuaRejectsIncompleteUnknownOrAmbiguousQualification(string fields)
        {
            var invocation = Invoke(fields);
            Assert.That(invocation.IsSuccess, Is.False, invocation.Diagnostic);
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(100));
        }

        [Test]
        public void OrdinaryGainFactoryAndLuaKeepOriginalSemantics()
        {
            Assert.That(Invoke("").IsSuccess, Is.True);
            var gain = ResourceEffectSpecFactory.Gain(1, ResourceType.Iron, 4);
            Assert.That(gain.DefinitionVersion, Is.EqualTo(ResourceEffectExecutor.GainDefinitionVersion));
            f.State.FindPlayer(1).Resources.Iron = 200;
            f.Run(gain); Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(204));
        }

        [Test]
        public void OldUnqualifiedGainRestoresBeforeExecutionAndDoesNotRepeat()
        {
            var gain = ResourceEffectSpecFactory.Gain(1, ResourceType.Iron, 4); gain.DefinitionVersion = "1.0.0";
            string id = f.Engine.CreateRoot(gain);
            f.Restore("旧普通获得未执行恢复");
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(104));
            Assert.That(f.Engine.GetNode(id).DefinitionVersion, Is.EqualTo(ResourceEffectExecutor.GainDefinitionVersion));
            f.Restore("旧普通获得已执行恢复"); Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(104));
        }

        [TestCase("1.0.0")][TestCase("3.0.0")]
        public void QualifiedGainWithWrongExecutionIdentityIsRejectedBeforeExecution(string version)
        {
            string id = f.Engine.CreateRoot(EffectSpec.Create(ResourceEffectTypeIds.Gain, Args(), 1));
            var archive = HostRecoveryService.CreateArchive(f.State);
            archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).DefinitionVersion = version;
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), f.Registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain("resource_gain_definition_version_mismatch"));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(100));
        }
    }
}
