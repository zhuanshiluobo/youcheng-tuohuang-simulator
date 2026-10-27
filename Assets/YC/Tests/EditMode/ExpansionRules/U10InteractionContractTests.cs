using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只验证真实请求/路由与领域链路；不加载场景、重建资产或计正常UI/多局。
    internal static class U10ShareTestSupport
    {
        internal static string StartCleanup(PaulContentTestFixture f, string face)
        {
            f.FinishPreparation(); f.State.Round = 3; f.State.Phase = GamePhase.Cleanup; f.State.ActionRound = 1;
            var chain = new RoundMainChainBuilder().Build(f.State.GameId, "", 3, f.State.LaunchPlayerOrder, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            f.State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            f.State.EffectRuntime.ActiveMainNodeId = main.NodeId; f.State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            f.State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            f.State.EffectRuntime.CurrentRoundNumber = 3; f.State.EffectRuntime.CurrentRoundStartPlayerId = 1;
            f.State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            main.ExecutionEffectId = f.Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.Status = EffectNodeStatus.Running; f.Pump();
            f.Artifacts.WriteLabeledText("U10收尾隔离前提", "前序回合/行动按既有夹具隔离完成；从真实Round主链收尾窗和正式单次cleanup_order命令受理，不计正常开局或页面验收。");
            var option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.SourceDefinitionId == "entrepreneur.round.P.3" && o.LocalId == "entrepreneur." + face + ".cleanup.cleanup_order");
            Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode);
            Assert.That(option.CleanupRequest, Is.Null, "保罗分享使用已登记的单次收尾正文。");
            var command = new GameCommand { Kind = GameCommandKind.InvokeSettlementOption, PlayerId = 1,
                CommandId = "qy007.u10.share." + face, Parameters = {
                    ["optionId"] = option.OptionId, ["useId"] = "",
                    ["expectedRevision"] = option.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            var result = new InvokeSettlementOptionCommandHandler(f.Registry).Handle(f.State, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); f.Pump();
            return option.TargetEffectId;
        }

        internal static void AssertOneCondition(PaulContentTestFixture f, string root)
        {
            Assert.That(f.State.EffectRuntime.InteractionRequests.Count(q => f.Within(q.OwnerEffectId, root) && q.InteractionTypeId == "action.decline_effect"), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => f.Within(n.EffectId, root) && n.EffectTypeId == EffectTypeIds.Condition), Is.EqualTo(1));
        }

        internal static void AnswerThroughMap(PaulContentTestFixture f, InteractionRequest request, string item)
        {
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.influence.remove.target"));
            Assert.That(request.AnswerSchema, Is.EqualTo("candidate_id")); Assert.That(request.AllowDecline, Is.False);
            var owner = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            Assert.That(owner.EffectTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.SelectInfluence));
            Assert.That(Flag(owner.NormalizedArguments, "requireCompleteSelection"), Is.True);
            var commands = new List<GameCommand>(); IReadOnlyList<WorkflowHighlight> highlights = null;
            var type = Type.GetType("YC.Presentation.MapEffectInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => f.State), new Func<int>(() => 1), null,
                    new Action<IReadOnlyList<WorkflowHighlight>>(v => highlights = v), new Action(() => { }),
                    new Action<GameCommand>(commands.Add), new Action<string>(_ => { }) });
            type.GetProperty("GetVisibleRequests").SetValue(renderer,
                new Func<IReadOnlyList<InteractionRequest>>(() => f.State.EffectRuntime.InteractionRequests));
            var projection = InteractionRequestProjector.ProjectForPlayer(request, 1);
            Assert.That(renderer.CanRender(projection), Is.True); renderer.Render(projection);
            var slot = f.State.Map.Influences.Single(i => i.InfluenceId == item).SlotId;
            Assert.That(highlights.Any(h => h.TargetId == slot && h.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot), Is.True);
            type.GetMethod("OnInfluenceSlotClicked").Invoke(renderer, new object[] { slot });
            Assert.That(commands.Count, Is.EqualTo(1)); Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { item }));
            renderer.Clear();
            var result = new AnswerInteractionCommandHandler(f.Registry).Handle(f.State, commands.Single());
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); f.Pump();
        }

        internal static void AddDepartureWait(PaulContentTestFixture f)
        {
            const string id = "qy007.u10.departure.wait";
            f.Registry.Register(new EffectRegistration(id, c => c.Node.FlowStage == "waiting"
                ? EffectStepResult.Completed()
                : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = id, AnsweringPlayerId = 2, Visibility = "owner", PromptKey = "qy007.u10.isolated.response",
                    CandidateIds = { "ack" }, AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 }), EffectExecutorKind.IntrinsicFlow));
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration(id, InfluenceEventTypeIds.InfluenceRemoved, "", id,
                _ => new List<EffectSpec> { EffectSpec.Create(id, Object(), 2) }, EffectHandlerRole.Observer));
            f.Artifacts.WriteLabeledText("必要响应隔离控制", "注册通用InfluenceRemoved等待观察者，只用来证明真实移除完成/收益须等响应结清；不是额外卡面效果或正常游戏可达性证明。");
        }
    }

    public sealed class U10InteractionContractTests : MartinContentTestFixture
    {
        [Test] public void ChooseBuildCancelRemainsOnItsOwnRequestWithoutAnotherConfirmation()
        {
            var state = State("entrepreneur.round.M.5", true); ActivateFace(state, "entrepreneur.round.M.5", "M10");
            var card = Blueprint(state, "entrepreneur.facility.M02"); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            Action(state, "M10"); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Answer(state, NormalizedValue.CreateString("continue"));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(Open(state).AllowDecline, Is.True);
            var selection = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == FacilitySelectionEffectExecutor.TypeId);
            Assert.That(selection.AllowDecline, Is.False); Assert.That(Flag(selection.NormalizedArguments, "allowDecline"), Is.True);
            state = Recover(state, "ChooseBuild自身取消待答"); Answer(state, NormalizedValue.CreateBoolean(false));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q => q.InteractionTypeId == "action.decline_effect"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId, Is.EqualTo(BlueprintZone(state)));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 2));
            Recover(state, "ChooseBuild取消保外层已付费用不建设");
        }
    }
}
