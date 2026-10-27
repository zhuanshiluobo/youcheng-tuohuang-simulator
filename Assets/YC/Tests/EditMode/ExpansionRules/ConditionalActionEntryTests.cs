using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
#if !QY007_R9_CLR
    public sealed class ConditionalActionEntryTests
    {
        [NUnit.Framework.TestCase("decline_retry")]
        [NUnit.Framework.TestCase("accept_recover")]
        [NUnit.Framework.TestCase("paid_resume")]
        [NUnit.Framework.TestCase("special_after")]
        [NUnit.Framework.TestCase("stock_0")]
        [NUnit.Framework.TestCase("stock_1")]
        [NUnit.Framework.TestCase("stock_changed")]
        [NUnit.Framework.TestCase("source_after_paid")]
        [NUnit.Framework.TestCase("bare_cost")]
        [NUnit.Framework.TestCase("body_condition")]
        [NUnit.Framework.TestCase("direct_omit")]
        [NUnit.Framework.TestCase("invalid_right")]
        [NUnit.Framework.TestCase("forge_protocol")]
        [NUnit.Framework.TestCase("forge_program")]
        [NUnit.Framework.TestCase("forge_hash")]
        [NUnit.Framework.TestCase("forge_cost")]
        public void GenericLuaEntryConditionPreservesAdmissionAndReceipts(string mode)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                string root = Path.Combine(Directory.GetCurrentDirectory(), "Artifacts", "conditional-action-entry", Guid.NewGuid().ToString("N"));
                artifacts.WriteLabeledText("通用入口条件隔离结果", JsonConvert.SerializeObject(ConditionalActionEntryChecks.Run(root, mode)));
            }
            finally { artifacts.Finish(); }
        }
    }
#endif
    internal static class ConditionalActionEntryChecks
    {
        private const string Definition = "fixture.conditional_card", Ability = "fixture.source", Quick = "fixture.quick", Special = "fixture.special", Track = "fixture.track";
        private static void Need(bool test, string reason) { if (!test) throw new InvalidOperationException(reason); }
        internal static object Run(string root, string mode)
        {
            Fixture f = null;
            try
            {
                f = new Fixture(root, mode);
                if (mode == "invalid_right") f.RejectShape();
                else if (mode.StartsWith("stock_", StringComparison.Ordinal) && mode != "stock_changed") f.Shortage(int.Parse(mode.Substring(6)));
                else if (mode.StartsWith("forge_", StringComparison.Ordinal)) f.Forge(mode);
                else if (mode == "decline_retry") f.DeclineRetry();
                else if (mode == "source_after_paid") f.SourceAfterPaid();
                else if (mode == "stock_changed") f.StockChanged();
                else if (mode == "direct_omit") f.Direct();
                else if (mode == "special_after") f.SpecialAfter();
                else f.Accept(mode);
                return new { success = true, mode, scope = "独立无企业家标识JSON/Lua内容→真实来源/轨道/通用行动入口/Host恢复；隔离规则，非Unity/正常局", f.Pack.ContentHash, f.Trace };
            }
            catch (Exception error) { throw new InvalidOperationException(JsonConvert.SerializeObject(new { success = false, mode, error = error.ToString(), trace = f?.Trace, state = f?.State }), error); }
        }
        private sealed class Fixture
        {
            internal readonly ExternalContentPack Pack;
            internal readonly List<object> Trace = new List<object>();
            internal GameState State;
            private readonly EffectRegistry registry;
            private readonly string card, charges, unused;
            private readonly string mode;
            private EffectTreeExecutor Engine => new EffectTreeExecutor(State, registry);
            private string Gold => State.FindPlayer(1).Resources.GoldVoucher.ToString();
            private AbilityActionInvocation Invocation => State.EffectRuntime.AbilityActionInvocations.Last();
            private int Stock => State.ItemZones.Items.Count(i => i.ZoneId == charges);
            private string MarkerSlot => State.ItemZones.Items.Single(i => i.ItemId == State.EffectRuntime.UsageTrackBindings.Single().MarkerItemId).SlotId;
            private InteractionRequest Open() => State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
            internal Fixture(string root, string mode)
            {
                this.mode = mode; Directory.CreateDirectory(root); WriteContent(root, mode); Pack = ExternalContentPack.Load(root);
                registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
                ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry);
                AbilitySourceLuaCatalog.Register(registry, Pack); CleanupObligation.Register(registry);
                var definitions = new GameContentDefinitions { AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    Components = new List<ComponentContentDefinition> { new ComponentContentDefinition { DefinitionId = Definition, TypeId = "core.award_card", QuantityPolicy = "finite" } } };
                registry.ContentDefinitions = definitions; UsageTracks.Register(registry, definitions.UsageTracks);
                var map = StaticMapDefinitions.CreateThreePlayerMap();
                var seed = new GameState { GameId = "fixture.entry." + mode, MapId = map.MapId, ContentPackHash = Pack.ContentHash, ContentDefinitions = definitions,
                    RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = Pack.ContentHash }, Round = 1, ActionRound = 1,
                    Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1,
                    Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离玩家甲", Resources = new ResourceSet { GoldVoucher = 20 }, RemainingMainActionsThisTurn = 1 }, new PlayerState { PlayerId = 2, Name = "隔离玩家乙" } } };
                State = new ItemZoneStateFactory().CreateForSetup(seed, map, new[] {
                    new ItemRecipeEntry { EntryId = "card", DefinitionId = Definition, TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "rewards" },
                    new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" }
                }, Pack.ActiveDefinitions.Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
                State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
                card = State.ItemZones.Items.Single(i => i.DefinitionId == Definition && i.OwnerSubject.PlayerId == 1).ItemId;
                charges = State.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == "charges").ZoneId;
                unused = State.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == "usage").ZoneId;
                Run(UsageTracks.CreateInitialization(1, card, Track));
                var supply = State.ItemZones.Zones.Single(z => z.LocalZoneId == "supply" && z.OwnerPlayerId == 1);
                foreach (var marker in State.ItemZones.Items.Where(i => i.ZoneId == supply.ZoneId).Take(3).ToArray()) marker.ZoneId = charges;
                Valid(); Trace.Clear();
            }
            private static void WriteContent(string root, string mode)
            {
                var camel = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
                JToken Token(object value) => JToken.Parse(JsonConvert.SerializeObject(value, camel));
                var track = new UsageTrackDeclaration { TrackId = Track, DefinitionId = Definition, CardZoneLocalId = "usage", InitialSlotId = "unused", Slots = new List<string> { "unused", "used" },
                    ActivationTransitions = new List<UsageTrackTransition> { new UsageTrackTransition { FromSlotId = "unused", ToSlotId = "used" } },
                    ResetTransitions = new List<UsageTrackTransition> { new UsageTrackTransition { FromSlotId = "used", ToSlotId = "unused" } } };
                CardZoneDefinition Zone(string local, string template, bool ordered) => new CardZoneDefinition { LocalZoneId = local, ZoneTemplateId = template,
                    AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                    Slots = ordered ? track.Slots : new List<string>(), CapacityPolicy = ordered ? "bounded" : "unbounded", Capacity = ordered ? 1 : -1,
                    OrderingKind = ordered ? ZoneOrderingKind.Track : ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Public,
                    EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                var source = new AbilitySourceDeclaration { DefinitionId = Definition, AbilityId = Ability, Kind = "quick", HandlerRef = "fixture.body", DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", PresentationRef = Definition,
                    EffectiveZones = new List<string> { "core.zone.player_rewards" }, AllowedOperations = new List<string> { "carrier.transfer.original_supply:" + Definition + ":charges:core.player_marker", "cleanup.defer:fixture.due", "action.invoke:fixture.direct" },
                    Actions = new List<AbilityActionDeclaration> {
                        new AbilityActionDeclaration { ActionId = Quick, EntryKind = "quick", EntryCostRef = "fixture.cost", BodyHandlerRef = "fixture.body", UsageTrackId = Track, PresentationRef = Definition, DirectCallPolicy = "fixture.direct" },
                        new AbilityActionDeclaration { ActionId = Special, EntryKind = "special", BodyHandlerRef = "fixture.body", EntryAfterEffectsRef = "fixture.after", UsageTrackId = Track, PresentationRef = Definition },
                        new AbilityActionDeclaration { ActionId = "fixture.caller", EntryKind = "quick", BodyHandlerRef = "fixture.caller.body", PresentationRef = Definition }
                    } };
                JObject Handler(string id, string file, string completed = "") => new JObject { ["handlerId"] = id, ["script"] = file, ["version"] = "1.0.0", ["completionHandlerId"] = completed };
                string caller = @"return function(ctx)
 local c=ctx.payload.sourceItemId
 local t=SourceData.GetTransferableTokens({carrierItemId=c,localZoneId='charges',typeId='core.player_marker'}).items[1]
 return {Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds={t.itemId},expectedSource={zoneId=t.zoneId},destination={kind='original_supply'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1}),
 Effect.InvokeAction({apiVersion=1,player=ctx.playerId,itemId=c,actionId='fixture.quick',callPolicyRef='fixture.direct'})} end";
                var definition = new JObject { ["definitionId"] = Definition, ["contentType"] = "component", ["version"] = "1.0.0", ["displayName"] = "独立检验卡", ["expansionId"] = "core", ["itemTypeId"] = "core.operator_card", ["artwork"] = "", ["abilities"] = new JArray(),
                    ["data"] = new JObject { ["quantityPolicy"] = "finite", ["instanceTypeId"] = "core.operator_card" }, ["cardZones"] = Token(new[] { Zone("charges", "core.zone.card_cube_storage", false), Zone("usage", "core.zone.board_marker_track", true) }),
                    ["usageTracks"] = Token(new[] { track }), ["abilitySources"] = Token(new[] { source }), ["abilityHandlers"] = new JArray(Handler("fixture.body", "body.lua", "fixture.body.completed"), Handler("fixture.cost", "cost.lua"), Handler("fixture.after", "after.lua"), Handler("fixture.due", "due.lua"), Handler("fixture.caller.body", "caller.lua")),
                    ["actionCallPolicies"] = Token(new[] { new AbilityActionCallPolicy { PolicyRef = "fixture.direct", TargetActionId = Quick, CallerAbilityId = Ability, CallerHandlerRef = "fixture.caller.body", CallerHandlerHash = LuaContentHasher.ComputeSha256(caller), OmitEntryCost = true, OmitEntryAfterEffects = true, OmitMainAction = true } }) };
                string body = @"return function(ctx)
 if ctx.eventType=='EffectCompleted' then
  if ctx.payload.outcome~='completed' or ctx.payload.continuationContext==nil then return {} end
  return {Effect.GainResource({player=ctx.playerId,resourceType='gold_voucher',amount=5})}
 end
 return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'go'},minSelections=1,maxSelections=1}),{stage='body'}),
  Effect.ScheduleCleanupObligation({apiVersion=1,player=ctx.playerId,handlerRef='fixture.due',dueRound=GameData.GetRound(),context={settlementPresentation={label=Text.Ref('fixture:due'),description=Text.Ref('fixture:due'),ariaLabel=Text.Ref('fixture:due')}}})}
end";
                if (mode == "body_condition") body = "return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end return {Effect.Condition({decisionPlayer=ctx.playerId,leftEffects={Effect.PayResource({player=ctx.playerId,resourceType='gold_voucher',amount=2})},rightEffects={Effect.GainResource({player=ctx.playerId,resourceType='gold_voucher',amount=7})}})} end";
                string payment = @"local c=ctx.payload.sourceItemId; local z=Global.Content.FindZones({parentId=c,localZoneId='charges'})[1].id
 local page=SourceData.GetTransferableTokens({carrierItemId=c,localZoneId='charges',typeId='core.player_marker'})
 local ids={}; for _,v in ipairs(page.items) do ids[#ids+1]=v.itemId end
 table.sort(ids); local chosen={}; local take=2; if #ids<take then take=#ids end
 for i=1,take do chosen[i]=ids[i] end; ids=chosen
 local pay=Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId=z},destination={kind='original_supply'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=2})";
                string cost = "return function(ctx) " + payment + (mode == "bare_cost" ? " return {pay} end" :
                    " return {Effect.WithPresentation(Effect.Condition({decisionPlayer=ctx.playerId,leftEffects={pay},rightEffects={" + (mode == "invalid_right" ? "Effect.GainScore({player=ctx.playerId,amount=99})" : "") + "}}),{interaction={prompt=Text.Ref('fixture:gate',{label='独立动作'})}})} end");
                var manifest = new JObject { ["schemaVersion"] = 3, ["packId"] = "fixture", ["version"] = "1.0.0", ["definitions"] = new JArray("source.json"), ["requiredCapabilities"] = new JArray(), ["setupRecipes"] = new JArray(), ["presentation"] = new JObject { ["schemaVersion"] = 1, ["texts"] = new JArray("texts.json"), ["icons"] = "icons.json", ["defaults"] = "defaults.json" } };
                definition["replaces"] = JValue.CreateNull();
                definition["itemTypeId"] = "core.award_card"; definition["data"]["instanceTypeId"] = "core.award_card";
                ((JObject)((JArray)definition["cardZones"])[0]).Remove("capacity");
                // 仅供无界面内容装载校验；不导入/展示，不构成该组件图的其他UI用途。
                definition["artwork"] = "fixture.png";
                File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/artwork/components/infected.component.gavial_token.png"), Path.Combine(root, "fixture.png"));
                manifest["enabledExpansionIds"] = new JArray("core"); manifest["expansionDependencies"] = new JArray();
                void Write(string name, string value) { File.WriteAllText(Path.Combine(root, name), value); }
                Write("source.json", definition.ToString()); Write("pack.json", manifest.ToString()); Write("cost.lua", cost); Write("body.lua", body); Write("caller.lua", caller);
                Write("after.lua", "return function(ctx) return {Effect.GainResource({player=ctx.playerId,resourceType='gold_voucher',amount=10})} end");
                Write("due.lua", "return function(ctx) return {Effect.GainScore({player=ctx.playerId,amount=1})} end");
                Write("texts.json", new JObject { ["schemaVersion"] = 1, ["locale"] = "zh-CN", ["messages"] = new JObject { ["fixture:gate"] = new JObject { ["parameters"] = new JObject { ["label"] = "text" }, ["template"] = "支付2个标记：{label}" }, ["fixture:due"] = new JObject { ["parameters"] = new JObject(), ["template"] = "检验后继义务" } } }.ToString());
                Write("icons.json", "{\"schemaVersion\":1,\"icons\":[]}"); Write("defaults.json", "{\"schemaVersion\":1,\"effectTypes\":{},\"interactions\":{},\"outcomes\":{}}");
            }
            private void Valid() => Need(new ItemZoneQuery().TryValidate(State, out string why), why);
            private string Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return id; }
            private void Pump() { Need(!Engine.RunUntilQuiescent().Faulted, State.EffectRuntime.LastFaultMessage); Valid(); }
            private AbilityActionOption Option(string action = Quick) => registry.QueryActions(State, 1).Single(o => o.ActionId == action);
            private AbilityActionOption Start(string command = "new", string action = Quick)
            {
                var option = Option(action); Need(option.Enabled, option.DisabledReason);
                Need(Engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, command, Object(), out _, out string why), why);
                return option;
            }
            private void Answer(NormalizedValue value) { var r = Open(); Need(r != null, "missing_request"); Need(Engine.TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, value, out string why), why); Pump(); }
            private void Recover(string stage)
            {
                var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), registry);
                Need(result.Succeeded, result.Diagnostic); State = result.State; Trace.Add(new { stage, stock = Stock, slot = MarkerSlot, invocation = State.EffectRuntime.AbilityActionInvocations.LastOrDefault()?.Phase });
            }
            private void Until(Func<bool> predicate) { for (int i = 0; i < 400 && !predicate(); i++) Need(Engine.Advance(), "no_progress"); Need(predicate(), "step_budget"); Valid(); }
            internal void RejectShape() { Need(!Option().Enabled && Option().DisabledReason == "entry_condition_requires_one_cost_and_separate_body", Option().DisabledReason); Need(Stock == 3 && MarkerSlot == "unused", "shape_preview_mutated"); }
            internal void Shortage(int count) { foreach (var item in State.ItemZones.Items.Where(i => i.ZoneId == charges).Skip(count)) item.ZoneId = item.OriginalSupplyZoneId; Valid(); Need(!Option().Enabled && Stock == count && MarkerSlot == "unused", "shortage_enabled_or_mutated"); }
            internal void DeclineRetry()
            {
                string before = JsonConvert.SerializeObject(State); for (int i = 0; i < 3; i++) Need(Option().Enabled, "query_disabled"); Need(JsonConvert.SerializeObject(State) == before, "preview_mutated_state");
                var option = Start(); Pump(); Need(Stock == 3 && MarkerSlot == "unused" && State.EffectRuntime.CleanupObligations.Count == 0 && !Invocation.ActivationCommitted, "confirmation_already_activated");
                Recover("条件待答"); Answer(NormalizedValue.CreateBoolean(false));
                Need(Stock == 3 && MarkerSlot == "unused" && !Invocation.ActivationCommitted && State.EffectRuntime.CleanupObligations.Count == 0, "decline_side_effect");
                Need(Engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "new", Object(), out _, out _), "duplicate_command_not_idempotent"); Pump();
                Start("retry"); Pump(); Need(State.EffectRuntime.AbilityActionInvocations.Count == 2 && Open() != null && Stock == 3, "fresh_retry_failed"); Recover("放弃后新命令再试");
            }
            internal void Accept(string kind)
            {
                var option = Start();
                if (kind != "bare_cost")
                {
                    Pump(); Need(MarkerSlot == "unused", "gate_used_track"); Recover("确认前");
                    if (kind == "paid_resume")
                    {
                        var r = Open(); Need(Engine.TrySubmitInteraction(r.GetStableInteractionId(), 1, r.StateRevision, NormalizedValue.CreateString("continue"), out string why), why);
                        Until(() => State.EffectRuntime.EffectNodes.Any(n => AbilityActionExecutor.HasCommittedTokenTransfer(State, n)));
                        Need(MarkerSlot == "unused" && !Invocation.ActivationCommitted && Stock == 1, "paid_gate_activated_early"); Recover("已付未激活恢复"); Pump();
                    }
                    else Answer(NormalizedValue.CreateString("continue"));
                }
                else Pump();
                Need(Stock == 1 && MarkerSlot == "used" && Invocation.ActivationCommitted && Invocation.PaymentCommitted, "payment_activation_order");
                Need(Invocation.ProtocolVersion == (kind == "bare_cost" ? 1 : AbilityActionEntryCondition.InvocationProtocolVersion), "receipt_protocol_wrong");
                if (kind == "body_condition")
                {
                    Answer(NormalizedValue.CreateBoolean(false)); Need(MarkerSlot == "used" && Stock == 1 && Gold == "20" && Invocation.Outcome == "completed", "body_decline_unactivated_or_paid_twice"); Recover("正文内条件放弃仍已用"); return;
                }
                Need(Open() != null && Open().CandidateIds.SequenceEqual(new[] { "go" }) && State.EffectRuntime.CleanupObligations.Count == 0, "body_or_due_executed_early");
                Recover("正文及Lua续接待答"); Answer(NormalizedValue.CreateString("go"));
                Need(Gold == "25" && State.EffectRuntime.CleanupObligations.Count == 1 && Invocation.Outcome == "completed", "body_not_once_or_due_missing");
                var due = State.EffectRuntime.CleanupObligations.Single(); Need(due.OriginKind == "action" && due.OriginId == Invocation.InvocationId, "due_origin_changed");
                Recover("正文和义务完成");
                Need(Engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "new", Object(), out _, out _), "duplicate_rejected"); Pump();
                Need(Gold == "25" && Stock == 1 && State.EffectRuntime.CleanupObligations.Count == 1 && State.EffectRuntime.UsageGroupReceipts.Count == 1, "duplicate_side_effect");
            }
            internal void StockChanged()
            {
                Start(); Pump(); foreach (var item in State.ItemZones.Items.Where(i => i.ZoneId == charges).ToArray()) item.ZoneId = item.OriginalSupplyZoneId;
                Answer(NormalizedValue.CreateString("continue")); Need(!Invocation.ActivationCommitted && MarkerSlot == "unused" && Gold == "20" && Stock == 0, "changed_stock_partially_paid_or_activated"); Recover("库存变化原子拒绝");
            }
            internal void SourceAfterPaid()
            {
                Start(); Pump(); var r = Open(); Need(Engine.TrySubmitInteraction(r.GetStableInteractionId(), 1, r.StateRevision, NormalizedValue.CreateString("continue"), out string why), why);
                Until(() => State.EffectRuntime.EffectNodes.Any(n => AbilityActionExecutor.HasCommittedTokenTransfer(State, n)));
                Need(MarkerSlot == "unused" && !Invocation.ActivationCommitted && Stock == 1, "activation_before_left_settled"); Recover("已付未激活");
                State.ItemZones.Items.Single(i => i.ItemId == card).ZoneId = State.ItemZones.Zones.Single(z => z.LocalZoneId == "reserve").ZoneId;
                var report = Engine.RunUntilQuiescent(); Need(report.Faulted && !Invocation.ActivationCommitted && Stock == 1 && MarkerSlot == "unused" && State.EffectRuntime.CleanupObligations.Count == 0, "paid_source_change_was_rolled_back_or_used");
                Recover("已付来源变化真实故障");
            }
            internal void Direct()
            {
                Start("caller", "fixture.caller"); Pump(); Need(Stock == 2 && MarkerSlot == "used" && Open()?.CandidateIds.Contains("go") == true, "direct_did_not_omit_gate");
                Answer(NormalizedValue.CreateString("go")); Need(Gold == "25" && Stock == 2 && State.EffectRuntime.UsageGroupReceipts.Count == 1, "direct_cost_or_after_leaked"); Recover("调用方付1标记，direct共轨且跳快速入口条件");
            }
            internal void SpecialAfter()
            {
                Start("special", Special); Pump(); Need(Stock == 3 && MarkerSlot == "used" && Open()?.CandidateIds.Contains("go") == true, "special_quick_gate_leaked");
                Answer(NormalizedValue.CreateString("go")); Need(Gold == "35" && Stock == 3 && Invocation.MainActionCommitted && State.EffectRuntime.UsageGroupReceipts.Count == 1, "special_after_or_budget_wrong"); Recover("special原正文后10券");
            }
            internal void Forge(string what)
            {
                Start(); Pump(); var archive = HostRecoveryService.CreateArchive(State); var saved = archive.Snapshot.State;
                var invocation = saved.EffectRuntime.AbilityActionInvocations.Single(); var gate = saved.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == EffectTypeIds.Condition);
                if (what == "forge_protocol") { invocation.ProtocolVersion = 1; invocation.EntryConditionVersion = invocation.EntryConditionHandlerHash = invocation.EntryConditionHandlerVersion = invocation.EntryConditionProgramDigest = ""; }
                if (what == "forge_program") gate.NestedGroupSizes[1] = 1;
                if (what == "forge_hash") invocation.EntryConditionHandlerHash = "wrong";
                if (what == "forge_cost") gate.NestedEffects[0].NormalizedArguments.Properties.Single(p => p.Name == "requiredCount").Value = NormalizedValue.CreateInteger(1);
                Need(!HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry).Succeeded, "forged_gate_restored");
            }
        }
    }
}
