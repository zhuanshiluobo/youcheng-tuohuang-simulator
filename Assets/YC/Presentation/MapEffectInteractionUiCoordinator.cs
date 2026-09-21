using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>通用地图交互；入场、主要行动、角色和设施共用候选高亮与回答入口。</summary>
    internal sealed class MapEffectInteractionUiCoordinator : InteractionBase, IInteractionRequestRenderer
    {
        private readonly Func<GameState> state;
        private readonly Func<int> player;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> highlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> prompt;
        private string inFlight = string.Empty;
        private string rendered = string.Empty;
        private int revision = -1;

        public MapEffectInteractionUiCoordinator(Func<GameState> state, Func<int> player,
            CharacterCardEffectChoiceDialog dialog, Action<IReadOnlyList<WorkflowHighlight>> highlights,
            Action clearHighlights, Action<GameCommand> submit, Action<string> prompt)
        {
            this.state = state; this.player = player; this.dialog = dialog;
            this.highlights = highlights; this.clearHighlights = clearHighlights;
            this.submit = submit; this.prompt = prompt;
        }

        public override string Id => "effect.map.interaction";
        public override InteractionPriority Priority => InteractionPriority.PendingResolution;
        int IInteractionRequestRenderer.Priority => 320;
        public override bool IsActive => Current() != null;
        public static bool Supports(string type) => type == "effect.influence.place.target" || type == "effect.influence.remove.target" || type == PlayerEntranceEffectExecutor.InteractionTypeId || type == CityMoveEffectExecutor.TargetInteractionTypeId || type == "action.decline_effect";
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer &&
            request.AnsweringPlayerId == player() && request.Status == "open" && (Supports(request.InteractionTypeId) ||
                (request.InteractionTypeId == "lua.choice" && request.PromptKey == "effect.influence.replace.target"));

        private InteractionRequestProjection Current()
        {
            var runtime = state()?.EffectRuntime;
            if (runtime == null) return null;
            foreach (var request in runtime.InteractionRequests)
            {
                var projected = InteractionRequestProjector.ProjectForPlayer(request, player());
                if (CanRender(projected)) return projected;
            }
            return null;
        }

        private string Slot(InteractionRequestProjection request, string candidate)
        {
            if (request.InteractionTypeId == "effect.influence.place.target") return candidate;
            var influence = state()?.Map?.Influences.Find(i => i.InfluenceId == candidate);
            return influence == null ? string.Empty : influence.SlotId;
        }

        private List<WorkflowHighlight> Targets(InteractionRequestProjection request)
        {
            var result = new List<WorkflowHighlight>();
            foreach (var candidate in request.CandidateIds)
            {
                if (request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId || request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId)
                {
                    result.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.Location, candidate, request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId ? WorkflowHighlightSemantic.MoveTarget : WorkflowHighlightSemantic.InitialPlacement));
                    continue;
                }
                var slot = Slot(request, candidate);
                if (!string.IsNullOrEmpty(slot)) result.Add(new WorkflowHighlight(
                    WorkflowHighlightTargetKind.InfluenceSlot, slot,
                    request.InteractionTypeId == "effect.influence.place.target"
                        ? WorkflowHighlightSemantic.DeployTarget : WorkflowHighlightSemantic.EventInfluenceTarget));
            }
            return result;
        }

        private static string Prompt(InteractionRequestProjection request) =>
            request.PromptKey == "effect.influence.replace.target" ? "请选择高亮的对手影响力进行替换。" :
            request.InteractionTypeId == "action.decline_effect" ? OptionalPrompt(request) :
            request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId ? "请选择高亮的城市移动目标。" :
            request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId ? "请选择高亮的入场地点。入场事件结算后轮到下一位玩家。" :
            (request.InteractionTypeId == "effect.influence.place.target" ? "请选择高亮位置放置影响力。" : "请选择高亮的影响力移除。") +
            (request.AllowDecline ? "确认前按 Esc 可取消本次主要行动。" : string.Empty);

        private static string OptionalPrompt(InteractionRequestProjection request) =>
            request.PromptKey == "character.tin_man.purchase.12" ? "支付 12 金券，获得 1 至纯源石？" :
            request.PromptKey == "character.tin_man.purchase.15" ? "支付 15 金券，获得 1 至纯源石？" : "是否执行此项效果？";

        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            if (rendered == request.InteractionId && revision == request.StateRevision) return;
            rendered = request.InteractionId; revision = request.StateRevision;
            if (request.InteractionTypeId == "action.decline_effect")
            {
                clearHighlights();
                string description = OptionalPrompt(request);
                dialog.ShowOptions("可选效果", description, new[]
                {
                    new EffectDialogOption("执行", () => Answer(request, "continue", false)),
                    new EffectDialogOption("放弃", () => Answer(request, "decline", false))
                }, () => Answer(request, "decline", false));
                prompt(description);
                return;
            }
            dialog.Hide(); highlights(Targets(request)); prompt(Prompt(request));
        }

        public override InteractionPresentation BuildPresentation()
        {
            var request = Current();
            return request == null ? InteractionPresentation.Empty :
                new InteractionPresentation(Targets(request), Prompt(request), InteractionMode.Busy, true);
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (!string.IsNullOrEmpty(inFlight)) return InteractionResult.Consumed;
            var candidate = request.CandidateIds.Find(id => Slot(request, id) == slotId);
            if (string.IsNullOrEmpty(candidate)) { prompt("请选择高亮的合法目标。"); return InteractionResult.Consumed; }
            if (request.AllowDecline)
                dialog.ShowOptions("放置影响力", "确认将影响力放置到 " + slotId + "？", new[]
                {
                    new EffectDialogOption("确认放置", () => Answer(request, candidate, false))
                }, () => Answer(request, null, true));
            else Answer(request, candidate, false);
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if ((request.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId || request.InteractionTypeId == CityMoveEffectExecutor.TargetInteractionTypeId) && request.CandidateIds.Contains(locationId))
                Answer(request, locationId, false);
            else prompt("请选择高亮的合法目标。");
            return InteractionResult.Consumed;
        }
        public override InteractionResult OnMobileCityClicked() => IsActive ? InteractionResult.Consumed : InteractionResult.Passthrough;
        public override InteractionResult OnEscape()
        {
            var request = Current();
            if (request == null) return InteractionResult.Passthrough;
            if (request.AllowDecline) Answer(request, null, true);
            else prompt("请先完成当前效果的目标选择。");
            return InteractionResult.Consumed;
        }

        private void Answer(InteractionRequestProjection request, string candidate, bool cancel)
        {
            if (!string.IsNullOrEmpty(inFlight)) return;
            var command = EffectInteractionCommands.Answer(request, player(),
                cancel ? null : new[] { candidate }, cancel);
            inFlight = command.CommandId;
            dialog.Hide(); clearHighlights();
            try { submit(command); }
            catch { NotifyCommandSettled(command.CommandId); throw; }
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (inFlight == commandId || string.IsNullOrEmpty(commandId))
            { inFlight = string.Empty; rendered = string.Empty; revision = -1; }
        }
        public override void Cancel() { Clear(); }
        public void Clear()
        { rendered = string.Empty; revision = -1; inFlight = string.Empty; dialog.Hide(); clearHighlights(); }
    }
}
