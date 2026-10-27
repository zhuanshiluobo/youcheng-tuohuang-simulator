#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Linq;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // 本地与联机共用正式公开预览；预检不提交命令，也不增加探索完成计数。
        private string EligibleCampaignMainAction(string planned, int actor)
        {
            if (planned != "explore") return planned;
            var visible = hud?.Frame == null ? null : Read(hud.Frame, "gameplaySnapshot") as GameStateView;
            int before = commands.Count;
            var progress = PlayProgress(actor);
            int explorations = progress.Explorations;
            bool current = CurrentPaymentView() != null && actor == LocalPlayer && actor == State.CurrentPlayerId &&
                visible.CurrentPlayerId == State.CurrentPlayerId && visible.MapId == State.MapId &&
                visible.Phase == State.Phase && visible.Round == State.Round;
            void Record(string origin, bool valid, string reason, string[] candidates) =>
                Append("探索资格预检.jsonl", new { State.GameId, State.ContentPackHash, State.MapId,
                    State.Round, State.Phase, PlayerId = actor, Revision = State.EffectRuntime.StateRevision,
                    Planned = planned, Origin = origin, Valid = valid, Reason = reason,
                    Input = visible == null ? null : new { visible.GameId, visible.ContentPackHash, visible.MapId,
                        visible.StateRevision, visible.ViewerRole, visible.ViewerPlayerId, visible.CurrentPlayerId,
                        visible.Phase, visible.Round, visible.Decks?.EventDeckGreenCount,
                        visible.Decks?.EventDeckYellowCount, visible.Decks?.EventDeckRedCount },
                    VisibleCandidates = candidates, CommandCountBefore = before, CommandCountAfter = commands.Count,
                    ExplorationCountBefore = explorations, ExplorationCountAfter = progress.Explorations,
                    InputSubmitted = false, NewCompletionClaimed = false });
            if (!current)
            {
                Record("DriverGuard", false, "driver_exploration_projection_identity_invalid", null);
                Require(false, "正常探索决策缺少本人当前同修订的正式公开投影");
            }
            var eligibility = new RouteResourceSelectionService().TryCreateExplorationPreview(
                GameStateViewProjector.ToClientState(visible), visible, actor, out var session, out _, out _);
            bool unavailable = !eligibility.IsValid && eligibility.Reason == "route_selection_target_unavailable";
            Record("TryCreateExplorationPreview", eligibility.IsValid, eligibility.Reason,
                session?.SelectableLocationUpperBound.ToArray() ?? (unavailable ? Array.Empty<string>() : null));
            if (unavailable)
            {
                Append("决策变化.jsonl", new { State.GameId, State.Round, State.ActionRound, PlayerId = actor,
                    Action = "正式探索无合法候选，转其他正常行动", OriginalPlan = planned,
                    eligibility.Reason, visible.Decks.EventDeckGreenCount, visible.Decks.EventDeckYellowCount,
                    visible.Decks.EventDeckRedCount, Progress = progress,
                    Fallback = "沿原特殊行动、建设或部署入口继续，不把无目标计为失败或探索完成" });
                return "build";
            }
            Require(eligibility.IsValid, "正常探索正式资格校验失败：" + eligibility.Reason);
            return planned;
        }

        private IEnumerator BeginCampaignExploration(PlayerPlayProgress progress)
        {
            int before = commands.Count;
            yield return Press(hud.ActionPanelView.ExploreButton, "按资源计划正常探索");
            if (Route?.IsActive != true)
                Write("探索合法预览未成草稿-" + State.EffectRuntime.StateRevision + ".json", new {
                    State.GameId, State.Round, State.Phase, PlayerId = LocalPlayer,
                    Revision = State.EffectRuntime.StateRevision, CommandCountBefore = before,
                    CommandCountAfter = commands.Count, Input = CurrentPaymentView() == null ? null : new {
                        CurrentPaymentView().ViewerPlayerId, CurrentPaymentView().StateRevision },
                    FallbackSelected = false, NewCompletionClaimed = false });
            Require(Route?.IsActive == true, "正常探索入口未形成实际地图草稿");
            explorationPlayPlan = PlanExploration(Route.Session, Route.Projection, State.Round >= 4 && !progress.RedZoneReached);
        }
    }
}
#endif
