using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class EffectExecutionBinding
    {
        public const string ValidationVersion = "facility_execution_history.v1.1.0";
        public int ProtocolVersion = 1, PlayerId, AuthorizedAtRevision;
        public string ParentBuildId = "", ChoiceInteractionId = "", AuthorizationRef = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;

        public static EffectExecutionBinding FromCommittedChoice(GameState state, EffectRegistry registry, NormalizedValue request,
            AbilitySourceRef source, AbilityVersionRef version)
        {
            if (request == null || request.Kind == NormalizedValueKind.Null) return null;
            if (!IsRequestValid(request)) throw new InvalidOperationException("effect_execution_binding_request_invalid");
            return Create(state, registry, ItemTransferEffectExecutor.Text(request, "parentBuildId"), source,
                ItemTransferEffectExecutor.Text(request, "choiceInteractionId"), ItemTransferEffectExecutor.Number(request, "playerId"), version);
        }
        public static bool IsRequestValid(NormalizedValue request) => request != null &&
            !(request.Kind != NormalizedValueKind.Object || request.Properties.Count != 4 ||
                request.Properties.Any(p => !new[] { "protocolVersion", "parentBuildId", "choiceInteractionId", "playerId" }.Contains(p.Name)) ||
                ItemTransferEffectExecutor.Value(request, "protocolVersion")?.Kind != NormalizedValueKind.Integer ||
                ItemTransferEffectExecutor.Number(request, "protocolVersion") != 1 ||
                ItemTransferEffectExecutor.Value(request, "playerId")?.Kind != NormalizedValueKind.Integer ||
                ItemTransferEffectExecutor.Value(request, "parentBuildId")?.Kind != NormalizedValueKind.String ||
                ItemTransferEffectExecutor.Value(request, "choiceInteractionId")?.Kind != NormalizedValueKind.String ||
                ItemTransferEffectExecutor.Number(request, "playerId") < 1 || ItemTransferEffectExecutor.Text(request, "parentBuildId").Length == 0 ||
                ItemTransferEffectExecutor.Text(request, "choiceInteractionId").Length == 0);
        public static EffectExecutionBinding Create(GameState state, EffectRegistry registry, string buildId,
            AbilitySourceRef source, string choiceInteractionId, int targetPlayer, AbilityVersionRef version)
        {
            if (registry == null) throw new InvalidOperationException("effect_execution_registry_required");
            var binding = new EffectExecutionBinding { PlayerId = targetPlayer, ParentBuildId = buildId,
                ChoiceInteractionId = choiceInteractionId, AuthorizedAtRevision = state.EffectRuntime.StateRevision,
                SourceRef = GameStateCloneService.DeepClone(source), VersionRef = GameStateCloneService.DeepClone(version) };
            binding.AuthorizationRef = ComputeAuthorization(state, binding);
            Validate(state, registry, binding);
            return binding;
        }
        public static void Validate(GameState state, EffectRegistry registry, EffectExecutionBinding binding)
            => ValidateCore(state,registry,binding,false);
        public static void ValidateHistorical(GameState state, EffectRegistry registry, EffectExecutionBinding binding)
            => ValidateCore(state,registry,binding,true);
        private static void ValidateCore(GameState state, EffectRegistry registry, EffectExecutionBinding binding,bool historical)
        {
            if (binding == null || binding.ProtocolVersion != 1 || binding.PlayerId < 1 || state.FindPlayer(binding.PlayerId) == null ||
                binding.AuthorizedAtRevision < 0 || binding.AuthorizedAtRevision > state.EffectRuntime.StateRevision ||
                binding.SourceRef == null || binding.VersionRef == null || binding.VersionRef.ContentPackHash != state.ContentPackHash ||
                binding.VersionRef.ContractVersion != "QY-COMMON/1.0.1" || binding.VersionRef.ExecutorVersion != "1.0.0")
                throw new InvalidOperationException("effect_execution_binding_invalid");
            var build = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == binding.ParentBuildId);
            var declared = state.ContentDefinitions?.AbilitySources.SingleOrDefault(d => d.AbilityId == binding.SourceRef.AbilityId &&
                d.DefinitionId == binding.SourceRef.DefinitionId);
            bool sourceCurrent = registry != null ? registry.IsCurrentFacilityExecutionSource(state, binding.SourceRef) :
                declared != null && AbilitySourceService.IsCurrent(state, state.ContentDefinitions.AbilitySources, binding.SourceRef);
            bool versionMatches = registry != null ? registry.FacilitySourceVersionMatches(binding.SourceRef, binding.VersionRef) :
                declared != null && declared.AllowedOperations.Contains("execution.bind") &&
                binding.VersionRef.DefinitionVersion == declared.DeclarationVersion &&
                binding.VersionRef.DefinitionVersion == binding.SourceRef.DeclarationVersion && !string.IsNullOrEmpty(binding.VersionRef.HandlerHash);
            if (build?.EffectTypeId != FacilityEntryEffectTypeIds.Build || build.PlayerId != binding.SourceRef.AuthorizedPlayerId ||
                !historical && !sourceCurrent || !versionMatches)
                throw new InvalidOperationException("effect_execution_source_invalid");
            var facts = FacilityBuildJournal.Read(build);
            if (facts == null || !facts.ScoreCommitted || !facts.AssemblyCommitted ||
                facts.IsOverlay && state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == facts.ObligationEffectId)?.Status != EffectNodeStatus.Completed || facts.EntryRequired &&
                state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == facts.EntryEffectId)?.Status != EffectNodeStatus.Completed)
                throw new InvalidOperationException("effect_execution_build_not_ready");
            var choice = state.EffectRuntime.InteractionRequests.SingleOrDefault(i => i.GetStableInteractionId() == binding.ChoiceInteractionId);
            var choiceOwner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == choice?.OwnerEffectId);
            if (choice == null || choice.Status != "answered" || choice.AnsweredAtRevision < 1 ||
                choice.AnsweredAtRevision > binding.AuthorizedAtRevision || choice.AnsweringPlayerId != build.PlayerId ||
                choiceOwner?.PlayerId != build.PlayerId ||
                !Within(state, choice.OwnerEffectId, build.EffectId) || !ChoiceSourceMatches(state, registry, choice.OwnerEffectId, build.EffectId, binding.SourceRef, binding.VersionRef,historical && !sourceCurrent) ||
                Player(choice.NormalizedAnswer) != binding.PlayerId || !choice.CandidateIds.Any(id => Player(NormalizedValue.CreateString(id)) == binding.PlayerId))
                throw new InvalidOperationException("effect_execution_choice_invalid");
            if (binding.AuthorizationRef != ComputeAuthorization(state, binding)) throw new InvalidOperationException("effect_execution_authorization_changed");
        }
        public static void ApplyToSpec(GameState state, EffectRegistry registry, EffectSpec spec, EffectExecutionBinding binding,
            int? explicitExecutor = null, int? explicitRecipient = null, int? explicitAnswerer = null)
        {
            if (registry == null) throw new InvalidOperationException("effect_execution_registry_required");
            Validate(state, registry, binding);
            if (spec.EffectTypeId == FacilityEntryEffectTypeIds.Activate) throw new InvalidOperationException("facility_entry_execution_binding_forbidden");
            if (new[] { explicitExecutor, explicitRecipient, explicitAnswerer }.Any(p => p.HasValue && p.Value != binding.PlayerId))
                throw new InvalidOperationException("effect_execution_subject_mismatch");
            ValidateOperationArguments(spec.NormalizedArguments, binding.PlayerId);
            spec.PlayerId = binding.PlayerId; spec.DecisionPlayerId = binding.PlayerId;
            // 新字段在本轮冻结的EffectSpec/Runtime中尚未接线；整合补丁必须复制本元数据。
            spec.ExecutionBinding = GameStateCloneService.DeepClone(binding);
        }
        public static void ValidateNode(GameState state, EffectRegistry registry, EffectNodeRuntimeState node, EffectExecutionBinding binding)
        {
            if (binding == null)
            {
                if (node.ContinuationContext != null && node.ContinuationScopeRootEffectId == node.EffectId &&
                    state.ContentDefinitions.AbilitySources.Any(d => d.AbilityId == node.ContinuationAbilityId && d.AllowedOperations.Contains("execution.bind")))
                {
                    var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == node.ParentEffectId);
                    var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    bool inherited = false, independent = false;
                    while (parent != null && visited.Add(parent.EffectId))
                    {
                        inherited |= parent.ExecutionBinding?.PlayerId == node.PlayerId;
                        independent |= state.EffectRuntime.AbilitySourceUses.Any(r => r.OwnerEffectId == parent.EffectId && r.SourceRef?.AuthorizedPlayerId == node.PlayerId) ||
                            state.EffectRuntime.AbilityActionInvocations.Any(r => r.OwnerEffectId == parent.EffectId && r.SourceRef?.AuthorizedPlayerId == node.PlayerId) ||
                            CleanupSourceService.Invocations(state).Any(r => r.TargetEffectId == parent.EffectId && r.SourceRef?.AuthorizedPlayerId == node.PlayerId);
                        if (parent.EffectTypeId == FacilityEntryEffectTypeIds.Build)
                        {
                            if (parent.PlayerId != node.PlayerId && !inherited && !independent) throw new InvalidOperationException("effect_execution_binding_missing");
                            break;
                        }
                        parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == parent.ParentEffectId);
                    }
                }
                return;
            }
            if(CleanupSourceService.Terminal(node)) ValidateHistorical(state,registry,binding);
            else Validate(state, registry, binding);
            if (node.EffectTypeId == FacilityEntryEffectTypeIds.Activate || node.PlayerId != binding.PlayerId || node.DecisionPlayerId >= 0 && node.DecisionPlayerId != binding.PlayerId ||
                !Within(state, node.EffectId, binding.ParentBuildId)) throw new InvalidOperationException("effect_execution_node_invalid");
        }
        public static bool Within(GameState state, string nodeId, string ancestorId)
        {
            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(nodeId) && visited.Add(nodeId))
            {
                if (nodeId == ancestorId) return true;
                nodeId = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == nodeId)?.ParentEffectId;
            }
            return false;
        }
        public static void ValidateOperationArguments(NormalizedValue args, int playerId)
        {
            foreach (string field in new[] { "player", "executingPlayer", "recipient", "recipientPlayer", "decisionPlayer" })
            {
                var value = ItemTransferEffectExecutor.Value(args, field);
                if (value != null && Player(value) != playerId) throw new InvalidOperationException("effect_execution_subject_mismatch");
            }
        }
        private static bool ChoiceSourceMatches(GameState state, EffectRegistry registry, string nodeId, string buildId, AbilitySourceRef source, AbilityVersionRef version,bool requireHistoricalReceipt=false)
        {
            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(nodeId) && nodeId != buildId && visited.Add(nodeId))
            {
                var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == nodeId);
                if (node == null) return false;
                if (!requireHistoricalReceipt && node.ContinuationContentInstanceId == source.ItemId && node.ContinuationAbilityId == source.AbilityId &&
                    node.ContinuationDefinitionVersion == source.DeclarationVersion &&
                    node.ContinuationContentHash == version.HandlerHash) return true;
                // 归档创建时没有registry；仍可沿真实Invocation/SourceUse收据证明精确来源。
                var action = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == nodeId && i.SourceRef?.AbilityId == source.AbilityId);
                var use = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(i => i.OwnerEffectId == nodeId && i.SourceRef?.AbilityId == source.AbilityId);
                var cleanup = CleanupSourceService.Invocations(state).SingleOrDefault(i => i.TargetEffectId == nodeId && i.SourceRef?.AbilityId == source.AbilityId);
                var recorded = action?.SourceRef ?? use?.SourceRef ?? cleanup?.SourceRef;
                var recordedVersion = action?.VersionRef ?? use?.VersionRef ?? cleanup?.VersionRef;
                if (recorded != null && recordedVersion != null &&
                    Newtonsoft.Json.JsonConvert.SerializeObject(recorded) == Newtonsoft.Json.JsonConvert.SerializeObject(source) &&
                    recordedVersion.ContractVersion == version.ContractVersion && recordedVersion.ContentPackHash == version.ContentPackHash &&
                    recordedVersion.DefinitionVersion == version.DefinitionVersion && recordedVersion.HandlerHash == version.HandlerHash &&
                    recordedVersion.ExecutorVersion == (action!=null?AbilityActionExecutor.Version:cleanup!=null?CleanupSourceService.Version:"1.0.0")) return true;
                if (!requireHistoricalReceipt && registry != null && node.ContinuationAbilityId == source.AbilityId &&
                    registry.TryResolveAbilityExecutionSource(state, node.EffectId, source.AbilityId, out var actual, out _) &&
                    actual.SourceKey == source.SourceKey && actual.ItemId == source.ItemId &&
                    actual.AuthorizationRef == source.AuthorizationRef && actual.AuthorizedPlayerId == source.AuthorizedPlayerId) return true;
                nodeId = node.ParentEffectId;
            }
            return false;
        }
        private static string ComputeAuthorization(GameState state, EffectExecutionBinding b)
        {
            var choice = state.EffectRuntime.InteractionRequests.SingleOrDefault(i => i.GetStableInteractionId() == b.ChoiceInteractionId);
            return FacilityBuildQueryService.Hash(string.Join("|", b.ParentBuildId, b.ChoiceInteractionId,
                choice?.NormalizedAnswer?.ToDeterministicString() ?? "", choice?.AnsweredAtRevision ?? 0,
                b.PlayerId, b.SourceRef?.SourceKey ?? "", b.VersionRef?.ContentPackHash ?? "",
                b.VersionRef?.DefinitionVersion ?? "", b.VersionRef?.HandlerHash ?? "", b.AuthorizedAtRevision));
        }
        private static int Player(NormalizedValue value)
        {
            if (value?.Kind == NormalizedValueKind.Array && value.Items.Count == 1) value = value.Items[0];
            string key = value?.Kind == NormalizedValueKind.StableReference && value.ReferenceType == "player" ? value.ReferenceId :
                value?.Kind == NormalizedValueKind.String ? value.StringValue : "";
            key = key ?? "";
            if (key.StartsWith("p", StringComparison.Ordinal)) key = key.Substring(1);
            return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int player) ? player : -1;
        }
    }
}

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        public bool TryResolveAbilityExecutionSource(GameState state, string ownerId, string abilityId,
            out AbilitySourceRef source, out AbilityVersionRef version)
        {
            source = null; version = null;
            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(ownerId) && visited.Add(ownerId))
            {
                var node = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == ownerId);
                if (node == null) return false;
                var obligation=state.EffectRuntime.CleanupObligations.SingleOrDefault(r=>r.InvokeEffectId==ownerId && r.Status=="executing" && r.SourceRef?.AbilityId==abilityId);
                if(obligation!=null)
                {
                    if(HandlerHash(obligation.HandlerRef)!=obligation.HandlerHash || GetAbilityHandlerVersion(obligation.HandlerRef)!=obligation.HandlerVersion) return false;
                    source=GameStateCloneService.DeepClone(obligation.SourceRef);version=GameStateCloneService.DeepClone(obligation.OriginVersion);version.HandlerHash=obligation.HandlerHash;return true;
                }
                var action = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == ownerId && i.SourceRef?.AbilityId == abilityId);
                var use = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(i => i.OwnerEffectId == ownerId && i.SourceRef?.AbilityId == abilityId);
                var cleanup = CleanupSourceService.Invocations(state).SingleOrDefault(i => i.TargetEffectId == ownerId && i.SourceRef?.AbilityId == abilityId);
                var actual = action?.SourceRef ?? use?.SourceRef ?? cleanup?.SourceRef;
                if (actual != null)
                {
                    var declaration = abilityDeclarations.SingleOrDefault(d => d.AbilityId == abilityId && d.DefinitionId == actual.DefinitionId);
                    if (declaration == null) return false;
                    source = GameStateCloneService.DeepClone(actual);
                    version = new AbilityVersionRef { ContentPackHash = ContentPackHash,
                        DefinitionVersion = declaration.DeclarationVersion, HandlerHash = HandlerHash(declaration.HandlerRef) };
                    return true;
                }
                ownerId = node.ParentEffectId;
            }
            return false;
        }
        // 实际执行身份仅来自真实调用收据和该角色已冻结的处理器字节；设施来源身份解析保持原合同。
        public bool TryResolveAbilityExecutionHandler(GameState state,string ownerId,string abilityId,string handlerHash,
            out AbilitySourceRef source,out AbilityVersionRef version)
        {
            string invocationOwnerId = ownerId;
            source=null;version=null;var visited=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            while(!string.IsNullOrEmpty(ownerId) && visited.Add(ownerId))
            {
                var node=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==ownerId);if(node==null) return false;
                if (node.EffectTypeId == EntrepreneurFlow.EntryTypeId)
                    return TryResolveImmediateExecutionSource(state, node, invocationOwnerId, out source, out version) &&
                        source.AbilityId == abilityId && version.HandlerHash == handlerHash;
                var due=state.EffectRuntime.CleanupObligations.SingleOrDefault(r=>r.InvokeEffectId==ownerId && r.Status=="executing" && r.SourceRef?.AbilityId==abilityId);
                if(due!=null)
                {
                    if(due.HandlerHash!=handlerHash || HandlerHash(due.HandlerRef)!=handlerHash || GetAbilityHandlerVersion(due.HandlerRef)!=due.HandlerVersion) return false;
                    source=GameStateCloneService.DeepClone(due.SourceRef);version=GameStateCloneService.DeepClone(due.OriginVersion);version.HandlerHash=handlerHash;return true;
                }
                var action=state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(r=>r.OwnerEffectId==ownerId && r.SourceRef?.AbilityId==abilityId);
                var use=state.EffectRuntime.AbilitySourceUses.SingleOrDefault(r=>r.OwnerEffectId==ownerId && r.SourceRef?.AbilityId==abilityId);
                var cleanup=CleanupSourceService.Invocations(state).SingleOrDefault(r=>r.TargetEffectId==ownerId && r.SourceRef?.AbilityId==abilityId);
                if(action!=null || use!=null || cleanup!=null)
                {
                    var actual=action?.SourceRef ?? use?.SourceRef ?? cleanup.SourceRef;
                    var frozen=action?.VersionRef ?? use?.VersionRef ?? cleanup.VersionRef;
                    string handler=action!=null?ActionDeclaration(action.ActionId)?.BodyHandlerRef:use!=null?ItemTransferEffectExecutor.Text(node.NormalizedArguments,"handlerRef"):SourceDeclaration(actual)?.HandlerRef;
                    if(action!=null && !action.ActivationCommitted || frozen?.HandlerHash!=handlerHash || string.IsNullOrEmpty(handler) || HandlerHash(handler)!=handlerHash || frozen.ContentPackHash!=ContentPackHash) return false;
                    source=GameStateCloneService.DeepClone(actual);version=GameStateCloneService.DeepClone(frozen);return true;
                }
                ownerId=node.ParentEffectId;
            }
            return false;
        }
        private FacilityBuildQueryService facilityBuildQueries;
        public FacilityBuildQueryService FacilityBuildQueries => facilityBuildQueries ?? (facilityBuildQueries = new FacilityBuildQueryService(this));
        public bool FacilityLayerContractsInstalled { get; private set; }
        public void CompleteFacilityLayerRegistration()
        {
            if (!pureQueryContracts.ContainsKey(FacilityBuildQueryService.BuiltKind) || !pureQueryContracts.ContainsKey(FacilityBuildQueryService.SlotKind) ||
                !pureQueryContracts.ContainsKey(FacilityBuildQueryService.SourceKind)) throw new InvalidOperationException("facility_contracts_not_registered");
            FacilityLayerContractsInstalled = true;
        }
        public bool IsCurrentFacilityExecutionSource(GameState state, AbilitySourceRef source) =>
            AbilitySourceService.IsCurrent(state, abilityDeclarations, source);
        public bool FacilitySourceVersionMatches(AbilitySourceRef source, AbilityVersionRef version)
        {
            var declaration = SourceDeclaration(source);
            return declaration != null && declaration.AllowedOperations.Contains("execution.bind") && version.ContractVersion == "QY-COMMON/1.0.1" &&
                version.ExecutorVersion == "1.0.0" && version.ContentPackHash == ContentPackHash &&
                version.DefinitionVersion == declaration.DeclarationVersion && version.HandlerHash == HandlerHash(declaration.HandlerRef);
        }
    }
}

