using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 本轮只读模块原文/小型隔离快照，不复制完整内容包；不是正式行动或界面验收。
    public sealed class AgnesQuickConditionTests
    {
        [TestCase("A09", "1A·集中驻扎")] [TestCase("A08", "1B·分遣部署")]
        [TestCase("A06", "3A·后勤保障")] [TestCase("A07", "3B·合作战略")] [TestCase("A05", "5B·特勤小组")]
        public void QuickEntryHasOneAtomicTwoCommandCostAndKeepsBodyOutsideTheGate(string face, string tactic)
        {
            using var evidence = new Evidence();
            string payload = JsonConvert.SerializeObject(new { abilityId = "entrepreneur." + face + ".tactic", actionId = "entrepreneur." + face + ".tactic.quick" });
            // 调当前真实薄成本入口，核五组冻结名称；正文由原BodyHandlerRef独立执行。
            var quick = Invoke(payload);
            evidence.Record(payload, quick);
            Assert.That(quick.IsSuccess, Is.True, quick.Diagnostic);
            var condition = quick.Effects.Single();
            Assert.That(condition.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(Value(condition.RawArguments, "ruleCondition").BooleanValue, Is.True);
            Assert.That(Text(condition.RawArguments, "decisionPlayer"), Is.EqualTo("p1"));
            Assert.That(condition.NestedEffectGroups.Length, Is.EqualTo(2));
            var payment = condition.NestedEffectGroups[0].Single();
            Assert.That(payment.EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(payment.RawArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(Number(payment.RawArguments, "requiredCount"), Is.EqualTo(2));
            Assert.That(Value(payment.RawArguments, "itemIds").Items.Count, Is.EqualTo(2));
            Assert.That(Value(payment.RawArguments, "itemIds").Items.Select(i => i.StringValue).Distinct().Count(), Is.EqualTo(2));
            Assert.That(Text(Value(payment.RawArguments, "destination"), "kind"), Is.EqualTo("original_supply"));
            Assert.That(condition.NestedEffectGroups[1], Is.Empty, "费用权限不得携带完整正文");
            foreach (var effect in new[] { condition, payment })
            {
                Assert.That(effect.ContinuationContext, Is.Null);
                Assert.That(effect.CompletionHandlerId, Is.Empty);
                Assert.That(effect.RequiredOutcome, Is.EqualTo(default(RequiredEffectOutcome)));
            }
            Assert.That(condition.Presentation.Interaction.Prompt.Key, Is.EqualTo("core:entrepreneur.A.quick.condition"));
            var title = condition.Presentation.Interaction.Prompt.Arguments.Single(a => a.Name == "tactic");
            Assert.That(title.Kind, Is.EqualTo("text"));
            Assert.That(title.Value.StringValue, Is.EqualTo(tactic));
            Assert.That(JsonConvert.SerializeObject(condition.Presentation.Started), Is.EqualTo(JsonConvert.SerializeObject(condition.Presentation.Interaction.Prompt)));
        }

        [TestCase(0)] [TestCase(1)]
        public void QuickEntryShortageKeepsRequiredTwoAndNeverInventsPartialPayment(int stock)
        {
            var result = Invoke("{\"abilityId\":\"entrepreneur.A09.tactic\",\"actionId\":\"entrepreneur.A09.tactic.quick\"}", stock: stock);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var left = result.Effects.Single().NestedEffectGroups[0].Single();
            Assert.That(Number(left.RawArguments, "requiredCount"), Is.EqualTo(2));
            Assert.That(Value(left.RawArguments, "itemIds").Items.Count, Is.EqualTo(stock));
            Assert.That(result.Effects.Single().NestedEffectGroups[1], Is.Empty);
            // 此例只核未执行费用树；Host不足禁用沿现有FormalQuickShortage两例验证。
        }

        [Test] public void A05BodyKeepsAfterMapGainWhileEntryCostRejectsCompletionCallbacks()
        {
            var body = Invoke("{\"abilityId\":\"entrepreneur.A05.tactic\",\"actionId\":\"entrepreneur.A05.tactic.quick\"}", "return A.invoke(ctx,{})");
            Assert.That(body.IsSuccess, Is.True, body.Diagnostic);
            Assert.That(body.Effects.Single().EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(Text(body.Effects.Single().ContinuationContext, "stage"), Is.EqualTo("after_map_gain"));
            const string payload = "{\"outcome\":\"completed\",\"continuationContext\":{\"stage\":\"after_map_gain\"}}";
            var normal = Invoke(payload, "return A.invoke(ctx,{})", "EffectCompleted");
            Assert.That(normal.IsSuccess, Is.True, normal.Diagnostic);
            Assert.That(Value(normal.Effects.Single().RawArguments, "options").Items.Select(i => i.StringValue), Is.EquivalentTo(new[] { "1" }));
            var costCompletion = Invoke(payload, eventType: "EffectCompleted");
            Assert.That(costCompletion.IsSuccess, Is.False);
            Assert.That(costCompletion.Diagnostic, Does.Contain("agnes_quick_condition_entry_invalid"));
        }

        [Test] public void SpecialRouteCannotAccidentallyEnterQuickEntryCondition()
        {
            var result = Invoke("{\"abilityId\":\"entrepreneur.A09.tactic\",\"actionId\":\"entrepreneur.A09.tactic.special\"}");
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("agnes_quick_condition_entry_invalid"));
        }

        private static LuaInvocationResult Invoke(string payload, string call = null, string eventType = "AbilityInvoked", int stock = 3)
        {
            string module = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/entrepreneurs/agnes.lua"));
            string code = call == null
                ? "local Shared={agnes=(function()\n" + module + "\nend)()}\n" + File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/entrepreneurs/agnes.cost.lua"))
                : "local A=(function()\n" + module + "\nend)()\nreturn function(ctx) " + call + " end";
            var snapshot = AgnesContentTests.Inventory(3, -1, 0); snapshot.Round = 1;
            snapshot.EventPayload = AgnesContentTestFixture.Snapshot(JToken.Parse(payload));
            var context = new LuaInvocationContext("qy013.quick-condition.isolated", eventType, "p1", 0, "1.0.0", 2, 0, 1000, 1000, 0, snapshot) {
                SourceFacts = AgnesContentTests.CallableFacts(0, stock) };
            return new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy013.quick-condition.isolated", "qy013.quick-condition",
                "qy013.quick-condition", "1.0.0", code, LuaContentHasher.ComputeSha256(code)), context);
        }

        private sealed class Evidence : System.IDisposable
        {
            private readonly IsolatedTestArtifacts artifacts = IsolatedTestArtifacts.BeginCase();
            public void Record(string payload, LuaInvocationResult quick) => artifacts.WriteLabeledText("R8真实薄成本入口构树",
                JsonConvert.SerializeObject(new { payload, quickStatus = quick.Status, quickDiagnostic = quick.Diagnostic, quickEffects = quick.Effects,
                    scope = "直接生产LuaHost构树，不计正式行动准入/轨道/恢复/界面；无内容包复制" }));
            public void Dispose() => artifacts.Finish();
        }
    }
}
