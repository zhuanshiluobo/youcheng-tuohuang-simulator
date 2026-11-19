using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class MultiSourceResourcePaymentTests
    {
        private List<(PaymentSourceQuery Query, PaymentSourcePageResponse Response)> ChannelPages(GameSession session, EffectNodeRuntimeState owner)
        {
            var q = CurrentQuery(owner); var result = new List<(PaymentSourceQuery, PaymentSourcePageResponse)>();
            do
            {
                var request = new PaymentSourcePageRequest { RequestId = PaymentSourcePageProtocol.Id(q), Query = GameStateCloneService.DeepClone(q) };
                var response = session.QueryPaymentSourcePage(1, request);
                Assert.That(response.Reason, Is.Empty); Assert.That(response.Page, Is.Not.Null);
                result.Add((GameStateCloneService.DeepClone(q), response));
                if (response.Page.NextSourceIndex == -1) break;
                q.SourceIndex = response.Page.NextSourceIndex; q.ItemOffset = response.Page.NextItemOffset;
            } while (result.Count < 1000);
            Assert.That(result.Last().Item2.Page.NextSourceIndex, Is.EqualTo(-1)); return result;
        }
        private EffectNodeRuntimeState ChannelInventory(bool equivalent = false)
        {
            // 隔离查询输入：默认19个不同事实组；等价分支保持相同事实，用于分页读齐后自动选足量。
            var items = f.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f.State, items[i], markerZone });
                items[i].LocalState = Object("amount", NormalizedValue.CreateInteger(equivalent ? 0 : i));
            }
            return OpenTotalPayment();
        }
        [Test, Property("paymentMarkerInventory", 19), Property("paymentFormalMap", 1)]
        public void ChannelSessionUsesAuthenticatedIdentityAndReadsEveryTypedPageWithoutMutatingState()
        {
            var owner = ChannelInventory(); var session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            string before = JsonConvert.SerializeObject(f.State); var pages = ChannelPages(session, owner);
            var inventory = pages.SelectMany(p => p.Response.Page.Sources).Where(r => r.Rule.RuleId == "test.rule_1").SelectMany(r => r.AvailableItems).ToList();
            Assert.That(inventory.Count, Is.EqualTo(19)); Assert.That(inventory.Select(i => i.ItemId).Distinct().Count(), Is.EqualTo(19));
            Assert.That(inventory.Select(i => i.SelectionGroupId).Distinct().Count(), Is.EqualTo(19));
            Assert.That(pages.Count, Is.EqualTo(6)); Assert.That(pages.All(p => p.Response.Page.Sources.Count == 1 && p.Response.Page.Sources[0].AvailableItems.Count <= 4), Is.True);
            var q = pages[0].Query; var request = new PaymentSourcePageRequest { RequestId = PaymentSourcePageProtocol.Id(q), Query = q };
            Assert.That(session.QueryPaymentSourcePage(2, request).Page, Is.Null);
            request.Query = GameStateCloneService.DeepClone(q); request.Query.PlayerId = 2; request.RequestId = PaymentSourcePageProtocol.Id(request.Query);
            Assert.That(session.QueryPaymentSourcePage(2, request).Page, Is.Null);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            Assert.That(bindings.TryBindConnection(22, 222, out _), Is.True);
            var publisher = new PrivatePlayerViewPublisher(session, f.Registry, bindings, 1);
            Assert.That(publisher.TryBind(11, "seat1"), Is.True);
            Assert.That(publisher.TryBind(22, "seat2"), Is.True);
            request.Query = GameStateCloneService.DeepClone(q); request.RequestId = PaymentSourcePageProtocol.Id(request.Query);
            Assert.That(publisher.TryQueryPaymentSources(11, "seat1", request, out var own), Is.True);
            Assert.That(own.Page, Is.Not.Null);
            Assert.That(publisher.TryQueryPaymentSources(22, "seat2", request, out var other), Is.True);
            Assert.That(other.Page, Is.Null);
            Assert.That(publisher.TryQueryPaymentSources(11, "expired", request, out _), Is.False);
            inventory[0].LocalState = Object("clientOnly", NormalizedValue.CreateBoolean(true));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("认证会话完整分页", JsonConvert.SerializeObject(pages.Select(p => new { p.Query, p.Response })));
        }
        [Test, Property("paymentMarkerInventory", 19)]
        public void ChannelCacheAcceptsSameSnapshotOutOfOrderButNeverInventsMissingPages()
        {
            var owner = ChannelInventory(); var session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            var pages = ChannelPages(session, owner); var cache = new PaymentSourcePageCache(); const string scope = "game/epoch1/member1/connection1";
            foreach (var p in pages) Assert.That(cache.Begin(scope, p.Query), Is.True);
            foreach (var p in pages.Skip(1).Reverse()) Assert.That(cache.Accept(scope, p.Response), Is.True);
            Assert.That(cache.Read(scope, pages[0].Query), Is.Null, "缺首页不能假装来源为空");
            Assert.That(cache.Accept(scope, pages[0].Response), Is.True);
            Assert.That(cache.Accept(scope, pages[0].Response), Is.False, "非pending重复页拒绝");
            foreach (var p in pages) Assert.That(cache.Read(scope, p.Query), Is.Not.Null);
            var clone = cache.Read(scope, pages[2].Query); clone.Sources.Clear(); Assert.That(cache.Read(scope, pages[2].Query).Sources.Count, Is.EqualTo(1));
            f.Artifacts.WriteLabeledText("乱序完整快照及缺页保持等待", JsonConvert.SerializeObject(pages.Select(p => p.Query)));
        }

        [Test, Property("paymentMarkerInventory", 19), Property("paymentFormalMap", 1)]
        public void PrivatePaymentWireTransfersAllPagesOnlyToTheirAuthenticatedConnection()
        {
            var owner = ChannelInventory(); var session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            string game = f.State.GameId, content = f.State.ContentPackHash, before = JsonConvert.SerializeObject(f.State);
            var pages = ChannelPages(session, owner);
            var client = new PrivateClientPaymentChannel(game, content, "seat-one", 1, 1, 1);
            var requests = new PrivatePayloadAssembler(game, content, "seat-one", 1, 1, 1, "payment-query");
            long resultId = 0;
            foreach (var expected in pages)
            {
                Assert.That(client.Read(expected.Query), Is.Null);
                byte[] encoded = null;
                while (client.TryTakeRequest(out var part))
                {
                    var forged = GameStateCloneService.DeepClone(part); forged.ConnectionToken = "seat-two";
                    Assert.That(requests.Receive(forged, out _, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Rejected));
                    var status = requests.Receive(part, out var ack, out var completed);
                    Assert.That(status, Is.Not.EqualTo(PrivatePayloadReceiveStatus.Rejected));
                    Assert.That(client.Acknowledge(ack), Is.True);
                    if (completed != null) encoded = completed;
                }
                Assert.That(encoded, Is.Not.Null);
                string json = System.Text.Encoding.UTF8.GetString(encoded);
                Assert.That(PrivatePaymentWireProtocol.TryDecodeRequest(json, out var envelope), Is.True);
                Assert.That(PrivatePaymentWireProtocol.TryDecodeRequest("{\"ProtocolVersion\":1," + json.Substring(1), out _), Is.False);
                Assert.That(envelope.PlayerId, Is.EqualTo(1));
                var actual = session.QueryPaymentSourcePage(1, envelope.Request);
                var wrongPlayer = GameStateCloneService.DeepClone(envelope.Request); wrongPlayer.Query.PlayerId = 2;
                wrongPlayer.RequestId = PaymentSourcePageProtocol.Id(wrongPlayer.Query);
                Assert.That(session.QueryPaymentSourcePage(1, wrongPlayer).Page, Is.Null);
                var response = new PrivatePaymentResponseEnvelope { GameId = game, ContentPackHash = content, ConnectionToken = "seat-one",
                    SessionEpoch = 1, MembershipVersion = 1, PlayerId = 1, Response = actual };
                var sender = new PrivatePayloadSendWindow(game, content, "seat-one", 1, 1, 1, "payment-result", ++resultId,
                    PrivatePaymentWireProtocol.Encode(response));
                bool changed = false;
                while (sender.TryTake(out var part))
                {
                    var forged = GameStateCloneService.DeepClone(part); forged.ConnectionToken = "seat-two";
                    Assert.That(client.Receive(forged, out _, out _), Is.False);
                    Assert.That(client.Receive(part, out var ack, out bool complete), Is.True, client.Failure);
                    Assert.That(sender.TryAcknowledge(ack), Is.True);
                    changed |= complete;
                }
                Assert.That(changed, Is.True);
                var page = client.Read(expected.Query);
                Assert.That(page, Is.Not.Null, client.Failure);
                Assert.That(JsonConvert.SerializeObject(page), Is.EqualTo(JsonConvert.SerializeObject(expected.Response.Page)));
            }
            Assert.That(pages.Count, Is.EqualTo(6));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
        }
        [Test, Property("paymentMarkerInventory", 19)]
        public void ChannelCacheRejectsDisconnectedOldScopeAndOldGenerationEvenIfResponsesArriveLate()
        {
            var owner = ChannelInventory(); var session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            var page = ChannelPages(session, owner)[0]; var cache = new PaymentSourcePageCache();
            Assert.That(cache.Begin("connection-old", page.Query), Is.True); cache.Reset();
            Assert.That(cache.Begin("connection-new", page.Query), Is.True);
            Assert.That(cache.Accept("connection-old", page.Response), Is.False);
            Assert.That(cache.Read("connection-new", page.Query), Is.Null);
            var next = GameStateCloneService.DeepClone(page.Query); next.PaymentGeneration++;
            Assert.That(cache.Begin("connection-new", next), Is.True);
            Assert.That(cache.Accept("connection-new", page.Response), Is.False);
            Assert.That(cache.Read("connection-new", next), Is.Null);
            var wrong = GameStateCloneService.DeepClone(page.Response); wrong.Query = next; wrong.RequestId = PaymentSourcePageProtocol.Id(next);
            Assert.That(cache.Accept("connection-new", wrong), Is.False, "内外代数不符不能安装");
            Assert.That(cache.Read("connection-new", next), Is.Null);
            f.Artifacts.WriteLabeledText("断线代次及陈旧页拒绝", JsonConvert.SerializeObject(new { oldQuery = page.Query, next }));
        }
        [Test, Property("paymentMarkerInventory", 19), Property("paymentFormalMap", 1)]
        public void ChannelBackgroundReadRunsOnTheActualSessionOwnerAndPublishesReplyOnMainThread()
        {
            var owner = ChannelInventory(); var session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            var request = new PaymentSourcePageRequest { Query = CurrentQuery(owner) }; request.RequestId = PaymentSourcePageProtocol.Id(request.Query);
            string before = JsonConvert.SerializeObject(f.State); int mainThread = Thread.CurrentThread.ManagedThreadId;
            var coordinator = new DeterministicSimulationCoordinator(session, DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1, 2 }));
            coordinator.StartBackgroundExecution(); PaymentSourcePageResponse reply = null; int replyThread = -1;
            try
            {
                Assert.Throws<InvalidOperationException>(() => session.QueryPaymentSourcePage(1, request));
                Assert.That(coordinator.TryQueryPaymentSourcePage(1, request, r => { reply = r; replyThread = Thread.CurrentThread.ManagedThreadId; }), Is.True);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (reply == null && DateTime.UtcNow < deadline)
                {
                    coordinator.DrainMainThreadNotifications(32);
                    if (coordinator.BackgroundExecutionException != null) throw coordinator.BackgroundExecutionException;
                    Thread.Sleep(1);
                }
                Assert.That(reply, Is.Not.Null); Assert.That(reply.Reason, Is.Empty); Assert.That(replyThread, Is.EqualTo(mainThread));
                Assert.That(reply.Page.OwnerEffectId, Is.EqualTo(owner.EffectId));
                f.Artifacts.WriteLabeledText("真实后台所有者分页回调", JsonConvert.SerializeObject(new { reply, mainThread, replyThread }));
            }
            finally
            {
                coordinator.RequestStop(); Assert.That(coordinator.ExecutionStopped.Wait(TimeSpan.FromSeconds(15)), Is.True);
            }
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "只读工作不改变付款或提交序列");
        }
    }
}
