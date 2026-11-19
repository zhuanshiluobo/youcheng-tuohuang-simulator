using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 当前正式来源声明/真实Lua的定向隔离；insertion只加在私有脚本文本，不改正式卡面或内容包。
    public sealed class PaymentInsertionContractR11Tests
    {
        public static GameState LastState;
#if UNITY_EDITOR
        [SetUp] public void ResetEvidence() { LastState = null; }
        [TearDown] public void PreserveEvidence()
        {
            if (LastState == null) return;
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try { artifacts.WriteLabeledText("QY007-R11真实隔离终态", JsonConvert.SerializeObject(LastState, Formatting.Indented)); }
            finally { artifacts.Finish(); }
        }
#endif
        public static void AuthorizeLegacyFixture(GameState state, EffectRegistry registry)
        {
            state.ContentDefinitions = state.ContentDefinitions ?? new GameContentDefinitions();
            registry.ContentPackHash = state.ContentPackHash;
            var mode = state.CharacterUses.Single().Modes.Single(); mode.AbilityId = "operator.passenger.tactic"; mode.Mode = "tactic";
            Bind(state, registry, "character/operator.passenger.json", "operator.passenger.payment", -1);
        }
        private static LuaSnapshotValue Snapshot(NormalizedValue n)
        {
            if (n == null || n.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            if (n.Kind == NormalizedValueKind.Object) return LuaSnapshotValue.Object(n.Properties.Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            if (n.Kind == NormalizedValueKind.Array) return LuaSnapshotValue.Array(n.Items.Select(Snapshot).ToList());
            if (n.Kind == NormalizedValueKind.String) return LuaSnapshotValue.String(n.StringValue);
            if (n.Kind == NormalizedValueKind.Integer) return LuaSnapshotValue.Integer(n.IntegerValue);
            if (n.Kind == NormalizedValueKind.Boolean) return LuaSnapshotValue.Boolean(n.BooleanValue);
            throw new InvalidOperationException("fixture_snapshot_kind");
        }
        private static void Bind(GameState state, EffectRegistry registry, string definitionFile, string ability, int maxUses, bool freeze = true)
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var data = JObject.Parse(File.ReadAllText(Path.Combine(root, definitionFile)));
            var d = data["abilitySources"].Single(s => (string)s["abilityId"] == ability).ToObject<AbilitySourceDeclaration>(); d.DefinitionId = (string)data["definitionId"];
            var handler = data["abilityHandlers"].Single(h => (string)h["handlerId"] == d.HandlerRef);
            string code = File.ReadAllText(Path.Combine(root, (string)handler["script"]));
            if (maxUses >= 0) code = "local body=(function() " + code + " end)(); return function(ctx) local r=body(ctx); for _,v in ipairs(r.items) do v.insertion={apiVersion=1,maxUses=" + maxUses +
                ",presentation={label={key='core:presentation.unavailable'},description={key='core:presentation.unavailable'},ariaLabel={key='core:presentation.unavailable'},contentMode='text'}} end;return r end";
            string hash = LuaContentHasher.ComputeSha256(code);
            var script = new LuaScriptDefinition(d.DefinitionId, d.AbilityId, d.HandlerRef, (string)handler["version"], code, hash);
            registry.RegisterAbilityHandler(d.HandlerRef, hash, c => Array.Empty<EffectSpec>(), (string)handler["version"]);
            registry.RegisterAbilityPureHandler(d.HandlerRef, (call, query) => {
                var facade = LuaFacadeSnapshot.FromInvocation("p" + call.SourceRef.AuthorizedPlayerId, 0, 0, 2, script.DefinitionVersion);
                facade.GlobalContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("instances", LuaSnapshotValue.Array(call.State.ItemZones.Items.Select(i =>
                    LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("id", LuaSnapshotValue.String(i.ItemId)), new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(i.DefinitionId)),
                        new LuaSnapshotEntry("controller", LuaSnapshotValue.String("p" + i.ControllerSubject.PlayerId)) })).ToList())) });
                facade.EventPayload = Snapshot(Object("queryId", NormalizedValue.CreateString(query.QueryId), "queryKind", NormalizedValue.CreateString(query.QueryKind),
                    "basisDigest", NormalizedValue.CreateString(query.FactsDigest), "facts", query.Facts.Clone()));
                var invocation = new LuaInvocationContext(query.QueryId, "RuleQuery", "p" + call.SourceRef.AuthorizedPlayerId, query.StateRevision, script.DefinitionVersion, 2, 0, 0, facade) { ResponseKind = "abilityRuleQuery" };
                var result = new MoonSharpLuaRuntimeHost().Invoke(script, invocation);
                Assert.That(result.IsSuccess, Is.True, result.Diagnostic); return result.AbilityQueryResult;
            });
            state.ContentDefinitions.AbilitySources.Add(d);
            if (ability == "operator.passenger.payment")
            {
                var activation = data["abilitySources"].Single(s => (string)s["abilityId"] == "operator.passenger.tactic").ToObject<AbilitySourceDeclaration>();
                activation.DefinitionId = d.DefinitionId;
                string activationText = File.ReadAllText(Path.Combine(root, "lua/characters/operator.passenger.tactic.lua"));
                registry.RegisterAbilityHandler(activation.HandlerRef, LuaContentHasher.ComputeSha256(activationText), c => Array.Empty<EffectSpec>());
                state.ContentDefinitions.AbilitySources.Add(activation);
            }
            if (freeze) registry.FreezeAbilityCatalog(state.ContentDefinitions.AbilitySources);
        }
        private static Tuple<GameState, EffectRegistry> Fixture(int maxUses, bool heterogeneous = false, bool build = false, bool withAward = false, int markerCount = 3)
        {
            var type = typeof(YC.Tests.EditMode.SettlementFlowContractTests);
            var state = (GameState)type.GetMethod("PaymentStateCoreWithMarkerCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { build, withAward, markerCount });
            state.ContentDefinitions = state.ContentDefinitions ?? new GameContentDefinitions();
            state.ContentDefinitions.AbilitySources.Clear();
            var pack = ExternalContentPack.Current.CreateActiveSnapshot(new GameRuleConfiguration { MapId = state.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
            state.ContentPackHash = state.RuleConfiguration.ContentPackHash = pack.ContentHash;
            state.ContentDefinitions.Characters = pack.CreateCharacters().ToList();
            var reg = new EffectRegistry { ContentPackHash = state.ContentPackHash };
            ResourceEffectExecutor.Register(reg); ResourcePaymentChoiceEffectExecutor.Register(reg); ResourcePaymentSubstitutionEffectExecutor.Register(reg);
            GenericLuaEffectExecutor.Register(reg); ItemTransferEffectExecutor.Register(reg); SettlementEntryService.Register(reg); FacilityBuildEffectExecutor.Register(reg);
            CharacterCoverEffectExecutor.Register(reg); CharacterCardLuaCatalog.Register(reg, pack);
            var mode = state.CharacterUses.Single().Modes.Single(); mode.AbilityId = "operator.passenger.tactic"; mode.Mode = "tactic";
            if (heterogeneous)
            {
                var markers = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).ToList();
                markers[0].LocalState = Object("amount", NormalizedValue.CreateInteger(1));
            }
            Bind(state, reg, "character/operator.passenger.json", "operator.passenger.payment", maxUses, !withAward);
            if (withAward) Bind(state, reg, "award/entrepreneur.award.M01.json", "entrepreneur.award.M01.payment", -1);
            state.CharacterUses.Clear();
            state.ItemZones.Items.RemoveAll(i => i.TypeId == "core.generated_character_card" || i.TypeId == "core.basic_character_card");
            var card = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.passenger" && i.ControllerSubject.PlayerId == 1);
            card.ZoneId = FindZone(state, "hand", 1).ZoneId;
            foreach (var marker in state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1)) marker.ZoneId = marker.OriginalSupplyZoneId;
            state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = state.StartPlayerId = 1;
            LastState = state;
            new EffectTreeExecutor(state, reg).CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run(state, reg); Answer(state, reg, NormalizedValue.CreateString(card.ItemId));
            new EffectTreeExecutor(state, reg).CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic")); Run(state, reg);
            Answer(state, reg, NormalizedValue.CreateString("3"));
            if (markerCount > 3)
            {
                // Passenger原初始存放仍是3；额外隔离库存经API-25既有玩家标记移动合法入区，不扩大角色规则。
                var extra = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 &&
                    i.ZoneId == i.OriginalSupplyZoneId).ToArray();
                var storage = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId && z.LocalZoneId == "markers");
                var engine = new EffectTreeExecutor(state, reg);
                string moved = engine.CreateRoot(TokenSpec(1, extra.Select(i => i.ItemId), extra.First().ZoneId, storage.ZoneId,
                    extra.Length, "core.player_marker_storage"));
                Run(state, reg); Assert.That(engine.GetNode(moved).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == storage.ZoneId), Is.EqualTo(markerCount));
            }
            return Tuple.Create(state, reg);
        }
        private static InteractionRequest Open(GameState s) => s.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
        private static void Run(GameState s, EffectRegistry r) => Assert.That(new EffectTreeExecutor(s, r).RunUntilQuiescent().Faulted, Is.False, s.EffectRuntime.LastFaultMessage);
        private static void Answer(GameState s, EffectRegistry r, NormalizedValue answer)
        { var q = Open(s); Assert.That(new EffectTreeExecutor(s, r).TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, answer, out string why), Is.True, why); Run(s, r); }
        private static string Pay(GameState s, EffectRegistry r)
        {
            var spec = ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 2, GoldVoucher = 3 }); spec.DefinitionVersion = ResourceEffectExecutor.PaymentDefinitionVersion;
            string id = new EffectTreeExecutor(s, r).CreateRoot(spec); Run(s, r); return id;
        }
        private static SettlementEntryRuntimeState Entry(GameState s, string owner) => s.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner).SettlementWindow.Entries.Single(e => e.PaymentInsertion != null);
        private static void Invoke(GameState s, EffectRegistry r, string id)
        { Assert.That(new EffectTreeExecutor(s, r).TryInvokeCharacterSettlementOption(1, id, "", s.EffectRuntime.StateRevision, out bool already, out string why), Is.True, why); Assert.That(already, Is.False); Run(s, r); }
        private static GameState Restore(GameState s, EffectRegistry r)
        { LastState = s; var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(s)), r); Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); LastState = restored.State; return restored.State; }

        // 只检原始字段合同／Host的反序列化前门禁；这些最小JSON不是可恢复完整对局。
        private static JObject ItemDraftRawProbe() => new JObject {
            ["PaymentInsertionItems"] = JObject.FromObject(new PaymentInsertionItemDraft {
                RequiredCount = 2, AwaitingRevision = 1, CandidateIds = new List<string> { "one", "two" },
                Picks = new List<PaymentInsertionItemPick> { new PaymentInsertionItemPick { EffectId = "pick.one", ItemId = "one" } } }) };

        [Test] public void ItemDraftRawShapeAcceptsCompleteCurrentDataAndAbsentOptionalState()
        {
            var node = ItemDraftRawProbe(); var original = node.ToString(Formatting.None);
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(node, true), Is.Empty);
            Assert.That(node.ToString(Formatting.None), Is.EqualTo(original), "校验不补写或转换原件");
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(new JObject(), true), Is.Empty);
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(new JObject { ["PaymentInsertionItems"] = JValue.CreateNull() }, true), Is.Empty);
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(node, false), Is.EqualTo("payment_insertion_item_draft_raw_shape_invalid"), "定义规格不能夹带运行时草稿");
        }

        [TestCase("ProtocolVersion")][TestCase("RequiredCount")][TestCase("AwaitingRevision")]
        [TestCase("Cancelled")][TestCase("CandidateIds")][TestCase("Picks")]
        public void ItemDraftRawShapeRejectsMissingFieldsBeforeDtoDefaults(string field)
        {
            var node = ItemDraftRawProbe(); ((JObject)node["PaymentInsertionItems"]).Remove(field);
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(node, true), Is.EqualTo("payment_insertion_item_draft_raw_shape_invalid"));
        }

        [TestCase("ProtocolVersion", "1")][TestCase("RequiredCount", "2")]
        [TestCase("AwaitingRevision", "1")][TestCase("Cancelled", "false")]
        public void ItemDraftRawShapeRejectsJsonNetScalarCoercion(string field, string scalar)
        {
            var node = ItemDraftRawProbe(); node["PaymentInsertionItems"][field] = scalar;
            Assert.DoesNotThrow(() => node["PaymentInsertionItems"].ToObject<PaymentInsertionItemDraft>(), "旧DTO读取可转换该值；新门禁须保留原始类型约束");
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(node, true), Is.EqualTo("payment_insertion_item_draft_raw_shape_invalid"));
        }

        [TestCase("CandidateIds[0]")][TestCase("Picks[0].EffectId")][TestCase("Picks[0].ItemId")]
        public void ItemDraftRawShapeRejectsCoercedNestedIdentity(string path)
        {
            var node = ItemDraftRawProbe(); node["PaymentInsertionItems"].SelectToken(path).Replace(new JValue(1));
            Assert.That(ResourcePaymentInsertionService.ValidateItemSelectionRaw(node, true), Is.EqualTo("payment_insertion_item_draft_raw_shape_invalid"));
        }

        [Test] public void HostRawTraversalRejectsMissingDraftProtocolBeforeDeserialization()
        {
            var node = ItemDraftRawProbe(); node["EffectTypeId"] = ResourcePaymentSubstitutionEffectExecutor.TypeId;
            ((JObject)node["PaymentInsertionItems"]).Remove("ProtocolVersion");
            var state = new JObject { ["EffectRuntime"] = new JObject { ["EffectNodes"] = new JArray(node) } };
            var method = typeof(HostRecoveryService).GetMethod("TryValidateRetiredCardMarkerRawState",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var args = new object[] { state, null };
            Assert.That(method.Invoke(null, args), Is.EqualTo(false));
            Assert.That(args[1], Is.EqualTo("payment_insertion_item_draft_raw_shape_invalid"));
        }

        [Test] public void RealRuleWithoutInsertionDoesNotBecomeClickableOffer()
        {
            var f = Fixture(-1); string id = Pay(f.Item1, f.Item2);
            var owner = f.Item1.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(ResourcePaymentSourceService.Query(f.Item1, f.Item2, owner, owner.Payment.PaymentCost).Count, Is.EqualTo(1));
            Assert.That(owner.SettlementWindow.Entries.All(e => e.PaymentInsertion == null), Is.True);
        }
        [TestCase(false)][TestCase(true)] public void StableSourceCanRepeatWithinDeclaredLimitAndCommitOnlyOnce(bool heterogeneous)
        {
            var f = Fixture(2, heterogeneous); var s = f.Item1; var r = f.Item2; string id = Pay(s, r);
            string option = Entry(s, id).OptionId, interaction = Open(s).GetStableInteractionId();
            Assert.That(s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).SettlementWindow.Entries.Count, Is.EqualTo(1), "新合同不得并列旧zone-only入口。");
            for (int use = 0; use < 2; use++)
            {
                s = Restore(s, r); Invoke(s, r, option); s = Restore(s, r);
                Answer(s, r, NormalizedValue.CreateString("Iron=1"));
                if (Open(s).InteractionTypeId == "effect.choice.execute")
                {
                    Assert.That(heterogeneous, Is.True); s = Restore(s, r); var q = Open(s);
                    Assert.That(q.CandidateIds.Count, Is.EqualTo(3 - use));
                    Answer(s, r, NormalizedValue.CreateString(q.CandidateIds.Last()));
                }
                Assert.That(Open(s).OwnerEffectId, Is.EqualTo(id));
                Assert.That(Entry(s, id).AcceptedUseCount, Is.EqualTo(use + 1));
                Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            }
            s = Restore(s, r); Invoke(s, r, option);
            var replacement = Open(s); string before = JsonConvert.SerializeObject(s);
            Assert.That(new EffectTreeExecutor(s, r).TrySubmitInteraction(replacement.GetStableInteractionId(), 1, replacement.StateRevision, NormalizedValue.CreateString("Iron=3"), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            Answer(s, r, NormalizedValue.CreateBoolean(false)); s = Restore(s, r);
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(s, Entry(s, id)), Is.EqualTo(2));
            Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            var p = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Payment;
            Assert.That(p.IsCommitted, Is.True); Assert.That(p.SourceReceipts.Count, Is.EqualTo(1)); Assert.That(p.SourceReceipts[0].ItemIds.Count, Is.EqualTo(2));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); s = Restore(s, r);
            Assert.That(p.FinalCost.Iron, Is.Zero);
        }
        [TestCase(3, false)][TestCase(17, false)][TestCase(17, true)]
        public void MultiItemDraftKeepsEveryCandidateAndCommitsOnlyTheCompleteSelection(int markerCount, bool cancel)
        {
            var fixture = Fixture(1, true, markerCount: markerCount); var s = fixture.Item1; var r = fixture.Item2;
            string ownerId = Pay(s, r); string option = Entry(s, ownerId).OptionId;
            Invoke(s, r, option); Answer(s, r, NormalizedValue.CreateString("Iron=2"));
            var selection = s.EffectRuntime.EffectNodes.Single(n => n.PaymentInsertionItems != null);
            var candidates = selection.PaymentInsertionItems.CandidateIds.ToArray();
            Assert.That(candidates.Length, Is.EqualTo(markerCount)); Assert.That(selection.PaymentInsertionItems.RequiredCount, Is.EqualTo(2));
            var originalZones = candidates.ToDictionary(id => id, id => s.ItemZones.Items.Single(i => i.ItemId == id).ZoneId);
            string[] CurrentLeafItems()
            {
                var ids = new List<string>();
                void Visit(EffectSpecRuntimeState spec)
                {
                    if (spec.EffectTypeId == ResourcePaymentInsertionService.PickItemTypeId) ids.Add(Text(spec.NormalizedArguments, "itemId"));
                    if (spec.EffectTypeId == LuaDomainEffectTypeIds.Choice) Assert.That(Value(spec.NormalizedArguments, "options").Items.Count, Is.LessThanOrEqualTo(16));
                    foreach (var child in spec.NestedEffects) Visit(child);
                }
                var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == Open(s).OwnerEffectId);
                foreach (var child in owner.NestedEffects) Visit(child);
                return ids.ToArray();
            }
            void Pick(string item)
            {
                for (int guard = 0; !Open(s).CandidateIds.Contains(item) && guard < 4; guard++)
                {
                    var q = Open(s); var node = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
                    string page = q.CandidateIds.Single(id => {
                        int index = Value(node.NormalizedArguments, "options").Items.FindIndex(v => v.StringValue == id);
                        return JsonConvert.SerializeObject(node.NestedEffects[node.NestedGroupSizes.Take(index).Sum()]).Contains("\"" + item + "\"");
                    });
                    Answer(s, r, NormalizedValue.CreateString(page));
                }
                Assert.That(Open(s).CandidateIds, Does.Contain(item)); Answer(s, r, NormalizedValue.CreateString(item));
            }
            Assert.That(CurrentLeafItems(), Is.EquivalentTo(candidates), "真实草稿候选须完整，不能截前16项");
            s = Restore(s, r); Pick(candidates.Last());
            Assert.That(CurrentLeafItems(), Is.EquivalentTo(candidates.Take(markerCount - 1)));
            s = Restore(s, r);
            foreach (var pair in originalZones) Assert.That(s.ItemZones.Items.Single(i => i.ItemId == pair.Key).ZoneId, Is.EqualTo(pair.Value));
            Assert.That(s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment.IsCommitted, Is.False);
            if (cancel)
            {
                Answer(s, r, NormalizedValue.CreateBoolean(false)); s = Restore(s, r);
                var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
                Assert.That(owner.Payment.SourceDrafts, Is.Empty); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
                Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(s, Entry(s, ownerId)), Is.Zero);
                foreach (var pair in originalZones) Assert.That(s.ItemZones.Items.Single(i => i.ItemId == pair.Key).ZoneId, Is.EqualTo(pair.Value));
                return;
            }
            Pick(candidates.First()); s = Restore(s, r);
            var selectedIds = new[] { candidates.Last(), candidates.First() };
            var payment = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            Assert.That(payment.SourceDrafts.Single().SelectedItemIds, Is.EquivalentTo(selectedIds)); Assert.That(payment.SourceReceipts, Is.Empty);
            foreach (var pair in originalZones) Assert.That(s.ItemZones.Items.Single(i => i.ItemId == pair.Key).ZoneId, Is.EqualTo(pair.Value));
            Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            payment = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            Assert.That(payment.IsCommitted, Is.True); Assert.That(payment.SourceReceipts.Single().ItemIds, Is.EquivalentTo(selectedIds));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            s = Restore(s, r); Run(s, r);
            Assert.That(s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment.SourceReceipts.Single().ItemIds, Is.EquivalentTo(selectedIds));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
        }

        [Test] public void WrongOwnerStaleRevisionAndLostSourceDoNotChangeState()
        {
            var f = Fixture(2); var s = f.Item1; string id = Pay(s, f.Item2); var e = Entry(s, id);
            foreach (var identity in new[] { Tuple.Create(2, s.EffectRuntime.StateRevision), Tuple.Create(1, s.EffectRuntime.StateRevision - 1) })
            { string before = JsonConvert.SerializeObject(s); Assert.That(new EffectTreeExecutor(s, f.Item2).TryInvokeCharacterSettlementOption(identity.Item1, e.OptionId, "", identity.Item2, out _, out _), Is.False); Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before)); }
            s.CharacterUses.Single().Modes.Single(m => m.Mode == "tactic").SubscriptionActive = false;
            string lost = JsonConvert.SerializeObject(s); Assert.That(new EffectTreeExecutor(s, f.Item2).TryInvokeCharacterSettlementOption(1, e.OptionId, "", s.EffectRuntime.StateRevision, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(lost));
        }
        [Test] public void ResourceFeeAndTokenSourcesShareOneBuildPayment()
        {
            var f = Fixture(1, build: true, withAward: true); var s = f.Item1; var r = f.Item2;
            var supply = s.ItemZones.Items.Single(i => i.ZoneId == FindZone(s, "facility_supply_0").ZoneId);
            var spec = FacilityBuildEffectSpecFactory.Build(1, supply.ItemId, -1, "resources", false, "fixture.build", consumeMainAction: false, expectedSourceZoneId: supply.ZoneId);
            new EffectTreeExecutor(s, r).CreateRoot(spec); Run(s, r);
            var q = Open(s); string ownerId = q.OwnerEffectId; var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            var entries = owner.SettlementWindow.Entries.Where(e => e.PaymentInsertion != null).ToList(); Assert.That(entries.Count, Is.EqualTo(2));
            foreach (var e in entries)
            { Invoke(s, r, e.OptionId); Answer(s, r, NormalizedValue.CreateString(e.PaymentInsertion.Rule.ItemCost == null ? "Iron=1" : "Originium=1")); }
            s = Restore(s, r); Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            var p = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            Assert.That(p.IsCommitted, Is.True); Assert.That(p.SourceReceipts.Count, Is.EqualTo(2));
            Assert.That(p.FinalCost.GoldVoucher, Is.EqualTo(4)); Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1));
            Assert.That(p.SourceReceipts.Sum(x => x.ItemIds.Count), Is.EqualTo(1));
        }
        [Test] public void MartinPaymentReplacementReturnsToBuildAndRetainsHistoricalInvocation()
        {
            var fixture = Fixture(1, build: true, withAward: true); var state = fixture.Item1; var registry = fixture.Item2;
            state.FindPlayer(1).Resources.Originium = 2;
            var supply = state.ItemZones.Items.Single(i => i.ZoneId == FindZone(state, "facility_supply_0").ZoneId);
            var spec = FacilityBuildEffectSpecFactory.Build(1, supply.ItemId, -1, "resources", false, "fixture.build",
                consumeMainAction: false, expectedSourceZoneId: supply.ZoneId);
            new EffectTreeExecutor(state, registry).CreateRoot(spec); Run(state, registry);
            string ownerId = Open(state).OwnerEffectId;
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            string optionId = owner.SettlementWindow.Entries.Single(e => e.PaymentInsertion?.Rule?.RuleId == "martin.concession").OptionId;
            Invoke(state, registry, optionId); Answer(state, registry, NormalizedValue.CreateString("Iron=1"));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            var entry = owner.SettlementWindow.Entries.Single(e => e.OptionId == optionId);
            Assert.That(entry.TargetEffectId, Is.Not.Empty, "历史执行节点必须留存供恢复校验");
            Assert.That(entry.PaymentInsertion.Invocations, Has.Count.EqualTo(1));
            var option = registry.GetSettlementOptions(state, 1).Single(o => o.OptionId == optionId);
            Assert.That(option.SettlementState, Is.EqualTo("available")); Assert.That(option.IsEnabled, Is.True);
            var type = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp")
                .GetType("YC.Presentation.SettlementEffectStripBinding", true);
            var binding = Activator.CreateInstance(type);
            type.GetMethod("SetScope").Invoke(binding, new object[] { state.GameId, 1 });
            Assert.DoesNotThrow(() => type.GetMethod("BindWindow").Invoke(binding,
                new object[] { option.WindowId, new[] { SettlementOptionView.FromOption(option) } }),
                "支付替代完成后必须接受正式结算投影，才能返回建设付款");
            Assert.That(Open(state).OwnerEffectId, Is.EqualTo(ownerId));
            var other = owner.SettlementWindow.Entries.Single(e => e.PaymentInsertion?.Rule?.RuleId == "passenger.stored_markers");
            Invoke(state, registry, other.OptionId);
            var waiting = registry.GetSettlementOptions(state, 1);
            Assert.That(waiting.Single(o => o.OptionId == optionId).IsEnabled, Is.False);
            Assert.That(waiting.Single(o => o.OptionId == other.OptionId).SettlementState, Is.EqualTo("executing"));
            Assert.DoesNotThrow(() => type.GetMethod("BindWindow").Invoke(binding,
                new object[] { option.WindowId, waiting.Select(SettlementOptionView.FromOption).ToArray() }),
                "另一个付款来源执行时，前一来源仍应保留有效但禁用的展示投影");
            Answer(state, registry, NormalizedValue.CreateBoolean(false));
            Answer(state, registry, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(owner.Payment.SourceReceipts.Count(receipt => receipt.Rule.RuleId == "martin.concession"), Is.EqualTo(1));
            Restore(state, registry);
        }
        [TestCase("Originium", "Iron", false)] [TestCase("Iron", "OriginiumShard", false)] [TestCase("OriginiumShard", "Originium", false)]
        [TestCase("Originium", "Iron", true)] [TestCase("Iron", "OriginiumShard", true)] [TestCase("OriginiumShard", "Originium", true)]
        public void ActualMartinAndPassengerPaymentSourcesBothReturnToBuild(string martinResource, string passengerResource, bool passengerFirst)
        {
            // 两项均使用原始正式Lua；不为异客夹具注入新的insertion声明。
            var fixture = Fixture(-1, build: true, withAward: true); var state = fixture.Item1; var registry = fixture.Item2;
            var supply = state.ItemZones.Items.Single(i => i.ZoneId == FindZone(state, "facility_supply_0").ZoneId);
            state.ContentDefinitions.Facilities.Single(f => f.FacilityId == supply.DefinitionId).ResourceCost.OriginiumShard = 2;
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 3, OriginiumShard = 3, Iron = 3, GoldVoucher = 10 };
            new EffectTreeExecutor(state, registry).CreateRoot(FacilityBuildEffectSpecFactory.Build(1, supply.ItemId, -1,
                "resources", false, "fixture.actual-sources", consumeMainAction: false, expectedSourceZoneId: supply.ZoneId));
            Run(state, registry);
            string ownerId = Open(state).OwnerEffectId;
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            string martin = owner.SettlementWindow.Entries.Single(e => e.PaymentInsertion?.Rule?.RuleId == "martin.concession").OptionId;
            string passenger = owner.SettlementWindow.Entries.Single(e => e.SourceDefinitionId == "operator.passenger" && e.PaymentInsertion == null).OptionId;
            var options = registry.GetSettlementOptions(state, 1);
            var type = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp")
                .GetType("YC.Presentation.SettlementEffectStripBinding", true);
            var binding = Activator.CreateInstance(type);
            type.GetMethod("SetScope").Invoke(binding, new object[] { state.GameId, 1 });
            string windowId = options.Single(o => o.OptionId == martin).WindowId;
            var steps = passengerFirst ? new[] { (passenger, passengerResource), (martin, martinResource) }
                : new[] { (martin, martinResource), (passenger, passengerResource) };
            foreach (var step in steps)
            {
                Invoke(state, registry, step.Item1);
                Assert.DoesNotThrow(() => type.GetMethod("BindWindow").Invoke(binding,
                    new object[] { windowId, registry.GetSettlementOptions(state, 1).Select(SettlementOptionView.FromOption).ToArray() }));
                Answer(state, registry, NormalizedValue.CreateString(step.Item2 + "=1"));
                Assert.DoesNotThrow(() => type.GetMethod("BindWindow").Invoke(binding,
                    new object[] { windowId, registry.GetSettlementOptions(state, 1).Select(SettlementOptionView.FromOption).ToArray() }));
            }
            Answer(state, registry, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId);
            Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(owner.Payment.SourceReceipts.Select(receipt => receipt.Rule.RuleId), Does.Contain("martin.concession"));
            Assert.That(owner.Payment.SourceReceipts.Select(receipt => receipt.Rule.RuleId), Does.Contain("passenger.stored_markers"));
            Restore(state, registry);
        }
        [Test] public void ExistingVersionOneInsertionStillRestoresWhilePending()
        {
            var f = Fixture(-1); var s = f.Item1; var r = f.Item2; string id = Pay(s, r);
            var entry = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).SettlementWindow.Entries.Single();
            Assert.That(entry.PaymentInsertion, Is.Null); Invoke(s, r, entry.OptionId); s = Restore(s, r);
            Answer(s, r, NormalizedValue.CreateString("Iron=2")); s = Restore(s, r);
            Assert.That(s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Payment.IsCommitted, Is.False);
            Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); Restore(s, r);
        }
        [Test] public void CancelKeepsAdmissionHistoryWithoutUsingConfigurationQuota()
        {
            var f = Fixture(1); var s = f.Item1; string id = Pay(s, f.Item2); string option = Entry(s, id).OptionId;
            Invoke(s, f.Item2, option); Answer(s, f.Item2, NormalizedValue.CreateBoolean(false)); s = Restore(s, f.Item2);
            Assert.That(Entry(s, id).AcceptedUseCount, Is.EqualTo(1)); Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Payment.SourceDrafts, Is.Empty);
            string before = JsonConvert.SerializeObject(s);
            Assert.That(new EffectTreeExecutor(s, f.Item2).TryInvokeCharacterSettlementOption(1, option, "", s.EffectRuntime.StateRevision - 1, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            Invoke(s, f.Item2, option); Answer(s, f.Item2, NormalizedValue.CreateBoolean(false)); s = Restore(s, f.Item2);
            Assert.That(Entry(s, id).AcceptedUseCount, Is.EqualTo(2));
            Assert.That(ResourcePaymentInsertionService.ConfiguredUseCount(s, Entry(s, id)), Is.Zero);
        }
        [Test] public void StaleDraftAndForgedBodyAreRejectedBeforeInvocation()
        {
            var f = Fixture(2); var s = f.Item1; string id = Pay(s, f.Item2); var entry = Entry(s, id);
            var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id); var rule = entry.PaymentInsertion.Rule;
            string unchanged = JsonConvert.SerializeObject(s); var open = Open(s);
            Assert.That(new EffectTreeExecutor(s, f.Item2).TryEditPaymentSource(1, id, open.GetStableInteractionId(), "save_source", rule.SourceRef.SourceKey, rule.RuleId,
                "", owner.Payment.PaymentGeneration, 0, new ResourceSet { Iron = 1 }, Array.Empty<string>(), s.EffectRuntime.StateRevision, "fixture.illegal.editor", out string editorReason), Is.False);
            Assert.That(editorReason, Is.EqualTo("payment_source_insertion_required")); Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(unchanged));
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(s, f.Item2, owner, rule.SourceRef.SourceKey, rule.RuleId,
                owner.Payment.PaymentGeneration, 0, new ResourceSet(), Array.Empty<string>(), "", out string why), Is.True, why);
            string before = JsonConvert.SerializeObject(s);
            Assert.That(new EffectTreeExecutor(s, f.Item2).TryInvokeCharacterSettlementOption(1, entry.OptionId, "", s.EffectRuntime.StateRevision, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            f.Item2.RefreshSettlementOffers(s, owner.SettlementWindow);
            entry = Entry(s, id); entry.Effects[0].NestedEffects.Add(ResourceEffectSpecFactory.GainScore(1, 99).ToPersistentDefinition());
            Assert.That(ResourcePaymentInsertionService.ValidateEntry(s, owner.SettlementWindow, entry, f.Item2), Is.False);
            before = JsonConvert.SerializeObject(s);
            Assert.That(new EffectTreeExecutor(s, f.Item2).TryInvokeCharacterSettlementOption(1, entry.OptionId, "", s.EffectRuntime.StateRevision, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
        }
        [Test] public void SourceLostAfterAllocationCannotCommitResourcesOrTokens()
        {
            var f = Fixture(1); var s = f.Item1; string id = Pay(s, f.Item2); Invoke(s, f.Item2, Entry(s, id).OptionId);
            Answer(s, f.Item2, NormalizedValue.CreateString("Iron=2"));
            s.CharacterUses.Single().Modes.Single(m => m.Mode == "tactic").SubscriptionActive = false;
            var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id); string before = JsonConvert.SerializeObject(s);
            Assert.That(ResourcePaymentPlanService.TryBuild(s, f.Item2, owner, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            var q = Open(s); bool accepted = new EffectTreeExecutor(s, f.Item2).TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision,
                NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out _);
            if (accepted) Run(s, f.Item2); else Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.False); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(s.ItemZones.Items.Count(i => i.ZoneId == ruleZone(s)), Is.EqualTo(3));
        }
        private static string ruleZone(GameState s) => s.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 && z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution").ZoneId;
        [Test] public void VersionTwoLuaCallAndClosedDeclarationRejectInvalidInputs()
        {
            var presentation = new SettlementPresentationDefinition { Label = new GameplayTextRef("core:presentation.unavailable"), Description = new GameplayTextRef("core:presentation.unavailable"), AriaLabel = new GameplayTextRef("core:presentation.unavailable") };
            var declaration = Object("apiVersion", NormalizedValue.CreateInteger(1), "maxUses", NormalizedValue.CreateInteger(1), "presentation", GameplayPresentationNormalizer.ToNormalized(presentation));
            Assert.That(ResourcePaymentInsertionService.TryReadDeclaration(declaration, out _), Is.True);
            declaration.Properties.Add(new NormalizedValueEntry { Name = "unknown", Value = NormalizedValue.CreateInteger(1) });
            Assert.That(ResourcePaymentInsertionService.TryReadDeclaration(declaration, out _), Is.False);
            foreach (bool invalid in new[] { false, true })
            {
                string code = "return function(ctx) return {Effect.PaymentSubstitution({apiVersion=2,player=ctx.playerId,paymentOwnerEffectId='owner',sourceKey='source',ruleId='rule',paymentGeneration=1,draftRevision=0" + (invalid ? ",sourceZoneId='old'" : "") + "})} end";
                var script = new LuaScriptDefinition("fixture.payment.call", "fixture.payment.call", "fixture.payment.call", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
                var result = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("fixture.call", "Fixture", "p1", 0, "1.0.0", 2, 0, 0));
                Assert.That(result.IsSuccess, Is.EqualTo(!invalid), result.Diagnostic);
                if (!invalid) Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1, ContentInstanceId = "fixture.payment.call", AbilityId = "fixture.payment.call" }, out _, out _), Is.True);
            }
        }
        [Test] public void ResourceLossAfterAcceptedConfirmationReopensWithoutFaultOrPartialPayment()
        {
            var f = Fixture(1); var s = f.Item1; var r = f.Item2; string id = Pay(s, r);
            Invoke(s, r, Entry(s, id).OptionId); Answer(s, r, NormalizedValue.CreateString("Iron=2"));
            var q = Open(s); string answered = q.GetStableInteractionId();
            Assert.That(new EffectTreeExecutor(s, r).TrySubmitInteraction(answered, 1, q.StateRevision,
                NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out string why), Is.True, why);
            // 明确的隔离时序反例：模拟受理与执行间库存已变化，不计正常合法点击或对局覆盖。
            s.FindPlayer(1).Resources.GoldVoucher = 2;
            Run(s, r);
            var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.False); Assert.That(owner.Payment.IsCommitRequested, Is.False);
            Assert.That(owner.Payment.SourceReceipts, Is.Empty); Assert.That(owner.Payment.SourceDrafts.Single().SelectedItemIds.Count, Is.EqualTo(2));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); Assert.That(Open(s).OwnerEffectId, Is.EqualTo(id));
            Assert.That(Open(s).GetStableInteractionId(), Is.Not.EqualTo(answered));
            s = Restore(s, r); string before = JsonConvert.SerializeObject(s);
            Assert.That(new EffectTreeExecutor(s, r).TrySubmitInteraction(answered, 1, q.StateRevision,
                NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(s), Is.EqualTo(before));
            // 延续同一隔离库存时序探针；不另开效果树改变revision后借旧Legacy请求提交。
            s.FindPlayer(1).Resources.GoldVoucher = 3;
            Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.True); Assert.That(owner.Payment.SourceReceipts.Single().ItemIds.Count, Is.EqualTo(2));
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.Zero); Restore(s, r);
        }
        [Test] public void ExistingVersionOneCancellationRestoresAndCanPayWithoutSubstitution()
        {
            var f = Fixture(-1); var s = f.Item1; var r = f.Item2;
            s.FindPlayer(1).Resources.Iron = 2; // 明确的隔离初始资源，原费用可不使用替代。
            string id = Pay(s, r); var entry = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).SettlementWindow.Entries.Single();
            Assert.That(entry.PaymentInsertion, Is.Null); Invoke(s, r, entry.OptionId); s = Restore(s, r);
            Answer(s, r, NormalizedValue.CreateBoolean(false)); s = Restore(s, r);
            var owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.SourceDrafts, Is.Empty); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5)); Assert.That(Open(s).OwnerEffectId, Is.EqualTo(id));
            Answer(s, r, NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            owner = s.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(owner.Payment.IsCommitted, Is.True); Assert.That(owner.Payment.SourceReceipts, Is.Empty);
            Assert.That(s.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); Assert.That(s.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(s.ItemZones.Items.Count(i => i.ZoneId == ruleZone(s)), Is.EqualTo(3)); Restore(s, r);
        }
    }
}
