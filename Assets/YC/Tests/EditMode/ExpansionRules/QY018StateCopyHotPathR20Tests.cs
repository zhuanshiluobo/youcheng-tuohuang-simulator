using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class QY018StateCopyHotPathR20Tests
    {
        public enum Marker { First = -1, Last = 19 }
        [Serializable] public class BaseDto { public List<int> Numbers = new List<int>(); }
        [Serializable] public sealed class DerivedDto : BaseDto { public decimal Amount; public object[] Mixed; }
        [Serializable] public sealed class ScalarDto
        {
            public List<long> Longs;
            public List<decimal> Decimals;
            public List<Marker> Markers;
            public List<int?> Nullable;
            public List<string> Text;
            public int[] Array;
            public BaseDto Child;
        }
        [Serializable] public sealed class MalformedPlayers : List<PlayerState> { public object Extra; }
        [Serializable] public sealed class ScalarPlayers : List<PlayerState>
        {
            public int[] Sequence = new int[3];
            public List<long> Values = new List<long>();
        }
        public sealed class Unsupported { }

        [Test]
        public void ScalarCollectionsAndDerivedDtosKeepValuesTypesAndTwoWayIsolation()
        {
            var source = new ScalarDto
            {
                Longs = new List<long> { long.MinValue, 0, long.MaxValue },
                Decimals = new List<decimal> { decimal.MinValue, 0.1m, decimal.MaxValue },
                Markers = new List<Marker> { Marker.Last, Marker.First, Marker.Last },
                Nullable = new List<int?> { null, int.MaxValue, 0, null },
                Text = new List<string> { null, "", "重复", "重复" },
                Array = new[] { int.MinValue, 0, int.MaxValue },
                Child = new DerivedDto { Amount = 1.25m, Numbers = new List<int> { 7 }, Mixed = new object[] { new List<int> { 8 }, new[] { 9 } } }
            };
            var copy = GameStateCloneService.DeepClone(source);
            Assert.That(copy.Longs, Is.EqualTo(source.Longs)); Assert.That(copy.Decimals, Is.EqualTo(source.Decimals));
            Assert.That(copy.Markers, Is.EqualTo(source.Markers)); Assert.That(copy.Nullable, Is.EqualTo(source.Nullable));
            Assert.That(copy.Text, Is.EqualTo(source.Text)); Assert.That(copy.Array, Is.EqualTo(source.Array));
            Assert.That(copy.Child, Is.TypeOf<DerivedDto>()); Assert.That(((DerivedDto)copy.Child).Amount, Is.EqualTo(1.25m));
            source.Longs[0] = 5; source.Text[0] = "源"; source.Array[0] = 6; source.Child.Numbers[0] = 10;
            Assert.That(copy.Longs[0], Is.EqualTo(long.MinValue)); Assert.That(copy.Text[0], Is.Null);
            Assert.That(copy.Array[0], Is.EqualTo(int.MinValue)); Assert.That(copy.Child.Numbers[0], Is.EqualTo(7));
            copy.Decimals.Clear(); copy.Markers.Clear(); copy.Nullable.Clear();
            ((List<int>)((DerivedDto)copy.Child).Mixed[0])[0] = 11;
            ((int[])((DerivedDto)copy.Child).Mixed[1])[0] = 12;
            Assert.That(source.Decimals.Count, Is.EqualTo(3)); Assert.That(source.Markers.Count, Is.EqualTo(3));
            Assert.That(source.Nullable.Count, Is.EqualTo(4));
            Assert.That(((List<int>)((DerivedDto)source.Child).Mixed[0])[0], Is.EqualTo(8));
            Assert.That(((int[])((DerivedDto)source.Child).Mixed[1])[0], Is.EqualTo(9));
        }

        [Test]
        public void CopyToPreservesExistingCollectionsObjectsAndNullableOrdering()
        {
            var target = new GameState { Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }, LaunchPlayerOrder = new List<int> { 4, 3, 2, 1 } };
            var players = target.Players; var player = players[0]; var order = target.LaunchPlayerOrder; var map = target.Map;
            var source = GameStateCloneService.DeepClone(target);
            source.LaunchPlayerOrder = new List<int> { 1, 2 }; source.Players[0].Name = "已复制";
            source.Players.Add(new PlayerState { PlayerId = 2 });
            GameStateCloneService.CopyTo(target, source);
            Assert.That(target.Players, Is.SameAs(players)); Assert.That(target.Players[0], Is.SameAs(player));
            Assert.That(target.Map, Is.SameAs(map)); Assert.That(target.LaunchPlayerOrder, Is.SameAs(order));
            Assert.That(GameStateCloneService.AreEquivalent(target, source), Is.True);
            target.Players[1].Name = "目标"; target.LaunchPlayerOrder[0] = 5;
            Assert.That(source.Players[1].Name, Is.Empty); Assert.That(source.LaunchPlayerOrder[0], Is.EqualTo(1));
            source.Players[0].Name = "源"; Assert.That(target.Players[0].Name, Is.EqualTo("已复制"));
            source.LaunchPlayerOrder.Clear(); GameStateCloneService.CopyTo(target, source);
            Assert.That(target.LaunchPlayerOrder, Is.SameAs(order)); Assert.That(order, Is.Empty);
            source.LaunchPlayerOrder = null; GameStateCloneService.CopyTo(target, source); Assert.That(target.LaunchPlayerOrder, Is.Null);
            source.LaunchPlayerOrder = new List<int> { 3, 1, 2 }; GameStateCloneService.CopyTo(target, source);
            Assert.That(target.LaunchPlayerOrder, Is.EqualTo(new[] { 3, 1, 2 })); Assert.That(target.LaunchPlayerOrder, Is.Not.SameAs(source.LaunchPlayerOrder));
            GameStateCloneService.CopyTo(target, target); Assert.That(target.LaunchPlayerOrder, Is.EqualTo(new[] { 3, 1, 2 }));

            var derived = new ScalarPlayers { Sequence = new[] { 1, 2, 3 }, Values = new List<long> { 4, 5 } };
            derived.Add(new PlayerState { PlayerId = 1 }); target.Players = derived;
            source = GameStateCloneService.DeepClone(target); var values = derived.Values; var sequence = derived.Sequence;
            ((ScalarPlayers)source.Players).Sequence[1] = 6; ((ScalarPlayers)source.Players).Values.Add(7);
            GameStateCloneService.CopyTo(target, source);
            Assert.That(target.Players, Is.SameAs(derived)); Assert.That(derived.Values, Is.SameAs(values));
            Assert.That(derived.Sequence, Is.SameAs(sequence)); Assert.That(sequence, Is.EqualTo(new[] { 1, 6, 3 }));
            Assert.That(values, Is.EqualTo(new long[] { 4, 5, 7 }));
            sequence[0] = 8; ((ScalarPlayers)source.Players).Values[0] = 9;
            Assert.That(((ScalarPlayers)source.Players).Sequence[0], Is.EqualTo(1)); Assert.That(values[0], Is.EqualTo(4));
        }

        [Test]
        public void CopyToRejectsUnsupportedNewElementAndCycleWithExactPath()
        {
            var source = new GameState { Players = new MalformedPlayers { Extra = new Dictionary<string, int>() } };
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.CopyTo(new GameState(), source)).Message,
                Is.EqualTo("持久化状态禁止 Dictionary：GameState.Players.Extra。请改用有序 List DTO。"));
            ((MalformedPlayers)source.Players).Extra = new Unsupported();
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.CopyTo(new GameState(), source)).Message,
                Does.StartWith("状态字段必须是 [Serializable] DTO：GameState.Players.Extra ("));
            ((MalformedPlayers)source.Players).Extra = source.Players;
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.CopyTo(new GameState(), source)).Message,
                Is.EqualTo("状态 DTO 不能包含循环引用：GameState.Players.Extra。"));
        }
    }
}
