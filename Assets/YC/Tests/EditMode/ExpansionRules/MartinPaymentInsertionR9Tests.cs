using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式原文件与Host的隔离验证；不改模式声明、不包装Lua，不计正常界面或整局验收。
    public sealed class MartinPaymentInsertionR9Tests : MartinContentTestFixture
    {
        private const string M01 = "entrepreneur.award.M01";
        private const string Script = "lua/payment_substitution/martin_concession.lua";
        private const string Facility = "entrepreneur.facility.M02";

        [SetUp] public override void StartMartin()
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            Pack = ExternalContentPack.Load(Installed).CreateActiveSnapshot(new GameRuleConfiguration {
                RuleModeId = "entrepreneur", PlayerCount = 3,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
            var definition = Pack.ActiveDefinitions.Single(d => d.DefinitionId == M01);
            var json = JObject.Parse(File.ReadAllText(Path.Combine(Installed, "award/entrepreneur.award.M01.json")));
            Assert.That(definition.Version, Is.EqualTo((string)json["version"]));
            Assert.That(Pack.GetScript(Script), Is.EqualTo(File.ReadAllText(Path.Combine(Installed, Script))), "必须直接执行正式Lua，不加私有insertion包装");
            using (var sha = SHA256.Create())
                Artifacts.WriteLabeledText("正式M01装载身份", JsonConvert.SerializeObject(new {
                    root = Installed, Pack.ContentHash, definition.Version,
                    sources = definition.AbilitySources, handlers = definition.AbilityHandlers,
                    scriptSha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(Installed, Script)))).Replace("-", "").ToLowerInvariant(),
                    testAssemblyMvid = GetType().Assembly.ManifestModule.ModuleVersionId,
                    domainAssemblyMvid = typeof(ResourcePaymentInsertionService).Assembly.ManifestModule.ModuleVersionId,
                    luaAssemblyMvid = typeof(ExternalContentPack).Assembly.ManifestModule.ModuleVersionId
                }, Formatting.Indented));
        }

        private GameState Fixture(bool grant = true, bool passenger = false)
        {
            var extra = passenger ? new[] { new ItemRecipeEntry { EntryId = "qy016.r9.passenger", DefinitionId = "operator.passenger",
                TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } } : null;
            var state = State("entrepreneur.round.M.5", extraRecipe: extra);
            state.RuleConfiguration.RuleModeId = "entrepreneur";
            state.Round = 5; // 隔离前置对应M10实际生效回合，不把指定奖励当初始发放。
            ResourcePaymentChoiceEffectExecutor.Register(Registry);
            ResourcePaymentSubstitutionEffectExecutor.Register(Registry);
            FacilityBuildEffectExecutor.Register(Registry);
            // 仅隔离准备：公开实例工厂装配一张玩家1能力卡，供正式Lua判断马丁本人；非正常设置验收。
            Registry.Register("qy016.r9.prepare_identity", c => EffectStepResult.Completed());
            var engine = new EffectTreeExecutor(state, Registry);
            string setup = engine.CreateRoot(EffectSpec.Create("qy016.r9.prepare_identity", playerId: 1));
            Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(state, setup, "M13身份准备", "entrepreneur.ability.M13",
                "entrepreneur.ability_card", FindZone(state, "entrepreneur_active", 1).ZoneId, ItemSubjectRef.Player(1), out string carrier, out string why), Is.True, why);
            foreach (var zone in Pack.ActiveDefinitions.Single(d => d.DefinitionId == "entrepreneur.ability.M13").CardZones)
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, carrier, zone, out _, out why), Is.True, why);
            Pump(state);
            if (grant)
            {
                SelectFace(state, "entrepreneur.round.M.5", "M10");
                Immediate(state, "M10");
                Assert.That(state.ItemZones.Items.Where(i => i.DefinitionId == M01).Select(i => i.ControllerSubject.PlayerId), Is.EquivalentTo(new[] { 2, 3 }));
            }
            if (passenger)
            {
                state.CurrentPlayerId = 2;
                var card = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.passenger" && i.ControllerSubject.PlayerId == 2);
                Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 2));
                Answer(state, NormalizedValue.CreateString(card.ItemId));
                Run(state, CharacterLifecycleService.CreateUseSpec(2, card.ItemId, "tactic"));
                Answer(state, NormalizedValue.CreateString("2"));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "markers").ZoneId), Is.EqualTo(2));
            }
            state.CurrentPlayerId = 2;
            return state;
        }

        private void Pump(GameState state)
        {
            LastState = state;
            Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private EffectNodeRuntimeState Build(GameState state, int player = 2)
        {
            state.CurrentPlayerId = player;
            var card = Card(state, Facility);
            Run(state, FacilityBuildEffectSpecFactory.Build(player, card.ItemId, -1, "resources", false, "qy016.r9.build", false, card.ZoneId, "deck"));
            var owner = state.EffectRuntime.EffectNodes.Last(n => n.PlayerId == player && n.Payment != null);
            Assert.That(owner.Payment.BeforePaymentReady, Is.True);
            Assert.That(owner.Payment.PaymentCost.Originium, Is.EqualTo(2));
            Assert.That(owner.Payment.PaymentCost.OriginiumShard, Is.EqualTo(2));
            Assert.That(owner.Payment.PaymentCost.Iron, Is.EqualTo(2));
            return owner;
        }
        private static SettlementEntryRuntimeState Entry(EffectNodeRuntimeState owner) => owner.SettlementWindow.Entries.Single(e => e.SourceDefinitionId == M01);
        private void Invoke(GameState state, SettlementEntryRuntimeState entry, int player = 2)
        {
            Assert.That(new EffectTreeExecutor(state, Registry).TryInvokeCharacterSettlementOption(player, entry.OptionId, "", state.EffectRuntime.StateRevision, out bool already, out string why), Is.True, why);
            Assert.That(already, Is.False);
            Pump(state);
        }
        private static string Resources(GameState s, int player = 2) => JsonConvert.SerializeObject(s.FindPlayer(player).Resources);
        private void Snapshot(string label, object value) => Artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(value, Formatting.Indented));

        [Test] public void FormalAwardBuildOffersOneNamedEntryAndCommitsOneVoucherForOneResource()
        {
            var state = Fixture();
            state.FindPlayer(2).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2, GoldVoucher = 2 };
            var owner = Build(state); string ownerId = owner.EffectId, paymentId = owner.Payment.PaymentId;
            var entry = Entry(owner);
            Assert.That(owner.SettlementWindow.Entries.Count, Is.EqualTo(1));
            Assert.That(entry.PaymentInsertion.Rule.AllowedResourceTypes, Is.EquivalentTo(new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron }));
            Assert.That(entry.PaymentInsertion.Rule.QuotaScope, Is.EqualTo("build"));
            Assert.That(entry.PaymentInsertion.Rule.MaxUnits, Is.EqualTo(1));
            Assert.That(entry.PaymentInsertion.Rule.QuotaLimit, Is.EqualTo(1));
            Assert.That(entry.Presentation.Label.Key, Is.EqualTo("core:entrepreneur.M.M01.name"));
            Assert.That(entry.Presentation.Description.Key, Is.EqualTo("core:entrepreneur.M.M01.description"));
            Assert.That(entry.PaymentInsertion.Rule.VersionRef.DefinitionVersion, Is.EqualTo("1.2.0"));
            string before = Resources(state);
            Invoke(state, entry);
            var request = Open(state); string unchanged = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("Iron=2"), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(unchanged));
            Answer(state, NormalizedValue.CreateString("Iron=1"));
            Assert.That(Resources(state), Is.EqualTo(before), "分配不预扣额外金券");
            state = Recover(state, "正式M01分配后原付款恢复");
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(Open(state).OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId));
            string exhausted = JsonConvert.SerializeObject(state);
            // 原提交修订不能重放；新的来源调用可返回未付配置，但不能再叠加第二份。
            Assert.That(new EffectTreeExecutor(state, Registry).TryInvokeCharacterSettlementOption(2, Entry(owner).OptionId, "", state.EffectRuntime.StateRevision - 1, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(exhausted));
            Answer(state, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(owner.Payment.SourceReceipts.Count, Is.EqualTo(1));
            Assert.That(owner.Payment.SourceReceipts.Single().Allocation.Iron, Is.EqualTo(1));
            Assert.That(owner.Payment.SourceReceipts.Single().ActualResourceCost.GoldVoucher, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(owner.Payment.FinalCost), Is.EqualTo(JsonConvert.SerializeObject(new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 1, GoldVoucher = 1 })));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == M01 && i.ControllerSubject.PlayerId == 2), Is.EqualTo(1));
            Snapshot("正式M01一次替代一次额外费", owner.Payment);
        }

        [TestCase("self")][TestCase("no_award")][TestCase("non_build")]
        public void FormalRulesDoNotOfferForSelfUngrantOrNonBuild(string scenario)
        {
            var state = Fixture(grant: scenario != "no_award");
            EffectNodeRuntimeState owner;
            if (scenario == "non_build")
            {
                var spec = ResourceEffectSpecFactory.Pay(2, new ResourceSet { Iron = 2 }); spec.DefinitionVersion = ResourceEffectExecutor.PaymentDefinitionVersion;
                owner = Run(state, spec);
            }
            else owner = Build(state, scenario == "self" ? 1 : 2);
            Assert.That(owner.SettlementWindow?.Entries.Any(e => e.SourceDefinitionId == M01) ?? false, Is.False);
            Assert.That(ResourcePaymentSourceService.Query(state, Registry, owner, owner.Payment.PaymentCost).Any(r => r.SourceRef.DefinitionId == M01), Is.False);
            Snapshot("正式资格拒绝-" + scenario, owner.Payment);
        }

        [TestCase(true)][TestCase(false)]
        public void FormalM01AndExistingPassengerEachCommitOnceRegardlessOfInsertionOrder(bool martinFirst)
        {
            var state = Fixture(passenger: true);
            state.FindPlayer(2).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2, GoldVoucher = 2 };
            var owner = Build(state); string id = owner.EffectId;
            var all = owner.SettlementWindow.Entries.ToList();
            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(all.Single(e => e.SourceDefinitionId == "operator.passenger").PaymentInsertion, Is.Null, "正式异客保持已有API1 Offer");
            foreach (string definition in martinFirst ? new[] { M01, "operator.passenger" } : new[] { "operator.passenger", M01 })
            {
                owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
                Invoke(state, owner.SettlementWindow.Entries.Single(e => e.SourceDefinitionId == definition));
                Answer(state, NormalizedValue.CreateString(definition == M01 ? "Iron=1" : "Originium=1"));
            }
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            var current = Open(state);
            Answer(state, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(owner.Payment.SourceReceipts.Count, Is.EqualTo(2));
            Assert.That(owner.Payment.SourceReceipts.Sum(r => r.ItemIds.Count), Is.EqualTo(1));
            Assert.That(owner.Payment.FinalCost.GoldVoucher, Is.EqualTo(1));
            Assert.That(owner.Payment.FinalCost.Originium, Is.EqualTo(1));
            Assert.That(owner.Payment.FinalCost.Iron, Is.EqualTo(1));
            string committed = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TrySubmitInteraction(current.GetStableInteractionId(), 2, current.StateRevision, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(committed));
            Snapshot("正式M01与异客原付款-顺序" + martinFirst, owner.Payment);
        }

        [Test] public void CancelMustNotConsumeUnspentCardQuotaOrPreventChoosingItAgain()
        {
            var state = Fixture(); var owner = Build(state); string id = owner.EffectId;
            var entry = Entry(owner); string stock = Resources(state);
            Invoke(state, entry); Answer(state, NormalizedValue.CreateBoolean(false));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(Open(state).OwnerEffectId, Is.EqualTo(id));
            Assert.That(Resources(state), Is.EqualTo(stock));
            Assert.That(owner.Payment.SourceDrafts, Is.Empty);
            Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            string before = JsonConvert.SerializeObject(state);
            bool retry = new EffectTreeExecutor(state, Registry).TryInvokeCharacterSettlementOption(2, Entry(owner).OptionId, "", state.EffectRuntime.StateRevision, out _, out string why);
            Snapshot("取消后未使用卡面但入口受理次数", new { retry, why, entry = Entry(owner), payment = owner.Payment, unchanged = before == JsonConvert.SerializeObject(state) });
            Assert.That(retry, Is.True, "取消未发生替代或收费；受理次数不能冒充每次建设已替代1份。实际拒绝：" + why);
            Pump(state); Answer(state, NormalizedValue.CreateString(""));
            state = Recover(state, "R12两次取消后原付款恢复");
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(Entry(owner).AcceptedUseCount, Is.EqualTo(2));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(state, Entry(owner)), Is.Zero);
            Assert.That(owner.Payment.SourceDrafts, Is.Empty); Assert.That(Resources(state), Is.EqualTo(stock));
            int stale = state.EffectRuntime.StateRevision - 1; before = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TryInvokeCharacterSettlementOption(2, Entry(owner).OptionId, "", stale, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Invoke(state, Entry(owner)); Answer(state, NormalizedValue.CreateString("Iron=1"));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(Entry(owner).AcceptedUseCount, Is.EqualTo(3));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(state, Entry(owner)), Is.EqualTo(1));
            Assert.That(owner.Payment.SourceDrafts.Single().Allocation.Iron, Is.EqualTo(1));
            Assert.That(owner.Payment.SourceReceipts, Is.Empty);
        }
        [Test] public void HistoricalVersionOneConfiguredArchiveKeepsHistoryAndCanWithdrawBeforePayment()
        {
            Fixture(); // 只建立同正式内容的Registry，历史输入不改写。
            string index = Path.Combine(Directory.GetCurrentDirectory(), "prompt/企业家内容实施规划-20261019/实施记录/QY-016-M01-PAYMENT-INSERTION-DECLARATION-R9/原生证据SHA.json");
            var record = JArray.Parse(File.ReadAllText(index)).Single(x => ((string)x["path"]).Contains("QY016-M01-R9-a003-") && ((string)x["path"]).EndsWith(".archive.json", StringComparison.Ordinal));
            string path = (string)record["path"]; string original;
            using (var sha = SHA256.Create()) original = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            Assert.That(original, Is.EqualTo((string)record["sha256"]));
            var restored = HostRecoveryService.RecoverJson(File.ReadAllText(path), Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); var state = restored.State; LastState = state;
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.Payment?.SourceDrafts.Any(d => d.Rule.SourceRef.DefinitionId == M01) == true);
            string id = owner.EffectId;
            Assert.That(Entry(owner).PaymentInsertion.Version, Is.EqualTo(ResourcePaymentInsertionService.LegacyVersion));
            Assert.That(Entry(owner).AcceptedUseCount, Is.EqualTo(1));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(state, Entry(owner)), Is.EqualTo(1));
            string stock = Resources(state); Invoke(state, Entry(owner)); Answer(state, NormalizedValue.CreateString(""));
            state = Recover(state, "R12真实v1历史配置撤回后恢复"); owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(Entry(owner).PaymentInsertion.Version, Is.EqualTo(ResourcePaymentInsertionService.Version));
            Assert.That(Entry(owner).AcceptedUseCount, Is.EqualTo(2)); Assert.That(Entry(owner).PaymentInsertion.Invocations.Count, Is.EqualTo(2));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(state, Entry(owner)), Is.Zero);
            Assert.That(owner.Payment.SourceReceipts, Is.Empty); Assert.That(Resources(state), Is.EqualTo(stock));
            using (var sha = SHA256.Create()) Assert.That(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(), Is.EqualTo(original));
            Snapshot("R12真实旧档未改原字节", new { path, sha256 = original });
        }

        [TestCase(false)][TestCase(true)]
        public void OldRevisionOrLostFormalAwardCannotInvokeOrCharge(bool loseAward)
        {
            var state = Fixture(); var owner = Build(state); var entry = Entry(owner);
            int revision = state.EffectRuntime.StateRevision;
            if (loseAward) state.ItemZones.Items.Single(i => i.ItemId == entry.SourceItemId).ControllerSubject = ItemSubjectRef.Player(3); // 明确隔离负向来源失效，不操作正常局。
            string before = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TryInvokeCharacterSettlementOption(2, entry.OptionId, "", loseAward ? revision : revision - 1, out _, out string why), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Snapshot("正式M01拒绝不变状态-失效" + loseAward, new { why, entry });
        }

        [TestCase(0,2)][TestCase(1,0)]
        public void SourceAllocationCanExistButFinalPaymentStillRequiresItsFullResources(int vouchers, int iron)
        {
            var state = Fixture();
            state.FindPlayer(2).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = iron, GoldVoucher = vouchers };
            if (iron == 0)
            {
                // 原始费用的缺口超过真实可替代份额，正常Build本身就应拒绝，无须强造付款请求。
                string originalStock = Resources(state);
                var card = Card(state, Facility);
                var rejected = Run(state, FacilityBuildEffectSpecFactory.Build(2, card.ItemId, -1, "resources", false, "qy016.r9.build", false, card.ZoneId, "deck"));
                Snapshot("替代后原费用仍不足-建设提前拒绝", rejected);
                Assert.That(rejected.Status, Is.EqualTo(EffectNodeStatus.Failed));
                Assert.That(rejected.Payment, Is.Null);
                Assert.That(Resources(state), Is.EqualTo(originalStock));
                Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
                return;
            }
            var owner = Build(state); string id = owner.EffectId; string stock = Resources(state);
            Invoke(state, Entry(owner)); Answer(state, NormalizedValue.CreateString("Iron=1"));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.SourceDrafts.Single().Allocation.Iron, Is.EqualTo(1), "选择配置不等于当前最终付款足额");
            var request = Open(state); string before = JsonConvert.SerializeObject(state);
            bool accepted = new EffectTreeExecutor(state, Registry).TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out string why);
            Assert.That(accepted, Is.False, "不足额必须在Host原确认提交前拒绝");
            Assert.That(why, Is.EqualTo("insufficient_resource"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Snapshot("最终付款不足-券" + vouchers + "铁" + iron, new { accepted, why, payment = owner.Payment, owner.Status, request });
            Assert.That(Resources(state), Is.EqualTo(stock));
            Assert.That(owner.Payment.IsCommitted, Is.False);
            Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Invoke(state, Entry(owner)); Answer(state, NormalizedValue.CreateString(""));
            state = Recover(state, "R12不足额后原来源撤回配置");
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.SourceDrafts.Single().Status, Is.EqualTo("withdrawn"));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(state, Entry(owner)), Is.Zero);
            Assert.That(Resources(state), Is.EqualTo(stock));
            Answer(state, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.True); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.FindPlayer(2).Resources.Originium + state.FindPlayer(2).Resources.OriginiumShard + state.FindPlayer(2).Resources.Iron, Is.Zero);
        }
    }
}
