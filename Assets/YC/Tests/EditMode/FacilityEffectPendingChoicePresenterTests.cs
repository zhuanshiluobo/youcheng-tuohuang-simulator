using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class FacilityEffectPendingChoicePresenterTests
    {
        [Test]
        public void TryGetPending_AcceptsOnlyLocalFacilityScenario()
        {
            var presenter = new FacilityEffectPendingChoicePresenter();
            var state = CreateState(FacilityPendingChoiceTypes.ScenarioId, 1);

            PendingCardSessionState pending;
            Assert.That(presenter.TryGetPending(state, 1, out pending), Is.True);
            Assert.That(pending, Is.SameAs(state.PendingCardSession));
            Assert.That(presenter.TryGetPending(state, 2, out pending), Is.False);

            state.PendingCardSession.ScenarioId = "explore_event";
            Assert.That(presenter.TryGetPending(state, 1, out pending), Is.False,
                "事件牌会话不能被设施待选入口接管。");
        }

        [Test]
        public void CreateResolveCommand_CopiesSessionOptionAndSubmittedParameters()
        {
            var presenter = new FacilityEffectPendingChoicePresenter();
            var pending = CreateState(FacilityPendingChoiceTypes.ScenarioId, 1).PendingCardSession;
            var command = presenter.CreateResolveCommand(
                pending,
                1,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    [ResolveFacilityEffectCommandHandler.OriginiumAmountParameter] = "2",
                    [ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter] = "1",
                    [ResolveFacilityEffectCommandHandler.IronAmountParameter] = "2"
                });

            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.SourceId, Is.EqualTo("building_029"));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { FacilityPendingChoiceTypes.ConfirmOption }));
            Assert.That(
                command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter],
                Is.EqualTo("facility-session"));
            Assert.That(
                command.Parameters[ResolveFacilityEffectCommandHandler.OptionIdParameter],
                Is.EqualTo(FacilityPendingChoiceTypes.ConfirmOption));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.OriginiumAmountParameter], Is.EqualTo("2"));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter], Is.EqualTo("1"));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.IronAmountParameter], Is.EqualTo("2"));
        }

        [Test]
        public void CreateResolveCommand_RejectsNonFacilityPendingSession()
        {
            var presenter = new FacilityEffectPendingChoicePresenter();
            var pending = CreateState("explore_event", 1).PendingCardSession;

            Assert.That(
                () => presenter.CreateResolveCommand(pending, 1, "option"),
                Throws.ArgumentException);
        }

        private static GameState CreateState(string scenarioId, int playerId)
        {
            return new GameState
            {
                PendingCardSession = new PendingCardSessionState
                {
                    SessionId = "facility-session",
                    ScenarioId = scenarioId,
                    ChoiceType = FacilityPendingChoiceTypes.ChooseFiveBasicResources,
                    CardId = "building_029",
                    PlayerId = playerId,
                    OptionIds = { FacilityPendingChoiceTypes.ConfirmOption }
                }
            };
        }
    }
}
