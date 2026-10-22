using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Presentation;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class LegacyCharacterGameplayTextUiTests
    {
        [Test]
        public void PendingPresentationRequiresMatchingVisibleOwnerAndSnapshot()
        {
            var state = State(CharacterPendingChoiceTypes.TinManFirstPurchase);
            var before = JsonConvert.SerializeObject(state);
            var presenter = new CharacterCardPanelPresenter();
            var query = presenter.QueryPendingOptions(state, 1, null);
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            var presentation = presenter.GetPendingPresentation(state, 1, query, visible);
            Assert.That(presentation, Is.Not.Null);
            Assert.That(presentation, Is.Not.SameAs(query.Presentation));
            presentation.Prompt.Arguments.Find(argument => argument.Name == "cost").Value = NormalizedValue.CreateInteger(99);
            Assert.That(query.Presentation.Prompt.Arguments.Find(argument => argument.Name == "cost").Value.IntegerValue,
                Is.EqualTo(CharacterCardService.TinManFirstPureOriginiumCost));

            foreach (var invalid in new[] { GameStateViewProjector.ProjectForPlayer(state, 2),
                GameStateViewProjector.ProjectForHost(state), GameStateViewProjector.ProjectForSpectator(state) })
                Assert.That(presenter.GetPendingPresentation(state, 1, query, invalid), Is.Null);
            var wrongGame = GameStateCloneService.DeepClone(visible); wrongGame.GameId = "other-match";
            var wrongContent = GameStateCloneService.DeepClone(visible); wrongContent.ContentPackHash = "other-content";
            var oldRevision = GameStateCloneService.DeepClone(visible); oldRevision.StateRevision--;
            var wrongCandidates = GameStateCloneService.DeepClone(visible);
            wrongCandidates.Interactions.Single().CandidateIds.Reverse();
            var duplicate = GameStateCloneService.DeepClone(visible);
            duplicate.Interactions.Add(GameStateCloneService.DeepClone(duplicate.Interactions.Single()));
            foreach (var invalid in new[] { wrongGame, wrongContent, oldRevision, wrongCandidates, duplicate })
                Assert.That(presenter.GetPendingPresentation(state, 1, query, invalid), Is.Null);
            Assert.That(presenter.GetPendingPresentation(state, 2, query, null), Is.Null);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void LegacyDialogPreservesResolvedPromptAndCandidateDocuments()
        {
            var core = Environment.GetEnvironmentVariable("YC_LEGACY_TEXT_CORE") ??
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "Assets", "StreamingAssets", "Content", "core");
            var pack = ExternalContentPack.Load(core);
            var type = Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp", true);
            foreach (var pendingType in new[] { CharacterPendingChoiceTypes.TinManDiscard,
                CharacterPendingChoiceTypes.TinManFirstPurchase, CharacterPendingChoiceTypes.TinManSecondPurchase })
            {
                var state = State(pendingType);
                var presenter = new CharacterCardPanelPresenter();
                var query = presenter.QueryPendingOptions(state, 1, null);
                var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
                var presentation = presenter.GetPendingPresentation(state, 1, query, visible);
                var context = new GameplayTextContext { Catalog = pack.Presentation,
                    ContentHash = pack.Presentation.ContentHash, Locale = pack.Presentation.Locale,
                    VisibilityScopeId = visible.GameId + ":" + visible.ViewerPlayerId,
                    ViewerPlayerId = visible.ViewerPlayerId, StateRevision = visible.StateRevision,
                    DefinitionNames = visible.Players.Single(player => player.PlayerId == 1).DiscardCardIds
                        .ToDictionary(id => id, id => "已可见来源[b]007[icon:core:resource.gold_voucher]") };
                var resolver = new GameplayTextResolver(pack.Presentation);
                var dialog = FormatterServices.GetUninitializedObject(type);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                type.GetField("resolveText", flags).SetValue(dialog,
                    (Func<GameplayTextRef, GameplayTextDocument>)(text => resolver.Resolve(text, context)));
                var prompt = (GameplayTextDocument)type.GetMethod("ResolveLegacyOptionsPrompt", flags)
                    .Invoke(dialog, new object[] { presentation });
                Assert.That(prompt, Is.SameAs(resolver.Resolve(presentation.Prompt, context)));
                Assert.That(prompt.Identity, Is.Not.Empty);
                Assert.That(type.GetMethod("ResolveLegacyOptionsPrompt", flags).Invoke(dialog, new object[] { presentation }),
                    Is.SameAs(prompt));
                foreach (var option in query.Get(CharacterEffectParameterKeys.Choice))
                {
                    var label = presentation.Options.Single(value => value.CandidateId == option.Id).Label;
                    var document = (GameplayTextDocument)type.GetMethod("ResolveLegacyOptionLabel", flags)
                        .Invoke(dialog, new object[] { presentation, option.Id });
                    Assert.That(document, Is.SameAs(resolver.Resolve(label, context)));
                    Assert.That(document.Runs, Is.Not.Empty);
                    if (option.Id == CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium ||
                        option.Id == CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium)
                    {
                        var cost = pendingType == CharacterPendingChoiceTypes.TinManFirstPurchase
                            ? CharacterCardService.TinManFirstPureOriginiumCost : CharacterCardService.TinManSecondPureOriginiumCost;
                        Assert.That(document.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain((long)cost));
                        Assert.That(document.Runs.OfType<IconRun>().Any(), Is.True);
                    }
                }
                Assert.That(type.GetMethod("ResolveLegacyOptionLabel", flags)
                    .Invoke(dialog, new object[] { presentation, "continue" }), Is.Null);
                if (pendingType == CharacterPendingChoiceTypes.TinManDiscard)
                {
                    Assert.That(prompt.ToPlainText(), Does.Contain(context.DefinitionNames.Values.Single()));
                    Assert.That(prompt.Runs.OfType<IconRun>(), Is.Empty);
                }
                if (pendingType == CharacterPendingChoiceTypes.TinManSecondPurchase)
                {
                    Assert.That(prompt.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(12L));
                    Assert.That(prompt.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(15L));
                    Assert.That(query.GoldVoucherCost, Is.EqualTo(12));
                }
            }
        }

        [Test]
        public void LegacyPurchaseParametersKeepChoiceAndSourceCommand()
        {
            var type = Type.GetType("YC.Presentation.CharacterCardEffectInteractionUiCoordinator, Assembly-CSharp", true);
            var parameters = type.GetMethod("CreateTinManPendingChoiceParameters", BindingFlags.Static | BindingFlags.NonPublic);
            var presenter = new CharacterCardPanelPresenter();
            foreach (var pendingType in new[] { CharacterPendingChoiceTypes.TinManFirstPurchase,
                CharacterPendingChoiceTypes.TinManSecondPurchase })
            {
                var state = State(pendingType);
                foreach (var choice in state.PendingCharacterEffect.OptionIds)
                {
                    var values = (IReadOnlyDictionary<string, string>)parameters.Invoke(null,
                        new object[] { state.PendingCharacterEffect, choice });
                    var command = presenter.CreateResolvePendingCommand(1, values);
                    Assert.That(command.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
                    Assert.That(command.PlayerId, Is.EqualTo(1));
                    Assert.That(command.Parameters.Count, Is.EqualTo(2));
                    Assert.That(command.Parameters[CharacterEffectParameterKeys.Choice], Is.EqualTo(choice));
                    Assert.That(command.Parameters[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                        Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
                    Assert.That(command.Parameters[CharacterEffectParameterKeys.Choice], Is.Not.EqualTo("continue"));
                    Assert.That(command.Parameters[CharacterEffectParameterKeys.Choice], Is.Not.EqualTo("decline"));
                }
            }
        }

        [Test]
        public void LegacyHudDocumentRequiresRealOwnerAndClearsStaleScope()
        {
            var type = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var bind = type.GetMethod("BindLegacyInteractionMessageDocument", flags);
            var read = type.GetMethod("GetLegacyInteractionMessageDocument", flags);
            var document = new GameplayTextDocument(new GameplayTextRun[]
                { new NumberRun(15), new IconRun("core:resource.gold_voucher", "金券") }, "legacy-hud");
            foreach (var pendingType in new[] { CharacterPendingChoiceTypes.TinManDiscard,
                CharacterPendingChoiceTypes.TinManFirstPurchase, CharacterPendingChoiceTypes.TinManSecondPurchase })
            {
                var state = State(pendingType);
                var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
                var pendingId = visible.Interactions.Single().InteractionId;
                var frame = FormatterServices.GetUninitializedObject(type);
                type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                bool current = true;
                object[] arguments = { state.GameId, 1, visible.StateRevision, pendingId, document,
                    (Func<bool>)(() => current) };
                Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(true));
                Assert.That(read.Invoke(frame, null), Is.SameAs(document));
                Assert.That(bind.Invoke(frame, new object[] { "other-game", 1, visible.StateRevision,
                    pendingId, null, null }), Is.EqualTo(false));
                Assert.That(bind.Invoke(frame, new object[] { state.GameId, 2, visible.StateRevision,
                    pendingId, document, (Func<bool>)(() => true) }), Is.EqualTo(false));
                Assert.That(read.Invoke(frame, null), Is.SameAs(document));
                type.GetField("interactionMessageOverridesPrompt", flags).SetValue(frame, true);
                type.GetMethod("ClearRequest").Invoke(frame, null);
                Assert.That(read.Invoke(frame, null), Is.SameAs(document), "无正式路由请求时仍保留有效旧Pending提示");
                current = false;
                Assert.That(read.Invoke(frame, null), Is.Null);
                Assert.That(type.GetField("legacyMessagePendingId", flags).GetValue(frame), Is.Null);
                Assert.That(type.GetField("interactionMessageOverridesPrompt", flags).GetValue(frame), Is.EqualTo(false));
                current = true;

                var invalidViews = new List<GameStateView>
                    { GameStateViewProjector.ProjectForPlayer(state, 2), GameStateViewProjector.ProjectForHost(state),
                      GameStateViewProjector.ProjectForSpectator(state) };
                var game = GameStateCloneService.DeepClone(visible); game.GameId = "other-game"; invalidViews.Add(game);
                var revision = GameStateCloneService.DeepClone(visible); revision.StateRevision++; invalidViews.Add(revision);
                var missing = GameStateCloneService.DeepClone(visible); missing.Interactions.Clear(); invalidViews.Add(missing);
                var closed = GameStateCloneService.DeepClone(visible); closed.Interactions.Single().Status = "answered"; invalidViews.Add(closed);
                var duplicate = GameStateCloneService.DeepClone(visible);
                duplicate.Interactions.Add(GameStateCloneService.DeepClone(duplicate.Interactions.Single())); invalidViews.Add(duplicate);
                var kind = GameStateCloneService.DeepClone(visible); kind.Interactions.Single().Kind = "lua.choice"; invalidViews.Add(kind);
                foreach (var invalid in invalidViews)
                {
                    type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                    Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(true));
                    type.GetField("gameplaySnapshot", flags).SetValue(frame, invalid);
                    Assert.That(read.Invoke(frame, null), Is.Null);
                    Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(false));
                }
                type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(true));
                var content = GameStateCloneService.DeepClone(visible); content.ContentPackHash = "changed-content";
                type.GetField("gameplaySnapshot", flags).SetValue(frame, content);
                Assert.That(read.Invoke(frame, null), Is.Null);
                type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(true));
                Assert.That(bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision,
                    pendingId, null, null }), Is.EqualTo(true));
                Assert.That(read.Invoke(frame, null), Is.Null);

                // 与真实投影身份一致的开放正式请求拥有正文优先级。
                Assert.That(bind.Invoke(frame, arguments), Is.EqualTo(true));
                var formal = new InteractionView { InteractionId = "formal-request", SourceNodeId = "formal-node",
                    OwnerEffectId = "formal-effect", Kind = "lua.choice", AnsweringPlayerId = 1,
                    Visibility = "owner", Status = "open", StateRevision = visible.StateRevision,
                    CandidateIds = new List<string> { "store", "decline" }, MinSelections = 1, MaxSelections = 1 };
                visible.Interactions.Add(formal);
                type.GetField("currentProjection", flags).SetValue(frame, new YC.Domain.Interactions.InteractionRequestProjection
                { InteractionId = formal.InteractionId, SourceNodeId = formal.SourceNodeId, OwnerEffectId = formal.OwnerEffectId,
                    InteractionTypeId = formal.Kind, AnsweringPlayerId = 1, Visibility = formal.Visibility,
                    Status = formal.Status, VisibleToViewer = true, StateRevision = formal.StateRevision,
                    CandidateIds = formal.CandidateIds, MinSelections = 1, MaxSelections = 1 });
                Assert.That(read.Invoke(frame, null), Is.Null, "正式请求正文优先，旧Pending文档应清除");
            }
        }

        [Test]
        public void LegacyNamesOnlyConsumeTheCurrentOwnersVisibleCards()
        {
            var type = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var build = type.GetMethod("BuildVisibleGameplayDefinitionNames", BindingFlags.Static | BindingFlags.NonPublic);
            var state = State(CharacterPendingChoiceTypes.TinManDiscard);
            state.FindPlayer(1).HandCardIds.Add("owner-hand");
            state.FindPlayer(1).CoveredCharacterCardId = "owner-covered";
            state.FindPlayer(2).HandCardIds.Add("hidden-hand");
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            visible.ItemZones.Items.Add(new YC.Domain.Items.ItemInstanceState
                { ItemId = "public-item", DefinitionId = "public-definition" });
            var owner = visible.Players.Single(player => player.PlayerId == 1);
            var other = visible.Players.Single(player => player.PlayerId == 2);
            other.PrivateCardIdsVisible = true;
            other.HandCardIds.Add("hidden-hand");
            other.DiscardCardIds.Add("hidden-discard");
            other.CoveredCharacterCardId = "hidden-covered";
            owner.HandCardIds.Add("unknown-card");
            var catalog = new Dictionary<string, string>
                { ["public-definition"] = "公开实例", ["owner-hand"] = "本人手牌", ["hidden-hand"] = "隐藏手牌" };
            var lookups = new List<string>();
            const string literalName = "已可见来源[b]007[icon:core:resource.gold_voucher]";
            Func<string, string> resolve = id =>
            {
                lookups.Add(id);
                return id == "visible-discard" ? literalName : id == "owner-covered" ? "本人盖牌" : null;
            };
            var names = (Dictionary<string, string>)build.Invoke(null, new object[] { visible, catalog, resolve });
            Assert.That(names.Keys, Is.EquivalentTo(new[] { "public-definition", "owner-hand", "visible-discard", "owner-covered" }));
            Assert.That(names["owner-hand"], Is.EqualTo(catalog["owner-hand"]));
            Assert.That(lookups, Is.EquivalentTo(new[] { "visible-discard", "owner-covered", "unknown-card" }));
            Assert.That(names.Keys.Any(id => id.StartsWith("hidden-", StringComparison.Ordinal)), Is.False);
            owner.PrivateCardIdsVisible = false;
            lookups.Clear();
            var denied = (Dictionary<string, string>)build.Invoke(null, new object[] { visible, catalog, resolve });
            Assert.That(denied.Keys, Is.EquivalentTo(new[] { "public-definition" }));
            Assert.That(lookups, Is.Empty);
            foreach (var role in new[] { GameStateViewerRole.Host, GameStateViewerRole.Spectator })
            {
                visible.ViewerRole = role;
                Assert.That((Dictionary<string, string>)build.Invoke(null, new object[] { visible, catalog, resolve }), Is.Empty);
                Assert.That(lookups, Is.Empty);
            }
            var core = Environment.GetEnvironmentVariable("YC_LEGACY_TEXT_CORE") ??
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "Assets", "StreamingAssets", "Content", "core");
            var pack = ExternalContentPack.Load(core);
            var query = new CharacterCardPanelPresenter().QueryPendingOptions(state, 1, null);
            var document = new GameplayTextResolver(pack.Presentation).Resolve(query.Presentation.Prompt,
                new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                    Locale = pack.Presentation.Locale, DefinitionNames = names });
            Assert.That(document.ToPlainText(), Does.Contain(literalName));
            Assert.That(document.Runs.OfType<IconRun>(), Is.Empty, "来源名称作为字面参数，不再次解析标记");
        }

        [Test]
        public void LegacyBottomHintsPreserveDocumentsAndInvalidateChangedPending()
        {
            var core = Environment.GetEnvironmentVariable("YC_LEGACY_TEXT_CORE") ??
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "Assets", "StreamingAssets", "Content", "core");
            var pack = ExternalContentPack.Load(core);
            var resolver = new GameplayTextResolver(pack.Presentation);
            var context = new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                Locale = pack.Presentation.Locale, ViewerPlayerId = 1, VisibilityScopeId = "legacy-bottom" };
            var dialogType = Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp", true);
            var dialog = FormatterServices.GetUninitializedObject(dialogType);
            dialogType.GetField("resolveText", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog,
                (Func<GameplayTextRef, GameplayTextDocument>)(text => resolver.Resolve(text, context)));
            var type = Type.GetType("YC.Presentation.CharacterCardEffectInteractionUiCoordinator, Assembly-CSharp", true);
            var state = State(CharacterPendingChoiceTypes.TinManFirstPurchase);
            int viewer = 1, stringFallbackCalls = 0, submissions = 0;
            var before = JsonConvert.SerializeObject(state);
            var coordinator = Activator.CreateInstance(type, new object[]
            {
                (Func<GameState>)(() => state), (Func<int>)(() => viewer), new CharacterCardPanelPresenter(), dialog,
                (Func<bool>)(() => false), (Action)(() => { }),
                (Action<string, IReadOnlyDictionary<string, string>>)((mode, values) => submissions++),
                (Action<IReadOnlyDictionary<string, string>>)(values => submissions++),
                (Action<string>)(value => stringFallbackCalls++)
            });
            var deliveries = new List<Tuple<string, int, int, string, GameplayTextDocument, Func<bool>>>();
            type.GetProperty("RenderLegacyMessageDocument").SetValue(coordinator,
                (Action<string, int, int, string, GameplayTextDocument, Func<bool>>)((game, player, revision, pending, document, current) =>
                    deliveries.Add(Tuple.Create(game, player, revision, pending, document, current))));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var present = type.GetMethod("PresentLegacyPrompt", flags);
            var create = type.GetMethod("CreatePurchaseHint", BindingFlags.Static | BindingFlags.NonPublic);
            var query = new CharacterCardPanelPresenter().QueryPendingOptions(state, 1, null);
            var references = new[]
            {
                new GameplayTextRef("core:legacy.character.tin_man.discard.hint"),
                new GameplayTextRef("core:legacy.character.tin_man.discard.move_unavailable",
                    new GameplayTextArgument { Name = "gold", Kind = "integer", Value = NormalizedValue.CreateInteger(5) }),
                (GameplayTextRef)create.Invoke(null, new object[] { true, true, query }),
                (GameplayTextRef)create.Invoke(null, new object[] { false, true, query }),
                (GameplayTextRef)create.Invoke(null, new object[] { false, false, query })
            };
            foreach (var reference in references)
            {
                present.Invoke(coordinator, new object[] { reference });
                var delivery = deliveries.Last();
                Assert.That(delivery.Item1, Is.EqualTo(state.GameId));
                Assert.That(delivery.Item2, Is.EqualTo(viewer));
                Assert.That(delivery.Item3, Is.EqualTo(state.EffectRuntime?.StateRevision ?? 0));
                Assert.That(delivery.Item4, Is.EqualTo(GameStateViewProjector.ProjectForPlayer(state, 1).Interactions.Single().InteractionId));
                Assert.That(delivery.Item5, Is.SameAs(resolver.Resolve(reference, context)));
                Assert.That(delivery.Item6(), Is.True);
            }
            Assert.That(deliveries[1].Item5.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(5L));
            Assert.That(deliveries[2].Item5.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(12L));
            Assert.That(deliveries[2].Item5.Runs.OfType<IconRun>().Select(run => run.NumberValue), Does.Contain(1));
            Assert.That(deliveries[3].Item5.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(15L));
            Assert.That(deliveries[3].Item5.Runs.OfType<NumberRun>().Any(run => run.Value == 12L), Is.False);
            Assert.That(deliveries[2].Item5.Runs.OfType<IconRun>().Any(), Is.True);
            Assert.That(deliveries[3].Item5.Runs.OfType<IconRun>().Any(), Is.True);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var original = state;
            var isCurrent = deliveries.Last().Item6;
            foreach (var change in new Action<GameState>[]
            {
                value => value.GameId = "other-game", value => value.ContentPackHash = "other-content",
                value => value.PendingCharacterEffect.SourceCommandId = "other-command",
                value => value.PendingCharacterEffect.CardId = "other-card",
                value => value.PendingCharacterEffect.ChoiceType = CharacterPendingChoiceTypes.TinManSecondPurchase,
                value => value.PendingCharacterEffect.OptionIds.Reverse(),
                value => value.PendingCharacterEffect.RemainingCardIds[0] = "other-source",
                value => value.PendingCharacterEffect.PlayerId = 2, value => value.PendingCharacterEffect = null
            })
            {
                state = GameStateCloneService.DeepClone(original); change(state);
                Assert.That(isCurrent(), Is.False);
            }
            state = original; viewer = 2; Assert.That(isCurrent(), Is.False); viewer = 1;
            Assert.That(isCurrent(), Is.True);
            var last = deliveries.Last();
            type.GetMethod("ClearLegacyPrompt", flags).Invoke(coordinator, null);
            var cleared = deliveries.Last();
            Assert.That(cleared.Item1, Is.EqualTo(last.Item1)); Assert.That(cleared.Item2, Is.EqualTo(last.Item2));
            Assert.That(cleared.Item3, Is.EqualTo(last.Item3)); Assert.That(cleared.Item4, Is.EqualTo(last.Item4));
            Assert.That(cleared.Item5, Is.Null); Assert.That(cleared.Item6, Is.Null);
            var count = deliveries.Count;
            type.GetMethod("ClearLegacyPrompt", flags).Invoke(coordinator, null);
            Assert.That(deliveries.Count, Is.EqualTo(count));
            Assert.That(submissions, Is.Zero); Assert.That(stringFallbackCalls, Is.Zero);
        }

        [Test]
        public void RoleBusinessReferencesResolveThroughTheExistingCatalog()
        {
            var resolve = BusinessTextResolver();
            var state = State(CharacterPendingChoiceTypes.TinManDiscard);
            state.PendingCharacterEffect = null;
            state.FindPlayer(1).DiscardCardIds.Clear();
            state.Phase = GamePhase.CharacterCover;
            var presenter = new CharacterCardPanelPresenter { ResolveGameplayText = resolve };
            var before = JsonConvert.SerializeObject(state);
            var view = presenter.BuildView(state, 1);
            Assert.That(view.CanCover, Is.True);
            Assert.That(view.CanUse, Is.False);
            Assert.That(view.CoveredStatus, Is.EqualTo(resolve(view.CoveredStatusText).ToPlainText()));
            Assert.That(view.InteractionStatus, Is.EqualTo(resolve(view.InteractionStatusText).ToPlainText()));
            Assert.That(presenter.GetEffectUnavailableReasonText(view, false), Is.SameAs(view.InteractionStatusText));
            Assert.That(presenter.GetEffectUnavailableReason(view, false), Is.EqualTo(view.InteractionStatus));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            state.FindPlayer(1).UsedCharacterThisRound = true;
            Assert.That(presenter.BuildView(state, 1).CoveredStatusText.Key, Is.EqualTo("core:legacy.character.status.used"));
            state.FindPlayer(1).UsedCharacterThisRound = false;
            state.FindPlayer(1).CharacterCardLockedThisTurn = true;
            Assert.That(presenter.BuildView(state, 1).CoveredStatusText.Key, Is.EqualTo("core:legacy.character.status.locked"));
            var missing = presenter.BuildView(null, 1);
            Assert.That(missing.InteractionStatus, Is.EqualTo(resolve(missing.InteractionStatusText).ToPlainText()));
            var references = new[]
            {
                "status.used", "status.locked", "status.covered", "status.not_covered", "status.pending_choice",
                "status.cover_select", "status.second_available", "status.second_no_targets", "status.effect_not_integrated",
                "status.can_use", "status.inspect_only", "status.player_missing", "unavailable.effect_not_integrated",
                "unavailable.effect_currently_unavailable", "unavailable.cover_none", "unavailable.cover_selection_stale",
                "unavailable.not_initialized", "unavailable.cannot_activate", "unavailable.effect_not_registered",
                "map.move_source", "map.move_target", "map.no_move", "map.click_influence", "map.choose_legal", "map.next_no_target"
            };
            foreach (var key in references)
                Assert.That(resolve(new GameplayTextRef("core:legacy.character." + key)).Runs, Is.Not.Empty);
            var cleanup = resolve(new GameplayTextRef("core:legacy.character.map.cleanup", new GameplayTextArgument
                { Name = "count", Kind = "integer", Value = NormalizedValue.CreateInteger(1) }));
            Assert.That(cleanup.Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(1L));
        }

        [Test]
        public void CardViewerHintKeepsConfiguredFormatsAndResolvedRuns()
        {
            var type = Type.GetType("YC.Presentation.CardViewer, Assembly-CSharp", true);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var viewer = FormatterServices.GetUninitializedObject(type);
            var plotFormat = "计谋配置（{0}）";
            var strategyFormat = "策略配置：{0}";
            var hint = "检查器配置的原提示[b][icon:core:resource.gold_voucher]";
            type.GetField("plotUnavailableFormat", flags).SetValue(viewer, plotFormat);
            type.GetField("strategyUnavailableFormat", flags).SetValue(viewer, strategyFormat);
            type.GetField("useHint", flags).SetValue(viewer, hint);
            var literal = new TextRun("本人可见名称[b]007[icon:core:resource.gold_voucher]");
            var number = new NumberRun(15);
            var icon = new IconRun("core:resource.score", "分数", 1);
            var bold = new TextRun("既有加粗", new GameplayTextRunStyle("SpecialWord", "Bold"));
            var plot = new GameplayTextDocument(new GameplayTextRun[] { literal, number, icon, bold }, "plot-reason");
            var strategy = BusinessTextResolver()(new GameplayTextRef("core:legacy.character.unavailable.effect_not_integrated"));
            var build = type.GetMethod("BuildUnavailableHintDocument", flags);
            var result = (GameplayTextDocument)build.Invoke(viewer, new object[] { false, false, plot, strategy });
            Assert.That(result.ToPlainText(), Is.EqualTo(string.Format(plotFormat, plot.ToPlainText()) + "  " +
                string.Format(strategyFormat, strategy.ToPlainText())));
            foreach (var run in plot.Runs.Concat(strategy.Runs))
                Assert.That(result.Runs.Any(value => ReferenceEquals(value, run)), Is.True);
            Assert.That(result.Runs.OfType<IconRun>().Count(), Is.EqualTo(1));
            Assert.That(result.Runs.OfType<NumberRun>().Single().Value, Is.EqualTo(15));
            Assert.That(result.Runs.OfType<IconRun>().Single().NumberValue, Is.EqualTo(1));
            var enabled = (GameplayTextDocument)build.Invoke(viewer, new object[] { true, true, null, null });
            Assert.That(enabled.ToPlainText(), Is.EqualTo(hint));
            Assert.That(enabled.Runs.OfType<IconRun>(), Is.Empty);
            Assert.That(enabled.Identity, Is.Not.EqualTo(result.Identity));
            var onlyPlot = (GameplayTextDocument)build.Invoke(viewer, new object[] { false, true, plot, null });
            Assert.That(onlyPlot.ToPlainText(), Is.EqualTo(string.Format(plotFormat, plot.ToPlainText())));
        }

        [Test]
        public void BusinessErrorDocumentRequiresTheRealViewerSnapshot()
        {
            var type = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var state = State(CharacterPendingChoiceTypes.TinManDiscard);
            state.PendingCharacterEffect = null;
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(visible.Interactions, Is.Empty);
            var frame = FormatterServices.GetUninitializedObject(type);
            type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
            var bind = type.GetMethod("BindScopedInteractionMessageDocument", flags);
            var read = type.GetMethod("GetScopedInteractionMessageDocument", flags);
            var document = BusinessTextResolver()(new GameplayTextRef("core:legacy.character.unavailable.cover_selection_stale"));
            bool current = true;
            Func<bool> check = () => current;
            Assert.That(bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision, document, check }), Is.EqualTo(true));
            Assert.That(read.Invoke(frame, null), Is.SameAs(document));
            foreach (var wrong in new object[][]
            {
                new object[] { "other-game", 1, visible.StateRevision, document, check },
                new object[] { state.GameId, 2, visible.StateRevision, document, check },
                new object[] { state.GameId, 1, visible.StateRevision + 1, document, check },
                new object[] { state.GameId, 1, visible.StateRevision, document, null },
                new object[] { "other-game", 1, visible.StateRevision, null, null }
            }) Assert.That(bind.Invoke(frame, wrong), Is.EqualTo(false));
            Assert.That(read.Invoke(frame, null), Is.SameAs(document));
            foreach (var invalid in new[] { GameStateViewProjector.ProjectForPlayer(state, 2),
                GameStateViewProjector.ProjectForHost(state), GameStateViewProjector.ProjectForSpectator(state) })
            {
                type.GetField("gameplaySnapshot", flags).SetValue(frame, invalid);
                Assert.That(read.Invoke(frame, null), Is.Null);
                Assert.That(bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision, document, check }), Is.EqualTo(false));
                type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                Assert.That(bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision, document, check }), Is.EqualTo(true));
            }
            var wrongContent = GameStateCloneService.DeepClone(visible); wrongContent.ContentPackHash = "other-content";
            type.GetField("gameplaySnapshot", flags).SetValue(frame, wrongContent);
            Assert.That(read.Invoke(frame, null), Is.Null);
            type.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
            bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision, document, check });
            current = false;
            Assert.That(read.Invoke(frame, null), Is.Null);
            Assert.That(type.GetField("scopedInteractionMessageDocument", flags).GetValue(frame), Is.Null);
        }

        [Test]
        public void PendingMapDocumentsSurviveRealRouterConfirmationAndKeepSubmission()
        {
            var resolve = BusinessTextResolver();
            foreach (var pendingType in new[] { CharacterPendingChoiceTypes.LiskarmCleanupRemoval, CharacterPendingChoiceTypes.TinManDiscard })
            {
                var state = State(pendingType);
                state.MapId = StaticMapDefinitions.FourPlayerMapId;
                state.Phase = pendingType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval ? GamePhase.Cleanup : GamePhase.ActionRound1;
                state.FindPlayer(1).Color = PlayerColor.Red;
                state.FindPlayer(1).InfluenceSupply = 30;
                var source = YC.Domain.Influence.InfluenceService.GetRouteSlotId("A1", 0);
                state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = source, RouteId = "A1" });
                if (pendingType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval)
                    state.PendingCharacterEffect.OptionIds = new List<string> { source };
                var before = JsonConvert.SerializeObject(state);
                int submissions = 0, oldStringCalls = 0;
                IReadOnlyDictionary<string, string> submitted = null;
                var coordinator = new YC.Presentation.CharacterMapInteractionCoordinator(() => state, () => 1,
                    new CharacterCardPanelPresenter(), values => { }, () => { }, (mode, values) => Assert.Fail("不得走普通角色效果提交"),
                    values => { submissions++; submitted = values; }, value => oldStringCalls++);
                coordinator.ResolveGameplayText = resolve;
                coordinator.ShowGameplayPrompt = (reference, check) => { Assert.That(resolve(reference), Is.Not.Null); Assert.That(check(), Is.True); };
                Assert.That(pendingType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval ? coordinator.Synchronize() :
                    coordinator.TryBeginTinManPendingMove(), Is.True);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var adapterType = Type.GetType("YC.Presentation.CharacterCardInteraction, Assembly-CSharp", true);
                var adapter = FormatterServices.GetUninitializedObject(adapterType);
                adapterType.GetField("mapCoordinator", flags).SetValue(adapter, coordinator);
                adapterType.GetField("hasPendingCharacterResolution", flags).SetValue(adapter, (Func<bool>)(() => state.PendingCharacterEffect != null));
                adapterType.GetField("hasFacilitySelection", flags).SetValue(adapter, (Func<bool>)(() => false));
                adapterType.GetField("tryCancelFacilitySelection", flags).SetValue(adapter, (Func<bool>)(() => false));
                var router = new InteractionRouter(value => oldStringCalls++);
                router.Register((IInteraction)adapter);
                var original = router.BuildActivePresentation();
                Assert.That(original.Highlights.Any(value => value.TargetId == source), Is.True);
                var prompt = original.PromptDocument;
                Assert.That(prompt, Is.Not.Null);
                var current = coordinator.CapturePendingPromptCurrentness();
                router.OnInfluenceSlotClicked(source, 100);
                Assert.That(submissions, Is.Zero);
                Assert.That(router.HasPendingMapConfirmation, Is.True);
                var confirmed = router.BuildActivePresentation();
                Assert.That(confirmed.PromptDocument, Is.SameAs(prompt));
                Assert.That(coordinator.OwnsPromptDocument(confirmed.PromptDocument), Is.True);
                Assert.That(confirmed.Highlights.Count, Is.EqualTo(original.Highlights.Count));
                Assert.That(confirmed.Highlights.Single(value => value.TargetId == source).State,
                    Is.EqualTo(WorkflowHighlightState.PendingConfirmation));
                var frameType = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
                var frame = FormatterServices.GetUninitializedObject(frameType);
                var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
                frameType.GetField("gameplaySnapshot", flags).SetValue(frame, visible);
                var bind = frameType.GetMethod("BindScopedInteractionMessageDocument", flags);
                var read = frameType.GetMethod("GetScopedInteractionMessageDocument", flags);
                Assert.That(bind.Invoke(frame, new object[] { state.GameId, 1, visible.StateRevision, confirmed.PromptDocument, current }), Is.EqualTo(true));
                Assert.That(read.Invoke(frame, null), Is.SameAs(prompt));
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
                var unchanged = state;
                foreach (var change in new Action<GameState>[]
                {
                    value => value.PendingCharacterEffect.CardId = "other-card",
                    value => value.PendingCharacterEffect.SourceCommandId = "other-command",
                    value => value.PendingCharacterEffect.OptionIds.Add("other-option"),
                    value => value.PendingCharacterEffect.RemainingCardIds[0] = "other-discard",
                    value => value.PendingCharacterEffect.PlayerId = 2
                })
                {
                    state = GameStateCloneService.DeepClone(unchanged); change(state);
                    Assert.That(current(), Is.False);
                }
                state = unchanged;
                Assert.That(current(), Is.True);
                router.OnInfluenceSlotClicked(source, 100);
                Assert.That(submissions, Is.Zero, "同帧重复输入不得提交");
                router.OnInfluenceSlotClicked(source, 101);
                if (pendingType == CharacterPendingChoiceTypes.TinManDiscard)
                {
                    Assert.That(submissions, Is.Zero);
                    Assert.That(current(), Is.False, "进入目标步骤应撤销来源提示的当前性");
                    Assert.That(read.Invoke(frame, null), Is.Null);
                    var targetPresentation = router.BuildActivePresentation();
                    Assert.That(targetPresentation.PromptDocument, Is.Not.SameAs(prompt));
                    var target = targetPresentation.Highlights[0].TargetId;
                    router.OnInfluenceSlotClicked(target, 102);
                    Assert.That(submissions, Is.Zero);
                    Assert.That(router.BuildActivePresentation().PromptDocument, Is.SameAs(targetPresentation.PromptDocument));
                    router.OnInfluenceSlotClicked(target, 103);
                    Assert.That(submitted[CharacterEffectParameterKeys.Choice], Is.EqualTo(CharacterEffectChoiceIds.MoveInfluence));
                    Assert.That(submitted[CharacterEffectParameterKeys.SourceInfluenceSlotId], Is.EqualTo(source));
                    Assert.That(submitted[CharacterEffectParameterKeys.TargetInfluenceSlotId], Is.EqualTo(target));
                }
                else Assert.That(submitted[CharacterEffectParameterKeys.TargetInfluenceSlotId], Is.EqualTo(source));
                Assert.That(submissions, Is.EqualTo(1));
                Assert.That(submitted[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId], Is.EqualTo("original-pending-command"));
                Assert.That(oldStringCalls, Is.Zero);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
                Assert.That(current(), Is.False);
            }
        }

        private static Func<GameplayTextRef, GameplayTextDocument> BusinessTextResolver()
        {
            var core = Environment.GetEnvironmentVariable("YC_LEGACY_TEXT_CORE") ??
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "Assets", "StreamingAssets", "Content", "core");
            var pack = ExternalContentPack.Load(core);
            var resolver = new GameplayTextResolver(pack.Presentation);
            var context = new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                Locale = pack.Presentation.Locale, VisibilityScopeId = "legacy-ui-consumption:1", ViewerPlayerId = 1, StateRevision = 0 };
            return reference => reference == null ? null : resolver.Resolve(reference, context);
        }

        private static GameState State(string pendingType)
        {
            var choices = pendingType == CharacterPendingChoiceTypes.TinManDiscard
                ? new List<string> { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }
                : new List<string> { pendingType == CharacterPendingChoiceTypes.TinManFirstPurchase
                    ? CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium : CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium,
                    CharacterEffectChoiceIds.TinManFinishPurchasing };
            var state = new GameState { GameId = "legacy-ui-consumption", MapId = StaticMapDefinitions.ThreePlayerMapId,
                ContentPackHash = "legacy-ui-content", Phase = GamePhase.ActionRound1, CurrentPlayerId = 1,
                StartPlayerId = 1, LaunchPlayerOrder = new List<int> { 1, 2, 3 },
                Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id }).ToList(),
                PendingCharacterEffect = new PendingCharacterEffectState { PlayerId = 1, CardId = "tin-man",
                    SourceCommandId = "original-pending-command", ChoiceType = pendingType, OptionIds = choices,
                    RemainingCardIds = new List<string> { "visible-discard" } } };
            state.FindPlayer(1).DiscardCardIds.Add("visible-discard");
            return state;
        }
    }
}
