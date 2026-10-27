using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CleanupSourceContractTests
    {
        private IsolatedTestArtifacts artifacts;
        private CleanupSourceTestFixture f;
        [SetUp] public void Begin() { f = null; artifacts = IsolatedTestArtifacts.BeginCase(); }
        [TearDown] public void Finish() { f?.Snapshot("008合同最终状态"); artifacts.Finish(); }
        private void ConfirmCondition()
        {
            if (f.Open.CandidateIds.Contains("continue")) f.Answer("continue");
        }

        private SettlementEntryRuntimeState PrimaryEntry => f.Window.Entries.Single(e => e.CleanupSource != null && e.SourceItemId == f.CardId);
        private CleanupSourceRequestRef Request => f.Registry.GetCleanupSourceRequest(f.State, f.Window, PrimaryEntry);
        private NormalizedValue Facts => CleanupSourceLuaCatalog.CreateFacts(f.State, f.Window, PrimaryEntry);
        private static int FormalMarkerSupply()
        {
            var recipe = new GameSetupRecipe(); recipe.AddAccessories();
            return recipe.Entries.Single(e => e.EntryId == "player_markers").Quantity;
        }
        private static int Nodes(NormalizedValue value) => 1 + (value.Kind == NormalizedValueKind.Array ? value.Items.Sum(Nodes) :
            value.Kind == NormalizedValueKind.Object ? value.Properties.Sum(p => Nodes(p.Value)) : 0);
        private void AssertCompactInventory(int count)
        {
            var facts = Facts;
            Assert.That(facts.IsValid(), Is.True); Assert.That(Nodes(facts), Is.LessThan(64));
            Assert.That(Number(Value(facts, "source"), "transferableTokenCount"), Is.EqualTo(count));
            Assert.That(Text(Value(facts, "cleanup"), "relatedFactsDigest"), Does.Match("^[0-9a-f]{64}$"));
            Assert.That(Request.FactsDigest, Does.Match("^[0-9a-f]{64}$"));
        }

        [Test] public void TwentyMarkersQueryAndStartWithoutExceedingTheNormalizedBudget()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 20, markerSupply: FormalMarkerSupply());
            AssertCompactInventory(20); Assert.That(f.Options.Single().IsEnabled, Is.True);
            f.Start("twenty"); ConfirmCondition(); f.Answer("resource");
            Assert.That(f.Stored, Is.EqualTo(17)); Assert.That(f.Source.Invocations.Count, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
        }

        [Test] public void TheEntireFormalPlayerMarkerSupplyCanBeQueriedAndRecovered()
        {
            int maximum = FormalMarkerSupply(); Assert.That(maximum, Is.GreaterThanOrEqualTo(29));
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: maximum, markerSupply: maximum);
            Assert.That(f.Stored, Is.EqualTo(maximum)); AssertCompactInventory(maximum);
            string digest = Request.FactsDigest; f.Restore("008正式普通标记全供应量查询");
            Assert.That(Request.FactsDigest, Is.EqualTo(digest));
            f.Start("full-supply"); ConfirmCondition(); f.Answer("resource"); Assert.That(f.Stored, Is.EqualTo(maximum - 3));
        }

        [Test] public void MultipleAuthorizedCarriersAndMixedTypesRetainEveryInventoryFact()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 20, markerSupply: FormalMarkerSupply(), additionalCarriers: 1, mixedMarkers: true);
            var second = f.State.ItemZones.Zones.Single(z => z.ParentKind == ZoneParentKind.Item && z.LocalZoneId == "markers" &&
                z.ParentId != f.CardId && f.State.ItemZones.Items.Any(i => i.ItemId == z.ParentId && i.DefinitionId == CleanupSourceTestFixture.Definition && i.ControllerSubject.PlayerId == 1));
            foreach (var item in f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Take(10)) item.ZoneId = second.ZoneId;
            var gangs = f.State.ItemZones.Items.Where(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.PlayerId == 1).ToList();
            for (int i = 0; i < gangs.Count; i++) gangs[i].ZoneId = i % 2 == 0 ? f.Storage.ZoneId : second.ZoneId;
            // 隔离通用载体输入：每种类型均由私有内容显式许可，并非修改T09正式接纳范围。
            Assert.That(gangs.Count, Is.EqualTo(4)); AssertCompactInventory(24);
            Assert.That(Number(Value(Facts, "source"), "storageZoneCount"), Is.EqualTo(2));
            var old = Request; gangs.Last().LocalState = Object("tag", NormalizedValue.CreateString("最后一枚也必须参与摘要"));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "last-mixed-item", old, out _, out _, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("cleanup_source_request_stale")); Assert.That(f.Source.Invocations, Is.Empty);
        }

        [Test] public void CanonicalFactsRespectLocalStateLimitsAndIgnoreUnorderedCollectionOrder()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: FormalMarkerSupply(), mixedMarkers: true);
            var markers = f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).ToList();
            markers[0].LocalState = NormalizedValue.CreateArray(Enumerable.Range(0, NormalizedValue.MaxElements - 1)
                .Select(_ => NormalizedValue.CreateInteger(NormalizedValue.MaxInteger)).ToList());
            markers[1].LocalState = NormalizedValue.CreateString(new string('x', NormalizedValue.MaxStringBytes));
            markers[2].LocalState = Object("b", NormalizedValue.CreateString("汉字|分隔:换行\n\"\\"), "a", NormalizedValue.CreateInteger(1));
            Assert.That(Nodes(markers[0].LocalState), Is.EqualTo(NormalizedValue.MaxElements));
            Assert.That(markers.Take(3).All(i => i.LocalState.IsValid()), Is.True);
            AssertCompactInventory(FormalMarkerSupply()); string before = Request.FactsDigest;
            f.State.ItemZones.Items.Reverse(); f.State.ItemZones.Zones.Reverse();
            f.State.ContentDefinitions.AbilitySources.Single(d => d.AbilityId == CleanupSourceTestFixture.Ability).AllowedOperations.Reverse();
            markers[2].LocalState = Object("a", NormalizedValue.CreateInteger(1), "b", NormalizedValue.CreateString("汉字|分隔:换行\n\"\\"));
            Assert.That(Request.FactsDigest, Is.EqualTo(before));
            markers[0].LocalState.Items[NormalizedValue.MaxElements - 2] = NormalizedValue.CreateInteger(0);
            Assert.That(Request.FactsDigest, Is.Not.EqualTo(before), "不能丢弃合法LocalState的末项。");
            markers[3].LocalState = NormalizedValue.CreateString("\u00e9"); string composed = Request.FactsDigest;
            markers[3].LocalState = NormalizedValue.CreateString("e\u0301");
            Assert.That(Request.FactsDigest, Is.Not.EqualTo(composed), "沿原归一化值精确保留码位，不额外作Unicode合并。");
        }

        [TestCase("replace")]
        [TestCase("move")]
        [TestCase("type")]
        [TestCase("originalSupply")]
        [TestCase("state")]
        [TestCase("owner")]
        [TestCase("controller")]
        [TestCase("carrierZone")]
        [TestCase("configuration")]
        [TestCase("permission")]
        [TestCase("version")]
        [TestCase("activeFace")]
        public void SameCountRelevantChangesRejectTheOldRequestWithoutAnInvocation(string change)
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 20, markerSupply: FormalMarkerSupply(), additionalCarriers: 1, mixedMarkers: true);
            var request = Request; var marker = f.State.ItemZones.Items.Last(i => i.ZoneId == f.Storage.ZoneId);
            var card = f.State.ItemZones.Items.Single(i => i.ItemId == f.CardId);
            var declaration = f.State.ContentDefinitions.AbilitySources.Single(d => d.AbilityId == CleanupSourceTestFixture.Ability);
            int stored = f.State.ItemZones.Items.Count(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentKind == ZoneParentKind.Item && z.LocalZoneId == "markers"));
            // 明确标记的失效负例，仅修改本用例私有状态；各项保持牌区总物品数不变。
            switch (change)
            {
                case "replace":
                    var spare = f.State.ItemZones.Items.First(i => i.TypeId == marker.TypeId && i.OwnerSubject.PlayerId == 1 && i.ZoneId == marker.OriginalSupplyZoneId);
                    spare.ZoneId = marker.ZoneId; marker.ZoneId = marker.OriginalSupplyZoneId; break;
                case "move": marker.ZoneId = f.State.ItemZones.Zones.First(z => z.ParentId != f.CardId && z.LocalZoneId == "markers" &&
                    f.State.ItemZones.Items.Any(i => i.ItemId == z.ParentId && i.ControllerSubject.PlayerId == 1)).ZoneId; break;
                case "type": marker.TypeId = "entrepreneur.gang_marker"; break;
                case "originalSupply": marker.OriginalSupplyZoneId = f.State.ItemZones.Items.First(i => i.TypeId == marker.TypeId && i.OwnerSubject.PlayerId == 2).OriginalSupplyZoneId; break;
                case "state": marker.LocalState = Object("declared", NormalizedValue.CreateBoolean(true)); break;
                case "owner": marker.OwnerSubject = ItemSubjectRef.Player(2); break;
                case "controller": card.ControllerSubject = ItemSubjectRef.Player(2); break;
                case "carrierZone": card.ZoneId = f.State.ItemZones.Zones.First(z => z.TemplateId == "core.zone.box_retired").ZoneId; break;
                case "configuration": f.Storage.Configuration.AcceptedTypeIds.Remove("core.player_marker"); break;
                case "permission": declaration.AllowedOperations.RemoveAll(p => p.EndsWith(":core.player_marker", StringComparison.Ordinal)); break;
                case "version": declaration.DeclarationVersion = "1.0.1"; break;
                case "activeFace": declaration.ActiveFaceId = "qy008.inactive.face"; break;
            }
            Assert.That(f.State.ItemZones.Items.Count(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentKind == ZoneParentKind.Item && z.LocalZoneId == "markers")), Is.EqualTo(stored));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "stale-" + change, request, out _, out _, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("cleanup_source_request_stale")); Assert.That(f.Source.Invocations, Is.Empty);
        }

        [Test] public void UnrelatedForeignInventoryDoesNotStaleTheLargeInventoryRequest()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 20, markerSupply: FormalMarkerSupply()); var request = Request;
            var foreignCard = f.State.ItemZones.Items.Single(i => i.DefinitionId == CleanupSourceTestFixture.Definition && i.ControllerSubject.PlayerId == 2);
            var foreignZone = f.State.ItemZones.Zones.Single(z => z.ParentId == foreignCard.ItemId && z.LocalZoneId == "markers");
            foreach (var marker in f.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 2)) marker.ZoneId = foreignZone.ZoneId;
            f.State.FindPlayer(2).Resources.GoldVoucher += 1;
            Assert.That(Request.FactsDigest, Is.EqualTo(request.FactsDigest));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "foreign-facts-unchanged", request, out _, out _, out string reason), Is.True, reason);
        }

        [Test] public void SixMarkersAllowTwoSeparateDifferentRewardsAndNeverAutoStartThird()
        {
            f = new CleanupSourceTestFixture(artifacts); string source = f.Options.Single().OptionId;
            f.Start("first"); ConfirmCondition(); Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.Engine.TrySubmitInteraction(f.Open.GetStableInteractionId(), 1, f.Open.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            f.Answer("resource"); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(f.Options.Single().OptionId, Is.EqualTo(source)); Assert.That(f.Options.Single().IsEnabled, Is.True);
            f.Start("second"); ConfirmCondition(); f.Answer("place");
            for (int i = 0; i < 12 && f.Source.ActiveInvocationId.Length > 0; i++) f.Answer(f.Open.CandidateIds.First());
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(1));
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(2)); Assert.That(f.Source.Invocations.Select(i => i.InvocationId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(f.Options.Single().OptionId, Is.EqualTo(source)); Assert.That(f.Options.Single().IsEnabled, Is.False);
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "PlayerCleanupStarted" && e.PlayerId == 1), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Any(u => u.AbilityId == CleanupSourceTestFixture.Ability), Is.False);
        }

        [Test] public void DuplicateCommandReplaysButAnotherCommandCannotReuseConsumedRequest()
        {
            f = new CleanupSourceTestFixture(artifacts); var request = f.Options.Single().CleanupRequest;
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "one", request, out bool replay, out string id, out string reason), Is.True, reason);
            Assert.That(replay, Is.False); f.Drain();
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "one", request, out replay, out var repeated, out reason), Is.True, reason);
            Assert.That(replay, Is.True); Assert.That(repeated, Is.EqualTo(id));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "another", request, out _, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("cleanup_source_request_consumed"));
            Assert.That(f.Engine.TryInvokeCleanupSource(2, "foreign", request, out _, out _, out _), Is.False);
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(1));
            ConfirmCondition(); f.Answer("resource");
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "one", request, out replay, out _, out reason), Is.True, reason);
            Assert.That(replay, Is.True); Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(1));
        }

        [Test] public void DecliningPaymentEndsOnlyThisAttemptAndNextClickCanRetry()
        {
            f = new CleanupSourceTestFixture(artifacts); f.Start("decline");
            var request = f.Open;
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason);
            f.Drain(); Assert.That(f.Stored, Is.EqualTo(6)); Assert.That(f.Window.IsClosed, Is.False);
            Assert.That(f.Options.Single().IsEnabled, Is.True); Assert.That(f.Source.Invocations.Single().Outcome, Is.EqualTo("declined"));
            f.Start("retry"); ConfirmCondition(); f.Answer("resource");
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(2)); Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
        }

        [Test] public void UnstartedUnavailableSourceDoesNotBlockClosingAndOldButtonCannotReopen()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 0); var request = f.Options.Single().CleanupRequest;
            Assert.That(f.Options.Single().IsEnabled, Is.False);
            Assert.That(f.Engine.TryEndSettlementAction(1, f.Window.Context.Id, "", "", f.State.EffectRuntime.StateRevision, "close", out string reason), Is.True, reason);
            f.Drain(); Assert.That(f.Window.IsClosed, Is.True); Assert.That(f.Source.Sealed, Is.True);
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "late-click", request, out _, out _, out _), Is.False);
            Assert.That(f.Source.Invocations, Is.Empty); Assert.That(f.Options, Is.Empty);
        }

        [Test] public void QueriesArePureAndUnrelatedPlayerResourcesDoNotStaleTheRequest()
        {
            f = new CleanupSourceTestFixture(artifacts); var first = f.Options.Single().CleanupRequest;
            string before = YC.Domain.State.RuleStateDigestService.Compute(f.State);
            var second = f.Options.Single().CleanupRequest;
            Assert.That(YC.Domain.State.RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
            Assert.That(second.InteractionId, Is.EqualTo(first.InteractionId));
            f.State.FindPlayer(2).Resources.GoldVoucher += 1; // 隔离模拟不相关的权威提交；不冒充正常局。
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "still-current", first, out _, out _, out string reason), Is.True, reason);
        }

        [Test] public void ChangedInventoryRejectsOldFactsWithoutAllocatingAnInvocation()
        {
            f = new CleanupSourceTestFixture(artifacts); var request = f.Options.Single().CleanupRequest;
            var marker = f.State.ItemZones.Items.First(i => i.ZoneId == f.Storage.ZoneId);
            marker.ZoneId = marker.OriginalSupplyZoneId; // 清楚标记的库存失效负例。
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "stale", request, out _, out _, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("cleanup_source_request_stale")); Assert.That(f.Source.Invocations, Is.Empty);
        }

        [Test] public void ExchangeReturnsToListAndAnotherFormalCleanupCanReplenishBeforeNextExchange()
        {
            const string body = @"return function(ctx) return {Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
rightEffects={Effect.GainScore({player=ctx.playerId,amount=1})}})} end";
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 3, bodyOverride: body, replenish: true);
            string entry = f.Options.Single(o => o.CleanupRequest != null).OptionId;
            f.Start("exchange-before"); f.Answer("continue");
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(f.Options.Single(o => o.OptionId == entry).IsEnabled, Is.False);
            var supply = f.Options.Single(o => o.SourceDefinitionId == "infected.award.score");
            Assert.That(f.Engine.TryInvokeCharacterSettlementOption(1, supply.OptionId, "", supply.StateRevision, out _, out string reason), Is.True, reason);
            f.Drain(); Assert.That(f.Stored, Is.EqualTo(3));
            Assert.That(f.Options.Single(o => o.OptionId == entry).IsEnabled, Is.True);
            f.Restore("008兑换后正式收尾补足");
            f.Start("exchange-after"); f.Answer("continue");
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(2));
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RealDepartureCustodyAndRequiredResponseKeepTheInvocationAndWindowOpen(bool concurrent)
        {
            const string body = @"return function(ctx) return {Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
rightEffects={Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=1,requiredCount=1})}})} end";
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 3, bodyOverride: body, storageResponse: true, concurrent: concurrent);
            var map = YC.Domain.Maps.StaticMapDefinitions.Resolve(f.State.MapId);
            string slot = YC.Domain.Influence.InfluenceService.GetLocationSlotId(map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            f.Engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slot, itemTypeId: "core.player_marker")); f.Drain();
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(1));
            f.Start("departure"); f.Answer("continue"); f.Answer(f.Open.CandidateIds.First());
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Single().Status, Is.EqualTo("open"));
            Assert.That(f.State.EffectRuntime.DeferredResponses.Single().IsBypass, Is.EqualTo(concurrent));
            Assert.That(f.Source.ActiveInvocationId, Is.Not.Empty);
            Assert.That(CleanupSourceDependencies.Instance.HasPendingForWindow(f.State, f.RootId, out _), Is.True);
            Assert.That(f.Engine.TryEndSettlementAction(1, f.Window.Context.Id, "", "", f.State.EffectRuntime.StateRevision, "too-early", out _), Is.False);
            f.Restore("008实际离场保管待答");
            Assert.That(f.Source.ActiveInvocationId, Is.Not.Empty); f.Answer("store");
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Single().Status, Is.Not.EqualTo("open"));
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(f.Source.ActiveInvocationId, Is.Empty);
            Assert.That(CleanupSourceDependencies.Instance.HasPendingForWindow(f.State, f.RootId, out _), Is.False);
            f.Restore("008实际收容完成回列表");
        }

        [Test] public void FormalSessionAndWireCommandUseTheCompleteU15RequestAndRejectExtraFields()
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers:3);
            var session = new GameSession(f.State); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            var option = session.ProjectFor(GameStateViewer.Player(1)).CharacterSettlementOptions.Single(o=>o.CleanupRequest!=null && o.SourceItemId==f.CardId);
            var cmd = CleanupSourceCommand.Create(1,"qy007.u15.formal",option.CleanupRequest,f.State.EffectRuntime.StateRevision);
            var dto=GameCommandDto.FromCommand(cmd); var roundTrip=dto.ToCommand();
            Assert.That(CleanupSourceCommand.TryRead(roundTrip,out _,out _),Is.True);
            OperationWindowService.Synchronize(f.State);
            Assert.That(OperationWindowService.TryResolveCommandContext(f.State,roundTrip,out var cause,out var validation),Is.True,validation?.Reason);
            var proposal=new OperationCommandProposalDto {GameId=f.State.GameId,SimulationEpoch=1,MembershipVersion=1,ClientSubmissionSequence=1,
                ObservedStateRevision=f.State.EffectRuntime.StateRevision,OperationWindowId=cause.OperationWindowId,WindowGeneration=cause.WindowGeneration,Command=dto};
            string digest=OperationCommandCanonicalizer.CanonicalProposalDigest(proposal); Assert.That(digest,Does.Match("^[0-9a-f]{64}$"));
            var bad=GameStateCloneService.DeepClone(proposal); bad.Command.Parameters.Add(new GameCommandParameterDto {Key="unexpected",Value="claim"});
            Assert.Throws<ArgumentException>(()=>OperationCommandCanonicalizer.CanonicalProposalDigest(bad));
            var wrong=GameCommandDto.FromCommand(cmd).ToCommand(); wrong.PlayerId=2;
            Assert.That(session.Submit(wrong).Succeeded,Is.False);
            Assert.That(session.Submit(roundTrip).Succeeded,Is.True); Assert.That(f.Source.Invocations.Count,Is.EqualTo(1));
            f.Answer("continue"); f.Answer("resource");
            Assert.That(f.Source.ActiveInvocationId,Is.Empty); Assert.That(f.Source.Invocations.Count,Is.EqualTo(1));
            Assert.That(session.Submit(roundTrip).Succeeded,Is.True); Assert.That(f.Source.Invocations.Count,Is.EqualTo(1));
            f.Restore("U15正式会话与完整请求重放恢复");
        }

        [TestCase(false)] [TestCase(true)]
        public void CompletionEventSubtreeCreatesARealDepartureBypassAndKeepsThePaidInvocation(bool concurrent)
        {
            const string body=@"return function(ctx) return {Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
rightEffects={Effect.GainScore({player=ctx.playerId,amount=1})}})} end";
            f=new CleanupSourceTestFixture(artifacts,initialMarkers:3,bodyOverride:body,storageResponse:true,concurrent:concurrent);
            const string code=@"return function(ctx) if ctx.payload.effectTypeId~='effect.cleanup.source.invoke' then return {} end
return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})} end";
            new LuaEffectEventDispatcher(f.Registry).Register(new LuaHandlerDefinition("qy007.late-cleanup","EffectCompleted","",
                new LuaScriptDefinition("qy007.late-cleanup","qy007.late-cleanup","qy007.late-cleanup","1.0.0",code,LuaContentHasher.ComputeSha256(code)),EffectHandlerRole.Observer));
            var map=YC.Domain.Maps.StaticMapDefinitions.Resolve(f.State.MapId);
            string slot=YC.Domain.Influence.InfluenceService.GetLocationSlotId(map.Locations.First(l=>!l.IsRedZone && l.InfluenceSlotCount>0).LocationId,0);
            f.Engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1,slot,itemTypeId:"core.player_marker")); f.Drain();
            f.Start("qy007.late-cleanup-click"); f.Answer("continue");
            string invocation=f.Source.Invocations.Single().TargetEffectId;
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="EffectCompleted" && e.SourceEffectId==invocation),Is.EqualTo(1));
            f.Answer(f.Open.CandidateIds.First());
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Single().Status,Is.EqualTo("open"));
            Assert.That(f.Source.ActiveInvocationId,Is.Not.Empty);
            Assert.That(CleanupSourceDependencies.Instance.HasPendingForWindow(f.State,f.RootId,out _),Is.True);
            int paid=f.State.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments,"apiVersion")==3 && n.Status==EffectNodeStatus.Completed);
            Assert.That(paid,Is.EqualTo(1)); f.Restore("完成事件后真实旁路保留已付Invocation"); f.Answer("store");
            Assert.That(f.Source.ActiveInvocationId,Is.Empty); Assert.That(f.Stored,Is.EqualTo(1)); Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments,"apiVersion")==3 && n.Status==EffectNodeStatus.Completed),Is.EqualTo(paid));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="EffectCompleted" && e.SourceEffectId==invocation),Is.EqualTo(1));
            f.Restore("完成事件后旁路收口不重付不重发");
        }

        [TestCase("event", "optional")]
        [TestCase("cleanup", "required")]
        [TestCase("cleanup", "unspecified")]
        public void UnboundedIsRejectedOutsideExplicitOptionalCleanup(string kind, string policy)
        {
            f = new CleanupSourceTestFixture(artifacts);
            string relative = "award/" + CleanupSourceTestFixture.Definition + ".json";
            var json = JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(f.ContentRoot, relative)));
            json["abilitySources"][0]["kind"] = kind; json["abilities"][0]["settlement"]["wholeEffectPolicy"] = policy;
            string root = artifacts.CopyContent(f.ContentRoot, "008非法重复声明", new System.Collections.Generic.Dictionary<string, string> { [relative] = json.ToString() });
            Assert.That(() => ExternalContentPack.Load(root), Throws.Exception);
        }
    }
}
