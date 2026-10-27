using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 技术隔离：读取当前正式内容；仅在内存装配活动快照，不启动正常模式、不变更G0、不复制输入树。
    public sealed class PageSequenceFixContractTests : MartinContentTestFixture
    {
        private static ExternalContentPack frozen;
        public override void StartMartin()
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            if (frozen == null)
            {
                var installed = ExternalContentPack.Load(Installed);
                var select = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(IEnumerable<string>), typeof(string), typeof(string) }, null);
                frozen = (ExternalContentPack)select.Invoke(installed, new object[] { new[] { "core", "enterprise", "infected" },
                    LuaContentHasher.ComputeSha256(installed.ContentHash + "|QY017.PAGE-SEQUENCE-FIX-R1.isolated"), "entrepreneur" });
            }
            Pack = frozen;
            Artifacts.WriteLabeledText("隔离输入身份", "直接读取当前正式目录；活动内存快照=" + Pack.ContentHash + "；不复制内容、不更改正式就绪状态、不计正常页面验收。");
        }

        private List<EffectSpec> Agnes(GameState state, string call, string ability = "")
        {
            var source = "local A=(function()\n" + Pack.GetScript("lua/entrepreneurs/agnes.lua") + "\nend)()\nreturn function(ctx) " + call + " end";
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 100, 3, "1.0.0"); snapshot.Round = 1;
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("abilityId", LuaSnapshotValue.String(ability)) });
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy017.isolated", "qy017", "qy017", "1.0.0", source,
                LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("qy017", "AbilityInvoked", "p1", state.EffectRuntime.StateRevision,
                "1.0.0", 3, 0, 100, snapshot));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: Pack).TryCompile(invocation,
                new LuaEffectCompilationContext { State = state, Registry = Registry, PlayerId = 1, SourceId = "qy017.isolated" },
                out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs;
        }

        private void AssertMapConsumer(GameState state, InteractionRequest request, bool array)
        {
            Assert.That(request.AllowDecline, Is.False);
            Assert.That(request.AnswerSchema, Is.EqualTo(array ? "candidate_id_array" : "candidate_id"));
            var type = Type.GetType("YC.Presentation." + (array ? "CharacterAbilityInteractionUiCoordinator" : "MapEffectInteractionUiCoordinator") + ", Assembly-CSharp", true);
            GameObject host = null;
            try
            {
                object dialog = null;
                if (array)
                {
                    host = new GameObject("隔离地图数组消费者", typeof(RectTransform));
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                    var registry = prefab.GetComponentInChildren(Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true), true);
                    dialog = Activator.CreateInstance(Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp", true),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { registry, (RectTransform)host.transform }, null);
                }
                var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type, new object[] {
                    new Func<GameState>(() => state), new Func<int>(() => 1), dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(_ => { }), new Action<string>(_ => { }) });
                Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(request, 1)), Is.True);
                Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(request, 2)), Is.False);
                renderer.Clear();
            }
            finally { if (host != null) UnityEngine.Object.DestroyImmediate(host); }
        }

        [TestCase(false)] [TestCase(true)]
        public void A08RealLuaKeepsEmptyCompletionOrMandatoryOwnMapSelection(bool target)
        {
            var state = State(); if (target) OwnInfluence(state);
            var node = Run(state, Agnes(state, "return A.a08Cleanup(ctx)").Single());
            if (!target) { Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False); return; }
            var request = Open(state); AssertMapConsumer(state, request, false);
            Assert.That(request.CandidateIds, Is.EquivalentTo(state.Map.Influences.Where(i => i.PlayerId == 1).Select(i => i.InfluenceId)));
            Answer(state, NormalizedValue.CreateString(request.CandidateIds.First()));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.Map.Influences.Count(i => i.PlayerId == 1), Is.Zero);
        }

        [Test]
        public void M13RealOverlayKeepsArrayMapStrictRemovalAndRequiredResponseBarrier()
        {
            var state = State(carrier: true); OwnInfluence(state);
            Position(state, Card(state, "reserve_002"), Cell(state, 0).CellZoneId);
            const string hold = "qy017.isolated.departure.wait";
            Registry.Register(hold, c => c.GetLatestInteractionAnswer() != null ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec { InteractionTypeId = hold,
                    AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "ack" } }), EffectExecutorKind.IntrinsicFlow);
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration(hold, "InfluenceRemoved", "*", hold,
                _ => new List<EffectSpec> { EffectSpec.Create(hold, playerId: 1) }, EffectHandlerRole.Observer));
            var build = Run(state, BuildScenario(state, Card(state, "entrepreneur.facility.additional_core_tower"), 0));
            var facts = FacilityBuildJournal.Read(build); var request = Open(state); AssertMapConsumer(state, request, true);
            Answer(state, NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(request.CandidateIds.First()) }));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(hold));
            var obligation = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == facts.ObligationEffectId);
            Assert.That(obligation.Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Answer(state, NormalizedValue.CreateString("ack"));
            Assert.That(obligation.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Number(obligation.NormalizedResult, "completedCount"), Is.EqualTo(1));
            Assert.That(state.Map.Influences.Count(i => i.PlayerId == 1), Is.Zero);
        }

        [Test]
        public void A03RealLuaHasTwoIndependentFreeMovesWithoutOptionalMoveGates()
        {
            var state = State(); var condition = Agnes(state, "return A.invoke(ctx,{})", "entrepreneur.A03.special").Single();
            Run(state, condition);
            var payment = Open(state); Assert.That(payment.CandidateIds.Any(id => id == "pay" || id == "continue"), Is.True);
            Answer(state, NormalizedValue.CreateString(payment.CandidateIds.Contains("pay") ? "pay" : "continue"));
            for (int guard = 0; guard < 4 && state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"); guard++)
            { Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.city.move.target")); Answer(state, NormalizedValue.CreateString(Open(state).CandidateIds.First())); }
            var moves = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == CityMoveEffectTypeIds.Move).ToArray();
            Assert.That(moves.Length, Is.EqualTo(2)); Assert.That(moves.All(n => !n.AllowDecline), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Where(r => r.InteractionTypeId == "action.decline_effect")
                .All(r => moves.All(m => m.EffectId != r.OwnerEffectId)), Is.True);
            Assert.That(moves.All(n => n.Status == EffectNodeStatus.Completed || n.Status == EffectNodeStatus.Failed), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void M06RealConditionDeclineOrPaidMoveStillContinuesIndependentUnderInsert(bool pay)
        {
            var state = State("entrepreneur.round.M.1", true); ActivateFace(state, "entrepreneur.round.M.1", "M06");
            var blueprint = Blueprint(state, "entrepreneur.facility.M02"); var top = Supply(state, "building_015", 0);
            Action(state, "M06"); var condition = Open(state);
            Answer(state, NormalizedValue.CreateString(pay ? (condition.CandidateIds.Contains("pay") ? "pay" : "continue") : condition.CandidateIds.First(id => id == "decline" || id == "cancel" || id == "choice.skip")));
            if (pay && Open(state).InteractionTypeId == "effect.city.move.target")
                Answer(state, NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.supply.select"));
            var moveIds = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == CityMoveEffectTypeIds.Move).Select(n => n.EffectId).ToArray();
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "action.decline_effect" && moveIds.Contains(r.OwnerEffectId)), Is.False);
            Answer(state, NormalizedValue.CreateString(top.ItemId));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == 0).ItemIds, Is.EqualTo(new[] { top.ItemId, blueprint.ItemId }));
        }

        [Test]
        public void M05SupplyUsesGenericPageAndBlueprintChoiceKeepsU19ThenAtomicExchange()
        {
            var state = State("entrepreneur.round.M.1", true); ActivateFace(state, "entrepreneur.round.M.1", "M05");
            var blueprint = Blueprint(state, "entrepreneur.facility.M03"); var top = Supply(state, "entrepreneur.facility.M03", 0);
            Action(state, "M05"); Answer(state, NormalizedValue.CreateString("place_twice"));
            UntilPrompt(state, "core:entrepreneur.M.blueprint.choose_supply");
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.supply.select"));
            Answer(state, NormalizedValue.CreateString(top.ItemId)); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("lua.choice"));
            Answer(state, NormalizedValue.CreateString(blueprint.ItemId));
            var batch = state.EffectRuntime.EffectNodes.Single(n => n.TransferBatchRequest?.OperationId.StartsWith("martin:exchange:") == true);
            Assert.That(batch.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(ItemTransferService.TryValidateBatchReceipt(batch, out var why), Is.True, why);
        }

        [Test]
        public void M09RequeriesNewTopForIndependentBottomSelection()
        {
            var state = State("entrepreneur.round.M.5", true); ActivateFace(state, "entrepreneur.round.M.5", "M09");
            var blueprint = Blueprint(state, "entrepreneur.facility.M02"); var top = Supply(state, "building_015", 0);
            Action(state, "M09"); Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.supply.select"));
            Answer(state, NormalizedValue.CreateString(top.ItemId));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.supply.select"));
            Assert.That(Open(state).CandidateIds, Does.Contain(blueprint.ItemId));
            Answer(state, NormalizedValue.CreateString(blueprint.ItemId));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == 0).ItemIds, Is.EqualTo(new[] { top.ItemId }));
        }

        [TestCase(false)] [TestCase(true)]
        public void PaidCityMovesReachRealMapTargetsWithoutAnotherMoveGate(bool martin)
        {
            var recipe = new[] { new ItemRecipeEntry { EntryId = "qy017.city", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city",
                Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" } };
            var state = martin ? State("entrepreneur.round.M.1", true, recipe) : State(extraRecipe: recipe);
            // 仅地图处理器技术隔离：提供已在地图的城市及真实拓扑的开放位置，不计正常可达性。
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-01");
            var dock = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.city_dock" && z.ParentId == location.ZoneId);
            var position = new ItemTransferRequest {
                ExecutingPlayerId = 1, ItemIds = new List<string> { city.ItemId }, RequiredCount = 1,
                ExpectedSource = new ItemPositionRef { ZoneId = city.ZoneId }, Destination = new ZoneRef { ZoneId = dock.ZoneId },
                InsertionKind = "unordered", OperationProfileId = "core.mobile_city_move" };
            // 批量设施转移只允许设施牌；城市技术初始位置使用已有单次真实物品转移。
            Registry.Register("qy017.city.position", c => {
                var result = new ItemTransferService().RelocateItems(c, position);
                var neighbor = new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap()).GetAdjacentLocations("A-01")
                    .OrderBy(l => l.LocationId, StringComparer.Ordinal).First().LocationId;
                foreach (var id in new[] { "A-01", neighbor })
                {
                    var parent = c.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == id);
                    var markers = c.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                    Assert.That(new ItemZoneStateFactory().TryCreateGrantedItem(c.State, c.Node.EffectId, id,
                        "core.resource_point", "core.resource_point_token", markers.ZoneId, new ItemSubjectRef(), out var tokenId, out var reason), Is.True, reason);
                    c.State.ItemZones.Items.Single(i => i.ItemId == tokenId).LocalState = Object(
                        "resourceType", NormalizedValue.CreateInteger((int)YC.Domain.Rules.ResourceType.Iron), "amount", NormalizedValue.CreateInteger(1));
                    if (!c.State.Map.OpenLocationIds.Contains(id)) c.State.Map.OpenLocationIds.Add(id);
                }
                return EffectStepResult.Completed(Object("movedCount", NormalizedValue.CreateInteger(result.MovedCount)));
            });
            Run(state, EffectSpec.Create("qy017.city.position", playerId: 1));
            if (martin)
            {
                ActivateFace(state, "entrepreneur.round.M.1", "M06"); Blueprint(state, "entrepreneur.facility.M02"); Supply(state, "building_015", 0);
                Action(state, "M06");
            }
            else Run(state, Agnes(state, "return A.invoke(ctx,{})", "entrepreneur.A03.special").Single());
            var payment = Open(state); Answer(state, NormalizedValue.CreateString(payment.CandidateIds.Contains("pay") ? "pay" : "continue"));
            int expected = martin ? 1 : 2;
            for (int i = 0; i < expected; i++)
            {
                var request = Open(state); Assert.That(request.InteractionTypeId, Is.EqualTo("effect.city.move.target"));
                Assert.That(request.AllowDecline, Is.False); Answer(state, NormalizedValue.CreateString(request.CandidateIds.First()));
            }
            var moves = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == CityMoveEffectTypeIds.Move).ToArray();
            Assert.That(moves.Length, Is.EqualTo(expected)); Assert.That(moves.All(n => n.Status == EffectNodeStatus.Completed), Is.True);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(martin ? 99 : 100));
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(martin ? 3 : 2));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            if (martin) Assert.That(Open(state).InteractionTypeId, Is.EqualTo("facility.supply.select"));
        }
    }
}
