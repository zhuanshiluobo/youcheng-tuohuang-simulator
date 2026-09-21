using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NMC007InteractionCandidateEditModeTests
    {
        [Test]
        public void CandidatePipeline_AppliesStablePoliciesAndPreservesHardPool()
        {
            var policies = new CandidatePolicyRegistry();
            policies.RegisterSortPolicy(new CandidateSortPolicy("city", id => id == "A" ? "2" : id));
            policies.Register(new CandidatePolicyRegistration(
                "z-policy", "city", context => new List<CandidatePatch>
                {
                    new CandidatePatch
                    {
                        Operation = CandidatePatchOperation.Add,
                        CandidateIds = new List<string> { "B" },
                        ReasonCode = "ability_add"
                    }
                },
                priority: 10));
            policies.Register(new CandidatePolicyRegistration(
                "a-policy", "city", context => new List<CandidatePatch>
                {
                    new CandidatePatch
                    {
                        Operation = CandidatePatchOperation.Remove,
                        CandidateIds = new List<string> { "A" },
                        ReasonCode = "ability_remove"
                    }
                },
                priority: 0));

            var draft = new CandidateSetDraft
            {
                CandidateSetId = "candidate-city-1",
                Version = 4,
                CandidateKind = "city",
                CandidatePoolIds = new List<string> { "A", "B", "C" },
                DefaultCandidateIds = new List<string> { "A" },
                CurrentIds = new List<string> { "A" },
                Context = NormalizedValue.CreateObject(new List<NormalizedValueEntry>())
            };

            CandidateResolutionResult result = new CandidateSetResolver().Resolve(draft, policies, 7);

            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            Assert.That(result.FinalCandidateIds, Is.EqualTo(new[] { "B" }));
            Assert.That(result.Record.AppliedPatches, Has.Count.EqualTo(2));
            Assert.That(result.Record.AppliedPatches[0].SubscriptionId, Is.EqualTo("a-policy"));
            Assert.That(result.Record.StateRevision, Is.EqualTo(7));
        }

        [TestCase(CandidatePatchOperation.Add)]
        [TestCase(CandidatePatchOperation.Remove)]
        [TestCase(CandidatePatchOperation.Intersect)]
        public void CandidatePipeline_RepeatedOperationsAreDeterministic(CandidatePatchOperation operation)
        {
            var policies = new CandidatePolicyRegistry();
            policies.Register(new CandidatePolicyRegistration(
                "same-policy", "resource", context => new List<CandidatePatch>
                {
                    new CandidatePatch { Operation = operation, CandidateIds = new List<string> { "A" } },
                    new CandidatePatch { Operation = operation, CandidateIds = new List<string> { "A" } }
                }));
            var draft = new CandidateSetDraft
            {
                CandidateSetId = "candidate-resource-1",
                CandidateKind = "resource",
                CandidatePoolIds = new List<string> { "A", "B" },
                DefaultCandidateIds = new List<string> { "A", "B" },
                CurrentIds = new List<string> { "A", "B" },
                Context = NormalizedValue.CreateObject(new List<NormalizedValueEntry>())
            };

            CandidateResolutionResult result = new CandidateSetResolver().Resolve(draft, policies);

            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            Assert.That(result.Record.AppliedPatches, Has.Count.EqualTo(2));
            Assert.That(result.FinalCandidateIds, Is.Not.Null);
        }

        [Test]
        public void CandidatePipeline_RejectsPatchOutsideHardPoolAndPatchFault()
        {
            var outside = new CandidatePolicyRegistry();
            outside.Register(new CandidatePolicyRegistration(
                "outside", "city", context => new List<CandidatePatch>
                {
                    new CandidatePatch
                    {
                        Operation = CandidatePatchOperation.Add,
                        CandidateIds = new List<string> { "NOT_IN_POOL" }
                    }
                }));
            var draft = NewDraft("candidate-fault", "city", "A");

            CandidateResolutionResult invalidTarget = new CandidateSetResolver().Resolve(draft, outside);
            Assert.That(invalidTarget.Succeeded, Is.False);
            Assert.That(invalidTarget.FaultCode, Is.EqualTo(CandidateSetResolver.InvalidTarget));

            var throwing = new CandidatePolicyRegistry();
            throwing.Register(new CandidatePolicyRegistration(
                "throwing", "city", context => throw new InvalidOperationException("boom")));
            CandidateResolutionResult patchFault = new CandidateSetResolver().Resolve(draft, throwing);
            Assert.That(patchFault.Succeeded, Is.False);
            Assert.That(patchFault.FaultCode, Is.EqualTo(CandidateSetResolver.PatchFault));
        }

        [Test]
        public void CandidatePipeline_PersistsResolutionInRuleCommitAndClone()
        {
            var registry = new CandidatePolicyRegistry();
            var draft = NewDraft("candidate-persisted", "city", "A");
            GameState state = NewState("candidate-persisted-state");
            var resolver = new CandidateSetResolver();
            CandidateResolutionRecord record;
            string faultCode;
            string diagnostic;

            Assert.That(resolver.TryResolveAndCommit(
                state,
                draft,
                registry,
                out record,
                out faultCode,
                out diagnostic), Is.True, diagnostic);
            Assert.That(state.EffectRuntime.CandidateResolutions, Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(1));
            Assert.That(state.HasPendingChoice(), Is.False);

            GameState clone = GameStateCloneService.DeepClone(state);
            Assert.That(clone.EffectRuntime.CandidateResolutions[0].FinalCandidateIds,
                Is.EqualTo(new[] { "A" }));
            Assert.That(clone.EffectRuntime.TryValidate(out diagnostic), Is.True, diagnostic);
        }

        [Test]
        public void InteractionRequest_IsBlockedThenResumesOnlyForAuthorizedCurrentRevisionAnswer()
        {
            var registry = new EffectRegistry();
            registry.Register("test.interaction", context =>
            {
                NormalizedValue answer = context.GetLatestInteractionAnswer();
                if (answer != null) return EffectStepResult.Completed(answer);
                var result = new EffectStepResult();
                var interaction = new EffectInteractionSpec
                {
                    InteractionTypeId = "interaction.choose_target",
                    AnsweringPlayerId = 1,
                    CandidateSetId = "candidate-target-1",
                    CandidateSetVersion = 2,
                    MinSelections = 1,
                    MaxSelections = 1,
                    AnswerSchema = "candidate_id"
                };
                interaction.CandidateIds.Add("A");
                interaction.CandidateIds.Add("B");
                result.AddInteraction(interaction);
                return result;
            });

            GameState state = NewState("interaction-revision");
            var executor = new EffectTreeExecutor(state, registry);
            string rootId = executor.CreateRoot(EffectSpec.Create("test.interaction", playerId: 1));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            InteractionRequest request = state.EffectRuntime.InteractionRequests.Find(item => item.Status == "open");
            Assert.That(request, Is.Not.Null);
            int revision = request.StateRevision;
            int before = state.EffectRuntime.StateRevision;

            string diagnostic;
            Assert.That(executor.TrySubmitInteraction(
                request.InteractionId,
                2,
                revision,
                NormalizedValue.CreateStableReference("candidate", "A"),
                out diagnostic), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(before));

            Assert.That(executor.TrySubmitInteraction(
                request.InteractionId,
                1,
                revision - 1,
                NormalizedValue.CreateStableReference("candidate", "A"),
                out diagnostic), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(before));

            Assert.That(executor.TrySubmitInteraction(
                request.InteractionId,
                1,
                revision,
                NormalizedValue.CreateStableReference("candidate", "A"),
                out diagnostic), Is.True, diagnostic);
            executor.RunUntilQuiescent();
            Assert.That(executor.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests[0].Status, Is.EqualTo("answered"));
        }

        [Test]
        public void AnswerInteractionCommand_UsesOnlyStableIdsAndExpectedRevision()
        {
            var registry = new EffectRegistry();
            registry.Register("test.command.interaction", context =>
            {
                if (context.GetLatestInteractionAnswer() != null) return EffectStepResult.Completed();
                var result = new EffectStepResult();
                var interaction = new EffectInteractionSpec
                {
                    InteractionTypeId = "interaction.command",
                    AnsweringPlayerId = 1,
                    MinSelections = 1,
                    MaxSelections = 1,
                    AnswerSchema = "candidate_id"
                };
                interaction.CandidateIds.Add("option-a");
                result.AddInteraction(interaction);
                return result;
            });
            GameState state = NewState("interaction-command");
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("test.command.interaction", playerId: 1));
            executor.RunUntilQuiescent();
            InteractionRequest request = state.EffectRuntime.InteractionRequests[0];

            var command = new GameCommand
            {
                Kind = GameCommandKind.AnswerInteraction,
                PlayerId = 1,
                SourceId = request.InteractionId,
                OptionIds = new List<string> { "option-a" }
            };
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] =
                request.StateRevision.ToString();
            CommandResult result = new AnswerInteractionCommandHandler(registry).Handle(state, command);

            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.EffectRuntime.InteractionRequests[0].Status, Is.EqualTo("answered"));
        }

        [Test]
        public void InteractionProjection_HidesPrivateCandidatesAndAnswerFromOtherPlayers()
        {
            var request = new InteractionRequest
            {
                InteractionId = "private-1",
                RequestId = "private-1",
                InteractionTypeId = "interaction.private",
                OwnerEffectId = "effect-1",
                AnsweringPlayerId = 1,
                Visibility = "owner",
                PromptKey = "private.prompt",
                CandidateIds = new List<string> { "hidden-a" },
                MinSelections = 1,
                MaxSelections = 1,
                NormalizedAnswer = NormalizedValue.CreateStableReference("candidate", "hidden-a")
            };

            InteractionRequestProjection other = InteractionRequestProjector.ProjectForPlayer(request, 2);
            InteractionRequestProjection owner = InteractionRequestProjector.ProjectForPlayer(request, 1);

            Assert.That(other.VisibleToViewer, Is.False);
            Assert.That(other.CandidateIds, Is.Empty);
            Assert.That(other.NormalizedAnswer, Is.Null);
            Assert.That(owner.VisibleToViewer, Is.True);
            Assert.That(owner.CandidateIds, Is.EqualTo(new[] { "hidden-a" }));
            Assert.That(owner.NormalizedAnswer, Is.Not.Null);
        }

        private static CandidateSetDraft NewDraft(string id, string kind, string candidate)
        {
            return new CandidateSetDraft
            {
                CandidateSetId = id,
                CandidateKind = kind,
                CandidatePoolIds = new List<string> { candidate },
                DefaultCandidateIds = new List<string> { candidate },
                CurrentIds = new List<string> { candidate },
                Context = NormalizedValue.CreateObject(new List<NormalizedValueEntry>())
            };
        }

        private static GameState NewState(string gameId)
        {
            return new GameState
            {
                GameId = gameId,
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1 },
                    new PlayerState { PlayerId = 2 }
                }
            };
        }
    }
}
