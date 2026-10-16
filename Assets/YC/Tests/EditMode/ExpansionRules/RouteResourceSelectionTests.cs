using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class RouteResourceSelectionTests
    {
        private static GameMapDefinition Map(params MapRouteDefinition[] routes)
        {
            var ids = routes.SelectMany(r => r.CoveredLocationIds).Distinct(StringComparer.Ordinal).ToArray();
            return new GameMapDefinition { MapId = "route_selection.test_map", MinPlayers = 2, MaxPlayers = 4,
                Locations = ids.Select(id => new MapLocationDefinition { LocationId = id, InfluenceSlotCount = 2, CanDockCity = true }).ToList(),
                Routes = routes.ToList(), Regions = new List<MapRegionDefinition>() };
        }
        private static MapRouteDefinition R(string id, params string[] locations)
            => new MapRouteDefinition { RouteId = id, FromLocationId = locations[0], ToLocationId = locations[1], CoveredLocationIds = locations.ToList(), InfluenceSlotCount = 2, RegionId = "same_region" };
        private static GameState State(GameMapDefinition map, int gold = 20)
            => new GameState { MapId = map.MapId, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, Round = 2,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, CityLocationId = "S", InfluenceSupply = 6, Resources = new ResourceSet { GoldVoucher = gold } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet() }, new PlayerState { PlayerId = 3, Resources = new ResourceSet() } } };
        private static EffectNodeRuntimeState Owner(GameState state, string id = "selection.owner", int player = 1)
        {
            var owner = new EffectNodeRuntimeState { EffectId = id, PlayerId = player, EffectTypeId = RouteResourceSelectionEffectExecutor.TypeId };
            state.EffectRuntime.EffectNodes.Add(owner); return owner;
        }
        private static RouteSelectionSession Open(GameState state, GameMapDefinition map, EffectNodeRuntimeState owner, out RouteSelectionSessionAuthority authority, int minimum = 1)
        {
            authority = new RouteSelectionSessionAuthority(new MapQueryService(map));
            var options = new RouteSelectionOptions(RouteSelectionPurpose.Exploration, owner.PlayerId, "S", map.Locations.Select(l => l.LocationId).ToArray(), minimum, map.Locations.Count, true);
            Assert.That(authority.TryOpen(state, owner, options, out var session).IsValid, Is.True);
            Assert.That(authority.TryPrepareRequest(state, owner, session, session.SelectableLocationUpperBound, out _, out _).IsValid, Is.True);
            return session;
        }
        private static RouteSelectionDraft Draft(RouteSelectionSession session, string[] routes, params string[] targets)
            => new RouteSelectionDraft(session.SessionId, 0, routes, targets, routes.Select(r => new RouteRecipientChoice(r, "bank")).ToArray());
        private static void Seal(RouteSelectionSessionAuthority authority, GameState state, EffectNodeRuntimeState owner, RouteSelectionSession session, RouteSelectionCommitment commitment)
        {
            var result = (ValidationResult)typeof(RouteSelectionSessionAuthority).GetMethod("Seal", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(authority, new object[] { state, owner, session, commitment });
            Assert.That(result.IsValid, Is.True, result.Reason);
        }
        private static NormalizedValue Record(EffectNodeRuntimeState owner) => owner.ContinuationData.Properties.Find(p => p.Name == "routeSelection").Value;

        private static void Production(out GameState state, out EffectRegistry registry, out ExplorationService exploration)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var config = new GameRuleConfiguration { PlayerCount = 2, EnabledExpansionIds = new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, new List<PlayerSeat> { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 } }, config, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            state.GameId = "route_selection.production"; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.Round = 1;
            state.FindPlayer(1).Resources.GoldVoucher = 4; state.FindPlayer(1).RemainingMainActionsThisTurn = 1;
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-03");
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == location.ZoneId && z.TemplateId == "core.zone.city_dock");
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { state, city, dock.ZoneId });
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map); var decks = new EventDeckService(321); var tokens = new ResourceTokenService();
            registry = new EffectRegistry(); catalog.Register(registry); GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence); EventCardEffectExecutor.Register(registry, map, influence, decks, tokens);
            exploration = new ExplorationService(map, influence, decks, tokens); ExplorationEffectExecutor.Register(registry, map, exploration, decks, tokens);
        }
        private static InteractionRequest Selection(GameState state) => state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection");
        private static void Submit(GameState state, EffectRegistry registry, InteractionRequest request, IReadOnlyList<string> ids)
        {
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TrySubmitInteraction(request.InteractionId, request.AnsweringPlayerId, state.EffectRuntime.StateRevision,
                NormalizedValue.CreateArray(ids.Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList()), out var diagnostic), Is.True, diagnostic);
            var report = executor.RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.Status == EffectNodeStatus.Failed), Is.False,
                string.Join("；", state.EffectRuntime.EffectNodes.Select(n => n.EffectTypeId + ":" + n.FailureReason)));
        }
        private static IReadOnlyList<string> SelectionAnswer(InteractionRequest request, string target, params string[] routes)
        {
            string scope = request.PromptParameters.Properties.Find(p => p.Name == "scopeKey").Value.StringValue;
            var ids = new List<string> { RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit) };
            if (target.Length > 0) ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, target));
            ids.AddRange(routes.Select(r => RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, r, "bank")));
            return ids;
        }
        private static void FinishEvents(GameState state, EffectRegistry registry)
        {
            for (int i = 0; i < 80; i++)
            {
                var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
                if (request == null) return;
                Assert.That(request.InteractionTypeId, Is.Not.EqualTo("route_resource.selection"));
                Submit(state, registry, request, request.CandidateIds.Take(Math.Max(1, request.MinSelections)).ToArray());
            }
            Assert.Fail("事件链未在保护次数内完成。");
        }
        private static EffectSpec CompileLua(string expression)
        {
            string script = "return function(ctx) return { " + expression + " } end";
            var definition = new LuaScriptDefinition("route_selection.script", "route_selection.script", "route_selection.script", "1.0.0", script, LuaContentHasher.ComputeSha256(script));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("route_selection.event", "route_selection.script", "p1", 0, "1.0.0", 2, 0, 4));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1, SourceId = "route_selection.script" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }

        [Test]
        public void LuaChooseExplore_CompilesToCommonSelectionAndUsesTheSameFrozenRoutePlan()
        {
            Production(out var state, out var registry, out _);
            var executor = new EffectExecutor(state, registry);
            Assert.That(executor.TryCreateRoot(CompileLua("Effect.ChooseExplore({ player = ctx.playerId })"), out _), Is.True, executor.LastDiagnostic);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = Selection(state); Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2"));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
            FinishEvents(state, registry); Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void ExploreCommand_StartAndCancelOnlyCreateSelection_WithoutSpendingRevealingOrMovingItems()
        {
            Production(out var state, out var registry, out var exploration);
            string items = JsonConvert.SerializeObject(state.ItemZones); int green = state.Decks.EventDeckGreen.Count;
            var handler = new ExploreLocationCommandHandler(exploration, new RoundAdvanceService(), registry);
            var started = handler.Handle(state, new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, CommandId = "begin_exploration" });
            Assert.That(started.Succeeded, Is.True, started.Validation.Reason);
            var request = Selection(state); Assert.That(request.AnswerSchema, Is.EqualTo("candidate_ids"));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(items)); Assert.That(state.Decks.EventDeckGreen.Count, Is.EqualTo(green));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            string cancel = request.CandidateIds.Single(id => id.EndsWith(".cancel", StringComparison.Ordinal));
            Submit(state, registry, request, new[] { cancel });
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(items)); Assert.That(state.Decks.EventDeckGreen.Count, Is.EqualTo(green));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }

        [Test]
        public void ExploreCommand_OneFinalAnswerUsesRouteIdPlanAndCompletesEscrowAndMainActionOnce()
        {
            Production(out var state, out var registry, out var exploration);
            var handler = new ExploreLocationCommandHandler(exploration, new RoundAdvanceService(), registry);
            Assert.That(handler.Handle(state, new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1, CommandId = "begin_exploration" }).Succeeded, Is.True);
            var request = Selection(state); Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2"));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId);
            Assert.That(new RouteSelectionSessionAuthority().TryResolveFinalized(state, owner.EffectId, out _, out var commitment).IsValid, Is.True);
            Assert.That(commitment.AcceptedPreview.ChargeRouteIds, Is.EqualTo(new[] { "A1", "A2" }));
            FinishEvents(state, registry);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero); Assert.That(state.Map.ResourceTokens.Any(t => t.LocationId == "A-01"), Is.True);
            string completed = JsonConvert.SerializeObject(state); Assert.That(new EffectExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(completed));
        }

        [Test]
        public void GrantedExplore_UsesCommonSelectionAndEscrowWithoutConsumingMainAction()
        {
            Production(out var state, out var registry, out var exploration);
            var handler = new ExploreLocationCommandHandler(exploration, new RoundAdvanceService(), registry);
            Assert.That(handler.BeginGrantedExplore(state, new GameCommand { PlayerId = 1, CommandId = "granted_exploration" }, "A-01").Succeeded, Is.True);
            var request = Selection(state); Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2"));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            FinishEvents(state, registry); Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void Harvest_NormalAndExtraUseSameFinalRoutesAndPayBeforeReward()
        {
            foreach (bool extra in new[] { false, true })
            {
                var map = StaticMapDefinitions.CreateFourPlayerMap(); var state = State(map, 4); state.FindPlayer(1).CityLocationId = "A-03";
                state.FindPlayer(1).ResourceCollectionStartGoldVoucher = 4; if (!extra) state.Phase = GamePhase.ResourceCollection;
                state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, LocationId = "A-01", SlotId = InfluenceService.GetLocationSlotId("A-01", 0) });
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-01", ResourceType = ResourceType.GoldVoucher, Amount = 3 });
                var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry); var executor = new EffectExecutor(state, registry);
                Assert.That(executor.TryCreateRoot(extra ? CompileLua("Effect.Collect({ apiVersion = 1, player = ctx.playerId, scope = 'extra' })") : RouteResourceSelectionEffectExecutor.CreateHarvest(1, false), out _), Is.True, executor.LastDiagnostic);
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var request = Selection(state); Submit(state, registry, request, SelectionAnswer(request, "A-01", "A1", "A2"));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3)); Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.EqualTo(!extra));
                Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId).NormalizedResult.Properties.Single(p => p.Name == "paidRouteIds").Value.Items.Select(v => v.StringValue), Is.EqualTo(new[] { "A1", "A2" }));
            }
        }

        [Test]
        public void Authority_MalformedRecordAndIllegalConsumerFailWithoutMutation()
        {
            var map = Map(R("a", "S", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out var authority);
            string original = JsonConvert.SerializeObject(owner.ContinuationData);
            foreach (string key in new[] { "scopeKey", "upperBound", "schemaVersion", "candidateSetVersion" })
            {
                owner.ContinuationData = JsonConvert.DeserializeObject<NormalizedValue>(original); var field = Record(owner).Properties.Single(p => p.Name == key);
                field.Value = key == "candidateSetVersion" ? new NormalizedValue { Kind = NormalizedValueKind.Integer, IntegerValue = long.MaxValue } : NormalizedValue.CreateNull();
                string before = JsonConvert.SerializeObject(state); Assert.That(authority.TryRestore(state, owner, out _, out _).IsValid, Is.False);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            }
            owner.ContinuationData = JsonConvert.DeserializeObject<NormalizedValue>(original);
            Record(owner).Properties.Add(new NormalizedValueEntry { Name = "unknown", Value = NormalizedValue.CreateString("x") });
            Assert.That(authority.TryRestore(state, owner, out _, out _).IsValid, Is.False);
            owner.ContinuationData = JsonConvert.DeserializeObject<NormalizedValue>(original);
            new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, Draft(session, new[] { "a" }, "T"), session.SelectableLocationUpperBound, out var commitment); Seal(authority, state, owner, session, commitment);
            var consumer = Owner(state, "illegal_consumer"); consumer.ParentEffectId = owner.EffectId;
            string accepted = JsonConvert.SerializeObject(state); Assert.That(authority.TryClaimFinalized(state, consumer, owner.EffectId, commitment.CommitmentId, out _, out _).IsValid, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(accepted));
        }

        [Test]
        public void DraftConstructorAndWithSelection_CopyInputsAndKeepPublicReadonlyValues()
        {
            var routes = new List<string> { "a" }; var targets = new List<string> { "T" }; var choices = new List<RouteRecipientChoice> { new RouteRecipientChoice("a", "bank") };
            var draft = new RouteSelectionDraft("session", 0, routes, targets, choices);
            routes.Clear(); targets.Clear(); choices.Clear();
            Assert.That(draft.ConfirmedRouteIds, Is.EqualTo(new[] { "a" })); Assert.That(draft.SelectedLocationIds, Is.EqualTo(new[] { "T" }));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.ConfirmedRouteIds).Add("b"));
            var next = draft.WithSelection(new string[0], new string[0], new RouteRecipientChoice[0]);
            Assert.That(next.LocalDraftVersion, Is.EqualTo(1)); Assert.That(draft.LocalDraftVersion, Is.EqualTo(0));
        }

        [Test]
        public void Query_TargetRingKeepsBothPathsAndPrunesHangingCycle_WithoutChangingState()
        {
            var map = Map(R("a", "S", "A"), R("b", "A", "T"), R("c", "S", "B"), R("d", "B", "T"), R("x", "A", "X"), R("y", "X", "Y"), R("z", "Y", "A"));
            var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _);
            var draft = Draft(session, map.Routes.Select(r => r.RouteId).ToArray(), "T"); string before = JsonConvert.SerializeObject(state);
            var result = new RouteResourceSelectionService(new MapQueryService(map)).Query(state, session, draft, new[] { "T", "X" });
            Assert.That(result.Validation.IsValid, Is.True); Assert.That(result.Preview.ChargeRouteIds, Is.EqualTo(new[] { "a", "b", "c", "d" }));
            Assert.That(result.Preview.PrunedRouteIds, Is.EqualTo(new[] { "x", "y", "z" })); Assert.That(result.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(8));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void Query_FinalPreviewDoesNotReintroducePrunedFreeBranch()
        {
            var map = Map(R("main", "S", "T"), R("free", "S", "X")); var state = State(map);
            state.Map.RoadRouteIds.Add("free"); var owner = Owner(state); var session = Open(state, map, owner, out _);
            var result = new RouteResourceSelectionService(new MapQueryService(map)).Query(state, session, Draft(session, new[] { "main" }, "T"), new[] { "T", "X" });
            Assert.That(result.Preview.EffectiveRouteIds, Is.EqualTo(new[] { "main" })); Assert.That(result.Preview.PrunedFreeRouteIds, Is.EqualTo(new[] { "free" }));
            Assert.That(result.LocationOptions.Single(l => l.LocationId == "X").IsReachableInOriginalDraft, Is.True);
            Assert.That(result.LocationOptions.Single(l => l.LocationId == "X").IsReachableInPreview, Is.False);
        }

        [Test]
        public void Query_NoTargetsRetainsDraftAndShowsProvisionalCost_ExplicitEmptyCompletionCostsZero()
        {
            var map = Map(R("a", "S", "A"), R("b", "A", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _, 0);
            var service = new RouteResourceSelectionService(new MapQueryService(map));
            var draft = Draft(session, new[] { "a", "b" }); var result = service.Query(state, session, draft, session.SelectableLocationUpperBound);
            Assert.That(result.Preview, Is.Null); Assert.That(result.ProvisionalGoldVoucherAmount, Is.EqualTo(4)); Assert.That(draft.ConfirmedRouteIds, Has.Count.EqualTo(2));
            result = service.Query(state, session, draft.WithSelection(draft.ConfirmedRouteIds, draft.SelectedLocationIds, draft.RecipientChoices, "", true), session.SelectableLocationUpperBound);
            Assert.That(result.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(0)); Assert.That(result.Preview.PrunedRouteIds, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Query_HyperedgeKeepsPhysicalRouteOnceAndUsesCallerLocationOrder()
        {
            var map = Map(R("hyper", "S", "T", "X"), R("branch", "X", "Y")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _);
            var result = new RouteResourceSelectionService(new MapQueryService(map)).Query(state, session, Draft(session, new[] { "hyper", "branch" }, "T"), new[] { "X", "T" });
            Assert.That(result.LocationOptions.Select(l => l.LocationId), Is.EqualTo(new[] { "X", "T" })); Assert.That(result.Preview.ChargeRouteIds, Is.EqualTo(new[] { "hyper" }));
            Assert.That(result.Preview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(2)); Assert.That(result.Preview.ConnectivityWitness.Any(l => l.LocationId == "X"), Is.True);
        }

        [Test]
        public void Finalize_SameRevisionChangedFreeFactRejectsInsteadOfRepricing()
        {
            var map = Map(R("a", "S", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _);
            var draft = Draft(session, new[] { "a" }, "T"); var service = new RouteResourceSelectionService(new MapQueryService(map));
            Assert.That(service.TryFinalize(state, session, session.ActiveRequestBinding, draft, session.SelectableLocationUpperBound, out _).IsValid, Is.True);
            int revision = state.EffectRuntime.StateRevision; state.Map.RoadRouteIds.Add("a"); string before = JsonConvert.SerializeObject(state);
            var result = service.TryFinalize(state, session, session.ActiveRequestBinding, draft, session.SelectableLocationUpperBound, out var commitment);
            Assert.That(result.IsValid, Is.False); Assert.That(result.Reason, Is.EqualTo("route_selection_facts_changed")); Assert.That(commitment, Is.Null);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision)); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void Finalize_SameRevisionChangedRecipientOrTargetQualificationRejectsWithoutMutation()
        {
            foreach (bool recipientChanged in new[] { true, false })
            {
                var map = Map(R("a", "S", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _);
                if (recipientChanged) state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, RouteId = "a", SlotId = "a-slot" });
                var eligible = recipientChanged ? session.SelectableLocationUpperBound : Array.AsReadOnly(new[] { "S" });
                string before = JsonConvert.SerializeObject(state);
                var validation = new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, Draft(session, new[] { "a" }, "T"), eligible, out var commitment);
                Assert.That(validation.IsValid, Is.False); Assert.That(validation.Reason, Is.EqualTo("route_selection_facts_changed")); Assert.That(commitment, Is.Null);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            }
        }

        [Test]
        public void StartBudget_DoesNotIncreaseAfterRewardOrRepeatedOpen()
        {
            var map = Map(R("a", "S", "A"), R("b", "A", "T")); var state = State(map, 2); var owner = Owner(state); var session = Open(state, map, owner, out var authority);
            state.FindPlayer(1).Resources.GoldVoucher = 100;
            Assert.That(authority.TryOpen(state, owner, new RouteSelectionOptions(RouteSelectionPurpose.Exploration, 1, "S", session.SelectableLocationUpperBound, 1, 1), out var reopened).IsValid, Is.True);
            Assert.That(reopened.StartGoldVoucherAmount, Is.EqualTo(2));
            var result = new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, Draft(session, new[] { "a", "b" }, "T"), session.SelectableLocationUpperBound, out _);
            Assert.That(result.IsValid, Is.False); Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
        }

        [Test]
        public void Authority_SealResolveUsesFrozenPlanAndRebuildsWitnessWithDelimiterSafeRouteIds()
        {
            var map = Map(R("canal~one", "S", "A"), R("canal,two", "A", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out var authority);
            Assert.That(new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, Draft(session, new[] { "canal~one", "canal,two" }, "T"), session.SelectableLocationUpperBound, out var commitment).IsValid, Is.True);
            Seal(authority, state, owner, session, commitment); state.Map.RoadRouteIds.Add("canal~one"); state.FindPlayer(1).Resources.GoldVoucher = 0;
            Assert.That(authority.TryResolveFinalized(state, owner.EffectId, out _, out var restored).IsValid, Is.True);
            Assert.That(restored.AcceptedPreview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(4)); Assert.That(restored.AcceptedPreview.ConnectivityWitness.Select(l => l.LocationId), Does.Contain("T"));
            Assert.That(restored.AcceptedPreview.PlanDigest, Is.EqualTo(commitment.AcceptedPreview.PlanDigest)); Assert.That(Record(owner).IsValid(), Is.True);
        }

        [Test]
        public void Authority_ClaimIsIdempotentForOneConsumerAndRejectsAnother()
        {
            var map = Map(R("a", "S", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out var authority);
            new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, Draft(session, new[] { "a" }, "T"), session.SelectableLocationUpperBound, out var commitment);
            Seal(authority, state, owner, session, commitment);
            var first = Owner(state, "consumer.first"); first.ParentEffectId = owner.EffectId; first.EffectTypeId = ExplorationEffectTypeIds.Explore;
            var second = Owner(state, "consumer.second"); second.ParentEffectId = owner.EffectId; second.EffectTypeId = ExplorationEffectTypeIds.Explore;
            Assert.That(authority.TryClaimFinalized(state, first, owner.EffectId, commitment.CommitmentId, out _, out _).IsValid, Is.True);
            string after = JsonConvert.SerializeObject(state);
            Assert.That(authority.TryClaimFinalized(state, first, owner.EffectId, commitment.CommitmentId, out _, out _).IsValid, Is.True); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(after));
            Assert.That(authority.TryClaimFinalized(state, second, owner.EffectId, commitment.CommitmentId, out _, out _).IsValid, Is.False);
        }

        [Test]
        public void Authority_CurrentFormalMapMaximumPlanFitsNormalizedLimitsAndRoundTrips()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap(); var state = State(map, 100); state.FindPlayer(1).CityLocationId = "A-01";
            var owner = Owner(state); var authority = new RouteSelectionSessionAuthority(new MapQueryService(map)); var upper = map.Locations.Select(l => l.LocationId).ToArray();
            Assert.That(authority.TryOpen(state, owner, new RouteSelectionOptions(RouteSelectionPurpose.Exploration, 1, "A-01", upper, 1, upper.Length), out var session).IsValid, Is.True);
            authority.TryPrepareRequest(state, owner, session, upper, out _, out var candidates);
            var routes = map.Routes.Select(r => r.RouteId).ToArray(); var draft = Draft(session, routes, upper);
            Assert.That(new RouteResourceSelectionService(new MapQueryService(map)).TryFinalize(state, session, session.ActiveRequestBinding, draft, upper, out var commitment).IsValid, Is.True);
            Seal(authority, state, owner, session, commitment);
            Assert.That(owner.ContinuationData.IsValid(), Is.True); Assert.That(authority.TryResolveFinalized(state, owner.EffectId, out _, out var restored).IsValid, Is.True);
            Assert.That(restored.AcceptedPreview.ChargeRouteIds.Count, Is.EqualTo(22)); Assert.That(restored.AcceptedPreview.PaymentPlan.TotalGoldVoucherAmount, Is.EqualTo(44));
            Assert.That(candidates.Count, Is.EqualTo(45));
        }

        [Test]
        public void Authority_ClientViewReaderProvidesOwnerSessionForAllPurposesAndDeniesSpectator()
        {
            var map = Map(R("a", "S", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out var authority);
            var request = new InteractionRequest { InteractionId = "selection.request", OwnerEffectId = owner.EffectId, AnsweringPlayerId = 1, Visibility = "owner", Status = "open", InteractionTypeId = "route_resource.selection",
                CandidateSetId = session.ActiveRequestBinding.CandidateSetId, CandidateSetVersion = session.ActiveRequestBinding.CandidateSetVersion, PromptParameters = Record(owner).Clone() };
            state.EffectRuntime.InteractionRequests.Add(request);
            var view = GameStateViewFactory.ForPlayer(state, 1);
            Assert.That(authority.TryRead(view, view.Interactions.Single(), out var projected, out _).IsValid, Is.True); Assert.That(projected.StartGoldVoucherAmount, Is.EqualTo(20));
            var spectator = GameStateViewFactory.ForSpectator(state); Assert.That(spectator.Interactions, Is.Empty);
            Assert.That(authority.TryRead(spectator, view.Interactions.Single(), out _, out _).IsValid, Is.False);
        }

        [Test]
        public void Codec_AcceptsOneFinalTypedArrayAndRejectsMixedCancelCrossScopeAndTwoRecipients()
        {
            var scope = "scope"; var commit = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit); var cancel = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Cancel);
            var location = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, "T");
            var route = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, "a", "player:2"); var other = RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, "a", "player:3");
            var request = new InteractionRequest { InteractionTypeId = "route_resource.selection", AnswerSchema = "candidate_ids", CandidateIds = new List<string> { commit, cancel, location, route, other },
                PromptParameters = NormalizedValue.CreateObject(new List<NormalizedValueEntry> { new NormalizedValueEntry { Name = "sessionId", Value = NormalizedValue.CreateString("session") }, new NormalizedValueEntry { Name = "scopeKey", Value = NormalizedValue.CreateString(scope) } }) };
            var codec = new RouteSelectionAnswerCodec(); Assert.That(codec.TryParse(request, new[] { commit, location, route }, out var answer).IsValid, Is.True); Assert.That(answer.Draft.RecipientChoices[0].ReceiverSubjectKey, Is.EqualTo("player:2"));
            Assert.That(codec.TryParse(request, new[] { commit, cancel }, out _).IsValid, Is.False); Assert.That(codec.TryParse(request, new[] { commit, location, route, other }, out _).IsValid, Is.False);
            Assert.That(codec.TryParse(request, new[] { RouteSelectionAnswerCodec.Encode("other", RouteSelectionCandidateKind.Commit) }, out _).IsValid, Is.False);
            Assert.That(codec.TryParse(request, new[] { cancel }, out var cancelled).IsValid, Is.True); Assert.That(cancelled.IsCancellation, Is.True);
        }

        [Test]
        public void Query_MaterializesInfluenceAndRoadProjectionOncePerCall()
        {
            var map = Map(R("a", "S", "A"), R("b", "A", "T")); var state = State(map); var owner = Owner(state); var session = Open(state, map, owner, out _);
            int influenceReads = 0, roadReads = 0;
            state.Map.Influences.Bind(() => { influenceReads++; return new List<InfluencePlacement>(); }); state.Map.RoadRouteIds.Bind(() => { roadReads++; return new List<string>(); });
            new RouteResourceSelectionService(new MapQueryService(map)).Query(state, session, Draft(session, new[] { "a", "b" }, "T"), session.SelectableLocationUpperBound);
            Assert.That(influenceReads, Is.EqualTo(1)); Assert.That(roadReads, Is.EqualTo(1));
        }

        [Test]
        public void NormalHarvest_MultipleOwnersKeepStartBudgetsAndSubmitOnceInEitherOrder()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap(); var state = State(map); state.Phase = GamePhase.ResourceCollection;
            var starts = new[] { "A-01", "B-01", "C-01" };
            for (int i = 0; i < state.Players.Count; i++) { state.Players[i].CityLocationId = starts[i]; state.Players[i].Resources.GoldVoucher = 20; state.Players[i].ResourceCollectionStartGoldVoucher = 20; }
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "B-01", ResourceType = ResourceType.Iron, Amount = 2 });
            var registry = new EffectRegistry(); RouteResourceSelectionEffectExecutor.Register(registry);
            var executor = new EffectExecutor(state, registry);
            for (int i = 1; i <= 3; i++) Assert.That(executor.TryCreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(i, false), out _), Is.True, executor.LastDiagnostic);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            var round = new RoundExecutionService(); var advanced = round.Advance(state); Assert.That(advanced.IsValid, Is.True, advanced.Reason);
            foreach (int player in new[] { 2, 1, 3 })
            {
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.AnsweringPlayerId == player);
                string scope = request.PromptParameters.Properties.Find(p => p.Name == "scopeKey").Value.StringValue;
                var ids = new List<string> { RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit) };
                if (player == 2) ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, "B-01"));
                var answer = NormalizedValue.CreateArray(ids.Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList());
                Assert.That(executor.TrySubmitInteraction(request.InteractionId, player, state.EffectRuntime.StateRevision, answer, out var diagnostic), Is.True, diagnostic);
                var report = executor.RunUntilQuiescent();
                Assert.That(report.Faulted, Is.False, report.FaultCode);
                Assert.That(state.FindPlayer(player).HasCollectedResourcesThisRound, Is.True,
                    "玩家=" + player + "；" + string.Join("；", state.EffectRuntime.EffectNodes.Select(n => n.PlayerId + ":" + n.FlowStage + ":" + n.Status + ":" + n.FailureReason)));
                advanced = round.Advance(state); Assert.That(advanced.IsValid, Is.True, "玩家=" + player + "；" + advanced.Reason);
            }
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(2)); Assert.That(state.Players.All(p => p.Resources.GoldVoucher == 20), Is.True);
        }
    }
}
