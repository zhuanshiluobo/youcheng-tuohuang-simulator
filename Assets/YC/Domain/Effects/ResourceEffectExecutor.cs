using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Cards;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Effects
{
    /// <summary>
    /// 资源和分数的最小通用 Effect。事件牌、移动和探索只生成这些稳定类型，
    /// 不再把“某张牌给什么奖励”写进命令处理器。
    /// </summary>
    public static class ResourceEffectTypeIds
    {
        public const string Gain = "effect.resource.gain";
        public const string GainOpponents = "effect.resource.gain_opponents";
        public const string Pay = "effect.resource.pay";
        public const string GainScore = "effect.score.gain";
    }

    public static class ResourceEffectSpecFactory
    {
        public static EffectSpec Gain(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.Gain, playerId, type, amount, sourceId);
        }

        public static EffectSpec Pay(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.Pay, playerId, type, amount, sourceId);
        }

        public static EffectSpec Pay(int playerId, ResourceSet cost, string sourceId = "")
        {
            if (cost == null) throw new ArgumentNullException(nameof(cost));
            var entries = cost.Enumerate().Select(item => new NormalizedValueEntry {
                Name = item.Key.ToString(), Value = NormalizedValue.CreateInteger(item.Value) }).ToList();
            var spec = EffectSpec.Create(ResourceEffectTypeIds.Pay, ItemTransferEffectExecutor.Object(
                "player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture)),
                "cost", NormalizedValue.CreateObject(entries)), playerId);
            spec.SourceId = sourceId ?? string.Empty;
            return spec;
        }

        public static EffectSpec GainOpponents(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.GainOpponents, playerId, type, amount, sourceId);
        }

        public static EffectSpec GainScore(int playerId, int amount, string sourceId = "")
        {
            return new EffectSpec(
                ResourceEffectTypeIds.GainScore,
                CreateObject(
                    Entry("executingPlayer", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture))),
                    Entry("amount", NormalizedValue.CreateInteger(amount)),
                    Entry("sourceId", NormalizedValue.CreateString(sourceId ?? string.Empty))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ResourceEffectExecutor.DefinitionVersion
            };
        }

        private static EffectSpec Create(string typeId, int playerId, ResourceType type, int amount, string sourceId)
        {
            return new EffectSpec(
                typeId,
                CreateObject(
                    Entry("executingPlayer", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture))),
                    Entry("resourceType", NormalizedValue.CreateString(type.ToString())),
                    Entry("amount", NormalizedValue.CreateInteger(amount)),
                    Entry("sourceId", NormalizedValue.CreateString(sourceId ?? string.Empty))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ResourceEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValue CreateObject(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>(entries));
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    public sealed class ResourceEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";
        public const string PaymentConfirmationStage = "awaiting_payment_confirmation";
        public const string PaymentConfirmationInteractionTypeId = "effect.resource.payment_confirmation";
        public const string PaymentConfirmationCandidateId = "pay";

        private readonly bool isScoreExecutor;
        private readonly bool isPaymentExecutor;
        private readonly bool targetsOpponents;

        private ResourceEffectExecutor(bool isPaymentExecutor, bool isScoreExecutor, bool targetsOpponents = false)
        {
            this.isPaymentExecutor = isPaymentExecutor;
            this.isScoreExecutor = isScoreExecutor;
            this.targetsOpponents = targetsOpponents;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            ResourceLossEffectExecutor.Register(registry);
            RegisterIfMissing(registry, ResourceEffectTypeIds.Gain, new ResourceEffectExecutor(false, false));
            RegisterIfMissing(registry, ResourceEffectTypeIds.GainOpponents, new ResourceEffectExecutor(false, false, true));
            RegisterIfMissing(registry, ResourceEffectTypeIds.Pay, new ResourceEffectExecutor(true, false));
            RegisterIfMissing(registry, ResourceEffectTypeIds.GainScore, new ResourceEffectExecutor(false, true));
        }

        private static void RegisterIfMissing(EffectRegistry registry, string typeId, ResourceEffectExecutor executor)
        {
            EffectRegistration ignored;
            if (registry.TryGet(typeId, out ignored)) return;
            registry.Register(new EffectRegistration(typeId, executor.Execute, executor.isPaymentExecutor ? EffectExecutorKind.IntrinsicFlow : EffectExecutorKind.Atomic, DefinitionVersion)
            {
                Validator = executor.ValidateSpec
            });
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult == null
                    ? NormalizedValue.CreateNull()
                    : context.Node.NormalizedResult.Clone());
            }

            if (isPaymentExecutor && ItemTransferEffectExecutor.Value(context.Node.NormalizedArguments, "cost") != null)
            {
                var args = context.Node.NormalizedArguments;
                string error = ValidateCompositePayment(args);
                if (error.Length > 0) return EffectStepResult.Failed(error);
                int payingPlayer = int.Parse(ItemTransferEffectExecutor.Text(args, "player"), CultureInfo.InvariantCulture);
                if (payingPlayer != context.Node.PlayerId) return EffectStepResult.Failed("payment_permission_denied");
                var target = context.State.FindPlayer(payingPlayer);
                var cost = ReadCompositeCost(args);
                if (target == null || target.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("payment_permission_denied");
                return ExecutePayment(context, cost, NormalizedValue.CreateString("paid"));
            }
            int playerId;
            ResourceType resourceType = ResourceType.Originium;
            int amount;
            string diagnostic;
            bool parsed = isScoreExecutor
                ? TryReadScore(context.Node.NormalizedArguments, out playerId, out amount, out diagnostic)
                : TryRead(context.Node.NormalizedArguments, out playerId, out resourceType, out amount, out diagnostic);
            if (!parsed)
            {
                return EffectStepResult.Failed("invalid_arguments", NormalizedValue.CreateString(diagnostic));
            }

            PlayerState player = context.State.FindPlayer(playerId);
            if (player == null)
            {
                return EffectStepResult.Failed("invalid_player");
            }

            if (isScoreExecutor)
            {
                player.Score += amount;
                return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.GainScore, amount, resourceType));
            }

            if (targetsOpponents)
            {
                for (int i = 0; i < context.State.Players.Count; i++)
                {
                    PlayerState opponent = context.State.Players[i];
                    if (opponent == null || opponent.PlayerId == playerId) continue;
                    int currentOpponentAmount = opponent.Resources.Get(resourceType);
                    opponent.Resources.Set(resourceType, currentOpponentAmount + amount);
                }

                return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.GainOpponents, amount, resourceType));
            }

            int current = player.Resources.Get(resourceType);
            if (isPaymentExecutor)
            {
                if (playerId != context.Node.PlayerId) return EffectStepResult.Failed("payment_permission_denied");
                var cost = new ResourceSet(); cost.Set(resourceType, amount);
                return ExecutePayment(context, cost, Result(playerId, ResourceEffectTypeIds.Pay, amount, resourceType));
            }

            player.Resources.Set(resourceType, current + amount);
            return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.Gain, amount, resourceType));
        }

        private static readonly ResourceType[] BasicPaymentTypes = { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron };
        private static List<ZoneInstanceState> PaymentZones(GameState state, int player)
        {
            return state.ItemZones.Zones.Where(zone => zone.ParentKind == ZoneParentKind.Item && zone.Configuration?.PaymentPolicyId == "core.basic_resource_substitution" &&
                zone.Configuration.MaxMarkersPerPayment == 2 && state.ItemZones.Items.Any(card => card.ItemId == zone.ParentId && card.ControllerSubject.PlayerId == player &&
                    state.ItemZones.Zones.Any(parent => parent.ZoneId == card.ZoneId && parent.TemplateId == "core.zone.character_persistent")) &&
                state.CharacterUses.Any(use => use.CardInstanceId == zone.ParentId && use.Modes.Any(mode => mode.SkillType == "persistent" && mode.Activated && mode.SubscriptionActive))).ToList();
        }
        private static List<string> PaymentMarkers(GameState state, ZoneInstanceState zone, int player) => state.ItemZones.Items.Where(item => item.ZoneId == zone.ZoneId && item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player).Select(item => item.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        private static ResourceSet RemainingCost(ResourceSet original, int[] allocation)
        {
            var remaining = GameStateCloneService.DeepClone(original);
            for (int i = 0; i < 3; i++) remaining.Set(BasicPaymentTypes[i], remaining.Get(BasicPaymentTypes[i]) - allocation[i]);
            return remaining;
        }
        private static List<string> PaymentCandidates(GameState state, int player, ResourceSet cost)
        {
            var candidates = new List<string>(); var stock = state.FindPlayer(player).Resources;
            if (stock.CanPay(cost)) candidates.Add("pay|0|0|0|0");
            foreach (var zone in PaymentZones(state, player))
            {
                int maximum = Math.Min(2, PaymentMarkers(state, zone, player).Count);
                for (int rock = 0; rock <= Math.Min(maximum, cost.Originium); rock++)
                for (int shard = 0; shard <= Math.Min(maximum - rock, cost.OriginiumShard); shard++)
                for (int iron = 0; iron <= Math.Min(maximum - rock - shard, cost.Iron); iron++)
                {
                    if (rock + shard + iron == 0) continue;
                    if (stock.CanPay(RemainingCost(cost, new[] { rock, shard, iron }))) candidates.Add("pay|" + zone.ZoneId + "|" + rock + "|" + shard + "|" + iron);
                }
            }
            return candidates;
        }
        public static bool CanPayWithStoredMarkers(GameState state, int player, ResourceSet cost) => state.FindPlayer(player) != null && PaymentCandidates(state, player, cost).Count > 0;
        private static NormalizedValue CostValue(ResourceSet cost) => Object("Originium", NormalizedValue.CreateInteger(cost.Originium), "OriginiumShard", NormalizedValue.CreateInteger(cost.OriginiumShard), "Iron", NormalizedValue.CreateInteger(cost.Iron), "PureOriginium", NormalizedValue.CreateInteger(cost.PureOriginium), "GoldVoucher", NormalizedValue.CreateInteger(cost.GoldVoucher));
        public static EffectStepResult ExecutePayment(EffectExecutionContext context, ResourceSet requested, NormalizedValue result)
        {
            var node = context.Node; var player = context.State.FindPlayer(node.PlayerId);
            bool legacy = node.FlowStage == "choose_substitution" || Value(node.ContinuationData, "paymentPlayerId") != null;
            if (!legacy)
            {
                var parent = context.State.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == node.ParentEffectId);
                if (parent?.EffectTypeId == EffectTypeIds.Condition &&
                    Flag(parent.NormalizedArguments, "ruleCondition") && parent.FlowStage == "left:0" && parent.NestedGroupSizes[0] == 1 &&
                    parent.NestedEffects[0].EffectTypeId == ResourceEffectTypeIds.Pay && node.EffectTypeId == ResourceEffectTypeIds.Pay &&
                    parent.ChildEffectIds.Count > 0 && parent.ChildEffectIds[0] == node.EffectId)
                {
                    var conditionPayment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, requested);
                    conditionPayment.SourceKind = "condition";
                    conditionPayment.RuleConditionOwnerEffectId = parent.EffectId;
                }
                var before = ResourcePaymentPreparationService.AwaitBeforePayment(context, requested);
                if (before != null) return before;
                if (node.Payment.IsCommitted || node.Payment.IsCommitRequested)
                    return ResourcePaymentSubstitutionEffectExecutor.CommitPayment(context, requested, result);
                if (node.FlowStage == ResourcePaymentPreparationService.BeforePaymentStage)
                {
                    bool requireConfirmation = node.Payment.SourceKind == "condition" || node.Payment.Preparation != null ||
                        node.Payment.AllowsSubstitution && PaymentZones(context.State, node.PlayerId).Any(zone => PaymentMarkers(context.State, zone, node.PlayerId).Count > 0) &&
                        BasicPaymentTypes.Any(type => requested.Get(type) > 0);
                    if (!requireConfirmation)
                    {
                        node.Payment.IsCommitRequested = true;
                        return ResourcePaymentSubstitutionEffectExecutor.CommitPayment(context, requested, result);
                    }
                    var facts = ResourcePaymentPreparationService.Facts(context.State, node);
                    facts.Properties.Add(new NormalizedValueEntry { Name = "cost", Value = ResourcePaymentPreparationService.Cost(requested) });
                    facts.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    bool condition = node.Payment.SourceKind == "condition";
                    if (condition)
                    {
                        facts.Properties.Add(new NormalizedValueEntry { Name = "ruleConditionOwnerEffectId", Value = NormalizedValue.CreateString(node.Payment.RuleConditionOwnerEffectId) });
                        facts.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    }
                    var request = new EffectInteractionSpec { InteractionTypeId = condition ? "action.decline_effect" : PaymentConfirmationInteractionTypeId,
                        AnsweringPlayerId = node.PlayerId, Visibility = "owner", PromptKey = condition ? "effect.decline" : "resource.payment.confirmation",
                        AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = condition, PromptParameters = facts,
                        Presentation = CreatePaymentPresentation(node, parent, requested, condition) };
                    request.CandidateIds.Add(condition ? "continue" : PaymentConfirmationCandidateId);
                    if (condition) request.CandidateIds.Add("decline");
                    if (node.Payment.Preparation?.AuthorizationKind == "effect_build" &&
                        ResourcePaymentPreparationService.ValidateEffectBuildAssociation(context.State, node, out var selection) && Flag(selection.NormalizedArguments, "allowDecline"))
                    { request.AllowDecline = true; request.CandidateIds.Add("decline"); }
                    return EffectStepResult.Continue(PaymentConfirmationStage).AddInteraction(PrepareInteraction(context, request));
                }
            }
            // 新窗口由Host在真实付款宿主建立时显式创建；不把旧在途组合请求改成新协议。
            if (node.Payment != null)
            {
                ResourcePaymentRuntimeState payment;
                try { payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, requested); }
                catch (InvalidOperationException error) { return EffectStepResult.Failed(error.Message); }
                if (payment.IsCommitted || payment.IsCommitRequested)
                    return ResourcePaymentSubstitutionEffectExecutor.CommitPayment(context, requested, result);
                if (node.FlowStage == PaymentConfirmationStage)
                {
                    var answer = context.GetLatestInteractionAnswer();
                    if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                    string id = answer?.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer?.StringValue;
                    if (answer?.Kind == NormalizedValueKind.Boolean && payment.SourceKind == "condition") id = answer.BooleanValue ? "continue" : "decline";
                    if (id == "decline" && payment.SourceKind == "condition")
                    {
                        ResourcePaymentSubstitutionEffectExecutor.CancelDraft(node);
                        return EffectStepResult.Failed("player_declined");
                    }
                    if (payment.Preparation == null && (id == PaymentConfirmationCandidateId || id == "continue" && payment.SourceKind == "condition"))
                    {
                        payment.IsCommitRequested = true;
                        return ResourcePaymentSubstitutionEffectExecutor.CommitPayment(context, requested, result);
                    }
                }
                return EffectStepResult.NoProgress("等待当前付款宿主提交或完成替代分配。");
            }
            if (player == null) return EffectStepResult.Failed("invalid_player");
            if (node.FlowStage.Length == 0 && (!BasicPaymentTypes.Any(type => requested.Get(type) > 0) || PaymentZones(context.State, node.PlayerId).All(zone => PaymentMarkers(context.State, zone, node.PlayerId).Count == 0)))
                return player.Resources.TryPay(requested) ? EffectStepResult.Completed(result) : EffectStepResult.Failed("insufficient_resource");
            if (node.FlowStage.Length == 0)
            {
                if (!CanPayWithStoredMarkers(context.State, node.PlayerId, requested)) return EffectStepResult.Failed("insufficient_resource");
                var payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, requested);
                var cost = Object("originium", NormalizedValue.CreateInteger(requested.Originium),
                    "originiumShard", NormalizedValue.CreateInteger(requested.OriginiumShard), "iron", NormalizedValue.CreateInteger(requested.Iron),
                    "pureOriginium", NormalizedValue.CreateInteger(requested.PureOriginium), "goldVoucher", NormalizedValue.CreateInteger(requested.GoldVoucher));
                var request = new EffectInteractionSpec { InteractionTypeId = PaymentConfirmationInteractionTypeId,
                    AnsweringPlayerId = node.PlayerId, Visibility = "owner", PromptKey = "resource.payment.confirmation",
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = false,
                    PromptParameters = Object("paymentId", NormalizedValue.CreateString(payment.PaymentId),
                        "ownerEffectId", NormalizedValue.CreateString(node.EffectId), "cost", cost,
                        "paymentOptions", NormalizedValue.CreateArray(new[] { Object("candidateId", NormalizedValue.CreateString(PaymentConfirmationCandidateId), "cost", cost.Clone()) })),
                    Presentation = new GameplayInteractionPresentation {
                        Prompt = new GameplayTextRef("core:resource.payment.confirmation.prompt", cost.Properties.Select(entry =>
                            new GameplayTextArgument { Name = entry.Name, Kind = "integer", Value = entry.Value.Clone() }).ToArray()),
                        Options = new List<GameplayOptionPresentation> { new GameplayOptionPresentation {
                            CandidateId = PaymentConfirmationCandidateId, Label = new GameplayTextRef("core:resource.payment.confirmation.pay") } }
                    } };
                request.CandidateIds.Add(PaymentConfirmationCandidateId);
                return EffectStepResult.Continue(PaymentConfirmationStage).AddInteraction(PrepareInteraction(context, request));
            }
            // 已持久化旧组合请求只由原协议继续，不能改成新typed付款或改变候选范围。
            if (Number(node.ContinuationData, "paymentPlayerId", -1) != node.PlayerId || Value(node.ContinuationData, "paymentCost")?.ToDeterministicString() != CostValue(requested).ToDeterministicString())
                return EffectStepResult.Failed("payment_cost_changed");
            var candidates = PaymentCandidates(context.State, node.PlayerId, requested);
            if (candidates.Count == 0) return EffectStepResult.Failed("insufficient_resource");
            if (node.FlowStage == "choose_substitution")
            {
                string answer = Answer(context);
                if (candidates.Contains(answer))
                {
                    var parts = answer.Split('|'); var allocation = parts.Skip(2).Select(int.Parse).ToArray(); int total = allocation.Sum();
                    var zone = total > 0 ? context.State.ItemZones.Zones.Single(value => value.ZoneId == parts[1]) : null;
                    var ids = total > 0 ? PaymentMarkers(context.State, zone, node.PlayerId).Take(total).ToList() : new List<string>();
                    ItemTransferRequest transfer = total > 0 ? new ItemTransferRequest { ExecutingPlayerId = node.PlayerId, ItemIds = ids, ExpectedSource = new ItemPositionRef { ZoneId = zone.ZoneId },
                        Destination = new ZoneRef { ZoneId = FindZone(context.State, "player_markers", node.PlayerId).ZoneId }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = total } : null;
                    if (transfer != null) { var valid = new ItemTransferService().Validate(context.State, transfer); if (!valid.IsValid) return EffectStepResult.Failed(valid.ReasonCode); }
                    var remaining = RemainingCost(requested, allocation);
                    if (!player.Resources.CanPay(remaining)) return EffectStepResult.Failed("insufficient_resource");
                    Set(node, "paymentZoneId", NormalizedValue.CreateString(zone?.ZoneId ?? "")); Set(node, "selectedSubstitution", NormalizedValue.CreateArray(allocation.Select(value => NormalizedValue.CreateInteger(value)).ToList()));
                    if (transfer != null) new ItemTransferService().RelocateItems(context, transfer);
                    if (!player.Resources.TryPay(remaining)) throw new InvalidOperationException("payment_balance_changed_during_commit");
                    if (zone != null) CharacterLifecycleService.ExitEmptyCarriers(context, new[] { zone.ZoneId });
                    return EffectStepResult.Completed(result).WithFlowStage("finished");
                }
            }
            var interaction = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = node.PlayerId, Visibility = "owner", PromptKey = "resource.payment.substitution", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
            interaction.CandidateIds.AddRange(candidates);
            interaction.PromptParameters = Object("cost", CostValue(requested), "maxMarkers", NormalizedValue.CreateInteger(2));
            return EffectStepResult.Continue("choose_substitution").AddInteraction(PrepareInteraction(context, interaction));
        }

        private static GameplayInteractionPresentation CreatePaymentPresentation(EffectNodeRuntimeState node,
            EffectNodeRuntimeState parent, ResourceSet cost, bool condition)
        {
            if (condition) return GameplayPresentationNormalizer.Clone(parent?.Presentation?.Interaction) ?? new GameplayInteractionPresentation();
            var source = node.Presentation?.Interaction;
            var presentation = GameplayPresentationNormalizer.Clone(source) ?? new GameplayInteractionPresentation();
            var costText = new GameplayTextRef("core:resource.payment.confirmation.prompt",
                ResourcePaymentPreparationService.Cost(cost).Properties.Select(entry => new GameplayTextArgument {
                    Name = entry.Name, Kind = "integer", Value = entry.Value.Clone() }).ToArray());
            // 来源正文仍作主文档；现有付款宿主会把Prompt/Progress合并渲染。
            if (presentation.Prompt == null) presentation.Prompt = costText;
            else if (presentation.Progress == null) presentation.Progress = costText;
            if (!condition && !presentation.Options.Any(option => option.CandidateId == PaymentConfirmationCandidateId))
                presentation.Options.Add(new GameplayOptionPresentation { CandidateId = PaymentConfirmationCandidateId,
                    Label = new GameplayTextRef("core:resource.payment.confirmation.pay") });
            return presentation;
        }
        public static bool TryValidatePendingPayments(GameState state, out string reason)
        {
            if (!ResourcePaymentSubstitutionEffectExecutor.TryValidatePaymentRecords(state, out reason)) return false;
            reason = "payment_substitution_state_invalid";
            foreach (var node in state.EffectRuntime.EffectNodes.Where(node => node.FlowStage == "choose_substitution" || Value(node.ContinuationData, "paymentCost") != null))
            {
                if (node.EffectTypeId != ResourceEffectTypeIds.Pay && node.EffectTypeId != FacilityBuildEffectExecutor.PaymentTypeId || !new[] { "choose_substitution", "finished" }.Contains(node.FlowStage)) return false;
                var cost = Value(node.ContinuationData, "paymentCost"); var player = Value(node.ContinuationData, "paymentPlayerId");
                string[] keys = { "Originium", "OriginiumShard", "Iron", "PureOriginium", "GoldVoucher" };
                if (cost?.Kind != NormalizedValueKind.Object || cost.Properties.Count != 5 || cost.Properties.Any(entry => !keys.Contains(entry.Name) || entry.Value.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > int.MaxValue) ||
                    player?.Kind != NormalizedValueKind.Integer || player.IntegerValue != node.PlayerId || state.FindPlayer(node.PlayerId) == null) return false;
                var selected = Value(node.ContinuationData, "selectedSubstitution");
                if (selected != null && (selected.Kind != NormalizedValueKind.Array || selected.Items.Count != 3 || selected.Items.Any(value => value.Kind != NormalizedValueKind.Integer || value.IntegerValue < 0 || value.IntegerValue > 2) || selected.Items.Sum(value => value.IntegerValue) > 2 || Enumerable.Range(0, 3).Any(i => selected.Items[i].IntegerValue > Number(cost, keys[i])))) return false;
                var selectedZone = Value(node.ContinuationData, "paymentZoneId");
                if (node.FlowStage == "finished" && (selected == null || selectedZone?.Kind != NormalizedValueKind.String || selected.Items.Sum(v => v.IntegerValue) > 0 && !state.ItemZones.Zones.Any(z => z.ZoneId == selectedZone.StringValue && z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution"))) return false;
            }
            reason = ""; return true;
        }

        private static bool TryRead(
            NormalizedValue args,
            out int playerId,
            out ResourceType resourceType,
            out int amount,
            out string diagnostic)
        {
            playerId = -1;
            resourceType = ResourceType.Originium;
            amount = 0;
            diagnostic = string.Empty;
            NormalizedValue player;
            NormalizedValue type;
            NormalizedValue value;
            if (!TryGet(args, "executingPlayer", out player) ||
                !TryGet(args, "resourceType", out type) ||
                !TryGet(args, "amount", out value) ||
                !TryReadPlayer(player, out playerId) ||
                type == null || type.Kind != NormalizedValueKind.String ||
                !Enum.TryParse(type.StringValue, true, out resourceType) ||
                value == null || value.Kind != NormalizedValueKind.Integer ||
                value.IntegerValue <= 0 || value.IntegerValue > int.MaxValue)
            {
                diagnostic = "资源 Effect 必须包含有效的 executingPlayer、resourceType 和正 amount。";
                return false;
            }

            amount = (int)value.IntegerValue;
            return true;
        }

        private string ValidateSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
            {
                return "资源 Effect 参数必须是对象。";
            }

            if (isPaymentExecutor && ItemTransferEffectExecutor.Value(spec.NormalizedArguments, "cost") != null)
                return ValidateCompositePayment(spec.NormalizedArguments);
            NormalizedValue player;
            NormalizedValue type;
            NormalizedValue amount;
            if (!TryGet(spec.NormalizedArguments, "executingPlayer", out player) ||
                !TryGet(spec.NormalizedArguments, "amount", out amount) ||
                (!isScoreExecutor && !TryGet(spec.NormalizedArguments, "resourceType", out type)))
            {
                return "资源 Effect 缺少必需字段。";
            }

            int ignoredPlayer;
            ResourceType ignoredType;
            int ignoredAmount;
            string diagnostic;
            bool valid = isScoreExecutor
                ? TryReadScore(spec.NormalizedArguments, out ignoredPlayer, out ignoredAmount, out diagnostic)
                : TryRead(spec.NormalizedArguments, out ignoredPlayer, out ignoredType, out ignoredAmount, out diagnostic);
            return valid
                ? string.Empty
                : diagnostic;
        }

        public static string ValidateCompositePayment(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Exists(entry => entry.Name != "player" && entry.Name != "cost" && entry.Name != "kind" && entry.Name != "effectTypeId" && entry.Name != "allowSubstitution") ||
                Value(args, "allowSubstitution") is NormalizedValue flag && flag.Kind != NormalizedValueKind.Boolean)
                return "composite_payment_arguments_invalid";
            var player = ItemTransferEffectExecutor.Value(args, "player");
            var cost = ItemTransferEffectExecutor.Value(args, "cost");
            if (player?.Kind != NormalizedValueKind.String || !int.TryParse(player.StringValue, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 ||
                cost?.Kind != NormalizedValueKind.Object || cost.Properties.Count == 0) return "composite_payment_arguments_invalid";
            var keys = new HashSet<string>();
            foreach (var entry in cost.Properties)
            {
                if (!keys.Add(entry.Name) || !Enum.TryParse(entry.Name, out ResourceType type) || !Enum.IsDefined(typeof(ResourceType), type) ||
                    entry.Value?.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > int.MaxValue) return "composite_payment_cost_invalid";
            }
            return cost.Properties.Count > 0 ? "" : "composite_payment_cost_invalid";
        }
        private static ResourceSet ReadCompositeCost(NormalizedValue args)
        {
            var cost = new ResourceSet();
            foreach (var entry in ItemTransferEffectExecutor.Value(args, "cost").Properties)
                cost.Set((ResourceType)Enum.Parse(typeof(ResourceType), entry.Name), (int)entry.Value.IntegerValue);
            return cost;
        }

        private static bool TryReadScore(
            NormalizedValue args,
            out int playerId,
            out int amount,
            out string diagnostic)
        {
            playerId = -1;
            amount = 0;
            diagnostic = string.Empty;
            NormalizedValue player;
            NormalizedValue value;
            if (!TryGet(args, "executingPlayer", out player) ||
                !TryGet(args, "amount", out value) ||
                !TryReadPlayer(player, out playerId) ||
                value == null || value.Kind != NormalizedValueKind.Integer ||
                value.IntegerValue <= 0 || value.IntegerValue > int.MaxValue)
            {
                diagnostic = "分数 Effect 必须包含有效的 executingPlayer 和正 amount。";
                return false;
            }

            amount = (int)value.IntegerValue;
            return true;
        }

        private static NormalizedValue Result(int playerId, string operation, int amount, ResourceType resourceType)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(amount) },
                new NormalizedValueEntry { Name = "operation", Value = NormalizedValue.CreateString(operation) },
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateString(resourceType.ToString()) }
            });
        }

        private static bool TryReadPlayer(NormalizedValue value, out int playerId)
        {
            playerId = -1;
            if (value == null) return false;
            if (value.Kind == NormalizedValueKind.Integer)
            {
                playerId = (int)value.IntegerValue;
                return value.IntegerValue >= 0;
            }

            return value.Kind == NormalizedValueKind.StableReference &&
                   value.ReferenceType == "player" &&
                   TryParsePlayerReference(value.ReferenceId, out playerId) &&
                   playerId >= 0;
        }

        private static bool TryParsePlayerReference(string referenceId, out int playerId)
        {
            playerId = -1;
            string value = referenceId ?? string.Empty;
            if (value.StartsWith("p", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId);
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
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
}
