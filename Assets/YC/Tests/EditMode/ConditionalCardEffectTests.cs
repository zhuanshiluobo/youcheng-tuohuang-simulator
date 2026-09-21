using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class ConditionalCardEffectTests
    {
        private static GameState State(string template = CharacterCardDatabase.Elysium)
        {
            var state = new GameState
            {
                GameId = "conditional-card-test", MapId = StaticMapDefinitions.CreateFourPlayerMap().MapId,
                Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, Round = 1, ActionRound = 1,
                Players = { new PlayerState { PlayerId = 1, CityLocationId = "A-01", InfluenceSupply = 30,
                    RemainingMainActionsThisTurn = 1, Resources = new ResourceSet { GoldVoucher = 30, OriginiumShard = 3, Originium = 3, Iron = 3 } },
                    new PlayerState { PlayerId = 2, CityLocationId = "G-01", InfluenceSupply = 30 } }
            };
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(1));
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = player.HandCardIds.Find(id => CharacterCardDatabase.Get(id).TemplateId == template);
            player.HandCardIds.Remove(player.CoveredCharacterCardId);
            foreach (var location in new[] { "A-01", "A-02", "B-01" })
                new ResourceTokenService().PlaceToken(state.Map, location, ResourceType.Iron, 2);
            return state;
        }

        private static EffectRegistry Registry(GameState state)
        {
            var map = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(map);
            var token = new ResourceTokenService();
            var decks = new EventDeckService(73);
            var cost = new TravelCostService(map);
            var movement = new CityMovementService(map, influence, cost, decks, token);
            var registry = new EffectRegistry();
            new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            CityMoveEffectExecutor.Register(registry, map, influence, movement, cost, token);
            EventCardEffectExecutor.Register(registry, map, influence, decks, token);
            CityStyleSpecialActionEffectExecutor.Register(registry, new SpecialActionOptionQueryService(
                map, influence, movement, new SpecialActionLifecycleService(), new MainActionBudgetService()), null, map, influence, movement);
            CharacterCardLuaCatalog.Register(registry);
            CityStyleLuaCatalog.Register(registry);
            EventCardLuaCatalog.Register(registry);
            return registry;
        }

        private static InfluencePlacement Place(GameState state, int player, string location, int slot = 0)
        {
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            new ResourceTokenService().PlaceToken(state.Map, location, ResourceType.Iron, 2);
            string id = InfluenceService.GetLocationSlotId(location, slot);
            Assert.That(service.Place(state, player, id).Succeeded, Is.True, id);
            InfluenceIdentity.Ensure(state);
            return service.FindInfluence(state, id);
        }

        private static InteractionRequest Open(GameState state, string type) =>
            state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.InteractionTypeId.StartsWith(type, StringComparison.Ordinal));

        private static void Answer(GameState state, EffectRegistry registry, InteractionRequest request, string id, bool decline = false)
        {
            Assert.That(request, Is.Not.Null);
            var result = new AnswerInteractionCommandHandler(registry).Handle(state,
                EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1,
                    id == null ? null : new[] { id }, decline));
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
        }

        private static string Activate(GameState state, EffectRegistry registry, string ability, string mode = CharacterEffectModes.Tactic)
        {
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1, TargetId = state.FindPlayer(1).CoveredCharacterCardId };
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = mode;
            var result = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? "" : result.Validation.Reason);
            return state.EffectRuntime.EffectNodes.Find(n => n.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId).EffectId;
        }

        [Test]
        public void Elysium_PaysBeforeOrdinaryMoveAndRecoveryDoesNotPayTwice()
        {
            var state = State();
            Place(state, 1, "A-02");
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.ElysiumTacticAbilityId);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            var target = Open(state, CityMoveEffectExecutor.TargetInteractionTypeId);
            Assert.That(target, Is.Not.Null);
            Assert.That(target.CandidateIds, Is.EqualTo(new[] { "A-02" }));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == ResourceEffectTypeIds.Pay && n.Status == EffectNodeStatus.Completed), Is.True);
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            Answer(state, registry, Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), "A-02");
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == CityMoveEventTypeIds.BeforeCityMove), Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == CityMoveEventTypeIds.CityMoveCompleted), Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CharacterAbilityEffectExecutor.AbilityEffectTypeId), Is.False);
        }

        [Test]
        public void LuaNestedConditions_CompileAndExecuteAllLevels()
        {
            const string source = @"return function(ctx) return { Effect.Condition({
                leftEffects = { Effect.PayResource({ payer = '1', resourceType = 'gold_voucher', amount = 1 }) },
                rightEffects = { Effect.Condition({
                    leftEffects = { Effect.PayResource({ payer = '1', resourceType = 'gold_voucher', amount = 2 }) },
                    rightEffects = { Effect.GainResource({ recipient = '1', resourceType = 'iron', amount = 4 }) }
                }) }
            }) } end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(
                new LuaScriptDefinition("test", "test", "test", "1", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("event", "test", "1", 0, "1", 3, 0, 0, new LuaFacadeSnapshot()));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,
                new LuaEffectCompilationContext { PlayerId = 1 }, out specs, out fault), Is.True, fault == null ? "" : fault.Diagnostic);
            var state = State();
            var executor = new EffectTreeExecutor(state, Registry(state));
            executor.CreateRoot(specs[0]);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(27));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
            Assert.That(state.EffectRuntime.EffectNodes.FindAll(n => n.EffectTypeId == EffectTypeIds.Condition && n.Status == EffectNodeStatus.Completed), Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ScopedPlacement_DoesNotExpandAnExplicitEmptyScope(bool hasTarget)
        {
            string source = "return function(ctx) return { Effect.PlaceInfluence({ executingPlayer = '1', influenceSource = 'test', ownerSubject = '1', candidateScope = {" +
                (hasTarget ? "'location:A-02:0'" : "") + "} }) } end";
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(
                new LuaScriptDefinition("scope", "scope", "scope", "1", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("event", "test", "1", 0, "1", 3, 0, 0, new LuaFacadeSnapshot()));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 },
                out specs, out fault), Is.True, fault == null ? "" : fault.Diagnostic);
            var state = State();
            var executor = new EffectTreeExecutor(state, Registry(state));
            string id = executor.CreateRoot(specs[0]);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            var request = Open(state, "effect.influence.place.target");
            if (hasTarget) Assert.That(request.CandidateIds, Is.EqualTo(new[] { "location:A-02:0" }));
            else
            {
                Assert.That(request, Is.Null);
                Assert.That(executor.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Failed));
            }
        }

        [Test]
        public void UnaffordableCondition_CreatesNoMoveOrRightHandInteraction()
        {
            var state = State();
            state.FindPlayer(1).Resources.OriginiumShard = 2;
            Place(state, 1, "A-02");
            Activate(state, Registry(state), CharacterAbilityCatalog.ElysiumTacticAbilityId);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CityMoveEffectTypeIds.Move), Is.False);
            Assert.That(Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), Is.Null);
            var condition = state.EffectRuntime.EffectNodes.Find(n => n.EffectTypeId == EffectTypeIds.Condition);
            Assert.That(condition.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(condition.NormalizedResult.ToDeterministicString(), Does.Contain("condition_not_met"));
        }

        [Test]
        public void Texas_PaysThenCommitsEachChoiceAndExcludesMovedInstance()
        {
            var state = State(CharacterCardDatabase.Texas);
            Place(state, 2, "A-02");
            Place(state, 1, "B-01");
            Place(state, 1, "B-02");
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.TexasTacticAbilityId);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(27));
            Answer(state, registry, Open(state, CharacterAbilityEffectExecutor.TargetInteractionTypeId), InfluenceService.GetLocationSlotId("A-02", 0));
            Assert.That(state.Map.Influences.Exists(i => i.PlayerId == 2), Is.False, "第一次移动选项出现时，移除已经完成");
            var first = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Assert.That(first, Is.Not.Null);
            string choice = first.CandidateIds[0];
            string[] parts = choice.Split('|');
            string movedId = state.Map.Influences.Find(i => i.SlotId == parts[1]).InfluenceId;
            Answer(state, registry, first, choice);
            Assert.That(state.Map.Influences.Find(i => i.InfluenceId == movedId).SlotId, Is.EqualTo(parts[2]));
            var second = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Assert.That(second, Is.Not.Null);
            Assert.That(second.CandidateIds.Exists(c => c.Split('|')[1] == parts[2]), Is.False, "同一实例不能被第二次移动");
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            second = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Answer(state, registry, second, second.CandidateIds[0]);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(27));
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void Liskarm_PaysBeforeTargetAndKeepsRemovalWhenSupplyEmpty()
        {
            var state = State(CharacterCardDatabase.Liskarm);
            var target = Place(state, 2, "A-02");
            state.FindPlayer(1).Resources.GoldVoucher = 3;
            state.FindPlayer(1).InfluenceSupply = 0;
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.LiskarmTacticAbilityId);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Answer(state, registry, Open(state, CharacterAbilityEffectExecutor.TargetInteractionTypeId), target.SlotId);
            Assert.That(state.Map.Influences.Exists(i => i.InfluenceId == target.InfluenceId), Is.False);
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void TinMan_DecliningFirstConditionStillOffersSecondAfterRecovery()
        {
            var state = State(CharacterCardDatabase.TinMan);
            state.FindPlayer(1).Resources.GoldVoucher = 15;
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.TinManStrategyAbilityId, CharacterEffectModes.Strategy);
            var first = Open(state, "action.decline_effect");
            Assert.That(first, Is.Not.Null);
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == ResourceEffectTypeIds.Pay), Is.False);
            Answer(state, registry, first, "decline");
            var second = Open(state, "action.decline_effect");
            Assert.That(second, Is.Not.Null);
            Assert.That(second.PromptKey, Is.EqualTo("character.tin_man.purchase.15"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(15));
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            Answer(state, registry, Open(state, "action.decline_effect"), "continue");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void CityMove_BeforeEventPrecedesTargetSelectionAndCandidateGeneration()
        {
            var state = State();
            Place(state, 1, "A-02");
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            string id = executor.CreateRoot(CityMoveEffectSpecFactory.Move(1, null, true, false));
            for (int i = 0; i < 30 && executor.GetNode(id).FlowStage != "before_city_move"; i++)
                Assert.That(executor.Advance(), Is.True, executor.LastDiagnostic);
            Assert.That(executor.GetNode(id).FlowStage, Is.EqualTo("before_city_move"));
            Assert.That(Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), Is.Null);
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == CityMoveEventTypeIds.BeforeCityMove), Has.Count.EqualTo(1));
            // 模拟 BeforeCityMove 响应改变合法目标；候选必须在响应之后构建。
            state.FindPlayer(2).CityLocationId = "A-02";
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            var request = Open(state, CityMoveEffectExecutor.TargetInteractionTypeId);
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds, Does.Not.Contain("A-02"));
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            Answer(state, registry, Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), request.CandidateIds[0]);
            Assert.That(state.EffectRuntime.RuleEvents.FindAll(e => e.EventType == CityMoveEventTypeIds.BeforeCityMove), Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TexasStrategy_GrantsGoldBeforeFacilityChoiceEvenWhenSupplyEmpty(bool hasFacility)
        {
            var state = State(CharacterCardDatabase.Texas);
            if (hasFacility) state.Decks.FacilitySupply.Add("facility-test");
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.TexasStrategyAbilityId, CharacterEffectModes.Strategy);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(42));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == ResourceEffectTypeIds.Gain && n.Status == EffectNodeStatus.Completed), Is.True);
            var request = Open(state, CharacterAbilityEffectExecutor.TargetInteractionTypeId);
            if (hasFacility)
            {
                Assert.That(request, Is.Not.Null);
                state = GameStateCloneService.DeepClone(state);
                registry = Registry(state);
                Answer(state, registry, Open(state, CharacterAbilityEffectExecutor.TargetInteractionTypeId), "facility-test");
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(42));
                Assert.That(state.Decks.FacilitySupply, Does.Contain("facility-test"));
            }
            else Assert.That(request, Is.Null);
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CannotStrategy_AllowsZeroSaleWithOrWithoutResources(bool hasResources)
        {
            var state = State(CharacterCardDatabase.Cannot);
            if (!hasResources) state.FindPlayer(1).Resources = new ResourceSet();
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            int ore = state.FindPlayer(1).Resources.Originium;
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.CannotStrategyAbilityId, CharacterEffectModes.Strategy);
            var request = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds, Does.Contain("sale|originium|0"));
            Answer(state, registry, request, "sale|originium|0");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(ore));
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Find(n => n.EffectTypeId == LuaDomainEffectTypeIds.ResourceSell).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TinManTactic_RecallsAllBeforeFirstRewardAndRestoresRemainingChoices(bool moveFirst)
        {
            var state = State(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            string active = player.CoveredCharacterCardId;
            var recalled = player.HandCardIds.GetRange(0, 2);
            foreach (string card in recalled) { player.HandCardIds.Remove(card); player.DiscardCardIds.Add(card); }
            Place(state, 1, "B-01");
            var registry = Registry(state);
            Activate(state, registry, CharacterAbilityCatalog.TinManTacticAbilityId);
            Assert.That(player.DiscardCardIds, Is.Empty);
            Assert.That(player.HandCardIds, Is.SupersetOf(recalled));
            Assert.That(player.HandCardIds, Does.Not.Contain(active));
            var first = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
            Assert.That(first, Is.Not.Null);
            string answer = moveFirst ? "move-influence" : CharacterEffectChoiceIds.GainGold;
            Assert.That(answer, Is.Not.Null);
            Answer(state, registry, first, answer);
            if (moveFirst)
            {
                var move = Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId);
                answer = move.CandidateIds[0];
                Answer(state, registry, move, answer);
                Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(answer.Split('|')[2]));
            }
            else Assert.That(player.Resources.GoldVoucher, Is.EqualTo(35));
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            Answer(state, registry, Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId), CharacterEffectChoiceIds.GainGold);
            player = state.FindPlayer(1);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(moveFirst ? 35 : 40));
            Assert.That(player.DiscardCardIds, Is.EqualTo(new[] { active }));
            Assert.That(player.HandCardIds, Is.SupersetOf(recalled));
            Assert.That(Open(state, CharacterAbilityEffectExecutor.ChoiceInteractionTypeId), Is.Null);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Mobilization_ReplacesOpponentAndKeepsRemovalWhenPlacementUnavailable(bool emptySupply, bool opponentCity)
        {
            var state = State();
            var target = Place(state, 2, "A-02");
            var own = Place(state, 1, "B-01");
            if (emptySupply) state.FindPlayer(1).InfluenceSupply = 0;
            if (opponentCity) state.FindPlayer(2).CityLocationId = "A-02";
            Marker(state, CityStyleDatabase.MobilizationSupportSystem, SpecialActionDatabase.MobilizationSupportSystem);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.MobilizationSupportSystem, "test-marker"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            var request = Open(state, "lua.choice");
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { target.InfluenceId }));
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            Answer(state, registry, Open(state, "lua.choice"), target.InfluenceId);
            Assert.That(state.Map.Influences.Exists(i => i.InfluenceId == target.InfluenceId), Is.False);
            Assert.That(state.Map.Influences.Exists(i => i.InfluenceId == own.InfluenceId), Is.True);
            Assert.That(state.Map.Influences.Exists(i => i.SlotId == target.SlotId && i.PlayerId == 1), Is.EqualTo(!emptySupply && !opponentCity));
            Assert.That(state.EffectRuntime.EffectNodes.Find(n => n.EffectTypeId == InfluenceEffectTypeIds.ReplaceInfluence).NormalizedResult.ToDeterministicString(), Does.Contain(emptySupply || opponentCity ? "removed_only" : "replaced"));
        }

        private static void Marker(GameState state, string style, string action)
        {
            state.FindPlayer(1).DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "test-marker", CityStyleId = style, UnlockedSpecialActionId = action,
                MarkerArea = CityStyleMarkerAreas.Unused, RemainingSpecialActionUses = 2
            });
        }

        [Test]
        public void Composite_PaymentChoiceIsLeftEffectAndMoveThenRoutePlacementAreRightEffects()
        {
            var state = State();
            Marker(state, CityStyleDatabase.CompositePowerSystem, SpecialActionDatabase.CompositePowerSystem);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.CompositePowerSystem, "test-marker"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            var payment = Open(state, ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
            Assert.That(payment, Is.Not.Null);
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == CityStyleSpecialActionEventTypeIds.Activated), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CityMoveEffectTypeIds.Move), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            state = GameStateCloneService.DeepClone(state);
            registry = Registry(state);
            payment = Open(state, ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
            Answer(state, registry, payment, "pay|1|2|1|0|0");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Answer(state, registry, Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), "A-02");
            var route = Open(state, "effect.influence.place.target");
            Assert.That(route, Is.Not.Null, "移动完成后才选择沿途影响力位置");
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            string slot = route.CandidateIds[0];
            Answer(state, registry, route, slot);
            Assert.That(state.Map.Influences.Exists(i => i.SlotId == slot), Is.True);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
        }

        [Test]
        public void Composite_DeclinedPaymentDoesNotCreateRightEffectsOrConsumeMarker()
        {
            var state = State();
            Marker(state, CityStyleDatabase.CompositePowerSystem, SpecialActionDatabase.CompositePowerSystem);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.CompositePowerSystem, "test-marker"));
            executor.RunUntilQuiescent();
            Answer(state, registry, Open(state, ResourcePaymentChoiceEffectExecutor.InteractionTypeId), null, true);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.ResolveContent), Is.False);
        }

        [Test]
        public void OldPendingMoveVersion_FaultsBeforeExecutingNewRules()
        {
            var state = State();
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            string id = executor.CreateRoot(CityMoveEffectSpecFactory.Move(1, "A-02", true, false));
            executor.GetNode(id).DefinitionVersion = "old-version";
            state = GameStateCloneService.DeepClone(state);
            executor = new EffectTreeExecutor(state, Registry(state));
            Assert.That(executor.RunUntilQuiescent().FaultCode, Is.EqualTo(EffectFaultCodes.DefinitionVersionMismatch));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
        }

        [Test]
        public void Composite_ChangedBalanceFailsWholePaymentWithoutPartialDeduction()
        {
            var state = State();
            Marker(state, CityStyleDatabase.CompositePowerSystem, SpecialActionDatabase.CompositePowerSystem);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.CompositePowerSystem, "test-marker"));
            executor.RunUntilQuiescent();
            state.FindPlayer(1).Resources.Iron = 0;
            Answer(state, registry, Open(state, ResourcePaymentChoiceEffectExecutor.InteractionTypeId), "pay|1|2|1|0|0");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.ResolveContent), Is.False);
        }

        [TestCase(6, true)]
        [TestCase(5, false)]
        public void IndustrialHub_GrantAndCharacterLockRequireSuccessfulPayment(int gold, bool succeeds)
        {
            var state = State();
            state.FindPlayer(1).Resources.GoldVoucher = gold;
            Marker(state, CityStyleDatabase.SourceStoneIndustrialHub, SpecialActionDatabase.SourceStoneIndustrialHub);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.SourceStoneIndustrialHub, "test-marker"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(succeeds ? 0 : 5));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(succeeds ? 2 : 1));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.EqualTo(succeeds));
            Assert.That(state.EffectRuntime.RuleEvents.Exists(e => e.EventType == CityStyleSpecialActionEventTypeIds.Activated), Is.EqualTo(succeeds));
        }

        [Test]
        public void EfficientMove_PaysOnceAndGeneratesSecondTargetAfterFirstMove()
        {
            var state = State();
            Marker(state, CityStyleDatabase.EfficientMobileManagementSystem, SpecialActionDatabase.EfficientMobileManagementSystem);
            var registry = Registry(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Activate(1, SpecialActionDatabase.EfficientMobileManagementSystem, "test-marker"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Answer(state, registry, Open(state, "action.decline_effect"), "continue");
            Answer(state, registry, Open(state, CityMoveEffectExecutor.TargetInteractionTypeId), "A-02");
            Answer(state, registry, Open(state, "action.decline_effect"), "continue");
            var second = Open(state, CityMoveEffectExecutor.TargetInteractionTypeId);
            Assert.That(second, Is.Not.Null);
            Assert.That(second.CandidateIds, Does.Contain("A-01").And.Not.Contains("A-02"));
            Answer(state, registry, second, "A-01");
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True);
        }
    }
}
