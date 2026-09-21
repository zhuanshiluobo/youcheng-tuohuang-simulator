using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
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
            int decisionPlayerId = -1)
        {
            var spec = new EffectSpec(EffectTypeIds.Condition)
            {
                AllowDecline = allowDecline,
                DecisionPlayerId = decisionPlayerId,
                NestedEffectGroups = new List<List<EffectSpec>>
                {
                    CopySpecs(leftEffects),
                    CopySpecs(rightEffects)
                }
            };
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
        internal EffectExecutionContext(
            GameState state,
            EffectNodeRuntimeState node,
            EffectRegistry registry)
        {
            State = state;
            Node = node;
            Registry = registry;
        }

        public GameState State { get; }
        public EffectNodeRuntimeState Node { get; }
        public EffectRegistry Registry { get; }

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

    public sealed class EffectRegistry
    {
        public string ContentPackHash { get; set; } = string.Empty;
        public Dictionary<string, string> CharacterActivationSubscriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

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
                DefinitionVersion = data.DefinitionVersion,
                SourceId = data.SourceId,
                PlayerId = data.PlayerId,
                StableKey = data.StableKey,
                AllowDecline = data.AllowDecline,
                DecisionPlayerId = data.DecisionPlayerId,
                DeclinePromptKey = data.DeclinePromptKey,
                UnavailablePolicy = data.UnavailablePolicy,
                CompletionHandlerId = data.CompletionHandlerId,
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
                DefinitionVersion = data.DefinitionVersion,
                SourceId = data.SourceId,
                PlayerId = data.PlayerId,
                StableKey = data.StableKey,
                AllowDecline = data.AllowDecline,
                DecisionPlayerId = data.DecisionPlayerId,
                DeclinePromptKey = data.DeclinePromptKey,
                UnavailablePolicy = data.UnavailablePolicy,
                CompletionHandlerId = data.CompletionHandlerId,
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

    public class EffectTreeExecutor
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
            if (TryAttachToCurrentPlayerActionWindow(playerId, spec, out nodeId)) return true;
            if (!string.IsNullOrEmpty(Runtime.ActiveMainNodeId)) return false;
            return TryCreateRoot(spec, roundId, sourceId, out nodeId);
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
                if (TryPromoteCreated(out nodeId)) return true;
                if (TryStartReady(out nodeId)) return true;
                if (TryExecuteRunning(out nodeId)) return true;
                if (Runtime.Status != EffectRuntimeStatus.Active) return false;
                if (TryResumeBlocked(out nodeId)) return true;

                if (HasNonTerminalNodes() && !HasOpenInteraction())
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
                Runtime.Status == EffectRuntimeStatus.Active && HasOpenInteraction(),
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
            diagnostic = string.Empty;
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
                if (IsDeferredResourceCollectionCompletion(request, requestOwner))
                {
                    diagnostic = "资源收集尚未完成，不能提前结束采集阶段。";
                    return false;
                }

                if (expectedRevision < 0 || Runtime.StateRevision != expectedRevision ||
                    request.StateRevision != expectedRevision)
                {
                    diagnostic = "交互已过期，请刷新当前交互。";
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
                if (workingRequest.InteractionTypeId == "action.decline_effect")
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
                if (node != null && node.Status == EffectNodeStatus.Created && IsEligibleCreated(node))
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
                if (node != null && node.Status == EffectNodeStatus.Ready)
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
                if (node != null && node.Status == EffectNodeStatus.Running)
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
            if (!string.IsNullOrEmpty(source.DefinitionVersion) && source.DefinitionVersion != registration.DefinitionVersion)
                throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "恢复的 Effect 执行版本不匹配：" + source.EffectTypeId);
            GameState work = GameStateCloneService.DeepClone(state);
            EffectNodeRuntimeState node = work.EffectRuntime.EffectNodes.Find(candidate => candidate.EffectId == nodeId);

            if (node.AllowDecline && string.IsNullOrEmpty(node.FlowStage))
            {
                var gate = new EffectStepResult();
                gate.AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = "action.decline_effect",
                    AnsweringPlayerId = node.DecisionPlayerId,
                    PromptKey = string.IsNullOrEmpty(node.DeclinePromptKey)
                        ? "effect.decline"
                        : node.DeclinePromptKey,
                    AllowDecline = true,
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
            try
            {
                result = registration.Executor(new EffectExecutionContext(work, node, registry));
            }
            catch (KernelException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new KernelException(EffectFaultCodes.HandlerException, exception.Message, exception);
            }

            if (result == null)
            {
                throw new KernelException(EffectFaultCodes.InvalidHandlerResult, "Effect 处理器返回 null。");
            }

            if (result.ExplicitNoProgress)
            {
                throw new KernelException(EffectFaultCodes.NoProgress, "Effect 处理器没有产生进展：" + result.FailureReason);
            }

            var entries = new List<RuleJournalEntry>();
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

            if (result.ChildEffects.Count > 0)
            {
                AppendChildEffects(
                    work,
                    node,
                    result.ChildEffects,
                    "child",
                    "child_effect",
                    entries);
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
                    ResponseKind = RuleEventResponseKind.Effects,
                    DefinitionVersion = node.DefinitionVersion,
                    SemanticKey = "completion",
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

            node.Status = node.PendingOutcome == EffectPendingOutcome.Completed
                ? EffectNodeStatus.Completed
                : EffectNodeStatus.Failed;
            if (!string.IsNullOrEmpty(node.ContinuationBindingId))
            {
                ContinuationBinding binding = work.EffectRuntime.ContinuationBindings.Find(candidate =>
                    candidate != null && candidate.BindingId == node.ContinuationBindingId);
                if (binding != null) binding.Status = ContinuationBindingStatus.Completed;
            }
            OnNodeTerminalOnWorking(work, work.EffectRuntime, node);
        }

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
                if (specs.Count > 0)
                {
                    attached.AddRange(AppendChildEffects(
                        work,
                        owner,
                        specs,
                        "event",
                        handler.SubscriptionId,
                        entries));
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
            List<RuleJournalEntry> entries)
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
                runtime.EffectNodes.Add(child);
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

            for (int i = 0; i < owner.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = runtime.EffectNodes.Find(candidate => candidate.EffectId == owner.ChildEffectIds[i]);
                if (child != null && child.Status == EffectNodeStatus.Created)
                {
                    bool previousTerminal = true;
                    for (int j = 0; j < i; j++)
                    {
                        EffectNodeRuntimeState previous = runtime.EffectNodes.Find(candidate => candidate.EffectId == owner.ChildEffectIds[j]);
                        if (previous == null || !IsNormalTerminal(previous.Status))
                        {
                            previousTerminal = false;
                            break;
                        }
                    }

                    if (previousTerminal)
                    {
                        child.Status = EffectNodeStatus.Ready;
                        break;
                    }
                }
            }

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
                InteractionId = requestId,
                RequestId = requestId,
                SourceNodeId = string.IsNullOrEmpty(spec.SourceNodeId) ? owner.EffectId : spec.SourceNodeId,
                InteractionTypeId = spec.InteractionTypeId,
                OwnerEffectId = owner.EffectId,
                AnsweringPlayerId = spec.AnsweringPlayerId,
                Visibility = spec.Visibility ?? "public",
                PromptKey = spec.PromptKey ?? string.Empty,
                PromptParameters = spec.PromptParameters.Clone(),
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
            string blockerId = StableIdFactory.Create("blocker", owner.EffectId, requestId, "interaction");
            AddBlockerOnWorking(
                work,
                owner.EffectId,
                string.Empty,
                requestId,
                "interaction",
                "等待玩家交互答案",
                blockerId);
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
        {
            diagnostic = string.Empty;
            if (request == null || answer == null)
            {
                diagnostic = "交互答案为空。";
                return false;
            }

            if (request.AllowDecline && answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                return true;
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
                    CandidateResolutionRecord resolution = Runtime.CandidateResolutions.Find(candidate =>
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
            EffectNodeRuntimeState node)
        {
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
        }

        private static void ActivateNextChild(EffectRuntimeState runtime, EffectNodeRuntimeState owner)
        {
            if (owner.ChildEffectIds == null) return;
            for (int i = 0; i < owner.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = runtime.EffectNodes.Find(candidate => candidate.EffectId == owner.ChildEffectIds[i]);
                if (child == null) continue;
                if (IsNormalTerminal(child.Status)) continue;
                if (child.Status != EffectNodeStatus.Created) return;
                for (int j = 0; j < i; j++)
                {
                    EffectNodeRuntimeState previous = runtime.EffectNodes.Find(candidate => candidate.EffectId == owner.ChildEffectIds[j]);
                    if (previous == null || !IsNormalTerminal(previous.Status)) return;
                }

                child.Status = EffectNodeStatus.Ready;
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

        private bool IsEligibleCreated(EffectNodeRuntimeState node)
        {
            if (string.IsNullOrEmpty(node.ParentEffectId)) return true;
            EffectNodeRuntimeState parent = GetNode(node.ParentEffectId);
            if (parent == null || IsTerminal(parent.Status)) return false;
            int index = parent.ChildEffectIds.IndexOf(node.EffectId);
            if (index < 0) return false;
            for (int i = 0; i < index; i++)
            {
                EffectNodeRuntimeState previous = GetNode(parent.ChildEffectIds[i]);
                if (previous == null || !IsNormalTerminal(previous.Status)) return false;
            }

            return true;
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
            EffectNodeRuntimeState node = new EffectNodeRuntimeState
            {
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
            RuleCommit commit;
            try
            {
                commit = RuleCommit.Apply(work, commandId ?? string.Empty, commitEntries.ToArray());
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
            string validationReason;
            if (!work.EffectRuntime.TryValidate(out validationReason))
            {
                throw new KernelException(EffectFaultCodes.InvalidRuntimeState, validationReason);
            }

            for (int i = 0; i < commit.Entries.Count; i++)
            {
                RuleJournalEntry entry = commit.Entries[i];
                if (entry.Kind == RuleJournalEntryKind.EffectNode)
                {
                    EffectNodeRuntimeState createdNode = work.EffectRuntime.EffectNodes.Find(
                        candidate => candidate.EffectId == entry.EntityId);
                    if (createdNode != null && createdNode.LastCommitSequence == 0)
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
                        node.Status = EffectNodeStatus.Faulted;
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
