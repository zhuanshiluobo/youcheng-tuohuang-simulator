using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.SpecialActions;
using UnityEngine;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>
    /// 展示设施通用 InteractionRequest。它只消费玩家投影并提交 AnswerInteraction，
    /// 不读取或修改设施旧 PendingCardSession，也不持有设施规则分支。
    /// </summary>
    internal sealed class FacilityInteractionUiCoordinator : IDisposable, IInteractionRequestRenderer
    {
        private readonly MainActionInteractionText actionText;
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly FacilityEffectChoiceDialog dialog;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<GameCommand> submit;
        private readonly Action<string> setPrompt;
        private string renderedInteractionId = string.Empty;
        private int renderedRevision = -1;
        private readonly ExplorePathSelectionController explorationPaths = new ExplorePathSelectionController();
        private string inFlightCommandId = string.Empty;
        private readonly List<string> selectedCandidates = new List<string>();

        public FacilityInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            GameplayDialogRegistry dialogRegistry,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt)
            : this(getState, getLocalPlayerId, getCanvas, dialogRegistry, setHighlights,
                clearHighlights, submit, setPrompt, null)
        {
        }

        public FacilityInteractionUiCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<RectTransform> getCanvas,
            GameplayDialogRegistry dialogRegistry,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<GameCommand> submit,
            Action<string> setPrompt, MainActionInteractionText actionText)
        {
            this.actionText = actionText ?? new MainActionInteractionText();
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
            dialog = new FacilityEffectChoiceDialog(
                dialogRegistry ?? throw new ArgumentNullException(nameof(dialogRegistry)),
                (getCanvas ?? throw new ArgumentNullException(nameof(getCanvas)))());
        }

        public bool IsActive
        {
            get
            {
                InteractionRequestProjection ignored;
                return TryGetRequest(out ignored);
            }
        }

        public string Id => "effect.facility.renderer";
        public int Priority => 305;
        public bool CanRender(InteractionRequestProjection request) => request != null && request.VisibleToViewer && request.Status == "open" &&
            request.AnsweringPlayerId == getLocalPlayerId() && request.InteractionTypeId == FacilityEntryEffectTypeIds.InteractionType;
        public void Clear() => ResetAndHide();
        public void Render(InteractionRequestProjection request)
        {
            if (!CanRender(request)) { Clear(); return; }
            RenderRequest(request);
        }

        public bool Synchronize()
        {
            InteractionRequestProjection request;
            if (!TryGetRequest(out request))
            {
                ResetAndHide();
                return false;
            }

            return RenderRequest(request);
        }

        private bool RenderRequest(InteractionRequestProjection request)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId))
            {
                return true;
            }

            var interactionId = request.InteractionId;
            if (interactionId == renderedInteractionId && request.StateRevision == renderedRevision)
            {
                return true;
            }

            selectedCandidates.Clear();
            explorationPaths.Clear();
            renderedInteractionId = interactionId;
            renderedRevision = request.StateRevision;
            clearHighlights();
            if (request.PromptKey == "action.explore.path")
                explorationPaths.SetPathChoices(request.CandidateIds.Select(ExplorationSelectionEffectExecutor.ReadPathCandidate).Where(path => path != null).ToList(), null);
            setHighlights(BuildHighlights(request));

            if (IsMapSelection(request))
            {
                dialog.Hide();
            }
            else if (request.AnswerSchema == "resource_allocation")
            {
                ShowResourceAllocation(request);
            }
            else
            {
                ShowCandidateOptions(request);
            }

            setPrompt(IsMainActionRequest(request) && request.PromptKey == "action.main.confirm"
                ? actionText.confirmStatus : FormatRequestPrompt(request));
            return true;
        }

        private bool IsMapSelection(InteractionRequestProjection request)
        {
            return IsMainActionRequest(request) &&
                (request.PromptKey == "action.dispatch.source" || request.PromptKey == "action.dispatch.target" ||
                 request.PromptKey == "action.move.target" || request.PromptKey == "action.explore.target" || request.PromptKey == "action.explore.path");
        }

        public bool TryHandleLocationClicked(string locationId)
        {
            if (!TryGetRequest(out var request)) return false;
            bool mapSelection = IsMapSelection(request);
            if (mapSelection && (GameplayHudFrame.EffectInputSuspended || !string.IsNullOrEmpty(inFlightCommandId))) return true;
            if (TryHandleCandidateClicked(locationId) || TryHandleCandidateClicked("explore.target:" + locationId)) return true;
            if (request.PromptKey == "effect.explore.choose_path")
            {
                var matches = request.CandidateIds.Where(id => ExploreStepLocation(id) == locationId).ToList();
                if (matches.Count == 1) return TryHandleCandidateClicked(matches[0]);
                if (matches.Count > 1) { setPrompt(IsMainActionRequest(request) && request.PromptKey == "action.main.confirm"
                ? actionText.confirmStatus : FormatRequestPrompt(request)); return true; }
            }
            if (mapSelection) setPrompt(actionText.invalidMapTarget);
            return mapSelection;
        }

        public bool TryHandleInfluenceSlotClicked(string slotId)
        {
            if (!TryGetRequest(out var request)) return false;
            bool mapSelection = IsMapSelection(request);
            if (mapSelection && (GameplayHudFrame.EffectInputSuspended || !string.IsNullOrEmpty(inFlightCommandId))) return true;
            if (TryHandleCandidateClicked(slotId) || TryHandleCandidateClicked("explore.finish:" + slotId)) return true;
            if (mapSelection && request.PromptKey == "action.explore.path")
            {
                var route = StaticMapDefinitions.Resolve(getState().MapId).Routes.Find(r =>
                    Enumerable.Range(0, r.InfluenceSlotCount).Any(i => InfluenceService.GetRouteSlotId(r.RouteId, i) == slotId));
                var paths = explorationPaths.PathChoices.Where(choice => route != null && choice.Path.RouteIds.Contains(route.RouteId)).Select(choice => choice.Path).ToList();
                if (paths.Count == 1)
                {
                    explorationPaths.SelectPath(paths[0]);
                    return TryHandleCandidateClicked(ExplorationSelectionEffectExecutor.PathCandidate(explorationPaths.SelectedPath));
                }
                if (paths.Count > 1 && paths.Count < explorationPaths.PathChoices.Count)
                {
                    explorationPaths.SetPathChoices(paths, null);
                    clearHighlights();
                    setHighlights(BuildHighlights(request));
                    setPrompt(actionText.exploreRoute);
                    return true;
                }
            }
            if (mapSelection) setPrompt(actionText.invalidMapTarget);
            return mapSelection;
        }

        private static string ExploreStepLocation(string candidate)
        {
            if (!candidate.StartsWith("explore.step:", StringComparison.Ordinal)) return null;
            var parts = candidate.Substring(13).Split(':');
            return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        public bool TryHandleEscape()
        {
            if (!IsActive) return false;
            setPrompt("当前设施效果需要完成选择后才能继续。");
            return true;
        }

        public void NotifyCommandSettled(string commandId)
        {
            if (!string.IsNullOrEmpty(commandId) && commandId == inFlightCommandId)
            {
                inFlightCommandId = string.Empty;
                renderedInteractionId = string.Empty;
                renderedRevision = -1;
            }
        }

        public void Dispose()
        {
            ResetAndHide();
        }

        private bool TryHandleCandidateClicked(string candidateId)
        {
            InteractionRequestProjection request;
            if (!TryGetRequest(out request) || string.IsNullOrEmpty(candidateId) ||
                request.CandidateIds == null || !request.CandidateIds.Contains(candidateId))
            {
                return false;
            }

            SelectCandidate(request, candidateId);
            return true;
        }

        private void ShowCandidateOptions(InteractionRequestProjection request)
        {
            var options = new List<EffectDialogOption>();
            if (request.CandidateIds != null)
            {
                for (var i = 0; i < request.CandidateIds.Count; i++)
                {
                    var candidateId = request.CandidateIds[i];
                    options.Add(new EffectDialogOption(
                        (selectedCandidates.Contains(candidateId) ? "已选：" : string.Empty) + FormatCandidateLabel(candidateId),
                        () =>
                        {
                            SelectCandidate(request, candidateId);
                        }));
                }
            }

            if (request.MaxSelections > 1)
            {
                options.Add(new EffectDialogOption("确认选择", () =>
                {
                    if (selectedCandidates.Count < request.MinSelections)
                    {
                        setPrompt("请先选足目标后再确认。");
                        return;
                    }
                    SubmitCandidateAnswer(request, new List<string>(selectedCandidates));
                }));
            }
            dialog.ShowOptions(
                IsMainActionRequest(request) ? actionText.title : "设施效果",
                FormatRequestPrompt(request),
                options);
        }

        private void ShowResourceAllocation(InteractionRequestProjection request)
        {
            var player = getState()?.FindPlayer(getLocalPlayerId());
            var resources = player == null ? new ResourceSet() : player.Resources;
            var isSale = request.CandidateIds != null && request.CandidateIds.Contains("choice.confirm");
            var labels = isSale
                ? new List<string> { "源岩", "源岩碎片", "异铁", "纯源石" }
                : new List<string> { "源岩", "源岩碎片", "异铁" };
            int total = 5;
            if (request.CandidateIds != null) foreach (var candidate in request.CandidateIds)
                if (candidate.StartsWith("total:", StringComparison.Ordinal) && int.TryParse(candidate.Substring(6), out int requestedTotal)) total = requestedTotal;
            var maximums = isSale
                ? new List<int> { resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium }
                : new List<int> { total, total, total };
            var exactTotal = isSale ? -1 : total;
            dialog.ShowResourceAllocation(
                "设施资源效果",
                isSale ? "选择要出售的资源数量。" : "分配总计 " + total + " 点资源。",
                labels,
                maximums,
                exactTotal,
                values => SubmitResourceAnswer(request, values, labels),
                isSale ? (Action)(() => SubmitCandidateAnswer(request, new List<string> { "choice.skip" })) : null);
        }

        private void SubmitCandidateAnswer(InteractionRequestProjection request, IList<string> selected)
        {
            if (request == null || selected == null || selected.Count == 0) return;
            var command = CreateAnswerCommand(request);
            for (var i = 0; i < selected.Count; i++) command.OptionIds.Add(selected[i]);
            SubmitAnswer(command);
        }

        private void SubmitResourceAnswer(
            InteractionRequestProjection request,
            IReadOnlyList<int> values,
            IReadOnlyList<string> labels)
        {
            var parts = new List<string>();
            var resourceKeys = new[] { "Originium", "OriginiumShard", "Iron", "PureOriginium" };
            for (var i = 0; i < values.Count && i < labels.Count && i < resourceKeys.Length; i++)
            {
                if (values[i] > 0)
                {
                    parts.Add(resourceKeys[i] + "=" + values[i].ToString(CultureInfo.InvariantCulture));
                }
            }

            var command = CreateAnswerCommand(request);
            command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] =
                string.Join(",", parts.ToArray());
            SubmitAnswer(command);
        }

        private GameCommand CreateAnswerCommand(InteractionRequestProjection request)
        {
            return EffectInteractionCommands.Answer(request, getLocalPlayerId(), null);
        }

        private void SubmitAnswer(GameCommand command)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId)) return;
            inFlightCommandId = command.CommandId;
            dialog.Hide();
            clearHighlights();
            try { submit(command); }
            catch { inFlightCommandId = string.Empty; renderedRevision = -1; throw; }
        }

        private bool TryGetRequest(out InteractionRequestProjection request)
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
                    candidate.InteractionTypeId == FacilityEntryEffectTypeIds.InteractionType &&
                    candidate.AnsweringPlayerId == localPlayerId)
                {
                    var projection = InteractionRequestProjector.ProjectForPlayer(candidate, localPlayerId);
                    if (!projection.VisibleToViewer) continue;
                    request = projection;
                    return true;
                }
            }

            return false;
        }

        private List<WorkflowHighlight> BuildHighlights(InteractionRequestProjection request)
        {
            var highlights = new List<WorkflowHighlight>();
            if (request.CandidateIds == null) return highlights;
            if (request.PromptKey == "action.explore.path")
            {
                var choices = explorationPaths.PathChoices;
                foreach (var route in StaticMapDefinitions.Resolve(getState().MapId).Routes)
                {
                    int count = choices.Count(choice => choice.Path.RouteIds.Contains(route.RouteId));
                    if (count == 0 || count == choices.Count) continue;
                    for (int i = 0; i < route.InfluenceSlotCount; i++)
                        highlights.Add(new WorkflowHighlight(WorkflowHighlightTargetKind.InfluenceSlot,
                            InfluenceService.GetRouteSlotId(route.RouteId, i), WorkflowHighlightSemantic.ExploreTarget));
                }
                return highlights;
            }
            for (var i = 0; i < request.CandidateIds.Count; i++)
            {
                var candidate = request.CandidateIds[i] ?? string.Empty;
                var target = candidate;
                var semantic = WorkflowHighlightSemantic.EventInfluenceTarget;
                var targetKind = WorkflowHighlightTargetKind.InfluenceSlot;
                if (candidate.StartsWith("explore.step:", StringComparison.Ordinal))
                {
                    var parts = candidate.Substring(13).Split(':');
                    if (parts.Length != 2) continue;
                    target = Uri.UnescapeDataString(parts[1]);
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.ExploreTarget;
                }
                else if (candidate.StartsWith("explore.target:", StringComparison.Ordinal))
                {
                    target = candidate.Substring(15);
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.ExploreTarget;
                }
                else if (candidate.StartsWith("explore.path:", StringComparison.Ordinal)) continue;
                else if (candidate.StartsWith("explore.finish:", StringComparison.Ordinal))
                {
                    target = candidate.Substring(15);
                    semantic = WorkflowHighlightSemantic.ExploreTarget;
                }
                else if (request.PromptKey.StartsWith("action.dispatch.", StringComparison.Ordinal))
                {
                    if (candidate.StartsWith("action.", StringComparison.Ordinal)) continue;
                }
                else if (candidate.StartsWith("explore:", StringComparison.Ordinal))
                {
                    target = candidate.Substring("explore:".Length);
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.ExploreTarget;
                }
                else if (candidate.StartsWith("replace:", StringComparison.Ordinal) ||
                         candidate.StartsWith("deploy:", StringComparison.Ordinal))
                {
                    target = candidate.Substring(candidate.IndexOf(':') + 1);
                    semantic = WorkflowHighlightSemantic.DeployTarget;
                }
                else if (candidate.IndexOf('-', StringComparison.Ordinal) > 0)
                {
                    targetKind = WorkflowHighlightTargetKind.Location;
                    semantic = WorkflowHighlightSemantic.MoveTarget;
                }
                else if (!candidate.Contains(":"))
                {
                    continue;
                }

                highlights.Add(new WorkflowHighlight(targetKind, target, semantic));
            }

            return highlights;
        }

        private void SelectCandidate(InteractionRequestProjection request, string candidateId)
        {
            if (!string.IsNullOrEmpty(inFlightCommandId)) return;
            if (request.MaxSelections <= 1)
            {
                SubmitCandidateAnswer(request, new List<string> { candidateId });
                return;
            }
            if (!selectedCandidates.Remove(candidateId))
            {
                if (selectedCandidates.Count >= request.MaxSelections)
                {
                    setPrompt("已选足目标，可先取消一个选择再更换。");
                    return;
                }
                selectedCandidates.Add(candidateId);
            }
            ShowCandidateOptions(request);
        }

        private string FormatRequestPrompt(InteractionRequestProjection request)
        {
            if (!IsMainActionRequest(request)) return FormatPrompt(request.PromptKey);
            if (request.PromptKey == "facility.entry.choose_additional_build") return actionText.buildChoose;
            if (request.PromptKey == "effect.explore.choose_path") return actionText.explorePath;
            if (request.PromptKey == "effect.build.choose_slot") return actionText.buildSlot;
            if (request.PromptKey != "action.main.confirm") return FormatPrompt(request.PromptKey);
            var runtime = getState().EffectRuntime;
            var raw = runtime.InteractionRequests.Find(r => r.InteractionId == request.InteractionId);
            var node = runtime.EffectNodes.Find(n => n.EffectId == raw.OwnerEffectId);
            string[] stage = (node.FlowStage ?? "").Split('|').Select(Uri.UnescapeDataString).ToArray();
            string summary = "";
            if (node.EffectTypeId == MainActionSelectionEffectExecutor.Dispatch && stage.Length >= 3)
            {
                var moves = new List<string>();
                for (int i = 1; i + 1 < stage.Length; i += 2) moves.Add(stage[i] + " → " + stage[i + 1]);
                summary = string.Format(actionText.dispatchSummary, string.Join("；", moves));
            }
            else if (node.EffectTypeId == MainActionSelectionEffectExecutor.Move && stage.Length == 2)
                summary = string.Format(actionText.moveSummary, stage[1]);
            else if (node.EffectTypeId == FacilitySelectionEffectExecutor.TypeId && stage.Length == 4)
                summary = string.Format(actionText.buildSummary, FacilityCardDatabase.Get(stage[1])?.Name ?? stage[1], FormatCandidateLabel(stage[2]), int.Parse(stage[3]) + 1);
            else if (node.EffectTypeId == ExplorationSelectionEffectExecutor.TypeId && stage.Length >= 5)
                summary = string.Format(actionText.exploreSummary, stage[1].Replace(",", " → "), stage[2], FormatMapSlot(stage[3]), FormatRecipients(stage[4]));
            else if (node.EffectTypeId == MainActionSelectionEffectExecutor.Special && stage.Length >= 3)
            {
                summary = string.Format(actionText.specialSummary, SpecialActionDatabase.Get(stage[1])?.Name ?? stage[1], stage[2]);
                if (stage.Length == 5) summary += "\n" + string.Format(actionText.paymentFormat, stage[3], stage[4]);
            }
            else if (node.EffectTypeId == MainActionSelectionEffectExecutor.Confirm && node.NestedEffects.Count == 1)
            {
                var operation = node.NestedEffects[0];
                var args = operation.NormalizedArguments;
                if (operation.EffectTypeId == CityMoveEffectTypeIds.Move)
                    summary = string.Format(actionText.moveSummary, Argument(args, "targetLocation"));
                else if (operation.EffectTypeId == ExplorationEffectTypeIds.Explore)
                    summary = string.Format(actionText.exploreSummary, Argument(args, "pathLocationIds"), Argument(args, "pathRouteIds"), FormatMapSlot(Argument(args, "influenceSlot")), FormatRecipients(Argument(args, "paymentRecipients")));
                else
                {
                    var moves = args.Properties.Find(p => p.Name == "moves")?.Value.Items;
                    if (moves != null) summary = string.Format(actionText.dispatchSummary,
                        string.Join("；", moves.Select(m => Argument(m, "targetInfluence") + " → " + Argument(m, "targetSlotId"))));
                }
            }
            return summary.Length == 0 ? actionText.confirmPrompt : summary + "\n\n" + actionText.confirmPrompt;
        }

        private string FormatMapSlot(string slotId)
        {
            var parts = (slotId ?? "").Split(':');
            if (parts.Length == 3 && int.TryParse(parts[2], out var index))
            {
                if (parts[0] == "location") return string.Format(actionText.locationSlotFormat, parts[1], index + 1);
                if (parts[0] == "route") return string.Format(actionText.routeSlotFormat, parts[1], index + 1);
            }
            return slotId;
        }

        private string FormatRecipients(string recipients)
        {
            if (string.IsNullOrEmpty(recipients)) return actionText.automaticRecipients;
            return string.Join("；", recipients.Split(',').Select(entry =>
            {
                var parts = entry.Split('=');
                return parts.Length == 2 ? string.Format(actionText.recipientFormat, parts[0], parts[1]) : entry;
            }));
        }

        private static string Argument(NormalizedValue value, string key)
        {
            return DisplayValue(value?.Properties?.Find(p => p.Name == key)?.Value);
        }

        private static string DisplayValue(NormalizedValue value)
        {
            if (value == null) return "";
            if (value.Kind == NormalizedValueKind.Array) return string.Join(" → ", value.Items.Select(DisplayValue));
            if (value.Kind == NormalizedValueKind.Object) return string.Join(" / ", value.Properties.Select(p => DisplayValue(p.Value)));
            if (value.Kind == NormalizedValueKind.StableReference) return value.ReferenceId;
            if (value.Kind == NormalizedValueKind.Integer) return value.IntegerValue.ToString(CultureInfo.InvariantCulture);
            return value.StringValue ?? "";
        }

        private string FormatPrompt(string key)
        {
            var mainPrompt = actionText.Prompt(key);
            if (mainPrompt != null) return mainPrompt;
            switch (key)
            {
                case "effect.build.choose_payment": return "选择本次建设的支付方式。";
                case "effect.build.choose_slot": return "选择建设位置。确认后支付费用并建设。";
                case "effect.explore.choose_path": return "逐段选择探索路线；到达合法目标后选择落点并结算，可退回上一步。";
                case "effect.explore.choose_recipient": return "选择此段探索路费的接收玩家。";
                case "facility.entry.choose_vehicle_action": return "选择移除后调度，或执行探索。";
                case "facility.entry.choose_adjacent": return "选择相邻设施，触发其入场效果。";
                case "facility.entry.choose_additional_build": return "选择要额外建设的设施。";
                case "facility.entry.choose_extension_hub": return "选择延伸枢纽或跳过。";
                case "facility.entry.sell_resources": return "选择要出售的资源数量。";
                case "facility.entry.choose_free_move": return "选择城市移动目的地。";
                case "facility.entry.choose_resources": return "分配总计 5 点资源。";
                case "facility.entry.choose_influence_branch": return "选择替换或部署影响力。";
                case "facility.entry.choose_two_influences": return "选择影响力位置，再确认选择。";
                default: return "请完成设施效果选择。";
            }
        }

        private bool IsMainActionRequest(InteractionRequestProjection request)
        {
            var runtime = getState()?.EffectRuntime;
            if (runtime == null) return false;
            var raw = runtime.InteractionRequests.Find(r => r.InteractionId == request.InteractionId);
            var node = raw == null ? null : runtime.EffectNodes.Find(n => n.EffectId == raw.OwnerEffectId);
            var parent = node == null ? null : runtime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            return parent != null && parent.EffectTypeId == MainActionEffectExecutor.TypeId;
        }

        private string FormatCandidateLabel(string candidateId)
        {
            if (string.IsNullOrEmpty(candidateId)) return "未命名选项";
            if (candidateId == "action.confirm") return actionText.confirm;
            if (candidateId == "action.dispatch.finish") return actionText.dispatchFinish;
            if (candidateId == "action.dispatch.add") return actionText.dispatchAdd;
            if (candidateId.StartsWith("payment:", StringComparison.Ordinal))
            { var parts = candidateId.Split(':'); if (parts.Length == 3) return string.Format(actionText.paymentFormat, parts[1], parts[2]); }
            if (candidateId.StartsWith("special:", StringComparison.Ordinal))
            { var parts = candidateId.Split(':'); if (parts.Length == 3) return string.Format(actionText.specialFormat, SpecialActionDatabase.Get(Uri.UnescapeDataString(parts[1]))?.Name ?? Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2])); }
            if (candidateId == "vehicle.remove_move") return "移除 1 个影响力，然后调度 1 次";
            if (candidateId == "vehicle.explore") return "执行 1 次探索";
            if (candidateId == "resources") return "支付资源";
            if (candidateId == "gold") return "支付金券";
            if (candidateId == "free") return "免费建设";
            if (candidateId == "explore.back") return "退回上一段路线";
            if (candidateId.StartsWith("build-slot:", StringComparison.Ordinal) && int.TryParse(candidateId.Substring(11), out int slot)) return "建设到城市面板位置 " + (slot + 1);
            if (candidateId.StartsWith("explore.step:", StringComparison.Ordinal))
            { var parts = candidateId.Substring(13).Split(':'); if (parts.Length == 2) return "经航道 " + Uri.UnescapeDataString(parts[0]) + " 前往 " + Uri.UnescapeDataString(parts[1]); }
            if (candidateId.StartsWith("explore.finish:", StringComparison.Ordinal)) return string.Format(actionText.exploreFinishFormat, candidateId.Substring(15));
            if (candidateId.StartsWith("explore.pay:", StringComparison.Ordinal)) return "支付给玩家 " + candidateId.Substring(12);
            var card = FacilityCardDatabase.Get(candidateId);
            if (card != null) return card.Name;
            var placements = getState()?.Map?.Facilities;
            if (placements != null) foreach (var placement in placements)
                if (placement != null && placement.ContentInstanceId == candidateId)
                    return (FacilityCardDatabase.Get(placement.FacilityCardId)?.Name ?? placement.FacilityCardId) + "（位置 " + (placement.CityBoardSlotIndex + 1) + "）";

            if (candidateId.StartsWith("explore:", StringComparison.Ordinal)) return "探索 " + candidateId.Substring(8);
            if (candidateId.StartsWith("replace:", StringComparison.Ordinal)) return "替换影响力 " + candidateId.Substring(8);
            if (candidateId.StartsWith("deploy:", StringComparison.Ordinal)) return "部署影响力 " + candidateId.Substring(7);
            if (candidateId == "choice.skip") return "跳过";
            if (candidateId == "choice.confirm") return "确认出售";
            return candidateId;
        }

        private void ResetAndHide()
        {
            selectedCandidates.Clear();
            explorationPaths.Clear();
            renderedInteractionId = string.Empty;
            renderedRevision = -1;
            inFlightCommandId = string.Empty;
            dialog.Hide();
            clearHighlights();
        }
    }
}
