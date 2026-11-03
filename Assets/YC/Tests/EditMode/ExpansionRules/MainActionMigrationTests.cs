using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Application.Sessions;
using Newtonsoft.Json.Linq;
using YC.Domain.Items;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // API-18 / 4.7.2：正式正文和真实子效果的隔离消费者，不计正常游戏或页面验收。
    public sealed class MainActionMigrationTests : FacilityLayerTestFixture
    {
        protected override bool UseInMemoryContent => true;
        private List<EffectSpec> Formal(string path, GameState state)
        {
            string source = Pack.GetScript(path);
            var script = new LuaScriptDefinition(Pack.PackId, "api18.formal", "api18.formal", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var facade = LuaFacadeSnapshot.FromInvocation("p1", 0, 200, 2, "1.0.0");
            facade.Round = state.Round; facade.ActionRound = state.ActionRound;
            var result = new MoonSharpLuaRuntimeHost().Invoke(script,
                new LuaInvocationContext("api18.formal", "isolated", "p1", 0, "1.0.0", 2, 0, 200, facade));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: Pack).TryCompile(result,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = path }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            artifacts.WriteLabeledText("正式主行动正文身份", path + "\n" + script.ContentHash);
            return specs;
        }
        private string Body(GameState state, List<EffectSpec> specs)
        {
            // 末尾的加分仅为顺序探针，不写入正式 Lua 或内容定义。
            specs.Add(ResourceEffectSpecFactory.GainScore(1, 1, "api18.isolated.order_probe"));
            Registry.Register(new EffectRegistration("api18.isolated.sequence", c => {
                if (c.Node.FlowStage == "") return EffectStepResult.Continue("waiting").AddChildren(specs);
                if (c.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("api18.child_failed");
                if (c.ChildNodes.Any(n => n.Status != EffectNodeStatus.Completed)) return EffectStepResult.NoProgress();
                return EffectStepResult.Completed();
            }, EffectExecutorKind.IntrinsicFlow));
            return new EffectTreeExecutor(state, Registry).CreateRoot(EffectSpec.Create("api18.isolated.sequence", playerId: 1));
        }
        [TestCase("build")][TestCase("deploy")][TestCase("dispatch")][TestCase("special")]
        public void EmptyImmediateActionReportsNoTargetInsteadOfSuccessfulExecution(string action)
        {
            var state = State();
            if (action == "deploy")
            {
                // 单独构造供应确实为零的隔离初始状态，不把仍有正式候选的页面当作无目标。
                var source = new GameState { GameId = state.GameId + ".empty_supply", MapId = state.MapId, ContentPackHash = state.ContentPackHash,
                    ContentDefinitions = state.ContentDefinitions, RuleConfiguration = state.RuleConfiguration, Players = GameStateCloneService.DeepClone(state.Players),
                    Phase = state.Phase, Round = state.Round, ActionRound = state.ActionRound, CurrentPlayerId = 1, StartPlayerId = 1 };
                source.RandomRuntime = GameplayRandomRuntime.Create(source.GameId);
                state = new ItemZoneStateFactory().CreateForSetup(source, Map, Array.Empty<ItemRecipeEntry>());
                state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            }
            var engine = new EffectTreeExecutor(state, Registry);
            string root = engine.CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.ExecuteMainAction,
                Object("player", NormalizedValue.CreateString("1"), "executionMode", NormalizedValue.CreateString("immediate"),
                    "allowedActionTypes", Strings(new[] { action })), 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False); Answer(state, action);
            var node = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(node.NormalizedResult, "outcome"), Is.EqualTo("no_legal_target"));
            Assert.That(Value(node.NormalizedResult, "executed").BooleanValue, Is.False);
            Assert.That(Text(node.NormalizedResult, "actionType"), Is.EqualTo(action));
            Assert.That(node.ChildEffectIds, Does.Contain(Text(node.NormalizedResult, "actionEffectId")));
            Recover(state, "即时行动无目标_" + action);
        }
        [Test]
        public void EarlierImmediateExecutorCannotResumeUnderTheNewResultContract()
        {
            var state = State(); var engine = new EffectTreeExecutor(state, Registry);
            string root = engine.CreateRoot(EffectSpec.Create(LuaDomainEffectTypeIds.ExecuteMainAction,
                Object("player", NormalizedValue.CreateString("1"), "executionMode", NormalizedValue.CreateString("immediate"), "allowedActionTypes", Strings(new[] { "build" })), 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var node = raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == root);
            node["DefinitionVersion"] = "1.0.0"; string before = raw.ToString();
            var result = HostRecoveryService.RecoverJson(before, Registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain("immediate_main_action_version_unsupported")); Assert.That(raw.ToString(), Is.EqualTo(before));
            artifacts.WriteLabeledText("旧即时执行版本明确拒绝", result.Diagnostic);
        }
        [TestCase(false)][TestCase(true)]
        public void FormalGrantsCompleteBeforeRewardKeepFalseLockAndRecoverOnce(bool restricted)
        {
            var state = State(); state.FindPlayer(1).RemainingMainActionsThisTurn = 1;
            CityStyleSpecialActionEffectExecutor.RegisterPrimitives(Registry);
            string path = restricted ? "lua/city_styles/city_style_pioneer_command_hub.special.lua" : "lua/city_styles/source_stone_industrial_hub.lua";
            string root = Body(state, Formal(path, state)); var engine = new EffectTreeExecutor(state, Registry);
            for (int i = 0; i < 80 && !state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions && n.Status == EffectNodeStatus.Completed); i++)
                Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            var grant = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions);
            Assert.That(grant.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(grant.NormalizedResult.IntegerValue, Is.EqualTo(restricted ? 1 : 2));
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            state = Recover(state, "正式额度已发后续未执行");
            Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Completed, Is.True);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(restricted ? 2 : 3));
            Assert.That(state.FindPlayer(1).RestrictedMainActionsThisTurn, Is.EqualTo(restricted ? 1 : 0));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.False); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.CharacterActionRoundPermissions.Count, Is.EqualTo(restricted ? 1 : 0));
            if (restricted) Assert.That(state.CharacterActionRoundPermissions.Single().ExpiresActionRoundSequence, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            state = Recover(state, "正式额度正文全部完成"); Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Completed, Is.True);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(restricted ? 2 : 3)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            new YC.Domain.Rules.MainActionBudgetService().EndActionTurn(state, 1);
            new YC.Domain.Rules.MainActionBudgetService().ResetForNewActionTurn(state, 1);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).RestrictedMainActionsThisTurn, Is.Zero);
        }
        [Test]
        public void FormalCommercialRewardPrecedesImmediatePaidBuildAndLaterRewardWaitsForPlacement()
        {
            var state = State(); state.FindPlayer(1).RemainingMainActionsThisTurn = 1;
            string definition = Definition(state, f => !f.HasEntryEffect && !f.ReserveOnly && f.GoldVoucherCost > 0 && f.Color != "rainbow"); string item = Card(state, definition).ItemId;
            var supply = state.ItemZones.Zones.First(z => z.TemplateId == FacilityLayerQuery.SupplyTemplate);
            Transfer(state, Leg(state, item, supply.ZoneId, "top"));
            int price = FacilityCardDatabase.Get(state, definition).GoldVoucherCost, gold = state.FindPlayer(1).Resources.GoldVoucher;
            string root = Body(state, Formal("lua/city_styles/city_style_core_commercial_area.special.lua", state));
            Assert.That(new EffectTreeExecutor(state, Registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "GoldVoucher", "Originium", "Iron", "OriginiumShard" })); Answer(state, "GoldVoucher");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "build", "explore", "move_city" }));
            state = Recover(state, "商业区资源已得即时行动待选"); Answer(state, "build");
            var selection = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build);
            Assert.That(Number(selection.NormalizedArguments, "apiVersion"), Is.EqualTo(3)); Assert.That(Value(selection.NormalizedArguments, "consumeMainAction").BooleanValue, Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); var q = Open(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TryPrepareEffectBuildPayment(1, selection.EffectId, q.GetStableInteractionId(), q.CandidateSetId,
                q.CandidateSetVersion, item, Object("paymentMode", NormalizedValue.CreateString("gold"), "expectedSourceZoneId", NormalizedValue.CreateString(supply.ZoneId)),
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string reason), Is.True, reason);
            Answer(state, "pay"); state = Recover(state, "即时建设已付待放置");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4 - price)); Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Answer(state, "0"); state = Recover(state, "商业区即时建设及后续完成");
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(item));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(FacilityCardDatabase.Get(state, definition).Score + 1));
            var probe = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ResourceEffectTypeIds.GainScore);
            Assert.That(probe.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Number(probe.NormalizedResult, "amount"), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + 4 - price));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredBuildSelectionTypeId), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.Payment?.Preparation?.IsPromoted == true), Is.EqualTo(1));
            var action = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction);
            Assert.That(Text(action.NormalizedResult, "outcome"), Is.EqualTo("completed")); Assert.That(Value(action.NormalizedResult, "executed").BooleanValue, Is.True);
        }
    }
}
