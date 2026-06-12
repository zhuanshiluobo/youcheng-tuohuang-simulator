using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceServiceContractTests
    {
        private const string ExploredLocationId = "A-02";
        private const string SourceLocationId = "A-01";
        private const string RouteId = "A1";

        [Test]
        public void InfluenceService_ExposesExpectedPublicContract()
        {
            var type = FindInfluenceServiceType();

            Assert.That(type, Is.Not.Null, "Expected production type YC.Domain.Influence.InfluenceService.");
            AssertHasMethod(type, "CanPlace", typeof(GameState), typeof(int), typeof(string));
            AssertHasMethod(type, "Place", typeof(GameState), typeof(int), typeof(string));
            AssertHasMethod(type, "CanMove", typeof(GameState), typeof(int), typeof(string), typeof(string));
            AssertHasMethod(type, "Move", typeof(GameState), typeof(int), typeof(string), typeof(string));
            AssertHasMethod(type, "Remove", typeof(GameState), typeof(string));
            AssertHasMethod(type, "Replace", typeof(GameState), typeof(string), typeof(int));
            AssertHasMethod(type, "CountInRegion", typeof(GameState), typeof(int), typeof(string), typeof(bool));
        }

        [Test]
        public void Place_OnExploredEmptyLocationSlot_SucceedsAndConsumesSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            var slotId = LocationSlot(ExploredLocationId, 0);

            var result = Invoke(service, "Place", state, 1, slotId);

            AssertSucceeded(result);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].PlayerId, Is.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(slotId));
            Assert.That(state.Map.Influences[0].LocationId, Is.EqualTo(ExploredLocationId));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void Place_OnUnexploredLocationSlot_FailsWithoutConsumingSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateState();
            var slotId = LocationSlot(ExploredLocationId, 0);

            var result = Invoke(service, "CanPlace", state, 1, slotId);

            AssertRejected(result, CommandErrorCode.ClosedLocation);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void Place_OnLocationWithOpponentCity_FailsWithoutConsumingSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            state.FindPlayer(2).CityLocationId = ExploredLocationId;
            var slotId = LocationSlot(ExploredLocationId, 0);

            var result = Invoke(service, "CanPlace", state, 1, slotId);

            AssertRejected(result, CommandErrorCode.OccupiedSlot);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void Place_OnRoadCoveredRouteSlot_FailsWithoutConsumingSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            AddRoadRoute(state, RouteId);
            var slotId = RouteSlot(RouteId, 0);

            var result = Invoke(service, "CanPlace", state, 1, slotId);

            AssertRejected(result, CommandErrorCode.OccupiedSlot);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void Place_WithoutAvailableSupply_FailsWithoutMutatingMap()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            state.FindPlayer(1).InfluenceSupply = 0;
            var slotId = LocationSlot(ExploredLocationId, 0);

            var result = Invoke(service, "CanPlace", state, 1, slotId);

            AssertRejected(result, CommandErrorCode.InsufficientInfluence);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(0));
        }

        [Test]
        public void Move_RelocatesInfluenceWithoutChangingSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            var sourceSlotId = LocationSlot(SourceLocationId, 0);
            var targetSlotId = LocationSlot(ExploredLocationId, 0);
            state.FindPlayer(1).InfluenceSupply = 29;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlotId,
                LocationId = SourceLocationId
            });

            var result = Invoke(service, "Move", state, 1, sourceSlotId, targetSlotId);

            AssertSucceeded(result);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(targetSlotId));
            Assert.That(state.Map.Influences[0].LocationId, Is.EqualTo(ExploredLocationId));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void Remove_ReturnsInfluenceToOwnersSupply()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            var slotId = LocationSlot(ExploredLocationId, 0);
            state.FindPlayer(1).InfluenceSupply = 29;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = slotId,
                LocationId = ExploredLocationId
            });

            var result = Invoke(service, "Remove", state, slotId);

            AssertSucceeded(result);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void CountInRegion_WhenIncludingCity_CountsCityAsTwoInfluence()
        {
            var service = CreateServiceOrIgnore();
            var state = CreateStateWithExploredLocation();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = LocationSlot(ExploredLocationId, 0),
                LocationId = ExploredLocationId
            });

            var withoutCity = Invoke(service, "CountInRegion", state, 1, "A", false);
            var withCity = Invoke(service, "CountInRegion", state, 1, "A", true);

            Assert.That(withoutCity, Is.EqualTo(1));
            Assert.That(withCity, Is.EqualTo(3));
        }

        private static GameState CreateStateWithExploredLocation()
        {
            var state = CreateState();
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = SourceLocationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = ExploredLocationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            return state;
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red,
                        CityLocationId = SourceLocationId,
                        InfluenceSupply = 30
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue,
                        CityLocationId = "B-01",
                        InfluenceSupply = 30
                    }
                },
                Map =
                {
                    OpenLocationIds = { SourceLocationId, ExploredLocationId, "B-01" }
                }
            };
        }

        private static object CreateServiceOrIgnore()
        {
            var type = FindInfluenceServiceType();
            if (type == null)
            {
                Assert.Ignore("InfluenceService is not implemented yet. See InfluenceService_ExposesExpectedPublicContract.");
            }

            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var mapQueryCtor = type.GetConstructor(new[] { typeof(IMapQueryService) });
            if (mapQueryCtor != null)
            {
                return mapQueryCtor.Invoke(new object[] { mapQuery });
            }

            var parameterlessCtor = type.GetConstructor(Type.EmptyTypes);
            if (parameterlessCtor != null)
            {
                return parameterlessCtor.Invoke(null);
            }

            Assert.Fail("InfluenceService must provide a public constructor with IMapQueryService or no parameters.");
            return null;
        }

        private static Type FindInfluenceServiceType()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("YC.Domain.Influence.InfluenceService", false))
                .FirstOrDefault(type => type != null);
        }

        private static void AssertHasMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null);
            Assert.That(method, Is.Not.Null, "Missing InfluenceService." + methodName + " with expected parameters.");
        }

        private static object Invoke(object service, string methodName, params object[] args)
        {
            var parameterTypes = args.Select(arg => arg.GetType()).ToArray();
            var method = service.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null);
            Assert.That(method, Is.Not.Null, "Missing InfluenceService." + methodName + " with expected parameters.");
            return method.Invoke(service, args);
        }

        private static void AssertSucceeded(object result)
        {
            if (result is InfluenceOperationResult influenceResult)
            {
                Assert.That(influenceResult.Succeeded, Is.True);
                return;
            }

            if (result is CommandResult commandResult)
            {
                Assert.That(commandResult.Succeeded, Is.True);
                return;
            }

            if (result is ValidationResult validationResult)
            {
                Assert.That(validationResult.IsValid, Is.True);
                return;
            }

            if (result is bool boolResult)
            {
                Assert.That(boolResult, Is.True);
                return;
            }

            Assert.Fail("Expected CommandResult, ValidationResult, InfluenceOperationResult, or bool result.");
        }

        private static void AssertRejected(object result, CommandErrorCode expectedErrorCode)
        {
            if (result is InfluenceOperationResult influenceResult)
            {
                Assert.That(influenceResult.Succeeded, Is.False);
                Assert.That(influenceResult.Validation.ErrorCode, Is.EqualTo(expectedErrorCode));
                return;
            }

            if (result is CommandResult commandResult)
            {
                Assert.That(commandResult.Succeeded, Is.False);
                Assert.That(commandResult.Validation.ErrorCode, Is.EqualTo(expectedErrorCode));
                return;
            }

            if (result is ValidationResult validationResult)
            {
                Assert.That(validationResult.IsValid, Is.False);
                Assert.That(validationResult.ErrorCode, Is.EqualTo(expectedErrorCode));
                return;
            }

            Assert.Fail("Failure checks must return CommandResult, ValidationResult, or InfluenceOperationResult so callers can inspect the error code.");
        }

        private static void AddRoadRoute(GameState state, string routeId)
        {
            var mapState = state.Map;
            var field = mapState.GetType().GetField("RoadRouteIds", BindingFlags.Instance | BindingFlags.Public);
            if (field != null && field.GetValue(mapState) is IList roadRouteIds)
            {
                roadRouteIds.Add(routeId);
                return;
            }

            var property = mapState.GetType().GetProperty("RoadRouteIds", BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.GetValue(mapState, null) is IList propertyRoadRouteIds)
            {
                propertyRoadRouteIds.Add(routeId);
                return;
            }

            Assert.Fail("MapRuntimeState must expose RoadRouteIds so roads can block route influence slots.");
        }

        private static string LocationSlot(string locationId, int index)
        {
            return "location:" + locationId + ":" + index;
        }

        private static string RouteSlot(string routeId, int index)
        {
            return "route:" + routeId + ":" + index;
        }
    }
}
