using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceRemovalServiceTests
    {
        [Test]
        public void Remove_ExistingInfluence_SucceedsAndReturnsSupply()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.FindPlayer(1).InfluenceSupply = 29;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = slotId,
                LocationId = "A-01"
            });
            var service = new InfluenceRemovalService();

            var result = service.Remove(state, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void Remove_NonexistentInfluence_FailsWithInfluenceNotFound()
        {
            var state = CreateState();
            var service = new InfluenceRemovalService();

            var result = service.Remove(state, LocationSlot("A-01", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(InfluenceFailureCode.InfluenceNotFound));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void RemoveAll_OnlyRemovesSpecifiedPlayer()
        {
            var state = CreateState();
            state.FindPlayer(1).InfluenceSupply = 28;
            state.FindPlayer(2).InfluenceSupply = 29;
            var slot1 = LocationSlot("A-01", 0);
            var slot2 = LocationSlot("A-01", 1);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot1, LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = slot2, LocationId = "A-01" });
            var service = new InfluenceRemovalService();

            var results = service.RemoveAll(state, new[] { slot1, slot2 }, onlyPlayerId: 1);

            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(1));
            Assert.That(state.Map.Influences[0].PlayerId, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void RemoveAll_WithoutPlayerFilter_RemovesAll()
        {
            var state = CreateState();
            state.FindPlayer(1).InfluenceSupply = 29;
            state.FindPlayer(2).InfluenceSupply = 29;
            var slot1 = LocationSlot("A-01", 0);
            var slot2 = LocationSlot("A-01", 1);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot1, LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = slot2, LocationId = "A-01" });
            var service = new InfluenceRemovalService();

            var results = service.RemoveAll(state, new[] { slot1, slot2 });

            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(30));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
        }

        private static string LocationSlot(string locationId, int index)
        {
            return "location:" + locationId + ":" + index;
        }
    }
}
