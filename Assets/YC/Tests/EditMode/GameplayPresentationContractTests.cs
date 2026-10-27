using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Maps;
#if GAMEPLAY_PRESENTATION_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class GameplayPresentationContractTests
    {
        #region NativeMapAndFormalPurchasePresentation
        [Test]
        public void NativeMapRequestDefaultsPersistProjectAndKeepAnswers()
        {
            foreach (string kind in new[] { "initial", "place", "route" })
            {
                var run = CreateNativeTextDriver(kind);
                Assert.That(run.Engine.RunUntilQuiescent().WaitingForInput, Is.True, run.State.EffectRuntime.LastFaultMessage);
                var request = run.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                string suffix = kind == "initial" ? "initial_influence" : kind == "place" ? "influence.place" : "route";
                Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:interaction.map." + suffix));
                Assert.That(request.Presentation.Prompt.Arguments, Is.Empty);
                Assert.That(request.CandidateIds, Is.EqualTo(kind == "place" ? new[] { "zeta", "alpha" } : new[] { "alpha", "zeta" }));
                Assert.That(request.MinSelections, Is.EqualTo(kind == "initial" ? 0 : 1));
                Assert.That(request.MaxSelections, Is.EqualTo(1));
                Assert.That(request.AllowDecline, Is.EqualTo(kind == "initial"));
                Assert.That(request.AnswerSchema, Is.EqualTo(kind == "initial" ? "candidate_ids" : "candidate_id"));
                var view = GameStateViewProjector.Project(run.State, GameStateViewer.Player(1));
                var projected = InteractionRequestProjector.ProjectForPlayer(request, 1, view, run.State.GameId);
                Assert.That(projected.Presentation.Prompt.Key, Is.EqualTo(request.Presentation.Prompt.Key));
                Assert.That(projected.CandidateIds, Is.EqualTo(request.CandidateIds));
                Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2).Presentation, Is.Null);
                var restored = GameStateCloneService.DeepClone(run.State);
                var restoredRequest = restored.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(restoredRequest.Presentation.Prompt.Key, Is.EqualTo(request.Presentation.Prompt.Key));
                Assert.That(restoredRequest.CandidateIds, Is.EqualTo(request.CandidateIds));
                run.Engine.RunUntilQuiescent();
                Assert.That(run.State.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
                Assert.That(run.State.EffectRuntime.InteractionRequests[0].Presentation.Prompt.Key, Is.EqualTo(request.Presentation.Prompt.Key));
                var resolver = new YC.Application.Presentation.GameplayTextResolver(run.Pack.Presentation);
                Assert.That(resolver.Resolve(projected.Presentation.Prompt, new GameplayTextContext { ViewerPlayerId = 1, StateRevision = view.StateRevision }).ToPlainText(), Is.Not.Empty);
                var answer = kind == "initial" ? NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(request.CandidateIds[0]) })
                    : NormalizedValue.CreateString(request.CandidateIds[0]);
                Assert.That(run.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 2, run.State.EffectRuntime.StateRevision, answer, out _), Is.False);
                Assert.That(run.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, run.State.EffectRuntime.StateRevision,
                    NormalizedValue.CreateString("not-a-candidate"), out _), Is.False);
                Assert.That(run.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, run.State.EffectRuntime.StateRevision, answer, out var reason), Is.True, reason);
                run.Engine.RunUntilQuiescent();
                var submitted = run.State.EffectRuntime.InteractionRequests.Single();
                Assert.That(submitted.NormalizedAnswer.ToDeterministicString(), Is.EqualTo(answer.ToDeterministicString()));
                Assert.That(submitted.Presentation.Prompt.Key, Is.EqualTo(request.Presentation.Prompt.Key));
                Assert.That(run.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == run.Root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
            // 节点专用文案优先于原生默认；已有请求专用文案仍优先于节点。
            foreach (bool requestSpecific in new[] { false, true })
            {
                var run = CreateNativeTextDriver("place", "core:presentation.unavailable", requestSpecific ? "core:interaction.map.entrance" : null);
                run.Engine.RunUntilQuiescent();
                var request = run.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(request.Presentation.Prompt.Key, Is.EqualTo(requestSpecific ? "core:interaction.map.entrance" : "core:presentation.unavailable"));
                Assert.That(request.CandidateIds, Is.EqualTo(new[] { "zeta", "alpha" }));
            }
            // 通用 gate 有默认；未知专用 gate 没有臆造正文；gate 文案不覆盖同节点后续地图请求。
            foreach (string gateKey in new[] { "", "unknown.special.gate", "effect.decline" })
            {
                var run = CreateNativeTextDriver("place", null, null, true, gateKey);
                run.Engine.RunUntilQuiescent();
                var gate = run.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(gate.CandidateIds, Is.EqualTo(new[] { "continue", "decline" }));
                Assert.That(gate.AllowDecline, Is.True);
                Assert.That(gate.Presentation?.Prompt?.Key, Is.EqualTo(gateKey == "unknown.special.gate" ? null : "core:interaction.effect.optional"));
                Assert.That(run.Engine.TrySubmitInteraction(gate.GetStableInteractionId(), 1, run.State.EffectRuntime.StateRevision,
                    NormalizedValue.CreateString("continue"), out var reason), Is.True, reason);
                run.Engine.RunUntilQuiescent();
                Assert.That(run.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open").Presentation.Prompt.Key, Is.EqualTo("core:interaction.map.influence.place"));
            }
            var dedicatedGate = CreateNativeTextDriver("place", "core:presentation.unavailable", null, true);
            dedicatedGate.Engine.RunUntilQuiescent();
            var dedicatedRequest = dedicatedGate.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(dedicatedRequest.Presentation.Prompt.Key, Is.EqualTo("core:presentation.unavailable"));
            Assert.That(dedicatedGate.Engine.TrySubmitInteraction(dedicatedRequest.GetStableInteractionId(), 1, dedicatedGate.State.EffectRuntime.StateRevision,
                NormalizedValue.CreateString("continue"), out var dedicatedReason), Is.True, dedicatedReason);
            dedicatedGate.Engine.RunUntilQuiescent();
            Assert.That(dedicatedGate.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open").Presentation.Prompt.Key,
                Is.EqualTo("core:interaction.map.influence.place"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FormalTinManPurchaseMetadataKeepsConditionsAndCommittedResults(bool owner)
        {
            var actual = FacilityPresentationFixture(); var original = FacilityPresentationFixture();
            (List<EffectSpec> Specs, Newtonsoft.Json.Linq.JToken Business) CompilePurchase(ExternalContentPack pack, string source)
            {
                var context = new LuaInvocationContext("tin.man.presentation.event", "CharacterCardRevealed", "p1", 0, "1.0.0", 3, 0, 0);
                var invocation = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768)).Invoke(
                    new LuaScriptDefinition("character.tin-man", "character.tin-man.strategy", "character.tin-man.strategy", "1.0.0", source, LuaContentHasher.ComputeSha256(source)), context);
                Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
                Assert.That(new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(invocation,
                    new LuaEffectCompilationContext { PlayerId = 1, SourceId = "character.tin-man", AbilityId = "character.tin-man.strategy" },
                    out var specs, out var fault), Is.True, fault?.Diagnostic);
                return (specs, Newtonsoft.Json.Linq.JToken.FromObject(invocation.Effects));
            }
            var actualCompilation = CompilePurchase(actual.Pack, actual.Pack.GetScript("lua/characters/tin-man.strategy.lua"));
            var originalCompilation = CompilePurchase(original.Pack, OriginalTinManPurchaseLua);
            var actualSpecs = actualCompilation.Specs; var originalSpecs = originalCompilation.Specs;
            Assert.That(actualSpecs.Count, Is.EqualTo(3));
            var actualBusiness = actualCompilation.Business; var originalBusiness = originalCompilation.Business;
            RemoveNativeTextMetadata(actualBusiness); RemoveNativeTextMetadata(originalBusiness);
            Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(actualBusiness, originalBusiness), Is.True, "只附展示元数据，完整Condition/付款/奖励/顺序不变");
            for (int index = 1; index <= 2; index++)
            {
                Assert.That(actualSpecs[index].Presentation.Interaction.Prompt.Key, Is.EqualTo("core:interaction.tin_man.purchase"));
                Assert.That(actualSpecs[index].Presentation.Interaction.Prompt.Arguments.Single().Kind, Is.EqualTo("integer"));
                Assert.That(actualSpecs[index].Presentation.Interaction.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(index == 1 ? 12 : 15));
                actualSpecs[index].Visibility = originalSpecs[index].Visibility = owner ? "owner" : "public";
            }
            actual.State.FindPlayer(1).Resources.GoldVoucher = original.State.FindPlayer(1).Resources.GoldVoucher = 40;
            int initialPure = actual.State.FindPlayer(1).Resources.PureOriginium;
            var actualEngine = new EffectTreeExecutor(actual.State, actual.Registry); var originalEngine = new EffectTreeExecutor(original.State, original.Registry);
            string actualRoot = actualEngine.CreateRoot(EffectSpec.Condition(actualSpecs, new List<EffectSpec>()));
            string originalRoot = originalEngine.CreateRoot(EffectSpec.Condition(originalSpecs, new List<EffectSpec>()));
            foreach (int cost in new[] { 12, 15 })
            {
                Assert.That(actualEngine.RunUntilQuiescent().WaitingForInput, Is.True, actual.State.EffectRuntime.LastFaultMessage);
                Assert.That(originalEngine.RunUntilQuiescent().WaitingForInput, Is.True, original.State.EffectRuntime.LastFaultMessage);
                var request = actual.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                var baseline = original.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                Assert.That(request.PromptKey, Is.EqualTo("character.tin_man.purchase." + cost));
                Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:interaction.tin_man.purchase"));
                Assert.That(request.Presentation.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(cost));
                Assert.That(baseline.Presentation, Is.Null, "旧未知专用gate继续沿旧兼容");
                Assert.That(request.CandidateIds, Is.EqualTo(baseline.CandidateIds));
                Assert.That(request.CandidateIds, Is.EqualTo(new[] { "continue", "decline" }));
                Assert.That(request.Visibility, Is.EqualTo(baseline.Visibility));
                Assert.That(request.MinSelections, Is.EqualTo(baseline.MinSelections));
                Assert.That(request.MaxSelections, Is.EqualTo(baseline.MaxSelections));
                Assert.That(request.AnswerSchema, Is.EqualTo(baseline.AnswerSchema));
                Assert.That(request.AllowDecline, Is.EqualTo(baseline.AllowDecline));
                foreach (int viewer in new[] { 1, 2 })
                {
                    var view = GameStateViewProjector.Project(actual.State, GameStateViewer.Player(viewer));
                    var projected = InteractionRequestProjector.ProjectForPlayer(request, viewer, view, actual.State.GameId);
                    var prior = InteractionRequestProjector.ProjectForPlayer(baseline, viewer);
                    Assert.That(projected.VisibleToViewer, Is.EqualTo(prior.VisibleToViewer));
                    Assert.That(projected.CandidateIds, Is.EqualTo(prior.CandidateIds));
                    Assert.That(projected.NormalizedAnswer?.ToDeterministicString(), Is.EqualTo(prior.NormalizedAnswer?.ToDeterministicString()));
                    if (projected.VisibleToViewer)
                        Assert.That(projected.Presentation.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(cost));
                }
                var document = new YC.Application.Presentation.GameplayTextResolver(actual.Pack.Presentation).Resolve(request.Presentation.Prompt,
                    new GameplayTextContext { ViewerPlayerId = 1, StateRevision = actual.State.EffectRuntime.StateRevision });
                Assert.That(document.ToPlainText(), Does.Contain(cost.ToString()));
                var clone = GameStateCloneService.DeepClone(actual.State);
                Assert.That(clone.EffectRuntime.InteractionRequests.Single(value => value.Status == "open").Presentation.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(cost));
                string answer = owner && cost == 15 ? "decline" : "continue";
                Assert.That(actualEngine.TrySubmitInteraction(request.GetStableInteractionId(), 1, actual.State.EffectRuntime.StateRevision,
                    NormalizedValue.CreateString(answer), out var reason), Is.True, reason);
                Assert.That(originalEngine.TrySubmitInteraction(baseline.GetStableInteractionId(), 1, original.State.EffectRuntime.StateRevision,
                    NormalizedValue.CreateString(answer), out var originalReason), Is.True, originalReason);
            }
            actualEngine.RunUntilQuiescent(); originalEngine.RunUntilQuiescent();
            Assert.That(actual.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(owner ? 28 : 13));
            Assert.That(actual.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(initialPure + (owner ? 1 : 2)));
            Assert.That(actual.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(original.State.FindPlayer(1).Resources.GoldVoucher));
            Assert.That(actual.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(original.State.FindPlayer(1).Resources.PureOriginium));
            Assert.That(actual.State.FindPlayer(1).Score, Is.EqualTo(original.State.FindPlayer(1).Score));
            Assert.That(actual.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == actualRoot).Status,
                Is.EqualTo(original.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == originalRoot).Status));
            Assert.That(actual.State.EffectRuntime.InteractionRequests.Select(value => value.NormalizedAnswer.ToDeterministicString()),
                Is.EqualTo(original.State.EffectRuntime.InteractionRequests.Select(value => value.NormalizedAnswer.ToDeterministicString())));
            Assert.That(actual.State.EffectRuntime.InteractionRequests.Select(value => value.Presentation.Prompt.Arguments.Single().Value.IntegerValue), Is.EqualTo(new long[] { 12, 15 }));
        }

        private static (GameState State, EffectTreeExecutor Engine, ExternalContentPack Pack, string Root) CreateNativeTextDriver(
            string kind, string nodeKey = null, string requestKey = null, bool gate = false, string gateKey = "")
        {
            var fixture = FacilityPresentationFixture(); var candidates = new[] { "zeta", "alpha" };
            fixture.Registry.Register("native.presentation.driver", context => {
                if (context.Node.FlowStage == "awaiting") return EffectStepResult.Completed(context.GetLatestInteractionAnswer());
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                if (kind == "initial") return (EffectStepResult)typeof(ExpansionSetupFlow).GetMethod("Choose", flags).Invoke(null,
                    new object[] { context, "awaiting", "setup.influence.optional", 1, candidates, 0, 1, true });
                if (kind == "route")
                {
                    string savedType = context.Node.EffectTypeId;
                    context.Node.EffectTypeId = ContentPrimitiveEffectExecutor.ClearRouteRoad;
                    var result = (EffectStepResult)typeof(ContentPrimitiveEffectExecutor).GetMethod("ContentWait", flags).Invoke(null,
                        new object[] { context, "choose_route", candidates, 1, 1, false });
                    context.Node.EffectTypeId = savedType; result.WithFlowStage("awaiting"); return result;
                }
                var interaction = (EffectInteractionSpec)typeof(InfluenceEffectExecutor).GetMethod("CreatePlacementInteraction", flags).Invoke(null,
                    new object[] { context, candidates });
                if (requestKey != null) interaction.Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef(requestKey) };
                return EffectStepResult.Continue("awaiting").AddInteraction(interaction);
            }, EffectExecutorKind.IntrinsicFlow);
            var spec = EffectSpec.Create("native.presentation.driver", playerId: 1);
            if (nodeKey != null) spec.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation { Prompt = new GameplayTextRef(nodeKey) } };
            if (gate) spec.WithExecutionOptions(true, 1, gateKey);
            var engine = new EffectTreeExecutor(fixture.State, fixture.Registry); string root = engine.CreateRoot(spec);
            return (fixture.State, engine, fixture.Pack, root);
        }
        private static void RemoveNativeTextMetadata(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JObject obj)
                foreach (var property in obj.Properties().ToArray())
                    if (property.Name == "Presentation") property.Remove(); else RemoveNativeTextMetadata(property.Value);
            else if (token is Newtonsoft.Json.Linq.JArray array) foreach (var item in array) RemoveNativeTextMetadata(item);
        }
        private const string OriginalTinManPurchaseLua = @"return function(ctx)
    local effects = { Effect.GainScore({ recipient = ctx.playerId, amount = 1 }) }
    for _, cost in ipairs({12, 15}) do
        table.insert(effects, Effect.Condition({ allowDecline = true, decisionPlayer = ctx.playerId,
            declinePromptKey = 'character.tin_man.purchase.' .. tostring(cost),
            leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = cost }) },
            rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) }
        }))
    end
    return effects
end
";
        #endregion


        #region AdditionalBuildInteractionPresentation
        [TestCase(false)]
        [TestCase(true)]
        public void FormalAdditionalBuildUsesVisibleCandidatesAndRealStage(bool reserve)
        {
            var fixture = FacilityPresentationFixture(); var state = fixture.State;
            var sourceDefinition = reserve ? YC.Domain.Facilities.FacilityCardDatabase.LogisticsHub : YC.Domain.Facilities.FacilityCardDatabase.SimpleEngineeringCamp;
            var sourceItem = state.ItemZones.Items.First(item => item.DefinitionId == sourceDefinition);
            var board = state.ItemZones.Zones.First(zone => zone.TemplateId == "core.zone.city_facility_cell" &&
                zone.OwnerPlayerId == 1 && state.ItemZones.Items.All(item => item.ZoneId != zone.ZoneId));
            state.ItemZones.Orders.Find(order => order.ZoneId == sourceItem.ZoneId)?.ItemIds.Remove(sourceItem.ItemId);
            sourceItem.ZoneId = board.ZoneId; sourceItem.SlotId = "";
            sourceItem.OwnerSubject = ItemSubjectRef.Player(1); sourceItem.ControllerSubject = ItemSubjectRef.Player(1);
            state.ItemZones.Orders.Find(order => order.ZoneId == board.ZoneId)?.ItemIds.Add(sourceItem.ItemId);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var itemReason), Is.True, itemReason);
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 99, OriginiumShard = 99, Iron = 99, PureOriginium = 99, GoldVoucher = 99 };
            var resourcesBefore = state.FindPlayer(1).Resources.Clone();
            string effect = reserve ? "build_extension_hub" : "build_additional_facility";
            var context = new LuaInvocationContext("additional.build.event", "FacilityEntryEffectActivated", "p1", 0, "1.0.0", 3, 0, 0);
            var invocation = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768)).Invoke(
                new LuaScriptDefinition("content.facility." + effect, "facility.entry." + effect, "additional.build.handler", "1.0.0",
                    fixture.Pack.GetScript("lua/facilities/" + effect + ".lua"),
                    LuaContentHasher.ComputeSha256(fixture.Pack.GetScript("lua/facilities/" + effect + ".lua"))), context);
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: fixture.Pack).TryCompile(invocation,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = sourceItem.ItemId, ContentInstanceId = sourceItem.ItemId,
                    AbilityId = "facility.entry." + effect }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var spec = specs.Single();
            Assert.That(spec.EffectTypeId, Is.EqualTo("effect.facility.choose_build"));
            Assert.That(ItemTransferEffectExecutor.Text(spec.NormalizedArguments, "sourceZone"), Is.EqualTo(reserve ? "reserve" : "supply"));
            Assert.That(ItemTransferEffectExecutor.Text(spec.NormalizedArguments, "effectId"), Is.EqualTo(reserve ? "reserve_extension_hub" : ""));
            Assert.That(spec.Presentation.PresentationScript, Is.EqualTo("lua/presentation/interaction.choose_build.lua"));
            int calls = 0; var contexts = new List<GameplayPresentationContext>(); var presenter = fixture.Registry.GameplayPresenter;
            fixture.Registry.GameplayPresenter = (string path, GameplayPresentationContext supplied,
                out GameplayPresentationResult result, out string diagnostic) => {
                calls++; contexts.Add(GameplayPresentationNormalizer.Clone(supplied)); return presenter(path, supplied, out result, out diagnostic);
            };
            var executor = new EffectTreeExecutor(state, fixture.Registry); string root = executor.CreateRoot(spec);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True, state.EffectRuntime.LastFaultMessage);
            var choose = state.EffectRuntime.InteractionRequests.Single(request => request.Status == "open");
            var originalIds = choose.CandidateIds.ToArray();
            Assert.That(choose.PromptKey, Is.EqualTo("facility.entry.choose_additional_build"));
            Assert.That(choose.AnswerSchema, Is.EqualTo("candidate_id")); Assert.That(choose.MinSelections, Is.EqualTo(1));
            Assert.That(choose.MaxSelections, Is.EqualTo(1)); Assert.That(choose.AllowDecline, Is.False);
            Assert.That(choose.CandidateIds.Contains("free"), Is.False);
            var suppliedChoose = contexts.Single();
            Assert.That(ItemTransferEffectExecutor.Text(suppliedChoose.Facts, "promptKey"), Is.EqualTo(choose.PromptKey));
            Assert.That(suppliedChoose.SourceItemId, Is.EqualTo(sourceItem.ItemId));
            Assert.That(suppliedChoose.SourceDefinitionId, Is.EqualTo(sourceDefinition));
            Assert.That(suppliedChoose.Facts.Properties.Select(value => value.Name), Is.EquivalentTo(new[] { "candidateIds", "minSelections", "maxSelections", "promptKey" }));
            var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
            var own = InteractionRequestProjector.ProjectForPlayer(choose, 1, visible, state.GameId);
            Assert.That(own.Presentation.Prompt.Key, Is.EqualTo("core:interaction.facility.build.choose"));
            Assert.That(own.Presentation.Options.Select(option => option.CandidateId), Is.EqualTo(originalIds));
            Assert.That(InteractionRequestProjector.ProjectForPlayer(choose, 2).Presentation, Is.Null);
            var resolver = new YC.Application.Presentation.GameplayTextResolver(fixture.Pack.Presentation);
            var names = visible.ItemZones.Items.Where(item => YC.Domain.Facilities.FacilityCardDatabase.Get(state, item.DefinitionId) != null)
                .ToDictionary(item => item.ItemId, item => YC.Domain.Facilities.FacilityCardDatabase.Get(state, item.DefinitionId).Name);
            var textContext = new GameplayTextContext { ViewerPlayerId = 1, StateRevision = visible.StateRevision, ItemNames = names };
            foreach (var option in own.Presentation.Options)
            {
                if (option.CandidateId == "choice.skip")
                    Assert.That(option.Label.Key, Is.EqualTo("core:interaction.facility.build.skip"));
                else if (!names.ContainsKey(option.CandidateId))
                    Assert.That(option.Label, Is.Null, "预备区缺可见实例身份时保原兼容，定义映射不扩权");
                else
                {
                    Assert.That(option.Label.Key, Is.EqualTo("core:interaction.entity.item"));
                    Assert.That(option.Label.Arguments.Single().Kind, Is.EqualTo("item"));
                    Assert.That(option.Label.Arguments.Single().Value.ReferenceId, Is.EqualTo(option.CandidateId));
                    Assert.That(resolver.Resolve(option.Label, textContext).ToPlainText(), Is.EqualTo(names[option.CandidateId]));
                }
            }
            var cardId = originalIds.First(id => id != "choice.skip");
            if (names.ContainsKey(cardId))
            {
                var hidden = GameStateCloneService.DeepClone(visible); hidden.ItemZones.Items.RemoveAll(item => item.ItemId == cardId);
                Assert.That(InteractionRequestProjector.ProjectForPlayer(choose, 1, hidden, state.GameId).Presentation.Options.Single(option => option.CandidateId == cardId).Label, Is.Null);
                Assert.That(choose.Presentation.Options.Single(option => option.CandidateId == cardId).Label, Is.Not.Null);
            }
            else Assert.That(choose.Presentation.Options.Single(option => option.CandidateId == cardId).Label, Is.Null);
            Assert.That(InteractionRequestProjector.ProjectForPlayer(choose, 1, visible, "previous.game").Presentation.Options
                .Where(option => option.CandidateId != "choice.skip").All(option => option.Label == null), Is.True);
            executor.RunUntilQuiescent(); Assert.That(calls, Is.EqualTo(1));
            var noSource = GameplayPresentationNormalizer.Clone(suppliedChoose); noSource.SourceItemId = ""; noSource.SourceDefinitionId = "";
            Assert.That(fixture.Pack.TryPresent("lua/presentation/interaction.choose_build.lua", noSource, out var compatibility,
                out var code, out var diagnostic), Is.True, code + ": " + diagnostic);
            Assert.That(compatibility.Interaction, Is.Null);
            var oldStage = GameplayPresentationNormalizer.Clone(suppliedChoose);
            oldStage.Facts.Properties.Single(value => value.Name == "promptKey").Value = NormalizedValue.CreateString("effect.build.choose_slot");
            Assert.That(fixture.Pack.TryPresent("lua/presentation/interaction.choose_build.lua", oldStage,
                out compatibility, out code, out diagnostic), Is.True, code + ": " + diagnostic);
            Assert.That(compatibility.Interaction, Is.Null);
            string answer = reserve ? "choice.skip" : cardId;
            Assert.That(executor.TrySubmitInteraction(choose.GetStableInteractionId(), 1, state.EffectRuntime.StateRevision,
                NormalizedValue.CreateString(answer), out var answerReason), Is.True, answerReason);
            var next = executor.RunUntilQuiescent(); Assert.That(next.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            if (reserve)
            {
                Assert.That(originalIds.Contains("choice.skip"), Is.True);
                Assert.That(state.EffectRuntime.InteractionRequests.All(request => request.Status != "open"), Is.True);
                Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(calls, Is.EqualTo(1), "skip不制造free付款请求");
            }
            else
            {
                Assert.That(next.WaitingForInput, Is.True);
                var payment = state.EffectRuntime.InteractionRequests.Single(request => request.Status == "open");
                Assert.That(payment.PromptKey, Is.EqualTo("effect.build.choose_payment"));
                Assert.That(payment.AnswerSchema, Is.EqualTo("candidate_id"));
                Assert.That(payment.CandidateIds, Is.EquivalentTo(new[] { "resources", "gold" }), string.Join(",", payment.CandidateIds));
                Assert.That(payment.Presentation.Prompt.Key, Is.EqualTo("core:interaction.facility.build.payment"));
                Assert.That(payment.Presentation.Options.Select(option => option.CandidateId), Is.EqualTo(payment.CandidateIds));
                Assert.That(payment.Presentation.Options.Select(option => option.Label.Key),
                    Is.EqualTo(payment.CandidateIds.Select(id => "core:interaction.facility.build." + id)));
                Assert.That(ItemTransferEffectExecutor.Text(contexts.Last().Facts, "promptKey"), Is.EqualTo(payment.PromptKey));
                Assert.That(calls, Is.EqualTo(2));
                executor.RunUntilQuiescent(); Assert.That(calls, Is.EqualTo(2));
                Assert.That(choose.CandidateIds, Is.EqualTo(originalIds), "支付阶段不改已存候选");
            }
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state.FindPlayer(1).Resources),
                Is.EqualTo(Newtonsoft.Json.JsonConvert.SerializeObject(resourcesBefore)), "选择／skip不能提前扣费用");
        }
        #endregion

        #region FacilityInteractionPresentation
        [TestCase(false)]
        [TestCase(true)]
        public void FormalFacilityChoiceKeepsBusinessCandidatesAndVisibleRefs(bool adjacent)
        {
            var fixture = FacilityPresentationFixture();
            var visibleItems = new ItemZoneQuery().Project(fixture.State, GameStateViewer.Player(1)).Items
                .Where(item => YC.Domain.Facilities.FacilityCardDatabase.Get(fixture.State, item.DefinitionId) != null).Take(6).ToArray();
            Assert.That(visibleItems.Length, Is.EqualTo(6));
            var context = new LuaInvocationContext("facility.presentation.event", "FacilityEntryEffectActivated", "p1", 0, "1.0.0", 3, 0, 0);
            context.FacadeSnapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("sourceSlotIndex", LuaSnapshotValue.Integer(4)) });
            LuaSnapshotValue Instance(int index, int slot, string owner, bool entry, string color) => LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("id", LuaSnapshotValue.String(visibleItems[index].ItemId)),
                new LuaSnapshotEntry("slotIndex", LuaSnapshotValue.Integer(slot)),
                new LuaSnapshotEntry("owner", LuaSnapshotValue.String(owner)),
                new LuaSnapshotEntry("hasEntryEffect", LuaSnapshotValue.Boolean(entry)),
                new LuaSnapshotEntry("color", LuaSnapshotValue.String(color)) });
            context.FacadeSnapshot.GlobalContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("instances", LuaSnapshotValue.Array(new[] {
                Instance(0, 3, "p1", true, "red"), Instance(1, 5, "p1", true, "blue"),
                Instance(2, 1, "p2", true, "red"), Instance(3, 7, "p1", true, "rainbow"),
                Instance(4, 0, "p1", true, "red"), Instance(5, 1, "p1", false, "blue") })) });
            string path = adjacent ? "lua/facilities/copy_adjacent_entry.lua" : "lua/facilities/remove_then_dispatch_or_explore.lua";
            string original = adjacent ? OriginalAdjacentFacilityLua : OriginalVehicleFacilityLua;
            LuaInvocationResult Invoke(string source) => new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768))
                .Invoke(new LuaScriptDefinition("facility.presentation", "facility.presentation.ability", "facility.presentation.handler",
                    "1.0.0", source, LuaContentHasher.ComputeSha256(source)), context);
            var before = Invoke(original); var actual = Invoke(fixture.Pack.GetScript(path));
            Assert.That(before.IsSuccess, Is.True, before.Diagnostic); Assert.That(actual.IsSuccess, Is.True, actual.Diagnostic);
            var expectedBusiness = Newtonsoft.Json.Linq.JToken.FromObject(before.Effects);
            var actualBusiness = Newtonsoft.Json.Linq.JToken.FromObject(actual.Effects);
            RemoveFacilityDisplayMetadata(expectedBusiness); RemoveFacilityDisplayMetadata(actualBusiness);
            Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(expectedBusiness, actualBusiness), Is.True,
                "完整原业务树、合法候选、子效果及参数保持");
            Assert.That(new LuaEffectSpecCompiler(presentationPack: fixture.Pack).TryCompile(actual,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = "facility.presentation" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var executor = new EffectTreeExecutor(fixture.State, fixture.Registry); executor.CreateRoot(specs.Single());
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True, fixture.State.EffectRuntime.LastFaultMessage);
            var request = fixture.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            var expectedIds = adjacent ? visibleItems.Take(2).Select(item => item.ItemId).ToArray() : new[] { "vehicle.remove_move", "vehicle.explore" };
            Assert.That(request.CandidateIds, Is.EqualTo(expectedIds));
            Assert.That(request.InteractionTypeId, Is.EqualTo("facility.entry.choice"));
            Assert.That(request.PromptKey, Is.EqualTo(adjacent ? "facility.entry.choose_adjacent" : "facility.entry.choose_vehicle_action"));
            Assert.That(request.MinSelections, Is.EqualTo(1)); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(request.AllowDecline, Is.False);
            var view = GameStateViewProjector.Project(fixture.State, GameStateViewer.Player(1));
            var own = InteractionRequestProjector.ProjectForPlayer(request, 1, view, fixture.State.GameId);
            Assert.That(own.Presentation.Options.Select(option => option.CandidateId), Is.EqualTo(expectedIds));
            Assert.That(own.Presentation.Prompt.Key, Is.EqualTo(adjacent ? "core:interaction.facility.adjacent.prompt" : "core:interaction.facility.vehicle.prompt"));
            Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2,
                GameStateViewProjector.Project(fixture.State, GameStateViewer.Player(2)), fixture.State.GameId).Presentation, Is.Null);
            var resolver = new YC.Application.Presentation.GameplayTextResolver(fixture.Pack.Presentation);
            var textContext = new GameplayTextContext { ViewerPlayerId = 1, StateRevision = view.StateRevision,
                ItemNames = visibleItems.ToDictionary(item => item.ItemId, item => YC.Domain.Facilities.FacilityCardDatabase.Get(fixture.State, item.DefinitionId).Name) };
            for (int index = 0; index < expectedIds.Length; index++)
            {
                var option = own.Presentation.Options[index];
                if (adjacent)
                {
                    Assert.That(option.Label.Key, Is.EqualTo("core:interaction.entity.item"));
                    Assert.That(option.Label.Arguments.Single().Kind, Is.EqualTo("item"));
                    Assert.That(option.Label.Arguments.Single().Value.ReferenceId, Is.EqualTo(expectedIds[index]));
                    Assert.That(option.Description.Key, Is.EqualTo("core:interaction.facility.slot"));
                    Assert.That(option.Description.Arguments.Single().Value.IntegerValue, Is.EqualTo(index == 0 ? 4 : 6));
                    Assert.That(resolver.Resolve(option.Label, textContext).ToPlainText(), Is.EqualTo(textContext.ItemNames[expectedIds[index]]));
                }
                else Assert.That(option.Label.Key, Is.EqualTo(index == 0 ? "core:interaction.facility.vehicle.remove_move" : "core:interaction.facility.vehicle.explore"));
            }
            if (adjacent)
            {
                Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 1).Presentation.Options.All(option => option.Label == null), Is.True);
                var hidden = GameStateCloneService.DeepClone(view);
                hidden.ItemZones.Items.RemoveAll(item => item.ItemId == expectedIds[0]);
                Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 1, hidden, fixture.State.GameId).Presentation.Options[0].Label, Is.Null);
                Assert.That(request.Presentation.Options[0].Label, Is.Not.Null, "投影不能改源请求");
            }
        }

        [TestCase(5)]
        [TestCase(7)]
        public void FacilityResourceRowsUseRealTotalAndKeepAllocationAnswer(int total)
        {
            var fixture = FacilityPresentationFixture();
            string source = total == 5 ? fixture.Pack.GetScript("lua/facilities/choose_five_basic_resources.lua")
                : "return function(ctx) return { Effect.ChooseResources({ player = ctx.playerId, amount = " + total + " }) } end";
            var spec = CompileFacilityPresentation(fixture.Pack, source);
            int calls = 0; GameplayPresentationContext supplied = null; var presenter = fixture.Registry.GameplayPresenter;
            fixture.Registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                out GameplayPresentationResult result, out string diagnostic) => {
                calls++; supplied = GameplayPresentationNormalizer.Clone(context); return presenter(path, context, out result, out diagnostic);
            };
            var executor = new EffectTreeExecutor(fixture.State, fixture.Registry); var root = executor.CreateRoot(spec);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True, fixture.State.EffectRuntime.LastFaultMessage);
            var request = fixture.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            string[] rows = { "Originium", "OriginiumShard", "Iron" };
            Assert.That(request.AnswerSchema, Is.EqualTo("resource_allocation"));
            Assert.That(request.CandidateIds, Is.EqualTo(rows.Concat(new[] { "total:" + total })));
            Assert.That(request.PromptKey, Is.EqualTo("effect.resource.choose_amounts"));
            Assert.That(ItemTransferEffectExecutor.Number(request.PromptParameters, "totalAmount"), Is.EqualTo(total));
            Assert.That(ItemTransferEffectExecutor.Value(request.PromptParameters, "rowIds").Items.Select(value => value.StringValue), Is.EqualTo(rows));
            Assert.That(ItemTransferEffectExecutor.Value(request.PromptParameters, "rowMaximums").Items.Select(value => value.IntegerValue), Is.EqualTo(new long[] { total, total, total }));
            Assert.That(ItemTransferEffectExecutor.Number(supplied.Facts, "totalAmount"), Is.EqualTo(total));
            Assert.That(supplied.Facts.Properties.Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal),
                Is.EqualTo(new[] { "candidateIds", "minSelections", "maxSelections", "totalAmount", "rowIds", "rowMaximums" }.OrderBy(value => value, StringComparer.Ordinal)));
            var view = GameStateViewProjector.Project(fixture.State, GameStateViewer.Player(1));
            var own = InteractionRequestProjector.ProjectForPlayer(request, 1, view, fixture.State.GameId);
            Assert.That(own.Presentation.Prompt.Key, Is.EqualTo("core:interaction.resource.choose.prompt"));
            Assert.That(own.Presentation.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(total));
            Assert.That(own.Presentation.QuantityRows.Select(row => row.RowId), Is.EqualTo(rows));
            string[] keys = { "originium", "originium_shard", "iron" };
            string[] icons = { "orirock", "originium_shard", "iron" };
            var resolver = new YC.Application.Presentation.GameplayTextResolver(fixture.Pack.Presentation);
            var textContext = new GameplayTextContext { ViewerPlayerId = 1, StateRevision = view.StateRevision };
            for (int i = 0; i < rows.Length; i++)
            {
                var label = own.Presentation.QuantityRows[i].Label;
                Assert.That(label.Key, Is.EqualTo("core:interaction.resource." + keys[i]));
                Assert.That(resolver.Resolve(label, textContext).Runs.OfType<IconRun>().Single().IconKey, Is.EqualTo("core:resource." + icons[i]));
            }
            Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2).Presentation, Is.Null);
            executor.RunUntilQuiescent(); InteractionRequestProjector.ProjectForPlayer(request, 1, view, fixture.State.GameId);
            Assert.That(calls, Is.EqualTo(1), "重复挂起／投影不重跑Lua");
            int revision = fixture.State.EffectRuntime.StateRevision;
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision,
                NormalizedValue.CreateString("Originium=0,OriginiumShard=0,Iron=0"), out _), Is.False, "原精确总量约束保持");
            Assert.That(fixture.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            var resources = fixture.State.FindPlayer(1).Resources;
            int rock = resources.Originium, shard = resources.OriginiumShard, iron = resources.Iron;
            string answer = "Originium=2,OriginiumShard=0,Iron=" + (total - 2);
            Assert.That(ResourceAllocationAnswer.TryParse(answer, rows, total, out var amounts, out _), Is.True);
            Assert.That(amounts["OriginiumShard"], Is.EqualTo(0), "原行0合法");
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision, NormalizedValue.CreateString(answer), out var reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            resources = fixture.State.FindPlayer(1).Resources;
            Assert.That(resources.Originium, Is.EqualTo(rock + 2)); Assert.That(resources.OriginiumShard, Is.EqualTo(shard));
            Assert.That(resources.Iron, Is.EqualTo(iron + total - 2));
        }

        [Test]
        public void FacilityResourceDisplayFailurePreservesStaticPromptAndBusiness()
        {
            var fixture = FacilityPresentationFixture(); var spec = CompileFacilityPresentation(fixture.Pack,
                fixture.Pack.GetScript("lua/facilities/choose_five_basic_resources.lua"));
            spec.Presentation.Interaction = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:presentation.unavailable") };
            fixture.Registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                out GameplayPresentationResult result, out string diagnostic) => { result = null; diagnostic = "facility_display_failure"; return false; };
            var executor = new EffectTreeExecutor(fixture.State, fixture.Registry); executor.CreateRoot(spec);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = fixture.State.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:presentation.unavailable"));
            Assert.That(request.AnswerSchema, Is.EqualTo("resource_allocation"));
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "Originium", "OriginiumShard", "Iron", "total:5" }));
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, fixture.State.EffectRuntime.StateRevision,
                NormalizedValue.CreateString("Originium=0,OriginiumShard=5,Iron=0"), out var reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.EffectRuntime.InteractionRequests.All(value => value.Status != "open"), Is.True);
            Assert.That(fixture.State.EffectRuntime.Diagnostics.Any(value => value.Code == "presentation_failed"), Is.True);
        }

        private static (GameState State, EffectRegistry Registry, ExternalContentPack Pack) FacilityPresentationFixture()
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var installed = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"));
            var active = ActiveContentCatalogBuilder.Build(installed, configuration); configuration.ContentPackHash = active.ContentPackHash;
            var state = active.SetupRecipe.Assemble(new GameState { GameId = "facility.presentation", MapId = configuration.MapId,
                ContentPackHash = active.ContentPackHash, ContentDefinitions = active.ContentDefinitions, RuleConfiguration = configuration,
                Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                LaunchPlayerOrder = new List<int> { 1, 2, 3 }, Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id }).ToList() },
                StaticMapDefinitions.CreateThreePlayerMap(), 17);
            var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry); active.Register(registry);
            return (state, registry, installed.CreateActiveSnapshot(configuration));
        }
        private static EffectSpec CompileFacilityPresentation(ExternalContentPack pack, string source)
        {
            var context = new LuaInvocationContext("facility.resource.event", "FacilityEntryEffectActivated", "p1", 0, "1.0.0", 3, 0, 0);
            var result = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768)).Invoke(
                new LuaScriptDefinition("facility.presentation", "facility.presentation.ability", "facility.presentation.handler", "1.0.0", source, LuaContentHasher.ComputeSha256(source)), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(result,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = "facility.presentation" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        private static void RemoveFacilityDisplayMetadata(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JObject obj)
                foreach (var property in obj.Properties().ToArray())
                    if (property.Name == "Presentation") property.Remove(); else RemoveFacilityDisplayMetadata(property.Value);
            else if (token is Newtonsoft.Json.Linq.JArray array) foreach (var item in array) RemoveFacilityDisplayMetadata(item);
        }
        private const string OriginalVehicleFacilityLua = @"return function(ctx)
    return { Effect.Choice({ player = ctx.playerId, interactionType = 'facility.entry.choice',
        promptKey = 'facility.entry.choose_vehicle_action', branches = {
            { id = 'vehicle.remove_move', effects = {
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', ownerFilter = 'any',
                    interactionType = 'character.ability.target', promptKey = 'effect.influence.remove.choose_target' }),
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', ownerFilter = 'self',
                    interactionType = 'character.ability.choice.move', promptKey = 'effect.influence.move.choose_target' })
            } },
            { id = 'vehicle.explore', effects = { Effect.ChooseExplore({ player = ctx.playerId }) } }
        }
    }) }
end";
        private const string OriginalAdjacentFacilityLua = @"return function(ctx)
    local source = ctx.payload.sourceSlotIndex
    local branches = {}
    for _, facility in ipairs(Global.Content.FindInstances()) do
        local target = facility.slotIndex
        local sameRow = source - source % 3 == target - target % 3
        local adjacent = target == source - 3 or target == source + 3 or
            (sameRow and (target == source - 1 or target == source + 1))
        if facility.owner == ctx.playerId and facility.hasEntryEffect and facility.color ~= 'rainbow' and adjacent then
            branches[#branches + 1] = { id = facility.id, effects = {
                Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = facility.id })
            } }
        end
    end
    if #branches == 0 then return {} end
    return { Effect.Choice({ player = ctx.playerId, branches = branches,
        interactionType = 'facility.entry.choice', promptKey = 'facility.entry.choose_adjacent' }) }
end";

        [TestCase(true)]
        [TestCase(false)]
        public void RepeatProgressKeepsChildPromptCountsFailedIterationsAndSurvivesRecovery(bool earlyStop)
        {
            var fixture = DynamicPresentationFixture(); var state = fixture.State;
            string source = "return function(ctx) return {Effect.Repeat{apiVersion=1,count=" + (earlyStop ? "3" : "2") +
                ",allowEarlyStop=" + (earlyStop ? "true" : "false") + ",effects={Effect.WithPresentation(Effect.Choice{player=ctx.playerId,branches={" +
                "{id='complete',effects={Effect.GainScore{recipient=ctx.playerId,amount=1,reasonId='repeat.display'}}}," +
                "{id='fail',effects={Effect.PayResource{payer=ctx.playerId,resourceType='gold_voucher',amount=100}}}}}," +
                "{interaction={prompt=Text.Ref('core:enterprise.blacksteel.level_1.choice.prompt')}})}}} end";
            int calls = 0; var presenter = fixture.Registry.GameplayPresenter;
            fixture.Registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                out GameplayPresentationResult result, out string diagnostic) => {
                calls++; return presenter(path, context, out result, out diagnostic);
            };
            var spec = CompileDynamicPresentation(fixture.Pack, source, "repeat.display", abilityId: "repeat.display.ability");
            var executor = new EffectTreeExecutor(state, fixture.Registry); string root = executor.CreateRoot(spec);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True, state.EffectRuntime.LastFaultMessage);
            if (earlyStop)
            {
                var decision = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                Assert.That(decision.CandidateIds, Is.EqualTo(new[] { "continue", "stop" }));
                Assert.That(decision.Presentation.Options.Select(value => value.CandidateId), Is.EqualTo(decision.CandidateIds));
                Assert.That(decision.Presentation.Prompt.Key, Is.EqualTo("core:effect.repeat.continue.prompt"));
                CheckDynamicProgress(decision, 0, 3);
                AnswerDynamicPresentation(state, executor, "continue");
            }
            var child = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(child.CandidateIds, Is.EqualTo(new[] { "complete", "fail" }));
            Assert.That(child.Presentation.Prompt.Key, Is.EqualTo("core:enterprise.blacksteel.level_1.choice.prompt"));
            CheckDynamicProgress(child, 0, earlyStop ? 3 : 2);
            int beforeRecovery = calls;
            var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, state.ContentPackHash));
            var restored = HostRecoveryService.RecoverJson(archive, fixture.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            state = restored.State; executor = new EffectTreeExecutor(state, fixture.Registry);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            Assert.That(calls, Is.EqualTo(beforeRecovery), "恢复挂起请求不重放展示");
            var restoredChild = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.FromObject(restoredChild.Presentation),
                Newtonsoft.Json.Linq.JToken.FromObject(child.Presentation)), Is.True);
            AnswerDynamicPresentation(state, executor, "fail");
            Assert.That(ItemTransferEffectExecutor.Number(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == root).ContinuationData,
                "completedCount"), Is.EqualTo(1), "失败迭代也已完成，不能以成功奖励数代替");
            if (earlyStop)
            {
                CheckDynamicProgress(state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open"), 1, 3);
                AnswerDynamicPresentation(state, executor, "continue");
            }
            child = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            CheckDynamicProgress(child, 1, earlyStop ? 3 : 2);
            Assert.That(child.Presentation.Prompt.Key, Is.EqualTo("core:enterprise.blacksteel.level_1.choice.prompt"));
            AnswerDynamicPresentation(state, executor, "complete");
            Assert.That(ItemTransferEffectExecutor.Number(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == root).ContinuationData,
                "completedCount"), Is.EqualTo(2));
            if (earlyStop)
            {
                CheckDynamicProgress(state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open"), 2, 3);
                AnswerDynamicPresentation(state, executor, "stop");
            }
            else Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.PromptKey == "effect.repeat.continue"), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Single(value => value.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1), "恢复或展示不得重发奖励");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PresentationFailureKeepsStaticPromptAndOriginalQuantityBusiness(bool throws)
        {
            var fixture = DynamicPresentationFixture(); var state = fixture.State;
            var card = PrepareDynamicMarkerCard(state, "operator.dorothy", 1);
            var spec = CompileDynamicPresentation(fixture.Pack, fixture.Pack.GetScript("lua/characters/operator.dorothy.tactic.lua"),
                card.DefinitionId, card.ItemId, "operator.dorothy.tactic");
            spec.Presentation.Interaction = new GameplayInteractionPresentation {
                Prompt = new GameplayTextRef("core:effect.repeat.continue.prompt") };
            fixture.Registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                out GameplayPresentationResult result, out string diagnostic) => {
                result = null; diagnostic = "fixture_presentation_failure";
                if (throws) throw new InvalidOperationException(diagnostic);
                return false;
            };
            var executor = new EffectTreeExecutor(state, fixture.Registry); executor.CreateRoot(spec);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:effect.repeat.continue.prompt"));
            Assert.That(ItemTransferEffectExecutor.Number(request.PromptParameters, "maxAmount"), Is.EqualTo(1));
            Assert.That(request.AnswerSchema, Is.EqualTo("bounded_amount")); Assert.That(request.AllowDecline, Is.True);
            Assert.That(state.EffectRuntime.Diagnostics.Count(value => value.Code == "presentation_failed"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            AnswerDynamicPresentation(state, executor, "0");
            Assert.That(state.EffectRuntime.LastFaultCode, Is.Empty);
        }

        private static (GameState State, EffectRegistry Registry, ExternalContentPack Pack) DynamicPresentationFixture()
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var installed = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"));
            var active = ActiveContentCatalogBuilder.Build(installed, configuration);
            configuration.ContentPackHash = active.ContentPackHash;
            var state = active.SetupRecipe.Assemble(new GameState { GameId = "dynamic.presentation", MapId = configuration.MapId,
                ContentPackHash = active.ContentPackHash, ContentDefinitions = active.ContentDefinitions, RuleConfiguration = configuration,
                Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                LaunchPlayerOrder = new List<int> { 1, 2, 3 }, Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id }).ToList() },
                StaticMapDefinitions.CreateThreePlayerMap(), 17);
            var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            active.Register(registry);
            return (state, registry, installed.CreateActiveSnapshot(configuration));
        }
        private static ItemInstanceState PrepareDynamicMarkerCard(GameState state, string definitionId, int available)
        {
            var card = state.ItemZones.Items.Single(item => item.DefinitionId == definitionId);
            var covered = state.ItemZones.Zones.Single(zone => zone.OwnerPlayerId == 1 && zone.TemplateId == "core.zone.character_covered");
            DynamicFixturePosition(state, card, covered.ZoneId); card.ControllerSubject = ItemSubjectRef.Player(1);
            var storage = state.ItemZones.Zones.Single(zone => zone.ParentId == card.ItemId && zone.LocalZoneId == "markers");
            var supply = state.ItemZones.Zones.Single(zone => zone.OwnerPlayerId == 1 && zone.LocalZoneId == "supply");
            foreach (var cube in state.ItemZones.Items.Where(item => item.ZoneId == supply.ZoneId && item.TypeId == "core.player_marker").Skip(available).ToArray())
                DynamicFixturePosition(state, cube, storage.ZoneId);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            return card;
        }
        private static void DynamicFixturePosition(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId; item.SlotId = "";
            state.ItemZones.Orders.Find(order => order.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }
        private static EffectSpec CompileDynamicPresentation(ExternalContentPack pack, string source, string definitionId,
            string itemId = "", string abilityId = "")
        {
            var context = new LuaInvocationContext("dynamic.presentation.event", "CharacterAbilityActivated", "p1", 0, "1.0.0", 3, 0, 0);
            context.FacadeSnapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("cardInstanceRef", LuaSnapshotValue.String(itemId)) });
            var invocation = new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768)).Invoke(
                new LuaScriptDefinition(definitionId, abilityId, "dynamic.presentation.handler", "1.0.0", source, LuaContentHasher.ComputeSha256(source)), context);
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(invocation, new LuaEffectCompilationContext {
                PlayerId = 1, SourceId = itemId, ContentInstanceId = itemId, AbilityId = abilityId
            }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        private static void AnswerDynamicPresentation(GameState state, EffectTreeExecutor executor, string answer)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, state.EffectRuntime.StateRevision,
                NormalizedValue.CreateString(answer), out var reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private static void CheckDynamicProgress(InteractionRequest request, int completed, int total)
        {
            Assert.That(request.Presentation.Progress.Key, Is.EqualTo("core:effect.repeat.progress"));
            Assert.That(request.Presentation.Progress.Arguments.Single(value => value.Name == "completed").Value.IntegerValue, Is.EqualTo(completed));
            Assert.That(request.Presentation.Progress.Arguments.Single(value => value.Name == "total").Value.IntegerValue, Is.EqualTo(total));
        }
        #endregion

        #region EnterpriseDetailMigration
        [Test]
        public void EnterpriseDetailsPreserveFormalReferencesAndFrozenDefinitions()
        {
            var pack = EnterpriseDetailPack();
            Assert.That(pack.ActiveDefinitions.Count(value => value.ContentType == "enterprise" || value.ContentType == "department"), Is.EqualTo(11));
            var registry = new EffectRegistry();
            var definitions = LuaContentCatalog.CreateEnterpriseDefinitions(registry, pack.ActiveDefinitions, pack.GetScript);
            foreach (var content in pack.ActiveDefinitions.Where(value => value.ContentType == "enterprise" || value.ContentType == "department"))
            {
                var context = EnterpriseDetailContext(content.DefinitionId);
                Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var result, out _, out var diagnostic), Is.True, diagnostic);
                var source = (Newtonsoft.Json.Linq.JArray)content.Data[content.ContentType == "department" ? "rewards" : "levels"];
                var expected = source.Where(level => level["description"] != null).OrderBy(level => (int)level["level"]).Select(level => (string)level["description"]["key"]).ToList();
                if (content.DefinitionId == "enterprise.rhine_lab") expected.Add("core:enterprise.rhine_lab.department_missing.description");
                Assert.That(result.Details.Select(value => value.Key), Is.EqualTo(expected), content.DefinitionId);
                Assert.That(result.Resolution, Is.Null);
                Assert.That(result.Interaction, Is.Null);
                foreach (var level in source.Where(value => value["description"] != null))
                {
                    string key = (string)level["description"]["key"];
                    Assert.That(pack.Presentation.TryGetTemplate(key, out _), Is.True, key);
                    var ability = content.Abilities.Find(value => value.AbilityId == (string)level["abilityId"]);
                    if (ability != null) Assert.That(ability.Presentation.Description.Key, Is.EqualTo(key));
                }
                var refs = content.ContentType == "enterprise" ? definitions.FindEnterprise(content.DefinitionId).Levels.Select(value => value.Description) :
                    definitions.FindDepartment(content.DefinitionId).Rewards.Select(value => value.Description);
                var first = refs.First(value => value != null); first.Key = "test:changed";
                var fresh = content.ContentType == "enterprise" ? definitions.FindEnterprise(content.DefinitionId).Levels.Select(value => value.Description) :
                    definitions.FindDepartment(content.DefinitionId).Rewards.Select(value => value.Description);
                Assert.That(fresh.First(value => value != null).Key == "test:changed", Is.False);
                var presentation = content.ContentType == "enterprise" ? definitions.FindEnterprise(content.DefinitionId).Presentation : definitions.FindDepartment(content.DefinitionId).Presentation;
                Assert.That(presentation.DetailScript, Is.EqualTo(content.Presentation.DetailScript));
                presentation.DetailScript = "changed";
                var again = content.ContentType == "enterprise" ? definitions.FindEnterprise(content.DefinitionId).Presentation : definitions.FindDepartment(content.DefinitionId).Presentation;
                Assert.That(again.DetailScript, Is.EqualTo(content.Presentation.DetailScript));
            }
        }

        [Test]
        public void RhineDetailsUseOnlyCurrentAuthorizedDepartmentAndNeverExecuteRewards()
        {
            var pack = EnterpriseDetailPack();
            var content = pack.ActiveDefinitions.Single(value => value.DefinitionId == "enterprise.rhine_lab");
            var state = new GameState { GameId = "enterprise-display", Players = { new PlayerState { PlayerId = 1, Score = 2 } } };
            string stateBefore = Newtonsoft.Json.JsonConvert.SerializeObject(state);
            var context = EnterpriseDetailContext(content.DefinitionId);
            foreach (var id in new[] { "enterprise.department.defense", "enterprise.department.engineering", "enterprise.department.structural" })
            {
                context.Facts = NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                    new NormalizedValueEntry { Name = "activeDepartmentDefinitionId", Value = NormalizedValue.CreateString(id) },
                    new NormalizedValueEntry { Name = "activeDepartmentLevelDescriptions", Value = NormalizedValue.CreateArray(new[] { GameplayPresentationNormalizer.ToNormalized(new GameplayTextRef("test:forged")) }) } });
                string before = Newtonsoft.Json.JsonConvert.SerializeObject(context);
                Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var result, out _, out var diagnostic), Is.True, diagnostic);
                Assert.That(result.Details.Select(value => value.Key), Is.EqualTo(new[] { "core:enterprise.rhine_lab.level_1.description" }.Concat(
                    Enumerable.Range(2, 4).Select(level => "core:" + id + ".level_" + level + ".description"))));
                Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(context), Is.EqualTo(before));
                result.Details[1].Key = "test:changed";
                Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var fresh, out _, out diagnostic), Is.True, diagnostic);
                Assert.That(fresh.Details[1].Key, Is.EqualTo("core:" + id + ".level_2.description"));
            }
            context.Facts = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var missing, out _, out _), Is.True);
            Assert.That(missing.Details.Select(value => value.Key), Is.EqualTo(new[] { "core:enterprise.rhine_lab.level_1.description", "core:enterprise.rhine_lab.department_missing.description" }));
            foreach (var invalid in new[] { "enterprise.blacksteel", "enterprise.department.unknown" })
            {
                context.Facts.Properties.Add(new NormalizedValueEntry { Name = "activeDepartmentDefinitionId", Value = NormalizedValue.CreateString(invalid) });
                Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var denied, out _, out _), Is.False);
                Assert.That(denied, Is.Null); context.Facts.Properties.Clear();
            }
            context.SourceDefinitionId = "enterprise.blacksteel";
            Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out _, out _, out _), Is.False);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state), Is.EqualTo(stateBefore));
            var installed = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"));
            context.SourceDefinitionId = "enterprise.rhine_lab";
            Assert.That(installed.TryPresent(content.Presentation.DetailScript, context, out _, out _, out _), Is.False, "未启用企业不能从安装目录显示为本局内容");
        }

        private static ExternalContentPack EnterpriseDetailPack() => ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"))
            .CreateActiveSnapshot(new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise" } });

        private static GameplayPresentationContext EnterpriseDetailContext(string definitionId) => new GameplayPresentationContext {
            Purpose = "enterprise_detail", SourceDefinitionId = definitionId, SourceItemId = "visible." + definitionId,
            ViewerPlayerId = 1, StateRevision = 7 };

        [TestCase("missing_key")]
        [TestCase("missing_argument")]
        [TestCase("missing_description")]
        [TestCase("ability_mismatch")]
        public void EnterpriseDescriptionLoaderRejectsInvalidFormalReferences(string scenario)
        {
            string root = EnterpriseDescriptionFixture(out var content, out var texts);
            var level = content["data"]["levels"][1];
            var ability = content["abilities"][0];
            string key = (string)level["description"]["key"];
            if (scenario == "missing_key")
            {
                level["description"]["key"] = "core:enterprise.missing.description";
                ability["presentation"]["description"] = level["description"].DeepClone();
            }
            else if (scenario == "missing_argument") texts["messages"][key]["parameters"]["required"] = "integer";
            else if (scenario == "missing_description") ((Newtonsoft.Json.Linq.JObject)level).Remove("description");
            else ability["presentation"]["description"]["key"] = "core:enterprise.blacksteel.level_2.description";
            File.WriteAllText(Path.Combine(root, "enterprise.json"), content.ToString());
            File.WriteAllText(Path.Combine(root, "texts.json"), texts.ToString());
            string diagnostic = string.Empty;
            try { ExternalContentPack.Load(root); }
            catch (InvalidDataException error) { diagnostic = error.Message; }
            string expected = scenario == "missing_key" ? "presentation_text_missing:" : scenario == "missing_argument" ? "presentation_argument_signature_mismatch:" :
                scenario == "missing_description" ? "presentation_enterprise_description_missing:" : "presentation_enterprise_ability_description_mismatch:";
            Assert.That(diagnostic.StartsWith(expected, StringComparison.Ordinal), Is.True, scenario + ":" + diagnostic);
        }

        [Test]
        public void EnterpriseDescriptionMappingParticipatesInPresentationHash()
        {
            string root = EnterpriseDescriptionFixture(out var content, out _);
            var before = ExternalContentPack.Load(root);
            content["data"]["levels"][1]["description"] = content["data"]["levels"][2]["description"].DeepClone();
            content["abilities"][0]["presentation"]["description"] = content["data"]["levels"][1]["description"].DeepClone();
            File.WriteAllText(Path.Combine(root, "enterprise.json"), content.ToString());
            var after = ExternalContentPack.Load(root);
            Assert.That(before.PresentationContentHash == after.PresentationContentHash, Is.False);
        }

        private static string EnterpriseDescriptionFixture(out Newtonsoft.Json.Linq.JObject content, out Newtonsoft.Json.Linq.JObject texts)
        {
            string core = Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core");
            content = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(core, "enterprise/enterprise.blacksteel.json")));
            content["artwork"] = "artwork.jpg"; content.Remove("artworkSpriteId");
            texts = new Newtonsoft.Json.Linq.JObject { ["schemaVersion"] = 1, ["locale"] = "zh-CN", ["messages"] = new Newtonsoft.Json.Linq.JObject() };
            foreach (var level in content["data"]["levels"].Where(value => value["description"] != null))
                texts["messages"][(string)level["description"]["key"]] = new Newtonsoft.Json.Linq.JObject {
                    ["parameters"] = new Newtonsoft.Json.Linq.JObject(), ["template"] = "正式等级正文" };
            string root = Path.GetFullPath(Path.Combine("Logs/GameplayPresentation/企业科室正文迁移/加载夹具", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "enterprise.json"), content.ToString());
            File.WriteAllText(Path.Combine(root, "texts.json"), texts.ToString());
            File.WriteAllText(Path.Combine(root, "icons.json"), "{\"schemaVersion\":1,\"icons\":[]}");
            File.WriteAllText(Path.Combine(root, "defaults.json"), "{\"schemaVersion\":1,\"effectTypes\":{},\"interactions\":{},\"outcomes\":{}}");
            File.Copy(Path.Combine(core, "artwork/sheets/enterprise_boards.jpg"), Path.Combine(root, "artwork.jpg"));
            foreach (var ability in content["abilities"])
            {
                string path = Path.Combine(root, (string)ability["script"]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "return function(ctx) return {} end");
            }
            string detail = (string)content["presentation"]["detailScript"];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, detail)));
            File.Copy(Path.Combine(core, detail), Path.Combine(root, detail));
            File.WriteAllText(Path.Combine(root, "pack.json"), new Newtonsoft.Json.Linq.JObject {
                ["schemaVersion"] = 2, ["packId"] = "core", ["version"] = "1", ["definitions"] = new Newtonsoft.Json.Linq.JArray("enterprise.json"),
                ["presentation"] = new Newtonsoft.Json.Linq.JObject { ["schemaVersion"] = 1, ["texts"] = new Newtonsoft.Json.Linq.JArray("texts.json"), ["icons"] = "icons.json", ["defaults"] = "defaults.json" } }.ToString());
            return root;
        }
        #endregion

        #region InfectedEnterpriseDetailMigration
        [Test]
        public void InfectedEnterpriseDescriptionsFollowActiveReplacementAndClone()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"))
                .CreateActiveSnapshot(new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
            Assert.That(pack.ActiveDefinitions.Any(value => value.DefinitionId == "enterprise.rhodes_island"), Is.False);
            var definitions = LuaContentCatalog.CreateEnterpriseDefinitions(new EffectRegistry(), pack.ActiveDefinitions, pack.GetScript);
            foreach (var identity in new[] { "infected.enterprise.rhodes_island", "infected.enterprise.maylander_foundation", "infected.enterprise.stonehoove_security" })
            {
                var content = pack.ActiveDefinitions.Single(value => value.DefinitionId == identity);
                var context = new GameplayPresentationContext { Purpose = "enterprise_detail", SourceDefinitionId = identity,
                    SourceItemId = "visible." + identity, ViewerPlayerId = 1, StateRevision = 9 };
                Assert.That(pack.TryPresent(content.Presentation.DetailScript, context, out var result, out _, out var diagnostic), Is.True, diagnostic);
                Assert.That(result.Details.Select(value => value.Key), Is.EqualTo(Enumerable.Range(1, 5).Select(level => "core:" + identity + ".level_" + level + ".description")));
                Assert.That(result.Resolution, Is.Null); Assert.That(result.Interaction, Is.Null);
                foreach (var level in content.Data["levels"].Where(value => (int)value["level"] > 0))
                {
                    string key = (string)level["description"]["key"];
                    Assert.That(pack.Presentation.TryGetTemplate(key, out _), Is.True, key);
                    Assert.That(content.Abilities.Single(value => value.AbilityId == (string)level["abilityId"]).Presentation.Description.Key, Is.EqualTo(key));
                }
                var copy = definitions.FindEnterprise(identity);
                Assert.That(copy.Levels.Single(level => level.Level == 0).Description, Is.Null);
                copy.Levels[1].Description.Key = "test:changed"; copy.Presentation.DetailScript = "changed";
                var fresh = definitions.FindEnterprise(identity);
                Assert.That(fresh.Levels.Single(level => level.Level == 1).Description.Key, Is.EqualTo("core:" + identity + ".level_1.description"));
                Assert.That(fresh.Presentation.DetailScript, Is.EqualTo("lua/presentation/enterprise.details.lua"));
            }
            var old = new GameplayPresentationContext { Purpose = "enterprise_detail", SourceDefinitionId = "enterprise.rhodes_island", ViewerPlayerId = 1, StateRevision = 9 };
            Assert.That(pack.TryPresent("lua/presentation/enterprise.details.lua", old, out _, out _, out _), Is.False);
        }

        [TestCase("blacksteel")]
        [TestCase("defense")]
        public void FormalInfluenceChoiceDecorationKeepsBusinessAndCandidateProjection(string choice)
        {
            bool defense = choice == "defense";
            string prefix = defense ? "core:enterprise.department.defense.level_3.choice" : "core:enterprise.blacksteel.level_1.choice";
            string path = defense ? "lua/departments/enterprise.department.defense.level_3.reward.lua" : "lua/enterprises/enterprise.blacksteel.level_1.special.lua";
            string last = defense ? "move" : "remove";
            var pack = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"))
                .CreateActiveSnapshot(new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise" } });
            // 独立保留修改前的完整业务式，比较整个Condition/Choice及全部child，而非只比标签。
            string originalChoice = "Effect.Choice({player=ctx.playerId," + (defense ? "" : "promptKey='effect.influence.place_or_remove',") + "branches={" +
                "{id='place',effects={Effect.PlaceInfluence({executingPlayer=ctx.playerId,influenceSource='player_supply',ownerSubject=ctx.playerId,causeKind='other_intrinsic_flow'})}}," +
                "{id='" + last + "',effects={Effect.SelectInfluence({player=ctx.playerId,operation='" + last + "',subjectFilter='" + (defense ? "self" : "any") +
                "',count=" + (defense ? "2" : "1") + ",requiredCount=0,distinctPolicy='instance',promptKey='effect.influence.choose'})}}}})";
            string original = "return function(ctx) return {" + (defense ? "Effect.Condition({leftEffects={Effect.PayResource({payer=ctx.playerId,resourceType='gold_voucher',amount=3})},rightEffects={" + originalChoice + "}})" : originalChoice) + "} end";
            LuaInvocationResult InvokeChoice(string source) => new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768)).Invoke(
                new LuaScriptDefinition("enterprise.display", "enterprise.choice", "enterprise.choice.handler", "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("enterprise.display.event", "EnterpriseSpecialEffectActivated", "p1", 3, "1.0.0", 2, 0, 0));
            var before = InvokeChoice(original); var actual = InvokeChoice(pack.GetScript(path));
            Assert.That(before.IsSuccess, Is.True, before.Diagnostic); Assert.That(actual.IsSuccess, Is.True, actual.Diagnostic);
            var expectedBusiness = Newtonsoft.Json.Linq.JToken.FromObject(before.Effects);
            var actualBusiness = Newtonsoft.Json.Linq.JToken.FromObject(actual.Effects);
            RemoveChoicePresentation(expectedBusiness); RemoveChoicePresentation(actualBusiness);
            Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(expectedBusiness, actualBusiness), Is.True, "完整原业务树／支付／数量／主体／候选须相同");
            var compiler = new LuaEffectSpecCompiler(presentationPack: pack);
            Assert.That(compiler.TryCompile(actual, new LuaEffectCompilationContext { PlayerId = 1, SourceId = "visible.enterprise" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var compiledChoice = defense ? specs[0].NestedEffectGroups[1][0] : specs[0];
            Assert.That(compiledChoice.Presentation.Interaction.Prompt.Key, Is.EqualTo(prefix + ".prompt"));
            Assert.That(compiledChoice.Presentation.Interaction.Options.Select(value => value.CandidateId), Is.EqualTo(new[] { "place", last }));
            foreach (var option in compiledChoice.Presentation.Interaction.Options) Assert.That(option.Label.Key, Is.EqualTo(prefix + "." + option.CandidateId));
            Assert.That(compiledChoice.AllowDecline, Is.False);
            // 仅运行已到达的Choice子树；防卫费用顺序由上面的完整原式比较核验，不冒充整段费用执行。
            var state = new GameState { GameId = "choice.display", Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, state, new YC.Domain.Maps.MapQueryService(YC.Domain.Maps.StaticMapDefinitions.CreateThreePlayerMap()));
            var executor = new EffectTreeExecutor(state, registry); executor.CreateRoot(compiledChoice);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "place", last }));
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo(prefix + ".prompt"));
            Assert.That(request.MinSelections, Is.EqualTo(1)); Assert.That(request.MaxSelections, Is.EqualTo(1)); Assert.That(request.AllowDecline, Is.False);
            var own = InteractionRequestProjector.ProjectForPlayer(request, 1);
            Assert.That(own.Presentation.Options.Select(value => value.CandidateId), Is.EqualTo(request.CandidateIds));
            Assert.That(own.Presentation.Prompt.Key, Is.EqualTo(prefix + ".prompt"));
            Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 2).Presentation, Is.Null);
        }

        private static void RemoveChoicePresentation(Newtonsoft.Json.Linq.JToken token)
        {
            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                foreach (var property in obj.Properties().ToArray())
                    if (property.Name == "Presentation") property.Remove(); else RemoveChoicePresentation(property.Value);
            }
            else if (token is Newtonsoft.Json.Linq.JArray array) foreach (var item in array) RemoveChoicePresentation(item);
        }
        #endregion

        private static MoonSharpLuaRuntimeHost Host() => new MoonSharpLuaRuntimeHost(new LuaExecutionBudget(20000, 1000, 12, 512, 16, 32768));
        private static LuaScriptDefinition Definition(string source) => new LuaScriptDefinition("test.presentation", "test.ability", "test.handler", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
        private static LuaInvocationContext Invocation(string response = "effects") => new LuaInvocationContext("test.event", "test.event_type", "p1", 3, "1.0.0", 2, 0, 0)
        { ResponseKind = response, SettlementContext = new SettlementWindowContext { Id = "test.window", Kind = SettlementEntryService.CharacterUse, OwnerEffectId = "test.owner", PlayerId = 1, StateRevision = 3 } };
        private static GameplayPresentationContext Display() => new GameplayPresentationContext
        { Purpose = "interaction", ViewerPlayerId = 1, StateRevision = 3, Facts = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
        { new NormalizedValueEntry { Name = "candidateIds", Value = NormalizedValue.CreateArray(new List<NormalizedValue> { NormalizedValue.CreateString("allowed") }) } }) };
        private static GameplayTextRef EntityText() => new GameplayTextRef("test:item", new GameplayTextArgument { Name = "source", Kind = "item", Value = NormalizedValue.CreateStableReference("item", "secret.item") });

        [Test]
        public void NormalizationRoundTripIsDetachedAndRejectsUnknownFields()
        {
            var source = EntityText();
            var normalized = GameplayPresentationNormalizer.ToNormalized(source);
            var copy = GameplayPresentationNormalizer.FromNormalized<GameplayTextRef>(normalized);
            copy.Arguments[0].Value = NormalizedValue.CreateStableReference("item", "changed");
            Assert.That(source.Arguments[0].Value.ReferenceId, Is.EqualTo("secret.item"));
            normalized.Properties.Add(new NormalizedValueEntry { Name = "hiddenState", Value = NormalizedValue.CreateInteger(1) });
            Assert.Throws<ArgumentException>(() => GameplayPresentationNormalizer.FromNormalized<GameplayTextRef>(normalized));
        }

        [Test]
        public void ProjectionRequiresEntityPermissionAndTrimsAuthoritativeSets()
        {
            var source = new GameplayInteractionPresentation { Prompt = EntityText(),
                Options = new List<GameplayOptionPresentation> { new GameplayOptionPresentation { CandidateId = "allowed", Label = new GameplayTextRef("test:allowed") }, new GameplayOptionPresentation { CandidateId = "forbidden" } },
                QuantityRows = new List<GameplayQuantityRowPresentation> { new GameplayQuantityRowPresentation { RowId = "iron" }, new GameplayQuantityRowPresentation { RowId = "gold" } } };
            var safe = GameplayPresentationNormalizer.ProjectInteraction(source, true, new[] { "allowed" }, new[] { "iron" });
            Assert.That(safe.Prompt, Is.Null);
            Assert.That(safe.Options.Select(o => o.CandidateId), Is.EqualTo(new[] { "allowed" }));
            Assert.That(safe.QuantityRows.Select(o => o.RowId), Is.EqualTo(new[] { "iron" }));
            var authorized = GameplayPresentationNormalizer.ProjectInteraction(source, true, new[] { "allowed" }, new[] { "iron" }, a => a.Value.ReferenceId == "secret.item");
            Assert.That(authorized.Prompt.Arguments[0].Value.ReferenceId, Is.EqualTo("secret.item"));
            Assert.That(GameplayPresentationNormalizer.ProjectInteraction(source, false, new[] { "allowed" }), Is.Null);
            Assert.That(source.Options.Count, Is.EqualTo(2));
        }

        [Test]
        public void HiddenInteractionDoesNotLeakDisplayOrStableIds()
        {
            var request = new InteractionRequest { RequestId = "secret.request", InteractionId = "secret.request", OwnerEffectId = "secret.owner", AnsweringPlayerId = 1, Visibility = "owner", Status = "open", Presentation = new GameplayInteractionPresentation { Prompt = EntityText() }, CandidateIds = new List<string> { "secret.item" } };
            var view = InteractionRequestProjector.ProjectForPlayer(request, 2);
            Assert.That(view.VisibleToViewer, Is.False);
            Assert.That(view.Presentation, Is.Null);
            Assert.That(view.InteractionId, Is.Empty);
            Assert.That(view.OwnerEffectId, Is.Empty);
            Assert.That(view.CandidateIds, Is.Empty);
        }

        [Test]
        public void LuaDecoratorPreservesBusinessDefinitionAndDisplayPersistence()
        {
            const string source = "return function(ctx) return { Effect.WithPresentation(Effect.GainScore{recipient='p1',amount=2,reasonId='test.reason'}, {started=Text.Ref('test:started',{player=Text.Player(1),amount=2})}) } end";
            var result = Host().Invoke(Definition(source), Invocation());
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Count, Is.EqualTo(1));
            Assert.That(result.Effects[0].Amount, Is.EqualTo(2));
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1, SourceId = "source.item", AbilityId = "test.ability" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            var persisted = specs[0].ToPersistentDefinition();
            Assert.That(persisted.Presentation.Started.Arguments.Single(a => a.Name == "player").Value.ReferenceId, Is.EqualTo("p1"));
            var restored = GameStateCloneService.DeepClone(persisted);
            restored.Presentation.Started.Key = "test:changed";
            Assert.That(persisted.Presentation.Started.Key, Is.EqualTo("test:started"));
            Assert.That(restored.EffectTypeId, Is.EqualTo("effect.score.gain"));
        }

        [Test]
        public void PresentOnlyReturnsAuthorizedDisplayAndHasNoWritableContext()
        {
            const string valid = "function Present(ctx) return {resolution=Text.Ref('test:message',{player=Text.Player(1)}),interaction={options={{candidateId='allowed',label=Text.Ref('test:choice')}}}} end";
            Assert.That(Host().TryPresent(Definition(valid), Display(), out var result, out var code, out var diagnostic), Is.True, diagnostic);
            Assert.That(result.Interaction.Options[0].CandidateId, Is.EqualTo("allowed"));
            foreach (var body in new[] { "ctx.stateRevision=9; return {}", "return {resolution=Effect.GainScore{recipient='p1',amount=1}}", "return {gameState={}}", "return {interaction={options={{candidateId='forbidden'}}}}", "return {interaction={quantityRows={{rowId='forbidden'}}}}" })
            {
                string script = "function Present(ctx) " + body + " end";
                Assert.That(Host().TryPresent(Definition(script), Display(), out result, out code, out diagnostic), Is.False, body);
                Assert.That(result, Is.Null);
            }
        }

        [Test]
        public void AvailabilityUsesNamedEntryPointAndCannotReturnBusinessEffects()
        {
            const string valid = "return {Available=function(ctx) return {enabled=true} end}";
            var context = Invocation("settlementAvailability"); context.EntryPoint = "Available";
            var result = Host().Invoke(Definition(valid), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.SettlementAvailability.Enabled, Is.True);
            Assert.That(result.Effects, Is.Empty);
            foreach (var body in new[] { "return {enabled=true,effects={}}", "return {enabled=true,disabledReasonKey='固定中文'}", "return {enabled=true,result=Effect.GainScore{recipient='p1',amount=1}}" })
                Assert.That(Host().Invoke(Definition("return {Available=function(ctx) " + body + " end}"), context).IsSuccess, Is.False, body);
            Assert.That(Host().Invoke(Definition("return function(ctx) return {} end"), Invocation("unknownProtocol")).IsSuccess, Is.False);
        }

        [Test]
        public void OffersHoldUninstantiatedDefinitionsAndRejectSparseBusinessArrays()
        {
            const string source = "return function(ctx) return {Settlement.Offer{apiVersion=1,player='p1',localId='test.ability.offer',sourceItemId='source.item',windowId=ctx.window.id,ownerEffectId=ctx.window.ownerEffectId,maxUses=1,availabilityHandlerId='test.available',visibility='owner',presentation={label=Text.Ref('test:label'),description=Text.Ref('test:description'),ariaLabel=Text.Ref('test:aria')},effects={Effect.GainScore{recipient='p1',amount=2,reasonId='test.reason'}}}} end";
            var result = Host().Invoke(Definition(source), Invocation("settlementOffers"));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects, Is.Empty);
            Assert.That(result.SettlementOffers.Count, Is.EqualTo(1));
            Assert.That(result.SettlementOffers[0].Effects.Count, Is.EqualTo(1));
            string bad = source.Replace("effects={Effect.GainScore", "effects={hidden=1,Effect.GainScore");
            Assert.That(Host().Invoke(Definition(bad), Invocation("settlementOffers")).IsSuccess, Is.False);
            Assert.That(Host().Invoke(Definition(source), Invocation()).IsSuccess, Is.False);
        }

        [Test]
        public void BoundedAllocationRejectsDuplicateRowsAndRespectsIndividualCost()
        {
            var ids = new[] { "iron", "originium" }; var limits = new Dictionary<string, int> { ["iron"] = 1, ["originium"] = 2 };
            Assert.That(ResourceAllocationAnswer.TryParseBounded("iron=1,originium=1", ids, 0, 2, limits, out _, out _), Is.True);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("", ids, 0, 2, limits, out _, out _), Is.True);
            foreach (string value in new[] { "iron=2", "iron=1,iron=1", "iron=-1", "gold=1", "originium=2,iron=1" })
                Assert.That(ResourceAllocationAnswer.TryParseBounded(value, ids, 0, 2, limits, out _, out _), Is.False, value);
            Assert.That(ResourceAllocationAnswer.TryParse("iron=1", ids, 2, out _, out _), Is.False);
            Assert.That(ResourceAllocationAnswer.TryParse("iron=1,originium=1", ids, 2, out _, out _), Is.True);
        }

        [Test]
        public void ControlledSettlementConstructorsUseCanonicalTypesAndRejectCrossPlayer()
        {
            string source = "return function(ctx) return {Effect.ConsumeCharacterDoubleUseSource{apiVersion=1,player='p1',useId='use',sourceItemId='source'},Effect.PaymentSubstitution{apiVersion=1,player='p1',paymentOwnerEffectId='owner',sourceZoneId='markers',substitutionBudget=2}} end";
            var result = Host().Invoke(Definition(source), Invocation());
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { PlayerId = 1 }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Assert.That(specs.Select(s => s.EffectTypeId), Is.EqualTo(new[] { YC.Domain.Cards.CharacterLifecycleService.ConsumeSourceTypeId, ResourcePaymentSubstitutionEffectExecutor.TypeId }));
            var registry = new EffectRegistry(); SettlementHostProtocol.Register(registry);
            foreach (var spec in specs) { Assert.That(registry.TryGet(spec.EffectTypeId, out var registration), Is.True); Assert.That(registration.Validator(spec), Is.Empty); }
            foreach (string bad in new[] { source.Replace("player='p1'", "player='p2'"), source.Replace("substitutionBudget=2", "substitutionBudget=3"), source.Replace("useId='use'", "scope='current_use',useId='use'") })
                Assert.That(Host().Invoke(Definition(bad), Invocation()).IsSuccess, Is.False);
        }

        [Test]
        public void ResolutionFeedbackExpiresAndCannotReplaceCurrentInteraction()
        {
            var state = new GameState { GameId = "test.game", Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } } };
            state.EffectRuntime.StateRevision = 1; state.EffectRuntime.NextCommitSequence = 2;
            var node = new EffectNodeRuntimeState { EffectId = "test.completed", PlayerId = 1, Visibility = "public", Status = EffectNodeStatus.Completed, LastCommitSequence = 1,
                Presentation = new EffectPresentationDefinition { Succeeded = new GameplayTextRef("test:completed"), Stopped = new GameplayTextRef("test:stopped") } };
            state.EffectRuntime.EffectNodes.Add(node);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).ResolutionMessage?.Message.Key, Is.EqualTo("test:completed"));
            state.EffectRuntime.StateRevision = 2; state.EffectRuntime.NextCommitSequence = 3;
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).ResolutionMessage, Is.Null);
            node.LastCommitSequence = 2; node.Status = EffectNodeStatus.Failed; node.FailureReason = "player_declined";
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).ResolutionMessage?.Message.Key, Is.EqualTo("test:stopped"));
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest { RequestId = "test.current", InteractionId = "test.current", OwnerEffectId = "test.current.owner", AnsweringPlayerId = 1, Visibility = "public", Status = "open" });
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).ResolutionMessage, Is.Null);
        }

        [Test]
        public void ActualCoreCatalogExposesDetachedTemplatesAndValidIconReferences()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"));
            IGameplayTextCatalogSnapshot snapshot = pack.Presentation;
            Assert.That(snapshot.Texts.Count > 0, Is.True);
            Assert.That(pack.PresentationContentHash, Has.Length.EqualTo(64));
            // 目录缓存使用实际内容包身份；显示子集哈希另进入 active 内容指纹。
            Assert.That(snapshot.ContentHash, Is.EqualTo(pack.ContentHash));
            var original = snapshot.Texts[0]; string key = original.Key, text = original.Template; original.Template = "修改副本";
            Assert.That(snapshot.TryGetTemplate(key, out var current), Is.True);
            Assert.That(current.Template, Is.EqualTo(text));
            foreach (var icon in pack.Presentation.Icons) Assert.That(snapshot.TryGetTemplate(icon.AltText.Key, out _), Is.True, icon.Id);
        }

        private static (GameState State, EffectRegistry Registry, EffectTreeExecutor Executor) CleanupFixture(bool required, bool wait, bool allowDecline = true)
        {
            var state = new GameState { GameId = "settlement.host.fixture", Phase = GamePhase.Cleanup, CurrentPlayerId = 1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var registry = new EffectRegistry();
            SettlementHostProtocol.Register(registry);
            ResourceEffectExecutor.Register(registry);
            registry.RegisterSettlementCatalogProvider("fixture.catalog", (s, w) => { }, (s, w, e, f) => new SettlementAvailabilityResult { Enabled = true });
            registry.Register(new EffectRegistration("fixture.wait", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("waiting").AddInteraction(
                new EffectInteractionSpec { InteractionTypeId = "fixture.choice", AnsweringPlayerId = 1, AllowDecline = allowDecline,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "continue" } })
                : EffectStepResult.Failed("player_declined"), EffectExecutorKind.IntrinsicFlow, "1.0.0"));
            registry.Register(new EffectRegistration("fixture.cleanup", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("settling").AddEvent(
                new EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = 1 }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow, "1.0.0"));
            for (int i = 0; i < 2; i++)
            {
                string ability = "fixture.ability." + i;
                var policy = i == 1 && required ? SettlementWholeEffectPolicy.Required : SettlementWholeEffectPolicy.Optional;
                registry.RegisterSettlementSourceContract(new SettlementSourceContract("fixture.definition." + i, ability,
                    ability + ".cleanup", SettlementEntryService.CleanupOrder, ability + ".available", policy));
                bool hasWait = wait && i == 0;
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(ability, "PlayerCleanupStarted", "", ability, c =>
                {
                    var body = new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, hasWait ? 2 : 7) };
                    if (hasWait) body.Add(EffectSpec.Create("fixture.wait", playerId: 1));
                    var root = SettlementEntryService.CreateCleanupSegment(1, ability + ".cleanup", body);
                    root.Settlement = new SettlementEffectDefinition { EntryKind = SettlementEntryService.CleanupOrder, AbilityId = ability,
                        LocalId = ability + ".cleanup", SourceItemId = ability + ".item", SourceEffectId = ability + ".activation", WholeEffectPolicy = policy,
                        Presentation = new SettlementPresentationDefinition { Label = new GameplayTextRef("fixture:label"), Description = new GameplayTextRef("fixture:description"), AriaLabel = new GameplayTextRef("fixture:aria") } };
                    return new List<EffectSpec> { root };
                }, EffectHandlerRole.Observer, abilityId: ability));
            }
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("fixture.cleanup", playerId: 1));
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(report.WaitingForInput, Is.True);
            return (state, registry, executor);
        }

        [Test]
        public void ActualInfectedCatalogRegistersAllSettlementContractsAndRecipe()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"));
            var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var active = ActiveContentCatalogBuilder.Build(pack, configuration);
            var registry = new EffectRegistry(); active.Register(registry);
            var resolver = new YC.Application.Presentation.GameplayTextResolver(active.TextCatalog);
            resolver.Precompile();
            Assert.That(resolver.CompiledTemplateCount, Is.EqualTo(active.TextCatalog.Texts.Count));
            Assert.That(active.Definitions.Count(d => d.ExpansionId == "infected"), Is.EqualTo(30));
            Assert.That(active.SetupRecipe.RecipeId, Is.EqualTo("infected"));
            Assert.That(active.Definitions.Count(d => d.DefinitionId == "enterprise.rhodes_island"), Is.EqualTo(0));
            var sources = active.Definitions.SelectMany(d => d.Abilities).Where(a => a.Settlement != null).ToList();
            Assert.That(sources.Count, Is.EqualTo(13));
            Assert.That(sources.Count(a => a.Settlement.WholeEffectPolicy == "optional"), Is.EqualTo(8));
            Assert.That(sources.Count(a => a.Settlement.WholeEffectPolicy == "required"), Is.EqualTo(3));
            foreach (var ability in sources)
            {
                Assert.That(registry.TryGetSettlementSourceContract(ability.AbilityId, out var contract), Is.True, ability.AbilityId);
                Assert.That(contract.LocalId, Is.EqualTo(ability.Settlement.LocalId));
                Assert.That(registry.TryGetSettlementAvailabilityHandler(contract.AvailabilityHandlerId, out _), Is.True, ability.AbilityId);
                if (ability.Settlement.EntryKind == SettlementEntryService.OptionalInsertion)
                    Assert.That(registry.TryGetSettlementOfferProvider(ability.Settlement.OfferHandlerId, out _), Is.True, ability.AbilityId);
            }
            Assert.That(registry.SettlementCatalogContentHash, Is.EqualTo(active.ContentPackHash));
        }

        private static (GameState State, EffectRegistry Registry, EffectTreeExecutor Executor) InfectedPartialFixture()
        {
            // 正式配置、目录、配方与执行器的定向片段，不计完整对局覆盖。
            var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var active = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core")), configuration);
            configuration.ContentPackHash = active.ContentPackHash;
            var state = active.SetupRecipe.Assemble(new GameState { GameId = "settlement.actual.partial", MapId = configuration.MapId,
                RuleConfiguration = configuration, ContentPackHash = active.ContentPackHash, ContentDefinitions = active.ContentDefinitions,
                Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1,
                LaunchPlayerOrder = new List<int> { 1, 2, 3 }, Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id }).ToList() },
                StaticMapDefinitions.CreateThreePlayerMap(), 17);
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry);
            ContentPrimitiveEffectExecutor.Register(registry); CharacterContentEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, state, new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap()));
            CityStyleSpecialActionEffectExecutor.RegisterCleanup(registry);
            CharacterCoverEffectExecutor.Register(registry); active.Register(registry);
            return (state, registry, new EffectTreeExecutor(state, registry));
        }
        private static void AnswerActual(GameState state, EffectTreeExecutor executor, string answer)
        {
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, state.EffectRuntime.StateRevision,
                NormalizedValue.CreateString(answer), out var reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private static void CoverActual(GameState state, EffectTreeExecutor executor, string itemId)
        {
            executor.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            AnswerActual(state, executor, itemId);
        }

        [Test]
        public void ActualAmiyaOfferRunsSourceConsumptionAndSurvivesHostRecovery()
        {
            var fixture = InfectedPartialFixture();
            var state = fixture.State; var executor = fixture.Executor;
            string amiya = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.character.amiya" && i.ControllerSubject.PlayerId == 1).ItemId;
            CoverActual(state, executor, amiya);
            executor.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, amiya, "tactic"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, amiya, "infected.character.amiya.tactic"), Is.True);
            string second = state.ItemZones.Items.First(i => i.TypeId == "core.basic_character_card" && i.ControllerSubject.PlayerId == 1 &&
                i.ItemId != amiya && state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.character_hand")).ItemId;
            CoverActual(state, executor, second);
            executor.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, second));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var window = SettlementHostProtocol.FindActiveWindow(state);
            Assert.That(window.Entries.Count, Is.EqualTo(1));
            Assert.That(window.Entries[0].AbilityId, Is.EqualTo("infected.character.amiya.tactic"));
            Assert.That(fixture.Registry.GetSettlementOptions(state, 1).Single().IsEnabled, Is.True);
            Assert.That(fixture.Registry.GetSettlementOptions(state, 2), Is.Empty);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(state, fixture.Registry, out var origin), Is.True, origin);
            var archive = new GameSession(state).CreateHostArchive();
            var session = new GameSession(GameStateCloneService.DeepClone(state));
            var recovered = session.RecoverHost(archive, fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var recoveredExecutor = new EffectTreeExecutor(session.State, fixture.Registry);
            window = SettlementHostProtocol.FindActiveWindow(session.State);
            int nodesBefore = session.State.EffectRuntime.EffectNodes.Count;
            Assert.That(recoveredExecutor.TryInvokeCharacterSettlementOption(1, window.Entries[0].OptionId, window.Context.TargetUseId,
                session.State.EffectRuntime.StateRevision, out _, out var reason), Is.True, reason);
            Assert.That(recoveredExecutor.RunUntilQuiescent().Faulted, Is.False, session.State.EffectRuntime.LastFaultMessage);
            var use = session.State.CharacterUses.Single(u => u.UseId == window.Context.TargetUseId);
            Assert.That(use.SourceConsumed && use.DoubleUseGranted, Is.True);
            Assert.That(session.State.EffectRuntime.EffectNodes.Count > nodesBefore, Is.True);
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(session.State, amiya, "infected.character.amiya.tactic"), Is.False);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(session.State, fixture.Registry, out origin), Is.True, origin);
            var changed = GameStateCloneService.DeepClone(session.State);
            changed.EffectRuntime.EffectNodes.Single(n => n.SettlementWindow?.Context.Id == window.Context.Id).SettlementWindow.Entries[0].SourceEffectId = "forged.source";
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(changed, fixture.Registry, out _), Is.False);
        }

        [Test]
        public void ActualDelayedCleanupKeepsDepartedSourceAndRejectsForgedEventReceipt()
        {
            var fixture = InfectedPartialFixture(); var state = fixture.State; var executor = fixture.Executor;
            string sourceItemId = state.ItemZones.Items.Single(i => i.DefinitionId == "liskarm" && i.ControllerSubject.PlayerId == 1).ItemId;
            CoverActual(state, executor, sourceItemId);
            executor.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, sourceItemId, "strategy"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            for (int i = 0; i < 4 && state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"); i++)
                AnswerActual(state, executor, state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open").CandidateIds.First());
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, sourceItemId, "character.liskarm.strategy"), Is.False);
            fixture.Registry.Register(new EffectRegistration("fixture.actual.cleanup_event", c => c.Node.FlowStage == "" ? EffectStepResult.Continue("settling").AddEvent(
                new EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = 1 }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow));
            executor.CreateRoot(EffectSpec.Create("fixture.actual.cleanup_event", playerId: 1));
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(report.WaitingForInput, Is.True);
            var window = SettlementHostProtocol.FindActiveWindow(state); var entry = window.Entries.Single();
            Assert.That(entry.AbilityId, Is.EqualTo("character.liskarm.strategy"));
            Assert.That(entry.WholeEffectPolicy, Is.EqualTo(SettlementWholeEffectPolicy.Required));
            Assert.That(executor.GetNode(entry.TargetEffectId).Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(fixture.Registry.GetSettlementOptions(state, 1).Single().IsEnabled, Is.True);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(state, fixture.Registry, out var reason), Is.True, reason);
            var session = new GameSession(GameStateCloneService.DeepClone(state));
            var recovered = session.RecoverHost(new GameSession(state).CreateHostArchive(), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var changed = GameStateCloneService.DeepClone(state);
            changed.EffectRuntime.DispatchReceipts.RemoveAll(r => r.EventId == window.Context.TriggerId && r.AttachedEffectIds.Contains(entry.TargetEffectId));
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(changed, fixture.Registry, out _), Is.False);
            changed = GameStateCloneService.DeepClone(state);
            changed.EffectRuntime.EffectNodes.Single(n => n.EffectId == entry.SourceEffectId).TimingBindings[0].AbilityId = "forged.ability";
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(changed, fixture.Registry, out _), Is.False);
        }

        [Test]
        public void PaymentDraftUsesOriginalRequestAndPaysOnlyAtItsFinalAnswer()
        {
            var fixture = InfectedPartialFixture(); var state = fixture.State; var executor = fixture.Executor;
            executor.CreateRoot(ResourceEffectSpecFactory.Gain(1, ResourceType.Originium, 3));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            ResourcePaymentChoiceEffectExecutor.Register(fixture.Registry);
            string ownerId = executor.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1, new[] {
                new ResourceSet { Originium = 1 }, new ResourceSet { Originium = 2 } }, "fixture.payment"));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            var options = ItemTransferEffectExecutor.Value(request.PromptParameters, "paymentOptions");
            Assert.That(options.Items.Count, Is.EqualTo(2));
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:resource.payment.choice.prompt"));
            string candidate = ItemTransferEffectExecutor.Text(options.Items.First(o => ItemTransferEffectExecutor.Number(ItemTransferEffectExecutor.Value(o, "cost"), "originium") == 1), "candidateId");
            var session = new GameSession(state); session.RegisterHandler(new PreparePaymentDraftCommandHandler(fixture.Registry));
            var command = new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = 1, Parameters = new Dictionary<string, string> {
                ["ownerEffectId"] = ownerId, ["interactionId"] = request.GetStableInteractionId(), ["candidateId"] = candidate,
                ["expectedRevision"] = state.EffectRuntime.StateRevision.ToString() } };
            Assert.That(session.Submit(command).Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open").GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(executor.GetNode(ownerId).Payment.PaymentCost.Originium, Is.EqualTo(1));
            var old = GameStateCloneService.DeepClone(state);
            string windowId = SettlementHostProtocol.FindActiveWindow(state).Context.Id;
            command.Parameters["candidateId"] = request.CandidateIds.Single(c => c != candidate);
            Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(old.EffectRuntime.StateRevision));
            command.Parameters["expectedRevision"] = state.EffectRuntime.StateRevision.ToString();
            Assert.That(session.Submit(command).Succeeded, Is.True);
            Assert.That(executor.GetNode(ownerId).Payment.PaymentCost.Originium, Is.EqualTo(2));
            Assert.That(executor.GetNode(ownerId).Payment.PaymentId, Is.EqualTo(ownerId));
            Assert.That(SettlementHostProtocol.FindActiveWindow(state).Context.Id, Is.EqualTo(windowId));
            Assert.That(SettlementHostProtocol.FindActiveWindow(state).Context.PaymentCost.Originium, Is.EqualTo(2));
            var current = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(current.GetStableInteractionId(), Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(current.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            Assert.That(ItemTransferEffectExecutor.Text(ItemTransferEffectExecutor.Value(current.PromptParameters, "paymentDraft"), "candidateId"), Is.EqualTo(command.Parameters["candidateId"]));
            Assert.That(ItemTransferEffectExecutor.Flag(ItemTransferEffectExecutor.Value(current.PromptParameters, "paymentDraft"), "canChange"), Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            AnswerActual(state, executor, command.Parameters["candidateId"]);
            Assert.That(executor.GetNode(ownerId).Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => ResourcePaymentSubstitutionEffectExecutor.InitializePayment(executor.GetNode(ownerId), new ResourceSet { Originium = 1 }));
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
        }

        private static (GameState State, EffectRegistry Registry, EffectTreeExecutor Executor, string SourceZoneId) ActiveActualPassengerFixture()
        {
            var fixture = InfectedPartialFixture(); var state = fixture.State; var executor = fixture.Executor;
            string passenger = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.passenger").ItemId;
            string outgoing = state.ItemZones.Items.First(i => i.ControllerSubject.PlayerId == 1 &&
                state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.character_hand")).ItemId;
            executor.CreateRoot(EffectSpec.Create(OperatorDispatchFlow.ReplaceTypeId,
                ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1")), 1));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            AnswerActual(state, executor, passenger); AnswerActual(state, executor, outgoing);
            CoverActual(state, executor, passenger);
            executor.CreateRoot(CharacterLifecycleService.CreateUseSpec(1, passenger, "tactic"));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            AnswerActual(state, executor, "3");
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, passenger, "operator.passenger.tactic"), Is.True);
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == passenger && z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution");
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, zone, 1).Count, Is.EqualTo(3));
            executor.CreateRoot(ResourceEffectSpecFactory.Gain(1, ResourceType.Originium, 3));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            return (state, fixture.Registry, executor, zone.ZoneId);
        }

        private static PaymentSourceRow ActualPassengerSource(GameState state, EffectRegistry registry, string ownerId)
        {
            var executor = new EffectTreeExecutor(state, registry); var owner = executor.GetNode(ownerId);
            Assert.That(owner.Payment.PaymentProtocolVersion, Is.EqualTo(2));
            var input = state.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == ownerId && r.Status == "open");
            var query = new PaymentSourceQuery { PlayerId = 1, OwnerEffectId = ownerId, InteractionId = input.GetStableInteractionId(),
                StateRevision = state.EffectRuntime.StateRevision, PaymentGeneration = owner.Payment.PaymentGeneration,
                SnapshotId = ItemTransferEffectExecutor.Text(ItemTransferEffectExecutor.Value(input.PromptParameters, "paymentSourceQuery"), "snapshotId") };
            PaymentSourceRow result = null;
            do
            {
                Assert.That(executor.TryQueryPaymentSources(query, out var page, out string reason), Is.True, reason);
                foreach (var row in page.Sources.Where(r => r.Rule.SourceRef.AbilityId == "operator.passenger.payment"))
                {
                    if (result == null) result = GameStateCloneService.DeepClone(row);
                    else result.AvailableItems.AddRange(row.AvailableItems);
                }
                if (page.NextSourceIndex == -1) break;
                Assert.That(page.NextSourceIndex > query.SourceIndex || page.NextSourceIndex == query.SourceIndex && page.NextItemOffset > query.ItemOffset, Is.True);
                query.SourceIndex = page.NextSourceIndex; query.ItemOffset = page.NextItemOffset;
            } while (true);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.AvailableItems.Select(i => i.ItemId).Distinct().Count(), Is.EqualTo(result.AvailableItemCount));
            return result;
        }
        private static void EditActualPassenger(GameState state, EffectRegistry registry, string ownerId, string operation, ResourceSet allocation = null)
        {
            var source = ActualPassengerSource(state, registry, ownerId); var executor = new EffectTreeExecutor(state, registry);
            var owner = executor.GetNode(ownerId); var edit = owner.Payment.SourceEdits.SingleOrDefault(e => e.Status == "open");
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.OwnerEffectId == ownerId);
            Assert.That(executor.TryEditPaymentSource(1, ownerId, request.GetStableInteractionId(), operation, source.Rule.SourceRef.SourceKey,
                source.Rule.RuleId, edit?.EditId ?? "", owner.Payment.PaymentGeneration, source.DraftRevision, allocation ?? new ResourceSet(),
                Array.Empty<string>(), state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private static void AssertActualPassengerReceipt(GameState state, string ownerId, int count)
        {
            var payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            var receipt = payment.SourceReceipts.Single();
            Assert.That(receipt.Rule.SourceRef.AbilityId, Is.EqualTo("operator.passenger.payment"));
            Assert.That(receipt.ProtocolVersion, Is.EqualTo(2)); Assert.That(receipt.ItemIds.Distinct().Count(), Is.EqualTo(count));
            Assert.That(ItemTransferEffectExecutor.Number(receipt.TokenArguments, "requiredCount"), Is.EqualTo(count));
            Assert.That(receipt.TokenTransfer.Request.ProtocolVersion, Is.EqualTo(3));
            Assert.That(receipt.TokenTransfer.Request.Transfers.SelectMany(t => t.ItemIds), Is.EquivalentTo(receipt.ItemIds));
            Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, receipt.TokenArguments, receipt.TokenTransfer, out string reason), Is.True, reason);
            Assert.That(payment.FinalReceipt.CostReceiptPolicyVersion, Is.EqualTo(ResourcePaymentSourceService.TokenCostContractVersion));
        }

        private static (GameState State, EffectRegistry Registry, string OwnerId, string SourceZoneId, string CandidateId) OpenActualPassengerAllocation(bool resourceRows = false)
        {
            var fixture = ActiveActualPassengerFixture(); var state = fixture.State; var executor = fixture.Executor;
            ResourcePaymentChoiceEffectExecutor.Register(fixture.Registry);
            string owner = executor.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1, new[] { new ResourceSet { Originium = 2 } }, "fixture.passenger.payment"));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            string candidate = ItemTransferEffectExecutor.Text(ItemTransferEffectExecutor.Value(request.PromptParameters, "paymentOptions").Items.Single(), "candidateId");
            var session = new GameSession(state); session.RegisterHandler(new PreparePaymentDraftCommandHandler(fixture.Registry));
            Assert.That(session.Submit(new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = 1, Parameters = new Dictionary<string, string> {
                ["ownerEffectId"] = owner, ["interactionId"] = request.GetStableInteractionId(), ["candidateId"] = candidate,
                ["expectedRevision"] = state.EffectRuntime.StateRevision.ToString() } }).Succeeded, Is.True);
            Assert.That(ActualPassengerSource(state, fixture.Registry, owner).AvailableItemCount, Is.EqualTo(3));
            if (resourceRows)
            {
                // 仅资源行投影的隔离接缝：通过内核CreateChild调用仍有效的api1，消费真实付款/纯规则来源。
                // 不恢复被取消的角色旧付款页，也不计正常角色插入或界面流程验收。
                executor.CreateChild(owner, ResourcePaymentSubstitutionEffectExecutor.Create(1, owner, fixture.SourceZoneId, 2));
                Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True, state.EffectRuntime.LastFaultMessage);
                Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId), Is.EqualTo(1));
            }
            else EditActualPassenger(state, fixture.Registry, owner, "begin_source_edit");
            return (state, fixture.Registry, owner, fixture.SourceZoneId, candidate);
        }

        private static GameCommand PaymentDraftCommand(GameState state, InteractionRequest request, string candidate)
            => new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = request.AnsweringPlayerId,
                Parameters = new Dictionary<string, string> { ["ownerEffectId"] = request.OwnerEffectId,
                    ["interactionId"] = request.GetStableInteractionId(), ["candidateId"] = candidate,
                    ["expectedRevision"] = state.EffectRuntime.StateRevision.ToString() } };

        private static void AssertOriginalV2Payment(GameState state, string ownerId, string interactionId)
        {
            var payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            Assert.That(payment, Is.Not.Null); Assert.That(payment.PaymentProtocolVersion, Is.EqualTo(2));
            Assert.That(payment.PaymentId, Is.EqualTo(ownerId)); Assert.That(payment.OwnerEffectId, Is.EqualTo(ownerId));
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.OwnerEffectId == ownerId);
            Assert.That(request.GetStableInteractionId(), Is.EqualTo(interactionId));
            Assert.That(ItemTransferEffectExecutor.Text(request.PromptParameters, "paymentId"), Is.EqualTo(payment.PaymentId));
        }

        private static void AssertPassengerPaymentDoesNotRepeatAfterRecovery(GameState state, EffectRegistry registry,
            string ownerId, string interactionId, string answer)
        {
            var payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).Payment;
            string receiptId = payment.FinalReceipt.ReceiptId, sourceReceiptId = payment.SourceReceipts.Single().ReceiptId;
            var positions = payment.SourceReceipts.Single().ItemIds.ToDictionary(id => id, id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId);
            string resources = Newtonsoft.Json.JsonConvert.SerializeObject(state.FindPlayer(1).Resources);
            int score = state.FindPlayer(1).Score;
            var recovered = new GameSession(GameStateCloneService.DeepClone(state));
            var result = recovered.RecoverHost(new GameSession(state).CreateHostArchive(), registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            var executor = new EffectTreeExecutor(recovered.State, registry);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(executor.GetNode(ownerId).Payment.IsCommitted, Is.True);
            Assert.That(executor.TrySubmitInteraction(interactionId, 1, recovered.State.EffectRuntime.StateRevision,
                NormalizedValue.CreateString(answer), out _), Is.False, "已付请求恢复后不能再次提交");
            Assert.That(executor.GetNode(ownerId).Payment.FinalReceipt.ReceiptId, Is.EqualTo(receiptId));
            Assert.That(executor.GetNode(ownerId).Payment.SourceReceipts.Single().ReceiptId, Is.EqualTo(sourceReceiptId));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(recovered.State.FindPlayer(1).Resources), Is.EqualTo(resources));
            Assert.That(recovered.State.FindPlayer(1).Score, Is.EqualTo(score));
            foreach (var position in positions) Assert.That(recovered.State.ItemZones.Items.Single(i => i.ItemId == position.Key).ZoneId, Is.EqualTo(position.Value));
            AssertActualPassengerReceipt(recovered.State, ownerId, 2);
        }

        [Test]
        public void ActualPassengerDraftReselectionPreservesSourceIdentityAndRecoversCost()
        {
            var fixture = ActiveActualPassengerFixture(); var state = fixture.State; var executor = fixture.Executor;
            ResourcePaymentChoiceEffectExecutor.Register(fixture.Registry);
            string owner = executor.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1,
                new[] { new ResourceSet { Originium = 1 }, new ResourceSet { Originium = 2 } }, "fixture.payment.reselection"));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var options = ItemTransferEffectExecutor.Value(request.PromptParameters, "paymentOptions").Items;
            string first = ItemTransferEffectExecutor.Text(options.Single(o => ItemTransferEffectExecutor.Number(ItemTransferEffectExecutor.Value(o, "cost"), "originium") == 1), "candidateId");
            string second = ItemTransferEffectExecutor.Text(options.Single(o => ItemTransferEffectExecutor.Number(ItemTransferEffectExecutor.Value(o, "cost"), "originium") == 2), "candidateId");
            var session = new GameSession(state); session.RegisterHandler(new PreparePaymentDraftCommandHandler(fixture.Registry));
            string PrepareCurrentPayment(string candidate, bool reissued)
            {
                string oldId = request.GetStableInteractionId();
                Assert.That(session.Submit(PaymentDraftCommand(state, request, candidate)).Succeeded, Is.True);
                // 付款前事件完成后 Host 会重发同一 owner 的页面；旧请求必须失效，费用身份不能更换。
                Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == oldId).Status, Is.EqualTo(reissued ? "invalidated" : "open"));
                request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.OwnerEffectId == owner);
                if (reissued)
                {
                    Assert.That(request.GetStableInteractionId(), Is.Not.EqualTo(oldId));
                    Assert.That(executor.TrySubmitInteraction(oldId, 1, state.EffectRuntime.StateRevision,
                        NormalizedValue.CreateString(candidate), out _), Is.False, "重发后旧付款请求不可提交");
                }
                else Assert.That(request.GetStableInteractionId(), Is.EqualTo(oldId), "v2重报价保留已完成的before历史和当前请求");
                return request.GetStableInteractionId();
            }
            string interactionId = PrepareCurrentPayment(first, true);
            AssertOriginalV2Payment(state, owner, interactionId);
            var firstSource = ActualPassengerSource(state, fixture.Registry, owner);
            EditActualPassenger(state, fixture.Registry, owner, "save_source", new ResourceSet { Originium = 1 });
            int generation = executor.GetNode(owner).Payment.PaymentGeneration;
            interactionId = PrepareCurrentPayment(second, false);
            AssertOriginalV2Payment(state, owner, interactionId);
            Assert.That(executor.GetNode(owner).Payment.PaymentCost.Originium, Is.EqualTo(2));
            var secondSource = ActualPassengerSource(state, fixture.Registry, owner);
            Assert.That(secondSource.Rule.SourceRef.SourceKey, Is.EqualTo(firstSource.Rule.SourceRef.SourceKey));
            Assert.That(ResourcePaymentSourceService.Fingerprint(secondSource.Rule), Is.EqualTo(ResourcePaymentSourceService.Fingerprint(firstSource.Rule)));
            Assert.That(executor.GetNode(owner).Payment.PaymentGeneration, Is.GreaterThan(generation));
            Assert.That(secondSource.DraftStatus, Is.EqualTo("reselect_required"));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            var recovered = new GameSession(GameStateCloneService.DeepClone(state));
            Assert.That(recovered.RecoverHost(session.CreateHostArchive(), fixture.Registry).Succeeded, Is.True);
            var restoredExecutor = new EffectTreeExecutor(recovered.State, fixture.Registry);
            AssertOriginalV2Payment(recovered.State, owner, interactionId);
            Assert.That(restoredExecutor.GetNode(owner).Payment.PaymentCost.Originium, Is.EqualTo(2));
            EditActualPassenger(recovered.State, fixture.Registry, owner, "save_source", new ResourceSet { Originium = 2 });
            Assert.That(restoredExecutor.GetNode(owner).Payment.IsCommitted, Is.False);
            Assert.That(restoredExecutor.GetNode(owner).Payment.SourceDrafts.Single().SelectedItemIds.Count, Is.EqualTo(2));
            AssertOriginalV2Payment(recovered.State, owner, interactionId);
            AnswerActual(recovered.State, restoredExecutor, second);
            Assert.That(restoredExecutor.GetNode(owner).Payment.IsCommitted, Is.True);
            Assert.That(recovered.State.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            AssertActualPassengerReceipt(recovered.State, owner, 2);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(recovered.State, fixture.Registry, out string reason), Is.True, reason);
            AssertPassengerPaymentDoesNotRepeatAfterRecovery(recovered.State, fixture.Registry, owner, interactionId, second);
        }

        [Test]
        public void ActualFreshMixedConditionPaymentUsesOriginalHostAndOneSourceConfirmation()
        {
            var fixture = ActiveActualPassengerFixture(); var state = fixture.State; var executor = fixture.Executor;
            executor.CreateRoot(ResourceEffectSpecFactory.Gain(1, ResourceType.Iron, 1));
            executor.CreateRoot(ResourceEffectSpecFactory.Gain(1, ResourceType.GoldVoucher, 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var cost = ItemTransferEffectExecutor.Object("Originium", NormalizedValue.CreateInteger(2),
                "Iron", NormalizedValue.CreateInteger(1), "GoldVoucher", NormalizedValue.CreateInteger(1));
            var payment = EffectSpec.Create(ResourceEffectTypeIds.Pay,
                ItemTransferEffectExecutor.Object("player", NormalizedValue.CreateString("1"), "cost", cost), 1);
            var condition = EffectSpec.Condition(new[] { payment }, new[] { ResourceEffectSpecFactory.GainScore(1, 5) });
            condition.PlayerId = 1;
            string root = executor.CreateRoot(condition);
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
            Assert.That(request.AnswerSchema, Is.EqualTo("candidate_id"));
            Assert.That(request.CandidateIds.Single(), Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationCandidateId));
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:resource.payment.confirmation.prompt"));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(0));
            string owner = request.OwnerEffectId, interactionId = request.GetStableInteractionId();
            AssertOriginalV2Payment(state, owner, interactionId);
            var recovered = new GameSession(GameStateCloneService.DeepClone(state));
            var recovery = recovered.RecoverHost(new GameSession(state).CreateHostArchive(), fixture.Registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            state = recovered.State; executor = new EffectTreeExecutor(state, fixture.Registry);
            AssertOriginalV2Payment(state, owner, interactionId);
            EditActualPassenger(state, fixture.Registry, owner, "save_source", new ResourceSet { Originium = 2 });
            Assert.That(executor.GetNode(owner).Payment.IsCommitted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.Zero);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            AssertOriginalV2Payment(state, owner, interactionId);
            AnswerActual(state, executor, ResourceEffectExecutor.PaymentConfirmationCandidateId);
            Assert.That(executor.GetNode(owner).Payment.PaymentId, Is.EqualTo(owner));
            Assert.That(executor.GetNode(owner).Payment.IsCommitted, Is.True);
            Assert.That(executor.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(5));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
            AssertActualPassengerReceipt(state, owner, 2);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(state, fixture.Registry, out string reason), Is.True, reason);
            AssertPassengerPaymentDoesNotRepeatAfterRecovery(state, fixture.Registry, owner, interactionId, ResourceEffectExecutor.PaymentConfirmationCandidateId);
        }

        [Test]
        public void ActualFreshPaymentWithoutSourceClickPaysOnlyFullStock()
        {
            var fixture = ActiveActualPassengerFixture(); var state = fixture.State; var executor = fixture.Executor;
            string owner = executor.CreateRoot(ResourceEffectSpecFactory.Pay(1, ResourceType.Originium, 2));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            Assert.That(ActualPassengerSource(state, fixture.Registry, owner).AvailableItemCount, Is.EqualTo(3));
            Assert.That(executor.GetNode(owner).Payment.SourceDrafts, Is.Empty);
            Assert.That(executor.GetNode(owner).ChildEffectIds, Is.Empty);
            AnswerActual(state, executor, ResourceEffectExecutor.PaymentConfirmationCandidateId);
            Assert.That(executor.GetNode(owner).Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            Assert.That(executor.GetNode(owner).Payment.SourceReceipts, Is.Empty);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }

        [Test]
        public void ActualFreshPaymentCancellationRecoversItsOriginalConfirmation()
        {
            var fixture = ActiveActualPassengerFixture(); var state = fixture.State; var executor = fixture.Executor;
            string owner = executor.CreateRoot(ResourceEffectSpecFactory.Pay(1, ResourceType.Originium, 2));
            Assert.That(executor.RunUntilQuiescent().WaitingForInput, Is.True);
            string originalId = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open").GetStableInteractionId();
            EditActualPassenger(state, fixture.Registry, owner, "begin_source_edit");
            EditActualPassenger(state, fixture.Registry, owner, "cancel_source_edit");
            Assert.That(state.EffectRuntime.LastFaultCode, Is.Empty);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.OwnerEffectId, Is.EqualTo(owner));
            Assert.That(request.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
            Assert.That(request.GetStableInteractionId(), Is.EqualTo(originalId));
            Assert.That(executor.GetNode(owner).Payment.SourceEdits.Single().Status, Is.EqualTo("cancelled"));
            Assert.That(executor.GetNode(owner).Payment.SourceDrafts, Is.Empty);
            var recovered = new GameSession(GameStateCloneService.DeepClone(state));
            var recovery = recovered.RecoverHost(new GameSession(state).CreateHostArchive(), fixture.Registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            state = recovered.State; executor = new EffectTreeExecutor(state, fixture.Registry);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            AnswerActual(state, executor, ResourceEffectExecutor.PaymentConfirmationCandidateId);
            Assert.That(executor.GetNode(owner).Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
        }

        [Test]
        public void ActualPassengerAllocationRecoversAndCommitsInItsFinalAnswer()
        {
            var fixture = OpenActualPassengerAllocation();
            var session = new GameSession(GameStateCloneService.DeepClone(fixture.State));
            var recovered = session.RecoverHost(new GameSession(fixture.State).CreateHostArchive(), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var state = session.State; var executor = new EffectTreeExecutor(state, fixture.Registry);
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.SourceEdits.Single().Status, Is.EqualTo("open"));
            EditActualPassenger(state, fixture.Registry, fixture.OwnerId, "save_source", new ResourceSet { Originium = 2 });
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.IsCommitted, Is.False);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            AnswerActual(state, executor, fixture.CandidateId);
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
            AssertActualPassengerReceipt(state, fixture.OwnerId, 2);
            Assert.That(SettlementHostProtocol.TryValidateRecoveryOrigins(state, fixture.Registry, out var reason), Is.True, reason);
        }

        [Test]
        public void ActualPaymentQuantityRowsSurviveViewAndUiReprojectionDetached()
        {
            var fixture = OpenActualPassengerAllocation(resourceRows: true);
            var original = fixture.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId);
            Assert.That(original.InteractionTypeId, Is.EqualTo(ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId));
            var view = GameStateViewProjector.ProjectForPlayer(fixture.State, 1);
            var first = view.Interactions.Single(r => r.InteractionId == original.GetStableInteractionId());
            Assert.That(first.Presentation.QuantityRows.Count, Is.EqualTo(3));
            var visible = YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null, view, 1).Single(r => r.GetStableInteractionId() == original.GetStableInteractionId());
            var projected = InteractionRequestProjector.ProjectForPlayer(visible, 1);
            Assert.That(projected.Presentation.QuantityRows.Count, Is.EqualTo(3), "真实付款经UI再次投影后应保留原3个授权数量行");
            Assert.That(projected.Presentation.QuantityRows.Select(r => r.RowId), Is.EqualTo(original.Presentation.QuantityRows.Select(r => r.RowId)));
            Assert.That(projected.Presentation.QuantityRows.Select(r => r.Label.Key), Is.EqualTo(original.Presentation.QuantityRows.Select(r => r.Label.Key)));
            projected.Presentation.QuantityRows[0].Label.Key = "test:changed";
            projected.Presentation.QuantityRows.RemoveAt(1);
            ItemTransferEffectExecutor.Value(projected.PromptParameters, "rowIds").Items.Clear();
            Assert.That(original.Presentation.QuantityRows.Count, Is.EqualTo(3));
            Assert.That(first.Presentation.QuantityRows.Count, Is.EqualTo(3));
            Assert.That(visible.Presentation.QuantityRows.Count, Is.EqualTo(3));
            Assert.That(visible.Presentation.QuantityRows[0].Label.Key, Is.EqualTo(original.Presentation.QuantityRows[0].Label.Key));
            Assert.That(ItemTransferEffectExecutor.Value(visible.PromptParameters, "rowIds").Items.Count, Is.EqualTo(3));
            var direct = YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(fixture.State, null, 1).Single(r => r.GetStableInteractionId() == original.GetStableInteractionId());
            Assert.That(direct.Presentation.QuantityRows.Count, Is.EqualTo(3));
        }

        [Test]
        public void ActualPaymentReprojectionRejectsUnlistedRowsAndHiddenParameters()
        {
            var fixture = OpenActualPassengerAllocation(resourceRows: true);
            var view = GameStateViewProjector.ProjectForPlayer(fixture.State, 1);
            var request = YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null, view, 1).Single(r => r.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId);
            var originalIds = request.Presentation.QuantityRows.Select(r => r.RowId).ToArray();
            request.CandidateIds.Add("candidate.only");
            request.Presentation.QuantityRows.Add(new GameplayQuantityRowPresentation { RowId = "candidate.only", Label = new GameplayTextRef("test:unlisted") });
            request.Presentation.QuantityRows[0].Description = EntityText();
            var rows = ItemTransferEffectExecutor.Value(request.PromptParameters, "rowIds");
            rows.Items[0] = NormalizedValue.CreateStableReference("row", originalIds[0]);
            rows.Items.Add(NormalizedValue.CreateInteger(99));
            rows.Items.Add(NormalizedValue.CreateBoolean(true));
            rows.Items.Add(NormalizedValue.CreateString(" "));
            var own = InteractionRequestProjector.ProjectForPlayer(request, 1);
            Assert.That(own.Presentation.QuantityRows.Select(r => r.RowId), Is.EqualTo(originalIds));
            Assert.That(own.Presentation.QuantityRows[0].Description, Is.Null);
            Assert.That(request.Presentation.QuantityRows[0].Description.Arguments[0].Value.ReferenceId, Is.EqualTo("secret.item"));
            var hidden = InteractionRequestProjector.ProjectForPlayer(request, 2);
            Assert.That(hidden.VisibleToViewer, Is.False);
            Assert.That(hidden.Presentation, Is.Null);
            Assert.That(hidden.PromptParameters.Properties, Is.Empty);
            Assert.That(hidden.CandidateIds, Is.Empty);
            Assert.That(hidden.InteractionId, Is.Empty);
            Assert.That(hidden.NormalizedAnswer, Is.Null);
            Assert.That(YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null, view, 2), Is.Empty);
            var otherView = GameStateViewProjector.ProjectForPlayer(fixture.State, 2);
            Assert.That(YC.Presentation.Workflows.VisibleInteractionRequestSource.Read(null, otherView, 2), Is.Empty);
            var noRows = request.PromptParameters.Clone();
            noRows.Properties.RemoveAll(p => p.Name == "rowIds");
            request.PromptParameters = noRows;
            Assert.That(InteractionRequestProjector.ProjectForPlayer(request, 1).Presentation.QuantityRows, Is.Empty);
        }

        [Test]
        public void ActualPassengerCancellationReopensOriginalPaymentAndKeepsStocks()
        {
            var fixture = OpenActualPassengerAllocation(); var state = fixture.State; var executor = new EffectTreeExecutor(state, fixture.Registry);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            int revision = state.EffectRuntime.StateRevision;
            Assert.That(executor.TrySubmitInteraction(request.GetStableInteractionId(), 1, revision, NormalizedValue.CreateBoolean(true), out _), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            EditActualPassenger(state, fixture.Registry, fixture.OwnerId, "cancel_source_edit");
            Assert.That(state.EffectRuntime.LastFaultCode, Is.Empty, state.EffectRuntime.LastFaultMessage);
            var resumed = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(resumed.OwnerEffectId, Is.EqualTo(fixture.OwnerId));
            Assert.That(resumed.InteractionTypeId, Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, state.ItemZones.Zones.Single(z => z.ZoneId == fixture.SourceZoneId), 1).Count, Is.EqualTo(3));
            Assert.That(fixture.Registry.GetSettlementOptions(state, 1), Is.Empty);
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.IsCommitted, Is.False);
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.SourceEdits.Single().Status, Is.EqualTo("cancelled"));
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.SourceDrafts, Is.Empty);
            Assert.That(ItemTransferEffectExecutor.Flag(ItemTransferEffectExecutor.Value(resumed.PromptParameters, "paymentDraft"), "canChange"), Is.True);
            var draftSession = new GameSession(state); draftSession.RegisterHandler(new PreparePaymentDraftCommandHandler(fixture.Registry));
            Assert.That(draftSession.Submit(PaymentDraftCommand(state, resumed, fixture.CandidateId)).Succeeded, Is.True,
                "取消来源编辑未消费付款，仍允许编辑原报价");
            var recovered = new GameSession(GameStateCloneService.DeepClone(state)).RecoverHost(new GameSession(state).CreateHostArchive(), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            AnswerActual(state, executor, fixture.CandidateId);
            Assert.That(executor.GetNode(fixture.OwnerId).Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
        }

        private static GameCommand EndCommand(GameState state, int revisionOffset = 0, bool includeCurrent = false, int playerId = 1)
        {
            var view = SettlementHostProtocol.GetEndActionView(state, 1);
            return new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = playerId, Parameters = new Dictionary<string, string> {
                ["windowId"] = view.WindowId, ["expectedRevision"] = (view.StateRevision + revisionOffset).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["currentNodeEffectId"] = includeCurrent ? view.CurrentNodeEffectId : "", ["interactionId"] = includeCurrent ? view.InteractionId : "" } };
        }
        private static GameSession Session(GameState state, EffectRegistry registry)
        {
            var session = new GameSession(state);
            session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(), null, registry));
            return session;
        }

        [Test]
        public void EventAttachedSegmentsWaitBeforeAnyBodyExecutes()
        {
            var fixture = CleanupFixture(true, false);
            var window = SettlementHostProtocol.FindActiveWindow(fixture.State);
            Assert.That(window.Entries.Count, Is.EqualTo(2));
            Assert.That(window.Entries.All(e => fixture.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == e.TargetEffectId).Status == EffectNodeStatus.Created), Is.True);
            Assert.That(fixture.State.EffectRuntime.DispatchReceipts.Where(r => r.EventId == window.Context.TriggerId).Count(r => r.Succeeded && r.AttachedEffectIds.Count == 1), Is.EqualTo(2));
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(0));
            var session = Session(fixture.State, fixture.Registry); int revision = fixture.State.EffectRuntime.StateRevision;
            Assert.That(session.Submit(EndCommand(fixture.State, 1)).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(session.Submit(EndCommand(fixture.State, playerId: 2)).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(session.Submit(EndCommand(fixture.State)).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.SettlementRequiredPending));
            Assert.That(fixture.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
        }

        [Test]
        public void IdleEndActionTerminatesExistingOptionalTargetsInHostCommand()
        {
            var fixture = CleanupFixture(false, false);
            var window = SettlementHostProtocol.FindActiveWindow(fixture.State);
            var targetIds = window.Entries.Select(e => e.TargetEffectId).ToArray();
            var session = Session(fixture.State, fixture.Registry);
            var command = EndCommand(fixture.State);
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True);
            Assert.That(targetIds.All(id => fixture.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == id).Status == EffectNodeStatus.Failed), Is.True);
            Assert.That(window.IsClosed, Is.True);
            Assert.That(window.Entries.All(e => e.SettlementState == "declined"), Is.True);
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(0));
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.PlayerAdvanced), Is.False);
            Assert.That(session.Submit(command).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.SettlementWindowMismatch));
        }

        [Test]
        public void CurrentNodeEndKeepsCommittedScoreAndOtherRequiredEntry()
        {
            var fixture = CleanupFixture(true, true);
            var window = SettlementHostProtocol.FindActiveWindow(fixture.State);
            Assert.That(fixture.Executor.TryInvokeCharacterSettlementOption(1, window.Entries[0].OptionId, "", fixture.State.EffectRuntime.StateRevision, out _, out var reason), Is.True, reason);
            Assert.That(fixture.Executor.RunUntilQuiescent().Faulted, Is.False, fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2));
            var session = Session(fixture.State, fixture.Registry);
            Assert.That(session.Submit(EndCommand(fixture.State)).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(SettlementHostProtocol.GetEndActionView(fixture.State, 1).CanStopCurrentNode, Is.True);
            var result = session.Submit(EndCommand(fixture.State, includeCurrent: true));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2));
            Assert.That(window.Entries[0].SettlementState, Is.EqualTo("resolved"));
            Assert.That(window.Entries[1].SettlementState, Is.EqualTo("available"));
            Assert.That(fixture.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == window.Entries[1].TargetEffectId).Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(session.Submit(EndCommand(fixture.State)).Validation.ErrorCode, Is.EqualTo(CommandErrorCode.SettlementRequiredPending));
        }

        [Test]
        public void OptionalSegmentDoesNotGrantCurrentNodeStopPermission()
        {
            var fixture = CleanupFixture(false, true, false);
            var window = SettlementHostProtocol.FindActiveWindow(fixture.State);
            Assert.That(fixture.Executor.TryInvokeCharacterSettlementOption(1, window.Entries[0].OptionId, "", fixture.State.EffectRuntime.StateRevision, out _, out var reason), Is.True, reason);
            fixture.Executor.RunUntilQuiescent();
            Assert.That(SettlementHostProtocol.GetEndActionView(fixture.State, 1).CanStopCurrentNode, Is.False);
            var result = Session(fixture.State, fixture.Registry).Submit(EndCommand(fixture.State, includeCurrent: true));
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.SettlementCurrentNodeStopForbidden));
            Assert.That(window.Entries[0].SettlementState, Is.EqualTo("executing"));
        }
    }
}
