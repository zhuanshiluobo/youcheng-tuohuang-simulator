using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Commands;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Domain.Interactions;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    public sealed partial class CharacterCardEffectInteractionUiCoordinatorTests
    {
        [TestCase("hand")]
        [TestCase("discard")]
        public void CardDestinationShowsOneRealCardAndEachActionSubmitsItsOriginalIdOnce(string destination)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                var state = CreateState();
                var request = CardDestinationRequest();
                state.EffectRuntime.StateRevision = request.StateRevision;
                state.EffectRuntime.InteractionRequests.Add(request);
                var before = JsonConvert.SerializeObject(request);
                var fixture = CreateCoordinator(state);
                var commands = new List<GameCommand>();
                var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
                var view = CardDestinationView(request);
                renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer, new Func<GameStateView>(() => view));
                renderer.Render(InteractionRequestProjector.ProjectForPlayer(request, 1));
                var overlay = GetOverlay(fixture.Dialog);
                AssertRewardCardPage(overlay);
                var cards = RewardCards(overlay);
                Assert.That(cards, Has.Length.EqualTo(1), "两个去向不能伪装成两张卡。");
                Assert.That(((RawImage)cards[0].GetType().GetProperty("CardImage").GetValue(cards[0])).texture, Is.Not.Null);
                Assert.That(FindChild(overlay, "Confirm Selection"), Is.Null);
                Assert.That(FindChild(overlay, "Cancel Selection"), Is.Null);
                Assert.That(commands, Is.Empty);
                var action = FindChild(overlay, "Card Action " + destination).GetComponent<Button>();
                request.StateRevision++;
                action.onClick.Invoke();
                Assert.That(commands, Is.Empty, "旧版本动作不得提交。");
                request.StateRevision--;
                action.onClick.Invoke(); action.onClick.Invoke();
                FindChild(overlay, "Card Action " + (destination == "hand" ? "discard" : "hand")).GetComponent<Button>().onClick.Invoke();
                Assert.That(commands, Has.Count.EqualTo(1));
                Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { destination }));
                Assert.That(JsonConvert.SerializeObject(request), Is.EqualTo(before));
                artifacts.WriteLabeledText("单张流形与实际动作命令", JsonConvert.SerializeObject(new { request, view, commands }));
                renderer.Clear();
            }
            finally { artifacts.Finish(); }
        }

        [Test]
        public void CardDestinationMissingArtworkKeepsSingleCardAndBothGenuineActions()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                var state = CreateState(); var request = CardDestinationRequest();
                state.EffectRuntime.StateRevision = request.StateRevision;
                state.EffectRuntime.InteractionRequests.Add(request);
                var fixture = CreateCoordinator(state); var commands = new List<GameCommand>();
                var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
                var view = CardDestinationView(request);
                view.ItemZones.Items[0].DefinitionId = "missing-generated-artwork";
                renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer, new Func<GameStateView>(() => view));
                LogAssert.Expect(LogType.Warning, "[card_action_artwork_missing] request=real-card-destination item=opaque-flowing-shape");
                renderer.Render(InteractionRequestProjector.ProjectForPlayer(request, 1));
                var overlay = GetOverlay(fixture.Dialog); AssertRewardCardPage(overlay);
                var cards = RewardCards(overlay); Assert.That(cards, Has.Length.EqualTo(1));
                Assert.That(((Text)cards[0].GetType().GetProperty("FallbackLabel").GetValue(cards[0])).gameObject.activeSelf, Is.True);
                Assert.That(FindChild(overlay, "Card Action hand").GetComponent<Button>().IsInteractable(), Is.True);
                ClickButton(overlay, "Card Action discard");
                Assert.That(commands.Single().OptionIds, Is.EqualTo(new[] { "discard" }));
                artifacts.WriteLabeledText("缺图仍为单卡动作页", JsonConvert.SerializeObject(new { request, commands }));
                renderer.Clear();
            }
            finally { artifacts.Finish(); }
        }

        [Test]
        public void CardDestinationDoesNotReadHiddenCardIdentityOrFallBackToOrdinaryOptions()
        {
            var state = CreateState(); var request = CardDestinationRequest();
            state.EffectRuntime.StateRevision = request.StateRevision;
            state.EffectRuntime.InteractionRequests.Add(request);
            var fixture = CreateCoordinator(state); var commands = new List<GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer, new Func<GameStateView>(() => new GameStateView()));
            Assert.Throws<InvalidOperationException>(() => renderer.Render(InteractionRequestProjector.ProjectForPlayer(request, 1)));
            Assert.That(commands, Is.Empty);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        private static InteractionRequest CardDestinationRequest() => new InteractionRequest
        {
            InteractionId = "real-card-destination", InteractionTypeId = "character.card.destination",
            StateRevision = 12, CandidateSetId = "actual-card-destinations", CandidateSetVersion = 3,
            Status = "open", Visibility = "owner", AnsweringPlayerId = 1, AnswerSchema = "candidate_id",
            MinSelections = 1, MaxSelections = 1, CandidateIds = new List<string> { "hand", "discard" },
            PromptKey = "core:interaction.card.destination.choose",
            Presentation = new GameplayInteractionPresentation
            {
                Prompt = new GameplayTextRef("core:interaction.card.destination.choose", new GameplayTextArgument
                { Name = "card", Kind = "item", Value = NormalizedValue.CreateStableReference("item", "opaque-flowing-shape") })
            }
        };

        private static GameStateView CardDestinationView(InteractionRequest request)
        {
            var view = new GameStateView { GameId = "character-ui-contract-test", StateRevision = request.StateRevision,
                ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };
            // 组件夹具显式提供同一请求的完整观察者合同，不绕过实体文本投影的授权门禁。
            view.Interactions.Add(new InteractionView { InteractionId = request.GetStableInteractionId(),
                SourceNodeId = request.SourceNodeId, OwnerEffectId = request.OwnerEffectId, Kind = request.InteractionTypeId,
                AnsweringPlayerId = request.AnsweringPlayerId, Visibility = request.Visibility, StateRevision = request.StateRevision,
                CandidateSetId = request.CandidateSetId, CandidateSetVersion = request.CandidateSetVersion,
                MinSelections = request.MinSelections, MaxSelections = request.MaxSelections, AllowDecline = request.AllowDecline,
                AnswerSchema = request.AnswerSchema, Status = request.Status, CandidateIds = new List<string>(request.CandidateIds),
                Presentation = request.Presentation, PromptKey = request.PromptKey });
            view.ItemZones.Items.Add(new ItemInstanceState { ItemId = "opaque-flowing-shape", TypeId = "core.generated_character_card",
                DefinitionId = "infected.character.flowing_shape_alpha" });
            return view;
        }
    }
}
