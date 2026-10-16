using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.State;
#if PRODUCTION_ROUTE_CONCURRENCY_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class ProductionGameSelectionPriorityTests
    {
        private static Type Target(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Presentation.Validation." + name)).First(t => t != null);
        private static object Plan() => JsonConvert.DeserializeObject(@"{
            'RouteName':'整局优先级窄验','PlayerCount':3,'MapId':'map-three-players','ContentPackHash':'固定夹具',
            'EnabledExpansionIds':['core','enterprise'],'EvaluationMode':'whole-game-health','AllowAdaptiveMainActions':true,
            'SelectionPriorities':{'EnterpriseDefinitionIds':['不可见定义','定义乙'],'DispatchCharacterDefinitionIds':['不可见定义','定义乙'],
                'CharacterUseModes':[{'DefinitionId':'定义乙','Mode':'tactic'}]}}", Target("ProductionGameRoutePlan"));
        private static GameStateView View(string kind) => JsonConvert.DeserializeObject<GameStateView>(@"{
            'GameId':'固定局','ViewerRole':'Player','ViewerPlayerId':2,'CurrentPlayerId':-1,'Round':1,'Phase':'Entrance','StateRevision':100,
            'Interactions':[{'InteractionId':'正式请求','SourceNodeId':'正式owner','OwnerEffectId':'正式owner','Kind':'" + kind + @"',
                'AnsweringPlayerId':2,'Status':'open','AnswerSchema':'candidate_id','MinSelections':1,'MaxSelections':1,'StateRevision':100,
                'CandidateIds':['本人甲','本人乙'],'PromptParameters':{'Kind':'Object','Properties':[{'Name':'candidateDefinitionIds','Value':
                    {'Kind':'Array','Items':[{'Kind':'String','StringValue':'定义甲'},{'Kind':'String','StringValue':'定义乙'}]}}]}}],
            'Effects':[{'EffectId':'正式owner','EffectTypeId':'setup.enterprise.select','PlayerId':2}],
            'ItemZones':{'Items':[{'ItemId':'本人乙','DefinitionId':'定义乙'}]}}");
        private static GameCommand Decide(GameStateView view)
        {
            var policy = Activator.CreateInstance(Target("ProductionGameRoutePolicy"), Plan(), null);
            return (GameCommand)policy.GetType().GetMethod("Decide").Invoke(policy, new object[] { view, 2, "" });
        }

        [TestCase("setup.enterprise.choose", "本人乙")]
        [TestCase("operator.dispatch.choose", "本人乙")]
        [TestCase("operator.dispatch.return", "本人甲")]
        [TestCase("setup.basic_character.discard", "本人甲")]
        public void PrioritiesOnlyReorderVisibleLegalCandidates(string kind, string expected)
        {
            var view = View(kind); var original = view.Interactions.Single().CandidateIds.ToArray();
            var command = Decide(view);
            Assert.That(command.OptionIds, Is.EqualTo(new[] { expected }));
            Assert.That(command.OptionIds.All(original.Contains), Is.True);
            Assert.That(view.Interactions.Single().CandidateIds, Is.EqualTo(original));
            Assert.That(command.Parameters["expectedRevision"], Is.EqualTo("100"));
        }

        [TestCase(true, "tactic")]
        [TestCase(false, "strategy")]
        public void ModePriorityUsesAvailableFormalModes(bool tacticAvailable, string expected)
        {
            var view = View("character.use.mode"); var request = view.Interactions.Single();
            request.CandidateIds = tacticAvailable ? new System.Collections.Generic.List<string> { "strategy", "tactic" } : new System.Collections.Generic.List<string> { "strategy" };
            var owner = view.Effects.Single(); owner.NormalizedArguments = NormalizedValue.CreateObject(new System.Collections.Generic.List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "useId", Value = NormalizedValue.CreateString("实际使用") } });
            view.CharacterUses.Add(new YC.Domain.Cards.CharacterUseRuntimeState { UseId = "实际使用", CardInstanceId = "本人乙", ExecutingPlayerId = 2 });
            var command = Decide(view);
            Assert.That(command.OptionIds, Is.EqualTo(new[] { expected }));
            Assert.That(request.CandidateIds.Contains(command.OptionIds.Single()), Is.True);
        }

        [Test]
        public void EventPriorityUsesOnlyTheFormalCurrentOptionId()
        {
            var view = View("event_card.option"); var request = view.Interactions.Single();
            string first = YC.Domain.Effects.EventCardOptionIdFactory.Create("夹具事件", 0), wanted = YC.Domain.Effects.EventCardOptionIdFactory.Create("夹具事件", 1);
            request.CandidateIds = new System.Collections.Generic.List<string> { first, wanted };
            var plan = Plan(); plan.GetType().GetField("CandidatePriorityDefinitionIds").SetValue(plan, new System.Collections.Generic.List<string> { wanted });
            var policy = Activator.CreateInstance(Target("ProductionGameRoutePolicy"), plan, null);
            var command = (GameCommand)policy.GetType().GetMethod("Decide").Invoke(policy, new object[] { view, 2, "" });
            Assert.That(command.OptionIds, Is.EqualTo(new[] { wanted }));
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { first, wanted }));
        }

        [Test]
        public void StylePreferenceRanksLegalPositionsWithoutChangingState()
        {
            var state = new GameState { EntitySchemaVersion = 0, CurrentPlayerId = 2, Round = 1, Phase = YC.Domain.Rules.GamePhase.ActionRound1,
                MapId = "map-four-players", ContentDefinitions = new YC.Domain.Rules.GameContentDefinitions() };
            state.Players.Add(new PlayerState { PlayerId = 2, CityLocationId = "A-01" });
            state.ContentDefinitions.Facilities.Add(new YC.Domain.Facilities.FacilityCardDefinition { FacilityId = "待建红色", Color = "red" });
            state.ContentDefinitions.Facilities.Add(new YC.Domain.Facilities.FacilityCardDefinition { FacilityId = "已有蓝色", Color = "blue" });
            state.ContentDefinitions.CityStyles.Add(new YC.Domain.CityStyles.CityStyleDefinition { CityStyleId = "目标样式", DeclarationRequirement = new YC.Domain.CityStyles.CityStyleRequirement {
                RequiredFacilityCount = 2, RequiredPatternCells = new System.Collections.Generic.List<YC.Domain.CityStyles.CityStylePatternCell> {
                    new YC.Domain.CityStyles.CityStylePatternCell { AllowedFacilityColors = new System.Collections.Generic.List<string> { "blue" } },
                    new YC.Domain.CityStyles.CityStylePatternCell { ColumnOffset = 1, AllowedFacilityColors = new System.Collections.Generic.List<string> { "red" } } } } });
            state.Decks.FacilitySupply.Add("待建红色"); state.Decks.CityStyleSupply.Add("目标样式");
            state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 2, FacilityCardId = "已有蓝色", ContentInstanceId = "蓝色实例", CityBoardSlotIndex = 0 });
            string before = JsonConvert.SerializeObject(state);
            var build = new YC.Domain.Facilities.BuildFacilityService();
            Assert.That(build.WithReadOnlyQuery(state, 2, null, q => q.ValidateCityBoardSlot("待建红色", 1).IsValid && q.ValidateCityBoardSlot("待建红色", 4).IsValid), Is.True);
            var method = Target("ProductionGameRoutePolicy").GetMethod("CityStylePlacementFit", BindingFlags.NonPublic | BindingFlags.Static);
            int near = (int)method.Invoke(null, new object[] { state, 2, "待建红色", 1, new[] { "目标样式" } });
            int distant = (int)method.Invoke(null, new object[] { state, 2, "待建红色", 4, new[] { "目标样式" } });
            Assert.That(near > distant, Is.True);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void WholeGamePlanAllowsEmptyFixedStepsAndCoverage()
        {
            var plan = Plan();
            Assert.That(JsonConvert.SerializeObject(plan), Does.Contain("whole-game-health"));
            Assert.That(Target("ProductionGameRoutePlan").GetMethod("Validate").Invoke(plan, Array.Empty<object>()), Is.Null);
        }

        [TestCase(false, "resources")]
        [TestCase(true, "gold")]
        public void BuildPrioritySubmitsAnAffordableExplicitPaymentToTheFormalHandler(bool goldOnly, string expected)
        {
            if (!YC.Domain.Facilities.FacilityCardDatabase.IsInitialized)
                YC.Domain.Facilities.FacilityCardDatabase.InitializeExternal(YC.Infrastructure.Lua.ExternalContentPack.Current.CreateFacilities());
            string facilityId = YC.Domain.Facilities.FacilityCardDatabase.BoroughAdministrativeDistrict;
            var facility = YC.Domain.Facilities.FacilityCardDatabase.Get(facilityId);
            var state = new GameState { EntitySchemaVersion = 0, CurrentPlayerId = 2, StartPlayerId = 2, Round = 1,
                ActionRound = 2, Phase = YC.Domain.Rules.GamePhase.ActionRound2, MapId = "map-three-players",
                ContentDefinitions = new YC.Domain.Rules.GameContentDefinitions() };
            var resources = goldOnly ? new ResourceSet { GoldVoucher = facility.GoldVoucherCost } :
                JsonConvert.DeserializeObject<ResourceSet>(JsonConvert.SerializeObject(facility.ResourceCost));
            state.Players.Add(new PlayerState { PlayerId = 2, CityLocationId = "A-01", RemainingMainActionsThisTurn = 1,
                Resources = resources });
            state.ContentDefinitions.Facilities.Add(facility);
            state.Decks.FacilitySupply.Add(facilityId);
            string before = JsonConvert.SerializeObject(state);
            var view = GameStateViewProjector.Project(state, GameStateViewer.Player(2));
            var policy = Activator.CreateInstance(Target("ProductionGameRoutePolicy"), Plan(), null);
            var command = (GameCommand)policy.GetType().GetMethod("LegalMainAction", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(policy, new object[] { view, 2 });
            Assert.That(command.Kind, Is.EqualTo(YC.Domain.Rules.GameCommandKind.BuildFacility));
            Assert.That(command.Parameters[YC.Application.Gameplay.BuildFacilityCommandHandler.PaymentModeParameter], Is.EqualTo(expected));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var result = new YC.Application.Gameplay.BuildFacilityCommandHandler().Handle(state, command);
            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Facilities.Single().FacilityCardId, Is.EqualTo(facilityId));
            Assert.That(state.FindPlayer(2).Resources.Iron + state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
        }
    }
}
