using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Influence;
using YC.Domain.SpecialActions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 私有通用付款夹具及真实Host分页；不恢复R15、不伪造来源页，不冒正常UI验收。
    [NUnit.Framework.PropertyAttribute("paymentMarkerInventory", 19), NUnit.Framework.PropertyAttribute("paymentFormalMap", 1)]
    public sealed class PaymentSourceHotseatR32Tests
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value)
            ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        static void Set(object value, string name, object data)
        {
            var field = value.GetType().GetField(name, Flags);
            if (field != null) field.SetValue(value, data); else value.GetType().GetProperty(name, Flags).SetValue(value, data);
        }
        static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        PaymentTestFixture f;
        MultiSourceResourcePaymentTests sourceFixture;
        GameSession session;
        object reader, portal;
        int viewer, pageCalls;
        readonly List<object> pages = new List<object>();
        void CreateQueryPortal()
        {
            var hud = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var registry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var map = new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap());
            var influence = new InfluenceService(map);
            var options = new SpecialActionOptionQueryService(map, influence,
                new CityMovementService(map, influence, new TravelCostService(map)), new SpecialActionLifecycleService());
            portal = Activator.CreateInstance(T("SpecialActionInteractionUiCoordinator"), Flags, null, new object[] {
                new Func<GameState>(() => f.State), new Func<int>(() => 1), new Func<UnityEngine.RectTransform>(() => null), registry, options,
                new Action<IReadOnlyList<YC.Presentation.Workflows.WorkflowHighlight>>(_ => { }), new Action(() => { }),
                new Action<YC.Domain.Commands.GameCommand>(_ => throw new InvalidOperationException("只读查询不得派令")), new Action<string>(_ => { }) }, null);
            Set(portal, "GetVisibleRequests", new Func<IReadOnlyList<InteractionRequest>>(() =>
                f.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.AnsweringPlayerId == 1)
                    .Select(GameStateCloneService.DeepClone).ToList()));
        }
        void CreateReader(int originalSeat, Func<int> currentViewer)
        {
            session = new GameSession(f.State); session.ConfigurePaymentSourceQueries(f.Registry);
            reader = Activator.CreateInstance(T("CommandSubmissionController"), Flags, null,
                new object[] { session, null, originalSeat, null, (Action)(() => { }), (Action<string>)(_ => { }), (Action<string>)(_ => { }), null, null }, null);
            Set(reader, "PaymentSourceViewerPlayerId", currentViewer);
        }
        PaymentSourceQuery Query()
        {
            var request = f.Open();
            var descriptor = ItemTransferEffectExecutor.Value(request.PromptParameters, "paymentSourceQuery");
            return new PaymentSourceQuery { PlayerId = 1, OwnerEffectId = request.OwnerEffectId,
                InteractionId = request.GetStableInteractionId(), StateRevision = request.StateRevision,
                PaymentGeneration = (int)ItemTransferEffectExecutor.Number(request.PromptParameters, "paymentGeneration"),
                SnapshotId = ItemTransferEffectExecutor.Text(descriptor, "snapshotId") };
        }
        PaymentSourceProjection Page(PaymentSourceQuery query)
        {
            pageCalls++;
            var page = (PaymentSourceProjection)Call(reader, "ReadPaymentSourcePage", query);
            pages.Add(new { Query = GameStateCloneService.DeepClone(query), Page = page,
                Reason = Read(reader, "LastPaymentSourcePageFailure"), Viewer = Read(reader, "LastPaymentSourcePageViewer") });
            return page;
        }
        void PagedFixture()
        {
            // 跨夹具不能借其按方法名读取的SetUp；沿同一私有夹具的正式创建/声明入口初始化。
            f = new PaymentTestFixture(19, YC.Domain.Maps.StaticMapDefinitions.CreateThreePlayerMap());
            sourceFixture = new MultiSourceResourcePaymentTests(); Set(sourceFixture, "f", f);
            var card = f.State.ItemZones.Items.Single(i => i.DefinitionId == "test.payment.source_1" && i.ControllerSubject.PlayerId == 1);
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(f.State, card.ItemId, new CardZoneDefinition {
                LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                AcceptedTypeIds = new List<string> { "core.player_marker" }, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = "core.public_items", CarrierExitPolicyId = "core.preserve_contents"
            }, out var markerZone, out var reason), Is.True, reason);
            Set(sourceFixture, "markerZone", markerZone);
            var markerIds = f.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1)
                .OrderBy(i => i.ItemId).Take(2).Select(i => i.ItemId).ToList();
            Set(sourceFixture, "markerIds", markerIds);
            foreach (string id in markerIds) typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { f.State, f.State.ItemZones.Items.Single(i => i.ItemId == id), markerZone });
            var declarations = new List<AbilitySourceDeclaration>(); ResourcePaymentSourceService.Register(f.Registry);
            for (int i = 0; i < 2; i++)
            {
                int index = i; string handler = "test.payment.handler_" + i;
                var declaration = new AbilitySourceDeclaration { DefinitionId = "test.payment.source_" + i, AbilityId = "test.payment.ability_" + i,
                    HandlerRef = handler, DeclarationVersion = "1.0.0", Kind = "ruleQuery", AuthorizationPolicy = "controller",
                    EffectiveZones = new List<string> { FindZone(f.State, "hand", 1).TemplateId }, QueryKinds = new List<string> { ResourcePaymentSourceService.QueryKind },
                    PresentationRef = "test.payment.source_" + i };
                if (i == 1) declaration.AllowedOperations.Add("carrier.transfer.original_supply:test.payment.source_1:markers:core.player_marker");
                declarations.Add(declaration); f.Registry.RegisterAbilityHandler(handler, "test.payment.hash_" + i, _ => Array.Empty<EffectSpec>());
                f.Registry.RegisterAbilityPureHandler(handler, (call, query) => Object("queryId", NormalizedValue.CreateString(query.QueryId),
                    "basisDigest", NormalizedValue.CreateString(query.FactsDigest), "items", NormalizedValue.CreateArray(new[] {
                        (NormalizedValue)typeof(MultiSourceResourcePaymentTests).GetMethod("Rule", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { index }) })));
            }
            f.Registry.FreezeAbilityCatalog(declarations); f.State.ContentDefinitions.AbilitySources = declarations;
            var owner = (EffectNodeRuntimeState)Call(sourceFixture, "ChannelInventory", false);
            CreateQueryPortal(); viewer = 1;
            CreateReader(2, () => viewer);
            Set(portal, "QueryPaymentSources", new Func<PaymentSourceQuery, PaymentSourceProjection>(Page));
        }
        bool Complete(out PaymentSourceProjection projection)
        {
            var args = new object[] { f.Open().GetStableInteractionId(), null };
            bool result = (bool)portal.GetType().GetMethod("TryGetPaymentSources", Flags).Invoke(portal, args);
            projection = (PaymentSourceProjection)args[1]; return result;
        }
        [TestCase(2), TestCase(4)]
        public void CurrentHotseatViewerReadsRealEmptySourcePageAndNormalPaymentCanComplete(int originalSeat)
        {
            f = new PaymentTestFixture();
            var owner = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(1, new ResourceSet { GoldVoucher = 6 }), 1));
            viewer = originalSeat; CreateReader(originalSeat, () => viewer); var query = Query(); string before = RuleStateDigestService.Compute(f.State);
            Assert.That(Page(query), Is.Null, "付款回答者尚不是本地可见玩家时，不能借热座越过身份");
            viewer = 1;
            var page = Page(query);
            Assert.That(page, Is.Not.Null); Assert.That(page.SourceCount, Is.Zero); Assert.That(page.Sources, Is.Empty);
            Assert.That(page.NextSourceIndex, Is.EqualTo(-1)); Assert.That(Read(reader, "LastPaymentSourcePageFailure"), Is.Null);
            sourceFixture = new MultiSourceResourcePaymentTests(); Set(sourceFixture, "f", f);
            CreateQueryPortal();
            Set(portal, "QueryPaymentSources", new Func<PaymentSourceQuery, PaymentSourceProjection>(Page));
            Assert.That(Complete(out var empty), Is.True, "原协调器须接受真实Host的合法空来源终页");
            Assert.That(empty.SourceCount, Is.Zero); Assert.That(empty.Sources, Is.Empty);
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before), "查询不修改规则状态");
            var resources = f.State.FindPlayer(1).Resources.Clone();
            f.Answer(f.Open().CandidateIds.First());
            Assert.That(f.Engine.GetNode(owner.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(resources.GoldVoucher - 6));
            Assert.That(f.State.FindPlayer(1).Resources.Originium + f.State.FindPlayer(1).Resources.OriginiumShard + f.State.FindPlayer(1).Resources.Iron,
                Is.EqualTo(resources.Originium + resources.OriginiumShard + resources.Iron - 1));
            f.Artifacts.WriteLabeledText("本地热座空来源与真实付款结清", JsonConvert.SerializeObject(new { originalSeat, viewer, pages, IsolatedGenericFixture = true, OriginalR15Replayed = false }));
        }
        [Test] public void NetworkFixedSeatDoesNotUseLocalHotseatViewerCallback()
        {
            f = new PaymentTestFixture();
            f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(1, new ResourceSet { GoldVoucher = 6 }), 1));
            int callbackCalls = 0; CreateReader(2, () => { callbackCalls++; return 1; });
            Set(reader, "usesDeterministicSimulation", true); // 私有适配器门禁，未伪造网络响应或Host成功。
            Assert.That(Page(Query()), Is.Null); Assert.That(callbackCalls, Is.Zero);
            Assert.That(Read(reader, "LastPaymentSourcePageFailure"), Is.EqualTo("payment_source_network_viewer_mismatch"));
            f.Artifacts.WriteLabeledText("联网固定席位保持", JsonConvert.SerializeObject(new { callbackCalls, pages }));
        }
        [Test] public void RealHostPagesKeepEveryItemAfterHotseatSwitch()
        {
            PagedFixture(); string before = RuleStateDigestService.Compute(f.State);
            Assert.That(Complete(out var projection), Is.True);
            Assert.That(projection.SourceCount, Is.EqualTo(2)); Assert.That(pageCalls, Is.EqualTo(6));
            var items = projection.Sources.Single(r => r.Rule.RuleId == "test.rule_1").AvailableItems;
            Assert.That(items.Count, Is.EqualTo(19)); Assert.That(items.Select(i => i.ItemId).Distinct().Count(), Is.EqualTo(19));
            Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("真实Host完整分页", JsonConvert.SerializeObject(new { pages, projection }));
        }
        [Test] public void MissingLaterPageNeverPublishesPartialSources()
        {
            PagedFixture();
            Set(portal, "QueryPaymentSources", new Func<PaymentSourceQuery, PaymentSourceProjection>(q => q.ItemOffset > 0 ? null : Page(q)));
            Assert.That(Complete(out var projection), Is.False); Assert.That(projection, Is.Null);
            Assert.That(Read(portal, "LastPaymentSourceQueryFailure"), Is.EqualTo("payment_source_page_missing"));
            f.Artifacts.WriteLabeledText("丢页仍拒绝", JsonConvert.SerializeObject(new { pages, PartialProjectionPublished = false }));
        }
        [Test] public void StaleQueryWrongViewerAndWrongOwnerStayRejected()
        {
            PagedFixture(); var query = Query(); var stale = GameStateCloneService.DeepClone(query); stale.StateRevision--;
            Assert.That(Page(stale), Is.Null);
            var wrongOwner = GameStateCloneService.DeepClone(query); wrongOwner.OwnerEffectId = "r32.private.missing.owner";
            Assert.That(Page(wrongOwner), Is.Null);
            viewer = 2; Assert.That(Complete(out var projection), Is.False); Assert.That(projection, Is.Null);
            Assert.That(Read(reader, "LastPaymentSourcePageFailure"), Is.EqualTo("payment_source_local_viewer_mismatch"));
            f.Artifacts.WriteLabeledText("旧修订错主体错owner拒绝", JsonConvert.SerializeObject(pages));
        }
        [TearDown] public void Release()
        {
            try
            {
                if (reader != null) Call(reader, "Dispose");
                if (portal is IDisposable disposable) disposable.Dispose();
                if (sourceFixture != null) sourceFixture.TearDown();
                else f?.Dispose();
            }
            finally { reader = null; portal = null; sourceFixture = null; f = null; pages.Clear(); pageCalls = 0; }
        }
    }
}
