using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Travel;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Effects
{
    public delegate bool GameplayPresentationProvider(
        string scriptPath, GameplayPresentationContext context,
        out GameplayPresentationResult result, out string diagnostic);

    public enum EffectExecutorKind
    {
        Atomic,
        IntrinsicFlow
    }

    public enum EffectHandlerRole
    {
        Primary,
        Observer,
        Continuation
    }

    public enum EffectRouteTier
    {
        Targeted = 0,
        Observer = 1
    }

    public static class EffectTypeIds
    {
        public const string Condition = "effect.flow.condition";
        public const string PlaceInfluence = "effect.influence.place";
        public const string RemoveInfluence = "effect.influence.remove";
        public const string MoveInfluence = "effect.influence.move";
        public const string ReplaceInfluence = "effect.influence.replace";
    }

    public static class EffectFaultCodes
    {
        public const string UnknownEffectType = "unknown_effect_type";
        public const string InvalidEffectSpec = "invalid_effect_spec";
        public const string InvalidTransition = "invalid_transition";
        public const string HandlerException = "handler_exception";
        public const string InvalidHandlerResult = "invalid_handler_result";
        public const string UnknownEventHandler = "unknown_event_handler";
        public const string EventDispatchException = "event_dispatch_exception";
        public const string InvalidEventResponse = "invalid_event_response";
        public const string BlockerTargetMissing = "blocker_target_missing";
        public const string BlockerCycle = "blocker_cycle";
        public const string BlockerOnTerminal = "blocker_on_terminal";
        public const string InteractionInvalid = "interaction_invalid";
        public const string NoProgress = "no_progress";
        public const string StepLimitExceeded = "step_limit_exceeded";
        public const string NodeLimitExceeded = "node_limit_exceeded";
        public const string EventLimitExceeded = "event_limit_exceeded";
        public const string DispatchLimitExceeded = "dispatch_limit_exceeded";
        public const string TreeDepthExceeded = "tree_depth_exceeded";
        public const string InvalidRuntimeState = "invalid_runtime_state";
        public const string DefinitionVersionMismatch = "definition_version_mismatch";
        public const string ContinuationBindingMissing = "continuation_binding_missing";
        public const string ContinuationContentHashMismatch = "continuation_content_hash_mismatch";
    }

    public sealed class EffectRuntimeLimits
    {
        public EffectRuntimeLimits(
            int maxSteps,
            int maxTreeDepth,
            int maxNodeCount,
            int maxEventCount,
            int maxDispatchCount)
        {
            if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
            if (maxTreeDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maxTreeDepth));
            if (maxNodeCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodeCount));
            if (maxEventCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxEventCount));
            if (maxDispatchCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxDispatchCount));

            MaxSteps = maxSteps;
            MaxTreeDepth = maxTreeDepth;
            MaxNodeCount = maxNodeCount;
            MaxEventCount = maxEventCount;
            MaxDispatchCount = maxDispatchCount;
        }

        public int MaxSteps { get; }
        public int MaxTreeDepth { get; }
        public int MaxNodeCount { get; }
        public int MaxEventCount { get; }
        public int MaxDispatchCount { get; }

        public static EffectRuntimeLimits Default { get; } = new EffectRuntimeLimits(
            maxSteps: 10000,
            maxTreeDepth: 64,
            maxNodeCount: 2048,
            maxEventCount: 4096,
            maxDispatchCount: 16384);
    }

    [Serializable]
    public sealed class EffectSpec
    {
        public YC.Domain.Presentation.SettlementEffectDefinition Settlement;
        public YC.Domain.Presentation.EffectPresentationDefinition Presentation;
        public string EffectTypeId = string.Empty;
        public string DefinitionVersion = string.Empty;
        public string SourceId = string.Empty;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public NormalizedValue NormalizedArguments = NormalizedValue.CreateObject(
            new List<NormalizedValueEntry>());
        public bool AllowDecline;
        public int DecisionPlayerId = -1;
        public string DeclinePromptKey = string.Empty;
        public string UnavailablePolicy = string.Empty;
        public string CompletionHandlerId = string.Empty;
        public NormalizedValue ContinuationContext;
        public string ContinuationContentId = string.Empty;
        public string ContinuationContentInstanceId = string.Empty;
        public string ContinuationAbilityId = string.Empty;
        public string ContinuationDefinitionVersion = string.Empty;
        public string ContinuationContentHash = string.Empty;
        public string StableKey = string.Empty;
        public List<EffectSpec> Children = new List<EffectSpec>();
        public List<List<EffectSpec>> NestedEffectGroups = new List<List<EffectSpec>>();

        public EffectSpec()
        {
        }

        public EffectSpec(string effectTypeId)
            : this(effectTypeId, NormalizedValue.CreateObject(new List<NormalizedValueEntry>()))
        {
        }

        public EffectSpec(string effectTypeId, NormalizedValue normalizedArguments)
        {
            EffectTypeId = effectTypeId ?? string.Empty;
            NormalizedArguments = normalizedArguments ?? NormalizedValue.CreateNull();
        }

        public static EffectSpec Create(
            string effectTypeId,
            NormalizedValue normalizedArguments = null,
            int playerId = -1)
        {
            return new EffectSpec(effectTypeId, normalizedArguments)
            {
                PlayerId = playerId
            };
        }

        public static EffectSpec Condition(
            IList<EffectSpec> leftEffects,
            IList<EffectSpec> rightEffects,
            bool allowDecline = false,
            int decisionPlayerId = -1,
            bool ruleCondition = false)
        {
            var spec = new EffectSpec(EffectTypeIds.Condition)
            {
                AllowDecline = allowDecline,
                DecisionPlayerId = decisionPlayerId,
                PlayerId = ruleCondition ? (decisionPlayerId > 0 ? decisionPlayerId : (leftEffects ?? rightEffects)?.FirstOrDefault()?.PlayerId ?? -1) : -1,
                NestedEffectGroups = new List<List<EffectSpec>>
                {
                    CopySpecs(leftEffects),
                    CopySpecs(rightEffects)
                }
            };
            spec.NormalizedArguments = ItemTransferEffectExecutor.Object("ruleCondition", NormalizedValue.CreateBoolean(ruleCondition));
            return spec;
        }

        public EffectSpec WithExecutionOptions(
            bool allowDecline,
            int decisionPlayerId = -1,
            string declinePromptKey = "",
            string unavailablePolicy = "")
        {
            AllowDecline = allowDecline;
            DecisionPlayerId = decisionPlayerId;
            DeclinePromptKey = declinePromptKey ?? string.Empty;
            UnavailablePolicy = unavailablePolicy ?? string.Empty;
            return this;
        }

        internal EffectSpecRuntimeState ToRuntimeState()
        {
            string reason = string.Empty;
            if (NormalizedArguments == null || !NormalizedArguments.TryValidate(out reason))
            {
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, reason);
            }

            var runtime = new EffectSpecRuntimeState
            {
                Settlement = GameStateCloneService.DeepClone(Settlement),
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(Presentation),
                EffectTypeId = EffectTypeId ?? string.Empty,
                DefinitionVersion = DefinitionVersion ?? string.Empty,
                SourceId = SourceId ?? string.Empty,
                PlayerId = PlayerId,
                Visibility = Visibility ?? string.Empty,
                NormalizedArguments = NormalizedArguments.Clone(),
                StableKey = StableKey ?? string.Empty,
                AllowDecline = AllowDecline,
                DecisionPlayerId = DecisionPlayerId,
                DeclinePromptKey = DeclinePromptKey ?? string.Empty,
                UnavailablePolicy = UnavailablePolicy ?? string.Empty,
                CompletionHandlerId = CompletionHandlerId ?? string.Empty,
                ContinuationContext = ContinuationContext?.Clone(),
                ContinuationContentId = ContinuationContentId ?? string.Empty,
                ContinuationContentInstanceId = ContinuationContentInstanceId ?? string.Empty,
                ContinuationAbilityId = ContinuationAbilityId ?? string.Empty,
                ContinuationDefinitionVersion = ContinuationDefinitionVersion ?? string.Empty,
                ContinuationContentHash = ContinuationContentHash ?? string.Empty,
                NestedEffects = new List<EffectSpecRuntimeState>(),
                NestedGroupSizes = new List<int>()
            };

            if (NestedEffectGroups != null)
            {
                for (int i = 0; i < NestedEffectGroups.Count; i++)
                {
                    IList<EffectSpec> group = NestedEffectGroups[i];
                    if (group == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 组不能为 null。");
                    runtime.NestedGroupSizes.Add(group.Count);
                    for (int j = 0; j < group.Count; j++)
                    {
                        runtime.NestedEffects.Add(group[j].ToRuntimeState());
                    }
                }
            }

            if (Children != null && Children.Count > 0)
            {
                runtime.NestedGroupSizes.Add(Children.Count);
                for (int i = 0; i < Children.Count; i++)
                {
                    if (Children[i] == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "Effect 子节点不能为 null。");
                    runtime.NestedEffects.Add(Children[i].ToRuntimeState());
                }
            }

            return runtime;
        }

        public EffectSpecRuntimeState ToPersistentDefinition() => ToRuntimeState();

        internal static List<EffectSpec> CopySpecs(IList<EffectSpec> source)
        {
            var result = new List<EffectSpec>();
            if (source == null) return result;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null)
                {
                    throw new ArgumentException("EffectSpec 列表不能包含 null。", nameof(source));
                }

                result.Add(source[i]);
            }

            return result;
        }
    }

    public sealed class EffectStepResult
    {
        internal sealed class IndependentChildren
        {
            internal string Key;
            internal int StartIndex;
            internal List<EffectSpec> Members;
        }

        internal readonly List<IndependentChildren> IndependentChildGroups = new List<IndependentChildren>();

        public EffectStepResult()
        {
            ChildEffects = new List<EffectSpec>();
            Events = new List<EffectEventRequest>();
            Interactions = new List<EffectInteractionSpec>();
            Logs = new List<EffectLogEntrySpec>();
            NormalizedResult = NormalizedValue.CreateNull();
        }

        public bool HasPendingOutcome { get; private set; }
        public EffectPendingOutcome PendingOutcome { get; private set; }
        public string FailureReason { get; private set; }
        public NormalizedValue NormalizedResult { get; private set; }
        public string FlowStage { get; private set; }
        public bool ExplicitNoProgress { get; private set; }
        public List<EffectSpec> ChildEffects { get; }
        public List<EffectEventRequest> Events { get; }
        public List<EffectInteractionSpec> Interactions { get; }
        public List<EffectLogEntrySpec> Logs { get; }

        public static EffectStepResult Completed(NormalizedValue result = null)
        {
            return Outcome(EffectPendingOutcome.Completed, string.Empty, result);
        }

        public static EffectStepResult Failed(string reason, NormalizedValue result = null)
        {
            return Outcome(EffectPendingOutcome.Failed, reason ?? string.Empty, result);
        }

        public static EffectStepResult Continue(string flowStage = "")
        {
            return new EffectStepResult { FlowStage = flowStage ?? string.Empty };
        }

        public static EffectStepResult NoProgress(string diagnostic = "")
        {
            return new EffectStepResult
            {
                ExplicitNoProgress = true,
                FailureReason = diagnostic ?? string.Empty
            };
        }

        public EffectStepResult AddChild(EffectSpec child)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            ChildEffects.Add(child);
            return this;
        }

        public EffectStepResult AddChildren(IList<EffectSpec> children)
        {
            if (children == null) return this;
            for (int i = 0; i < children.Count; i++) AddChild(children[i]);
            return this;
        }

        /// <summary>显式添加一个固定成员的独立子任务组；前后普通子任务仍是顺序屏障。</summary>
        public EffectStepResult AddIndependentChildren(string stableGroupKey, IList<EffectSpec> specs)
        {
            if (string.IsNullOrWhiteSpace(stableGroupKey)) throw new ArgumentException("独立子任务组 key 不能为空。", nameof(stableGroupKey));
            if (specs == null || specs.Count == 0) throw new ArgumentException("独立子任务组不能为空。", nameof(specs));
            if (IndependentChildGroups.Exists(group => group.Key == stableGroupKey))
                throw new ArgumentException("同一父节点不能重复添加或扩充独立子任务组。", nameof(stableGroupKey));
            var members = new List<EffectSpec>(specs);
            if (members.Exists(spec => spec == null)) throw new ArgumentException("独立子任务组成员不能为 null。", nameof(specs));
            IndependentChildGroups.Add(new IndependentChildren { Key = stableGroupKey, StartIndex = ChildEffects.Count, Members = members });
            ChildEffects.AddRange(members);
            return this;
        }

        public EffectStepResult AddEvent(EffectEventRequest ruleEvent)
        {
            if (ruleEvent == null) throw new ArgumentNullException(nameof(ruleEvent));
            Events.Add(ruleEvent);
            return this;
        }

        public EffectStepResult AddInteraction(EffectInteractionSpec interaction)
        {
            if (interaction == null) throw new ArgumentNullException(nameof(interaction));
            Interactions.Add(interaction);
            return this;
        }

        public EffectStepResult AddLog(EffectLogEntrySpec log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            Logs.Add(log);
            return this;
        }

        public EffectStepResult WithFlowStage(string flowStage)
        {
            FlowStage = flowStage ?? string.Empty;
            return this;
        }

        private static EffectStepResult Outcome(
            EffectPendingOutcome outcome,
            string failureReason,
            NormalizedValue result)
        {
            return new EffectStepResult
            {
                HasPendingOutcome = true,
                PendingOutcome = outcome,
                FailureReason = failureReason ?? string.Empty,
                NormalizedResult = result == null ? NormalizedValue.CreateNull() : result
            };
        }
    }

    public sealed class EffectLogEntrySpec
    {
        public int PlayerId = -1;
        public string Message = string.Empty;
    }

    public sealed class EffectEventRequest
    {
        public string EventId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string SourceEffectId { get; set; } = string.Empty;
        public string OwnerNodeId { get; set; } = string.Empty;
        public string RouteKey { get; set; } = string.Empty;
        public string TargetEntityId { get; set; } = string.Empty;
        public int PlayerId { get; set; } = -1;
        public NormalizedValue Payload { get; set; } = NormalizedValue.CreateNull();
        public NormalizedValue HostOnlyPayload { get; set; } = NormalizedValue.CreateNull();
        public RuleEventResponseKind ResponseKind { get; set; } = RuleEventResponseKind.Effects;
        public string DefinitionVersion { get; set; } = string.Empty;
        public string Visibility { get; set; } = "public";
        public string SemanticKey { get; set; } = string.Empty;
    }

    public sealed class EffectInteractionSpec
    {
        public YC.Domain.Presentation.GameplayInteractionPresentation Presentation { get; set; }
        public string SourceNodeId { get; set; } = string.Empty;
        public string InteractionTypeId { get; set; } = string.Empty;
        public string Visibility { get; set; } = "public";
        public string PromptKey { get; set; } = string.Empty;
        public NormalizedValue PromptParameters { get; set; } = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public int AnsweringPlayerId { get; set; } = -1;
        public string CandidateSetId { get; set; } = string.Empty;
        public int CandidateSetVersion { get; set; }
        public string CandidateResolutionId { get; set; } = string.Empty;
        public List<string> CandidateIds { get; } = new List<string>();
        public int MinSelections { get; set; }
        public int MaxSelections { get; set; }
        public bool AllowDecline { get; set; }
        public string AnswerSchema { get; set; } = string.Empty;
    }

    public sealed class EffectExecutionContext
    {
        private readonly Func<IReadOnlyList<DeferredResponseDependency>, bool> attachDeferredDependencies;
        internal EffectExecutionContext(
            GameState state,
            EffectNodeRuntimeState node,
            EffectRegistry registry,
            EffectRuntimeLimits limits = null)
            : this(state, node, registry, limits, null) { }

        internal EffectExecutionContext(GameState state, EffectNodeRuntimeState node, EffectRegistry registry,
            EffectRuntimeLimits limits, Func<IReadOnlyList<DeferredResponseDependency>, bool> attachDeferredDependencies)
        {
            State = state;
            contextNodeId = node.EffectId;
            Registry = registry;
            Limits = limits ?? EffectRuntimeLimits.Default;
            this.attachDeferredDependencies = attachDeferredDependencies;
        }

        public bool TryAttachDeferredResponseDependenciesOnWorking(IReadOnlyList<DeferredResponseDependency> dependencies)
        {
            if (attachDeferredDependencies == null) throw new InvalidOperationException("当前 Effect 没有旁路依赖的工作事务绑定。");
            return attachDeferredDependencies(dependencies);
        }

        public GameState State { get; }
        private readonly string contextNodeId;
        public EffectNodeRuntimeState Node => State.EffectRuntime.EffectNodes.Find(node => node.EffectId == contextNodeId);
        public EffectRegistry Registry { get; }
        public EffectRuntimeLimits Limits { get; }

        public IList<EffectNodeRuntimeState> ChildNodes
        {
            get
            {
                var result = new List<EffectNodeRuntimeState>();
                if (Node.ChildEffectIds == null) return result;
                for (int i = 0; i < Node.ChildEffectIds.Count; i++)
                {
                    var child = State.EffectRuntime.EffectNodes.Find(
                        candidate => candidate.EffectId == Node.ChildEffectIds[i]);
                    if (child != null) result.Add(child);
                }

                return result;
            }
        }

        public NormalizedValue GetLatestInteractionAnswer()
        {
            for (int i = State.EffectRuntime.InteractionRequests.Count - 1; i >= 0; i--)
            {
                InteractionRequest request = State.EffectRuntime.InteractionRequests[i];
                if (request != null && request.OwnerEffectId == Node.EffectId &&
                    request.Status == "answered")
                {
                    return request.NormalizedAnswer;
                }
            }

            return null;
        }
    }

    public sealed class EffectEventHandlerContext
    {
        public EffectEventHandlerContext(
            GameState state,
            RuleEvent ruleEvent,
            EffectNodeRuntimeState ownerNode,
            EffectEventHandlerRegistration registration)
        {
            State = state;
            Event = ruleEvent;
            OwnerNode = ownerNode;
            Registration = registration;
        }

        public GameState State { get; }
        public RuleEvent Event { get; }
        public EffectNodeRuntimeState OwnerNode { get; }
        public EffectEventHandlerRegistration Registration { get; }
    }

    public sealed class EffectRegistration
    {
        public EffectRegistration(
            string effectTypeId,
            Func<EffectExecutionContext, EffectStepResult> executor,
            EffectExecutorKind executorKind = EffectExecutorKind.Atomic,
            string definitionVersion = "v1")
        {
            if (string.IsNullOrEmpty(effectTypeId)) throw new ArgumentException("Effect 类型 ID 不能为空。", nameof(effectTypeId));
            if (executor == null) throw new ArgumentNullException(nameof(executor));
            EffectTypeId = effectTypeId;
            Executor = executor;
            ExecutorKind = executorKind;
            DefinitionVersion = definitionVersion ?? string.Empty;
        }

        public string EffectTypeId { get; }
        public string DefinitionVersion { get; }
        public EffectExecutorKind ExecutorKind { get; }
        public Func<EffectExecutionContext, EffectStepResult> Executor { get; }
        public Func<EffectSpec, string> Validator { get; set; }

        internal void Validate(EffectSpec spec)
        {
            if (spec == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "EffectSpec 不能为 null。");
            if (!string.IsNullOrEmpty(spec.DefinitionVersion) &&
                !string.Equals(spec.DefinitionVersion, DefinitionVersion, StringComparison.Ordinal))
            {
                throw new KernelException(
                    EffectFaultCodes.DefinitionVersionMismatch,
                    "Effect 定义版本不匹配：" + EffectTypeId);
            }

            if (Validator != null)
            {
                string reason = Validator(spec);
                if (!string.IsNullOrEmpty(reason))
                {
                    throw new KernelException(EffectFaultCodes.InvalidEffectSpec, reason);
                }
            }
        }
    }

    public sealed class EffectEventHandlerRegistration
    {
        public EventResponseAttachmentKind AttachmentKind { get; set; } = EventResponseAttachmentKind.RequiredChild;
        public Func<EffectEventHandlerContext, EffectSpec, DeferredEventResponseDescriptor> DescribeDeferredResponse { get; set; }
        public EffectEventHandlerRegistration(
            string subscriptionId,
            string eventType,
            string routeKey,
            string handlerId,
            Func<EffectEventHandlerContext, IList<EffectSpec>> handler,
            EffectHandlerRole role = EffectHandlerRole.Observer,
            int priority = 0,
            string contentInstanceId = "",
            string abilityId = "",
            string definitionVersion = "v1",
            string contentHash = "",
            string contentId = "")
        {
            if (string.IsNullOrEmpty(subscriptionId)) throw new ArgumentException("订阅 ID 不能为空。", nameof(subscriptionId));
            if (string.IsNullOrEmpty(eventType)) throw new ArgumentException("Event 类型不能为空。", nameof(eventType));
            if (string.IsNullOrEmpty(handlerId)) throw new ArgumentException("处理器 ID 不能为空。", nameof(handlerId));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            SubscriptionId = subscriptionId;
            EventType = eventType;
            RouteKey = routeKey ?? string.Empty;
            HandlerId = handlerId;
            Handler = handler;
            Role = role;
            Priority = priority;
            ContentInstanceId = contentInstanceId ?? string.Empty;
            AbilityId = abilityId ?? string.Empty;
            DefinitionVersion = definitionVersion ?? string.Empty;
            ContentHash = contentHash ?? string.Empty;
            ContentId = contentId ?? string.Empty;
        }

        public string SubscriptionId { get; }
        public string EventType { get; }
        public string RouteKey { get; }
        public string HandlerId { get; }
        public EffectHandlerRole Role { get; }
        public int Priority { get; }
        public string ContentInstanceId { get; }
        public string AbilityId { get; }
        public string DefinitionVersion { get; }
        public string ContentHash { get; }
        public string ContentId { get; }
        public Func<EffectEventHandlerContext, IList<EffectSpec>> Handler { get; }
        public string EffectTypeFilter { get; set; } = string.Empty;
        public string SourceEffectFilter { get; set; } = string.Empty;

        public int RouteTier
        {
            get { return Role == EffectHandlerRole.Observer ? (int)EffectRouteTier.Observer : (int)EffectRouteTier.Targeted; }
        }

        internal bool Matches(RuleEvent ruleEvent, EffectNodeRuntimeState ownerNode, bool continuation)
        {
            if (ruleEvent == null || !string.Equals(EventType, ruleEvent.EventType, StringComparison.Ordinal)) return false;
            if (continuation)
            {
                return Role == EffectHandlerRole.Continuation &&
                       string.Equals(HandlerId, ownerNode.ContinuationHandlerId, StringComparison.Ordinal) &&
                       string.Equals(ContentId, ownerNode.ContinuationContentId ?? string.Empty, StringComparison.Ordinal) &&
                       string.Equals(ContentInstanceId, ownerNode.ContinuationContentInstanceId ?? string.Empty, StringComparison.Ordinal) &&
                       string.Equals(AbilityId, ownerNode.ContinuationAbilityId ?? string.Empty, StringComparison.Ordinal) &&
                       string.Equals(DefinitionVersion, ownerNode.ContinuationDefinitionVersion ?? string.Empty, StringComparison.Ordinal) &&
                       string.Equals(ContentHash, ownerNode.ContinuationContentHash ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            if (Role == EffectHandlerRole.Continuation) return false;
            bool routeMatches = string.IsNullOrEmpty(ruleEvent.RouteKey)
                ? string.IsNullOrEmpty(RouteKey) || RouteKey == "*"
                : string.Equals(RouteKey, ruleEvent.RouteKey, StringComparison.Ordinal) ||
                  (Role == EffectHandlerRole.Observer && RouteKey == "*");
            if (!routeMatches) return false;
            if (!string.IsNullOrEmpty(EffectTypeFilter) &&
                !string.Equals(EffectTypeFilter, ownerNode.EffectTypeId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(SourceEffectFilter) &&
                !string.Equals(SourceEffectFilter, ruleEvent.SourceEffectId, StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public sealed partial class EffectRegistry
    {
        public YC.Domain.Influence.InfluenceService InfluenceService { get; set; }
        public YC.Domain.Enterprises.EnterpriseDefinitions EnterpriseDefinitions { get; set; } = new YC.Domain.Enterprises.EnterpriseDefinitions();
        public string ContentPackHash { get; set; } = string.Empty;
        public GameSetupRecipe SetupRecipe { get; set; }
        public GameContentDefinitions ContentDefinitions { get; set; }
        public GameplayPresentationProvider GameplayPresenter { get; set; }
        public YC.Domain.Facilities.FacilityBuildCandidateQuery FacilityBuildCandidates { get; set; } = new YC.Domain.Facilities.FacilityBuildCandidateQuery();
        public GameRuleConfiguration LaunchConfiguration { get; set; }
        public Dictionary<string, string> CharacterActivationSubscriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, YC.Domain.Cards.CharacterModeContract> CharacterAbilityContracts { get; } = new Dictionary<string, YC.Domain.Cards.CharacterModeContract>(StringComparer.Ordinal);

        private readonly Dictionary<string, EffectRegistration> effects = new Dictionary<string, EffectRegistration>(StringComparer.Ordinal);
        private readonly List<EffectEventHandlerRegistration> eventHandlers = new List<EffectEventHandlerRegistration>();
        private readonly Dictionary<string, List<EffectEventHandlerRegistration>> eventHandlerIndex =
            new Dictionary<string, List<EffectEventHandlerRegistration>>(StringComparer.Ordinal);

        public EffectRegistry()
        {
            Register(new EffectRegistration(
                EffectTypeIds.Condition,
                ExecuteCondition,
                EffectExecutorKind.IntrinsicFlow));
        }

        public void Register(EffectRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (effects.ContainsKey(registration.EffectTypeId))
            {
                throw new InvalidOperationException("Effect 类型重复注册：" + registration.EffectTypeId);
            }

            effects.Add(registration.EffectTypeId, registration);
        }

        public void Register(
            string effectTypeId,
            Func<EffectExecutionContext, EffectStepResult> executor,
            EffectExecutorKind executorKind = EffectExecutorKind.Atomic,
            string definitionVersion = "v1")
        {
            Register(new EffectRegistration(effectTypeId, executor, executorKind, definitionVersion));
        }

        public bool TryGet(string effectTypeId, out EffectRegistration registration)
        {
            return effects.TryGetValue(effectTypeId ?? string.Empty, out registration);
        }

        public EffectRegistration Get(string effectTypeId)
        {
            EffectRegistration result;
            if (!TryGet(effectTypeId, out result))
            {
                throw new KernelException(EffectFaultCodes.UnknownEffectType, "未知 Effect 类型：" + effectTypeId);
            }

            return result;
        }

        internal bool HasContinuation(string handlerId, string eventType)
        {
            EffectEventHandlerRegistration ignored;
            return TryGetContinuation(handlerId, eventType, out ignored);
        }

        public bool TryGetContinuation(
            string handlerId,
            string eventType,
            out EffectEventHandlerRegistration registration)
        {
            for (int i = 0; i < eventHandlers.Count; i++)
            {
                EffectEventHandlerRegistration candidate = eventHandlers[i];
                if (candidate.Role == EffectHandlerRole.Continuation &&
                    candidate.HandlerId == handlerId &&
                    candidate.EventType == eventType)
                {
                    registration = candidate;
                    return true;
                }
            }

            registration = null;
            return false;
        }

        public bool HasEventHandler(string subscriptionId)
        {
            if (string.IsNullOrEmpty(subscriptionId)) return false;
            return eventHandlers.Exists(candidate =>
                candidate != null && candidate.SubscriptionId == subscriptionId);
        }

        internal EffectEventHandlerRegistration FindEventHandler(string subscriptionId) =>
            eventHandlers.Find(candidate => candidate.SubscriptionId == subscriptionId);

        public void RegisterEventHandler(EffectEventHandlerRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            for (int i = 0; i < eventHandlers.Count; i++)
            {
                EffectEventHandlerRegistration existing = eventHandlers[i];
                if (string.Equals(existing.SubscriptionId, registration.SubscriptionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Event 订阅 ID 重复：" + registration.SubscriptionId);
                }

                bool samePrimary = existing.Role == EffectHandlerRole.Primary &&
                                    registration.Role == EffectHandlerRole.Primary &&
                                    string.Equals(existing.EventType, registration.EventType, StringComparison.Ordinal) &&
                                    string.Equals(existing.RouteKey, registration.RouteKey, StringComparison.Ordinal);
                if (samePrimary)
                {
                    throw new InvalidOperationException("同一 Event 路由只能有一个 Primary 处理器。" + registration.EventType);
                }
            }

            eventHandlers.Add(registration);
            string indexKey = CreateHandlerIndexKey(registration.EventType, registration.RouteKey, registration.Role);
            List<EffectEventHandlerRegistration> indexed;
            if (!eventHandlerIndex.TryGetValue(indexKey, out indexed))
            {
                indexed = new List<EffectEventHandlerRegistration>();
                eventHandlerIndex.Add(indexKey, indexed);
            }

            indexed.Add(registration);
        }

        public void RegisterContinuation(
            string handlerId,
            Func<EffectEventHandlerContext, IList<EffectSpec>> handler,
            string eventType = "EffectCompleted",
            int priority = 0,
            string contentInstanceId = "",
            string abilityId = "",
            string definitionVersion = "v1",
            string contentHash = "",
            string contentId = "")
        {
            RegisterEventHandler(new EffectEventHandlerRegistration(
                "continuation:" + handlerId,
                eventType,
                string.Empty,
                handlerId,
                handler,
                EffectHandlerRole.Continuation,
                priority,
                contentInstanceId,
                abilityId,
                definitionVersion,
                contentHash,
                contentId));
        }

        internal List<EffectEventHandlerRegistration> GetMatchingHandlers(
            RuleEvent ruleEvent,
            EffectNodeRuntimeState ownerNode)
        {
            var candidates = new List<EffectEventHandlerRegistration>();
            bool continuationEvent = ruleEvent.EventType == "EffectCompleted" &&
                ownerNode != null && !string.IsNullOrEmpty(ownerNode.ContinuationHandlerId);
            if (continuationEvent)
            {
                AddIndexedHandlers(candidates, ruleEvent.EventType, string.Empty, true);
            }

            AddIndexedHandlers(candidates, ruleEvent.EventType, ruleEvent.RouteKey, false);

            var matched = new List<EffectEventHandlerRegistration>();
            for (int i = 0; i < candidates.Count; i++)
            {
                EffectEventHandlerRegistration registration = candidates[i];
                bool continuation = continuationEvent && registration.Role == EffectHandlerRole.Continuation;
                if (registration.Matches(ruleEvent, ownerNode, continuation)) matched.Add(registration);
            }

            matched.Sort(CompareHandlers);
            return matched;
        }

        private void AddIndexedHandlers(
            List<EffectEventHandlerRegistration> destination,
            string eventType,
            string routeKey,
            bool continuation)
        {
            string targetedKey = CreateHandlerIndexKey(
                eventType,
                routeKey ?? string.Empty,
                continuation ? EffectHandlerRole.Continuation : EffectHandlerRole.Primary);
            List<EffectEventHandlerRegistration> indexed;
            if (eventHandlerIndex.TryGetValue(targetedKey, out indexed)) destination.AddRange(indexed);

            if (!continuation)
            {
                string observerKey = CreateHandlerIndexKey(eventType, routeKey ?? string.Empty, EffectHandlerRole.Observer);
                if (eventHandlerIndex.TryGetValue(observerKey, out indexed)) destination.AddRange(indexed);
                if (routeKey != "*")
                {
                    observerKey = CreateHandlerIndexKey(eventType, "*", EffectHandlerRole.Observer);
                    if (eventHandlerIndex.TryGetValue(observerKey, out indexed)) destination.AddRange(indexed);
                }
            }
        }

        private static string CreateHandlerIndexKey(
            string eventType,
            string routeKey,
            EffectHandlerRole role)
        {
            return (eventType ?? string.Empty) + "\u001f" + (routeKey ?? string.Empty) + "\u001f" + ((int)role).ToString(CultureInfo.InvariantCulture);
        }

        private static int CompareHandlers(
            EffectEventHandlerRegistration left,
            EffectEventHandlerRegistration right)
        {
            int comparison = left.RouteTier.CompareTo(right.RouteTier);
            if (comparison != 0) return comparison;
            comparison = left.Priority.CompareTo(right.Priority);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.ContentInstanceId, right.ContentInstanceId);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.AbilityId, right.AbilityId);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.HandlerId, right.HandlerId);
            if (comparison != 0) return comparison;
            return StringComparer.Ordinal.Compare(left.SubscriptionId, right.SubscriptionId);
        }

        private static EffectStepResult ExecuteCondition(EffectExecutionContext context)
        {
            EffectNodeRuntimeState node = context.Node;
            if (node.NestedGroupSizes == null || node.NestedGroupSizes.Count < 2)
            {
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "条件式缺少左右两侧 Effect式。");
            }

            int leftCount = node.NestedGroupSizes[0];
            int rightCount = node.NestedGroupSizes[1];
            string stage = node.FlowStage ?? string.Empty;
            if (string.IsNullOrEmpty(stage) || stage == "decline_accepted") stage = "left:0";

            int separator = stage.IndexOf(':');
            string side = separator < 0 ? "left" : stage.Substring(0, separator);
            int index;
            if (separator < 0 || !int.TryParse(stage.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "条件式阶段损坏：" + stage);
            }

            EffectNodeRuntimeState lastChild = GetLastChild(context);
            if (lastChild != null &&
                lastChild.Status != EffectNodeStatus.Completed &&
                lastChild.Status != EffectNodeStatus.Failed)
            {
                return EffectStepResult.NoProgress("条件式在子节点未结束时被重新执行。");
            }

            bool transitionedToRight = false;
            if (side == "left")
            {
                if (lastChild != null && lastChild.Status == EffectNodeStatus.Failed)
                {
                    return EffectStepResult.Completed(CreateConditionResult(false, lastChild));
                }

                if (lastChild != null) index++;
                if (index < leftCount)
                {
                    return EffectStepResult.Continue("left:" + index)
                        .AddChild(ToNestedSpec(node, 0, index));
                }

                side = "right";
                index = 0;
                transitionedToRight = true;
            }

            if (side != "right")
            {
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "未知条件式阶段：" + side);
            }

            if (!transitionedToRight &&
                (lastChild != null &&
                 (lastChild.Status == EffectNodeStatus.Completed || lastChild.Status == EffectNodeStatus.Failed)))
            {
                index++;
            }

            if (index < rightCount)
            {
                return EffectStepResult.Continue("right:" + index)
                    .AddChild(ToNestedSpec(node, 1, index));
            }

            return EffectStepResult.Completed(CreateConditionResult(true, null));
        }

        private static EffectNodeRuntimeState GetLastChild(EffectExecutionContext context)
        {
            if (context.Node.ChildEffectIds == null || context.Node.ChildEffectIds.Count == 0) return null;
            string id = context.Node.ChildEffectIds[context.Node.ChildEffectIds.Count - 1];
            return context.State.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == id);
        }

        private static EffectSpec ToNestedSpec(EffectNodeRuntimeState node, int group, int index)
        {
            int offset = 0;
            for (int i = 0; i < group; i++) offset += node.NestedGroupSizes[i];
            EffectSpecRuntimeState data = node.NestedEffects[offset + index];
            var spec = new EffectSpec(data.EffectTypeId, data.NormalizedArguments == null
                ? NormalizedValue.CreateNull()
                : data.NormalizedArguments.Clone())
            {
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(data.Presentation),
                Settlement = GameStateCloneService.DeepClone(data.Settlement),
                Visibility = data.Visibility,
                DefinitionVersion = data.DefinitionVersion,
                SourceId = data.SourceId,
                PlayerId = data.PlayerId,
                StableKey = data.StableKey,
                AllowDecline = data.AllowDecline,
                DecisionPlayerId = data.DecisionPlayerId,
                DeclinePromptKey = data.DeclinePromptKey,
                UnavailablePolicy = data.UnavailablePolicy,
                CompletionHandlerId = data.CompletionHandlerId,
                ContinuationContext = data.ContinuationContext?.Clone(),
                ContinuationContentId = data.ContinuationContentId,
                ContinuationContentInstanceId = data.ContinuationContentInstanceId,
                ContinuationAbilityId = data.ContinuationAbilityId,
                ContinuationDefinitionVersion = data.ContinuationDefinitionVersion,
                ContinuationContentHash = data.ContinuationContentHash
            };

            if (data.NestedGroupSizes != null && data.NestedEffects != null)
            {
                int nestedOffset = 0;
                for (int i = 0; i < data.NestedGroupSizes.Count; i++)
                {
                    int count = data.NestedGroupSizes[i];
                    if (count < 0 || nestedOffset + count > data.NestedEffects.Count)
                    {
                        throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 描述长度不一致。");
                    }

                    var groupSpecs = new List<EffectSpec>();
                    for (int j = 0; j < count; j++)
                    {
                        groupSpecs.Add(FromRuntimeSpec(data.NestedEffects[nestedOffset + j]));
                    }

                    spec.NestedEffectGroups.Add(groupSpecs);
                    nestedOffset += count;
                }

                if (nestedOffset != data.NestedEffects.Count)
                {
                    throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 描述存在未消费项。");
                }
            }

            return spec;
        }

        internal static EffectSpec FromRuntimeSpec(EffectSpecRuntimeState data)
        {
            if (data == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 描述不能为 null。");
            var spec = new EffectSpec(
                data.EffectTypeId,
                data.NormalizedArguments == null ? NormalizedValue.CreateNull() : data.NormalizedArguments.Clone())
            {
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(data.Presentation),
                Settlement = GameStateCloneService.DeepClone(data.Settlement),
                Visibility = data.Visibility,
                DefinitionVersion = data.DefinitionVersion,
                SourceId = data.SourceId,
                PlayerId = data.PlayerId,
                StableKey = data.StableKey,
                AllowDecline = data.AllowDecline,
                DecisionPlayerId = data.DecisionPlayerId,
                DeclinePromptKey = data.DeclinePromptKey,
                UnavailablePolicy = data.UnavailablePolicy,
                CompletionHandlerId = data.CompletionHandlerId,
                ContinuationContext = data.ContinuationContext?.Clone(),
                ContinuationContentId = data.ContinuationContentId,
                ContinuationContentInstanceId = data.ContinuationContentInstanceId,
                ContinuationAbilityId = data.ContinuationAbilityId,
                ContinuationDefinitionVersion = data.ContinuationDefinitionVersion,
                ContinuationContentHash = data.ContinuationContentHash
            };

            if (data.NestedGroupSizes != null && data.NestedEffects != null)
            {
                int offset = 0;
                for (int i = 0; i < data.NestedGroupSizes.Count; i++)
                {
                    int count = data.NestedGroupSizes[i];
                    if (count < 0 || offset + count > data.NestedEffects.Count)
                    {
                        throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 描述长度不一致。");
                    }

                    var group = new List<EffectSpec>();
                    for (int j = 0; j < count; j++) group.Add(FromRuntimeSpec(data.NestedEffects[offset + j]));
                    spec.NestedEffectGroups.Add(group);
                    offset += count;
                }

                if (offset != data.NestedEffects.Count)
                {
                    throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 描述存在未消费项。");
                }
            }

            return spec;
        }

        private static NormalizedValue CreateConditionResult(bool met, EffectNodeRuntimeState failedChild)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "conditionMet",
                    Value = NormalizedValue.CreateBoolean(met)
                },
                new NormalizedValueEntry
                {
                    Name = "failedChildId",
                    Value = NormalizedValue.CreateString(failedChild == null ? string.Empty : failedChild.EffectId)
                },
                new NormalizedValueEntry
                {
                    Name = "outcome",
                    Value = NormalizedValue.CreateString(met ? "condition_met" : "condition_not_met")
                }
            });
        }
    }

    public sealed class EffectAdvanceResult
    {
        internal EffectAdvanceResult(bool progressed, string nodeId, string faultCode)
        {
            Progressed = progressed;
            NodeId = nodeId ?? string.Empty;
            FaultCode = faultCode ?? string.Empty;
        }

        public bool Progressed { get; }
        public string NodeId { get; }
        public string FaultCode { get; }
        public bool Faulted { get { return !string.IsNullOrEmpty(FaultCode); } }
    }

    public sealed class EffectRunReport
    {
        internal EffectRunReport(int steps, bool waiting, bool completed, string faultCode)
        {
            Steps = steps;
            WaitingForInput = waiting;
            Completed = completed;
            FaultCode = faultCode ?? string.Empty;
        }

        public int Steps { get; }
        public bool WaitingForInput { get; }
        public bool Completed { get; }
        public string FaultCode { get; }
        public bool Faulted { get { return !string.IsNullOrEmpty(FaultCode); } }
    }

    public partial class EffectTreeExecutor
    {
        private readonly GameState state;
        private readonly EffectRegistry registry;
        private readonly EffectRuntimeLimits limits;
        private readonly Action<GameState, EffectNodeRuntimeState> nodeTerminalObserver;
        private string lastDiagnostic = string.Empty;

        public EffectTreeExecutor(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits = null)
            : this(state, registry, limits, null)
        {
        }

        public EffectTreeExecutor(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits,
            Action<GameState, EffectNodeRuntimeState> nodeTerminalObserver)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (!string.IsNullOrEmpty(registry.ContentPackHash))
            {
                if (!string.IsNullOrEmpty(state.ContentPackHash) && state.ContentPackHash != registry.ContentPackHash)
                    throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "当前内容包与存档绑定的内容哈希不一致，请使用原内容包恢复。");
                state.ContentPackHash = registry.ContentPackHash;
            }
            this.limits = limits ?? EffectRuntimeLimits.Default;
            this.nodeTerminalObserver = nodeTerminalObserver;
            if (this.state.EffectRuntime == null) this.state.EffectRuntime = new EffectRuntimeState();
            if (!EffectContinuationContext.TryValidate(this.state.EffectRuntime, out string contextReason)) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, contextReason);
            if (!ContentTransactionEffectExecutor.TryValidateRuntime(this.state,out string transactionReason)) throw new KernelException(EffectFaultCodes.InvalidEffectSpec,transactionReason);
        }

        public GameState State { get { return state; } }
        public EffectRuntimeState Runtime { get { return state.EffectRuntime; } }
        public EffectRegistry Registry { get { return registry; } }
        public string LastDiagnostic { get { return lastDiagnostic; } }

        public string CreateRoot(EffectSpec spec, string roundId = "", string sourceId = "")
        {
            string nodeId;
            if (!TryCreateRoot(spec, roundId, sourceId, out nodeId))
            {
                throw new InvalidOperationException(lastDiagnostic);
            }

            return nodeId;
        }

        public bool TryCreateRoot(
            EffectSpec spec,
            string roundId,
            string sourceId,
            out string nodeId)
        {
            nodeId = string.Empty;
            try
            {
                ValidateSpec(spec, 0, 0);
                if (Runtime.EffectNodes.Count >= limits.MaxNodeCount)
                {
                    throw new KernelException(EffectFaultCodes.NodeLimitExceeded, "Effect 节点数量超过上限。");
                }

                string normalizedRoundId = roundId ?? string.Empty;
                string normalizedSourceId = string.IsNullOrEmpty(sourceId)
                    ? (spec.SourceId ?? string.Empty)
                    : sourceId;
                string createdNodeId = StableIdFactory.Create(
                    "effect",
                    state.GameId ?? string.Empty,
                    "root",
                    normalizedRoundId,
                    spec.EffectTypeId ?? string.Empty,
                    normalizedSourceId,
                    Runtime.EffectNodes.Count.ToString(CultureInfo.InvariantCulture),
                    spec.StableKey ?? string.Empty);
                nodeId = createdNodeId;

                CommitNewState(
                    "effect.create:" + nodeId,
                    new List<RuleJournalEntry>(),
                    work =>
                    {
                        EffectRuntimeState runtime = work.EffectRuntime;
                        if (runtime.EffectNodes.Exists(candidate => candidate.EffectId == createdNodeId))
                        {
                            return;
                        }

                        EffectNodeRuntimeState node = CreateNode(spec, createdNodeId, normalizedRoundId, string.Empty);
                        node.OperationContext = runtime.CurrentOperationContext?.Clone();
                        runtime.EffectNodes.Add(node);
                        AddContinuationBinding(runtime, node);
                    },
                    new[] { nodeId });
                return true;
            }
            catch (KernelException exception)
            {
                Fault(exception.Code, exception.Message, nodeId, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                Fault(EffectFaultCodes.InvalidEffectSpec, exception.Message, nodeId, string.Empty);
                return false;
            }
        }

        public bool TryCreateRoot(EffectSpec spec, out string nodeId)
        {
            return TryCreateRoot(spec, string.Empty, string.Empty, out nodeId);
        }

        /// <summary>
        /// 把玩家主动行动挂到当前回合主链的 player_action_window。
        /// 正式行动路径必须使用这个入口；只有没有主链的旧存档/单元测试才允许
        /// 调用方显式回退到 CreateRoot。
        /// </summary>
        public bool TryAttachToCurrentPlayerActionWindow(
            int playerId,
            EffectSpec spec,
            out string nodeId)
        {
            nodeId = string.Empty;
            if (spec == null)
            {
                lastDiagnostic = "待挂载的 Effect 不能为空。";
                return false;
            }

            MainlineNodeRuntimeState window = Runtime.MainNodes == null
                ? null
                : Runtime.MainNodes.Find(candidate =>
                    candidate != null &&
                    candidate.NodeId == Runtime.ActiveMainNodeId &&
                    candidate.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                    candidate.PlayerId == playerId);
            if (window == null || string.IsNullOrEmpty(window.ExecutionEffectId))
            {
                lastDiagnostic = "当前玩家没有可挂载的 player_action_window。";
                return false;
            }

            try
            {
                nodeId = CreateChild(window.ExecutionEffectId, spec);
                return true;
            }
            catch (Exception exception)
            {
                lastDiagnostic = exception.Message;
                nodeId = string.Empty;
                return false;
            }
        }

        public bool TryCreatePlayerActionEffect(
            int playerId,
            EffectSpec spec,
            string roundId,
            string sourceId,
            out string nodeId)
        {
            if (Runtime.EffectNodes.Any(node => ResourcePaymentPreparationService.IsCurrentPreparation(state, node, playerId)))
            { nodeId = ""; lastDiagnostic = "payment_preparation_cancel_required"; return false; }
            if (TryAttachToCurrentPlayerActionWindow(playerId, spec, out nodeId)) return true;
            if (!string.IsNullOrEmpty(Runtime.ActiveMainNodeId)) return false;
            return TryCreateRoot(spec, roundId, sourceId, out nodeId);
        }

        /// <summary>局部插入只替换同一使用的模式请求；失效、去重和条件子树同次RuleCommit。</summary>
        public bool TryInvokeCharacterSettlementOption(int playerId, string optionId, string useId, int expectedRevision,
            out bool alreadyInvoked, out string diagnostic)
        {
            alreadyInvoked = false; diagnostic = string.Empty;
            if (Runtime.EffectNodes.Any(n => n.SettlementWindow?.Entries?.Any(e => e.OptionId == optionId) == true))
                return TryInvokeSettlementEntry(playerId, optionId, useId, expectedRevision, out alreadyInvoked, out diagnostic);
            var option = YC.Domain.Cards.CharacterSettlementOptionQuery.GetOptions(state, playerId, useId).FirstOrDefault(value => value.OptionId == optionId);
            if (option == null) { diagnostic = "settlement_option_missing"; return false; }
            var originalOwner = Runtime.EffectNodes.Find(value => value.EffectId == option.OwnerEffectId);
            if (YC.Domain.Cards.CharacterSettlementOptionQuery.IsInvoked(originalOwner, optionId)) { alreadyInvoked = true; return true; }
            if (!option.IsEnabled || expectedRevision != Runtime.StateRevision) { diagnostic = expectedRevision != Runtime.StateRevision ? "state_revision_mismatch" : option.DisabledReasonCode; return false; }
            bool cleanupOption = string.IsNullOrEmpty(useId) && Runtime.InteractionRequests.Any(value =>
                value.OwnerEffectId == originalOwner?.EffectId && value.GetStableInteractionId() == option.InteractionId &&
                value.Status == "open" && value.InteractionTypeId == YC.Domain.Cards.CharacterContentEffectExecutor.CleanupInteractionTypeId);
            if (string.IsNullOrEmpty(useId) && !cleanupOption)
            {
                bool created = TryCreatePlayerActionEffect(playerId, ContentLifecycleEffectExecutor.AwardSpec(playerId, option.SourceItemId, false), "", option.SourceItemId, out _);
                if (!created) diagnostic = LastDiagnostic;
                return created;
            }
            IList<EffectSpec> specs = cleanupOption ? YC.Domain.Cards.CharacterContentEffectExecutor.GetCleanupBody(state, option) : new[] {
                YC.Domain.Cards.CharacterLifecycleService.CreateDoubleUseInsertion(playerId, useId, option.SourceItemId) };
            foreach (var spec in specs) ValidateSpec(spec, 0, 0);
            var work = GameStateCloneService.DeepClone(state);
            var owner = work.EffectRuntime.EffectNodes.Single(value => value.EffectId == option.OwnerEffectId);
            var request = work.EffectRuntime.InteractionRequests.Single(value => value.GetStableInteractionId() == option.InteractionId && value.Status == "open");
            request.Status = "invalidated";
            foreach (var blocker in work.EffectRuntime.Blockers.Where(value => value.InteractionRequestId == option.InteractionId)) blocker.IsResolved = true;
            var invoked = YC.Domain.Effects.ItemTransferEffectExecutor.Value(owner.ContinuationData, "invokedOptionIds")?.Clone() ?? NormalizedValue.CreateArray(new List<NormalizedValue>());
            invoked.Items.Add(NormalizedValue.CreateString(optionId));
            YC.Domain.Effects.ItemTransferEffectExecutor.Set(owner, "invokedOptionIds", invoked);
            if (cleanupOption) owner.FlowStage = "cleanup_started";
            else
            {
                var use = work.CharacterUses.Single(value => value.UseId == useId); use.DoubleUseSourceItemId = option.SourceItemId;
                owner.FlowStage = "awaiting_insertion";
            }
            var entries = new List<RuleJournalEntry>();
            var attached = AppendChildEffects(work, owner, specs, "settlement", optionId, entries);
            CommitPreparedWork(work, "settlement.invoke:" + optionId, entries, -1, -1, new[] { owner.EffectId, attached[0] });
            return true;
        }

        public bool CanStopCharacterMode(int playerId, string useId, string interactionId, int expectedRevision) =>
            YC.Domain.Cards.CharacterLifecycleService.TryGetModeStopScope(state, playerId, useId, interactionId, expectedRevision, out _, out _);

        public bool TryStopCharacterMode(int playerId, string useId, string interactionId, int expectedRevision, string commandId, out string diagnostic)
        {
            diagnostic = string.Empty;
            var previous = state.CharacterUses.Find(value => value.UseId == useId);
            if (!string.IsNullOrEmpty(commandId) && previous?.StopCommandId == commandId && previous.ExecutingPlayerId == playerId) return true;
            if (string.IsNullOrEmpty(commandId) || !YC.Domain.Cards.CharacterLifecycleService.TryGetModeStopScope(state, playerId, useId, interactionId, expectedRevision, out var activation, out var owned))
            { diagnostic = "character_mode_stop_not_available"; return false; }
            var work = GameStateCloneService.DeepClone(state);
            var use = work.CharacterUses.Single(value => value.UseId == useId);
            use.SkippedModes.Add(use.ModeOrder[1]); use.StopCommandId = commandId; use.StoppedActivationEffectId = activation.EffectId;
            var entries = new List<RuleJournalEntry>();
            foreach (var node in work.EffectRuntime.EffectNodes.Where(value => owned.Contains(value.EffectId) && value.EffectId != activation.EffectId))
            {
                // 已有完成事件代表已提交结果；它的外部响应与收据不能改写。
                if (IsTerminal(node.Status) || !string.IsNullOrEmpty(node.CompletedEventId)) continue;
                ItemTransferEffectExecutor.Set(node, YC.Domain.Cards.CharacterLifecycleService.StoppedUseKey, NormalizedValue.CreateString(useId));
                node.PendingOutcome = EffectPendingOutcome.Failed;
                node.FailureReason = YC.Domain.Cards.CharacterLifecycleService.ModeStoppedReason;
                node.Status = EffectNodeStatus.Blocked;
                var binding = work.EffectRuntime.ContinuationBindings.Find(value => value.BindingId == node.ContinuationBindingId);
                if (binding != null) binding.Status = ContinuationBindingStatus.Cancelled;
                foreach (var request in work.EffectRuntime.InteractionRequests.Where(value => value.OwnerEffectId == node.EffectId && value.Status == "open" && value.AnsweringPlayerId == playerId))
                {
                    request.Status = "cancelled";
                    ResolveInteractionBlockerOnWorking(work.EffectRuntime, request.GetStableInteractionId());
                    entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.Interaction, EntityId = request.GetStableInteractionId(), Detail = YC.Domain.Cards.CharacterLifecycleService.ModeStoppedReason });
                }
                entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = node.EffectId, Detail = YC.Domain.Cards.CharacterLifecycleService.ModeStoppedReason });
            }
            CommitPreparedWork(work, commandId, entries, -1, -1, owned.ToList());
            return true;
        }

        private bool TryMigrateCharacterSecondMode()
        {
            var use = state.CharacterUses.Find(value => value.SelectionProtocolVersion == 2 && !value.Finished && value.ModeOrder.Count == 1 && value.CompletedModes.Count == 1 && value.OfferSecondEffect && value.CanOfferSecondEffect &&
                Runtime.InteractionRequests.Any(request => request.OwnerEffectId == value.OwnerEffectId && request.Status == "open" && request.InteractionTypeId == YC.Domain.Cards.CharacterLifecycleService.SecondModeInteractionTypeId));
            if (use == null) return false;
            var work = GameStateCloneService.DeepClone(state);
            use = work.CharacterUses.Single(value => value.UseId == use.UseId);
            var owner = work.EffectRuntime.EffectNodes.Single(value => value.EffectId == use.OwnerEffectId);
            var quick = work.EffectRuntime.EffectNodes.Single(value => value.EffectId == owner.ParentEffectId);
            foreach (var request in work.EffectRuntime.InteractionRequests.Where(value => value.OwnerEffectId == owner.EffectId && value.Status == "open" && value.InteractionTypeId == YC.Domain.Cards.CharacterLifecycleService.SecondModeInteractionTypeId))
            { request.Status = "invalidated"; ResolveInteractionBlockerOnWorking(work.EffectRuntime, request.GetStableInteractionId()); }
            use.SelectionProtocolVersion = 3;
            foreach (var node in new[] { owner, quick }) node.NormalizedArguments.Properties.Single(value => value.Name == "selectionProtocolVersion").Value = NormalizedValue.CreateInteger(3);
            owner.FlowStage = "resolving_modes";
            if (!HasUnresolvedBlocker(work.EffectRuntime, owner)) owner.Status = EffectNodeStatus.Ready;
            CommitPreparedWork(work, "character.selection.migrate:" + use.UseId, new List<RuleJournalEntry>(), -1, -1, new[] { owner.EffectId, quick.EffectId });
            return true;
        }

        public string CreateChild(string parentEffectId, EffectSpec spec)
        {
            string nodeId = string.Empty;
            try
            {
                ValidateSpec(spec, 0, 0);
                GameState work = GameStateCloneService.DeepClone(state);
                EffectNodeRuntimeState parent = work.EffectRuntime.EffectNodes.Find(
                    candidate => candidate.EffectId == parentEffectId);
                if (parent == null)
                {
                    throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "父 Effect 不存在。");
                }

                var entries = new List<RuleJournalEntry>();
                List<string> attached = AppendChildEffects(
                    work,
                    parent,
                    new[] { spec },
                    "child",
                    "manual",
                    entries);
                nodeId = attached[0];
                CommitPreparedWork(
                    work,
                    "effect.child.create:" + nodeId,
                    entries,
                    -1,
                    -1,
                    new[] { parentEffectId, nodeId });
                return nodeId;
            }
            catch (KernelException exception)
            {
                Fault(exception.Code, exception.Message, parentEffectId, string.Empty);
                throw new InvalidOperationException(lastDiagnostic);
            }
            catch (Exception exception)
            {
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, parentEffectId, string.Empty);
                throw;
            }
        }

        public bool TryPublishEvent(
            string ownerEffectId,
            EffectEventRequest request,
            out string eventId,
            out string diagnostic)
        {
            eventId = string.Empty;
            diagnostic = string.Empty;
            try
            {
                GameState work = GameStateCloneService.DeepClone(state);
                EffectNodeRuntimeState owner = work.EffectRuntime.EffectNodes.Find(
                    candidate => candidate.EffectId == ownerEffectId);
                if (owner == null) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "Event owner 不存在。");
                if (request == null || string.IsNullOrEmpty(request.EventType))
                {
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "Event 请求无效。");
                }

                string sourceId = string.IsNullOrEmpty(request.SourceEffectId)
                    ? owner.EffectId
                    : request.SourceEffectId;
                string predictedEventId = string.IsNullOrEmpty(request.EventId)
                    ? StableIdFactory.Create(
                        "event",
                        sourceId,
                        request.EventType,
                        request.RouteKey ?? string.Empty,
                        request.SemanticKey ?? "0")
                    : request.EventId;
                if (work.EffectRuntime.RuleEvents.Exists(candidate => candidate.EventId == predictedEventId))
                {
                    eventId = predictedEventId;
                    return true;
                }

                int eventStart = work.EffectRuntime.RuleEvents.Count;
                int receiptStart = work.EffectRuntime.DispatchReceipts.Count;
                var entries = new List<RuleJournalEntry>();
                DispatchEventsOnWorking(work, new[] { request }, owner, entries);
                if (work.EffectRuntime.RuleEvents.Count > eventStart)
                {
                    eventId = work.EffectRuntime.RuleEvents[eventStart].EventId;
                }
                else
                {
                    eventId = predictedEventId;
                }

                CommitPreparedWork(
                    work,
                    "event.publish:" + eventId,
                    entries,
                    eventStart,
                    receiptStart,
                    new[] { ownerEffectId });
                return true;
            }
            catch (KernelException exception)
            {
                diagnostic = exception.Message;
                Fault(exception.Code, exception.Message, ownerEffectId, eventId);
                return false;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, ownerEffectId, eventId);
                return false;
            }
        }

        public bool Advance()
        {
            if (Runtime.Status != EffectRuntimeStatus.Active) return false;
            if (Runtime.EffectNodes.Count > limits.MaxNodeCount)
            {
                Fault(EffectFaultCodes.NodeLimitExceeded, "已恢复的 Effect 节点数量超过上限。", string.Empty, string.Empty);
                return false;
            }

            if (Runtime.RuleEvents.Count > limits.MaxEventCount)
            {
                Fault(EffectFaultCodes.EventLimitExceeded, "已恢复的 RuleEvent 数量超过上限。", string.Empty, string.Empty);
                return false;
            }

            if (Runtime.DispatchReceipts.Count > limits.MaxDispatchCount)
            {
                Fault(EffectFaultCodes.DispatchLimitExceeded, "已恢复的 Event handler 分发次数超过上限。", string.Empty, string.Empty);
                return false;
            }

            if (Runtime.StepCount >= limits.MaxSteps)
            {
                Fault(EffectFaultCodes.StepLimitExceeded, "Effect 推进步数超过上限。", string.Empty, string.Empty);
                return false;
            }

            try
            {
                string nodeId;
                if (TryMigrateCharacterSecondMode()) return true;
                if (TryPromoteCreated(out nodeId)) return true;
                if (TryStartReady(out nodeId)) return true;
                if (TryExecuteRunning(out nodeId)) return true;
                if (Runtime.Status != EffectRuntimeStatus.Active) return false;
                if (TryResumeBlocked(out nodeId)) return true;

                if (HasNonTerminalNodes() && !HasOpenInteraction() && !SettlementEntryService.HasPendingPlayerSelection(Runtime) &&
                    !Runtime.EffectNodes.Any(node => node.Payment?.Preparation != null && !node.Payment.Preparation.IsPromoted && !node.Payment.Preparation.IsCancelled &&
                        node.Payment.Preparation.ActionKind == "city_move" && node.Payment.Preparation.TargetId.Length == 0))
                {
                    Fault(
                        EffectFaultCodes.NoProgress,
                        BuildNoProgressDiagnostic(),
                        string.Empty,
                        string.Empty);
                }

                return false;
            }
            catch (KernelException exception)
            {
                Fault(exception.Code, exception.Message, string.Empty, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, string.Empty, string.Empty);
                return false;
            }
        }

        public EffectRunReport RunUntilQuiescent(int maxSteps = -1)
        {
            string timingDiagnostic = TimingHandlerRegistry.ValidateBindings(state, registry);
            if (Runtime.Status == EffectRuntimeStatus.Active && !string.IsNullOrEmpty(timingDiagnostic))
                Fault("timing_binding_invalid", timingDiagnostic, string.Empty, string.Empty);
            int budget = maxSteps <= 0 ? limits.MaxSteps : maxSteps;
            int steps = 0;
            string continuationFaultCode;
            string continuationFaultMessage;
            if (Runtime.Status == EffectRuntimeStatus.Active &&
                !TryValidateContinuationBindings(out continuationFaultCode, out continuationFaultMessage))
            {
                Fault(continuationFaultCode, continuationFaultMessage, string.Empty, string.Empty);
            }

            while (steps < budget && Runtime.Status == EffectRuntimeStatus.Active)
            {
                if (!Advance()) break;
                steps++;
            }

            if (Runtime.Status == EffectRuntimeStatus.Active && steps >= budget && HasNonTerminalNodes())
            {
                Fault(EffectFaultCodes.StepLimitExceeded, "本次运行步数超过上限。", string.Empty, string.Empty);
            }

            return new EffectRunReport(
                steps,
                Runtime.Status == EffectRuntimeStatus.Active && (HasOpenInteraction() || SettlementEntryService.HasPendingPlayerSelection(Runtime)),
                Runtime.Status == EffectRuntimeStatus.Active && !HasNonTerminalNodes(),
                Runtime.Status == EffectRuntimeStatus.PausedFault ? Runtime.LastFaultCode : string.Empty);
        }

        private bool TryValidateContinuationBindings(out string faultCode, out string faultMessage)
        {
            faultCode = string.Empty;
            faultMessage = string.Empty;
            if (Runtime.ContinuationBindings == null) return true;

            for (int i = 0; i < Runtime.ContinuationBindings.Count; i++)
            {
                ContinuationBinding binding = Runtime.ContinuationBindings[i];
                if (binding == null || binding.Status == ContinuationBindingStatus.Cancelled) continue;
                EffectNodeRuntimeState owner = GetNode(binding.SourceEffectId);
                if (owner == null || string.IsNullOrEmpty(owner.ContinuationHandlerId))
                {
                    faultCode = EffectFaultCodes.ContinuationBindingMissing;
                    faultMessage = "continuation binding 的来源节点不存在。";
                    return false;
                }

                if (!ValidateContinuationBinding(owner, binding, out faultCode, out faultMessage))
                {
                    return false;
                }
            }

            return true;
        }

        public EffectNodeRuntimeState GetNode(string effectId)
        {
            return Runtime.EffectNodes.Find(candidate => candidate != null && candidate.EffectId == effectId);
        }

        public bool TryAddBlocker(
            string ownerEffectId,
            string targetEffectId,
            string blockerKind,
            string reason,
            out string blockerId)
        {
            blockerId = string.Empty;
            try
            {
                string ownerId = ownerEffectId ?? string.Empty;
                string targetId = targetEffectId ?? string.Empty;
                if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(targetId))
                {
                    throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "Blocker 必须包含 owner 和 target。。");
                }

                string createdBlockerId = StableIdFactory.Create(
                    "blocker",
                    ownerId,
                    targetId,
                    blockerKind ?? string.Empty,
                    Runtime.Blockers.Count.ToString(CultureInfo.InvariantCulture));
                blockerId = createdBlockerId;
                CommitNewState(
                    "blocker.create:" + blockerId,
                    new List<RuleJournalEntry>(),
                    work => AddBlockerOnWorking(
                        work,
                        ownerId,
                        targetId,
                        string.Empty,
                        blockerKind,
                        reason,
                        createdBlockerId),
                    new[] { ownerId });
                return true;
            }
            catch (KernelException exception)
            {
                Fault(exception.Code, exception.Message, ownerEffectId, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, ownerEffectId, string.Empty);
                return false;
            }
        }

        public bool TrySubmitInteraction(
            string requestId,
            int answeringPlayerId,
            NormalizedValue answer,
            out string diagnostic)
        {
            InteractionRequest request = FindInteraction(requestId);
            return TrySubmitInteraction(
                requestId,
                answeringPlayerId,
                request == null ? -1 : request.StateRevision,
                answer,
                out diagnostic);
        }

        public bool TrySubmitInteraction(
            string requestId,
            int answeringPlayerId,
            int expectedRevision,
            NormalizedValue answer,
            out string diagnostic)
        {
            return TrySubmitInteraction(requestId, answeringPlayerId, expectedRevision,
                answer, out diagnostic, out _);
        }

        public bool TrySubmitInteraction(
            string requestId,
            int answeringPlayerId,
            int expectedRevision,
            NormalizedValue answer,
            out string diagnostic,
            out CommandErrorCode errorCode)
            => TrySubmitInteractionCore(requestId, answeringPlayerId, expectedRevision, answer, null, out diagnostic, out errorCode);

        internal bool TrySubmitInteractionAtRequestContract(GameCommand command, NormalizedValue answer, string expectedContractDigest,
            out string diagnostic, out CommandErrorCode errorCode)
        {
            diagnostic = string.Empty; errorCode = CommandErrorCode.InvalidTarget;
            if (Runtime.CurrentOperationContext?.SourceCommandId != command.CommandId || Runtime.CurrentOperationContext.ActingPlayerId != command.PlayerId)
            { diagnostic = "请求合同入口缺少原有序命令的可信执行上下文。"; return false; }
            if (!InteractionRequestValidity.TryValidateAnswer(state, command, expectedContractDigest, out var validation))
            { diagnostic = validation.Reason; errorCode = validation.ErrorCode; return false; }
            command.Parameters.TryGetValue("interactionId", out var id);
            if (string.IsNullOrEmpty(id)) id = !string.IsNullOrEmpty(command.SourceId) ? command.SourceId : command.TargetId;
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.GetStableInteractionId() == id);
            if (request == null) { diagnostic = "原交互已经关闭。"; return false; }
            if (!int.TryParse(command.Parameters.TryGetValue("expectedRevision", out var revision) ? revision : "", out int expectedRevision) || expectedRevision < 0)
            { diagnostic = "交互回答缺少有效的原expectedRevision。"; return false; }
            return TrySubmitInteractionCore(request.GetStableInteractionId(), command.PlayerId, expectedRevision, answer, expectedContractDigest,
                out diagnostic, out errorCode);
        }

        private bool TrySubmitInteractionCore(string requestId, int answeringPlayerId, int expectedRevision,
            NormalizedValue answer, string verifiedContractDigest, out string diagnostic, out CommandErrorCode errorCode)
        {
            diagnostic = string.Empty;
            errorCode = CommandErrorCode.InvalidTarget;
            if (Runtime.Status != EffectRuntimeStatus.Active)
            {
                diagnostic = "运行时已经暂停于故障。";
                return false;
            }

            try
            {
                string id = requestId ?? string.Empty;
                InteractionRequest request = FindInteraction(id);
                if (request == null || request.Status != "open")
                {
                    diagnostic = "交互不存在或已经回答。";
                    return false;
                }

                EffectNodeRuntimeState requestOwner = Runtime.EffectNodes.Find(candidate =>
                    candidate != null && candidate.EffectId == request.OwnerEffectId);
                if (requestOwner?.Payment?.Preparation != null && !requestOwner.Payment.Preparation.IsPromoted &&
                    requestOwner.Payment.Preparation.AuthorizationKind != "effect_build")
                { diagnostic = "payment_requires_original_action_confirmation"; return false; }
                if (IsDeferredResourceCollectionCompletion(request, requestOwner))
                {
                    diagnostic = "资源收集尚未完成，不能提前结束采集阶段。";
                    return false;
                }

                if (expectedRevision < 0 || verifiedContractDigest == null &&
                    (Runtime.StateRevision != expectedRevision || request.StateRevision != expectedRevision))
                {
                    diagnostic = "交互已过期，请刷新当前交互。";
                    errorCode = CommandErrorCode.StateRevisionMismatch;
                    return false;
                }

                if (request.AnsweringPlayerId >= 0 && request.AnsweringPlayerId != answeringPlayerId)
                {
                    diagnostic = "回答玩家无权处理该交互。";
                    return false;
                }

                string reason = string.Empty;
                if (answer == null || !answer.TryValidate(out reason))
                {
                    diagnostic = reason;
                    return false;
                }

                if (!TryValidateInteractionAnswer(request, answer, out reason))
                {
                    diagnostic = reason;
                    return false;
                }

                if (requestOwner?.Payment?.Preparation?.AuthorizationKind == "effect_build")
                {
                    bool submitted = TrySubmitEffectBuildPaymentAnswerOnWorking(request, answeringPlayerId, answer, out diagnostic);
                    if (submitted) errorCode = CommandErrorCode.None;
                    return submitted;
                }
                if (request.InteractionTypeId == FacilitySelectionEffectExecutor.NormalBuildInteractionType &&
                    !(answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue && request.AllowDecline))
                { diagnostic = "effect_build_requires_preparation"; return false; }

                GameState work = GameStateCloneService.DeepClone(state);
                EffectRuntimeState runtime = work.EffectRuntime;
                InteractionRequest workingRequest = FindInteraction(runtime, id);
                EffectNodeRuntimeState owner = runtime.EffectNodes.Find(candidate => candidate.EffectId == workingRequest.OwnerEffectId);
                if (owner == null || IsTerminal(owner.Status))
                {
                    diagnostic = "交互所属节点已经终止。";
                    return false;
                }

                workingRequest.Status = "answered";
                if (verifiedContractDigest != null && workingRequest.InteractionTypeId == "route_resource.selection")
                {
                    // 仅新可信入口写持久化接受事实；旧答案/旧存档不补标，后继仍可辨别legacy strict。
                    var accepted = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                    RouteSelectionSessionAuthority.Set(accepted, InteractionRequestValidity.AcceptedVersionParameter, NormalizedValue.CreateInteger(InteractionRequestValidity.ContractVersion));
                    RouteSelectionSessionAuthority.Set(accepted, InteractionRequestValidity.AcceptedDigestParameter, NormalizedValue.CreateString(verifiedContractDigest));
                    RouteSelectionSessionAuthority.Set(accepted, InteractionRequestValidity.AcceptedCommandParameter, NormalizedValue.CreateString(Runtime.CurrentOperationContext.SourceCommandId));
                    RouteSelectionSessionAuthority.Set(accepted, "interactionId", NormalizedValue.CreateString(workingRequest.GetStableInteractionId()));
                    RouteSelectionSessionAuthority.Set(owner.ContinuationData, InteractionRequestValidity.AcceptanceRecord, accepted);
                }
                owner.ContinuationOperationContext = runtime.CurrentOperationContext?.Clone() ?? owner.ContinuationOperationContext;
                workingRequest.NormalizedAnswer = answer.Clone();
                workingRequest.AnsweredAtRevision = runtime.StateRevision + 1;
                if (workingRequest.InteractionTypeId == "collection.task")
                {
                    PlayerState collectingPlayer = work.FindPlayer(answeringPlayerId);
                    if (collectingPlayer != null)
                    {
                        collectingPlayer.HasCollectedResourcesThisRound = true;
                    }
                }
                if (workingRequest.InteractionTypeId == "action.decline_effect" && string.IsNullOrEmpty(owner.Payment?.RuleConditionOwnerEffectId))
                {
                    var choice = answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1 ? answer.Items[0] : answer;
                    string selected = choice.Kind == NormalizedValueKind.StableReference ? choice.ReferenceId : choice.StringValue;
                    owner.FlowStage = selected == "continue" ? "optional_accepted" : "decline_accepted";
                }

                ResolveInteractionBlockerOnWorking(runtime, id);
                if (!HasUnresolvedBlocker(runtime, owner) && owner.PendingOutcome == EffectPendingOutcome.None)
                {
                    owner.Status = EffectNodeStatus.Ready;
                }

                var events = new List<EffectEventRequest>
                {
                    new EffectEventRequest
                    {
                        EventType = "InteractionAnswered",
                        SourceEffectId = owner.EffectId,
                        OwnerNodeId = owner.EffectId,
                        PlayerId = answeringPlayerId,
                        Visibility = workingRequest.Visibility ?? "owner",
                        ResponseKind = RuleEventResponseKind.Effects,
                        SemanticKey = id,
                        Payload = CreateObject(new Dictionary<string, NormalizedValue>
                        {
                            { "answer", answer },
                            { "answeringPlayerId", NormalizedValue.CreateInteger(answeringPlayerId) },
                            { "requestId", NormalizedValue.CreateString(id) }
                        })
                    }
                };
                var entries = new List<RuleJournalEntry>();
                int eventStart = runtime.RuleEvents.Count;
                int receiptStart = runtime.DispatchReceipts.Count;
                DispatchEventsOnWorking(work, events, owner, entries);
                entries.Insert(0, new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.Interaction,
                    EntityId = id,
                    Detail = "interaction_answered",
                    Payload = answer
                });
                CommitPreparedWork(work, "interaction.answer:" + id, entries, eventStart, receiptStart, new[] { owner.EffectId });
                errorCode = CommandErrorCode.None;
                return true;
            }
            catch (KernelException exception)
            {
                diagnostic = exception.Message;
                Fault(exception.Code, exception.Message, string.Empty, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, string.Empty, string.Empty);
                return false;
            }
        }

        private bool IsDeferredResourceCollectionCompletion(
            InteractionRequest request,
            EffectNodeRuntimeState owner)
        {
            if (request == null || owner == null ||
                request.InteractionTypeId == null ||
                !request.InteractionTypeId.StartsWith(
                    RoundExecutionService.MainlineCompletionInteractionTypeId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            MainlineNodeRuntimeState mainNode = Runtime.MainNodes.Find(candidate =>
                candidate != null &&
                candidate.ExecutionEffectId == owner.EffectId &&
                candidate.NodeTypeId == RoundMainlineNodeTypeIds.Collection);
            if (mainNode == null || Runtime.PlayerOrderSnapshot == null ||
                Runtime.PlayerOrderSnapshot.Count == 0 || mainNode.CompletedPlayerIds == null)
            {
                return false;
            }

            for (int i = 0; i < Runtime.PlayerOrderSnapshot.Count; i++)
            {
                if (!mainNode.CompletedPlayerIds.Contains(Runtime.PlayerOrderSnapshot[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryInvalidateInteraction(
            string interactionId,
            string reason,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (Runtime.Status != EffectRuntimeStatus.Active)
            {
                diagnostic = "运行时已经暂停于故障。";
                return false;
            }

            InteractionRequest request = FindInteraction(interactionId);
            if (request == null || request.Status != "open")
            {
                diagnostic = "交互不存在或已经结束。";
                return false;
            }

            try
            {
                GameState work = GameStateCloneService.DeepClone(state);
                InteractionRequest workingRequest = FindInteraction(work.EffectRuntime, interactionId);
                EffectNodeRuntimeState owner = work.EffectRuntime.EffectNodes.Find(
                    candidate => candidate.EffectId == workingRequest.OwnerEffectId);
                if (owner == null || IsTerminal(owner.Status))
                {
                    diagnostic = "交互所属节点已经终止。";
                    return false;
                }

                workingRequest.Status = "invalidated";
                owner.PendingOutcome = EffectPendingOutcome.Failed;
                owner.FailureReason = string.IsNullOrEmpty(reason) ? "candidate_invalidated" : reason;
                owner.Status = EffectNodeStatus.Blocked;
                ResolveInteractionBlockerOnWorking(work.EffectRuntime, interactionId);
                var entries = new List<RuleJournalEntry>
                {
                    new RuleJournalEntry
                    {
                        Kind = RuleJournalEntryKind.Interaction,
                        EntityId = interactionId,
                        Detail = "interaction_invalidated",
                        Payload = NormalizedValue.CreateString(owner.FailureReason)
                    }
                };
                TryCompleteNodeOnWorking(work, owner, entries);
                CommitPreparedWork(
                    work,
                    "interaction.invalidate:" + interactionId,
                    entries,
                    -1,
                    -1,
                    new[] { owner.EffectId });
                return true;
            }
            catch (KernelException exception)
            {
                diagnostic = exception.Message;
                Fault(exception.Code, exception.Message, string.Empty, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, string.Empty, string.Empty);
                return false;
            }
        }

        public bool TryDeclineEffect(int playerId, string effectId, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (Runtime.Status != EffectRuntimeStatus.Active)
            {
                diagnostic = "运行时已经暂停于故障。";
                return false;
            }

            try
            {
                EffectNodeRuntimeState source = GetNode(effectId);
                if (source == null || IsTerminal(source.Status))
                {
                    diagnostic = "Effect 不存在或已经终止。";
                    return false;
                }

                var preparedRulePayment = ResourcePaymentPreparationService.FindDeclinableRulePayment(state, source, playerId);
                if (preparedRulePayment != null)
                {
                    var preparedWork = GameStateCloneService.DeepClone(state);
                    var paymentOwner = preparedWork.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == preparedRulePayment.EffectId);
                    ResourcePaymentSubstitutionEffectExecutor.CancelDraft(paymentOwner);
                    ClosePreparedPaymentInput(preparedWork, paymentOwner);
                    paymentOwner.PendingOutcome = EffectPendingOutcome.Failed; paymentOwner.FailureReason = "player_declined";
                    var preparedEntries = new List<RuleJournalEntry>();
                    int preparedEventStart = preparedWork.EffectRuntime.RuleEvents.Count;
                    int preparedReceiptStart = preparedWork.EffectRuntime.DispatchReceipts.Count;
                    TryCompleteNodeOnWorking(preparedWork, paymentOwner, preparedEntries);
                    CommitPreparedWork(preparedWork, "effect.decline:" + effectId, preparedEntries, preparedEventStart, preparedReceiptStart, new[] { paymentOwner.EffectId });
                    return true;
                }

                if (!source.AllowDecline ||
                    (source.DecisionPlayerId >= 0 && source.DecisionPlayerId != playerId) ||
                    source.PendingOutcome != EffectPendingOutcome.None ||
                    source.ChildEffectIds.Count != 0)
                {
                    diagnostic = "当前 Effect 不允许该玩家放弃。";
                    return false;
                }

                GameState work = GameStateCloneService.DeepClone(state);
                EffectNodeRuntimeState node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == effectId);
                node.PendingOutcome = EffectPendingOutcome.Failed;
                node.FailureReason = "player_declined";
                node.Status = EffectNodeStatus.Blocked;
                for (int i = 0; i < work.EffectRuntime.InteractionRequests.Count; i++)
                {
                    InteractionRequest request = work.EffectRuntime.InteractionRequests[i];
                    if (request != null && request.OwnerEffectId == effectId && request.Status == "open")
                    {
                        request.Status = "cancelled";
                        ResolveInteractionBlockerOnWorking(work.EffectRuntime, request.RequestId);
                    }
                }

                int eventStart = work.EffectRuntime.RuleEvents.Count;
                int receiptStart = work.EffectRuntime.DispatchReceipts.Count;
                var entries = new List<RuleJournalEntry>();
                TryCompleteNodeOnWorking(work, node, entries);
                CommitPreparedWork(work, "effect.decline:" + effectId, entries, eventStart, receiptStart, new[] { effectId });
                return true;
            }
            catch (KernelException exception)
            {
                diagnostic = exception.Message;
                Fault(exception.Code, exception.Message, effectId, string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, effectId, string.Empty);
                return false;
            }
        }

        private bool TryPromoteCreated(out string nodeId)
        {
            nodeId = string.Empty;
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = Runtime.EffectNodes[i];
                if (node != null && node.Status == EffectNodeStatus.Created && IsSchedulingEligible(node))
                {
                    nodeId = node.EffectId;
                    CommitStatus(nodeId, EffectNodeStatus.Ready, "effect.ready");
                    return true;
                }
            }

            return false;
        }

        private bool TryStartReady(out string nodeId)
        {
            nodeId = string.Empty;
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = Runtime.EffectNodes[i];
                if (node != null && node.Status == EffectNodeStatus.Ready && IsSchedulingEligible(node))
                {
                    nodeId = node.EffectId;
                    CommitStatus(nodeId, EffectNodeStatus.Running, "effect.running");
                    return true;
                }
            }

            return false;
        }

        private bool TryExecuteRunning(out string nodeId)
        {
            nodeId = string.Empty;
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = Runtime.EffectNodes[i];
                if (node != null && node.Status == EffectNodeStatus.Running && IsSchedulingEligible(node))
                {
                    nodeId = node.EffectId;
                    try
                    {
                        ExecuteNode(nodeId);
                        return Runtime.Status == EffectRuntimeStatus.Active;
                    }
                    catch (KernelException exception)
                    {
                        Fault(exception.Code, exception.Message, nodeId, string.Empty);
                        return false;
                    }
                    catch (Exception exception)
                    {
                        Fault(EffectFaultCodes.InvalidRuntimeState, exception.Message, nodeId, string.Empty);
                        return false;
                    }
                }
            }

            return false;
        }

        private bool TryResumeBlocked(out string nodeId)
        {
            nodeId = string.Empty;
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = Runtime.EffectNodes[i];
                if (node == null || node.Status != EffectNodeStatus.Blocked) continue;
                nodeId = node.EffectId;
                string currentNodeId = nodeId;
                if (HasUnresolvedBlocker(Runtime, node)) continue;
                GameState work = GameStateCloneService.DeepClone(state);
                EffectNodeRuntimeState workingNode = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == currentNodeId);
                ResolveTerminalTargetBlockersOnWorking(work.EffectRuntime, workingNode);
                if (HasUnresolvedBlocker(work.EffectRuntime, workingNode)) continue;
                var entries = new List<RuleJournalEntry>();
                int eventStart = work.EffectRuntime.RuleEvents.Count;
                int receiptStart = work.EffectRuntime.DispatchReceipts.Count;
                if (workingNode.PendingOutcome != EffectPendingOutcome.None)
                {
                    TryCompleteNodeOnWorking(work, workingNode, entries);
                }
                else
                {
                    workingNode.Status = EffectNodeStatus.Ready;
                }

                CommitPreparedWork(work, "effect.resume:" + nodeId, entries, eventStart, receiptStart, new[] { nodeId });
                return true;
            }

            return false;
        }

        private void ExecuteNode(string nodeId)
        {
            EffectNodeRuntimeState source = GetNode(nodeId);
            EffectRegistration registration = registry.Get(source.EffectTypeId);
            if (!string.IsNullOrEmpty(source.DefinitionVersion) && source.DefinitionVersion != registration.DefinitionVersion && !ItemTransferEffectExecutor.CanRestoreLegacy(state, source) && !InfluenceEffectExecutor.CanRestoreLegacy(state, source))
                throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "Effect 定义版本与存档不匹配：" + source.EffectTypeId + "。请使用原定义恢复；不支持重新解释旧节点。");
            GameState work = GameStateCloneService.DeepClone(state);
            EffectNodeRuntimeState node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == nodeId);

            if (ItemTransferEffectExecutor.CanRestoreLegacy(work, node) || InfluenceEffectExecutor.CanRestoreLegacy(work, node)) node.DefinitionVersion = registration.DefinitionVersion;
            if (node.EffectTypeId == EffectTypeIds.Condition && ItemTransferEffectExecutor.Flag(node.NormalizedArguments, "ruleCondition") &&
                string.IsNullOrEmpty(node.FlowStage))
            {
                node.AllowDecline = node.NestedGroupSizes[0] != 1 || node.NestedEffects[0].EffectTypeId != ResourceEffectTypeIds.Pay;
                node.DecisionPlayerId = node.PlayerId;
            }
            if (node.AllowDecline && string.IsNullOrEmpty(node.FlowStage))
            {
                bool ruleCondition = node.EffectTypeId == EffectTypeIds.Condition && ItemTransferEffectExecutor.Flag(node.NormalizedArguments, "ruleCondition");
                var gate = new EffectStepResult();
                gate.AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = "action.decline_effect",
                    AnsweringPlayerId = node.DecisionPlayerId,
                    PromptKey = string.IsNullOrEmpty(node.DeclinePromptKey)
                        ? "effect.decline"
                        : node.DeclinePromptKey,
                    AllowDecline = true,
                    PromptParameters = ruleCondition ? ItemTransferEffectExecutor.Object(
                        "sourceKind", NormalizedValue.CreateString("condition"),
                        "ownerEffectId", NormalizedValue.CreateString(node.EffectId),
                        "ruleConditionOwnerEffectId", NormalizedValue.CreateString(node.EffectId)) : NormalizedValue.CreateObject(new List<NormalizedValueEntry>()),
                    Presentation = ruleCondition ? GameplayPresentationNormalizer.Clone(node.Presentation?.Interaction) :
                        string.IsNullOrEmpty(node.DeclinePromptKey) || node.DeclinePromptKey == "effect.decline"
                        ? new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.effect.optional") } : null,
                    AnswerSchema = "candidate_id",
                    CandidateIds = { "continue", "decline" },
                    MinSelections = 1,
                    MaxSelections = 1
                });
                gate.WithFlowStage("awaiting_decline");
                ApplyStepResult(work, node, gate, new List<RuleJournalEntry>());
                return;
            }

            if (node.AllowDecline && node.FlowStage == "decline_accepted")
            {
                ApplyStepResult(
                    work,
                    node,
                    EffectStepResult.Failed("player_declined"),
                    new List<RuleJournalEntry>());
                return;
            }

            // 接受只放行一次；随后执行器把自身阶段写回持久化节点。
            if (node.FlowStage == "optional_accepted") node.FlowStage = string.Empty;
            EffectStepResult result;
            var entries = new List<RuleJournalEntry>();
            try
            {
                result = registration.Executor(new EffectExecutionContext(work, node, registry, limits,
                    dependencies => TryAttachDeferredResponseDependenciesOnWorking(work, nodeId, dependencies, entries)));
            }
            catch (KernelException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new KernelException(EffectFaultCodes.HandlerException, exception.Message, exception);
            }

            node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == nodeId);
            if (result == null)
            {
                throw new KernelException(EffectFaultCodes.InvalidHandlerResult, "Effect 处理器返回 null。");
            }

            if (result.ExplicitNoProgress)
            {
                throw new KernelException(EffectFaultCodes.NoProgress, "Effect 处理器没有产生进展：" + result.FailureReason);
            }

            ApplyStepResult(work, node, result, entries);
        }

        private void ApplyStepResult(
            GameState work,
            EffectNodeRuntimeState node,
            EffectStepResult result,
            List<RuleJournalEntry> entries)
        {
            ValidateResult(result);
            if (!string.IsNullOrEmpty(result.FlowStage)) node.FlowStage = result.FlowStage;
            if (result.HasPendingOutcome)
            {
                node.PendingOutcome = result.PendingOutcome;
                node.FailureReason = result.FailureReason ?? string.Empty;
                node.NormalizedResult = result.NormalizedResult == null
                    ? NormalizedValue.CreateNull()
                    : result.NormalizedResult.Clone();
            }

            if (result.ChildEffects.Count > 0 || result.IndependentChildGroups.Count > 0)
            {
                AppendChildEffects(
                    work,
                    node,
                    result.ChildEffects,
                    "child",
                    "child_effect",
                    entries,
                    result.IndependentChildGroups);
            }

            // 主链节点只保存其运行 Effect 的顺序子任务索引，避免恢复时重新推导或重复创建。
            for (int i = 0; i < work.EffectRuntime.MainNodes.Count; i++)
            {
                MainlineNodeRuntimeState mainline = work.EffectRuntime.MainNodes[i];
                if (mainline != null && mainline.ExecutionEffectId == node.EffectId)
                {
                    mainline.OrderedChildEffectIds.Clear();
                    mainline.OrderedChildEffectIds.AddRange(node.ChildEffectIds);
                    break;
                }
            }

            int eventStart = work.EffectRuntime.RuleEvents.Count;
            int receiptStart = work.EffectRuntime.DispatchReceipts.Count;
            DispatchEventsOnWorking(work, result.Events, node, entries);
            for (int i = 0; i < result.Interactions.Count; i++)
            {
                AddInteractionOnWorking(work, node, result.Interactions[i], i);
            }

            for (int i = 0; i < result.Logs.Count; i++)
            {
                EffectLogEntrySpec log = result.Logs[i];
                if (log == null || string.IsNullOrWhiteSpace(log.Message))
                {
                    throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "Effect 可见日志不能为空。");
                }

                if (work.Logs == null)
                {
                    work.Logs = new List<GameLogEntry>();
                }

                string logCommandId = "effect.log:" + node.EffectId + ":" + i.ToString(CultureInfo.InvariantCulture);
                if (!work.Logs.Exists(candidate => candidate != null && candidate.CommandId == logCommandId))
                {
                    work.Logs.Add(new GameLogEntry
                    {
                        Sequence = work.Logs.Count + 1,
                        CommandId = logCommandId,
                        PlayerId = log.PlayerId,
                        Message = log.Message
                    });
                    entries.Add(new RuleJournalEntry
                    {
                        Kind = RuleJournalEntryKind.DomainState,
                        EntityId = logCommandId,
                        Detail = "visible_log",
                        Payload = NormalizedValue.CreateString(log.Message)
                    });
                }
            }

            bool hasBlocker = HasUnresolvedBlocker(work.EffectRuntime, node);
            if (result.HasPendingOutcome)
            {
                node.Status = hasBlocker ? EffectNodeStatus.Blocked : EffectNodeStatus.Blocked;
                if (!hasBlocker) TryCompleteNodeOnWorking(work, node, entries);
            }
            else if (hasBlocker)
            {
                node.Status = EffectNodeStatus.Blocked;
            }
            else if (result.ChildEffects.Count > 0 ||
                     result.Events.Count > 0 ||
                     result.Interactions.Count > 0 ||
                     !string.IsNullOrEmpty(result.FlowStage))
            {
                // Intrinsic flow 可以只推进自己的持久化阶段或发布一个没有订阅者的 Event。
                // 这仍然是合法进展；下一次 Advance 会按新阶段继续执行。
                node.Status = EffectNodeStatus.Ready;
            }
            else
            {
                throw new KernelException(EffectFaultCodes.NoProgress, "Effect 处理器没有结果、子节点或阻塞项。");
            }

            // 选点已经写入 entered/结果/真实事件时即可放行后位；不等待该节点终态。
            if (!IsNormalTerminal(node.Status))
                ActivateAffectedEntranceOwners(work, node.EffectId, entries);

            CommitPreparedWork(
                work,
                "effect.execute:" + node.EffectId,
                entries,
                eventStart,
                receiptStart,
                new[] { node.EffectId });
        }

        private void TryCompleteNodeOnWorking(
            GameState work,
            EffectNodeRuntimeState node,
            List<RuleJournalEntry> entries)
        {
            if (HasUnresolvedBlocker(work.EffectRuntime, node))
            {
                node.Status = EffectNodeStatus.Blocked;
                return;
            }

            if (node.PendingOutcome == EffectPendingOutcome.None)
            {
                node.Status = EffectNodeStatus.Ready;
                return;
            }

            if (string.IsNullOrEmpty(node.CompletedEventId))
            {
                node.CompletedEventId = StableIdFactory.Create(
                    "event",
                    node.EffectId,
                    "EffectCompleted",
                    node.PendingOutcome.ToString());
                if (!string.IsNullOrEmpty(node.ContinuationBindingId))
                {
                    ContinuationBinding binding = work.EffectRuntime.ContinuationBindings.Find(candidate =>
                        candidate != null && candidate.BindingId == node.ContinuationBindingId);
                    if (binding != null) binding.CompletionEventId = node.CompletedEventId;
                }
                var completion = new EffectEventRequest
                {
                    EventId = node.CompletedEventId,
                    EventType = "EffectCompleted",
                    SourceEffectId = node.EffectId,
                    OwnerNodeId = node.EffectId,
                    PlayerId = node.PlayerId,
                    Visibility = string.IsNullOrEmpty(node.Visibility)
                        ? (node.PlayerId >= 0 ? "owner" : "public")
                        : node.Visibility,
                    ResponseKind = IsStoppedCharacterNode(work, node) ? RuleEventResponseKind.None : RuleEventResponseKind.Effects,
                    DefinitionVersion = node.DefinitionVersion,
                    SemanticKey = "completion",
                    HostOnlyPayload = node.ContinuationContext == null ? NormalizedValue.CreateNull() : CreateObject(new Dictionary<string,NormalizedValue> {
                        { "effectId", NormalizedValue.CreateString(node.EffectId) },
                        { "effectTypeId", NormalizedValue.CreateString(node.EffectTypeId) },
                        { "failureReason", NormalizedValue.CreateString(node.FailureReason ?? string.Empty) },
                        { "outcome", NormalizedValue.CreateString(node.PendingOutcome == EffectPendingOutcome.Completed ? "completed" : "failed") },
                        { "normalizedResult", node.NormalizedResult?.Clone() ?? NormalizedValue.CreateNull() },
                        { "continuationContext", node.ContinuationContext.Clone() } }),
                    Payload = CreateObject(new Dictionary<string, NormalizedValue>
                    {
                        { "effectId", NormalizedValue.CreateString(node.EffectId) },
                        { "effectTypeId", NormalizedValue.CreateString(node.EffectTypeId) },
                        { "failureReason", NormalizedValue.CreateString(node.FailureReason ?? string.Empty) },
                        { "outcome", NormalizedValue.CreateString(node.PendingOutcome == EffectPendingOutcome.Completed ? "completed" : "failed") },
                        { "normalizedResult", node.NormalizedResult == null ? NormalizedValue.CreateNull() : node.NormalizedResult }
                    })
                };
                DispatchEventsOnWorking(work, new[] { completion }, node, entries);
            }

            if (HasUnresolvedBlocker(work.EffectRuntime, node))
            {
                node.Status = EffectNodeStatus.Blocked;
                return;
            }

            if (node.ContinuationContext != null && node.PendingOutcome == EffectPendingOutcome.Completed &&
                node.ChildEffectIds.Any(id => work.EffectRuntime.EffectNodes.Any(child => child.EffectId == id && child.Status == EffectNodeStatus.Failed &&
                    // 条件左式失败已被本节点消费为正常未满足；不能在内容续接收口时重新升级为技术失败。
                    !(node.EffectTypeId == EffectTypeIds.Condition &&
                        ItemTransferEffectExecutor.Text(node.NormalizedResult, "outcome") == "condition_not_met" &&
                        ItemTransferEffectExecutor.Text(node.NormalizedResult, "failedChildId") == child.EffectId))))
            {
                node.PendingOutcome = EffectPendingOutcome.Failed;
                node.FailureReason = "continuation_child_failed";
            }
            CompleteDeferredResponseOnWorking(work, node, entries);
            node.Status = node.PendingOutcome == EffectPendingOutcome.Completed
                ? EffectNodeStatus.Completed
                : EffectNodeStatus.Failed;
            if (!string.IsNullOrEmpty(node.ContinuationBindingId))
            {
                ContinuationBinding binding = work.EffectRuntime.ContinuationBindings.Find(candidate =>
                    candidate != null && candidate.BindingId == node.ContinuationBindingId);
                if (binding != null && binding.Status != ContinuationBindingStatus.Cancelled) binding.Status = ContinuationBindingStatus.Completed;
            }
            OnNodeTerminalOnWorking(work, work.EffectRuntime, node, entries);
        }

        private static bool IsStoppedCharacterNode(GameState work, EffectNodeRuntimeState node) =>
            node.FailureReason == YC.Domain.Cards.CharacterLifecycleService.ModeStoppedReason &&
            work.CharacterUses.Any(use => use.UseId == ItemTransferEffectExecutor.Text(node.ContinuationData, YC.Domain.Cards.CharacterLifecycleService.StoppedUseKey) && use.SkippedModes.Count == 1);

        private void DispatchEventsOnWorking(
            GameState work,
            IList<EffectEventRequest> requests,
            EffectNodeRuntimeState defaultOwner,
            List<RuleJournalEntry> entries)
        {
            if (requests == null) return;
            for (int i = 0; i < requests.Count; i++)
            {
                EffectEventRequest request = requests[i];
                if (request == null || string.IsNullOrEmpty(request.EventType))
                {
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "Event 请求无效。");
                }

                string sourceId = string.IsNullOrEmpty(request.SourceEffectId)
                    ? defaultOwner.EffectId
                    : request.SourceEffectId;
                string ownerId = string.IsNullOrEmpty(request.OwnerNodeId)
                    ? defaultOwner.EffectId
                    : request.OwnerNodeId;
                string eventId = string.IsNullOrEmpty(request.EventId)
                    ? StableIdFactory.Create(
                        "event",
                        sourceId,
                        request.EventType,
                        request.RouteKey ?? string.Empty,
                        request.SemanticKey ?? i.ToString(CultureInfo.InvariantCulture))
                    : request.EventId;
                RuleEvent existing = work.EffectRuntime.RuleEvents.Find(candidate => candidate.EventId == eventId);
                if (existing != null)
                {
                    continue;
                }

                if (work.EffectRuntime.RuleEvents.Count >= limits.MaxEventCount)
                {
                    throw new KernelException(EffectFaultCodes.EventLimitExceeded, "RuleEvent 数量超过上限。");
                }

                var ruleEvent = new RuleEvent
                {
                    OperationContext = OperationCause(work, work.EffectRuntime.EffectNodes.Find(n => n.EffectId == sourceId))?.Clone(),
                    EventId = eventId,
                    EventType = request.EventType,
                    SourceEffectId = sourceId,
                    OwnerNodeId = ownerId,
                    RouteKey = request.RouteKey ?? string.Empty,
                    TargetEntityId = request.TargetEntityId ?? string.Empty,
                    PlayerId = request.PlayerId,
                    Payload = request.Payload == null ? NormalizedValue.CreateNull() : request.Payload.Clone(),
                    HostOnlyPayload = request.HostOnlyPayload == null
                        ? NormalizedValue.CreateNull()
                        : request.HostOnlyPayload.Clone(),
                    ResponseKind = request.ResponseKind,
                    DefinitionVersion = request.DefinitionVersion ?? string.Empty,
                    Visibility = request.Visibility ?? "public"
                };
                string payloadReason;
                if (!ruleEvent.Payload.TryValidate(out payloadReason))
                {
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, payloadReason);
                }

                work.EffectRuntime.RuleEvents.Add(ruleEvent);
                entries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.RuleEvent,
                    EntityId = eventId,
                    Detail = request.EventType,
                    Payload = ruleEvent.Payload
                });

                EffectNodeRuntimeState owner = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == ownerId);
                if (owner == null) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "Event owner 节点不存在：" + ownerId);
                DispatchOneEventOnWorking(work, ruleEvent, owner, entries);
            }
        }

        private void DispatchOneEventOnWorking(
            GameState work,
            RuleEvent ruleEvent,
            EffectNodeRuntimeState owner,
            List<RuleJournalEntry> entries)
        {
            if (ruleEvent.ResponseKind == RuleEventResponseKind.None)
            {
                return;
            }

            if (ruleEvent.ResponseKind != RuleEventResponseKind.Effects)
            {
                throw new KernelException(EffectFaultCodes.InvalidEventResponse, "当前内核只支持 Effects 响应。");
            }

            if (ruleEvent.EventType == "EffectCompleted" &&
                !string.IsNullOrEmpty(owner.ContinuationHandlerId))
            {
                ContinuationBinding binding = work.EffectRuntime.ContinuationBindings.Find(candidate =>
                    candidate != null && candidate.BindingId == owner.ContinuationBindingId);
                if (binding == null)
                {
                    throw new KernelException(
                        EffectFaultCodes.ContinuationBindingMissing,
                        "未找到持久化 continuation binding：" + owner.ContinuationHandlerId);
                }

                string continuationFaultCode;
                string continuationFaultMessage;
                if (!ValidateContinuationBinding(owner, binding, out continuationFaultCode, out continuationFaultMessage))
                {
                    throw new KernelException(continuationFaultCode, continuationFaultMessage);
                }
            }

            List<EffectEventHandlerRegistration> handlers = registry.GetMatchingHandlers(ruleEvent, owner);
            if (ruleEvent.EventType == "EffectCompleted" &&
                !string.IsNullOrEmpty(owner.ContinuationHandlerId) &&
                !handlers.Exists(candidate => candidate.Role == EffectHandlerRole.Continuation))
            {
                throw new KernelException(
                    string.IsNullOrEmpty(owner.ContinuationContentHash)
                        ? EffectFaultCodes.DefinitionVersionMismatch
                        : EffectFaultCodes.ContinuationContentHashMismatch,
                    "continuation handler 版本或内容哈希与持久化 binding 不一致：" + owner.ContinuationHandlerId);
            }
            for (int i = 0; i < handlers.Count; i++)
            {
                EffectEventHandlerRegistration handler = handlers[i];
                DispatchReceipt existing = work.EffectRuntime.DispatchReceipts.Find(candidate =>
                    candidate.EventId == ruleEvent.EventId && candidate.SubscriptionId == handler.SubscriptionId);
                if (existing != null)
                {
                    if (!existing.Succeeded) throw new KernelException(EffectFaultCodes.EventDispatchException, "已有失败分发收据：" + existing.ReceiptId);
                    continue;
                }

                if (work.EffectRuntime.DispatchReceipts.Count >= limits.MaxDispatchCount)
                {
                    throw new KernelException(EffectFaultCodes.DispatchLimitExceeded, "Event handler 分发次数超过上限。");
                }

                IList<EffectSpec> specs;
                try
                {
                    specs = handler.Handler(new EffectEventHandlerContext(work, ruleEvent, owner, handler));
                }
                catch (KernelException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new KernelException(EffectFaultCodes.EventDispatchException, exception.Message, exception);
                }

                if (specs == null)
                {
                    throw new KernelException(EffectFaultCodes.InvalidEventResponse, "Event 处理器返回 null。");
                }
                ValidateSpecList(specs, 0);
                var attached = new List<string>();
                var deferredIds = new List<string>();
                if (specs.Count > 0)
                {
                    var attachmentOwner = owner;
                    if (handler.Role == EffectHandlerRole.Continuation && owner.ContinuationContext != null &&
                        owner.ContinuationScopeRootEffectId != owner.EffectId)
                    {
                        attachmentOwner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner.ContinuationScopeRootEffectId);
                        if (attachmentOwner.ContinuationContentHash != owner.ContinuationContentHash || attachmentOwner.ContinuationAbilityId != owner.ContinuationAbilityId)
                            throw new KernelException(EffectFaultCodes.ContinuationBindingMissing, "continuation_scope_mismatch");
                    }
                    if (!TryAttachDeferredEventResponsesOnWorking(work, ruleEvent, attachmentOwner, handler, specs, entries, attached, deferredIds))
                        attached.AddRange(AppendChildEffects(
                        work,
                        attachmentOwner,
                        specs,
                        "event",
                        handler.SubscriptionId,
                        entries, settlementTriggerId: ruleEvent.EventId, operationContext: ruleEvent.OperationContext));
                }

                string receiptId = StableIdFactory.Create(
                    "receipt",
                    ruleEvent.EventId,
                    handler.SubscriptionId,
                    handler.DefinitionVersion);
                work.EffectRuntime.DispatchReceipts.Add(new DispatchReceipt
                {
                    ReceiptId = receiptId,
                    EventId = ruleEvent.EventId,
                    SubscriptionId = handler.SubscriptionId,
                    HandlerId = handler.HandlerId,
                    ContentId = handler.ContentId,
                    DefinitionVersion = handler.DefinitionVersion,
                    RouteTier = handler.RouteTier,
                    Priority = handler.Priority,
                    ContentInstanceId = handler.ContentInstanceId,
                    AbilityId = handler.AbilityId,
                    InvocationKey = ruleEvent.EventId + ":" + handler.SubscriptionId,
                    AttachedEffectIds = attached,
                    ActualAttachmentKind = deferredIds.Count == 0 ? EventResponseAttachmentKind.RequiredChild : EventResponseAttachmentKind.DeferredPlayerResponse,
                    DeferredResponseIds = deferredIds,
                    Succeeded = true
                });
                entries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.DispatchReceipt,
                    EntityId = receiptId,
                    Detail = handler.SubscriptionId
                });
            }
        }

        private bool ValidateContinuationBinding(
            EffectNodeRuntimeState owner,
            ContinuationBinding binding,
            out string faultCode,
            out string faultMessage)
        {
            faultCode = string.Empty;
            faultMessage = string.Empty;
            if (owner == null || binding == null ||
                !string.Equals(binding.HandlerId, owner.ContinuationHandlerId, StringComparison.Ordinal) ||
                !string.Equals(binding.ContentId, owner.ContinuationContentId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(binding.ContentInstanceId, owner.ContinuationContentInstanceId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(binding.AbilityId, owner.ContinuationAbilityId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(binding.DefinitionVersion, owner.ContinuationDefinitionVersion ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(binding.ContentHash, owner.ContinuationContentHash ?? string.Empty, StringComparison.Ordinal))
            {
                faultCode = EffectFaultCodes.ContinuationBindingMissing;
                faultMessage = "continuation binding 与来源节点元数据不一致。";
                return false;
            }

            EffectEventHandlerRegistration registration;
            if (!registry.TryGetContinuation(owner.ContinuationHandlerId, "EffectCompleted", out registration))
            {
                faultCode = EffectFaultCodes.UnknownEventHandler;
                faultMessage = "未注册 continuation 处理器：" + owner.ContinuationHandlerId;
                return false;
            }

            if (!string.Equals(registration.ContentHash, owner.ContinuationContentHash ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                faultCode = EffectFaultCodes.ContinuationContentHashMismatch;
                faultMessage = "continuation handler 内容哈希与持久化 binding 不一致。";
                return false;
            }

            if (!string.Equals(registration.DefinitionVersion, owner.ContinuationDefinitionVersion ?? string.Empty, StringComparison.Ordinal))
            {
                faultCode = EffectFaultCodes.DefinitionVersionMismatch;
                faultMessage = "continuation handler 版本与持久化 binding 不一致。";
                return false;
            }

            if (!string.Equals(registration.ContentId, owner.ContinuationContentId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(registration.ContentInstanceId, owner.ContinuationContentInstanceId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(registration.AbilityId, owner.ContinuationAbilityId ?? string.Empty, StringComparison.Ordinal))
            {
                faultCode = EffectFaultCodes.ContinuationBindingMissing;
                faultMessage = "continuation handler 稳定内容标识与持久化 binding 不一致。";
                return false;
            }

            return true;
        }

        private List<string> AppendChildEffects(
            GameState work,
            EffectNodeRuntimeState owner,
            IList<EffectSpec> specs,
            string idKind,
            string pathKey,
            List<RuleJournalEntry> entries,
            IList<EffectStepResult.IndependentChildren> independentGroups = null,
            string settlementTriggerId = null,
            OperationCommandContext operationContext = null)
        {
            ValidateSpecList(specs, 0);
            EffectRuntimeState runtime = work.EffectRuntime;
            if (runtime.EffectNodes.Count + specs.Count > limits.MaxNodeCount)
            {
                throw new KernelException(EffectFaultCodes.NodeLimitExceeded, "Effect 节点数量超过上限。");
            }

            int depth = GetDepth(runtime, owner.EffectId) + 1;
            if (depth > limits.MaxTreeDepth)
            {
                throw new KernelException(EffectFaultCodes.TreeDepthExceeded, "Effect 树深度超过上限。");
            }

            var attached = new List<string>();
            int initialChildCount = owner.ChildEffectIds.Count;
            if (independentGroups != null && independentGroups.Count > 0)
            {
                var keys = new HashSet<string>(owner.ChildSchedulingGroups.ConvertAll(group => group.StableGroupKey), StringComparer.Ordinal);
                int previousEnd = 0;
                foreach (var group in independentGroups)
                {
                    if (group == null || string.IsNullOrWhiteSpace(group.Key) || !keys.Add(group.Key) ||
                        group.Members == null || group.Members.Count == 0 || group.StartIndex < previousEnd ||
                        group.StartIndex > specs.Count - group.Members.Count)
                        throw new KernelException(EffectFaultCodes.InvalidHandlerResult, "独立子任务组重复、扩充或边界无效。");
                    for (int member = 0; member < group.Members.Count; member++)
                        if (!ReferenceEquals(group.Members[member], specs[group.StartIndex + member]))
                            throw new KernelException(EffectFaultCodes.InvalidHandlerResult, "独立子任务组的固定成员已被改动。");
                    previousEnd = group.StartIndex + group.Members.Count;
                }
            }
            for (int i = 0; i < specs.Count; i++)
            {
                EffectSpec spec = specs[i];
                string id = StableIdFactory.Create(
                    "effect",
                    owner.EffectId,
                    idKind,
                    pathKey ?? string.Empty,
                    (initialChildCount + i).ToString(CultureInfo.InvariantCulture),
                    spec.EffectTypeId ?? string.Empty,
                    spec.StableKey ?? string.Empty);
                if (runtime.EffectNodes.Exists(candidate => candidate.EffectId == id))
                {
                    throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "生成了重复的 Effect 节点 ID：" + id);
                }

                EffectNodeRuntimeState child = CreateNode(spec, id, owner.RoundId, owner.EffectId);
                child.OperationContext = (operationContext ?? OperationCause(work, owner))?.Clone();
                runtime.EffectNodes.Add(child);
                if (child.ContinuationContext != null && owner.ContinuationContext != null &&
                    child.ContinuationAbilityId == owner.ContinuationAbilityId && child.ContinuationContentHash == owner.ContinuationContentHash)
                    child.ContinuationScopeRootEffectId = owner.ContinuationScopeRootEffectId;
                AddContinuationBinding(runtime, child);
                owner.ChildEffectIds.Add(id);
                attached.Add(id);
                string blockerId = StableIdFactory.Create(
                    "blocker",
                    owner.EffectId,
                    id,
                    idKind,
                    pathKey ?? string.Empty);
                AddBlockerOnWorking(
                    work,
                    owner.EffectId,
                    id,
                    string.Empty,
                    idKind == "event" ? "event_response" : "child",
                    "等待子 Effect 进入 Completed/Failed",
                    blockerId);
                entries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.EffectNode,
                    EntityId = id,
                    Detail = idKind
                });
            }

            if (independentGroups != null && independentGroups.Count > 0)
            {
                foreach (var group in independentGroups)
                {
                    owner.ChildSchedulingGroups.Add(new EffectChildSchedulingGroup
                    {
                        GroupId = StableIdFactory.Create("effect_child_group", owner.EffectId, group.Key),
                        OwnerEffectId = owner.EffectId,
                        StableGroupKey = group.Key,
                        Mode = EffectChildSchedulingMode.Independent,
                        StartIndex = initialChildCount + group.StartIndex,
                        ChildEffectIds = attached.GetRange(group.StartIndex, group.Members.Count)
                    });
                }
            }
            RegisterAttachedSettlementSegments(work, owner, attached, settlementTriggerId);
            ActivateNextChild(runtime, owner);
            return attached;
        }

        private void AddInteractionOnWorking(
            GameState work,
            EffectNodeRuntimeState owner,
            EffectInteractionSpec spec,
            int ordinal)
        {
            if (spec == null || string.IsNullOrEmpty(spec.InteractionTypeId))
            {
                throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互类型不能为空。");
            }

            EffectRuntimeState runtime = work.EffectRuntime;
            // 同一节点可连续唤起同类交互；序号来自已持久化请求，恢复后也不复用。
            ordinal = runtime.InteractionRequests.FindAll(r => r.OwnerEffectId == owner.EffectId && r.InteractionTypeId == spec.InteractionTypeId).Count;
            string requestId = StableIdFactory.Create(
                "interaction",
                owner.EffectId,
                spec.InteractionTypeId,
                ordinal.ToString(CultureInfo.InvariantCulture));
            if (runtime.InteractionRequests.Exists(candidate => candidate.RequestId == requestId))
            {
                var existing = runtime.InteractionRequests.Find(candidate => candidate.RequestId == requestId);
                throw new KernelException(
                    EffectFaultCodes.InteractionInvalid,
                    "交互请求 ID 重复：" + requestId +
                    " owner=" + owner.EffectId +
                    " type=" + spec.InteractionTypeId +
                    " ordinal=" + ordinal +
                    " existingStatus=" + (existing == null ? "unknown" : existing.Status) +
                    " existingOwner=" + (existing == null ? "" : existing.OwnerEffectId));
            }

            if (spec.PromptParameters == null || !spec.PromptParameters.IsValid())
            {
                throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互提示参数非法。");
            }

            if (spec.CandidateIds == null)
            {
                throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互候选不能为 null。");
            }

            for (int i = 0; i < spec.CandidateIds.Count; i++)
            {
                if (string.IsNullOrEmpty(spec.CandidateIds[i]))
                {
                    throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互候选 ID 不能为空。");
                }

                for (int j = 0; j < i; j++)
                {
                    if (spec.CandidateIds[i] == spec.CandidateIds[j])
                    {
                        throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互候选 ID 不能重复。");
                    }
                }
            }

            if (!string.IsNullOrEmpty(spec.CandidateResolutionId))
            {
                CandidateResolutionRecord resolution = runtime.CandidateResolutions.Find(candidate =>
                    candidate != null && candidate.CandidateSetId == spec.CandidateResolutionId);
                if (resolution == null || !AreSameIds(resolution.FinalCandidateIds, spec.CandidateIds))
                {
                    throw new KernelException(
                        EffectFaultCodes.InteractionInvalid,
                        "交互候选与已解析候选集不一致：resolutionId=" + (spec.CandidateResolutionId ?? string.Empty) +
                        ";resolved=" + (resolution == null ? "missing" : FormatIds(resolution.FinalCandidateIds)) +
                        ";requested=" + FormatIds(spec.CandidateIds));
                }
            }

            var request = new InteractionRequest
            {
                OperationContext = OperationCause(work, owner)?.Clone(),
                InteractionId = requestId,
                RequestId = requestId,
                SourceNodeId = string.IsNullOrEmpty(spec.SourceNodeId) ? owner.EffectId : spec.SourceNodeId,
                InteractionTypeId = spec.InteractionTypeId,
                OwnerEffectId = owner.EffectId,
                AnsweringPlayerId = spec.AnsweringPlayerId,
                Visibility = spec.Visibility ?? "public",
                PromptKey = spec.PromptKey ?? string.Empty,
                PromptParameters = spec.PromptParameters.Clone(),
                Presentation = ResolveInteractionPresentation(owner, spec),
                CandidateSetId = spec.CandidateSetId ?? string.Empty,
                CandidateSetVersion = spec.CandidateSetVersion,
                CandidateResolutionId = spec.CandidateResolutionId ?? string.Empty,
                CandidateIds = spec.CandidateIds == null ? new List<string>() : new List<string>(spec.CandidateIds),
                MinSelections = spec.MinSelections,
                MaxSelections = spec.MaxSelections,
                AllowDecline = spec.AllowDecline,
                AnswerSchema = spec.AnswerSchema ?? string.Empty,
                Status = "open",
                NormalizedAnswer = NormalizedValue.CreateNull(),
                StateRevision = runtime.StateRevision + 1
            };
            if (request.MinSelections < 0 || request.MaxSelections < request.MinSelections ||
                (request.MaxSelections > 0 && request.CandidateIds.Count == 0) ||
                (request.MaxSelections > 0 && request.MaxSelections < request.CandidateIds.Count &&
                 request.MinSelections > request.MaxSelections))
            {
                throw new KernelException(EffectFaultCodes.InteractionInvalid, "交互选择数量约束无效。");
            }

            runtime.InteractionRequests.Add(request);
            AddResourcePaymentFacts(work, owner, request);
            AddPaymentChoiceFacts(work, owner, request);
            OpenInteractionSettlementWindow(work, owner, request);
            string blockerId = StableIdFactory.Create("blocker", owner.EffectId, requestId, "interaction");
            AddBlockerOnWorking(
                work,
                owner.EffectId,
                string.Empty,
                requestId,
                "interaction",
                "等待玩家交互答案",
                blockerId);
            ApplyInteractionPresentationOnWorking(work, owner, request);
        }

        private void ApplyInteractionPresentationOnWorking(
            GameState work, EffectNodeRuntimeState owner, InteractionRequest request)
        {
            if (registry.GameplayPresenter == null || request.Visibility != GameStateVisibilityPolicy.Owner) return;
            EffectNodeRuntimeState repeat = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var current = owner; current != null && visited.Add(current.EffectId);
                current = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == current.ParentEffectId))
                if (current.EffectTypeId == LuaDomainEffectTypeIds.Repeat) { repeat = current; break; }
            if (string.IsNullOrEmpty(owner.Presentation?.PresentationScript) &&
                string.IsNullOrEmpty(repeat?.Presentation?.PresentationScript)) return;

            // 只执行身份可见投影；不调用带规则查询的完整GameStateView投影。
            var identities = new YC.Domain.Items.ItemZoneQuery().Project(work, GameStateViewer.Player(request.AnsweringPlayerId));
            string sourceId = ItemTransferEffectExecutor.Text(request.PromptParameters, "sourceItemId");
            if (sourceId.Length == 0) sourceId = owner.ContinuationContentInstanceId ?? string.Empty;
            var source = identities.Items.Find(item => item.ItemId == sourceId);
            var allowedItems = new HashSet<string>(identities.Items.Where(item =>
                item.ItemId == source?.ItemId || request.CandidateIds.Contains(item.ItemId)).Select(item => item.ItemId), StringComparer.Ordinal);
            var allowedDefinitions = new HashSet<string>(identities.Items.Where(item => allowedItems.Contains(item.ItemId))
                .Select(item => item.DefinitionId), StringComparer.Ordinal);
            var canSee = GameStateViewProjector.CreateGameplayTextArgumentVisibility(new GameStateView {
                GameId = work.GameId, ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = request.AnsweringPlayerId,
                StateRevision = request.StateRevision, ItemZones = identities });
            bool CanSeeProvidedArgument(GameplayTextArgument argument) => canSee(argument) &&
                (argument.Kind == "item" && allowedItems.Contains(argument.Value.ReferenceId) ||
                 argument.Kind == "definition" && allowedDefinitions.Contains(argument.Value.ReferenceId));
            var facts = new List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "candidateIds", Value = ItemTransferEffectExecutor.Strings(request.CandidateIds) },
                new NormalizedValueEntry { Name = "minSelections", Value = NormalizedValue.CreateInteger(request.MinSelections) },
                new NormalizedValueEntry { Name = "maxSelections", Value = NormalizedValue.CreateInteger(request.MaxSelections) }
            };
            if (owner.EffectTypeId == FacilitySelectionEffectExecutor.TypeId)
                facts.Add(new NormalizedValueEntry { Name = "promptKey", Value = NormalizedValue.CreateString(request.PromptKey) });
            foreach (string name in new[] { "minAmount", "maxAmount", "totalAmount", "rowIds", "rowMaximums" })
            {
                var value = ItemTransferEffectExecutor.Value(request.PromptParameters, name);
                if (value != null && (name == "minAmount" || name == "maxAmount" || name == "totalAmount"
                    ? value.Kind == NormalizedValueKind.Integer : value.Kind == NormalizedValueKind.Array))
                    facts.Add(new NormalizedValueEntry { Name = name, Value = value.Clone() });
            }
            var completed = ItemTransferEffectExecutor.Value(repeat?.ContinuationData, "completedCount");
            var total = ItemTransferEffectExecutor.Value(repeat?.ContinuationData, "requestedCount");
            if (completed?.Kind == NormalizedValueKind.Integer && total?.Kind == NormalizedValueKind.Integer &&
                completed.IntegerValue >= 0 && total.IntegerValue >= completed.IntegerValue && total.IntegerValue <= int.MaxValue)
            {
                facts.Add(new NormalizedValueEntry { Name = "completedCount", Value = completed.Clone() });
                facts.Add(new NormalizedValueEntry { Name = "totalCount", Value = total.Clone() });
            }
            var rows = ItemTransferEffectExecutor.Value(request.PromptParameters, "rowIds");
            var rowIds = rows?.Kind == NormalizedValueKind.Array
                ? rows.Items.Select(value => value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue)
                : Enumerable.Empty<string>();
            void Present(EffectNodeRuntimeState scriptOwner, bool progressOnly)
            {
                string path = scriptOwner?.Presentation?.PresentationScript;
                if (string.IsNullOrEmpty(path)) return;
                var context = new GameplayPresentationContext {
                    Purpose = "interaction", EffectId = scriptOwner.EffectId, InteractionId = request.GetStableInteractionId(),
                    AbilityId = scriptOwner.ContinuationAbilityId ?? string.Empty, SourceItemId = source?.ItemId ?? string.Empty,
                    SourceDefinitionId = source?.DefinitionId ?? string.Empty, ViewerPlayerId = request.AnsweringPlayerId,
                    StateRevision = request.StateRevision, Facts = NormalizedValue.CreateObject(facts) };
                string diagnostic;
                try
                {
                    if (registry.GameplayPresenter(path, context, out var result, out diagnostic))
                    {
                        GameplayPresentationNormalizer.Validate(result);
                        var incoming = GameplayPresentationNormalizer.ProjectInteraction(result?.Interaction, true,
                            request.CandidateIds, rowIds, CanSeeProvidedArgument);
                        if (incoming == null) return;
                        if (progressOnly)
                        {
                            if (incoming.Progress == null) return;
                            request.Presentation = request.Presentation ?? new GameplayInteractionPresentation();
                            request.Presentation.Progress = incoming.Progress;
                        }
                        else request.Presentation = MergeInteractionPresentation(request.Presentation, incoming);
                        return;
                    }
                }
                catch (Exception error) { diagnostic = error.Message; }
                work.EffectRuntime.Diagnostics.Add(new EffectDiagnosticRuntimeState {
                    Code = "presentation_failed", Message = diagnostic ?? string.Empty,
                    NodeId = scriptOwner.EffectId, StateRevision = request.StateRevision });
            }
            Present(owner, false);
            if (repeat != null && repeat.EffectId != owner.EffectId) Present(repeat, true);
        }

        private static GameplayInteractionPresentation ResolveInteractionPresentation(
            EffectNodeRuntimeState owner, EffectInteractionSpec spec)
        {
            var requestPresentation = spec.Presentation;
            var nodePresentation = owner.Presentation?.Interaction;
            string defaultKey;
            switch (spec.InteractionTypeId)
            {
                case "setup.influence.optional": defaultKey = "core:interaction.map.initial_influence"; break;
                case "effect.influence.place.target": defaultKey = spec.AllowDecline ? "core:interaction.map.influence.place.optional" : "core:interaction.map.influence.place"; break;
                case "effect.influence.remove.target": defaultKey = spec.AllowDecline ? "core:interaction.map.influence.remove.optional" : "core:interaction.map.influence.remove"; break;
                case "lua.choice": defaultKey = spec.PromptKey == "effect.influence.replace.choose_target" ? "core:interaction.map.influence.replace" : string.Empty; break;
                case "effect.city.move.target": defaultKey = "core:interaction.map.city_move"; break;
                case "entrance.location": defaultKey = "core:interaction.map.entrance"; break;
                case "effect.route.choose": defaultKey = "core:interaction.map.route"; break;
                case "action.decline_effect": defaultKey = spec.PromptKey == "effect.decline" ? "core:interaction.effect.optional" : string.Empty; break;
                default: defaultKey = string.Empty; break;
            }
            bool nativeDefault = !string.IsNullOrEmpty(defaultKey) && requestPresentation?.Prompt?.Key == defaultKey &&
                requestPresentation.Prompt.Arguments.Count == 0 && requestPresentation.Progress == null &&
                requestPresentation.Options.Count == 0 && requestPresentation.QuantityRows.Count == 0;
            if (!nativeDefault) return GameplayPresentationNormalizer.Clone(requestPresentation ?? nodePresentation);
            // 可选节点的 Interaction 已用于 gate；后续同节点地图请求使用自己的正文。
            if (owner.AllowDecline && spec.InteractionTypeId != "action.decline_effect") nodePresentation = null;
            return nodePresentation == null ? GameplayPresentationNormalizer.Clone(requestPresentation)
                : MergeInteractionPresentation(requestPresentation, nodePresentation);
        }

        private static GameplayInteractionPresentation MergeInteractionPresentation(
            GameplayInteractionPresentation current, GameplayInteractionPresentation incoming)
        {
            var result = GameplayPresentationNormalizer.Clone(current) ?? new GameplayInteractionPresentation();
            if (incoming.Prompt != null) result.Prompt = incoming.Prompt;
            if (incoming.Progress != null) result.Progress = incoming.Progress;
            foreach (var option in incoming.Options)
            {
                var target = result.Options.Find(value => value.CandidateId == option.CandidateId);
                if (target == null) { result.Options.Add(option); continue; }
                if (option.Label != null) target.Label = option.Label;
                if (option.Description != null) target.Description = option.Description;
                if (option.DisabledReason != null) target.DisabledReason = option.DisabledReason;
                if (!string.IsNullOrEmpty(option.IconKey)) target.IconKey = option.IconKey;
            }
            foreach (var row in incoming.QuantityRows)
            {
                var target = result.QuantityRows.Find(value => value.RowId == row.RowId);
                if (target == null) { result.QuantityRows.Add(row); continue; }
                if (row.Label != null) target.Label = row.Label;
                if (row.Description != null) target.Description = row.Description;
                if (row.Unit != null) target.Unit = row.Unit;
                if (!string.IsNullOrEmpty(row.IconKey)) target.IconKey = row.IconKey;
            }
            return result;
        }

        private void AddBlockerOnWorking(
            GameState work,
            string ownerEffectId,
            string targetEffectId,
            string interactionRequestId,
            string blockerKind,
            string reason,
            string blockerId)
        {
            EffectRuntimeState runtime = work.EffectRuntime;
            EffectNodeRuntimeState owner = runtime.EffectNodes.Find(candidate => candidate.EffectId == ownerEffectId);
            if (owner == null) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "Blocker owner 不存在。");
            if (IsTerminal(owner.Status)) throw new KernelException(EffectFaultCodes.BlockerOnTerminal, "不能阻塞已终止节点。");

            if (!string.IsNullOrEmpty(targetEffectId))
            {
                EffectNodeRuntimeState target = runtime.EffectNodes.Find(candidate => candidate.EffectId == targetEffectId);
                if (target == null) throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "Blocker target 不存在。");
                if (IsTerminal(target.Status)) throw new KernelException(EffectFaultCodes.BlockerOnTerminal, "不能等待已终止节点。");
                if (ownerEffectId == targetEffectId || WouldCreateCycle(runtime, ownerEffectId, targetEffectId))
                {
                    throw new KernelException(EffectFaultCodes.BlockerCycle, "Blocker 会形成循环依赖。");
                }
            }
            else
            {
                InteractionRequest request = runtime.InteractionRequests.Find(candidate => candidate.RequestId == interactionRequestId);
                if (request == null || request.Status != "open")
                {
                    throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "交互 Blocker target 不存在或未开放。");
                }
            }

            if (runtime.Blockers.Exists(candidate => candidate.BlockerId == blockerId)) return;
            runtime.Blockers.Add(new EffectBlockerRuntimeState
            {
                BlockerId = blockerId,
                OwnerEffectId = ownerEffectId,
                TargetEffectId = targetEffectId ?? string.Empty,
                InteractionRequestId = interactionRequestId ?? string.Empty,
                BlockerKind = blockerKind ?? string.Empty,
                Reason = reason ?? string.Empty,
                IsResolved = false
            });
            owner.BlockerIds.Add(blockerId);
            owner.Status = EffectNodeStatus.Blocked;
        }

        private InteractionRequest FindInteraction(string interactionId)
        {
            return FindInteraction(Runtime, interactionId);
        }

        private static InteractionRequest FindInteraction(EffectRuntimeState runtime, string interactionId)
        {
            if (runtime == null || runtime.InteractionRequests == null) return null;
            for (int i = 0; i < runtime.InteractionRequests.Count; i++)
            {
                InteractionRequest request = runtime.InteractionRequests[i];
                if (request != null && (request.RequestId == interactionId || request.InteractionId == interactionId))
                {
                    return request;
                }
            }

            return null;
        }

        private bool TryValidateInteractionAnswer(
            InteractionRequest request,
            NormalizedValue answer,
            out string diagnostic)
            => TryValidateInteractionAnswerOnState(state, request, answer, out diagnostic);

        public static bool TryValidateInteractionAnswerOnState(GameState state, InteractionRequest request, NormalizedValue answer, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state?.EffectRuntime == null || request == null || answer == null)
            {
                diagnostic = "交互答案为空。";
                return false;
            }
            if (request.AllowDecline && answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                return true;
            if (request?.AnswerSchema == ResourceAllocationAnswer.BoundedAnswerSchema)
            {
                var minimum = ItemTransferEffectExecutor.Value(request.PromptParameters, "minAmount");
                var maximum = ItemTransferEffectExecutor.Value(request.PromptParameters, "maxAmount");
                var rowLimits = ItemTransferEffectExecutor.Value(request.PromptParameters, "rowMaximums");
                var limits = new Dictionary<string, int>(StringComparer.Ordinal);
                if (minimum?.Kind != NormalizedValueKind.Integer || maximum?.Kind != NormalizedValueKind.Integer || rowLimits?.Kind != NormalizedValueKind.Object || answer?.Kind != NormalizedValueKind.String)
                { diagnostic = "bounded_resource_allocation_contract_invalid"; return false; }
                foreach (var entry in rowLimits.Properties)
                {
                    if (entry.Value.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > 99)
                    { diagnostic = "bounded_resource_allocation_row_invalid"; return false; }
                    limits.Add(entry.Name, (int)entry.Value.IntegerValue);
                }
                return ResourceAllocationAnswer.TryParseBounded(answer.StringValue, request.CandidateIds, (int)minimum.IntegerValue, (int)maximum.IntegerValue, limits, out _, out diagnostic);
            }
            if (request.AnswerSchema == ContentPrimitiveEffectExecutor.QuantityAnswerSchema)
            {
                bool valid = request.InteractionTypeId == ContentPrimitiveEffectExecutor.QuantityInteractionTypeId &&
                    ContentPrimitiveEffectExecutor.TryReadBoundedAmount(request.PromptParameters, answer, out _);
                if (!valid) diagnostic = "quantity_answer_out_of_range";
                return valid;
            }
            // 兼容已保存的旧版放弃确认；新版 UI 使用 continue / decline 明确区分。
            if (request.InteractionTypeId == "action.decline_effect" && answer.Kind == NormalizedValueKind.Boolean)
                return true;

            if (request.AnswerSchema == "resource_allocation" && request.CandidateIds != null)
            {
                foreach (var candidate in request.CandidateIds)
                    if (candidate.StartsWith("total:", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(candidate.Substring(6), out int total) || answer.Kind != NormalizedValueKind.String)
                        { diagnostic = "资源分配请求或答案格式无效。"; return false; }
                        var allowed = request.CandidateIds.FindAll(id => !id.StartsWith("total:", StringComparison.Ordinal));
                        return YC.Domain.Interactions.ResourceAllocationAnswer.TryParse(answer.StringValue, allowed, total, out _, out diagnostic);
                    }
            }

            if (request.AnswerSchema == "boolean_accept")
            {
                if (answer.Kind != NormalizedValueKind.Boolean || !answer.BooleanValue)
                {
                    diagnostic = "确认交互必须提交 true。";
                    return false;
                }

                return true;
            }

            if (answer.Kind == NormalizedValueKind.Boolean &&
                request.CandidateIds != null && request.CandidateIds.Count == 0 &&
                request.MinSelections == 0 && request.MaxSelections == 0)
            {
                return true;
            }

            var selectedIds = new List<string>();
            if (answer.Kind == NormalizedValueKind.Array)
            {
                if (answer.Items == null)
                {
                    diagnostic = "交互答案数组非法。";
                    return false;
                }

                for (int i = 0; i < answer.Items.Count; i++)
                {
                    string candidateId;
                    if (!TryReadCandidateId(answer.Items[i], out candidateId))
                    {
                        diagnostic = "交互答案只能包含稳定候选 ID。";
                        return false;
                    }

                    if (selectedIds.Contains(candidateId))
                    {
                        diagnostic = "交互答案不能重复选择同一候选。";
                        return false;
                    }

                    selectedIds.Add(candidateId);
                }
            }
            else if (answer.Kind == NormalizedValueKind.String ||
                     answer.Kind == NormalizedValueKind.StableReference)
            {
                string candidateId;
                if (!TryReadCandidateId(answer, out candidateId))
                {
                    diagnostic = "交互答案不是有效候选 ID。";
                    return false;
                }

                selectedIds.Add(candidateId);
            }
            else
            {
                diagnostic = "交互答案不是候选 ID 或选项值。";
                return false;
            }

            if (selectedIds.Count < request.MinSelections || selectedIds.Count > request.MaxSelections)
            {
                diagnostic = "交互答案数量不满足约束。";
                return false;
            }

            for (int i = 0; i < selectedIds.Count; i++)
            {
                if (request.CandidateIds == null || !request.CandidateIds.Contains(selectedIds[i]))
                {
                    diagnostic = "交互答案不在当前候选集中。";
                    return false;
                }

                if (!string.IsNullOrEmpty(request.CandidateResolutionId))
                {
                    CandidateResolutionRecord resolution = state.EffectRuntime.CandidateResolutions.Find(candidate =>
                        candidate != null && candidate.CandidateSetId == request.CandidateResolutionId &&
                        candidate.Version == request.CandidateSetVersion);
                    if (resolution == null || !resolution.FinalCandidateIds.Contains(selectedIds[i]))
                    {
                        diagnostic = "交互候选解析已失效，请刷新当前交互。";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryReadCandidateId(NormalizedValue value, out string candidateId)
        {
            candidateId = string.Empty;
            if (value == null) return false;
            if (value.Kind == NormalizedValueKind.String)
            {
                candidateId = value.StringValue ?? string.Empty;
                return !string.IsNullOrEmpty(candidateId);
            }

            if (value.Kind == NormalizedValueKind.StableReference &&
                (value.ReferenceType == "candidate" || value.ReferenceType == "option"))
            {
                candidateId = value.ReferenceId ?? string.Empty;
                return !string.IsNullOrEmpty(candidateId);
            }

            return false;
        }

        private static bool AreSameIds(IList<string> left, IList<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        private static string FormatIds(IList<string> ids)
        {
            return ids == null ? "null" : string.Join(",", new List<string>(ids).ToArray());
        }

        private static bool WouldCreateCycle(
            EffectRuntimeState runtime,
            string ownerEffectId,
            string targetEffectId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(targetEffectId);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!visited.Add(current)) continue;
                if (current == ownerEffectId) return true;
                for (int i = 0; i < runtime.Blockers.Count; i++)
                {
                    EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                    if (blocker != null && !blocker.IsResolved && blocker.OwnerEffectId == current &&
                        !string.IsNullOrEmpty(blocker.TargetEffectId))
                    {
                        pending.Push(blocker.TargetEffectId);
                    }
                }
            }

            return false;
        }

        private static void ResolveInteractionBlockerOnWorking(
            EffectRuntimeState runtime,
            string requestId)
        {
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && blocker.InteractionRequestId == requestId)
                {
                    blocker.IsResolved = true;
                }
            }
        }

        private void OnNodeTerminalOnWorking(
            GameState work,
            EffectRuntimeState runtime,
            EffectNodeRuntimeState node,
            List<RuleJournalEntry> entries)
        {
            if (node.EffectTypeId.StartsWith("timing.handler.", StringComparison.Ordinal) && node.FailureReason == "whole_effect_declined")
            {
                var source = runtime.EffectNodes.Find(n => n.EffectId == ItemTransferEffectExecutor.Text(node.NormalizedArguments, "sourceEffectId"));
                var binding = source?.TimingBindings.Find(b => b.BindingId == ItemTransferEffectExecutor.Text(node.NormalizedArguments, "bindingId"));
                if (binding != null) { binding.Status = "cancelled"; binding.AttachedEffectId = node.EffectId; }
            }
            foreach (var windowOwner in runtime.EffectNodes.Where(n => n.SettlementWindow != null))
            {
                var window = windowOwner.SettlementWindow;
                bool wasActive = window.ActiveTargetEffectId == node.EffectId;
                SettlementEntryService.ObserveTerminal(window, node);
                if (window.Context.OwnerEffectId == node.EffectId) window.IsClosed = true;
                if (wasActive && window.Context.Kind != SettlementEntryService.PlayerCleanup)
                {
                    bool resumePayment = window.Context.Kind == SettlementEntryService.ResourcePayment && windowOwner.Payment != null &&
                        !windowOwner.Payment.IsCommitted && !windowOwner.Payment.IsCommitRequested;
                    window.IsClosed = !resumePayment;
                    if (window.Context.Kind == SettlementEntryService.ResourcePayment && !string.IsNullOrEmpty(window.ResumeFlowStage))
                    {
                        windowOwner.FlowStage = window.ResumeFlowStage;
                        if (resumePayment) ReopenSuspendedPaymentOnWorking(work, windowOwner, window);
                    }
                }
                if (window.Context.Kind == SettlementEntryService.PlayerCleanup && window.Entries.Count > 0 &&
                    window.Entries.All(e => e.SettlementState == "resolved" || e.SettlementState == "declined")) window.IsClosed = true;
                if (window.IsClosed && runtime.ActiveSettlementWindowId == window.Context.Id)
                    runtime.ActiveSettlementWindowId = SettlementHostProtocol.FindAncestorWindowId(runtime, windowOwner.ParentEffectId);
            }
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && blocker.TargetEffectId == node.EffectId)
                {
                    blocker.IsResolved = true;
                    EffectNodeRuntimeState owner = runtime.EffectNodes.Find(candidate => candidate.EffectId == blocker.OwnerEffectId);
                    if (owner != null) ActivateNextChild(runtime, owner);
                }
            }

            for (int i = 0; i < runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState owner = runtime.EffectNodes[i];
                if (owner == null || owner.Status != EffectNodeStatus.Blocked || HasUnresolvedBlocker(runtime, owner)) continue;
                if (owner.PendingOutcome == EffectPendingOutcome.None) owner.Status = EffectNodeStatus.Ready;
            }

            if (nodeTerminalObserver != null)
            {
                nodeTerminalObserver(work, node);
            }
            ActivateAffectedEntranceOwners(work, node.EffectId, entries);
        }

        private static void ActivateAffectedEntranceOwners(
            GameState work, string changedEffectId, List<RuleJournalEntry> entries)
        {
            var runtime = work.EffectRuntime;
            var ownerIds = RoundExecutionService.GetEntranceSchedulingOwners(runtime, changedEffectId);
            if (ownerIds == null || ownerIds.Count == 0) return;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ownerId in ownerIds)
            {
                if (!visited.Add(ownerId)) continue;
                var owner = runtime.EffectNodes.Find(candidate => candidate.EffectId == ownerId);
                if (owner == null)
                    throw new KernelException(EffectFaultCodes.BlockerTargetMissing, "入场调度所属节点不存在。");
                if (IsTerminal(owner.Status)) continue;
                var created = owner.ChildEffectIds.Where(id => runtime.EffectNodes.Exists(candidate =>
                    candidate.EffectId == id && candidate.Status == EffectNodeStatus.Created)).ToList();
                ActivateNextChild(runtime, owner);
                foreach (var childId in created)
                {
                    var child = runtime.EffectNodes.Find(candidate => candidate.EffectId == childId);
                    if (child.Status != EffectNodeStatus.Ready) continue;
                    entries.Add(new RuleJournalEntry
                    {
                        Kind = RuleJournalEntryKind.EffectNode,
                        EntityId = childId,
                        Detail = "entrance_child_ready"
                    });
                }
            }
        }

        private static void ActivateNextChild(EffectRuntimeState runtime, EffectNodeRuntimeState owner)
        {
            if (owner.ChildEffectIds == null) return;
            for (int i = 0; i < owner.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = runtime.EffectNodes.Find(candidate => candidate.EffectId == owner.ChildEffectIds[i]);
                if (child == null || IsNormalTerminal(child.Status)) continue;
                if (owner.SettlementWindow?.Context?.Kind == SettlementEntryService.PlayerCleanup && owner.SettlementWindow.Entries.Exists(e => e.TargetEffectId == child.EffectId))
                {
                    if (!runtime.IsChildSchedulingEligible(owner, i)) continue;
                    if (child.Status == EffectNodeStatus.Created) child.Status = EffectNodeStatus.Ready;
                    return;
                }
                var group = owner.ChildSchedulingGroups.Find(value => i >= value.StartIndex && i - value.StartIndex < value.ChildEffectIds.Count);
                if (group == null && child.Status != EffectNodeStatus.Created) return;
                if (group == null && !runtime.IsChildSchedulingEligible(owner, i)) return;
                int end = group == null ? i + 1 : group.StartIndex + group.ChildEffectIds.Count;
                for (int member = i; member < end; member++)
                {
                    var candidate = runtime.EffectNodes.Find(value => value.EffectId == owner.ChildEffectIds[member]);
                    if (candidate != null && candidate.Status == EffectNodeStatus.Created &&
                        runtime.IsChildSchedulingEligible(owner, member)) candidate.Status = EffectNodeStatus.Ready;
                }
                return;
            }
        }

        private static bool HasUnresolvedBlocker(EffectRuntimeState runtime, EffectNodeRuntimeState node)
        {
            if (node == null || node.BlockerIds == null) return false;
            for (int i = 0; i < node.BlockerIds.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers.Find(candidate => candidate.BlockerId == node.BlockerIds[i]);
                if (blocker != null && !blocker.IsResolved && !IsTerminalTarget(runtime, blocker)) return true;
            }

            return false;
        }

        private static void ResolveTerminalTargetBlockersOnWorking(
            EffectRuntimeState runtime,
            EffectNodeRuntimeState owner)
        {
            if (runtime == null || owner == null || owner.BlockerIds == null) return;
            for (int i = 0; i < owner.BlockerIds.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers.Find(candidate =>
                    candidate != null && candidate.BlockerId == owner.BlockerIds[i]);
                if (blocker != null && !blocker.IsResolved && IsTerminalTarget(runtime, blocker))
                {
                    blocker.IsResolved = true;
                }
            }
        }

        private static bool IsTerminalTarget(EffectRuntimeState runtime, EffectBlockerRuntimeState blocker)
        {
            if (runtime == null || blocker == null || string.IsNullOrEmpty(blocker.TargetEffectId)) return false;
            EffectNodeRuntimeState target = runtime.EffectNodes.Find(candidate =>
                candidate != null && candidate.EffectId == blocker.TargetEffectId);
            // Faulted 不是普通终态：它表示所属 Effect 根已经暂停，不能解除父节点
            // 的正常 blocker，也不能让有序兄弟继续推进。
            return target != null && IsNormalTerminal(target.Status);
        }

        private bool IsSchedulingEligible(EffectNodeRuntimeState node)
        {
            if (node.Payment?.Preparation?.ActionKind == "city_move" && !node.Payment.Preparation.IsCancelled &&
                !node.Payment.Preparation.IsPromoted && node.Payment.Preparation.TargetId.Length == 0) return false;
            if (string.IsNullOrEmpty(node.ParentEffectId)) return true;
            EffectNodeRuntimeState parent = GetNode(node.ParentEffectId);
            if (parent == null || IsTerminal(parent.Status)) return false;
            int index = parent.ChildEffectIds.IndexOf(node.EffectId);
            return index >= 0 && Runtime.IsChildSchedulingEligible(parent, index);
        }

        private static int GetDepth(EffectRuntimeState runtime, string nodeId)
        {
            int depth = 0;
            string current = nodeId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(current))
            {
                if (!visited.Add(current)) throw new KernelException(EffectFaultCodes.BlockerCycle, "Effect 父关系形成循环。");
                EffectNodeRuntimeState node = runtime.EffectNodes.Find(candidate => candidate.EffectId == current);
                if (node == null) break;
                depth++;
                current = node.ParentEffectId;
            }

            return depth;
        }

        private EffectNodeRuntimeState CreateNode(
            EffectSpec spec,
            string nodeId,
            string roundId,
            string parentId)
        {
            EffectRegistration registration = registry.Get(spec.EffectTypeId);
            registration.Validate(spec);
            YC.Domain.Presentation.GameplayPresentationNormalizer.Validate(spec.Presentation);
            if (!GameplayPresentationStateValidator.TryValidateSpecMetadata(spec.Presentation, spec.Settlement, out var metadataReason))
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, metadataReason);
            EffectNodeRuntimeState node = new EffectNodeRuntimeState
            {
                Settlement = GameStateCloneService.DeepClone(spec.Settlement),
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(spec.Presentation),
                EffectId = nodeId,
                RoundId = roundId ?? string.Empty,
                ParentEffectId = parentId ?? string.Empty,
                SourceId = spec.SourceId ?? string.Empty,
                EffectTypeId = spec.EffectTypeId ?? string.Empty,
                DefinitionVersion = string.IsNullOrEmpty(spec.DefinitionVersion)
                    ? registration.DefinitionVersion
                    : spec.DefinitionVersion,
                PlayerId = spec.PlayerId,
                Visibility = spec.Visibility ?? string.Empty,
                Status = EffectNodeStatus.Created,
                PendingOutcome = EffectPendingOutcome.None,
                NormalizedArguments = spec.NormalizedArguments.Clone(),
                NormalizedResult = NormalizedValue.CreateNull(),
                FailureReason = string.Empty,
                ChildEffectIds = new List<string>(),
                BlockerIds = new List<string>(),
                NestedEffects = new List<EffectSpecRuntimeState>(),
                NestedGroupSizes = new List<int>(),
                ContinuationHandlerId = spec.CompletionHandlerId ?? string.Empty,
                ContinuationContext = spec.ContinuationContext?.Clone(),
                ContinuationScopeRootEffectId = spec.ContinuationContext == null ? "" : nodeId,
                ContinuationBindingId = string.Empty,
                ContinuationContentId = spec.ContinuationContentId ?? string.Empty,
                ContinuationContentInstanceId = spec.ContinuationContentInstanceId ?? string.Empty,
                ContinuationAbilityId = spec.ContinuationAbilityId ?? string.Empty,
                ContinuationDefinitionVersion = string.IsNullOrEmpty(spec.ContinuationDefinitionVersion)
                    ? (string.IsNullOrEmpty(spec.DefinitionVersion)
                        ? registration.DefinitionVersion
                        : spec.DefinitionVersion)
                    : spec.ContinuationDefinitionVersion,
                ContinuationContentHash = spec.ContinuationContentHash ?? string.Empty,
                AllowDecline = spec.AllowDecline,
                DecisionPlayerId = spec.DecisionPlayerId >= 0 ? spec.DecisionPlayerId : spec.PlayerId,
                DeclinePromptKey = spec.DeclinePromptKey ?? string.Empty,
                UnavailablePolicy = spec.UnavailablePolicy ?? string.Empty,
                FlowStage = string.Empty
            };

            if (spec.NestedEffectGroups != null && spec.NestedEffectGroups.Count > 0)
            {
                for (int i = 0; i < spec.NestedEffectGroups.Count; i++)
                {
                    IList<EffectSpec> group = spec.NestedEffectGroups[i];
                    int count = group == null ? 0 : group.Count;
                    node.NestedGroupSizes.Add(count);
                    if (group == null) continue;
                    for (int j = 0; j < group.Count; j++)
                    {
                        ValidateSpec(group[j], 1, 0);
                        node.NestedEffects.Add(group[j].ToRuntimeState());
                    }
                }
            }

            if (spec.Children != null && spec.Children.Count > 0)
            {
                node.NestedGroupSizes.Add(spec.Children.Count);
                for (int i = 0; i < spec.Children.Count; i++)
                {
                    ValidateSpec(spec.Children[i], 1, 0);
                    node.NestedEffects.Add(spec.Children[i].ToRuntimeState());
                }
            }

            return node;
        }

        private static void AddContinuationBinding(
            EffectRuntimeState runtime,
            EffectNodeRuntimeState node)
        {
            if (runtime == null || node == null || string.IsNullOrEmpty(node.ContinuationHandlerId)) return;

            string bindingId = StableIdFactory.Create(
                "continuation",
                node.EffectId,
                node.ContinuationContentId ?? string.Empty,
                node.ContinuationContentInstanceId ?? string.Empty,
                node.ContinuationAbilityId ?? string.Empty,
                node.ContinuationHandlerId,
                node.ContinuationDefinitionVersion ?? string.Empty,
                node.ContinuationContentHash ?? string.Empty);
            node.ContinuationBindingId = bindingId;
            if (runtime.ContinuationBindings.Exists(candidate => candidate != null && candidate.BindingId == bindingId)) return;
            runtime.ContinuationBindings.Add(new ContinuationBinding
            {
                BindingId = bindingId,
                SourceEffectId = node.EffectId,
                ContentId = node.ContinuationContentId ?? string.Empty,
                ContentInstanceId = node.ContinuationContentInstanceId ?? string.Empty,
                AbilityId = node.ContinuationAbilityId ?? string.Empty,
                HandlerId = node.ContinuationHandlerId ?? string.Empty,
                DefinitionVersion = node.ContinuationDefinitionVersion ?? string.Empty,
                ContentHash = node.ContinuationContentHash ?? string.Empty,
                Status = ContinuationBindingStatus.Attached
            });
        }

        private void ValidateSpecList(IList<EffectSpec> specs, int depth)
        {
            if (specs == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "EffectSpec 列表不能为 null。");
            if (specs.Count > limits.MaxNodeCount) throw new KernelException(EffectFaultCodes.NodeLimitExceeded, "一次返回的 Effect 数量超过上限。");
            for (int i = 0; i < specs.Count; i++) ValidateSpec(specs[i], depth, 0);
        }

        private void ValidateSpec(EffectSpec spec, int depth, int elementCount)
        {
            if (spec == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "EffectSpec 不能为 null。");
            if (depth > limits.MaxTreeDepth) throw new KernelException(EffectFaultCodes.TreeDepthExceeded, "Effect 描述嵌套深度超过上限。");
            EffectRegistration registration;
            if (!registry.TryGet(spec.EffectTypeId, out registration))
            {
                throw new KernelException(EffectFaultCodes.UnknownEffectType, "未知 Effect 类型：" + spec.EffectTypeId);
            }

            registration.Validate(spec);
            string reason = string.Empty;
            if (spec.NormalizedArguments == null || !spec.NormalizedArguments.TryValidate(out reason))
            {
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, reason);
            }

            if (spec.Children != null)
            {
                if (spec.Children.Count > limits.MaxNodeCount) throw new KernelException(EffectFaultCodes.NodeLimitExceeded, "Effect 子节点数量超过上限。");
                for (int i = 0; i < spec.Children.Count; i++) ValidateSpec(spec.Children[i], depth + 1, elementCount + 1);
            }

            if (spec.NestedEffectGroups != null)
            {
                for (int i = 0; i < spec.NestedEffectGroups.Count; i++)
                {
                    IList<EffectSpec> group = spec.NestedEffectGroups[i];
                    if (group == null) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "嵌套 Effect 组不能为 null。");
                    for (int j = 0; j < group.Count; j++) ValidateSpec(group[j], depth + 1, elementCount + 1);
                }
            }
        }

        private static void ValidateResult(EffectStepResult result)
        {
            if (result.HasPendingOutcome && result.PendingOutcome == EffectPendingOutcome.None)
            {
                throw new KernelException(EffectFaultCodes.InvalidHandlerResult, "处理器结果的终态不能为空。");
            }

            if (result.NormalizedResult != null)
            {
                string reason;
                if (!result.NormalizedResult.TryValidate(out reason))
                {
                    throw new KernelException(EffectFaultCodes.InvalidHandlerResult, reason);
                }
            }
        }

        private void CommitStatus(string nodeId, EffectNodeStatus status, string detail)
        {
            CommitNewState(
                detail + ":" + nodeId,
                new List<RuleJournalEntry>(),
                work =>
                {
                    EffectNodeRuntimeState node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == nodeId);
                    if (node == null) throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "节点不存在：" + nodeId);
                    EnsureTransition(node.Status, status);
                    node.Status = status;
                },
                new[] { nodeId });
        }

        private void CommitNewState(
            string commandId,
            List<RuleJournalEntry> entries,
            Action<GameState> mutation,
            IList<string> touchedNodeIds)
        {
            GameState work = GameStateCloneService.DeepClone(state);
            mutation(work);
            if (entries.Count == 0)
            {
                entries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.EffectNode,
                    EntityId = touchedNodeIds == null || touchedNodeIds.Count == 0 ? commandId : touchedNodeIds[0],
                    Detail = commandId
                });
            }

            CommitPreparedWork(work, commandId, entries, -1, -1, touchedNodeIds);
        }

        private void CommitPreparedWork(
            GameState work,
            string commandId,
            IList<RuleJournalEntry> entries,
            int eventStart,
            int receiptStart,
            IList<string> touchedNodeIds)
        {
            if (work == null || work.EffectRuntime == null) throw new KernelException(EffectFaultCodes.InvalidRuntimeState, "工作状态为空。");
            if (!YC.Domain.Cards.CharacterLifecycleService.ValidateRetainedBasicCharacters(work, out string retentionReason))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, retentionReason);
            if (!TryValidateDeferredResponses(work, registry, out string deferredReason))
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, deferredReason);
            var commitEntries = entries == null ? new List<RuleJournalEntry>() : new List<RuleJournalEntry>(entries);
            if (commitEntries.Count == 0)
            {
                commitEntries.Add(new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.EffectNode,
                    EntityId = commandId ?? string.Empty,
                    Detail = "effect_runtime"
                });
            }

            work.EffectRuntime.StepCount = checked(work.EffectRuntime.StepCount + 1);
            OperationWindowService.Synchronize(work);
            RuleCommit commit;
            try
            {
                commit = RuleCommit.ApplyPreparedEffectCommit(work, commandId ?? string.Empty, commitEntries.ToArray());
            }
            catch (InvalidOperationException exception)
            {
                throw new KernelException(
                    EffectFaultCodes.InvalidRuntimeState,
                    exception.Message +
                    "（command=" + (commandId ?? string.Empty) +
                    ", schema=" + work.EffectRuntime.SchemaVersion.ToString(CultureInfo.InvariantCulture) +
                    ", status=" + work.EffectRuntime.Status +
                    ", nodes=" + work.EffectRuntime.EffectNodes.Count.ToString(CultureInfo.InvariantCulture) +
                    ", revision=" + work.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture) + "）",
                    exception);
            }
            for (int i = 0; i < commit.Entries.Count; i++)
            {
                RuleJournalEntry entry = commit.Entries[i];
                if (entry.Kind == RuleJournalEntryKind.EffectNode)
                {
                    EffectNodeRuntimeState createdNode = work.EffectRuntime.EffectNodes.Find(
                        candidate => candidate.EffectId == entry.EntityId);
                    if (createdNode != null && (createdNode.LastCommitSequence == 0 ||
                        entry.Detail == "entrance_child_ready"))
                    {
                        createdNode.LastCommitSequence = entry.CommitSequence;
                    }
                }

                for (int j = 0; j < work.EffectRuntime.RuleEvents.Count; j++)
                {
                    RuleEvent ruleEvent = work.EffectRuntime.RuleEvents[j];
                    if (ruleEvent != null && ruleEvent.EventId == entry.EntityId && ruleEvent.StateRevision == 0)
                    {
                        ruleEvent.StateRevision = commit.StateRevision;
                        ruleEvent.CommitSequence = entry.CommitSequence;
                    }
                }

                for (int j = 0; j < work.EffectRuntime.DispatchReceipts.Count; j++)
                {
                    DispatchReceipt receipt = work.EffectRuntime.DispatchReceipts[j];
                    if (receipt != null && receipt.ReceiptId == entry.EntityId && receipt.CommitSequence == 0)
                    {
                        receipt.CommitSequence = entry.CommitSequence;
                    }
                }
            }

            if (touchedNodeIds != null)
            {
                for (int i = 0; i < touchedNodeIds.Count; i++)
                {
                    EffectNodeRuntimeState node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == touchedNodeIds[i]);
                    if (node != null) node.LastCommitSequence = commit.LastCommitSequence;
                }
            }

            GameStateCloneService.CopyTo(state, work);
        }

        private void Fault(string code, string message, string nodeId, string eventId)
        {
            lastDiagnostic = code + ": " + (message ?? string.Empty);
            if (state.EffectRuntime == null) state.EffectRuntime = new EffectRuntimeState();
            if (state.EffectRuntime.Status == EffectRuntimeStatus.PausedFault) return;

            try
            {
                GameState work = GameStateCloneService.DeepClone(state);
                EffectRuntimeState runtime = work.EffectRuntime;
                for (int i = 0; i < runtime.EffectNodes.Count; i++)
                {
                    EffectNodeRuntimeState node = runtime.EffectNodes[i];
                    if (node == null || IsTerminal(node.Status)) continue;
                    if (string.IsNullOrEmpty(nodeId) ||
                        node.EffectId == nodeId ||
                        IsAncestorOf(runtime, node.EffectId, nodeId))
                    {
                        // 保管中的旁路子树保持可恢复身份；故障仍暂停整个runtime。
                        if (!TryFindDeferredResponse(work, node.EffectId, out _)) node.Status = EffectNodeStatus.Faulted;
                    }
                }

                runtime.Status = EffectRuntimeStatus.PausedFault;
                runtime.LastFaultCode = code ?? EffectFaultCodes.InvalidRuntimeState;
                runtime.LastFaultMessage = message ?? string.Empty;
                var diagnostic = new EffectDiagnosticRuntimeState
                {
                    Code = runtime.LastFaultCode,
                    Message = runtime.LastFaultMessage,
                    NodeId = nodeId ?? string.Empty,
                    EventId = eventId ?? string.Empty
                };
                runtime.Diagnostics.Add(diagnostic);
                RuleCommit commit = RuleCommit.ApplyFault(
                    work,
                    "fault:" + runtime.LastFaultCode,
                    new RuleJournalEntry
                    {
                        Kind = RuleJournalEntryKind.Fault,
                        EntityId = nodeId ?? string.Empty,
                        Detail = runtime.LastFaultCode,
                        Payload = NormalizedValue.CreateString(runtime.LastFaultMessage)
                    });
                diagnostic.StateRevision = commit.StateRevision;
                diagnostic.CommitSequence = commit.LastCommitSequence;
                GameStateCloneService.CopyTo(state, work);
            }
            catch (Exception exception)
            {
                // 故障处理本身不能把运行时伪装成可继续状态。
                state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
                state.EffectRuntime.LastFaultCode = code ?? EffectFaultCodes.InvalidRuntimeState;
                state.EffectRuntime.LastFaultMessage = (message ?? string.Empty) + "；故障记录失败：" + exception.Message;
            }
        }

        private static OperationCommandContext OperationCause(GameState work, EffectNodeRuntimeState owner)
        {
            var current = work.EffectRuntime.CurrentOperationContext;
            // 当前事务只覆盖其准确来源节点；其他并发任务消费自己保存的创建/继续因果。
            return current != null && owner?.EffectId == current.SourceEffectId ? current :
                owner?.ContinuationOperationContext ?? owner?.OperationContext;
        }

        private static bool IsAncestorOf(EffectRuntimeState runtime, string candidateId, string descendantId)
        {
            string current = descendantId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(current) && visited.Add(current))
            {
                EffectNodeRuntimeState node = runtime.EffectNodes.Find(candidate => candidate.EffectId == current);
                if (node == null) return false;
                if (node.ParentEffectId == candidateId) return true;
                current = node.ParentEffectId;
            }

            return false;
        }

        private static void EnsureTransition(EffectNodeStatus from, EffectNodeStatus to)
        {
            bool valid =
                (from == EffectNodeStatus.Created && to == EffectNodeStatus.Ready) ||
                (from == EffectNodeStatus.Ready && to == EffectNodeStatus.Running) ||
                (from == EffectNodeStatus.Running && (to == EffectNodeStatus.Blocked || to == EffectNodeStatus.Completed || to == EffectNodeStatus.Failed)) ||
                (from == EffectNodeStatus.Blocked && (to == EffectNodeStatus.Ready || to == EffectNodeStatus.Completed || to == EffectNodeStatus.Failed)) ||
                (from == to);
            if (!valid) throw new KernelException(EffectFaultCodes.InvalidTransition, "非法 Effect 状态转换：" + from + " → " + to);
        }

        private static bool IsTerminal(EffectNodeStatus status)
        {
            return status == EffectNodeStatus.Completed ||
                   status == EffectNodeStatus.Failed ||
                   status == EffectNodeStatus.Faulted;
        }

        private static bool IsNormalTerminal(EffectNodeStatus status)
        {
            return status == EffectNodeStatus.Completed || status == EffectNodeStatus.Failed;
        }

        private bool HasNonTerminalNodes()
        {
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                if (Runtime.EffectNodes[i] != null && !IsTerminal(Runtime.EffectNodes[i].Status)) return true;
            }

            return false;
        }

        private bool HasOpenInteraction()
        {
            for (int i = 0; i < Runtime.InteractionRequests.Count; i++)
            {
                if (Runtime.InteractionRequests[i] != null && Runtime.InteractionRequests[i].Status == "open") return true;
            }

            return false;
        }

        private string BuildNoProgressDiagnostic()
        {
            var details = new List<string>();
            for (int i = 0; i < Runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = Runtime.EffectNodes[i];
                if (node == null || IsTerminal(node.Status)) continue;
                details.Add(
                    node.EffectId +
                    ":type=" + node.EffectTypeId +
                    ":status=" + node.Status +
                    ":stage=" + (node.FlowStage ?? string.Empty) +
                    ":children=" + FormatChildStatuses(node) +
                    ":blockers=" + FormatBlockerStatuses(node));
            }

            return "Effect 运行时存在未终止节点，但没有可执行步骤或合法等待输入。" +
                   (details.Count == 0 ? string.Empty : "节点=" + string.Join("|", details.ToArray()));
        }

        private string FormatChildStatuses(EffectNodeRuntimeState node)
        {
            if (node == null || node.ChildEffectIds == null || node.ChildEffectIds.Count == 0) return "none";
            var values = new List<string>();
            for (int i = 0; i < node.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = Runtime.EffectNodes.Find(candidate =>
                    candidate != null && candidate.EffectId == node.ChildEffectIds[i]);
                values.Add(node.ChildEffectIds[i] + "=" + (child == null ? "missing" : child.Status.ToString()));
            }

            return string.Join(",", values.ToArray());
        }

        private string FormatBlockerStatuses(EffectNodeRuntimeState node)
        {
            if (node == null || node.BlockerIds == null || node.BlockerIds.Count == 0) return "none";
            var values = new List<string>();
            for (int i = 0; i < node.BlockerIds.Count; i++)
            {
                EffectBlockerRuntimeState blocker = Runtime.Blockers.Find(candidate =>
                    candidate != null && candidate.BlockerId == node.BlockerIds[i]);
                if (blocker == null) values.Add(node.BlockerIds[i] + "=missing");
                else values.Add(node.BlockerIds[i] + "=" + (blocker.IsResolved ? "resolved" : "open") +
                                ":target=" + blocker.TargetEffectId + ":request=" + blocker.InteractionRequestId);
            }

            return string.Join(",", values.ToArray());
        }

        private static NormalizedValue CreateObject(Dictionary<string, NormalizedValue> values)
        {
            var entries = new List<NormalizedValueEntry>();
            foreach (KeyValuePair<string, NormalizedValue> pair in values)
            {
                entries.Add(new NormalizedValueEntry { Name = pair.Key, Value = pair.Value });
            }

            return NormalizedValue.CreateObject(entries);
        }
    }

    public class EffectRuntimeEngine : EffectTreeExecutor
    {
        public EffectRuntimeEngine(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits = null)
            : base(state, registry, limits)
        {
        }

        public EffectRuntimeEngine(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits,
            Action<GameState, EffectNodeRuntimeState> nodeTerminalObserver)
            : base(state, registry, limits, nodeTerminalObserver)
        {
        }
    }

    public sealed class EffectExecutor : EffectTreeExecutor
    {
        public EffectExecutor(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits = null)
            : base(state, registry, limits)
        {
        }

        public EffectExecutor(
            GameState state,
            EffectRegistry registry,
            EffectRuntimeLimits limits,
            Action<GameState, EffectNodeRuntimeState> nodeTerminalObserver)
            : base(state, registry, limits, nodeTerminalObserver)
        {
        }
    }

    public sealed class KernelException : Exception
    {
        public KernelException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public KernelException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
