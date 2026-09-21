using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class InfluenceEffectTypeIds
    {
        public const string PlaceInfluence = "effect.influence.place";
        public const string RemoveInfluence = "effect.influence.remove";
        public const string MoveInfluence = "effect.influence.move";
        public const string ReplaceInfluence = "effect.influence.replace";
    }

    public static class InfluenceEventTypeIds
    {
        public const string InfluencePlaced = "InfluencePlaced";
        public const string InfluenceRemoved = "InfluenceRemoved";
        public const string InfluenceMoved = "InfluenceMoved";
        public const string InfluenceReplaced = "InfluenceReplaced";
    }

    public static class InfluenceCauseKinds
    {
        public const string Direct = "direct";
        public const string MoveCity = "move_city";
        public const string PlaceRoad = "place_road";
        public const string Replace = "replace";
        public const string OtherIntrinsicFlow = "other_intrinsic_flow";
    }

    /// <summary>
    /// 影响力四种 Effect 的可持久化参数构造器。调用方只写入稳定 ID 和归一化值，
    /// 不把运行时对象引用塞进 Effect 树。
    /// </summary>
    public static class InfluenceEffectSpecFactory
    {
        public static EffectSpec PlaceInfluence(
            int executingPlayerId,
            string targetSlotId,
            string sourceId = "",
            RuleSubjectReference ownerSubject = null,
            string causeKind = InfluenceCauseKinds.Direct,
            string replaceEffectId = "")
        {
            var owner = ownerSubject ?? InfluenceIdentity.CreatePlayerOwner(executingPlayerId);
            var source = InfluenceIdentity.CreatePlayerSupplySource(executingPlayerId, sourceId);
            var entries = new List<NormalizedValueEntry>
            {
                Entry("executingPlayer", PlayerReference(executingPlayerId)),
                Entry("influenceSource", SourceValue(source)),
                Entry("ownerSubject", SubjectValue(owner)),
                Entry("causeKind", String(causeKind))
            };
            // 不提供 targetSlotId 表示“由该基础放置 Effect 在执行时产生候选并等待回答”。
            // 这使一个放置 Effect 仍是一个原子规则动作，同时允许父 Effect 组合多个
            // 独立放置子节点，而不把多个位置编码成一个组合候选。
            if (!string.IsNullOrEmpty(targetSlotId))
            {
                entries.Add(Entry("targetSlotId", SlotReference(targetSlotId)));
            }
            if (!string.IsNullOrEmpty(replaceEffectId)) entries.Add(Entry("replaceEffectId", EffectReference(replaceEffectId)));
            return Create(
                InfluenceEffectTypeIds.PlaceInfluence,
                executingPlayerId,
                Object(entries));
        }

        public static EffectSpec RemoveInfluence(
            int executingPlayerId,
            string targetInfluenceId,
            string reasonId = "rule.unspecified",
            string causeKind = InfluenceCauseKinds.Direct,
            string replaceEffectId = "")
        {
            var entries = new List<NormalizedValueEntry>
            {
                Entry("executingPlayer", PlayerReference(executingPlayerId)),
                Entry("targetInfluence", InfluenceReference(targetInfluenceId)),
                Entry("destination", String(InfluenceIdentity.PlayerSupplySourceKind)),
                Entry("reasonId", String(reasonId)),
                Entry("causeKind", String(causeKind))
            };
            if (!string.IsNullOrEmpty(replaceEffectId))
            {
                entries.Add(Entry("replaceEffectId", EffectReference(replaceEffectId)));
            }

            return Create(InfluenceEffectTypeIds.RemoveInfluence, executingPlayerId, Object(entries));
        }

        public static EffectSpec MoveInfluence(
            int executingPlayerId,
            string targetInfluenceId,
            string targetSlotId,
            string causeKind = InfluenceCauseKinds.Direct)
        {
            return Create(
                InfluenceEffectTypeIds.MoveInfluence,
                executingPlayerId,
                Object(
                    Entry("executingPlayer", PlayerReference(executingPlayerId)),
                    Entry("targetInfluence", InfluenceReference(targetInfluenceId)),
                    Entry("targetSlotId", SlotReference(targetSlotId)),
                    Entry("causeKind", String(causeKind))));
        }

        public static EffectSpec MoveInfluences(
            int executingPlayerId,
            IList<KeyValuePair<string, string>> moves,
            string causeKind = InfluenceCauseKinds.MoveCity)
        {
            var items = new List<NormalizedValue>();
            if (moves != null)
            {
                for (var i = 0; i < moves.Count; i++)
                {
                    items.Add(Object(
                        Entry("targetInfluence", InfluenceReference(moves[i].Key)),
                        Entry("targetSlotId", SlotReference(moves[i].Value))));
                }
            }

            return Create(
                InfluenceEffectTypeIds.MoveInfluence,
                executingPlayerId,
                Object(
                    Entry("executingPlayer", PlayerReference(executingPlayerId)),
                    Entry("moves", NormalizedValue.CreateArray(items)),
                    Entry("causeKind", String(causeKind))));
        }

        public static EffectSpec ReplaceInfluence(
            int executingPlayerId,
            string targetInfluenceId,
            string replacementSourceId = "",
            RuleSubjectReference replacementOwner = null,
            string causeKind = InfluenceCauseKinds.Replace)
        {
            var owner = replacementOwner ?? InfluenceIdentity.CreatePlayerOwner(executingPlayerId);
            var source = InfluenceIdentity.CreatePlayerSupplySource(executingPlayerId, replacementSourceId);
            return Create(
                InfluenceEffectTypeIds.ReplaceInfluence,
                executingPlayerId,
                Object(
                    Entry("executingPlayer", PlayerReference(executingPlayerId)),
                    Entry("targetInfluence", InfluenceReference(targetInfluenceId)),
                    Entry("replacementSource", SourceValue(source)),
                    Entry("replacementOwner", SubjectValue(owner)),
                    Entry("placementFailurePolicy", String("keep_removal")),
                    Entry("causeKind", String(causeKind))));
        }

        // 这些别名让迁移期调用方可以按动词命名，而不产生第二套参数合同。
        public static EffectSpec CreatePlaceInfluence(int playerId, string slotId, string sourceId = "")
        {
            return PlaceInfluence(playerId, slotId, sourceId);
        }

        public static EffectSpec CreateRemoveInfluence(int playerId, string influenceId, string reasonId = "rule.unspecified")
        {
            return RemoveInfluence(playerId, influenceId, reasonId);
        }

        public static EffectSpec CreateMoveInfluence(int playerId, string influenceId, string targetSlotId)
        {
            return MoveInfluence(playerId, influenceId, targetSlotId);
        }

        public static EffectSpec CreateReplaceInfluence(int playerId, string influenceId)
        {
            return ReplaceInfluence(playerId, influenceId);
        }

        private static EffectSpec Create(string typeId, int playerId, NormalizedValue arguments)
        {
            return new EffectSpec(typeId, arguments)
            {
                PlayerId = playerId,
                DefinitionVersion = InfluenceEffectExecutor.DefinitionVersion
            };
        }

        internal static NormalizedValue PlayerReference(int playerId)
        {
            return NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture));
        }

        internal static NormalizedValue InfluenceReference(string influenceId)
        {
            return NormalizedValue.CreateStableReference("influence", influenceId ?? string.Empty);
        }

        internal static NormalizedValue SlotReference(string slotId)
        {
            return NormalizedValue.CreateStableReference("slot", slotId ?? string.Empty);
        }

        internal static NormalizedValue EffectReference(string effectId)
        {
            return NormalizedValue.CreateStableReference("effect", effectId ?? string.Empty);
        }

        internal static NormalizedValue String(string value)
        {
            return NormalizedValue.CreateString(value ?? string.Empty);
        }

        internal static NormalizedValue Object(params NormalizedValueEntry[] entries)
        {
            return Object(new List<NormalizedValueEntry>(entries ?? new NormalizedValueEntry[0]));
        }

        internal static NormalizedValue Object(IList<NormalizedValueEntry> entries)
        {
            return NormalizedValue.CreateObject(entries);
        }

        internal static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }

        internal static NormalizedValue SubjectValue(RuleSubjectReference subject)
        {
            return Object(
                Entry("subjectType", String(subject == null ? "" : subject.SubjectType)),
                Entry("instanceId", String(subject == null ? "" : subject.InstanceId)),
                Entry("definitionId", String(subject == null ? "" : subject.DefinitionId)),
                Entry("playerId", NormalizedValue.CreateInteger(subject == null ? -1 : subject.PlayerId)));
        }

        internal static NormalizedValue SourceValue(InfluenceSourceReference source)
        {
            return Object(
                Entry("kind", String(source == null ? "" : source.Kind)),
                Entry("sourceId", String(source == null ? "" : source.SourceId)),
                Entry("subject", SubjectValue(source == null ? null : source.Subject)));
        }
    }

    public sealed class InfluenceEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";
        public const string PlaceEffectTypeId = InfluenceEffectTypeIds.PlaceInfluence;
        public const string RemoveEffectTypeId = InfluenceEffectTypeIds.RemoveInfluence;
        public const string MoveEffectTypeId = InfluenceEffectTypeIds.MoveInfluence;
        public const string ReplaceEffectTypeId = InfluenceEffectTypeIds.ReplaceInfluence;

        private readonly InfluenceService influenceService;

        public InfluenceEffectExecutor(InfluenceService influenceService)
        {
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
        }

        public static void Register(EffectRegistry registry, InfluenceService influenceService)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (influenceService == null) throw new ArgumentNullException(nameof(influenceService));
            var executor = new InfluenceEffectExecutor(influenceService);
            RegisterIfMissing(registry, executor, PlaceEffectTypeId, executor.ExecutePlace, ValidatePlace, EffectExecutorKind.IntrinsicFlow);
            RegisterIfMissing(registry, executor, RemoveEffectTypeId, executor.ExecuteRemove, ValidateRemove, EffectExecutorKind.IntrinsicFlow);
            RegisterIfMissing(registry, executor, MoveEffectTypeId, executor.ExecuteMove, ValidateMove, EffectExecutorKind.IntrinsicFlow);
            RegisterIfMissing(registry, executor, ReplaceEffectTypeId, executor.ExecuteReplace, ValidateReplace, EffectExecutorKind.IntrinsicFlow);
        }

        public static void Register(EffectRegistry registry, GameState state, YC.Domain.Maps.IMapQueryService mapQuery)
        {
            Register(registry, new InfluenceService(state, mapQuery));
        }

        private static void RegisterIfMissing(
            EffectRegistry registry,
            InfluenceEffectExecutor executor,
            string typeId,
            Func<EffectExecutionContext, EffectStepResult> handler,
            Func<EffectSpec, string> validator,
            EffectExecutorKind executorKind)
        {
            EffectRegistration ignored;
            if (registry.TryGet(typeId, out ignored)) return;
            registry.Register(new EffectRegistration(typeId, handler, executorKind, DefinitionVersion)
            {
                Validator = validator
            });
        }

        private EffectStepResult ExecutePlace(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            }

            int playerId;
            string slotId;
            string causeKind;
            string replaceEffectId;
            RuleSubjectReference owner;
            InfluenceSourceReference source;
            string diagnostic;
            if (!TryReadPlace(context.Node.NormalizedArguments, out playerId, out slotId, out owner, out source, out causeKind, out replaceEffectId, out diagnostic))
            {
                return Failed(context, "invalid_arguments", diagnostic, null);
            }

            if (string.IsNullOrEmpty(slotId))
            {
                if (context.Node.FlowStage == "")
                {
                    var candidates = GetPlacementCandidates(context, playerId);
                    if (candidates.Count == 0)
                    {
                        return Failed(context, "no_legal_placement", "当前没有合法的影响力放置位置。", null);
                    }

                    return EffectStepResult.Continue("awaiting_target").AddInteraction(
                        CreatePlacementInteraction(context, candidates));
                }

                if (context.Node.FlowStage != "awaiting_target")
                {
                    return Failed(context, "invalid_flow_stage", "影响力放置 Effect 阶段无效。", null);
                }

                slotId = ReadAnswer(context.GetLatestInteractionAnswer());
                var answer = context.GetLatestInteractionAnswer();
                if (CanCancelPlacement(context) && answer != null &&
                    answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                    return EffectStepResult.Failed("player_cancelled");
                if (string.IsNullOrEmpty(slotId) ||
                    !ContainsCandidate(GetPlacementCandidates(context, playerId), slotId))
                {
                    return Failed(context, "invalid_placement_target", "影响力放置目标已经不再合法。", null);
                }
            }

            NormalizedValue declaredScope;
            if (TryGet(context.Node.NormalizedArguments, "candidateScope", out declaredScope) &&
                !ContainsCandidate(GetPlacementCandidates(context, playerId), slotId))
                return Failed(context, "invalid_placement_target", "目标不在当前影响力候选范围内。", null);

            string influenceId = StableIdFactory.Create("influence", context.Node.EffectId, "place", slotId, source.SourceId);
            InfluenceOperationResult result = influenceService.Place(
                context.State, playerId, slotId, influenceId, owner, source);
            if (!result.Succeeded)
            {
                return Failed(context, result.StableFailureCode, result.Reason, Result(result, "failed", result.Reason));
            }

            var step = EffectStepResult.Completed(Result(result, "placed", string.Empty));
            step.AddEvent(CreatePlacedEvent(context.Node, result, causeKind, context.Node.ParentEffectId, replaceEffectId, owner, source));
            step.AddLog(new EffectLogEntrySpec
            {
                PlayerId = playerId,
                Message = "影响力已放置到 " + result.SlotId + "。"
            });
            return step;
        }

        private IReadOnlyList<string> GetPlacementCandidates(EffectExecutionContext context, int playerId)
        {
            var legal = influenceService.GetLegalPlacementSlotIds(context.State, playerId);
            NormalizedValue scope;
            if (!TryGet(context.Node.NormalizedArguments, "candidateScope", out scope)) return legal;
            var result = new List<string>();
            if (scope == null || scope.Kind != NormalizedValueKind.Array) return result;
            foreach (var item in scope.Items)
            {
                string id = item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId : item.StringValue;
                if (ContainsCandidate(legal, id) && !result.Contains(id)) result.Add(id);
            }
            return result;
        }

        private static bool CanCancelPlacement(EffectExecutionContext context)
        {
            var parent = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.Node.ParentEffectId);
            return parent != null && parent.EffectTypeId == MainActionEffectExecutor.TypeId &&
                   parent.ChildEffectIds.Count == 1 && context.Node.ChildEffectIds.Count == 0;
        }

        private static EffectInteractionSpec CreatePlacementInteraction(
            EffectExecutionContext context,
            IReadOnlyList<string> candidates)
        {
            var interaction = new EffectInteractionSpec
            {
                SourceNodeId = context.Node.EffectId,
                InteractionTypeId = "effect.influence.place.target",
                Visibility = GameStateVisibilityPolicy.Owner,
                PromptKey = "effect.influence.place.choose_target",
                AnsweringPlayerId = context.Node.PlayerId,
                CandidateSetId = StableIdFactory.Create(
                    "influence-placement-candidate-set",
                    context.Node.EffectId,
                    context.State.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture)),
                CandidateSetVersion = 1,
                MinSelections = 1,
                MaxSelections = 1,
                AnswerSchema = "candidate_id"
            };
            interaction.AllowDecline = CanCancelPlacement(context);
            if (candidates != null)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!string.IsNullOrEmpty(candidates[i])) interaction.CandidateIds.Add(candidates[i]);
                }
            }

            return interaction;
        }

        private static EffectInteractionSpec CreateRemovalInteraction(
            EffectExecutionContext context,
            IReadOnlyList<string> candidates)
        {
            var interaction = new EffectInteractionSpec
            {
                SourceNodeId = context.Node.EffectId,
                InteractionTypeId = "effect.influence.remove.target",
                Visibility = GameStateVisibilityPolicy.Owner,
                PromptKey = "effect.influence.remove.choose_target",
                AnsweringPlayerId = context.Node.PlayerId,
                CandidateSetId = StableIdFactory.Create(
                    "influence-removal-candidate-set",
                    context.Node.EffectId,
                    context.State.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture)),
                CandidateSetVersion = 1,
                MinSelections = 1,
                MaxSelections = 1,
                AnswerSchema = "candidate_id"
            };
            if (candidates != null)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!string.IsNullOrEmpty(candidates[i])) interaction.CandidateIds.Add(candidates[i]);
                }
            }

            return interaction;
        }

        private static bool ContainsCandidate(IReadOnlyList<string> candidates, string candidate)
        {
            if (candidates == null || string.IsNullOrEmpty(candidate)) return false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] == candidate) return true;
            }

            return false;
        }

        private EffectStepResult ExecuteRemove(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            }

            int playerId;
            string influenceId;
            string reasonId;
            string causeKind;
            string replaceEffectId;
            List<string> candidateScope;
            string diagnostic;
            if (!TryReadRemove(context.Node.NormalizedArguments, out playerId, out influenceId, out reasonId, out causeKind, out replaceEffectId, out candidateScope, out diagnostic))
            {
                return Failed(context, "invalid_arguments", diagnostic, null);
            }

            if (string.IsNullOrEmpty(influenceId))
            {
                var candidates = GetExistingInfluenceCandidates(context.State, candidateScope);
                if (candidates.Count == 0)
                {
                    return Failed(context, "no_legal_removal_target", "当前没有仍然存在的影响力移除目标。", null);
                }

                if (context.Node.FlowStage == string.Empty)
                {
                    return EffectStepResult.Continue("awaiting_target").AddInteraction(
                        CreateRemovalInteraction(context, candidates));
                }

                if (context.Node.FlowStage != "awaiting_target")
                {
                    return Failed(context, "invalid_flow_stage", "影响力移除 Effect 阶段无效。", null);
                }

                influenceId = ReadAnswer(context.GetLatestInteractionAnswer());
                if (string.IsNullOrEmpty(influenceId) || !ContainsCandidate(candidates, influenceId))
                {
                    return Failed(context, "invalid_removal_target", "影响力移除目标已经不再属于候选范围。", null);
                }
            }

            InfluenceOperationResult result = influenceService.Remove(context.State, influenceId);
            if (!result.Succeeded)
            {
                return Failed(context, result.StableFailureCode, result.Reason, Result(result, "failed", result.Reason));
            }

            var step = EffectStepResult.Completed(Result(result, "removed", string.Empty));
            step.AddEvent(CreateRemovedEvent(context.Node, result, reasonId, causeKind, replaceEffectId));
            step.AddLog(new EffectLogEntrySpec
            {
                PlayerId = playerId,
                Message = "影响力已移除并返回玩家供给区。"
            });
            return step;
        }

        private EffectStepResult ExecuteMove(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            }

            int playerId;
            string causeKind;
            List<MoveItem> moves;
            string diagnostic;
            if (!TryReadMoves(context.Node.NormalizedArguments, out playerId, out moves, out causeKind, out diagnostic))
            {
                return Failed(context, "invalid_arguments", diagnostic, null);
            }

            var requests = new List<InfluenceMoveRequest>(moves.Count);
            for (var i = 0; i < moves.Count; i++)
            {
                InfluencePlacement placement = influenceService.FindInfluence(context.State, moves[i].InfluenceId);
                if (placement == null)
                {
                    var missing = InfluenceOperationResult.Failure(
                        InfluenceFailureCode.InfluenceNotFound,
                        "要移动的影响力不存在。",
                        playerId,
                        moves[i].InfluenceId,
                        false,
                        moves[i].InfluenceId);
                    return Failed(context, missing.StableFailureCode, missing.Reason, Result(missing, "failed", missing.Reason));
                }

                moves[i].FromSlotId = placement.SlotId;
                requests.Add(new InfluenceMoveRequest(placement.SlotId, moves[i].TargetSlotId));
            }

            InfluenceOperationResult result = moves.Count == 1
                ? influenceService.Move(context.State, playerId, moves[0].InfluenceId, moves[0].TargetSlotId)
                : influenceService.MoveAtomically(context.State, playerId, requests);
            if (!result.Succeeded)
            {
                return Failed(context, result.StableFailureCode, result.Reason, Result(result, "failed", result.Reason));
            }

            for (var i = 0; i < moves.Count; i++)
            {
                var moved = influenceService.FindInfluence(context.State, moves[i].InfluenceId);
                if (moved != null) moves[i].TargetSlotId = moved.SlotId;
            }

            var step = EffectStepResult.Completed(CreateMoveResult(context.State, result, moves));
            step.AddEvent(CreateMovedEvent(context.Node, result, moves, causeKind));
            step.AddLog(new EffectLogEntrySpec
            {
                PlayerId = playerId,
                Message = "影响力移动已提交。"
            });
            return step;
        }

        private EffectStepResult ExecuteReplace(EffectExecutionContext context)
        {
            EffectNodeRuntimeState node = context.Node;
            if (node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(node.NormalizedResult.Clone());
            }

            int playerId;
            string targetInfluenceId;
            InfluenceSourceReference source;
            RuleSubjectReference owner;
            string causeKind;
            string diagnostic;
            if (!TryReadReplace(node.NormalizedArguments, out playerId, out targetInfluenceId, out source, out owner, out causeKind, out diagnostic))
            {
                return Failed(context, "invalid_arguments", diagnostic, null);
            }

            if (node.FlowStage == "")
            {
                InfluencePlacement target = influenceService.FindInfluence(context.State, targetInfluenceId);
                if (target == null)
                {
                    var missing = InfluenceOperationResult.Failure(
                        InfluenceFailureCode.InfluenceNotFound,
                        "替换目标影响力不存在。",
                        playerId,
                        targetInfluenceId,
                        false,
                        targetInfluenceId);
                    return Failed(context, missing.StableFailureCode, missing.Reason, Result(missing, "failed", missing.Reason));
                }

                return EffectStepResult.Continue("remove_started")
                    .AddChild(InfluenceEffectSpecFactory.RemoveInfluence(
                        playerId,
                        target.InfluenceId,
                        "replace",
                        InfluenceCauseKinds.Replace,
                        node.EffectId));
            }

            EffectNodeRuntimeState removalNode = GetOnlyChild(context, InfluenceEffectTypeIds.RemoveInfluence);
            if (removalNode == null || removalNode.Status == EffectNodeStatus.Failed)
            {
                NormalizedValue failedResult = removalNode == null
                    ? NormalizedValue.CreateNull()
                    : removalNode.NormalizedResult.Clone();
                string reason = removalNode == null ? "移除阶段未完成。" : removalNode.FailureReason;
                return EffectStepResult.Failed("remove_failed", failedResult)
                    .AddLog(new EffectLogEntrySpec { PlayerId = playerId, Message = "影响力替换的移除阶段失败：" + reason });
            }

            if (node.FlowStage == "remove_started")
            {
                string removedSlotId = ReadStableId(removalNode.NormalizedResult, "slotId", "slot");
                if (string.IsNullOrEmpty(removedSlotId))
                {
                    return EffectStepResult.Failed("remove_failed", removalNode.NormalizedResult.Clone());
                }

                return EffectStepResult.Continue("place_started")
                    .AddChild(InfluenceEffectSpecFactory.PlaceInfluence(
                        playerId,
                        removedSlotId,
                        source.SourceId,
                        owner,
                        causeKind,
                        node.EffectId));
            }

            EffectNodeRuntimeState placementNode = GetOnlyChild(context, InfluenceEffectTypeIds.PlaceInfluence);
            if (placementNode == null)
            {
                return EffectStepResult.Failed("place_failed", NormalizedValue.CreateNull());
            }

            if (placementNode.Status == EffectNodeStatus.Failed)
            {
                var placementReason = ReadString(placementNode.NormalizedResult, "reason", placementNode.FailureReason);
                return EffectStepResult.Completed(CreateReplaceResult(
                    "removed_only",
                    removalNode.NormalizedResult,
                    placementNode.NormalizedResult,
                    placementReason))
                    .AddLog(new EffectLogEntrySpec
                    {
                        PlayerId = playerId,
                        Message = "影响力替换完成了移除，但重新放置失败，未回滚。"
                    });
            }

            var result = EffectStepResult.Completed(CreateReplaceResult(
                "replaced",
                removalNode.NormalizedResult,
                placementNode.NormalizedResult,
                string.Empty));
            result.AddEvent(CreateReplacedEvent(node, removalNode, placementNode, causeKind));
            result.AddLog(new EffectLogEntrySpec
            {
                PlayerId = playerId,
                Message = "影响力替换已完成。"
            });
            return result;
        }

        private static EffectStepResult Failed(
            EffectExecutionContext context,
            string failureCode,
            string diagnostic,
            NormalizedValue result)
        {
            return EffectStepResult.Failed(
                    failureCode ?? "failed",
                    result ?? NormalizedValue.CreateObject(new List<NormalizedValueEntry>()))
                .AddLog(new EffectLogEntrySpec
                {
                    PlayerId = context.Node.PlayerId,
                    Message = "影响力 Effect 提交失败：" + (diagnostic ?? failureCode) + "。"
                });
        }

        private static EffectNodeRuntimeState GetOnlyChild(EffectExecutionContext context, string typeId)
        {
            for (var i = context.Node.ChildEffectIds.Count - 1; i >= 0; i--)
            {
                EffectNodeRuntimeState child = context.State.EffectRuntime.EffectNodes.Find(
                    candidate => candidate != null && candidate.EffectId == context.Node.ChildEffectIds[i]);
                if (child != null && child.EffectTypeId == typeId) return child;
            }

            return null;
        }

        private static NormalizedValue Result(InfluenceOperationResult result, string outcome, string reason)
        {
            var entries = new List<NormalizedValueEntry>
            {
                InfluenceEffectSpecFactory.Entry("outcome", InfluenceEffectSpecFactory.String(outcome)),
                InfluenceEffectSpecFactory.Entry("failureCode", InfluenceEffectSpecFactory.String(result == null ? "" : result.StableFailureCode)),
                InfluenceEffectSpecFactory.Entry("influenceId", string.IsNullOrEmpty(result == null ? "" : result.InfluenceId)
                    ? NormalizedValue.CreateNull()
                    : InfluenceEffectSpecFactory.InfluenceReference(result.InfluenceId)),
                InfluenceEffectSpecFactory.Entry("playerId", NormalizedValue.CreateInteger(result == null ? -1 : result.PlayerId)),
                InfluenceEffectSpecFactory.Entry("reason", InfluenceEffectSpecFactory.String(reason)),
                InfluenceEffectSpecFactory.Entry("slotId", string.IsNullOrEmpty(result == null ? "" : result.SlotId)
                    ? NormalizedValue.CreateNull()
                    : InfluenceEffectSpecFactory.SlotReference(result.SlotId)),
                InfluenceEffectSpecFactory.Entry("fromSlotId", string.IsNullOrEmpty(result == null ? "" : result.FromSlotId)
                    ? NormalizedValue.CreateNull()
                    : InfluenceEffectSpecFactory.SlotReference(result.FromSlotId))
            };
            return InfluenceEffectSpecFactory.Object(entries);
        }

        private static NormalizedValue CreateMoveResult(GameState state, InfluenceOperationResult result, IList<MoveItem> moves)
        {
            var values = new List<NormalizedValue>();
            for (var i = 0; i < moves.Count; i++)
            {
                InfluencePlacement placement = state == null || state.Map == null || state.Map.Influences == null
                    ? null
                    : state.Map.Influences.Find(candidate => candidate != null && candidate.InfluenceId == moves[i].InfluenceId);
                values.Add(InfluenceEffectSpecFactory.Object(
                    InfluenceEffectSpecFactory.Entry("fromSlotId", InfluenceEffectSpecFactory.SlotReference(moves[i].FromSlotId)),
                    InfluenceEffectSpecFactory.Entry("influenceId", InfluenceEffectSpecFactory.InfluenceReference(moves[i].InfluenceId)),
                    InfluenceEffectSpecFactory.Entry("slotId", InfluenceEffectSpecFactory.SlotReference(placement == null ? moves[i].TargetSlotId : placement.SlotId))));
            }

            return InfluenceEffectSpecFactory.Object(
                InfluenceEffectSpecFactory.Entry("moves", NormalizedValue.CreateArray(values)),
                InfluenceEffectSpecFactory.Entry("outcome", InfluenceEffectSpecFactory.String("moved")));
        }

        private static NormalizedValue CreateReplaceResult(
            string outcome,
            NormalizedValue removalResult,
            NormalizedValue placementResult,
            string placementFailureReason)
        {
            return InfluenceEffectSpecFactory.Object(
                InfluenceEffectSpecFactory.Entry("outcome", InfluenceEffectSpecFactory.String(outcome)),
                InfluenceEffectSpecFactory.Entry("removedInfluenceRef", GetResultReference(removalResult, "influenceId", "influence")),
                InfluenceEffectSpecFactory.Entry("slot", GetResultReference(removalResult, "slotId", "slot")),
                InfluenceEffectSpecFactory.Entry("removed", removalResult == null ? NormalizedValue.CreateNull() : removalResult.Clone()),
                InfluenceEffectSpecFactory.Entry("placed", placementResult == null ? NormalizedValue.CreateNull() : placementResult.Clone()),
                InfluenceEffectSpecFactory.Entry("placeFailureReason", InfluenceEffectSpecFactory.String(placementFailureReason)));
        }

        private static NormalizedValue GetResultReference(NormalizedValue value, string name, string type)
        {
            var id = ReadStableId(value, name, type);
            return string.IsNullOrEmpty(id) ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference(type, id);
        }

        private static EffectEventRequest CreatePlacedEvent(
            EffectNodeRuntimeState node,
            InfluenceOperationResult result,
            string causeKind,
            string parentOperationEffectId,
            string replaceEffectId,
            RuleSubjectReference owner,
            InfluenceSourceReference source)
        {
            return Event(
                node,
                InfluenceEventTypeIds.InfluencePlaced,
                result.InfluenceId,
                "placed",
                CreateEventObject(
                    InfluenceEffectSpecFactory.Entry("causeKind", InfluenceEffectSpecFactory.String(causeKind)),
                    InfluenceEffectSpecFactory.Entry("placeEffectId", InfluenceEffectSpecFactory.EffectReference(node.EffectId)),
                    InfluenceEffectSpecFactory.Entry("executingPlayerId", NormalizedValue.CreateInteger(result.PlayerId)),
                    InfluenceEffectSpecFactory.Entry("influenceRefAfterPlacement", InfluenceEffectSpecFactory.InfluenceReference(result.InfluenceId)),
                    InfluenceEffectSpecFactory.Entry("ownerSubject", InfluenceEffectSpecFactory.SubjectValue(owner)),
                    InfluenceEffectSpecFactory.Entry("parentOperationEffectId", StringOrNull(parentOperationEffectId)),
                    InfluenceEffectSpecFactory.Entry("replaceEffectId", StringOrNull(replaceEffectId)),
                    InfluenceEffectSpecFactory.Entry("source", InfluenceEffectSpecFactory.SourceValue(source)),
                    InfluenceEffectSpecFactory.Entry("toSlot", InfluenceEffectSpecFactory.SlotReference(result.SlotId))));
        }

        private static EffectEventRequest CreateRemovedEvent(
            EffectNodeRuntimeState node,
            InfluenceOperationResult result,
            string reasonId,
            string causeKind,
            string replaceEffectId)
        {
            return Event(
                node,
                InfluenceEventTypeIds.InfluenceRemoved,
                result.InfluenceId,
                "removed",
                CreateEventObject(
                    InfluenceEffectSpecFactory.Entry("causeKind", InfluenceEffectSpecFactory.String(causeKind)),
                    InfluenceEffectSpecFactory.Entry("destination", InfluenceEffectSpecFactory.String(InfluenceIdentity.PlayerSupplySourceKind)),
                    InfluenceEffectSpecFactory.Entry("removeEffectId", InfluenceEffectSpecFactory.EffectReference(node.EffectId)),
                    InfluenceEffectSpecFactory.Entry("executingPlayerId", NormalizedValue.CreateInteger(result.PlayerId)),
                    InfluenceEffectSpecFactory.Entry("fromSlot", InfluenceEffectSpecFactory.SlotReference(result.FromSlotId)),
                    InfluenceEffectSpecFactory.Entry("influenceRefBeforeRemoval", InfluenceEffectSpecFactory.InfluenceReference(result.InfluenceId)),
                    InfluenceEffectSpecFactory.Entry("parentOperationEffectId", StringOrNull(node.ParentEffectId)),
                    InfluenceEffectSpecFactory.Entry("reasonId", InfluenceEffectSpecFactory.String(reasonId)),
                    InfluenceEffectSpecFactory.Entry("replaceEffectId", StringOrNull(replaceEffectId))));
        }

        private static EffectEventRequest CreateMovedEvent(
            EffectNodeRuntimeState node,
            InfluenceOperationResult result,
            IList<MoveItem> moves,
            string causeKind)
        {
            return Event(
                node,
                InfluenceEventTypeIds.InfluenceMoved,
                result.InfluenceId,
                "moved",
                CreateEventObject(
                    InfluenceEffectSpecFactory.Entry("causeKind", InfluenceEffectSpecFactory.String(causeKind)),
                    InfluenceEffectSpecFactory.Entry("executingPlayerId", NormalizedValue.CreateInteger(result.PlayerId)),
                    InfluenceEffectSpecFactory.Entry("moves", CreateMoveArray(moves))));
        }

        private static NormalizedValue CreateMoveArray(IList<MoveItem> moves)
        {
            var values = new List<NormalizedValue>();
            if (moves != null)
            {
                for (var i = 0; i < moves.Count; i++)
                {
                    values.Add(InfluenceEffectSpecFactory.Object(
                        InfluenceEffectSpecFactory.Entry("fromSlotId", InfluenceEffectSpecFactory.SlotReference(moves[i].FromSlotId)),
                        InfluenceEffectSpecFactory.Entry("influenceId", InfluenceEffectSpecFactory.InfluenceReference(moves[i].InfluenceId)),
                        InfluenceEffectSpecFactory.Entry("slotId", InfluenceEffectSpecFactory.SlotReference(moves[i].TargetSlotId))));
                }
            }

            return NormalizedValue.CreateArray(values);
        }

        private static EffectEventRequest CreateReplacedEvent(
            EffectNodeRuntimeState node,
            EffectNodeRuntimeState removal,
            EffectNodeRuntimeState placement,
            string causeKind)
        {
            string removedId = ReadStableId(removal.NormalizedResult, "influenceId", "influence");
            string placedId = ReadStableId(placement.NormalizedResult, "influenceId", "influence");
            string slotId = ReadStableId(placement.NormalizedResult, "slotId", "slot");
            return Event(
                node,
                InfluenceEventTypeIds.InfluenceReplaced,
                slotId,
                "replaced",
                CreateEventObject(
                    InfluenceEffectSpecFactory.Entry("causeKind", InfluenceEffectSpecFactory.String(causeKind)),
                    InfluenceEffectSpecFactory.Entry("executingPlayerId", NormalizedValue.CreateInteger(node.PlayerId)),
                    InfluenceEffectSpecFactory.Entry("replaceEffectId", InfluenceEffectSpecFactory.EffectReference(node.EffectId)),
                    InfluenceEffectSpecFactory.Entry("placeEffectId", InfluenceEffectSpecFactory.EffectReference(placement.EffectId)),
                    InfluenceEffectSpecFactory.Entry("placedInfluenceRef", InfluenceEffectSpecFactory.InfluenceReference(placedId)),
                    InfluenceEffectSpecFactory.Entry("removeEffectId", InfluenceEffectSpecFactory.EffectReference(removal.EffectId)),
                    InfluenceEffectSpecFactory.Entry("removedInfluenceRef", InfluenceEffectSpecFactory.InfluenceReference(removedId)),
                    InfluenceEffectSpecFactory.Entry("slot", InfluenceEffectSpecFactory.SlotReference(slotId))));
        }

        private static EffectEventRequest Event(
            EffectNodeRuntimeState node,
            string eventType,
            string targetId,
            string semanticKey,
            NormalizedValue payload)
        {
            return new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", node.EffectId, eventType, targetId ?? ""),
                EventType = eventType,
                SourceEffectId = node.EffectId,
                OwnerNodeId = node.EffectId,
                TargetEntityId = targetId ?? "",
                PlayerId = node.PlayerId,
                Payload = payload,
                ResponseKind = RuleEventResponseKind.Effects,
                DefinitionVersion = DefinitionVersion,
                Visibility = "public",
                SemanticKey = semanticKey
            };
        }

        private static NormalizedValue CreateEventObject(params NormalizedValueEntry[] entries)
        {
            return InfluenceEffectSpecFactory.Object(entries);
        }

        private static NormalizedValue StringOrNull(string value)
        {
            return string.IsNullOrEmpty(value) ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference("effect", value);
        }

        private sealed class MoveItem
        {
            public string InfluenceId = string.Empty;
            public string TargetSlotId = string.Empty;
            public string FromSlotId = string.Empty;
        }

        private static bool TryReadPlace(
            NormalizedValue args,
            out int playerId,
            out string slotId,
            out RuleSubjectReference owner,
            out InfluenceSourceReference source,
            out string causeKind,
            out string replaceEffectId,
            out string diagnostic)
        {
            playerId = -1;
            slotId = string.Empty;
            owner = null;
            source = null;
            causeKind = InfluenceCauseKinds.Direct;
            replaceEffectId = string.Empty;
            diagnostic = string.Empty;
            NormalizedValue ownerValue;
            NormalizedValue sourceValue;
            if (!TryPlayer(args, "executingPlayer", out playerId) ||
                !TryStableOrDeferredSlot(args, out slotId) ||
                !TryGet(args, "ownerSubject", out ownerValue) ||
                !TryGet(args, "influenceSource", out sourceValue) ||
                !TrySubject(ownerValue, out owner) ||
                !TrySource(sourceValue, out source))
            {
                diagnostic = "PlaceInfluence 参数必须包含 executingPlayer、influenceSource、ownerSubject 和 targetSlotId。";
                return false;
            }

            causeKind = ReadString(args, "causeKind", InfluenceCauseKinds.Direct);
            replaceEffectId = ReadStableId(args, "replaceEffectId", "effect");
            if (!IsCauseKind(causeKind))
            {
                diagnostic = "causeKind 不是已登记的影响力操作来源。";
                return false;
            }
            return true;
        }

        private static bool TryStableOrDeferredSlot(NormalizedValue value, out string id)
        {
            id = string.Empty;
            NormalizedValue candidate;
            if (!TryGet(value, "targetSlotId", out candidate) &&
                !TryGet(value, "targetSlot", out candidate) &&
                !TryGet(value, "slot", out candidate))
            {
                // 缺少目标字段就是延迟选点模式；其他必需字段仍由调用方校验。
                return true;
            }

            if (candidate == null || candidate.Kind != NormalizedValueKind.StableReference ||
                candidate.ReferenceType != "slot")
            {
                return false;
            }

            id = candidate.ReferenceId ?? string.Empty;
            return true;
        }

        private static string ReadAnswer(NormalizedValue value)
        {
            if (value == null) return string.Empty;
            if (value.Kind == NormalizedValueKind.String ||
                value.Kind == NormalizedValueKind.StableReference)
            {
                return value.Kind == NormalizedValueKind.String
                    ? value.StringValue ?? string.Empty
                    : value.ReferenceId ?? string.Empty;
            }

            if (value.Kind == NormalizedValueKind.Array && value.Items != null && value.Items.Count == 1)
            {
                return ReadAnswer(value.Items[0]);
            }

            return string.Empty;
        }

        private static bool TryReadRemove(
            NormalizedValue args,
            out int playerId,
            out string influenceId,
            out string reasonId,
            out string causeKind,
            out string replaceEffectId,
            out List<string> candidateScope,
            out string diagnostic)
        {
            playerId = -1;
            influenceId = string.Empty;
            reasonId = "rule.unspecified";
            causeKind = InfluenceCauseKinds.Direct;
            replaceEffectId = string.Empty;
            candidateScope = new List<string>();
            diagnostic = string.Empty;
            if (!TryPlayer(args, "executingPlayer", out playerId))
            {
                diagnostic = "RemoveInfluence 参数必须包含 executingPlayer。";
                return false;
            }

            if (!TryStable(args, "targetInfluence", "influence", out influenceId))
            {
                NormalizedValue rawScope;
                if (!TryGet(args, "candidateScope", out rawScope) ||
                    rawScope == null || rawScope.Kind != NormalizedValueKind.Array ||
                    rawScope.Items == null || rawScope.Items.Count == 0)
                {
                    diagnostic = "RemoveInfluence 参数必须包含 targetInfluence 或非空 candidateScope。";
                    return false;
                }

                for (var i = 0; i < rawScope.Items.Count; i++)
                {
                    string candidateId;
                    if (!TryStableValue(rawScope.Items[i], "influence", out candidateId) ||
                        candidateScope.Contains(candidateId))
                    {
                        diagnostic = "RemoveInfluence 的 candidateScope 必须是唯一影响力引用数组。";
                        return false;
                    }
                    candidateScope.Add(candidateId);
                }
                influenceId = string.Empty;
            }

            reasonId = ReadString(args, "reasonId", "rule.unspecified");
            causeKind = ReadString(args, "causeKind", InfluenceCauseKinds.Direct);
            NormalizedValue replace;
            if (TryGet(args, "replaceEffectId", out replace) && replace != null && replace.Kind == NormalizedValueKind.StableReference && replace.ReferenceType == "effect")
            {
                replaceEffectId = replace.ReferenceId;
            }

            if (!IsCauseKind(causeKind))
            {
                diagnostic = "causeKind 不是已登记的影响力操作来源。";
                return false;
            }
            return true;
        }

        private static List<string> GetExistingInfluenceCandidates(
            GameState state,
            IReadOnlyList<string> candidateScope)
        {
            var result = new List<string>();
            if (state == null || candidateScope == null) return result;
            for (var i = 0; i < candidateScope.Count; i++)
            {
                var influenceId = candidateScope[i];
                if (string.IsNullOrEmpty(influenceId) || result.Contains(influenceId)) continue;
                if (state.Map != null && state.Map.Influences != null &&
                    state.Map.Influences.Exists(candidate => candidate != null && candidate.InfluenceId == influenceId))
                {
                    result.Add(influenceId);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static bool TryReadMoves(
            NormalizedValue args,
            out int playerId,
            out List<MoveItem> moves,
            out string causeKind,
            out string diagnostic)
        {
            playerId = -1;
            moves = new List<MoveItem>();
            causeKind = InfluenceCauseKinds.Direct;
            diagnostic = string.Empty;
            if (!TryPlayer(args, "executingPlayer", out playerId))
            {
                diagnostic = "MoveInfluence 参数必须包含 executingPlayer。";
                return false;
            }

            NormalizedValue batch;
            if (TryGet(args, "moves", out batch))
            {
                if (batch.Kind != NormalizedValueKind.Array || batch.Items == null || batch.Items.Count == 0)
                {
                    diagnostic = "moves 必须是非空数组。";
                    return false;
                }

                for (var i = 0; i < batch.Items.Count; i++)
                {
                    string influenceId;
                    string slotId;
                    if (!TryStable(batch.Items[i], "targetInfluence", "influence", out influenceId) ||
                        !TryStableEither(batch.Items[i], "targetSlotId", "targetSlot", "slot", out slotId))
                    {
                        diagnostic = "moves 的每一项必须包含影响力实例 ID 和目标槽位。";
                        return false;
                    }

                    moves.Add(new MoveItem { InfluenceId = influenceId, TargetSlotId = slotId });
                }
            }
            else
            {
                string influenceId;
                string slotId;
                if (!TryStable(args, "targetInfluence", "influence", out influenceId) || !TryStableEither(args, "targetSlotId", "targetSlot", "slot", out slotId))
                {
                    diagnostic = "MoveInfluence 参数必须包含 targetInfluence 和 targetSlot，或 moves 数组。";
                    return false;
                }

                moves.Add(new MoveItem { InfluenceId = influenceId, TargetSlotId = slotId });
            }

            causeKind = ReadString(args, "causeKind", InfluenceCauseKinds.Direct);
            if (!IsCauseKind(causeKind))
            {
                diagnostic = "causeKind 不是已登记的影响力操作来源。";
                return false;
            }
            return true;
        }

        private static bool TryReadReplace(
            NormalizedValue args,
            out int playerId,
            out string targetInfluenceId,
            out InfluenceSourceReference source,
            out RuleSubjectReference owner,
            out string causeKind,
            out string diagnostic)
        {
            playerId = -1;
            targetInfluenceId = string.Empty;
            source = null;
            owner = null;
            causeKind = InfluenceCauseKinds.Replace;
            diagnostic = string.Empty;
            NormalizedValue sourceValue;
            NormalizedValue ownerValue;
            if (!TryPlayer(args, "executingPlayer", out playerId) ||
                !TryStable(args, "targetInfluence", "influence", out targetInfluenceId) ||
                !TryGet(args, "replacementSource", out sourceValue) ||
                !TryGet(args, "replacementOwner", out ownerValue) ||
                !TrySource(sourceValue, out source) ||
                !TrySubject(ownerValue, out owner) ||
                !string.Equals(ReadString(args, "placementFailurePolicy", ""), "keep_removal", StringComparison.Ordinal))
            {
                diagnostic = "ReplaceInfluence 参数必须使用 keep_removal 并包含目标、替换来源和替换所有者。";
                return false;
            }

            causeKind = ReadString(args, "causeKind", InfluenceCauseKinds.Replace);
            if (!IsCauseKind(causeKind))
            {
                diagnostic = "causeKind 不是已登记的影响力操作来源。";
                return false;
            }
            return true;
        }

        private static bool IsCauseKind(string causeKind)
        {
            return causeKind == InfluenceCauseKinds.Direct ||
                   causeKind == InfluenceCauseKinds.MoveCity ||
                   causeKind == InfluenceCauseKinds.PlaceRoad ||
                   causeKind == InfluenceCauseKinds.Replace ||
                   causeKind == InfluenceCauseKinds.OtherIntrinsicFlow;
        }

        private static string ValidatePlace(EffectSpec spec) { return ValidateKeys(spec, new[] { "causeKind", "executingPlayer", "influenceSource", "ownerSubject", "replaceEffectId", "targetSlot", "targetSlotId", "candidateScope" }, new[] { "executingPlayer", "influenceSource", "ownerSubject" }); }
        private static string ValidateRemove(EffectSpec spec)
        {
            string result = ValidateKeys(
                spec,
                new[] { "causeKind", "candidateScope", "destination", "executingPlayer", "reasonId", "replaceEffectId", "targetInfluence" },
                new[] { "executingPlayer" });
            if (!string.IsNullOrEmpty(result)) return result;
            NormalizedValue target;
            NormalizedValue scope;
            if (!TryGet(spec.NormalizedArguments, "targetInfluence", out target) &&
                (!TryGet(spec.NormalizedArguments, "candidateScope", out scope) ||
                 scope == null || scope.Kind != NormalizedValueKind.Array || scope.Items == null || scope.Items.Count == 0))
            {
                return "RemoveInfluence 必须提供 targetInfluence 或 candidateScope。";
            }
            return string.Empty;
        }
        private static string ValidateMove(EffectSpec spec) { return ValidateKeys(spec, new[] { "causeKind", "executingPlayer", "moves", "targetInfluence", "targetSlot", "targetSlotId" }, new[] { "executingPlayer" }); }
        private static string ValidateReplace(EffectSpec spec) { return ValidateKeys(spec, new[] { "causeKind", "executingPlayer", "placementFailurePolicy", "replacementOwner", "replacementSource", "targetInfluence" }, new[] { "executingPlayer", "targetInfluence", "replacementOwner", "replacementSource", "placementFailurePolicy" }); }

        private static string ValidateKeys(EffectSpec spec, IList<string> allowed, IList<string> required)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object) return "影响力 Effect 参数必须是对象。";
            if (spec.NormalizedArguments.Properties == null) return "影响力 Effect 参数字段无效。";
            for (var i = 0; i < spec.NormalizedArguments.Properties.Count; i++)
            {
                NormalizedValueEntry entry = spec.NormalizedArguments.Properties[i];
                if (entry == null || !allowed.Contains(entry.Name)) return "影响力 Effect 参数包含未登记字段。";
            }
            for (var i = 0; i < required.Count; i++)
            {
                NormalizedValue ignored;
                if (!TryGet(spec.NormalizedArguments, required[i], out ignored)) return "影响力 Effect 缺少必需字段：" + required[i] + "。";
            }
            return string.Empty;
        }

        private static bool TryPlayer(NormalizedValue value, string name, out int playerId)
        {
            playerId = -1;
            NormalizedValue player;
            if (!TryGet(value, name, out player) || player == null) return false;
            if (player.Kind == NormalizedValueKind.Integer)
            {
                playerId = (int)player.IntegerValue;
                return player.IntegerValue >= 0;
            }
            if (player.Kind == NormalizedValueKind.StableReference && player.ReferenceType == "player")
            {
                var referenceId = player.ReferenceId ?? string.Empty;
                if (referenceId.StartsWith("p", StringComparison.OrdinalIgnoreCase)) referenceId = referenceId.Substring(1);
                return int.TryParse(referenceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId) && playerId >= 0;
            }
            return false;
        }

        private static bool TryStable(NormalizedValue value, string name, string referenceType, out string id)
        {
            id = string.Empty;
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && TryStableValue(candidate, referenceType, out id);
        }

        private static bool TryStableValue(NormalizedValue candidate, string referenceType, out string id)
        {
            id = string.Empty;
            if (candidate == null || candidate.Kind != NormalizedValueKind.StableReference ||
                candidate.ReferenceType != referenceType || string.IsNullOrEmpty(candidate.ReferenceId))
            {
                return false;
            }

            id = candidate.ReferenceId;
            return true;
        }

        private static bool TryStableEither(
            NormalizedValue value,
            string primaryName,
            string fallbackName,
            string referenceType,
            out string id)
        {
            if (TryStable(value, primaryName, referenceType, out id)) return true;
            return TryStable(value, fallbackName, referenceType, out id);
        }

        private static bool TrySubject(NormalizedValue value, out RuleSubjectReference subject)
        {
            subject = null;
            string subjectType = ReadString(value, "subjectType", "");
            string instanceId = ReadString(value, "instanceId", "");
            string definitionId = ReadString(value, "definitionId", "");
            long playerId = ReadInteger(value, "playerId", -1);
            if (string.IsNullOrEmpty(subjectType)) return false;
            subject = new RuleSubjectReference { SubjectType = subjectType, InstanceId = instanceId, DefinitionId = definitionId, PlayerId = (int)playerId };
            return true;
        }

        private static bool TrySource(NormalizedValue value, out InfluenceSourceReference source)
        {
            source = null;
            if (value == null || value.Kind != NormalizedValueKind.Object) return false;
            string kind = ReadString(value, "kind", "");
            string sourceId = ReadString(value, "sourceId", "");
            NormalizedValue subjectValue;
            RuleSubjectReference subject = null;
            if (TryGet(value, "subject", out subjectValue) && !TrySubject(subjectValue, out subject)) return false;
            if (string.IsNullOrEmpty(kind)) return false;
            source = new InfluenceSourceReference { Kind = kind, SourceId = sourceId, Subject = subject ?? new RuleSubjectReference() };
            return true;
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && candidate != null && candidate.Kind == NormalizedValueKind.String ? candidate.StringValue : fallback;
        }

        private static long ReadInteger(NormalizedValue value, string name, long fallback)
        {
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && candidate != null && candidate.Kind == NormalizedValueKind.Integer ? candidate.IntegerValue : fallback;
        }

        private static string ReadStableId(NormalizedValue value, string name, string type)
        {
            string result;
            return TryStable(value, name, type, out result) ? result : string.Empty;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (var i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name)
                    {
                        result = entry.Value;
                        return true;
                    }
                }
            }
            result = null;
            return false;
        }
    }

    public static class InfluenceEffectRunner
    {
        public static EffectNodeRuntimeState Run(
            GameState state,
            InfluenceService influenceService,
            EffectSpec spec,
            out EffectRunReport report)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (influenceService == null) throw new ArgumentNullException(nameof(influenceService));
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            var registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, influenceService);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(spec, sourceId: spec.SourceId);
            report = executor.RunUntilQuiescent();
            return executor.GetNode(effectId);
        }
    }
}
