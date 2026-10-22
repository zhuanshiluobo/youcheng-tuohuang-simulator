using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class MapTargetConfirmationTests
    {
        [TestCase("MapEffectInteractionUiCoordinator")]
        [TestCase("CharacterAbilityInteractionUiCoordinator")]
        [TestCase("EventCardInteractionUiCoordinator")]
        [TestCase("FacilityInteractionAdapter")]
        [TestCase("SpecialActionInteractionAdapter")]
        [TestCase("CharacterCardInteraction")]
        public void EveryEffectMapEntry_UsesSharedConfirmationScope(string name)
        {
            var type = System.Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
            Assert.That(typeof(IMapConfirmationScope).IsAssignableFrom(type), Is.True);
        }

        [Test]
        public void RoutedEffects_RequireIndependentSecondClick_KeepOtherCandidates_AndResetOnScopeChange()
        {
            var interaction = new EffectTargets();
            var router = new InteractionRouter(_ => { }); router.Register(interaction);
            router.OnLocationClicked("A", 1);
            router.OnLocationClicked("A", 1);
            Assert.That(interaction.Commands, Is.Zero);
            var presentation = router.BuildActivePresentation();
            Assert.That(presentation.Highlights.Count, Is.EqualTo(2));
            Assert.That(presentation.Highlights.Single(h => h.TargetId == "A").State, Is.EqualTo(WorkflowHighlightState.PendingConfirmation));
            router.OnLocationClicked("B", 2);
            Assert.That(interaction.Commands, Is.Zero);
            Assert.That(router.BuildActivePresentation().Highlights.Single(h => h.TargetId == "A").State, Is.EqualTo(WorkflowHighlightState.Available));
            interaction.Scope = "request-1:revision-2:step-1";
            router.OnLocationClicked("B", 3);
            Assert.That(interaction.Commands, Is.Zero, "旧修订不能确认新目标");
            router.OnLocationClicked("B", 4);
            Assert.That(interaction.Commands, Is.EqualTo(1));
            Assert.That(router.HasPendingMapConfirmation, Is.False);
        }

        [Test]
        public void CancellationOrInvalidTarget_ClearsOnlyTransientSelection()
        {
            var interaction = new EffectTargets();
            var router = new InteractionRouter(_ => { }); router.Register(interaction);
            router.OnLocationClicked("A", 1); router.ClearMapConfirmation();
            router.OnLocationClicked("A", 2);
            Assert.That(interaction.Commands, Is.Zero);
            interaction.Targets.RemoveAt(0);
            router.BuildActivePresentation();
            Assert.That(router.HasPendingMapConfirmation, Is.False);
            router.OnLocationClicked("B", 3); router.OnLocationClicked("B", 4);
            Assert.That(interaction.Commands, Is.EqualTo(1));
        }

        private sealed class EffectTargets : InteractionBase, IMapConfirmationScope
        {
            public string Scope = "request-1:revision-1:step-1";
            public int Commands;
            public readonly List<WorkflowHighlight> Targets = new List<WorkflowHighlight>
            {
                new WorkflowHighlight(WorkflowHighlightTargetKind.Location, "A", WorkflowHighlightSemantic.MoveTarget),
                new WorkflowHighlight(WorkflowHighlightTargetKind.Location, "B", WorkflowHighlightSemantic.MoveTarget)
            };
            public string MapConfirmationScope => Scope;
            public override string Id => "test.effect";
            public override InteractionPriority Priority => InteractionPriority.PendingResolution;
            public override bool IsActive => true;
            public override InteractionPresentation BuildPresentation() => new InteractionPresentation(Targets, "", InteractionMode.Busy);
            public override InteractionResult OnLocationClicked(string id) { Commands++; return InteractionResult.Consumed; }
            public override InteractionResult OnInfluenceSlotClicked(string id) => InteractionResult.Consumed;
            public override InteractionResult OnMobileCityClicked() => InteractionResult.Consumed;
            public override InteractionResult OnEscape() => InteractionResult.Consumed;
            public override void Cancel() { }
        }
    }
}
