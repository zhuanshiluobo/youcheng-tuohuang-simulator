using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PreparationPoolResolverTests
    {
        [Test]
        public void FixedAndRequiredRandomResolveExactCandidatesAndRejectMissingOrDuplicateDefinitions()
        {
            var all = new[] { "item.c", "item.a", "item.b" };
            int shuffles = 0;
            void Reverse(IList<string> values) { shuffles++; var reversed = values.Reverse().ToArray(); for (int i = 0; i < values.Count; i++) values[i] = reversed[i]; }
            var fixedPool = new PreparationPoolPolicy { Kind = "fixed", Count = 2, CandidateDefinitionIds = new List<string> { "item.c", "item.a" } };
            Assert.That(PreparationPoolResolver.Resolve(fixedPool, all, x => x, Reverse, "fixture"), Is.EqualTo(new[] { "item.c", "item.a" }));
            Assert.That(shuffles, Is.Zero);
            var randomPool = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 2,
                RequiredDefinitionIds = new List<string> { "item.a" } };
            Assert.That(PreparationPoolResolver.Resolve(randomPool, all, x => x, Reverse, "fixture"), Is.EqualTo(new[] { "item.a", "item.c" }));
            Assert.That(shuffles, Is.EqualTo(1));
            randomPool.RequiredDefinitionIds[0] = "item.missing";
            Assert.That(Assert.Throws<InvalidOperationException>(() => PreparationPoolResolver.Resolve(randomPool, all, x => x, Reverse, "fixture")).Message,
                Does.StartWith("preparation_pool_definition_missing:"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => PreparationPoolResolver.Resolve(fixedPool, new[] { "item.a", "item.a", "item.c" }, x => x, Reverse, "fixture")).Message,
                Does.StartWith("preparation_pool_definition_duplicate:"));
        }

        [Test]
        public void ConfiguredCityStyleSupplyIsSelectedOnceAndDoesNotUseOldDrawCounts()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(core, config);
            config.ContentPackHash = catalog.ContentPackHash;
            var definitions = catalog.ContentDefinitions;
            var levelOne = definitions.CityStyles.Where(x => x.Level == 1).Select(x => x.CityStyleId).OrderBy(x => x).ToArray();
            var levelTwo = definitions.CityStyles.Where(x => x.Level == 2).Select(x => x.CityStyleId).OrderBy(x => x).ToArray();
            Assert.That(levelOne.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(levelTwo.Length, Is.GreaterThanOrEqualTo(1));
            var recipe = catalog.SetupRecipe;
            recipe.Preparation = new PreparationFlowPlan { ModeId = config.RuleModeId, CityStyles = new PreparationCityStylePlan {
                LevelOne = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 2,
                    CandidateDefinitionIds = levelOne.Take(3).ToList(), RequiredDefinitionIds = new List<string> { levelOne[0] } },
                LevelTwo = new PreparationPoolPolicy { Kind = "fixed", Count = 1, CandidateDefinitionIds = new List<string> { levelTwo[0] } }
            } };
            var seats = new[] { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 } };
            GameState Create() => GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, config, 321,
                recipe, definitions, null, "preparation-style-fixture", null, catalog);
            List<string> Supply(GameState state)
            {
                string zone = state.ItemZones.Zones.Single(z => z.LocalZoneId == "city_styles").ZoneId;
                return state.ItemZones.Items.Where(item => item.ZoneId == zone).Select(item => item.DefinitionId).OrderBy(x => x).ToList();
            }
            var first = Create();
            var second = Create();
            var actual = Supply(first);
            Assert.That(actual, Has.Count.EqualTo(3));
            Assert.That(actual, Does.Contain(levelOne[0]).And.Contain(levelTwo[0]));
            Assert.That(actual.Count(id => CityStyleDatabase.Get(first, id).Level == 1), Is.EqualTo(2));
            Assert.That(actual.Count(id => CityStyleDatabase.Get(first, id).Level == 2), Is.EqualTo(1));
            Assert.That(Supply(second), Is.EqualTo(actual));
            Assert.That(first.SetupRecipe.Preparation.CityStyles.LevelOne.Count, Is.EqualTo(2));
        }
    }
}
