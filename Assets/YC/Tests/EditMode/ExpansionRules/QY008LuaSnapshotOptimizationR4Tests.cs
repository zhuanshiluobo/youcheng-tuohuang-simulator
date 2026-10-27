using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 当前正式夹具的隔离对照；不操作场景，不计正常局或Unity时延改善。
    public sealed class QY008LuaSnapshotOptimizationR4Tests
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private static object Invoke(string name, params object[] arguments) => typeof(ExternalContentPack).Assembly
            .GetType("YC.Infrastructure.Lua.LuaQuerySnapshotBuilder", true).GetMethod(name, Flags).Invoke(null, arguments);
        private static LuaSnapshotValue Content(GameState state, int player)
        {
            var visible = new ItemZoneQuery().Project(state, GameStateViewer.Player(player));
            var instances = visible.Items.Select(i => LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("id", LuaSnapshotValue.String(i.ItemId)),
                new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(i.DefinitionId)),
                new LuaSnapshotEntry("owner", LuaSnapshotValue.String("p" + i.ControllerSubject.PlayerId)) })).ToList();
            return (LuaSnapshotValue)Invoke("Content", state, visible, instances);
        }
        private static string Json(object value) => JsonConvert.SerializeObject(value);
        private static HashSet<string> Ids(LuaSnapshotValue snapshot) => new HashSet<string>(snapshot.Get("instances").Items.Select(i => i.Get("id").StringValue), StringComparer.Ordinal);

        [Test] public void SameRevisionChangesRefreshFactsAndBoundedCapacityWithoutMutatingPriorSnapshots()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal();
            var state = fixture.State; var item = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1);
            var before = Content(state, 1); string original = Json(before); int revision = state.EffectRuntime.StateRevision;
            string digest = before.Get("instances").Items.Single(i => i.Get("id").StringValue == item.ItemId).Get("localStateDigest").StringValue;
            item.LocalState = Object("amount", NormalizedValue.CreateInteger(2));
            var zone = fixture.Storage; int count = state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId);
            zone.Configuration.CapacityPolicy = "bounded"; zone.Configuration.Capacity = count + 3;
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            string stateBeforeRead = Json(state); var after = Content(state, 1);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(after.Get("instances").Items.Single(i => i.Get("id").StringValue == item.ItemId).Get("localStateDigest").StringValue, Is.Not.EqualTo(digest));
            Assert.That(after.Get("zones").Items.Single(z => z.Get("id").StringValue == zone.ZoneId).Get("capacity").IntegerValue, Is.EqualTo(count + 3));
            Assert.That(Json(before), Is.EqualTo(original)); Assert.That(Json(state), Is.EqualTo(stateBeforeRead));
            zone.Configuration.Capacity = count + 8;
            Assert.That(Content(state, 1).Get("zones").Items.Single(z => z.Get("id").StringValue == zone.ZoneId).Get("capacity").IntegerValue, Is.EqualTo(count + 8));
            Assert.That(after.Get("zones").Items.Single(z => z.Get("id").StringValue == zone.ZoneId).Get("capacity").IntegerValue, Is.EqualTo(count + 3));
            var oldMap = Invoke("Map", state, null); string retainedMap = Json(oldMap);
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            var destination = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.city_dock" && z.ZoneId != city.ZoneId &&
                !state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId));
            // 明确隔离输入变化，验证兼容位置投影不跨同步调用保留；不计正常移动流程。
            city.ZoneId = destination.ZoneId;
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.True, reason);
            string location = state.ItemZones.Zones.Single(z => z.ZoneId == destination.ParentId).LocalZoneId;
            var updatedMap = (LuaSnapshotValue)Invoke("Map", state, null);
            Assert.That(updatedMap.Get("citiesAt").Get(location).Items.Any(p => p.Get("playerId").StringValue == "p1"), Is.True);
            Assert.That(Json(oldMap), Is.EqualTo(retainedMap)); Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
        }

        [Test] public void VisibleIdentitySetsRemainPerPlayerAndReturnedValuesAreDetached()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal(); var state = fixture.State;
            var ownHand = state.ItemZones.Zones.Single(z => z.LocalZoneId == "hand" && z.OwnerPlayerId == 1);
            var otherHand = state.ItemZones.Zones.Single(z => z.LocalZoneId == "hand" && z.OwnerPlayerId == 2);
            var own = state.ItemZones.Items.Where(i => i.ZoneId == ownHand.ZoneId).Select(i => i.ItemId).ToArray();
            var other = state.ItemZones.Items.Where(i => i.ZoneId == otherHand.ZoneId).Select(i => i.ItemId).ToArray();
            Assert.That(own.Length > 0, Is.True); Assert.That(other.Length > 0, Is.True);
            string stateBefore = Json(state); var first = Content(state, 1); var second = Content(state, 2);
            Assert.That(own.All(Ids(first).Contains) && other.All(Ids(second).Contains), Is.True);
            Assert.That(other.Any(Ids(first).Contains) || own.Any(Ids(second).Contains), Is.False);
            string retained = Json(first);
            var projected = new ItemZoneQuery().Project(state, GameStateViewer.Player(1)); projected.Items[0].DefinitionId = "qy008.invalid_copy";
            Assert.That(Json(first), Is.EqualTo(retained)); Assert.That(Json(state), Is.EqualTo(stateBefore));
            Assert.That(Json(Content(state, 1)), Is.EqualTo(retained));
        }

        [Test] public void InvalidCapacityAndCandidateInputsKeepTheirRejections()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal(); var state = fixture.State;
            var zone = GameStateCloneService.DeepClone(fixture.Storage);
            zone.TemplateId = "qy008.unknown_template"; zone.Configuration.CapacityPolicy = "unbounded"; zone.Configuration.Capacity = null;
            Assert.That(Error(() => Invoke("ZoneCapacity", zone)), Is.EqualTo(typeof(NullReferenceException).FullName));
            zone.TemplateId = null;
            Assert.That(Error(() => Invoke("ZoneCapacity", zone)), Is.EqualTo(typeof(NullReferenceException).FullName));
            zone.Configuration.CapacityPolicy = "bounded";
            Assert.That(Error(() => Invoke("ZoneCapacity", zone)), Is.EqualTo(typeof(InvalidOperationException).FullName));
            string before = Json(state);
            var draft = new CandidateSetDraft { CandidatePoolIds = new List<string> { "qy008.missing_item" } };
            Assert.That(Error(() => Invoke("CandidateContent", state, draft, state.EffectRuntime.StateRevision)), Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(Json(state), Is.EqualTo(before));
        }
        private static string Error(Action action)
        {
            try { action(); return "未拒绝"; }
            catch (TargetInvocationException e) { return e.InnerException.GetType().FullName; }
        }

        [Test] public void RealA08CostAndBodyRemainAtomicAndRejectTheWrongPlayer()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal(); fixture.Choose(1, "A08", "2");
            var round = new RoundExecutionService(fixture.Registry);
            Assert.That(round.PrepareActionWindowForSmoke(fixture.State, 1).IsValid, Is.True);
            var session = new GameSession(fixture.State); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry, round));
            var option = fixture.Registry.QueryActions(session.State, 1).Single(o => o.ActionId == "entrepreneur.A08.tactic.quick");
            string before = Json(session.State);
            Assert.That(session.Submit(AbilityActionCommand.Create(2, option)).Succeeded, Is.False);
            Assert.That(Json(session.State), Is.EqualTo(before));
            var command = AbilityActionCommand.Create(1, option);
            Assert.That(session.Submit(command).Succeeded, Is.True); fixture.State = session.State;
            var invocation = session.State.EffectRuntime.AbilityActionInvocations.Single(i => i.CommandId == command.CommandId);
            Assert.That(invocation.PaymentCommitted, Is.True); Assert.That(fixture.Stored, Is.Zero);
            var paid = AbilityActionExecutor.EntryPaymentNodes(session.State, invocation).Where(n => AbilityActionExecutor.HasCommittedTokenTransfer(session.State, n)).ToArray();
            Assert.That(paid.Length, Is.EqualTo(1)); Assert.That(Number(paid[0].NormalizedResult, "movedCount"), Is.EqualTo(2));
            Assert.That(session.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" &&
                session.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == r.OwnerEffectId).EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence), Is.True);
            Assert.That(session.State.EffectRuntime.InteractionRequests.Any(r => r.PromptKey == "effect.card.payment_items"), Is.False);
        }

        [Test] public void OptionalTimingListenersCannotChangeTheActionOrReplaceItsResult()
        {
            var luaField = typeof(LuaEffectEventDispatcher).GetField("CpuTimingSample");
            var validationField = typeof(ItemZoneQuery).GetField("ValidationTimingSample");
            Assert.That(luaField, Is.Not.Null); Assert.That(validationField, Is.Not.Null);
            object oldLua = luaField.GetValue(null), oldValidation = validationField.GetValue(null);
            try
            {
                luaField.SetValue(null, null); validationField.SetValue(null, null);
                using var fixture = new AgnesContentTestFixture(); fixture.Formal(); fixture.Choose(1, "A08", "2");
                var round = new RoundExecutionService(fixture.Registry);
                Assert.That(round.PrepareActionWindowForSmoke(fixture.State, 1).IsValid, Is.True);
                var option = fixture.Registry.QueryActions(fixture.State, 1).Single(o => o.ActionId == "entrepreneur.A08.tactic.quick");
                var command = AbilityActionCommand.Create(1, option);
                GameSession Session()
                {
                    var value = new GameSession(GameStateCloneService.DeepClone(fixture.State));
                    value.RegisterHandler(new InvokeSettlementOptionCommandHandler(fixture.Registry, round)); return value;
                }
                var plain = Session(); Assert.That(plain.Submit(command).Succeeded, Is.True);
                var observed = Session(); int luaCalls = 0, validationCalls = 0;
                luaField.SetValue(null, (Action<string, string, long>)((phase, handler, ticks) => {
                    luaCalls++; throw new InvalidOperationException("隔离计时监听故障"); }));
                validationField.SetValue(null, (Action<long>)(ticks => {
                    validationCalls++; throw new InvalidOperationException("隔离校验监听故障"); }));
                Assert.That(observed.Submit(command).Succeeded, Is.True);
                Assert.That(luaCalls > 0 && validationCalls > 0, Is.True);
                Assert.That(Json(observed.State), Is.EqualTo(Json(plain.State)));
            }
            finally { luaField.SetValue(null, oldLua); validationField.SetValue(null, oldValidation); }
        }
    }
}
