using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class GameCommandSerializationTests
    {
        [Test]
        public void GameCommandDto_RoundTrip_PreservesCommandFields()
        {
            var command = new GameCommand
            {
                CommandId = "cmd-1",
                Kind = GameCommandKind.MoveCity,
                PlayerId = 2,
                SourceId = "A-01",
                TargetId = "A-02",
                OptionIds = { "opt-a", "opt-b" },
                Parameters =
                {
                    { "cost", "3" },
                    { "route", "A-01>A-02" }
                }
            };

            var dto = GameCommandDto.FromCommand(command);
            var restored = dto.ToCommand();

            Assert.That(restored.CommandId, Is.EqualTo("cmd-1"));
            Assert.That(restored.Kind, Is.EqualTo(GameCommandKind.MoveCity));
            Assert.That(restored.PlayerId, Is.EqualTo(2));
            Assert.That(restored.SourceId, Is.EqualTo("A-01"));
            Assert.That(restored.TargetId, Is.EqualTo("A-02"));
            Assert.That(restored.OptionIds, Is.EqualTo(new[] { "opt-a", "opt-b" }));
            Assert.That(restored.Parameters["cost"], Is.EqualTo("3"));
            Assert.That(restored.Parameters["route"], Is.EqualTo("A-01>A-02"));
        }

        [Test]
        public void GameCommandDto_ToCommand_ToleratesMissingListsFromNetworkPayload()
        {
            var dto = new GameCommandDto
            {
                CommandId = "cmd-minimal",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1,
                OptionIds = null,
                Parameters = null
            };

            var restored = dto.ToCommand();

            Assert.That(restored.CommandId, Is.EqualTo("cmd-minimal"));
            Assert.That(restored.OptionIds, Is.Empty);
            Assert.That(restored.Parameters, Is.Empty);
        }

        [Test]
        public void EventDeckService_WithSameSeed_InitializesSameDeckOrder()
        {
            var hostDecks = new DeckRuntimeState();
            var clientDecks = new DeckRuntimeState();
            var greenIds = new[] { "event_green_01", "event_green_02", "event_green_03", "event_green_04" };
            var yellowIds = new[] { "event_yellow_01", "event_yellow_02", "event_yellow_03", "event_yellow_04" };
            var redIds = new[] { "event_red_01", "event_red_02", "event_red_03", "event_red_04" };

            new EventDeckService(12345).InitializeDecks(hostDecks, greenIds, yellowIds, redIds);
            new EventDeckService(12345).InitializeDecks(clientDecks, greenIds, yellowIds, redIds);

            Assert.That(clientDecks.EventDeckGreen, Is.EqualTo(hostDecks.EventDeckGreen));
            Assert.That(clientDecks.EventDeckYellow, Is.EqualTo(hostDecks.EventDeckYellow));
            Assert.That(clientDecks.EventDeckRed, Is.EqualTo(hostDecks.EventDeckRed));
        }

        [Test]
        public void GameState_JsonRoundTrip_PreservesCompleteCityStyleDeclarationState()
        {
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "style-marker-1",
                                CityStyleId = CityStyleDatabase.CompositePowerSystem,
                                MarkerArea = CityStyleMarkerAreas.UsesOne,
                                UnlockedSpecialActionId = "special-action-test",
                                RemainingSpecialActionUses = 1,
                                UsedFacilityIds = { "building-a", "building-b" },
                                UsedCityBoardSlotIndexes = { 1, 4 }
                            }
                        }
                    }
                }
            };

            AssertCompleteCityStyleDeclaration(CloneJson(state));
        }

        private static T CloneJson<T>(T source)
        {
            return JsonUtility.FromJson<T>(JsonUtility.ToJson(source));
        }

        private static void AssertCompleteCityStyleDeclaration(GameState state)
        {
            var player = state.FindPlayer(1);
            Assert.That(player, Is.Not.Null);
            Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1));
            var declaration = player.DeclaredCityStyles[0];
            Assert.That(declaration.InfluenceMarkerId, Is.EqualTo("style-marker-1"));
            Assert.That(declaration.CityStyleId, Is.EqualTo(CityStyleDatabase.CompositePowerSystem));
            Assert.That(declaration.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(declaration.UnlockedSpecialActionId, Is.EqualTo("special-action-test"));
            Assert.That(declaration.RemainingSpecialActionUses, Is.EqualTo(1));
            Assert.That(declaration.UsedFacilityIds, Is.EqualTo(new[] { "building-a", "building-b" }));
            Assert.That(declaration.UsedCityBoardSlotIndexes, Is.EqualTo(new[] { 1, 4 }));
        }
    }
}
