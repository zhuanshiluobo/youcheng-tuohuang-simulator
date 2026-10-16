using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Tests.EditMode
{
    public sealed class RouteSelectionPlanValidationTests
    {
        private sealed class Fixture
        {
            public GameState Host;
            public GameState Member;
            public RouteSelectionSession HostSession;
            public RouteSelectionRequestBinding HostBinding;
            public RouteSelectionSession MemberSession;
            public RouteSelectionRequestBinding MemberBinding;
            public RouteSelectionDraft Draft;
            public RouteResourceSelectionService Service;
            public IReadOnlyList<string> Eligible;
        }

        private static Fixture Create()
        {
            var map = new GameMapDefinition
            {
                MapId = "readonly-plan-test", MinPlayers = 2, MaxPlayers = 2,
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition { LocationId = "S", InfluenceSlotCount = 2, CanDockCity = true },
                    new MapLocationDefinition { LocationId = "T", InfluenceSlotCount = 2, CanDockCity = true }
                },
                Routes = new List<MapRouteDefinition>
                {
                    new MapRouteDefinition { RouteId = "R", FromLocationId = "S", ToLocationId = "T",
                        CoveredLocationIds = new List<string> { "S", "T" }, InfluenceSlotCount = 2, RegionId = "region" }
                }
            };
            var state = new GameState
            {
                GameId = "readonly-plan-game", MapId = map.MapId, Round = 1,
                Phase = GamePhase.ResourceCollection, CurrentPlayerId = -1,
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, CityLocationId = "S", InfluenceSupply = 6,
                        Resources = new ResourceSet { GoldVoucher = 20 }, ResourceCollectionStartGoldVoucher = 20 },
                    new PlayerState { PlayerId = 2 }
                }
            };
            state.Map.Influences.Add(new InfluencePlacement
            { InfluenceId = "location-right", LocationId = "T", PlayerId = 1, OwnerSubject = RuleSubjectReference.ForPlayer(1) });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            { LocationId = "T", ResourceType = ResourceType.Originium, Amount = 1 });
            var query = new MapQueryService(map);
            var registry = new EffectRegistry();
            RouteResourceSelectionEffectExecutor.Register(registry, query);
            var executor = new EffectTreeExecutor(state, registry);
            string ownerId = executor.CreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(1, false, "plan-validation"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var owner = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == ownerId);
            var authority = new RouteSelectionSessionAuthority(query);
            Assert.That(authority.TryRestore(state, owner, out var hostSession, out var hostBinding).IsValid, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            var view = GameStateViewProjector.ProjectForPlayer(state, 1);
            var visibleRequest = view.Interactions.Single(value => value.InteractionId == request.InteractionId);
            Assert.That(authority.TryRead(view, visibleRequest, out var memberSession, out var memberBinding).IsValid, Is.True);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Interactions.Any(value =>
                value.InteractionId == request.InteractionId), Is.False, "他人不能取得本人的采集计划绑定");
            var member = GameStateViewProjector.ToClientState(view);
            Assert.That(member.EffectRuntime, Is.Null, "成员运行态必须来自正式ToClientState，不补造Host runtime");
            return new Fixture
            {
                Host = state, Member = member, HostSession = hostSession, HostBinding = hostBinding,
                MemberSession = memberSession, MemberBinding = memberBinding,
                Draft = new RouteSelectionDraft(hostSession.SessionId, 1, new[] { "R" }, new[] { "T" },
                    new[] { new RouteRecipientChoice("R", "bank") }),
                Service = new RouteResourceSelectionService(query), Eligible = hostSession.SelectableLocationUpperBound
            };
        }

        [Test]
        public void MemberProjectionValidatesWithoutRuntimeAndHostFinalizationUsesRealRevision()
        {
            var fixture = Create();
            string hostBefore = JsonConvert.SerializeObject(fixture.Host);
            string memberBefore = JsonConvert.SerializeObject(fixture.Member);
            string bindingsBefore = JsonConvert.SerializeObject(new[] { fixture.HostSession, fixture.MemberSession });
            IRouteResourceSelectionService service = fixture.Service;
            var host = service.TryValidatePlan(fixture.Host, fixture.HostSession, fixture.HostBinding,
                fixture.Draft, fixture.Eligible, out var hostPreview);
            var member = service.TryValidatePlan(fixture.Member, fixture.MemberSession, fixture.MemberBinding,
                fixture.Draft, fixture.Eligible, out var memberPreview);
            Assert.That(host.IsValid, Is.True, host.Reason);
            Assert.That(member.IsValid, Is.True, member.Reason);
            Assert.That(memberPreview.PlanDigest, Is.EqualTo(hostPreview.PlanDigest));
            Assert.That(memberPreview.PaymentPlan.TotalGoldVoucherAmount > 0, Is.True);
            Assert.That(memberPreview.ChargeRouteIds, Is.EqualTo(new[] { "R" }));
            Assert.That(memberPreview.PaymentPlan.Lines.Single().ReceiverSubjectKey, Is.EqualTo("bank"));
            var finalized = service.TryFinalize(fixture.Host, fixture.HostSession, fixture.HostBinding,
                fixture.Draft, fixture.Eligible, out var commitment);
            Assert.That(finalized.IsValid, Is.True, finalized.Reason);
            Assert.That(fixture.Host.EffectRuntime.StateRevision > 0, Is.True, "真实内核提交已生成Host版本");
            Assert.That(commitment.AcceptedAtStateRevision, Is.EqualTo(fixture.Host.EffectRuntime.StateRevision));
            Assert.That(commitment.AcceptedPreview.PlanDigest, Is.EqualTo(memberPreview.PlanDigest));
            var clientFinalization = service.TryFinalize(fixture.Member, fixture.MemberSession, fixture.MemberBinding,
                fixture.Draft, fixture.Eligible, out var clientCommitment);
            Assert.That(clientFinalization.IsValid, Is.False);
            Assert.That(clientFinalization.Reason, Is.EqualTo("route_selection_host_runtime_required"));
            Assert.That(clientCommitment, Is.Null);
            Assert.That(JsonConvert.SerializeObject(fixture.Host), Is.EqualTo(hostBefore));
            Assert.That(JsonConvert.SerializeObject(fixture.Member), Is.EqualTo(memberBefore));
            Assert.That(JsonConvert.SerializeObject(new[] { fixture.HostSession, fixture.MemberSession }), Is.EqualTo(bindingsBefore));
        }

        [TestCase("facts")]
        [TestCase("budget")]
        public void ChangedFactsOrBudgetRejectWithoutPreviewOrInputMutation(string change)
        {
            var fixture = Create();
            if (change == "facts") fixture.Member.Map.ResourceTokens[0].Amount++;
            else fixture.Member.FindPlayer(1).Resources.GoldVoucher = 0;
            string before = JsonConvert.SerializeObject(fixture.Member);
            var validation = fixture.Service.TryValidatePlan(fixture.Member, fixture.MemberSession, fixture.MemberBinding,
                fixture.Draft, fixture.Eligible, out var preview);
            Assert.That(validation.IsValid, Is.False);
            Assert.That(preview, Is.Null);
            if (change == "facts") Assert.That(validation.Reason, Is.EqualTo("route_selection_facts_changed"));
            else Assert.That(validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            var finalization = fixture.Service.TryFinalize(fixture.Member, fixture.MemberSession, fixture.MemberBinding,
                fixture.Draft, fixture.Eligible, out var commitment);
            Assert.That(finalization.ErrorCode, Is.EqualTo(validation.ErrorCode));
            Assert.That(finalization.Reason, Is.EqualTo(validation.Reason));
            Assert.That(commitment, Is.Null);
            Assert.That(JsonConvert.SerializeObject(fixture.Member), Is.EqualTo(before));
        }

        [Test]
        public void UnconfirmedEmptyDraftRejectsThroughTheOriginalTargetGuard()
        {
            var fixture = Create();
            var draft = new RouteSelectionDraft(fixture.MemberSession.SessionId, 0, Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<RouteRecipientChoice>());
            string before = JsonConvert.SerializeObject(fixture.Member);
            var validation = fixture.Service.TryValidatePlan(fixture.Member, fixture.MemberSession, fixture.MemberBinding,
                draft, fixture.Eligible, out var preview);
            Assert.That(validation.IsValid, Is.False);
            Assert.That(validation.Reason, Is.EqualTo("route_selection_target_required"));
            Assert.That(preview, Is.Null);
            Assert.That(JsonConvert.SerializeObject(fixture.Member), Is.EqualTo(before));
        }
    }
}
