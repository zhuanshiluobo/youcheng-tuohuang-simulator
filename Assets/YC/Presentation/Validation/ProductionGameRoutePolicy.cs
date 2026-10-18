#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;
using YC.Domain.Movement;
using YC.Domain.Effects;

namespace YC.Presentation.Validation
{
    // 路线是有限的玩家意图；权限、支付、随机和效果结算始终由正式 Host 决定。
    [Serializable]
    public sealed class ProductionGameRoutePlan
    {
        public int SchemaVersion = 1;
        public string RouteName = "", MapId = "", ContentPackHash = "";
        public int PlayerCount;
        public int MaximumCommands = 1200;
        public bool AllowAdaptiveMainActions;
        public bool UseOptionalInsertions;
        public string EvaluationMode = "";
        public bool RepresentativeRecovery;
        public ProductionGameSelectionPriorities SelectionPriorities = new ProductionGameSelectionPriorities();
        public List<string> CandidatePriorityDefinitionIds = new List<string>();
        public List<string> EnabledExpansionIds = new List<string>();
        public List<ProductionGameRouteStep> Steps = new List<ProductionGameRouteStep>();
        public List<ProductionGameCoverageRequirement> Coverage = new List<ProductionGameCoverageRequirement>();
        public List<ProductionGameEndActionCase> EndActionCases = new List<ProductionGameEndActionCase>();
        public List<ProductionGameRecoveryCheckpoint> RecoveryCheckpoints = new List<ProductionGameRecoveryCheckpoint>();
        public void Validate()
        {
            if (Steps == null || Coverage == null || EndActionCases == null || RecoveryCheckpoints == null || EnabledExpansionIds == null || CandidatePriorityDefinitionIds == null || SelectionPriorities == null)
                throw new ArgumentException("路线集合不得为null。");
            SelectionPriorities.Validate();
            if (EvaluationMode != "" && EvaluationMode != "whole-game-health") throw new ArgumentException("未知整局评估方式。");
            if (SchemaVersion != 1 || MaximumCommands < 0 || MaximumCommands > 5000 || EvaluationMode != "whole-game-health" && MaximumCommands == 0 || string.IsNullOrWhiteSpace(RouteName) || string.IsNullOrWhiteSpace(ContentPackHash) ||
                PlayerCount < 2 || PlayerCount > 4 || !EnabledExpansionIds.Contains("core") ||
                EnabledExpansionIds.Contains("infected") && !EnabledExpansionIds.Contains("enterprise") ||
                EnabledExpansionIds.Distinct().Count() != EnabledExpansionIds.Count ||
                EnabledExpansionIds.Any(id => !new[] { "core", "enterprise", "infected" }.Contains(id)) ||
                !new[] { "map-three-players", "map-four-players" }.Contains(MapId) ||
                PlayerCount == 4 && MapId != "map-four-players") throw new ArgumentException("路线固定配置无效。");
            if (Steps.Any(s => s == null || string.IsNullOrWhiteSpace(s.StepId) || s.PlayerId < 1 || s.PlayerId > PlayerCount ||
                s.Round < 0 || s.Round > 8 || !Enum.GetNames(typeof(GamePhase)).Contains(s.Phase) ||
                !Enum.GetNames(typeof(GameCommandKind)).Contains(s.CommandKind)) ||
                Steps.Select(s => s.StepId).Distinct().Count() != Steps.Count) throw new ArgumentException("路线步骤身份或阶段无效。");
            if (Steps.Any(s => s.ExpectRejection && s.ExpectedErrorCode.Length == 0) ||
                Coverage.Any(c => c == null || string.IsNullOrWhiteSpace(c.CoverageId) || string.IsNullOrWhiteSpace(c.DefinitionId) || string.IsNullOrWhiteSpace(c.Branch)) ||
                Coverage.Select(c => c.CoverageId).Distinct().Count() != Coverage.Count ||
                EndActionCases.Any(c => c == null || string.IsNullOrWhiteSpace(c.CaseId) || !Steps.Any(s => s.StepId == c.StepId && s.PlayerId == c.PlayerId) ||
                    !new[] { "mandatory-idle-rejected", "active-node-only", "optional-window-declined" }.Contains(c.Kind)))
                throw new ArgumentException("反例、覆盖合同或结束语义身份无效。");
            if (Coverage.Any(c => c.Clauses == null || c.Required && (c.Clauses.Count < 3 ||
                new[] { "identity", "result", "state-change" }.Any(role => !c.Clauses.Any(clause => clause.Role == role))) ||
                c.Clauses.Any(clause => clause == null || !new[] { "identity", "result", "state-change" }.Contains(clause.Role) ||
                    !new[] { "equals", "not-equals", "contains", "delta-equals" }.Contains(clause.Operator))))
                throw new ArgumentException("必测业务必须有身份、结果和真实前后变化的联合合同。");
            if (Steps.Any(s => s.ChooseLegalMainAction && s.Phase != nameof(GamePhase.ActionRound1) && s.Phase != nameof(GamePhase.ActionRound2)))
                throw new ArgumentException("合法主行动选择只能用于两个正式主行动阶段。");
            if (RecoveryCheckpoints.Any(c => c == null || !System.Text.RegularExpressions.Regex.IsMatch(c.CheckpointId, @"^[A-Za-z][A-Za-z0-9_-]{0,63}$") ||
                c.PlayerId != 1 || c.Round < 0 || c.Round == 0 && !c.OptionalIfUnavailable || c.Round > 8 || !Enum.GetNames(typeof(GamePhase)).Contains(c.Phase) ||
                string.IsNullOrWhiteSpace(c.RequestKind)) || RecoveryCheckpoints.Select(c => c.CheckpointId).Distinct().Count() != RecoveryCheckpoints.Count)
                throw new ArgumentException("恢复检查点必须引用明确玩家、回合、阶段和真实请求类型。");
            // 旧正式路线的正数上限只作为旧输入接受，不能恢复整局自动硬停。
            if (EvaluationMode == "whole-game-health") MaximumCommands = 0;
        }
    }

    [Serializable]
    public sealed class ProductionGameSelectionPriorities
    {
        public List<string> EnterpriseDefinitionIds = new List<string>(), DispatchCharacterDefinitionIds = new List<string>(),
            CityStyleDefinitionIds = new List<string>(), SpecialActionIds = new List<string>(), FacilityDefinitionIds = new List<string>();
        public List<ProductionGameCharacterModePriority> CharacterUseModes = new List<ProductionGameCharacterModePriority>();
        public void Validate()
        {
            foreach (var values in new[] { EnterpriseDefinitionIds, DispatchCharacterDefinitionIds, CityStyleDefinitionIds, SpecialActionIds, FacilityDefinitionIds })
                if (values == null || values.Any(string.IsNullOrWhiteSpace) || values.Distinct().Count() != values.Count)
                    throw new ArgumentException("选择优先级必须是唯一明确的内容ID。");
            if (CharacterUseModes == null || CharacterUseModes.Any(p => p == null || string.IsNullOrWhiteSpace(p.DefinitionId) ||
                !new[] { CharacterEffectModes.Strategy, CharacterEffectModes.Tactic, CharacterEffectModes.Both }.Contains(p.Mode)))
                throw new ArgumentException("角色优先级须引用正式模式。");
        }
        public static int Rank(IList<string> priorities, string id) { int index = priorities.IndexOf(id); return index < 0 ? int.MaxValue : index; }
    }
    [Serializable]
    public sealed class ProductionGameCharacterModePriority { public string DefinitionId = "", Mode = ""; }

    [Serializable]
    public sealed class ProductionGameRecoveryCheckpoint
    {
        public string CheckpointId = "", Phase = "", RequestKind = "", PromptKey = "", SettlementLocalId = "";
        public int PlayerId, Round;
        public bool RequireSettlementWindow, RequirePayment;
        public bool OptionalIfUnavailable;
        public bool Matches(GameStateView view)
        {
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != PlayerId ||
                (Round == 0 && (view.Round < 1 || view.Round > 8)) ||
                (Round != 0 && view.Round != Round) || view.Phase.ToString() != Phase) return false;
            if (!view.Interactions.Any(r => r.Status == "open" && r.AnsweringPlayerId == PlayerId && r.Kind == RequestKind &&
                (PromptKey.Length == 0 || r.PromptKey == PromptKey))) return false;
            if (RequireSettlementWindow && string.IsNullOrEmpty(view.ActiveSettlementWindowId)) return false;
            return SettlementLocalId.Length == 0 || view.CharacterSettlementOptions.Any(o => o.AnsweringPlayerId == PlayerId && o.VisibleToViewer && o.LocalId == SettlementLocalId);
        }
    }

    [Serializable]
    public sealed class ProductionGameRouteStep
    {
        public string StepId = "", Phase = "", CommandKind = "", RequestKind = "", PromptKey = "";
        public int PlayerId, Round, ActionRound;
        public bool ExpectRejection;
        public bool OptionalIfUnavailable, UseAvailableCandidateFallback, ChooseLegalMainAction;
        public string EndActionCaseKind = "";
        public string ExpectedErrorCode = "";
        public string SourceDefinitionId = "", SourceId = "", TargetId = "", AnswerValue = "";
        public string SettlementLocalId = "", SettlementSourceDefinitionId = "";
        public List<string> CandidateIds = new List<string>(), CandidateDefinitionIds = new List<string>();
        public Dictionary<string, string> Parameters = new Dictionary<string, string>();
    }

    [Serializable]
    public sealed class ProductionGameEndActionCase
    {
        public string CaseId = "", StepId = "", Kind = "";
        public int PlayerId;
        public bool OptionalIfUnavailable;
    }

    [Serializable]
    public sealed class ProductionGameCoverageRequirement
    {
        public string CoverageId = "", DefinitionId = "", AbilityId = "", EffectTypeId = "", Branch = "";
        // 只支持在真实效果 Result 或真实归档状态中核对 JSON 字段；不执行脚本。
        public string ResultPath = "", ResultEqualsJson = "", StatePath = "", StateEqualsJson = "", BeforeEqualsJson = "";
        public string Category = "key-branch", EvidenceKind = "state-change", TargetResultPath = "";
        public bool Required;
        public List<ProductionGameEvidenceClause> Clauses = new List<ProductionGameEvidenceClause>();
    }

    [Serializable]
    public sealed class ProductionGameEvidenceClause
    {
        public string Role = "", LeftScope = "", LeftPath = "", Operator = "equals";
        public string RightScope = "literal", RightPath = "", RightJson = "";
        public string BeforeScope = "", BeforePath = "";
    }

    public sealed class ProductionGameRoutePolicy
    {
        private readonly ProductionGameRoutePlan plan;
        private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> SkippedSteps => skipped;
        private string pendingCommandId, pendingStepId;
        private string pendingGameId;
        private GameCommandDto pendingSubmission;
        private ProductionGameRouteStep pendingStep;
        private InteractionRefresh pendingInteraction, staleRefresh;
        private readonly Func<double> monotonicSeconds;
        private readonly Dictionary<string, int> refreshAttempts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, InteractionRefresh> retiredStaleCommands = new Dictionary<string, InteractionRefresh>(StringComparer.Ordinal);
        private const int MaximumStaleRefreshes = 3;
        private const double StaleRefreshWaitSeconds = 10;
        private const double EntranceRefreshQuietSeconds = 1;
        private const string StaleInteractionReason = "交互已过期，请刷新当前交互。";
        private sealed class InteractionRefresh
        {
            public string GameId, InteractionId, Kind, Contract, AttemptKey, Status, NewCommandId;
            public GamePhase Phase;
            public int PlayerId, Round, Revision, Attempts, NewRevision;
            public long? PromptRevision, NewPromptRevision;
            public double StartedAt;
            public int CoalescedRevision;
            public double CoalescedRevisionObservedAt;
            public bool Waiting;
            public GameCommandDto Command;
        }
        public IEnumerable<string> CompletedSteps => completed.OrderBy(id => id, StringComparer.Ordinal);
        public bool HasPendingCommand => pendingCommandId != null;
        public string PendingStepId => pendingStepId;
        public string PendingCommandId => pendingCommandId;
        public bool IsWaitingForStaleInteractionRefresh => staleRefresh?.Waiting == true;
        public object StaleInteractionRefreshEvidence => staleRefresh == null ? null : new {
            staleRefresh.GameId, staleRefresh.PlayerId, staleRefresh.Round, staleRefresh.Phase, staleRefresh.Kind, staleRefresh.InteractionId,
            rejectedCommandId = staleRefresh.Command.CommandId, rejectedRevision = staleRefresh.Revision,
            staleRefresh.NewCommandId, staleRefresh.NewRevision, staleRefresh.Attempts,
            staleRefresh.PromptRevision, staleRefresh.NewPromptRevision,
            staleRefresh.CoalescedRevision, staleRefresh.CoalescedRevisionObservedAt,
            staleRefresh.Status, staleRefresh.Waiting, requestContract = staleRefresh.Contract
        };
        public ProductionGameRoutePolicy(ProductionGameRoutePlan plan, Func<double> monotonicSeconds = null)
        {
            this.plan = plan; plan.Validate();
            this.monotonicSeconds = monotonicSeconds ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        }

        public void Confirm(string commandId)
        {
            if (commandId == null || commandId != pendingCommandId) return;
            if (pendingStep?.ExpectRejection == true) throw new InvalidOperationException("本应拒绝的路线命令被Host接受：" + pendingStep.StepId);
            if (pendingStepId != null) completed.Add(pendingStepId);
            if (staleRefresh?.NewCommandId == commandId) staleRefresh.Status = "新命令已真实确认";
            pendingCommandId = pendingStepId = null;
            pendingStep = null; pendingInteraction = null; pendingSubmission = null; pendingGameId = null;
        }

        // 新协议只消费真实本地执行；Host 前缀单独审计，不解除未知等待。
        public bool ApplyLocalResult(string gameId, GameCommandDto command, bool succeeded)
        {
            if (!succeeded || !MatchesPendingSubmission(gameId, command)) return false;
            Confirm(command.CommandId);
            return true;
        }

        public bool RetireUnappliedSubmission(string gameId, GameCommandDto command, string errorCode)
        {
            if (!MatchesPendingSubmission(gameId, command)) return false;
            if (pendingStep?.ExpectRejection == true && pendingStep.ExpectedErrorCode == errorCode)
                return Reject(command.CommandId, errorCode);
            // 拒绝/恢复退役不是成功；后继 Decide 重新读取当前本人候选，不重签旧答案。
            pendingCommandId = pendingStepId = null; pendingStep = null; pendingInteraction = null;
            pendingSubmission = null; pendingGameId = null; staleRefresh = null;
            return true;
        }

        private bool MatchesPendingSubmission(string gameId, GameCommandDto command) =>
            command != null && pendingSubmission != null && pendingCommandId == command.CommandId &&
            pendingGameId == gameId && CommandIdentity(command) == CommandIdentity(pendingSubmission);

        public bool Reject(string commandId, string errorCode)
        {
            if (commandId != pendingCommandId || pendingStep?.ExpectRejection != true || pendingStep.ExpectedErrorCode != errorCode) return false;
            completed.Add(pendingStep.StepId); pendingCommandId = pendingStepId = null; pendingStep = null; pendingInteraction = null;
            pendingSubmission = null; pendingGameId = null;
            return true;
        }

        public bool TryBeginStaleInteractionRefresh(RejectedGameCommandDto rejected, GameStateView visible, out string reason)
        {
            reason = "";
            if (pendingInteraction == null || pendingStepId != null || pendingStep != null ||
                pendingCommandId != pendingInteraction.Command.CommandId ||
                !IsExactStaleRejection(rejected, pendingInteraction.Command)) return false;
            ValidateRefreshProjection(pendingInteraction, visible);
            var request = visible.Interactions.Single(r => r.InteractionId == pendingInteraction.InteractionId);
            if (visible.StateRevision < pendingInteraction.Revision || request.StateRevision < pendingInteraction.Revision)
                throw new InvalidOperationException("交互刷新收到早于原提交的投影。");
            staleRefresh = pendingInteraction;
            int attempts = refreshAttempts.TryGetValue(staleRefresh.AttemptKey, out int count) ? count + 1 : 1;
            refreshAttempts[staleRefresh.AttemptKey] = attempts;
            staleRefresh.Attempts = attempts;
            if (attempts > MaximumStaleRefreshes)
            {
                staleRefresh.Status = "同一交互请求刷新次数耗尽";
                throw new InvalidOperationException(staleRefresh.Status);
            }
            staleRefresh.StartedAt = monotonicSeconds(); staleRefresh.Waiting = true;
            staleRefresh.CoalescedRevision = -1; staleRefresh.CoalescedRevisionObservedAt = staleRefresh.StartedAt;
            staleRefresh.Status = reason = "等待同一交互请求的新授权投影";
            retiredStaleCommands.Add(staleRefresh.Command.CommandId, staleRefresh);
            // 只释放已准确匹配的被拒命令；不计入任何计划步骤完成。
            pendingCommandId = pendingStepId = null; pendingStep = null; pendingInteraction = null;
            return true;
        }

        public bool IsRetiredStaleInteractionRejection(RejectedGameCommandDto rejected, GameStateView visible)
        {
            return rejected?.Command != null && retiredStaleCommands.TryGetValue(rejected.Command.CommandId, out var retired) &&
                visible?.ViewerRole == GameStateViewerRole.Player && visible.ViewerPlayerId == retired.PlayerId &&
                visible.GameId == retired.GameId && IsExactStaleRejection(rejected, retired.Command);
        }

        public void CheckStaleInteractionRefreshTimeout()
        {
            if (IsWaitingForStaleInteractionRefresh && monotonicSeconds() - staleRefresh.StartedAt >= StaleRefreshWaitSeconds)
            {
                if (plan.EvaluationMode == "whole-game-health")
                {
                    staleRefresh.Status = "等待交互新授权投影已超过观察阈值，继续等待";
                    return;
                }
                staleRefresh.Status = "等待交互新授权投影超时";
                throw new InvalidOperationException(staleRefresh.Status);
            }
        }

        private static bool IsExactStaleRejection(RejectedGameCommandDto rejected, GameCommandDto submitted)
        {
            if (submitted == null || rejected?.Command == null ||
                !(rejected.ErrorCode == CommandErrorCode.StateRevisionMismatch ||
                    rejected.ErrorCode == CommandErrorCode.InvalidTarget && rejected.Reason == StaleInteractionReason) ||
                rejected.Command.Parameters == null ||
                rejected.Command.Parameters.Any(p => p == null) ||
                rejected.Command.Parameters.Select(p => p.Key).Distinct().Count() != rejected.Command.Parameters.Count) return false;
            return CommandIdentity(rejected.Command) == CommandIdentity(submitted);
        }

        private static string CommandIdentity(GameCommandDto command) => JsonConvert.SerializeObject(new {
            command.CommandId, command.Kind, command.PlayerId, command.SourceId, command.TargetId, command.OptionIds,
            parameters = command.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value })
        });

        private static string InteractionContract(GameStateView view, InteractionView request) => JsonConvert.SerializeObject(new {
            request.InteractionId, request.SourceNodeId, request.OwnerEffectId, request.Kind, request.AnsweringPlayerId,
            request.Visibility, request.PromptKey, promptParameters = StableInteractionPrompt(request),
            requestRevisionKind = request.PromptParameters?.Properties.FirstOrDefault(p => p.Name == "requestRevision")?.Value?.Kind,
            request.CandidateSetId, request.CandidateSetVersion,
            request.CandidateIds, request.MinSelections, request.MaxSelections, request.AllowDecline, request.AnswerSchema,
            owner = RefreshOwnerContract(view, request),
            entranceSource = EntranceSourceChain(view, request)?.Select(node => new { node.EffectId, node.ParentEffectId,
                node.EffectTypeId, node.PlayerId, node.NormalizedArguments }),
            options = view.CharacterSettlementOptions.Where(o => o.InteractionId == request.InteractionId)
                .OrderBy(o => o.RegistrationSequence).Select(o => new { o.OptionId, o.WindowId, o.LocalId, o.EntryKind,
                    o.TargetEffectId, o.TargetUseId, o.RegistrationSequence, o.SettlementState, o.CanDeclineWholeEffect })
        });

        private static NormalizedValue StableInteractionPrompt(InteractionView request)
        {
            var prompt = request.PromptParameters?.Clone();
            // 正式并发请求只刷新这个版本字段；其余预算、来源、事实摘要和候选完整比较。
            prompt?.Properties.RemoveAll(p => p.Name == "requestRevision");
            return prompt;
        }

        private static long? InteractionPromptRevision(InteractionView request)
        {
            var value = request.PromptParameters?.Properties.FirstOrDefault(p => p.Name == "requestRevision")?.Value;
            return value?.Kind == NormalizedValueKind.Integer ? value.IntegerValue : (long?)null;
        }

        private static EffectNodeView RefreshOwner(GameStateView view, InteractionView request) => view.Effects
            .SingleOrDefault(node => node.EffectId == request.OwnerEffectId && node.PlayerId == request.AnsweringPlayerId);

        private static object RefreshOwnerContract(GameStateView view, InteractionView request)
        {
            var owner = RefreshOwner(view, request);
            return owner == null ? null : new { owner.EffectId, owner.ParentEffectId, owner.EffectTypeId, owner.PlayerId,
                owner.NormalizedArguments };
        }

        private static bool IsRefreshableInteraction(GameStateView view, InteractionView request)
        {
            if (request == null || request.Status != "open" || request.AnsweringPlayerId != view.ViewerPlayerId ||
                string.IsNullOrEmpty(request.InteractionId)) return false;
            if (string.IsNullOrEmpty(request.OwnerEffectId) || string.IsNullOrEmpty(request.SourceNodeId)) return false;
            if (view.Phase != GamePhase.ResourceCollection && view.Phase != GamePhase.Entrance && view.Phase != GamePhase.CharacterCover) return false;
            var owner = RefreshOwner(view, request);
            if (owner == null) return false;
            if (view.Phase == GamePhase.ResourceCollection && request.Kind == "route_resource.selection")
            {
                var purpose = request.PromptParameters?.Properties.FirstOrDefault(value => value.Name == "purpose")?.Value;
                var ownerPurpose = owner.NormalizedArguments?.Properties.FirstOrDefault(value => value.Name == "purpose")?.Value;
                return owner.EffectTypeId == "effect.route_resource.select" &&
                    purpose?.Kind == NormalizedValueKind.Integer && purpose.IntegerValue == (int)RouteSelectionPurpose.NormalHarvest &&
                    ownerPurpose?.Kind == NormalizedValueKind.Integer && ownerPurpose.IntegerValue == (int)RouteSelectionPurpose.NormalHarvest;
            }
            if (view.Phase == GamePhase.Entrance && request.Kind == "setup.enterprise.choose")
                return owner.EffectTypeId == "setup.enterprise.select";
            if (view.Phase == GamePhase.Entrance && request.Kind == "entrance.location")
                return view.ViewerRole == GameStateViewerRole.Player && owner.EffectTypeId == "flow.player.enter" &&
                    request.SourceNodeId == owner.EffectId && !string.IsNullOrEmpty(owner.ParentEffectId) &&
                    view.Interactions.Count(r => r.InteractionId == request.InteractionId) == 1;
            if (view.Phase == GamePhase.Entrance && new[] { "event_card.option", "event_card.influence_targets",
                "setup.basic_character.discard", "setup.influence.optional" }.Contains(request.Kind))
                return view.Interactions.Count(r => r.InteractionId == request.InteractionId) == 1 && EntranceSourceChain(view, request) != null;
            if (view.Phase != GamePhase.CharacterCover || request.Kind != "character.cover.card" ||
                owner.EffectTypeId != "character.cover.task") return false;
            var version = owner.NormalizedArguments?.Properties.FirstOrDefault(value => value.Name == "apiVersion")?.Value;
            var mode = owner.NormalizedArguments?.Properties.FirstOrDefault(value => value.Name == "coverMode")?.Value;
            return !(version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 2) &&
                !(mode?.Kind == NormalizedValueKind.String && mode.StringValue == "additional");
        }

        private static string ArgumentText(EffectNodeView node, string key) =>
            node.NormalizedArguments?.Properties.FirstOrDefault(p => p.Name == key)?.Value?.StringValue ?? "";
        private static bool EntranceProtocol(EffectNodeView node)
        {
            var version = node.NormalizedArguments?.Properties.FirstOrDefault(p => p.Name == "setupProtocolVersion")?.Value;
            return version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 2;
        }
        private static List<EffectNodeView> EntranceSourceChain(GameStateView view, InteractionView request)
        {
            if (view.Phase != GamePhase.Entrance || !new[] { "event_card.option", "event_card.influence_targets",
                "setup.basic_character.discard", "setup.influence.optional" }.Contains(request.Kind)) return null;
            var owner = RefreshOwner(view, request);
            string expected = request.Kind.StartsWith("event_card.", StringComparison.Ordinal) ? "effect.event_card.resolve" :
                request.Kind == "setup.basic_character.discard" ? "setup.basic_character.retain" : "setup.initial_influence";
            if (owner == null || owner.EffectTypeId != expected || request.SourceNodeId != owner.EffectId) return null;
            var chain = new List<EffectNodeView> { owner }; var visited = new HashSet<string> { owner.EffectId };
            var node = owner;
            while (!string.IsNullOrEmpty(node.ParentEffectId))
            {
                var parents = view.Effects.Where(p => p.EffectId == node.ParentEffectId).ToList();
                if (parents.Count != 1 || !visited.Add(parents[0].EffectId) || !parents[0].ChildEffectIds.Contains(node.EffectId)) return null;
                node = parents[0]; chain.Add(node);
                if (node.EffectTypeId == "round.mainline.node") break;
                if (node.PlayerId != view.ViewerPlayerId) return null;
            }
            var task = chain.FirstOrDefault(n => n.EffectTypeId == "setup.player.entrance");
            var main = chain.Last();
            if (task == null || !EntranceProtocol(task) || task.ParentEffectId != main.EffectId ||
                main.EffectTypeId != "round.mainline.node" || !EntranceProtocol(main) ||
                main.PlayerId != -1 || !string.IsNullOrEmpty(main.ParentEffectId) ||
                ArgumentText(main, "nodeTypeId") != "player_entrance_setup" ||
                string.IsNullOrEmpty(ArgumentText(main, "mainNodeId")) || ArgumentText(task, "mainNodeId") != ArgumentText(main, "mainNodeId") ||
                !string.IsNullOrEmpty(view.ActiveMainNodeId) && ArgumentText(main, "mainNodeId") != view.ActiveMainNodeId ||
                request.Kind.StartsWith("event_card.", StringComparison.Ordinal) && !chain.Any(n => n.EffectTypeId == "flow.player.enter")) return null;
            return chain;
        }

        private static void ValidateRefreshProjection(InteractionRefresh refresh, GameStateView view)
        {
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != refresh.PlayerId ||
                view.GameId != refresh.GameId || view.Round != refresh.Round || view.Phase != refresh.Phase)
                throw new InvalidOperationException("交互刷新局、玩家或回合阶段已改变。");
            var requests = view.Interactions.Where(r => r.InteractionId == refresh.InteractionId).ToList();
            if (requests.Count != 1 || requests[0].Status != "open" || requests[0].AnsweringPlayerId != refresh.PlayerId ||
                requests[0].Kind != refresh.Kind || !IsRefreshableInteraction(view, requests[0]) ||
                InteractionContract(view, requests[0]) != refresh.Contract)
                throw new InvalidOperationException("交互刷新请求已关闭、消失或来源候选合同改变。");
            long? promptRevision = InteractionPromptRevision(requests[0]);
            if (promptRevision < refresh.PromptRevision || promptRevision > requests[0].StateRevision)
                throw new InvalidOperationException("交互刷新提示版本回退或超过正式请求版本。");
        }

        public GameCommand Decide(GameStateView view, int player, out string reason)
        {
            reason = "";
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != player)
                throw new InvalidOperationException("路线只能读取本人玩家投影。");
            if (HasPendingCommand) return null;
            InteractionView refreshedRequest = null;
            if (IsWaitingForStaleInteractionRefresh)
            {
                CheckStaleInteractionRefreshTimeout();
                ValidateRefreshProjection(staleRefresh, view);
                refreshedRequest = view.Interactions.Single(r => r.InteractionId == staleRefresh.InteractionId);
                if (view.StateRevision < staleRefresh.Revision || refreshedRequest.StateRevision < staleRefresh.Revision)
                    throw new InvalidOperationException("交互刷新收到早于原提交的投影。");
                if (view.StateRevision != refreshedRequest.StateRevision || view.StateRevision <= staleRefresh.Revision)
                { reason = "等待同一交互请求的新授权投影"; return null; }
                if (plan.EvaluationMode == "whole-game-health" && view.Phase == GamePhase.Entrance)
                {
                    double now = monotonicSeconds();
                    if (staleRefresh.CoalescedRevision != view.StateRevision)
                    {
                        staleRefresh.CoalescedRevision = view.StateRevision;
                        staleRefresh.CoalescedRevisionObservedAt = now;
                    }
                    // 只合并准确拒绝后的连续授权版本；保留原意图、三次重建和原十秒等待上限。
                    if (now - staleRefresh.CoalescedRevisionObservedAt < EntranceRefreshQuietSeconds ||
                        now - staleRefresh.StartedAt < 1 + 0.25 * player)
                    { staleRefresh.Status = reason = "合并入场版本变化，等待最新授权投影短暂稳定"; return null; }
                }
            }
            foreach (var expired in plan.Steps.Where(s => s.PlayerId == player && !completed.Contains(s.StepId) && !skipped.ContainsKey(s.StepId) && s.OptionalIfUnavailable && s.Round > 0 && view.Round > s.Round))
                skipped.Add(expired.StepId, "目标未在计划回合自然出现");
            var step = plan.Steps.FirstOrDefault(s => s.PlayerId == player && !completed.Contains(s.StepId) && !skipped.ContainsKey(s.StepId));
            if (step != null && step.Round > 0 && view.Round > step.Round)
                throw new InvalidOperationException("路线目标未自然出现：" + step.StepId);
            bool inWindow = step != null && step.Phase == view.Phase.ToString() &&
                (step.Round == 0 || step.Round == view.Round) && (step.ActionRound == 0 || step.ActionRound == view.ActionRound);
            var requests = view.Interactions.Where(r => r.Status == "open" && r.AnsweringPlayerId == player &&
                !r.Kind.StartsWith(InteractionRequest.MainlineCompletionInteractionPrefix, StringComparison.Ordinal) && r.Kind != "collection.task").ToList();
            var request = refreshedRequest ?? (inWindow && (step.CommandKind == nameof(GameCommandKind.AnswerInteraction) || step.CommandKind == nameof(GameCommandKind.PreparePaymentDraft))
                ? requests.FirstOrDefault(r => (step.RequestKind.Length == 0 || r.Kind == step.RequestKind) &&
                    (step.PromptKey.Length == 0 || r.PromptKey == step.PromptKey)) : requests.FirstOrDefault());
            GameCommand command = null;
            string selectedStep = null;
            if (refreshedRequest != null)
            {
                // 只重建已被准确拒绝的同一意图，不重新作选卡、路线或付款决策。
                command = staleRefresh.Command.ToCommand();
                command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] =
                    refreshedRequest.StateRevision.ToString(CultureInfo.InvariantCulture);
            }
            else if (inWindow && step.CommandKind == nameof(GameCommandKind.PreparePaymentDraft))
            {
                if (request == null) { reason = "等待本人正式付款选择请求：" + step.StepId; return null; }
                if (request.Kind != "effect.resource.payment_choice" || string.IsNullOrEmpty(request.OwnerEffectId) ||
                    step.CandidateIds.Count != 1 || !request.CandidateIds.Contains(step.CandidateIds[0]))
                    throw new InvalidOperationException("费用草稿只能选择同一正式付款请求中的一个候选。");
                command = new GameCommand { PlayerId = player, Kind = GameCommandKind.PreparePaymentDraft };
                command.Parameters["ownerEffectId"] = request.OwnerEffectId;
                command.Parameters["interactionId"] = request.InteractionId;
                command.Parameters["candidateId"] = step.CandidateIds[0];
                command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
                selectedStep = step.StepId;
            }
            else if (inWindow && step.CommandKind == nameof(GameCommandKind.EndAction))
            {
                if (view.CurrentPlayerId != player) return null;
                command = new GameCommand { PlayerId = player, Kind = GameCommandKind.EndAction, Parameters = new Dictionary<string, string>(step.Parameters) };
                if (!string.IsNullOrEmpty(view.ActiveSettlementWindowId))
                {
                    // 两级结束必须消费Host的独立投影；不能按可选项、牌名或AllowDecline猜整窗权限。
                    var ending = view.SettlementEndAction;
                    if (ending == null || ending.WindowId != view.ActiveSettlementWindowId || ending.StateRevision != view.StateRevision)
                    { reason = "等待Host结束行动投影：" + step.StepId; return null; }
                    bool currentNode = !string.IsNullOrEmpty(ending.CurrentNodeEffectId);
                    bool predicate = step.EndActionCaseKind.Length == 0 ||
                        step.EndActionCaseKind == "mandatory-idle-rejected" && !currentNode && !ending.CanCloseCleanupWindow && ending.CloseCleanupWindowReasonCode == "SettlementRequiredPending" ||
                        step.EndActionCaseKind == "active-node-only" && currentNode && ending.CanStopCurrentNode ||
                        step.EndActionCaseKind == "optional-window-declined" && !currentNode && ending.CanCloseCleanupWindow && view.CharacterSettlementOptions.Any(o => o.AnsweringPlayerId == player && o.SettlementState == "available" && o.CanDeclineWholeEffect);
                    if (!predicate && step.OptionalIfUnavailable) { skipped.Add(step.StepId, "本轮没有对应的真实结束权限边界"); return Decide(view, player, out reason); }
                    if (!step.ExpectRejection && !(currentNode ? ending.CanStopCurrentNode : ending.CanCloseCleanupWindow))
                    { reason = currentNode ? ending.StopCurrentNodeReasonCode : ending.CloseCleanupWindowReasonCode; return null; }
                    command.Parameters["windowId"] = ending.WindowId;
                    command.Parameters["currentNodeEffectId"] = ending.CurrentNodeEffectId;
                    command.Parameters["interactionId"] = ending.InteractionId;
                    command.Parameters["expectedRevision"] = ending.StateRevision.ToString(CultureInfo.InvariantCulture);
                }
                else if (step.EndActionCaseKind.Length > 0 && step.OptionalIfUnavailable) { skipped.Add(step.StepId, "本轮未出现对应结算窗口"); return Decide(view, player, out reason); }
                else if (requests.Count > 0) { reason = "当前请求须先完成：" + step.StepId; return null; }
                selectedStep = step.StepId;
            }
            else if (inWindow && step.CommandKind == nameof(GameCommandKind.InvokeSettlementOption))
            {
                var option = view.CharacterSettlementOptions.Where(o => o.AnsweringPlayerId == player && o.VisibleToViewer && o.IsEnabled &&
                    o.LocalId == step.SettlementLocalId && (step.SettlementSourceDefinitionId.Length == 0 ||
                        o.SourceDefinitionId == step.SettlementSourceDefinitionId))
                    .OrderBy(o => o.RegistrationSequence).FirstOrDefault();
                if (option == null)
                {
                    if (step.OptionalIfUnavailable) { skipped.Add(step.StepId, "当前合法结算候选没有目标"); return Decide(view, player, out reason); }
                    reason = "等待Host结算入口：" + step.StepId; return null;
                }
                command = EffectInteractionCommands.InvokeSettlementOption(player, option.OptionId, option.TargetUseId, option.StateRevision);
                selectedStep = step.StepId;
            }
            else if (request != null)
            {
                var insertion = plan.UseOptionalInsertions ? view.CharacterSettlementOptions.Where(o => o.VisibleToViewer && o.IsEnabled &&
                    o.AnsweringPlayerId == player && o.EntryKind == "optional_insertion" && o.InteractionId == request.InteractionId)
                    .OrderBy(o => o.RegistrationSequence).FirstOrDefault() : null;
                if (insertion != null)
                    command = EffectInteractionCommands.InvokeSettlementOption(player, insertion.OptionId, insertion.TargetUseId, insertion.StateRevision);
                else
                {
                var answerStep = inWindow && step.CommandKind == nameof(GameCommandKind.AnswerInteraction) &&
                    (step.RequestKind.Length == 0 || step.RequestKind == request.Kind) &&
                    (step.PromptKey.Length == 0 || step.PromptKey == request.PromptKey) ? step : null;
                command = Answer(view, request, player, answerStep, out reason);
                if (command == null) return null;
                selectedStep = answerStep?.StepId;
                }
            }
            else if (view.WaitingForPlayerIds.Count > 0) return null;
            else if (inWindow && step.CommandKind != nameof(GameCommandKind.AnswerInteraction))
            {
                if (view.CurrentPlayerId != player && view.Phase != GamePhase.ResourceCollection) return null;
                if (step.ChooseLegalMainAction)
                {
                    command = LegalMainAction(view, player);
                    if (command == null) { reason = "当前没有规划的合法部署或调度：" + step.StepId; return null; }
                    selectedStep = step.StepId;
                }
                else
                {
                command = new GameCommand { PlayerId = player, Kind = (GameCommandKind)Enum.Parse(typeof(GameCommandKind), step.CommandKind),
                    TargetId = step.TargetId, Parameters = new Dictionary<string, string>(step.Parameters) };
                command.SourceId = step.SourceId;
                if (step.SourceDefinitionId.Length > 0)
                {
                    var source = view.ItemZones.Items.FirstOrDefault(i => i.DefinitionId == step.SourceDefinitionId &&
                        (view.Players.Single(p => p.PlayerId == player).HandCardIds.Contains(i.ItemId) ||
                         view.Characters.Any(c => c.ItemId == i.ItemId && c.ControllerPlayerId == player && c.CanUse)));
                    if (source == null)
                    {
                        if (step.OptionalIfUnavailable) { skipped.Add(step.StepId, "本人当前没有合法目标来源"); return Decide(view, player, out reason); }
                        reason = "等待本人可用来源：" + step.StepId; return null;
                    }
                    command.SourceId = source.ItemId;
                }
                command.OptionIds.AddRange(step.CandidateIds);
                if (command.Kind == GameCommandKind.UseCharacterCard && command.SourceId.Length > 0)
                    command.Parameters["cardId"] = command.SourceId;
                if (command.Kind == GameCommandKind.BuildFacility && command.SourceId.Length > 0)
                    command.Parameters["facilityId"] = command.SourceId;
                selectedStep = step.StepId;
                }
            }
            else if (view.Phase == GamePhase.ResourceCollection)
            {
                var own = view.Players.Single(p => p.PlayerId == player);
                if (!own.HasCollectedResourcesThisRound)
                {
                    command = new GameCommand { PlayerId = player, Kind = GameCommandKind.CollectResource };
                    if (own.CityLocationId.Length > 0) command.OptionIds.Add(own.CityLocationId);
                }
            }
            else if (string.IsNullOrEmpty(view.ActiveSettlementWindowId) && view.CurrentPlayerId == player && (view.Phase == GamePhase.Entrance ||
                (view.Phase == GamePhase.ActionRound1 || view.Phase == GamePhase.ActionRound2) &&
                view.Players.Single(p => p.PlayerId == player).ActedMainActionThisTurn))
                command = new GameCommand { PlayerId = player, Kind = GameCommandKind.EndAction };
            else if (plan.AllowAdaptiveMainActions && view.CurrentPlayerId == player)
            {
                if ((view.Phase == GamePhase.ActionRound1 || view.Phase == GamePhase.ActionRound2) && !view.Players.Single(p => p.PlayerId == player).ActedMainActionThisTurn)
                    command = LegalMainAction(view, player);
                else if (!string.IsNullOrEmpty(view.ActiveSettlementWindowId))
                {
                    var option = view.CharacterSettlementOptions.Where(o => o.VisibleToViewer && o.IsEnabled && o.AnsweringPlayerId == player).OrderBy(o => o.RegistrationSequence).FirstOrDefault();
                    if (option != null) command = EffectInteractionCommands.InvokeSettlementOption(player, option.OptionId, option.TargetUseId, option.StateRevision);
                    else if (view.SettlementEndAction?.CanCloseCleanupWindow == true && view.SettlementEndAction.StateRevision == view.StateRevision)
                    {
                        command = new GameCommand { PlayerId = player, Kind = GameCommandKind.EndAction };
                        command.Parameters["windowId"] = view.SettlementEndAction.WindowId;
                        command.Parameters["expectedRevision"] = view.StateRevision.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            if (command == null) { reason = step == null ? "缺少主行动路线" : "等待路线窗口：" + step.StepId; return null; }
            if (refreshedRequest != null && (selectedStep != null || command.Kind != GameCommandKind.AnswerInteraction ||
                !command.Parameters.TryGetValue("interactionId", out string refreshedId) || refreshedId != staleRefresh.InteractionId))
                throw new InvalidOperationException("交互刷新无法沿原自动回答路径重建命令。");
            command.CommandId = Guid.NewGuid().ToString("N");
            pendingCommandId = command.CommandId; pendingStepId = selectedStep;
            pendingGameId = view.GameId; pendingSubmission = GameCommandDto.FromCommand(command);
            pendingStep = selectedStep == null ? null : step;
            pendingInteraction = selectedStep == null && command.Kind == GameCommandKind.AnswerInteraction &&
                IsRefreshableInteraction(view, request) &&
                request.StateRevision == view.StateRevision && request.AnsweringPlayerId == player && request.Status == "open"
                ? new InteractionRefresh { GameId = view.GameId, PlayerId = player, Round = view.Round, Phase = view.Phase, Kind = request.Kind,
                    InteractionId = request.InteractionId, Revision = request.StateRevision, Contract = InteractionContract(view, request),
                    PromptRevision = InteractionPromptRevision(request),
                    AttemptKey = JsonConvert.SerializeObject(new { view.GameId, player, view.Round, request.InteractionId }),
                    Command = GameCommandDto.FromCommand(command) } : null;
            if (refreshedRequest != null)
            {
                staleRefresh.Waiting = false; staleRefresh.NewCommandId = command.CommandId;
                staleRefresh.NewRevision = view.StateRevision; staleRefresh.Status = "已从新授权投影重建，等待真实确认";
                staleRefresh.NewPromptRevision = InteractionPromptRevision(refreshedRequest);
            }
            return command;
        }

        private GameCommand LegalMainAction(GameStateView view, int player)
        {
            // 只在本人已授权投影的客户端状态上作正式规则查询；Host仍重新校验命令。
            var state = GameStateViewProjector.ToClientState(view);
            var priorities = plan.SelectionPriorities;
            if (plan.EvaluationMode == "whole-game-health")
            {
                var matcher = new CityStylePatternMatcher(); var declaration = new DeclareCityStyleService();
                foreach (string id in state.Decks.CityStyleSupply.OrderBy(id => ProductionGameSelectionPriorities.Rank(priorities.CityStyleDefinitionIds, DefinitionIdFor(state, id))))
                {
                    var definition = CityStyleDatabase.Get(state, id); if (definition == null) continue;
                    var match = matcher.Match(state, player, definition);
                    if (!match.Succeeded || !declaration.Validate(state, player, id, match.UsedCityBoardSlotIndexes).IsValid) continue;
                    return new GameCommand { PlayerId = player, Kind = GameCommandKind.DeclareCityStyle,
                        Parameters = new Dictionary<string, string> { { "cityStyleId", id }, { "usedCityBoardSlotIndexes", string.Join(",", match.UsedCityBoardSlotIndexes) } } };
                }
                var map = new MapQueryService(StaticMapDefinitions.Resolve(view.MapId));
                var influenceQuery = new InfluenceService(map);
                var query = new SpecialActionOptionQueryService(map, influenceQuery, new CityMovementService(map, influenceQuery, new TravelCostService(map)), new SpecialActionLifecycleService());
                var special = query.Query(state, player).Options.Where(o => o.CanUse)
                    .OrderBy(o => ProductionGameSelectionPriorities.Rank(priorities.SpecialActionIds, o.SpecialActionId)).FirstOrDefault();
                if (special != null && (priorities.SpecialActionIds.Contains(special.SpecialActionId) || CharacterLifecycleService.GetActionRoundSequence(state) % 3 == 0)) return new GameCommand { PlayerId = player, Kind = GameCommandKind.UseSpecialAction,
                    Parameters = new Dictionary<string, string> { { "specialActionId", special.SpecialActionId }, { "declarationMarkerId", special.DeclarationMarkerId } } };
                // 当前正式建设只读查询决定可行性；优先级不能制造来源或支付能力。
                var build = new BuildFacilityService();
                var building = build.WithReadOnlyQuery(state, player, null, q => state.Decks.FacilitySupply
                    .OrderBy(id => ProductionGameSelectionPriorities.Rank(priorities.FacilityDefinitionIds,
                        view.ItemZones.Items.FirstOrDefault(i => i.ItemId == id)?.DefinitionId ?? id))
                    .Where(id => q.ValidateFacility(id).IsValid)
                    .Select(id => new { id, paymentMode = new[] { BuildFacilityService.PaymentModeResources, BuildFacilityService.PaymentModeGold }
                        .FirstOrDefault(mode => q.ValidatePaymentMode(id, mode).IsValid) })
                    .Where(candidate => candidate.paymentMode != null)
                    .SelectMany(candidate => Enumerable.Range(0, BuildFacilityService.CityBoardSlotCount).Where(slot => q.ValidateCityBoardSlot(candidate.id, slot).IsValid)
                        .Select(slot => new { candidate.id, candidate.paymentMode, slot, fit = CityStylePlacementFit(state, player, candidate.id, slot, priorities.CityStyleDefinitionIds) }))
                    .OrderByDescending(candidate => candidate.fit).FirstOrDefault());
                if (building != null && CharacterLifecycleService.GetActionRoundSequence(state) % 3 != 1)
                    return new GameCommand { PlayerId = player, Kind = GameCommandKind.BuildFacility,
                        Parameters = new Dictionary<string, string> { { "facilityId", building.id }, { "cityBoardSlotIndex", building.slot.ToString(CultureInfo.InvariantCulture) },
                            { YC.Application.Gameplay.BuildFacilityCommandHandler.PaymentModeParameter, building.paymentMode } } };
            }
            int actionSequence = CharacterLifecycleService.GetActionRoundSequence(state);
            // 取消的权威事实只约束本次行动轮；新 revision 或请求 ID 不代表主行动已推进。
            var cancelled = new HashSet<string>(view.CharacterUses.Where(use => use.ExecutingPlayerId == player &&
                use.ActionRoundSequence == actionSequence && use.SelectionCancelled && !use.SourceConsumed &&
                use.CompletedModes.Count == 0).Select(use => use.CardInstanceId), StringComparer.Ordinal);
            var character = view.Characters.Where(c => c.ControllerPlayerId == player && c.CanUse && !cancelled.Contains(c.ItemId))
                .OrderBy(c => Math.Min(ProductionGameSelectionPriorities.Rank(priorities.DispatchCharacterDefinitionIds, DefinitionIdFor(state, c.ItemId)),
                    priorities.CharacterUseModes.FindIndex(p => p.DefinitionId == DefinitionIdFor(state, c.ItemId)) < 0 ? int.MaxValue : priorities.CharacterUseModes.FindIndex(p => p.DefinitionId == DefinitionIdFor(state, c.ItemId))))
                .ThenBy(c => { int rank = plan.CandidatePriorityDefinitionIds.IndexOf(view.ItemZones.Items.FirstOrDefault(i => i.ItemId == c.ItemId)?.DefinitionId ?? ""); return rank < 0 ? int.MaxValue : rank; }).FirstOrDefault();
            if (character != null)
            {
                var use = new GameCommand { PlayerId = player, Kind = GameCommandKind.UseCharacterCard };
                use.Parameters["cardId"] = character.ItemId;
                return use;
            }
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(view.MapId)));
            foreach (var item in state.Map.Influences.Where(i => i.PlayerId == player))
            {
                string id = InfluenceIdentity.GetStableId(state, item);
                foreach (string target in influence.GetLegalMovementSlotIds(state, id))
                    if (influence.CanMoveAtomically(state, player, new[] { new InfluenceMoveRequest(id, target) }).IsValid)
                        return new GameCommand { PlayerId = player, Kind = GameCommandKind.DispatchInfluence, SourceId = id, TargetId = target };
            }
            string slot = influence.GetLegalPlacementSlotIds(state, player).FirstOrDefault();
            if (slot == null && cancelled.Count > 0)
                throw new InvalidOperationException("本次行动取消后没有其他规划的合法主行动；行动序号=" + actionSequence +
                    "，已取消角色=" + string.Join(",", cancelled.OrderBy(id => id, StringComparer.Ordinal)));
            return slot == null ? null : new GameCommand { PlayerId = player, Kind = GameCommandKind.DeployInfluence, TargetId = slot };
        }

        // 仅作合法建设候选的接近程度排序，不代替正式样式匹配或Host结算。
        private static string DefinitionIdFor(GameState state, string id) => state.ItemZones.Items.FirstOrDefault(item => item.ItemId == id)?.DefinitionId ?? id;
        internal static int CityStylePlacementFit(GameState state, int player, string facilityId, int slot, IList<string> priorityIds)
        {
            FacilityCardDefinition added;
            if (!FacilityCardDatabase.TryGet(state, facilityId, out added)) return 0;
            var owner = state.FindPlayer(player);
            var used = new HashSet<string>(owner.DeclaredCityStyles.SelectMany(d => d.UsedFacilityIds));
            var slots = state.Map.Facilities.Where(f => f.PlayerId == player && !used.Contains(f.ContentInstanceId))
                .Select(f => new { Slot = f.CityBoardSlotIndex, Card = FacilityCardDatabase.Get(state, f.FacilityCardId) })
                .Where(f => f.Card != null && f.Slot >= 0).ToList();
            slots.Add(new { Slot = slot, Card = added });
            int best = 0;
            foreach (string id in state.Decks.CityStyleSupply.OrderBy(id => ProductionGameSelectionPriorities.Rank(priorityIds, DefinitionIdFor(state, id))))
            {
                var requirement = CityStyleDatabase.Get(state, id)?.DeclarationRequirement;
                if (requirement == null) continue;
                var eligible = slots.Where(f => (requirement.RequiredCityBoardSlotIndexes.Count == 0 || requirement.RequiredCityBoardSlotIndexes.Contains(f.Slot)) &&
                    (requirement.RequiredResourceTypes.Count == 0 || requirement.RequiredResourceTypes.Any(r => f.Card.ResourceCost.Get(r) > 0))).ToList();
                if (!eligible.Any(f => f.Slot == slot)) continue;
                int fit = eligible.Count;
                if (requirement.RequireSameCityBoardRow) fit = eligible.Count(f => f.Slot / BuildFacilityService.CityBoardSlotCountPerRow == slot / BuildFacilityService.CityBoardSlotCountPerRow);
                if (requirement.RequiredPatternCells.Count > 0)
                {
                    fit = 0;
                    int columns = BuildFacilityService.CityBoardSlotCountPerRow, rows = BuildFacilityService.CityBoardSlotCount / columns;
                    for (int rotation = 0; rotation < 4; rotation++) for (int row = 0; row < rows; row++) for (int column = 0; column < columns; column++)
                    {
                        int matched = 0; bool includesAdded = false, valid = true;
                        foreach (var cell in requirement.RequiredPatternCells)
                        {
                            int dr = cell.RowOffset, dc = cell.ColumnOffset;
                            for (int turn = 0; turn < rotation; turn++) { int previous = dr; dr = dc; dc = -previous; }
                            int r = row + dr, c = column + dc;
                            if (r < 0 || r >= rows || c < 0 || c >= columns) { valid = false; break; }
                            int target = r * columns + c;
                            var facility = eligible.FirstOrDefault(f => f.Slot == target && (cell.AllowedFacilityColors.Count == 0 ||
                                cell.AllowedFacilityColors.Any(color => string.Equals(color.Trim(), f.Card.Color.Trim(), StringComparison.OrdinalIgnoreCase))));
                            if (facility != null) { matched++; includesAdded |= target == slot; }
                        }
                        if (valid && includesAdded) fit = Math.Max(fit, matched);
                    }
                }
                fit = fit * 10 + requirement.RequiredEffectTypes.Count(effect => eligible.Any(f => f.Card.EffectType == effect));
                string definitionId = DefinitionIdFor(state, id);
                if (fit > 0 && priorityIds.Contains(definitionId)) fit += 1000 - Math.Min(900, ProductionGameSelectionPriorities.Rank(priorityIds, definitionId));
                best = Math.Max(best, fit);
            }
            return best;
        }

        private GameCommand Answer(GameStateView view, InteractionView request, int player, ProductionGameRouteStep step, out string reason)
        {
            reason = "";
            var command = new GameCommand { PlayerId = player, Kind = GameCommandKind.AnswerInteraction };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.InteractionId;
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (step == null && (request.AnswerSchema == "bounded_amount" || request.AnswerSchema == ResourceAllocationAnswer.BoundedAnswerSchema))
            {
                var min = request.PromptParameters?.Properties.FirstOrDefault(p => p.Name == "minAmount")?.Value;
                if (min?.Kind != NormalizedValueKind.Integer) { reason = "Host未提供合法最小数量"; return null; }
                if (request.AnswerSchema == "bounded_amount") command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = min.IntegerValue.ToString(CultureInfo.InvariantCulture);
                else
                {
                    var max = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "maxAmount")?.Value;
                    var rows = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "rowMaximums")?.Value;
                    if (max?.Kind != NormalizedValueKind.Integer || rows?.Kind != NormalizedValueKind.Object) { reason = "Host资源分配界限缺失"; return null; }
                    long remaining = plan.UseOptionalInsertions && request.Kind == "effect.resource.payment_substitution_allocation" ? max.IntegerValue : min.IntegerValue;
                    var parts = new List<string>();
                    foreach (string id in request.CandidateIds)
                    {
                        var limit = rows.Properties.FirstOrDefault(p => p.Name == id)?.Value;
                        if (limit?.Kind != NormalizedValueKind.Integer) { reason = "Host资源行上限缺失"; return null; }
                        long selected = Math.Min(remaining, limit.IntegerValue); remaining -= selected;
                        parts.Add(id + "=" + selected.ToString(CultureInfo.InvariantCulture));
                    }
                    if (remaining != 0) { reason = "Host候选无法满足合法最小分配"; return null; }
                    string encoded = string.Join(",", parts);
                    var limits = rows.Properties.ToDictionary(p => p.Name, p => checked((int)p.Value.IntegerValue), StringComparer.Ordinal);
                    if (!ResourceAllocationAnswer.TryParseBounded(encoded, request.CandidateIds, checked((int)min.IntegerValue), checked((int)max.IntegerValue), limits, out _, out reason)) return null;
                    command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = encoded;
                }
                return command;
            }
            if (step == null && request.AnswerSchema == "resource_allocation")
            {
                string total = request.CandidateIds.FirstOrDefault(id => id.StartsWith("total:", StringComparison.Ordinal));
                string resource = request.CandidateIds.FirstOrDefault(id => Enum.GetNames(typeof(ResourceType)).Contains(id));
                if (total == null || resource == null || !int.TryParse(total.Substring(6), out int amount)) { reason = "Host固定分配总量或资源候选缺失"; return null; }
                string encoded = resource + "=" + amount.ToString(CultureInfo.InvariantCulture);
                if (!ResourceAllocationAnswer.TryParse(encoded, request.CandidateIds.Where(id => !id.StartsWith("total:", StringComparison.Ordinal)).ToList(), amount, out _, out reason)) return null;
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = encoded;
                return command;
            }
            if (step == null && new[] { "boolean", "boolean_accept", "bool" }.Contains(request.AnswerSchema))
            {
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = plan.EvaluationMode == "whole-game-health" || !request.AllowDecline ? "true" : "false";
                return command;
            }
            if (step != null && step.AnswerValue.Length > 0)
            {
                if (step.AnswerValue.Equals("false", StringComparison.OrdinalIgnoreCase) && !request.AllowDecline)
                    throw new InvalidOperationException("Host未开放整体放弃：" + step.StepId);
                if (request.AnswerSchema == "bounded_amount")
                {
                    var min = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "minAmount")?.Value;
                    var max = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "maxAmount")?.Value;
                    if (min?.Kind != NormalizedValueKind.Integer || max?.Kind != NormalizedValueKind.Integer ||
                        !long.TryParse(step.AnswerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) ||
                        amount < min.IntegerValue || amount > max.IntegerValue) throw new InvalidOperationException("数量超出Host当前范围。");
                }
                if (request.AnswerSchema == ResourceAllocationAnswer.BoundedAnswerSchema && !step.AnswerValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    var min = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "minAmount")?.Value;
                    var max = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "maxAmount")?.Value;
                    var rows = request.PromptParameters.Properties.FirstOrDefault(p => p.Name == "rowMaximums")?.Value;
                    if (min?.Kind != NormalizedValueKind.Integer || max?.Kind != NormalizedValueKind.Integer || rows?.Kind != NormalizedValueKind.Object ||
                        rows.Properties.Any(p => p.Value.Kind != NormalizedValueKind.Integer)) throw new InvalidOperationException("Host资源分配范围缺失。");
                    var limits = rows.Properties.ToDictionary(p => p.Name, p => checked((int)p.Value.IntegerValue), StringComparer.Ordinal);
                    if (!ResourceAllocationAnswer.TryParseBounded(step.AnswerValue, request.CandidateIds, checked((int)min.IntegerValue), checked((int)max.IntegerValue), limits, out _, out string diagnostic))
                        throw new InvalidOperationException(diagnostic);
                }
                if (request.AnswerSchema == "resource_allocation" && !step.AnswerValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    string total = request.CandidateIds.FirstOrDefault(id => id.StartsWith("total:", StringComparison.Ordinal));
                    if (total == null || !int.TryParse(total.Substring(6), out int amount) ||
                        !ResourceAllocationAnswer.TryParse(step.AnswerValue, request.CandidateIds.Where(id => !id.StartsWith("total:", StringComparison.Ordinal)).ToList(), amount, out _, out _))
                        throw new InvalidOperationException("回答不满足Host固定资源总量。");
                }
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = step.AnswerValue;
                return command;
            }
            var candidates = new List<string>();
            if (step != null)
            {
                candidates.AddRange(step.CandidateIds);
                foreach (string definition in step.CandidateDefinitionIds)
                {
                    string id = request.CandidateIds.FirstOrDefault(c => CandidateDefinition(view, request, c) == definition);
                    if (id == null) { if (step.UseAvailableCandidateFallback) continue; reason = "Host候选中缺少目标：" + definition; return null; }
                    candidates.Add(id);
                }
                if (step.UseAvailableCandidateFallback)
                    candidates.AddRange(request.CandidateIds.Where(id => !candidates.Contains(id)).Take(Math.Max(0, request.MinSelections - candidates.Count)));
            }
            else if (request.Kind == CharacterLifecycleService.ModeInteractionTypeId || request.Kind == CharacterLifecycleService.OrderInteractionTypeId)
            {
                var advancing = request.Kind == CharacterLifecycleService.ModeInteractionTypeId
                    ? new[] { CharacterEffectModes.Both, CharacterEffectModes.Strategy, CharacterEffectModes.Tactic }
                    : new[] { CharacterEffectOrders.StrategyFirst, CharacterEffectOrders.TacticFirst };
                if (request.Kind == CharacterLifecycleService.ModeInteractionTypeId)
                {
                    var owner = view.Effects.FirstOrDefault(e => e.EffectId == request.OwnerEffectId);
                    string useId = owner?.NormalizedArguments?.Properties.FirstOrDefault(p => p.Name == "useId")?.Value?.StringValue;
                    var use = view.CharacterUses.FirstOrDefault(u => u.UseId == useId);
                    string definition = view.ItemZones.Items.FirstOrDefault(i => i.ItemId == use?.CardInstanceId)?.DefinitionId;
                    var completed = new HashSet<string>(view.CharacterUses.Where(u => u.CardInstanceId == use?.CardInstanceId).SelectMany(u => u.CompletedModes));
                    advancing = plan.SelectionPriorities.CharacterUseModes.Where(p => p.DefinitionId == definition && !completed.Contains(p.Mode)).Select(p => p.Mode)
                        .Concat(advancing).Distinct().ToArray();
                }
                string choice = advancing.FirstOrDefault(id => request.CandidateIds.Contains(id));
                if (choice == null)
                    throw new InvalidOperationException("当前角色选择没有可推进的合法候选：" + request.Kind +
                        "，候选=" + string.Join(",", request.CandidateIds));
                candidates.Add(choice);
            }
            else if (new[] { "candidate_id", "candidate_ids", "candidate_id_array" }.Contains(request.AnswerSchema))
                candidates.AddRange(request.CandidateIds.Where(id => id != "choice.skip" && id != "decline")
                    .OrderBy(id => request.Kind == "operator.dispatch.return" && view.ItemZones.Items.Any(item =>
                        item.ItemId == id && YC.Domain.Items.ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, "core.basic_character_card")) ? 0 : 1)
                    .ThenBy(id => (request.Kind == "setup.basic_character.discard" || request.Kind == "operator.dispatch.return" ? -1L : 1L) * ProductionGameSelectionPriorities.Rank(request.Kind == "setup.enterprise.choose"
                        ? plan.SelectionPriorities.EnterpriseDefinitionIds : plan.SelectionPriorities.DispatchCharacterDefinitionIds,
                        CandidateDefinition(view, request, id)))
                    .ThenBy(id => { int rank = plan.CandidatePriorityDefinitionIds.IndexOf(CandidateDefinition(view, request, id)); return rank < 0 ? int.MaxValue : rank; })
                    .Take(Math.Min(request.MaxSelections, Math.Max(1, request.MinSelections))));
            else { reason = "交互需要明确路线回答：" + request.Kind + "/" + request.AnswerSchema; return null; }
            if (candidates.Distinct().Count() != candidates.Count || candidates.Any(id => !request.CandidateIds.Contains(id)) ||
                candidates.Count < request.MinSelections || candidates.Count > request.MaxSelections)
                throw new InvalidOperationException("回答不满足Host候选与数量范围。");
            command.OptionIds.AddRange(candidates);
            return command;
        }

        private static string CandidateDefinition(GameStateView view, InteractionView request, string candidateId)
        {
            // 事件选项的正式稳定ID由EventCardOptionIdFactory给出；仅对当前授权候选排序。
            if (request.Kind == "event_card.option" && request.CandidateIds.Contains(candidateId)) return candidateId;
            var item = view.ItemZones.Items.FirstOrDefault(i => i.ItemId == candidateId);
            if (item != null) return item.DefinitionId;
            // 与现有企业UI相同的本人授权候选描述；不从Host/储备区反查隐藏实体。
            int index = request.CandidateIds.IndexOf(candidateId);
            var values = request.PromptParameters?.Properties.FirstOrDefault(value => value.Name == "candidateDefinitionIds")?.Value;
            return index >= 0 && values?.Kind == NormalizedValueKind.Array && index < values.Items.Count && values.Items[index]?.Kind == NormalizedValueKind.String
                ? values.Items[index].StringValue : string.Empty;
        }
    }
}
#endif
