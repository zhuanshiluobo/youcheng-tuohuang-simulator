using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class MartinContentDeclarationTests : MartinContentTestFixture
    {
        [Test] public void M13ObservesOnlySuccessfulOriginalBuildCompletionAndKeepsExactWrappedRemovalSource()
        {
            var d=Pack.ActiveDefinitions.Single(x=>x.DefinitionId=="entrepreneur.ability.M13");
            var after=d.AbilitySources.Single(s=>s.AbilityId=="entrepreneur.M13.urbanization_after");
            Assert.That(after.Kind,Is.EqualTo("event"));Assert.That(after.ObservedEvents,Is.EqualTo(new[]{"EffectCompleted"}));
            Assert.That(after.CompletedEffectTypes,Is.EqualTo(new[]{FacilityEntryEffectTypeIds.Build}));Assert.That(after.CompletionPolicy,Is.EqualTo("succeeded"));
            Assert.That(after.AllowedOperations,Does.Contain("execution.bind"));
            Assert.That(d.AbilityHandlers.Single(h=>h.HandlerId==after.HandlerRef).CompletionHandlerId,Is.Not.Empty);
            var required=d.AbilitySources.Single(s=>s.AbilityId=="entrepreneur.M13.overlay_response");
            Assert.That(required.Kind,Is.EqualTo("setup"));Assert.That(required.ObservedEvents,Is.Empty);
            Assert.That(d.AbilitySources.Any(s=>s.ObservedEvents.Contains("FacilityPlaced")),Is.False,"不双挂落位和完成观察");
        }
        [Test] public void AllTwelveRoundDetailsKeepTypedTextReferences()
        {
            var rows=Pack.ActiveDefinitions.Where(d=>d.ContentType=="entrepreneur_round"&&d.DefinitionId.StartsWith("entrepreneur.round.M."))
                .SelectMany(d=>d.Data["playerDetails"]).ToList();
            Assert.That(rows.Count,Is.EqualTo(12));Assert.That(rows.All(r=>r["effectTextRef"] is Newtonsoft.Json.Linq.JObject),Is.True);
        }
        [Test] public void EveryOriginalFaceRuleAndImmediateHasOneSelectedFaceSource()
        {
            var owner = Pack.ActiveDefinitions.Single(d => d.DefinitionId == "entrepreneur.ability.M13");
            var content = owner.Data["entrepreneur"].ToObject<EntrepreneurDefinition>();
            foreach (var face in content.RoundGroups.SelectMany(g => g.Candidates))
            {
                var entity = Pack.ActiveDefinitions.Single(d => d.DefinitionId == face.EntityDefinitionId);
                foreach (string id in face.RuleRefs)
                    Assert.That(entity.AbilitySources.Count(s => s.AbilityId == id && s.ActiveFaceId == face.FaceId && s.AuthorizationPolicy == "active_selection"), Is.EqualTo(1));
                foreach (string handler in face.ImmediateHandlerRefs)
                    Assert.That(entity.AbilitySources.Count(s => s.HandlerRef == handler && s.Kind == "setup" && s.ActiveFaceId == face.FaceId), Is.EqualTo(1));
            }
            Assert.That(content.Components.Where(c => c.CarrierLocalZoneId == "blueprints").Sum(c => c.Quantity), Is.EqualTo(9));
            Assert.That(content.SetupHandlerRefs, Is.Empty, "初始组件沿004，不新增发放");
        }
        [Test] public void DraftAndUnselectedFaceCannotProvideActionOrGrantSource()
        {
            var state = State("entrepreneur.round.M.5");
            Assert.That(Registry.QuerySources(state, 1).Where(s => s.DefinitionId == "entrepreneur.round.M.5"), Is.Empty);
            SelectFace(state, "entrepreneur.round.M.5", "M09");
            var selected = Registry.QuerySources(state, 1).Where(s => s.DefinitionId == "entrepreneur.round.M.5").ToList();
            Assert.That(selected.Count, Is.GreaterThan(0)); Assert.That(selected.All(s => s.ActiveFaceId == "M09"), Is.True);
            Assert.That(Registry.QuerySources(state, 2).Where(s => s.DefinitionId == "entrepreneur.round.M.5"), Is.Empty);
        }
        [Test] public void BothGrantSourcesHaveExactOtherPlayerPermissionAndKeepM01Held()
        {
            var round = Pack.ActiveDefinitions.Single(d => d.DefinitionId == "entrepreneur.round.M.5");
            foreach (string face in new[] { "M09", "M10" })
                Assert.That(round.AbilitySources.Single(s => s.AbilityId == "entrepreneur." + face + ".setup").AllowedOperations,
                    Is.EqualTo(new[] { "reward.grant:entrepreneur.award.M01:core.award_card:1:other:core.zone.player_rewards" }));
            var award = Pack.ActiveDefinitions.Single(d => d.DefinitionId == "entrepreneur.award.M01");
            Assert.That((string)award.Data["lifecycle"], Is.EqualTo("held")); Assert.That((bool)award.Data["randomAwardPool"], Is.False);
        }
        [Test] public void SharedTrackBelongsToTheRoundEntityAndFormalG0StillRejectsContentReadiness()
        {
            foreach (string round in new[] { "1", "5", "7" })
            {
                var d = Pack.ActiveDefinitions.Single(x => x.DefinitionId == "entrepreneur.round.M." + round);
                string track = d.UsageTracks.Single().TrackId;
                Assert.That(d.AbilitySources.SelectMany(s => s.Actions).All(a => a.UsageTrackId == track), Is.True);
            }
            Assert.That(RuleModeReadiness.Missing(new[] { "entrepreneur_content.M.v1" }), Does.Contain("entrepreneur_content.M.v1"));
        }
    }
}
