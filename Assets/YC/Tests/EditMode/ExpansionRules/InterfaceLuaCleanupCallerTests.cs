using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>008费用调用端隔离合同；R2按005正式api3，不以设施批次证明token费用。</summary>
    public sealed class InterfaceLuaCleanupCallerTests : FacilityLayerTestFixture
    {
        private GameState state;
        private EffectTreeExecutor Engine => new EffectTreeExecutor(state, Registry);
        [TearDown] public void KeepCallerState()
        { if (state != null) artifacts.WriteLabeledText("008费用调用端最终状态", JsonConvert.SerializeObject(state)); state = null; }
        private string Supply => state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == 1).ZoneId;
        private string Retired => state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId;
        private List<string> Tokens(int count) => state.ItemZones.Items.Where(i => i.ZoneId == Supply && i.TypeId == "core.player_marker")
            .OrderBy(i => i.ItemId, StringComparer.Ordinal).Take(count).Select(i => i.ItemId).ToList();
        private EffectSpec Move(IReadOnlyList<string> ids, int required) => TokenSpec(1, ids, Supply, Retired, required, "core.player_marker_storage");
        private void Pump()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted) artifacts.WriteLabeledText("008费用调用端首次故障", JsonConvert.SerializeObject(state));
            Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private EffectNodeRuntimeState Node(string id) => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
        private void InstallMandatoryRewardWait(string type)
        {
            Registry.Register(type, c => c.GetLatestInteractionAnswer() == null ?
                EffectStepResult.Continue("wait").AddInteraction(new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = 1,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "gain" }, AllowDecline = false }) :
                EffectStepResult.Completed().AddChild(ResourceEffectSpecFactory.GainScore(1, 1)), EffectExecutorKind.IntrinsicFlow);
        }

        [Test] public void RuleConditionCanDeclineBeforeAnyTokenMoves()
        {
            state = State(); var ids = Tokens(3); int score = state.FindPlayer(1).Score;
            Engine.CreateRoot(EffectSpec.Condition(new[] { Move(ids, 3) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) },
                allowDecline: true, decisionPlayerId: 1, ruleCondition: true)); Pump();
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out string why), Is.True, why); Pump();
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == Supply), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == TypeId), Is.False);
        }

        [Test] public void RuleConditionPaidRightIsMandatoryAndRestoresWithoutAnotherTokenMove()
        {
            state = State(); var ids = Tokens(3); int score = state.FindPlayer(1).Score; const string wait = "qy008.condition.reward.wait";
            InstallMandatoryRewardWait(wait);
            Engine.CreateRoot(EffectSpec.Condition(new[] { Move(ids, 3) }, new[] { EffectSpec.Create(wait, playerId: 1) },
                allowDecline: true, decisionPlayerId: 1, ruleCondition: true)); Pump();
            var gate = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(gate.GetStableInteractionId(), 1, gate.StateRevision, NormalizedValue.CreateString("continue"), out string why), Is.True, why); Pump();
            var transfer = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == TypeId);
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, transfer), Is.True);
            var reward = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(reward.GetStableInteractionId(), 1, reward.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            var restored = artifacts.Recover(state, "008条件左式3token已付右式必选", Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            reward = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(reward.GetStableInteractionId(), 1, reward.StateRevision, NormalizedValue.CreateString("gain"), out why), Is.True, why); Pump();
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == Retired), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == TypeId), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
        }

        [Test] public void RealTokenTransferInConditionLeftContinuesAndItsReceiptSurvivesRecovery()
        {
            state = State(); var ids = Tokens(2); int before = state.FindPlayer(1).Score;
            string root = Engine.CreateRoot(EffectSpec.Condition(new[] { Move(ids, 2) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            Pump(); var transfer = state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root && n.EffectTypeId == TypeId);
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == Retired), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(before + 1));
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, transfer), Is.True);
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, transfer), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty, "通用固定实例转移不新增付款选择。");
            var restored = artifacts.Recover(state, "008条件左式通用token提交后", Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State; Pump();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(before + 1));
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, Node(transfer.EffectId)), Is.True);
        }

        [Test] public void ShortTokenTransferIsAllZeroAndDoesNotRunConditionRight()
        {
            state = State(); var ids = Tokens(1); int before = state.FindPlayer(1).Score;
            string root = Engine.CreateRoot(EffectSpec.Condition(new[] { Move(ids, 2) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            Pump(); var transfer = state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root && n.EffectTypeId == TypeId);
            Assert.That(transfer.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(transfer.PositionChanges, Is.Empty);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == ids[0]).ZoneId, Is.EqualTo(Supply));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(before));
            Assert.That(Text(Node(root).NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"));
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, transfer), Is.False);
        }

        [Test] public void ATokenRewardOnConditionRightIsNotInventedAsPayment()
        {
            state = State(); var ids = Tokens(1);
            string root = Engine.CreateRoot(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, new[] { Move(ids, 1) }));
            Pump(); var transfer = state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root && n.EffectTypeId == TypeId);
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, transfer), Is.True);
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, transfer), Is.False);
            // 即使同一实际位置证据被改写为设施配置，也不能成为token付款证明。
            var bad = GameStateCloneService.DeepClone(transfer);
            bad.NormalizedArguments.Properties.Single(p => p.Name == "operationProfileId").Value = NormalizedValue.CreateString("core.facility_card_transfer");
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.False);
        }

        [Test] public void CompletedChoiceOrTamperedCountCannotReplaceActualTokenPositionEvidence()
        {
            state = State(); string root = Engine.CreateRoot(Move(Tokens(2), 2)); Pump();
            var real = Node(root); Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, real), Is.True);
            var bad = GameStateCloneService.DeepClone(real); bad.PositionChanges.Clear();
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.False);
            bad = GameStateCloneService.DeepClone(real); bad.NormalizedResult.Properties.Single(p => p.Name == "movedCount").Value = NormalizedValue.CreateInteger(1);
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.False);
            bad = GameStateCloneService.DeepClone(real); bad.PositionChanges[0].From.ZoneId = Supply + ".forged";
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.False);
            bad = GameStateCloneService.DeepClone(real); bad.EffectTypeId = LuaDomainEffectTypeIds.Choice;
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.False);
            bad.Payment = new ResourcePaymentRuntimeState { IsCommitted = true };
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, bad), Is.False);
            bad = GameStateCloneService.DeepClone(real); bad.Status = EffectNodeStatus.Faulted; bad.PendingOutcome = EffectPendingOutcome.Completed;
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, bad), Is.True, "提交后后继故障仍保留真实费用证据。");
        }

        [Test] public void RealFacilityMoveKeepsItsOwnContractAndIsNotTokenPayment()
        {
            state = State(); var card = state.ItemZones.Items.First(i => i.TypeId == "core.facility_card");
            var target = state.ItemZones.Zones.First(z => z.TemplateId == FacilityLayerQuery.SupplyTemplate && !state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId));
            string root = Engine.CreateRoot(EffectSpec.Condition(new[] { MoveSpec(1, new[] { card.ItemId }, card.ZoneId, target.ZoneId, "top", 1, "core.facility_card_transfer") },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            Pump(); var move = state.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root && n.EffectTypeId == TypeId);
            Assert.That(move.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId, Is.EqualTo(target.ZoneId));
            Assert.That(AbilityActionExecutor.IsTokenTransferOperation(move), Is.False);
            Assert.That(AbilityActionExecutor.IsCommittedCost(state, move), Is.False);
            Assert.That(Text(Node(root).NormalizedResult, "outcome"), Is.Not.EqualTo("condition_not_met"));
        }

        private AbilitySourceRef PrepareAction(bool installTransferPreview, bool waitForReward = false)
        {
            state = State();
            var transferRegistration = Registry.Get(TypeId);
            Registry = new EffectRegistry { ContentPackHash = state.ContentPackHash };
            // 复用真实转移执行器，仅隔离调用端预览装配；不让正式注册器的其它接线改变缺provider负例。
            Registry.Register(transferRegistration); ResourceEffectExecutor.Register(Registry); AbilityActionExecutor.Register(Registry);
            const string ability = "qy008.caller.cost", costHandler = ability + ".entry", bodyHandler = ability + ".body";
            var ids = Tokens(2); string from = Supply, to = Retired;
            const string waitType = "qy008.caller.reward.wait";
            if (waitForReward) InstallMandatoryRewardWait(waitType);
            Registry.RegisterAbilityHandler(bodyHandler, "qy008.caller.body.v1", c => waitForReward ? new[] { EffectSpec.Create(waitType, playerId: 1) } : new[] { ResourceEffectSpecFactory.GainScore(1, 1) });
            Registry.RegisterAbilityHandler(costHandler, "qy008.caller.entry.v1", c => new[] { Move(ids, 2) });
            Registry.FreezeAbilityCatalog(new[] { new AbilitySourceDeclaration { DefinitionId = Token, AbilityId = ability, Kind = "setup",
                HandlerRef = bodyHandler, DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", PresentationRef = Token,
                EffectiveZones = new List<string> { "core.zone.box_reserve" },
                Actions = new List<AbilityActionDeclaration> { new AbilityActionDeclaration { ActionId = ability, EntryKind = "quick",
                    EntryCostRef = costHandler, BodyHandlerRef = bodyHandler, PresentationRef = Token } } } });
            state.ContentDefinitions.AbilitySources = Registry.AbilityDeclarations.ToList();
            if (installTransferPreview)
                Registry.RegisterItemTransferEntryCostPreview((work, source, spec) => {
                    return new ItemTransferService().TryPlanTokens(work, source, spec.NormalizedArguments, Registry.AbilityDeclarations,
                        "qy008.entry.preview", out _, out string why) ? "" : why;
                });
            return Registry.QuerySources(state, 1).Single();
        }

        [Test] public void GenericMoveEntryCostHasAnExplicitProviderBoundaryInsteadOfUnsupportedCost()
        {
            var source = PrepareAction(false); var option = Registry.QueryActions(state, 1).Single();
            Assert.That(option.Enabled, Is.False);
            Assert.That(option.DisabledReason, Is.EqualTo("item_transfer_cost_preview_not_registered"));
            string reason = AbilityActionExecutor.PreviewCost(state, 1, new[] { Move(Tokens(2), 2) });
            Assert.That(reason, Is.EqualTo("entry_cost_source_required"));
            Assert.That(Registry.PreviewItemTransferEntryCost(state, source, Move(Tokens(2), 2)), Is.EqualTo("item_transfer_cost_preview_not_registered"));
        }

        [Test] public void RealActionEntryTokenCostIsPaidOnceAndIsRestoredFromItsEntrySubtree()
        {
            PrepareAction(true); var option = Registry.QueryActions(state, 1).Single(); Assert.That(option.Enabled, Is.True, option.DisabledReason);
            int before = state.FindPlayer(1).Score;
            string root = Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy008.real.entry", Object())); Pump();
            var invocation = state.EffectRuntime.AbilityActionInvocations.Single();
            Assert.That(invocation.PaymentCommitted, Is.True); Assert.That(invocation.ActivationCommitted, Is.True);
            Assert.That(invocation.Phase, Is.EqualTo("completed")); Assert.That(invocation.PaymentEffectIds.Count, Is.EqualTo(1));
            Assert.That(AbilityActionExecutor.HasCommittedEntryPayment(state, invocation), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(before + 1));
            var restored = artifacts.Recover(state, "008真实行动通用token费用提交后", Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State; Pump();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(before + 1)); Assert.That(Node(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var bad = GameStateCloneService.DeepClone(state); bad.EffectRuntime.AbilityActionInvocations.Single().PaymentCommitted = false;
            Assert.That(AbilityActionRecovery.TryValidateAbilityActions(bad, Registry, out string why), Is.False);
            Assert.That(why, Does.Contain("entry_payment_commit_mismatch"));
        }

        [Test] public void PaidMandatoryRewardSurvivesSourceExitAndReplayWithoutRefundOrAnotherTransfer()
        {
            var source = PrepareAction(true, waitForReward: true); var option = Registry.QueryActions(state, 1).Single();
            var ids = Tokens(2); int score = state.FindPlayer(1).Score;
            Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy008.paid.wait", Object())); Pump();
            var invocation = state.EffectRuntime.AbilityActionInvocations.Single(); Assert.That(invocation.PaymentCommitted, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            // 明确隔离的来源退出控制，保留真实实例及已付费用；不冒充正常对局。
            state.ItemZones.Items.Single(i => i.ItemId == source.ItemId).ZoneId = Retired;
            var restored = artifacts.Recover(state, "008通用token已付必选收益待答及来源退出", Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            Assert.That(Engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest,
                option.VersionRef.ContentPackHash, "qy008.paid.wait", Object(), out string original, out string why), Is.True, why);
            Assert.That(original, Is.EqualTo(invocation.OwnerEffectId)); Assert.That(state.EffectRuntime.AbilityActionInvocations.Count, Is.EqualTo(1));
            request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("gain"), out why), Is.True, why); Pump();
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == Retired), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Assert.That(state.EffectRuntime.AbilityActionInvocations.Single().Phase, Is.EqualTo("completed"));
            Assert.That(AbilityActionExecutor.HasCommittedEntryPayment(state, state.EffectRuntime.AbilityActionInvocations.Single()), Is.True);
        }

        [Test] public void RetiredPaymentNodeAndUnexecutedNestedPaymentAreDiagnosticallyRejected()
        {
            state = State();
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "qy008.retired", EffectTypeId = AbilityActionRecovery.RetiredMarkerPaymentTypeId });
            Assert.That(AbilityActionRecovery.TryValidateAbilityActions(state, Registry, out string reason), Is.False);
            Assert.That(reason, Does.Contain(AbilityActionRecovery.RetiredMarkerPaymentDiagnostic));
            state.EffectRuntime.EffectNodes.Clear();
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "qy008.nested", EffectTypeId = EffectTypeIds.Condition,
                NestedEffects = new List<EffectSpecRuntimeState> { new EffectSpecRuntimeState { EffectTypeId = AbilityActionRecovery.RetiredMarkerPaymentTypeId } } });
            Assert.That(AbilityActionRecovery.TryValidateAbilityActions(state, Registry, out reason), Is.False);
            Assert.That(reason, Does.Contain(AbilityActionRecovery.RetiredMarkerPaymentDiagnostic));
        }
    }
}
