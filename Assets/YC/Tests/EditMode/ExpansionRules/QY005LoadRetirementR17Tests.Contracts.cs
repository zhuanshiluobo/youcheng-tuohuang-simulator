using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.Maps;
using YC.Domain.Cards;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class QY005LoadRetirementR17Tests
    {
        [Test] public void CurrentPackLoadsWithExactSharedModuleIdentityAndNoLoadApi()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            string version = (string)pack.Manifest["version"];
            Require(version == "2.1.1", version);
            Require(!MoonSharpLuaRuntimeHost.SupportedEffectConstructors.Contains("LoadCardMarkers"), "旧公开构造器仍存在");
            var r = new EffectRegistry(); GenericLuaEffectExecutor.Register(r);
            Require(!r.TryGet(ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId, out _), "旧Load仍可执行");
            LastEvidence = JsonConvert.SerializeObject(new { version, pack.ContentHash, constructors = MoonSharpLuaRuntimeHost.SupportedEffectConstructors });
        }
        [Test] public void OldRawLoadAndPrivateDeliveryStateAreRejectedBeforeInstallation()
        {
            Setup("empty");
            string clean = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var control = HostRecoveryService.RecoverJson(clean, registry);
            Require(control.Succeeded, "合法新档对照未过：" + control.Diagnostic);
            var results = new List<object>();
            foreach (string mode in new[] { "load.v1", "load.v2", "load.v3.completed", "nested", "receipt", "payment_binding", "requirement", "delivery_ref", "orphan_staging" })
            {
                var raw = JObject.Parse(clean); var target = (JObject)raw["Snapshot"]["State"];
                var node = JObject.FromObject(new EffectNodeRuntimeState { EffectId = "r17.legacy", EffectTypeId = "effect.score.gain", PlayerId = 1 });
                if (mode.StartsWith("load")) { node["EffectTypeId"] = ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId; node["DefinitionVersion"] = mode == "load.v1" ? "v1" : mode == "load.v2" ? "2.0.0" : "3.0.0"; if (mode.EndsWith("completed")) node["Status"] = (int)EffectNodeStatus.Completed; }
                else if (mode == "nested") ((JArray)node["NestedEffects"]).Add(JObject.FromObject(new EffectSpecRuntimeState { EffectTypeId = ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId }));
                else if (mode == "receipt") node["FixedDelivery"] = new JObject { ["Paid"] = true, ["Delivered"] = false };
                else if (mode == "payment_binding") { node["Payment"] = JObject.FromObject(new ResourcePaymentRuntimeState()); node["Payment"]["FixedDeliveryReceiptId"] = "old.binding"; }
                else if (mode == "requirement" || mode == "delivery_ref") ((JArray)node["NormalizedArguments"]["Properties"]).Add(JObject.FromObject(new NormalizedValueEntry { Name = mode == "requirement" ? "fixedDeliveryRequirement" : "deliveryRef", Value = NormalizedValue.CreateString("old") }));
                else ((JArray)target["ItemZones"]["Zones"]).Add(new JObject { ["LocalZoneId"] = "fixed_delivery" });
                ((JArray)target["EffectRuntime"]["EffectNodes"]).Add(node);
                var rejected = HostRecoveryService.RecoverJson(raw.ToString(), registry);
                string expected = mode.StartsWith("load") || mode == "nested" ? ContentPrimitiveEffectExecutor.RetiredMarkerLoadReason : ContentPrimitiveEffectExecutor.RetiredFixedDeliveryReason;
                results.Add(new { mode, rejected.Succeeded, rejected.Diagnostic });
                Require(!rejected.Succeeded && rejected.Diagnostic == expected, JsonConvert.SerializeObject(results));
            }
            LastEvidence = JsonConvert.SerializeObject(new { control = control.Succeeded, results });
        }
        [Test] public void CompletedAndNestedTypedLoadContractsCannotBypassRecoveryValidation()
        {
            Setup("empty");
            foreach (bool nested in new[] { false, true })
            {
                var work = GameStateCloneService.DeepClone(state);
                var node = new EffectNodeRuntimeState { EffectId = "r17.legacy.typed", EffectTypeId = nested ? "effect.flow.condition" : ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId, Status = EffectNodeStatus.Completed };
                if (nested) node.NestedEffects.Add(new EffectSpecRuntimeState { EffectTypeId = ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId });
                work.EffectRuntime.EffectNodes.Add(node);
                Require(!ContentPrimitiveEffectExecutor.TryValidateContentContinuations(work, out string reason) && reason == ContentPrimitiveEffectExecutor.RetiredMarkerLoadReason, reason);
            }
            LastEvidence = "终态及嵌套旧Load均由typed门禁明确拒绝";
        }
        [Test] public void ResourceAmountChoiceStillUsesTheExistingQuantityInteraction()
        {
            Setup("empty"); state.FindPlayer(2).Resources.Iron = 3; // 第一条Journal之前的隔离输入。
            string root = Engine.CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"),
                "amountRange", Object("minAmount", NormalizedValue.CreateInteger(0), "maxAmount", NormalizedValue.CreateInteger(4),
                    "sourcePlayer", NormalizedValue.CreateString("2"), "resourceType", NormalizedValue.CreateString("iron"))), 1));
            Require(!Engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Require(request.InteractionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId && Number(request.PromptParameters, "maxAmount") == 3, "资源数量路径或实时边界丢失");
            Require(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("2"), out string error), error);
            Require(!Engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
            Require(Engine.GetNode(root).Status == EffectNodeStatus.Completed, "资源数量未完成");
            LastEvidence = JsonConvert.SerializeObject(Engine.GetNode(root).NormalizedResult);
        }
        [Test] public void FormalTiagoMultipleReputationMovesOpenOnePointChoice()
        {
            // 原生共享SetUpFixture已从完整资产目录初始化；本局筛选的Content只用于下方独立Host，不覆盖进程目录。
            Require(CharacterCardDatabase.IsInitialized && YC.Domain.Facilities.FacilityCardDatabase.IsInitialized &&
                EventCardDatabase.IsInitialized && YC.Domain.CityStyles.CityStyleDatabase.IsInitialized &&
                YC.Domain.SpecialActions.SpecialActionDatabase.IsInitialized,
                "此用例须先由EditMode共享SetUpFixture初始化完整目录。");
            using var f = new TiagoInvestmentTestFixture();
            f.CreateSelectedHost(new[] { "D03" });
            f.OpenSelectedWindow(true); f.StartInvestment(); f.ToPointChoice(reputation: 2);
            var moves = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == TypeId && Text(Value(n.NormalizedArguments, "destination"), "zoneId") == f.State.ItemZones.Zones.Single(z => z.ParentId == f.Carrier && z.LocalZoneId == "reputation").ZoneId).ToList();
            Require(moves.Count == 2 && moves.All(n => Number(n.NormalizedArguments, "requiredCount") == 1 && n.Status == EffectNodeStatus.Completed), "声望未逐枚真实提交");
            var open = f.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.AnsweringPlayerId == 1).ToList();
            Require(open.Count == 1 && Text(f.Engine.GetNode(open[0].OwnerEffectId).ContinuationContext, "stage") == "main_selected", "骰点页不是一次");
            int dice = f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice);
            f.Restore("R17多枚声望后一次骰点页"); f.Drain();
            Require(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice) == dice, "恢复重复掷骰");
            LastEvidence = JsonConvert.SerializeObject(new { stored = moves.Count, singlePointRequest = open[0].GetStableInteractionId(), dice, contentHash = f.Pack.ContentHash });
        }
        [Test] public void FormalDorothyZeroAndNoInventoryKeepTheNormalFinishLifecycle()
        {
            var results = new List<object>();
            foreach (int stock in new[] { 0, 2 })
            {
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"))
                    .CreateActiveSnapshot(new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2,
                        RuleModeId = "entrepreneur", EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
                registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
                ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
                CharacterCardLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
                var map = StaticMapDefinitions.CreateThreePlayerMap();
                var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "card", DefinitionId = "operator.dorothy", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } };
                if (stock > 0) recipe.Add(new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = stock, PerPlayer = true, InitialZoneRole = "supply" });
                var seed = new GameState { GameId = "r17.dorothy.zero." + stock, MapId = map.MapId, ContentPackHash = pack.ContentHash,
                    RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash }, LaunchPlayerOrder = new List<int> { 1, 2 },
                    Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } },
                    ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                        AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) } };
                state = new ItemZoneStateFactory().CreateForSetup(seed, map, recipe, new[] { new ItemCardZoneDeclaration { DefinitionId = "operator.dorothy", CardZones = pack.Definitions.Single(d => d.DefinitionId == "operator.dorothy").CardZones } });
                state.Round = state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = state.StartPlayerId = 1; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
                card = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1).ItemId;
                void Drain() => Require(!Engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
                void Answer(string value) { var r = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open"); Require(Engine.TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, NormalizedValue.CreateString(value), out string reason), reason); Drain(); }
                Engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Drain(); Answer(card);
                string use = Engine.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Drain();
                if (stock > 0) Answer("0");
                Require(!state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), "零枚或无库存产生空页");
                Require(Engine.GetNode(use).Status == EffectNodeStatus.Completed && state.CharacterUses.Single().Finished, "角色使用未按原生命周期结束");
                // 正式多萝西的emptyExitPolicyId为空；零枚结束仍保留持续性订阅，不能套用“空载弃牌”的其它载体规则。
                Require(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId == FindZone(state, "persistent", 1).ZoneId, "零枚结束未保留正式持续性角色");
                results.Add(new { stock, useCompleted = true, remainsPersistent = true, openRequests = 0 });
            }
            LastEvidence = JsonConvert.SerializeObject(results);
        }
    }
}
