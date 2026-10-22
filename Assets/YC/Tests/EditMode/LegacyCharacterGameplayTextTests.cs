using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Presentation;
using YC.Domain.Cards;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class LegacyCharacterGameplayTextTests
    {
        private const string Prefix = "core:legacy.character.tin_man.";

        [Test]
        public void PurchaseRefsSeparateNextCostFromPaidSummaryAndKeepLegacyCandidatesAndState()
        {
            foreach (var second in new[] { false, true })
            {
                var purchaseId = second ? CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium
                    : CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium;
                var state = State(second ? CharacterPendingChoiceTypes.TinManSecondPurchase
                    : CharacterPendingChoiceTypes.TinManFirstPurchase,
                    CharacterEffectChoiceIds.TinManFinishPurchasing, purchaseId);
                var pending = state.PendingCharacterEffect;
                var beforeIds = pending.OptionIds.ToArray();
                var query = new CharacterCardOptionQueryService().QueryPending(state, 1);
                var expectedCost = second ? CharacterCardService.TinManSecondPureOriginiumCost
                    : CharacterCardService.TinManFirstPureOriginiumCost;

                Assert.That(query.Get(CharacterEffectParameterKeys.Choice).Select(c => c.Id),
                    Is.EqualTo(new[] { purchaseId, CharacterEffectChoiceIds.TinManFinishPurchasing }));
                Assert.That(query.Presentation.Options.Select(o => o.CandidateId),
                    Is.EqualTo(query.Get(CharacterEffectParameterKeys.Choice).Select(c => c.Id)));
                Assert.That(Value(query.Presentation.Prompt, "cost"), Is.EqualTo(expectedCost));
                Assert.That(Value(query.Presentation.Options[0].Label, "cost"), Is.EqualTo(expectedCost));
                Assert.That(query.GoldVoucherCost, Is.EqualTo(second ? CharacterCardService.TinManFirstPureOriginiumCost : 0));
                Assert.That(query.ScoreGain, Is.EqualTo(1));
                Assert.That(query.PureOriginiumGain, Is.EqualTo(second ? 1 : 0));
                if (second) Assert.That(Value(query.Presentation.Prompt, "paid"), Is.EqualTo(query.GoldVoucherCost));
                var catalog = Catalog.Load();
                var document = new GameplayTextResolver(catalog).Resolve(query.Presentation.Options[0].Label, Context(catalog));
                Assert.That(document.Runs.OfType<NumberRun>().Select(n => n.Value), Does.Contain((long)expectedCost));
                Assert.That(document.Runs.OfType<IconRun>().Select(i => i.IconKey), Does.Contain("core:resource.gold_voucher"));
                Assert.That(state.PendingCharacterEffect, Is.SameAs(pending));
                Assert.That(pending.OptionIds, Is.EqualTo(beforeIds));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(7));
                Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
            }
        }

        [TestCase(CharacterPendingChoiceTypes.TinManFirstPurchase)]
        [TestCase(CharacterPendingChoiceTypes.TinManSecondPurchase)]
        public void OnlyAuthoritativeFinishCandidateDoesNotInventPurchaseOrFormalLuaIds(string choiceType)
        {
            var state = State(choiceType, CharacterEffectChoiceIds.TinManFinishPurchasing);
            var query = new CharacterCardOptionQueryService().QueryPending(state, 1,
                new Dictionary<string, string> { { CharacterEffectParameterKeys.TinManPurchasePureOriginium12, "true" } });
            Assert.That(query.Get(CharacterEffectParameterKeys.Choice).Select(c => c.Id),
                Is.EqualTo(new[] { CharacterEffectChoiceIds.TinManFinishPurchasing }));
            Assert.That(query.Presentation.Options.Select(o => o.CandidateId),
                Is.EqualTo(new[] { CharacterEffectChoiceIds.TinManFinishPurchasing }));
            Assert.That(query.Presentation.Options.Any(o => o.CandidateId == "continue" || o.CandidateId == "decline"), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(7));
        }

        [Test]
        public void DiscardRefUsesOnlyCurrentVisibleCardAndDoesNotRescanItsDisplayName()
        {
            var state = State(CharacterPendingChoiceTypes.TinManDiscard,
                CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence);
            state.PendingCharacterEffect.RemainingCardIds.Add("character.red.p1.cannot");
            state.PendingCharacterEffect.RemainingCardIds.Add("later-private-card");
            var query = new CharacterCardOptionQueryService().QueryPending(state, 1);
            Assert.That(query.Presentation.Prompt.Arguments, Has.Count.EqualTo(1));
            var card = query.Presentation.Prompt.Arguments[0];
            Assert.That(card.Kind, Is.EqualTo("definition"));
            Assert.That(card.Value.ReferenceId, Is.EqualTo("character.red.p1.cannot"));
            Assert.That(query.Presentation.Options.Select(o => o.CandidateId),
                Is.EqualTo(new[] { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }));
            Assert.That(query.Get(CharacterEffectParameterKeys.SourceInfluenceSlotId), Is.Empty);
            var catalog = Catalog.Load();
            var context = Context(catalog);
            context.DefinitionNames = new Dictionary<string, string> { { card.Value.ReferenceId, "可见[b]999[/b]" } };
            var document = new GameplayTextResolver(catalog).Resolve(query.Presentation.Prompt, context);
            Assert.That(document.Runs.OfType<TextRun>().Any(r => r.Text.Contains("可见[b]999[/b]")), Is.True);
            Assert.That(document.Runs.OfType<NumberRun>(), Is.Empty);
            Assert.That(document.ToPlainText(), Does.Not.Contain("later-private-card"));
            Assert.That(state.PendingCharacterEffect.RemainingCardIds,
                Is.EqualTo(new[] { "character.red.p1.cannot", "later-private-card" }));
        }

        [TestCase(null)]
        [TestCase("")]
        public void MissingCurrentCardReferenceKeepsExistingChoicesAndUsesPromptWithoutInventedSource(string cardId)
        {
            var state = State(CharacterPendingChoiceTypes.TinManDiscard,
                CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence);
            state.PendingCharacterEffect.RemainingCardIds.Add(cardId);
            var query = new CharacterCardOptionQueryService().QueryPending(state, 1);
            Assert.That(query.Presentation.Prompt.Key, Is.EqualTo(Prefix + "discard.prompt_without_card"));
            Assert.That(query.Presentation.Prompt.Arguments, Is.Empty);
            Assert.That(query.Get(CharacterEffectParameterKeys.Choice), Has.Count.EqualTo(2));
        }

        [Test]
        public void ForeignInvalidAndUnmigratedPendingDoNotCreatePresentation()
        {
            var service = new CharacterCardOptionQueryService();
            var state = State(CharacterPendingChoiceTypes.TinManFirstPurchase, CharacterEffectChoiceIds.TinManFinishPurchasing);
            Assert.That(service.QueryPending(state, 2).Presentation, Is.Null);
            Assert.That(service.QueryPending(state, 2).Get(CharacterEffectParameterKeys.Choice), Is.Empty);
            state.PendingCharacterEffect.OptionIds.Clear();
            Assert.That(service.QueryPending(state, 1).Presentation, Is.Null);
            state.PendingCharacterEffect = null;
            Assert.That(service.QueryPending(state, 1).Presentation, Is.Null);
            state = State(CharacterPendingChoiceTypes.LiskarmCleanupRemoval, "route:A1:0");
            var liskarm = service.QueryPending(state, 1);
            Assert.That(liskarm.Presentation, Is.Null);
            Assert.That(liskarm.Get(CharacterEffectParameterKeys.TargetInfluenceSlotId).Single().Id, Is.EqualTo("route:A1:0"));
        }

        [Test]
        public void AllMigratedTemplatesPrecompileAndResolveWithDeclaredTypedArguments()
        {
            var catalog = Catalog.Load();
            Assert.That(catalog.Texts, Has.Count.EqualTo(15));
            var resolver = new GameplayTextResolver(catalog);
            var diagnostics = new List<GameplayTextFormatException>();
            resolver.Diagnostic += diagnostics.Add;
            resolver.Precompile();
            foreach (var template in catalog.Texts)
            {
                var reference = new GameplayTextRef(template.Key);
                foreach (var parameter in template.Parameters)
                {
                    var value = parameter.Name == "paid" ? CharacterCardService.TinManFirstPureOriginiumCost
                        : parameter.Name == "cost" ? (template.Key.Contains("second_purchase")
                            ? CharacterCardService.TinManSecondPureOriginiumCost : CharacterCardService.TinManFirstPureOriginiumCost)
                        : parameter.Name == "gold" ? 5 : 1;
                    reference.Arguments.Add(new GameplayTextArgument { Name = parameter.Name, Kind = parameter.Kind,
                        Value = parameter.Kind == "definition" ? NormalizedValue.CreateStableReference("definition", "visible-card")
                            : NormalizedValue.CreateInteger(value) });
                }
                var context = Context(catalog);
                context.DefinitionNames = new Dictionary<string, string> { { "visible-card", "可见角色" } };
                Assert.That(resolver.Resolve(reference, context).Runs, Is.Not.Empty, template.Key);
            }
            Assert.That(diagnostics, Is.Empty);
        }

        private static long Value(GameplayTextRef reference, string name)
        {
            var argument = reference.Arguments.Single(a => a.Name == name);
            Assert.That(argument.Kind, Is.EqualTo("integer"));
            return argument.Value.IntegerValue;
        }

        private static GameState State(string choiceType, params string[] choices) => new GameState
        {
            MapId = StaticMapDefinitions.FourPlayerMapId,
            Players = { new PlayerState { PlayerId = 1, InfluenceSupply = 30, Score = 3, Resources = { GoldVoucher = 7 } } },
            PendingCharacterEffect = new PendingCharacterEffectState
            {
                PlayerId = 1, CardId = "character.red.p1.tin-man", ChoiceType = choiceType,
                OptionIds = choices.ToList(), SourceCommandId = "unchanged-command"
            }
        };

        private static GameplayTextContext Context(Catalog catalog) => new GameplayTextContext
        {
            Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale,
            ViewerPlayerId = 1, VisibilityScopeId = "legacy-owner"
        };

        private sealed class Catalog : IGameplayTextCatalogSnapshot
        {
            private readonly Dictionary<string, GameplayTextTemplateDefinition> templates = new Dictionary<string, GameplayTextTemplateDefinition>();
            private readonly Dictionary<string, GameplayIconDefinition> icons = new Dictionary<string, GameplayIconDefinition>();
            public string ContentHash => "legacy-character-fixed-input";
            public string Locale => "zh-CN";
            public IReadOnlyList<GameplayTextTemplateDefinition> Texts => templates.Values.Where(t => t.Key.StartsWith(Prefix, StringComparison.Ordinal)).ToList().AsReadOnly();
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) => templates.TryGetValue(key, out definition);
            public bool TryGetIcon(string key, out GameplayIconDefinition definition) => icons.TryGetValue(key, out definition);
            public static Catalog Load()
            {
                var root = Environment.GetEnvironmentVariable("NOMADCITY_LEGACY_TEXT_CONTENT_ROOT")
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core", "presentation");
                var catalog = new Catalog();
                foreach (var message in ((JObject)JObject.Parse(File.ReadAllText(Path.Combine(root, "texts.zh-CN.json")))["messages"]).Properties())
                {
                    var definition = new GameplayTextTemplateDefinition { Key = message.Name, Template = (string)message.Value["template"] };
                    foreach (var parameter in ((JObject)message.Value["parameters"]).Properties())
                        definition.Parameters.Add(new GameplayTextParameterDefinition { Name = parameter.Name, Kind = (string)parameter.Value });
                    catalog.templates.Add(definition.Key, definition);
                }
                foreach (var icon in JObject.Parse(File.ReadAllText(Path.Combine(root, "icons.json")))["icons"])
                {
                    var definition = icon.ToObject<GameplayIconDefinition>();
                    catalog.icons.Add(definition.Id, definition);
                }
                return catalog;
            }
        }
    }
}
