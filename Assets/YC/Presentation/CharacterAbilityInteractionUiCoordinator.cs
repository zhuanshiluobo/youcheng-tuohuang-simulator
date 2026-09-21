using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 展示 NMC-010 角色能力生成的通用 InteractionRequest。
    /// 这类请求不再回退到 PendingCharacterEffect，也不能借用事件牌提示。
    /// </summary>
    internal sealed class CharacterAbilityInteractionUiCoordinator :
        InteractionBase,
        IInteractionRequestRenderer,
        IDisposable
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly CharacterCardEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private string renderedInteractionId = string.Empty;
        private int renderedRevision = -1;
        private string inFlightCommandId = string.Empty;
        private readonly HashSet<string> selectedCandidates = new HashSet<string>(StringComparer.Ordinal);

        public CharacterAbilityInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            CharacterCardEffectChoiceDialog dialog,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public override string Id => "character-ability.interaction";

        int IInteractionRequestRenderer.Priority => 310;

        public override InteractionPriority Priority => InteractionPriority.PendingResolution;

        public override bool IsActive
        {
            get
            {
                InteractionRequest ignored;
                return TryGetRequest(out ignored);
            }
        }

        public bool CanRender(InteractionRequestProjection request)
        {
            return request != null && request.VisibleToViewer &&
                   request.AnsweringPlayerId == getLocalPlayerId() && request.Status == "open" &&
                   IsCharacterAbilityInteraction(request.InteractionTypeId);
        }

        public void Render(InteractionRequestProjection projection)
        {
            if (!CanRender(projection))
            {
                Clear();
                return;
            }

            if (projection.InteractionId == renderedInteractionId &&
                projection.StateRevision == renderedRevision &&
                string.IsNullOrEmpty(inFlightCommandId))
            {
                return;
            }

            renderedInteractionId = projection.InteractionId;
            renderedRevision = projection.StateRevision;
            selectedCandidates.Clear();
            clearHighlights();

            if (IsLocationTargetInteraction(projection.PromptKey))
            {
                dialog.Hide();
                setHighlights(BuildTargetHighlights(
                    projection.CandidateIds,
                    WorkflowHighlightTargetKind.Location,
                    WorkflowHighlightSemantic.MoveTarget));
            }
            else if (IsInfluenceTargetInteraction(projection.PromptKey))
            {
                dialog.Hide();
                setHighlights(BuildTargetHighlights(
                    projection.CandidateIds,
                    WorkflowHighlightTargetKind.InfluenceSlot,
                    InfluenceTargetSemantic(projection.PromptKey)));
            }
            else if (projection.PromptKey == "character.cannot.strategy.choose_sale" || projection.PromptKey == "effect.resource.sell")
            {
                dialog.ShowResourceSaleCandidates(
                    projection.CandidateIds,
                    selected => SubmitCandidates(projection, selected),
                    null);
            }
            else
            {
                ShowCandidateOptions(projection);
            }

            setPrompt(BuildPrompt(projection.PromptKey));
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            InteractionRequest request;
            if (TryGetRequest(out request) &&
                IsLocationTargetInteraction(request.PromptKey) &&
                request.CandidateIds != null &&
                request.CandidateIds.Contains(locationId))
            {
                SubmitCandidates(request, new[] { locationId });
                return InteractionResult.Consumed;
            }

            setPrompt("请先完成当前角色能力选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            if (!IsActive) return InteractionResult.Passthrough;
            InteractionRequest request;
            if (TryGetRequest(out request) &&
                IsInfluenceTargetInteraction(request.PromptKey) &&
                request.CandidateIds != null &&
                request.CandidateIds.Contains(slotId))
            {
                SubmitCandidates(request, new[] { slotId });
                return InteractionResult.Consumed;
            }

            setPrompt(request != null && IsInfluenceTargetInteraction(request.PromptKey)
                ? "请点击地图上高亮的合法影响力槽位。"
                : "请在角色能力选择界面中完成当前选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("请先完成当前角色能力选择。");
            return InteractionResult.Consumed;
        }

        public override InteractionResult OnEscape()
        {
            if (!IsActive) return InteractionResult.Passthrough;
            setPrompt("当前角色能力选择不能取消，请完成结算。");
            return InteractionResult.Consumed;
        }

        public override InteractionPresentation BuildPresentation()
        {
            InteractionRequest request;
            if (!TryGetRequest(out request)) return InteractionPresentation.Empty;

            if (IsLocationTargetInteraction(request.PromptKey))
            {
                return new InteractionPresentation(
                    BuildTargetHighlights(
                        request.CandidateIds,
                        WorkflowHighlightTargetKind.Location,
                        WorkflowHighlightSemantic.MoveTarget),
                    BuildPrompt(request.PromptKey),
                    InteractionMode.Busy,
                    true);
            }

            if (IsInfluenceTargetInteraction(request.PromptKey))
            {
                return new InteractionPresentation(
                    BuildTargetHighlights(
                        request.CandidateIds,
                        WorkflowHighlightTargetKind.InfluenceSlot,
                        InfluenceTargetSemantic(request.PromptKey)),
                    BuildPrompt(request.PromptKey),
                    InteractionMode.Busy,
                    true);
            }

            return new InteractionPresentation(
                new List<WorkflowHighlight>().AsReadOnly(),
                BuildPrompt(request.PromptKey),
                InteractionMode.Busy,
                false);
        }

        public override void Cancel()
        {
            Clear();
        }

        public override void NotifyCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(inFlightCommandId) ||
                (!string.IsNullOrEmpty(commandId) && commandId != inFlightCommandId))
            {
                return;
            }

            inFlightCommandId = string.Empty;
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
        }

        public void Dispose()
        {
            Clear();
        }

        public void Clear()
        {
            selectedCandidates.Clear();
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
            inFlightCommandId = string.Empty;
            dialog.Hide();
            clearHighlights();
        }

        private void ShowCandidateOptions(InteractionRequestProjection projection)
        {
            var options = new List<EffectDialogOption>();
            if (projection.CandidateIds != null)
            {
                for (var i = 0; i < projection.CandidateIds.Count; i++)
                {
                    var candidateId = projection.CandidateIds[i];
                    options.Add(new EffectDialogOption(
                        (selectedCandidates.Contains(candidateId) ? "已选：" : "") + FormatCandidateLabel(candidateId),
                        () =>
                        {
                            if (projection.MaxSelections <= 1)
                            {
                                SubmitCandidates(projection, new[] { candidateId });
                                return;
                            }
                            if (!selectedCandidates.Remove(candidateId) && selectedCandidates.Count < projection.MaxSelections)
                                selectedCandidates.Add(candidateId);
                            ShowCandidateOptions(projection);
                        }));
                }
            }
            if (projection.MaxSelections > 1 && selectedCandidates.Count >= projection.MinSelections)
                options.Add(new EffectDialogOption("确认选择", () => SubmitCandidates(projection,
                    projection.CandidateIds.FindAll(id => selectedCandidates.Contains(id)))));

            dialog.ShowOptions(
                "角色能力",
                BuildPrompt(projection.PromptKey),
                options);
        }

        private void SubmitCandidates(
            InteractionRequestProjection projection,
            IReadOnlyList<string> selected)
        {
            if (projection == null || selected == null || selected.Count == 0 ||
                !string.IsNullOrEmpty(inFlightCommandId))
            {
                return;
            }

            var command = EffectInteractionCommands.Answer(projection, getLocalPlayerId(), selected);

            inFlightCommandId = command.CommandId;
            dialog.Hide();
            clearHighlights();
            try
            {
                submit(command);
            }
            catch
            {
                inFlightCommandId = string.Empty;
                throw;
            }
        }

        private void SubmitCandidates(
            InteractionRequest request,
            IReadOnlyList<string> selected)
        {
            if (request == null) return;
            SubmitCandidates(
                InteractionRequestProjector.ProjectForPlayer(request, getLocalPlayerId()),
                selected);
        }

        private bool TryGetRequest(out InteractionRequest request)
        {
            request = null;
            var state = getState();
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.InteractionRequests == null)
            {
                return false;
            }

            var localPlayerId = getLocalPlayerId();
            for (var i = 0; i < state.EffectRuntime.InteractionRequests.Count; i++)
            {
                var candidate = state.EffectRuntime.InteractionRequests[i];
                if (candidate != null && candidate.Status == "open" &&
                    candidate.AnsweringPlayerId == localPlayerId &&
                    IsCharacterAbilityInteraction(candidate.InteractionTypeId))
                {
                    request = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool IsCharacterAbilityInteraction(string interactionTypeId)
        {
            return interactionTypeId == "lua.choice" ||
                   (!string.IsNullOrEmpty(interactionTypeId) &&
                    interactionTypeId.StartsWith("character.ability.", StringComparison.Ordinal));
        }

        private static bool IsLocationTargetInteraction(string promptKey)
        {
            return promptKey == "character.elysium.tactic.choose_raid";
        }

        private static bool IsInfluenceTargetInteraction(string promptKey)
        {
            return promptKey == "effect.influence.remove.choose_target" || promptKey == "effect.influence.replace.choose_target" || promptKey == "effect.influence.place.choose_target" ||
                   promptKey == "character.liskarm.tactic.choose_target" ||
                   promptKey == "character.texas.tactic.choose_removal";
        }

        private static WorkflowHighlightSemantic InfluenceTargetSemantic(string promptKey)
        {
            return promptKey == "effect.influence.place.choose_target"
                ? WorkflowHighlightSemantic.DeployTarget
                : WorkflowHighlightSemantic.EventInfluenceTarget;
        }

        private static List<WorkflowHighlight> BuildTargetHighlights(
            IReadOnlyList<string> candidateIds,
            WorkflowHighlightTargetKind targetKind,
            WorkflowHighlightSemantic semantic)
        {
            var highlights = new List<WorkflowHighlight>();
            if (candidateIds == null) return highlights;

            for (var i = 0; i < candidateIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(candidateIds[i]))
                {
                    highlights.Add(new WorkflowHighlight(targetKind, candidateIds[i], semantic));
                }
            }

            return highlights;
        }

        private static string BuildPrompt(string promptKey)
        {
            switch (promptKey ?? string.Empty)
            {
                case "effect.resource.sell": return "选择要出售的资源数量（可以全部为零）。";
                case "effect.influence.replace.choose_target": return "请选择要替换的对手影响力。";
                case "facility.entry.choose_influence_branch": return "选择替换或放置 1 个影响力。";
                case "effect.influence.place.choose_target":
                    return "请选择地图上高亮的空格放置影响力。";
                case "character.elysium.strategy.choose_resource":
                    return "极境策略：选择一种基础资源。";
                case "character.cannot.tactic.choose_resource":
                    return "坎诺特计谋：选择一种基础资源进行征收。";
                case "character.cannot.strategy.choose_sale":
                    return "坎诺特策略：选择要出售的资源数量。";
                case "character.liskarm.tactic.choose_target":
                    return "雷蛇战术：选择要控制的影响力目标。";
                case "character.elysium.tactic.choose_raid":
                    return "极境计谋：选择相邻、已探索且有己方影响力的资源点移动城市。";
                case "character.texas.strategy.choose_facility":
                    return "德克萨斯策略：选择一张设施牌。";
                case "effect.influence.remove.choose_target":
                    return "选择要移除的影响力。";
                case "character.texas.tactic.choose_removal":
                    return "德克萨斯战术：选择要移除的影响力。";
                case "effect.influence.move.choose_target":
                    return "选择本次调度的起点和终点。";
                case "character.texas.tactic.choose_first_move":
                    return "德克萨斯战术：选择第一次移动。";
                case "character.texas.tactic.choose_second_move":
                    return "德克萨斯战术：选择第二次移动。";
                case "character.tin_man.strategy.choose_purchase":
                    return "锡人策略：选择是否购买至纯源石。";
                case "character.tin_man.strategy.choose_second_purchase":
                    return "锡人策略：选择是否进行第二次购买。";
                case "character.tin_man.tactic.choose_discard_resolution":
                    return "锡人战术：选择弃牌带来的奖励。";
                default:
                    return "请完成当前角色能力选择。";
            }
        }

        private static string FormatCandidateLabel(string candidateId)
        {
            if (string.IsNullOrEmpty(candidateId)) return "未命名选项";

            var parts = candidateId.Split('|');
            if (parts.Length == 3 && parts[0] == "sale")
            {
                return FormatResource(parts[1]) + " × " + parts[2];
            }

            if (parts.Length == 3 && parts[0] == "place")
            {
                return "放置到 " + parts[1] + " 和 " + parts[2];
            }

            if (parts.Length == 3 && parts[0] == "move")
            {
                return "从 " + parts[1] + " 移动到 " + parts[2];
            }

            return FormatResource(candidateId);
        }

        private static string FormatResource(string resourceId)
        {
            switch (resourceId ?? string.Empty)
            {
                case "originium": return "源岩";
                case "originium-shard": return "源石碎片";
                case "iron": return "异铁";
                case "pure-originium": return "至纯源石";
                case "choice.finish_character_use": return "结束角色牌使用";
                case "choice.purchase_first_pure_originium": return "支付 12 金券购买至纯源石";
                case "choice.purchase_second_pure_originium": return "支付 15 金券购买至纯源石";
                case "choice.gain_gold":
                case CharacterEffectChoiceIds.GainGold: return "获得 5 金券";
                case "replace-influence": return "替换 1 个对手影响力";
                case "place-influence": return "放置 1 个己方影响力";
                case "move-influence": return "移动 1 枚己方影响力";
                default: return resourceId;
            }
        }
    }
}
