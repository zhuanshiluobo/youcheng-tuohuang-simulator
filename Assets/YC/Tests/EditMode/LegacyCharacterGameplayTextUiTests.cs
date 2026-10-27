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
            var reference = new GameplayTextRef("core:action.character.use.name", new GameplayTextArgument {
                Name = "card", Kind = "definition", Value = NormalizedValue.CreateStableReference("definition", "visible-discard") });
            var document = new GameplayTextResolver(pack.Presentation).Resolve(reference,
                new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                    Locale = pack.Presentation.Locale, DefinitionNames = names });
            Assert.That(document.ToPlainText(), Does.Contain(literalName));
            Assert.That(document.Runs.OfType<IconRun>(), Is.Empty, "来源名称作为字面参数，不再次解析标记");
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
                "status.cover_select", "status.effect_not_integrated",
                "status.can_use", "status.inspect_only", "status.player_missing", "unavailable.effect_not_integrated",
                "unavailable.effect_currently_unavailable", "unavailable.cover_none", "unavailable.cover_selection_stale",
                "unavailable.not_initialized", "unavailable.cannot_activate", "unavailable.effect_not_registered"
            };
            foreach (var key in references)
                Assert.That(resolve(new GameplayTextRef("core:legacy.character." + key)).Runs, Is.Not.Empty);
            Assert.That(resolve(new GameplayTextRef("core:interaction.map.influence.remove")).Runs, Is.Not.Empty);
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
