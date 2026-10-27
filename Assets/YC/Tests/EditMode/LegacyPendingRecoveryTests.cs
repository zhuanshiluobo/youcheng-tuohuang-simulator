using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // 隔离恢复协议检查；不作为正常菜单、UI或历史真实归档的业务验收。
    public sealed class LegacyPendingRecoveryTests
    {
        private static readonly string[] FacilityTypes = {
            FacilityPendingChoiceTypes.CopyAdjacentEntryEffect, FacilityPendingChoiceTypes.BuildAdditionalFacility, FacilityPendingChoiceTypes.BuildExtensionHub,
            FacilityPendingChoiceTypes.SellResources, FacilityPendingChoiceTypes.FreeCityMove, FacilityPendingChoiceTypes.ChooseFiveBasicResources,
            FacilityPendingChoiceTypes.ReplaceOneInfluence, FacilityPendingChoiceTypes.DeployTwoInfluences, FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore };
        private static readonly string[] CharacterTypes = { CharacterPendingChoiceTypes.TinManDiscard, CharacterPendingChoiceTypes.TinManFirstPurchase,
            CharacterPendingChoiceTypes.TinManSecondPurchase, CharacterPendingChoiceTypes.LiskarmCleanupRemoval, CharacterPendingChoiceTypes.SecondEffectDecision, CharacterPendingChoiceTypes.SecondEffectExecution };
        private static readonly string[] SpecialSteps = { SpecialActionPendingSteps.AwaitMilitaryTargets, SpecialActionPendingSteps.AwaitMobilizationTarget,
            SpecialActionPendingSteps.AwaitFreeMoveTarget, SpecialActionPendingSteps.AwaitMoveEvent, SpecialActionPendingSteps.AwaitRouteInfluence };

        [Test]
        public void ActualBootstrapperDoesNotRegisterLegacyFacilityOrSpecialAnswerHandlers()
        {
            using var f = Create();
            var handlers = (IEnumerable<IGameCommandHandler>)typeof(GameSession).GetField("commandHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Session);
            Assert.That(handlers.Any(h => h is ResolveFacilityEffectCommandHandler), Is.False);
            Assert.That(handlers.OfType<UseSpecialActionCommandHandler>().Single().CanHandle(new GameCommand { Kind = GameCommandKind.ResolvePendingChoice }), Is.False);
            Assert.That(handlers.OfType<UseCharacterCardCommandHandler>().Single().CanHandle(new GameCommand { Kind = GameCommandKind.ResolvePendingChoice }), Is.True,
                "只有命令名匹配不能证明旧角色终结路径可恢复");
            Assert.That(handlers.Any(h => h is AnswerInteractionCommandHandler), Is.True);
        }

        [Test]
        public void AllLegacyFacilityCharacterAndSpecialSlotsRejectWithoutChangingArchiveOrExecutingEffects()
        {
            using var f = Create(); int executions = 0;
            f.Registry.Register(new EffectRegistration("recovery.pending.probe", _ => { executions++; return EffectStepResult.Completed(); }, EffectExecutorKind.IntrinsicFlow, "1"));
            var engine = new EffectTreeExecutor(f.Session.State, f.Registry);
            engine.CreateRoot(new EffectSpec("recovery.pending.probe") { PlayerId = 1, DefinitionVersion = "1" });
            var clean = HostRecoveryService.CreateArchive(f.Session.State);
            var baseline = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(clean), f.Registry);
            Assert.That(baseline.Succeeded, Is.True, baseline.FaultCode + ":" + baseline.Diagnostic);
            var changes = FacilityTypes.Select(type => (Action<GameState>)(s => s.PendingCardSession = CardSession(type)))
                .Concat(CharacterTypes.Select(type => (Action<GameState>)(s => s.PendingCharacterEffect = Character(type))))
                .Concat(SpecialSteps.Select(step => (Action<GameState>)(s => s.PendingSpecialAction = Special(step)))).ToList();
            foreach (var change in changes)
            {
                var archive = GameStateCloneService.DeepClone(clean); change(archive.Snapshot.State);
                string before = HostRecoveryService.SerializeArchive(archive);
                var result = HostRecoveryService.Recover(archive, f.Registry);
                Assert.That(result.Succeeded, Is.False); Assert.That(result.FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported), result.Diagnostic);
                Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
                Assert.That(executions, Is.Zero);
                var raw = HostRecoveryService.RecoverJson(before, f.Registry);
                Assert.That(raw.Succeeded, Is.False); Assert.That(raw.FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported), raw.Diagnostic);
                Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
            }
        }

        [Test]
        public void RegisteredLegacyCharacterFinishCannotWriteTheEntityLifecycle()
        {
            using var f = Create();
            var handlers = (IEnumerable<IGameCommandHandler>)typeof(GameSession).GetField("commandHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Session);
            var handler = handlers.OfType<UseCharacterCardCommandHandler>().Single();
            var work = GameStateCloneService.DeepClone(f.Session.State);
            work.PendingCharacterEffect = Character(CharacterPendingChoiceTypes.SecondEffectDecision);
            var command = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            command.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] = CharacterEffectChoiceIds.FinishCharacterUse;
            var error = Assert.Throws<InvalidOperationException>(() => handler.Handle(work, command));
            Assert.That(error.Message, Does.Contain("legacy_position_write_rejected"));
            Assert.That(f.Session.State.PendingCharacterEffect, Is.Null);
        }

        [Test]
        public void UnsupportedJournalCheckpointIsRejectedBeforeAnyRecoveryProgress()
        {
            using var f = Create(); var archive = HostRecoveryService.CreateArchive(f.Session.State);
            archive.Journal.SchemaVersion = archive.Snapshot.SchemaVersion = 3;
            var checkpoint = GameStateCloneService.DeepClone(f.Session.State); checkpoint.PendingCardSession = CardSession(FacilityTypes[0]);
            archive.Journal.Entries.Add(new HostJournalEntryDto { CommitId = "old.pending", CommitSequence = archive.Snapshot.LastCommitSequence + 1,
                StateRevision = archive.Snapshot.StateRevision + 1, StateAfter = checkpoint, ContentHash = archive.Snapshot.ContentHash });
            string original = HostRecoveryService.SerializeArchive(archive);
            var recovered = HostRecoveryService.Recover(archive, f.Registry);
            Assert.That(recovered.FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported));
            Assert.That(HostRecoveryService.RecoverJson(original, f.Registry).FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        [Test]
        public void ResolvedHistoricalPendingDoesNotPreemptTheOriginalJournalValidation()
        {
            using var f = Create(); var archive = HostRecoveryService.CreateArchive(f.Session.State);
            archive.Journal.SchemaVersion = archive.Snapshot.SchemaVersion = 3;
            var final = GameStateCloneService.DeepClone(f.Session.State);
            archive.Snapshot.State.PendingCardSession = CardSession(FacilityTypes[0]);
            archive.Journal.Entries.Add(new HostJournalEntryDto { CommitId = "historical.finish", CommitSequence = archive.Snapshot.LastCommitSequence + 99,
                StateRevision = archive.Snapshot.StateRevision + 1, StateAfter = final, ContentHash = archive.Snapshot.ContentHash });
            string original = HostRecoveryService.SerializeArchive(archive);
            Assert.That(HostRecoveryService.Recover(archive, f.Registry).FaultCode, Is.EqualTo(HostRecoveryService.JournalGap));
            Assert.That(HostRecoveryService.RecoverJson(original, f.Registry).FaultCode, Is.EqualTo(HostRecoveryService.JournalGap));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        [Test]
        public void AliasesUnknownTypesAndMalformedRawSlotsCannotBypassThePolicy()
        {
            using var f = Create(); var baseline = HostRecoveryService.CreateArchive(f.Session.State);
            foreach (var type in FacilityTypes.Concat(CharacterTypes).Concat(new[] { "entrance_event", "explore_event", "move_city_event", "unknown.pending" }))
            {
                var archive = GameStateCloneService.DeepClone(baseline);
                archive.Snapshot.State.PendingChoice = new PendingChoiceState { ChoiceType = type, CardId = "old.card", PlayerId = 1, OptionIds = new List<string> { "old.option" } };
                var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), f.Registry);
                Assert.That(result.FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported), type);
            }
            var malformed = JObject.Parse(HostRecoveryService.SerializeArchive(baseline));
            malformed["Snapshot"]["State"]["PendingCharacterEffect"] = new JArray("old");
            Assert.That(HostRecoveryService.RecoverJson(malformed.ToString(Formatting.None), f.Registry).FaultCode, Is.EqualTo(HostRecoveryService.LegacyPendingUnsupported));
        }

        [Test]
        public void RejectedOldPendingNeverInstallsIntoTheLiveSessionAndTheOriginalJsonCanStillBeSaved()
        {
            using var f = Create(); var archive = HostRecoveryService.CreateArchive(f.Session.State);
            archive.Snapshot.State.PendingCharacterEffect = Character(CharacterPendingChoiceTypes.SecondEffectDecision);
            string json = HostRecoveryService.SerializeArchive(archive); string before = JsonConvert.SerializeObject(f.Session.State);
            var result = HostRecoveryService.RecoverJson(json, f.Registry);
            if (result.Succeeded) f.Session.ReplaceState(result.State);
            Assert.That(result.Succeeded, Is.False); Assert.That(JsonConvert.SerializeObject(f.Session.State), Is.EqualTo(before));
            var copy = new StringWriter(); HostRecoveryService.SerializeArchive(archive, copy);
            Assert.That(copy.ToString(), Is.EqualTo(json));
            Assert.That(archive.Snapshot.State.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.SecondEffectDecision));
        }

        [Test]
        public void NormalEntityEffectRequestRestoresAndAnswersThroughTheRegisteredHandlerOnce()
        {
            using var f = Create(); var state = f.Session.State;
            var engine = new EffectTreeExecutor(state, f.Registry);
            string id = engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, ""));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == id && r.Status == "open");
            Assert.That(state.PendingCardSession, Is.Null); Assert.That(state.PendingCharacterEffect, Is.Null); Assert.That(state.PendingSpecialAction, Is.Null); Assert.That(state.PendingChoice, Is.Null);
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var result = HostRecoveryService.RecoverJson(archive, f.Registry);
            Assert.That(result.Succeeded, Is.True, result.FaultCode + ":" + result.Diagnostic);
            f.Session.ReplaceState(result.State);
            request = f.Session.State.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == id && r.Status == "open");
            var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { request.CandidateIds.First() }, false);
            var handled = f.Session.Submit(command);
            Assert.That(handled.Succeeded, Is.True, handled.Validation.Reason);
            Assert.That(f.Session.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            int count = f.Session.State.Map.Influences.Count;
            Assert.That(f.Session.Submit(command).Succeeded, Is.False);
            Assert.That(f.Session.State.Map.Influences.Count, Is.EqualTo(count));
        }

        [Test]
        public void NormalFreshStateAndGenericVersionOrHandlerFailuresKeepTheirExistingRecoverySemantics()
        {
            using var f = Create(); var archive = HostRecoveryService.CreateArchive(f.Session.State);
            string json = HostRecoveryService.SerializeArchive(archive);
            Assert.That(HostRecoveryService.RecoverJson(json, f.Registry).Succeeded, Is.True);
            var raw = JObject.Parse(json);
            raw["Snapshot"]["State"]["EffectRuntime"]["SchemaVersion"] = EffectRuntimeState.CurrentSchemaVersion + 100;
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), f.Registry).Succeeded, Is.False,
                "未知版本交原通用运行态校验，不能因放开原始整数读取而被接受");
            raw = JObject.Parse(json); raw["Snapshot"]["ContentHash"] = "different-content";
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), f.Registry).FaultCode, Is.EqualTo(HostRecoveryService.ContentHashMismatch));
        }

        private static PendingCardSessionState CardSession(string type) => new PendingCardSessionState { SessionId = "old.facility", ScenarioId = "facility_entry_effect",
            ChoiceType = type, CardId = "old.facility.card", PlayerId = 1, OptionIds = new List<string> { "old.option" } };
        private static PendingCharacterEffectState Character(string type) => new PendingCharacterEffectState { ChoiceType = type, PlayerId = 1, CardId = "tin-man",
            SourceCommandId = "old.use", OptionIds = new List<string> { CharacterEffectChoiceIds.FinishCharacterUse }, RemainingCardIds = new List<string> { "old.discard" }, RemainingEffectMode = "strategy" };
        private static PendingSpecialActionState Special(string step) => new PendingSpecialActionState { SessionId = "old.special", PlayerId = 1, SpecialActionId = "old.special.action",
            DeclarationMarkerId = "old.marker", Step = step, RemainingRepetitions = 1 };

        private sealed class Fixture : IDisposable
        {
            public GameSession Session; public EffectRegistry Registry;
#if !LEGACY_PENDING_OFFLINE
            public UnityEngine.GameObject Owner;
#endif
            public void Dispose()
            {
#if !LEGACY_PENDING_OFFLINE
                if (Owner != null) UnityEngine.Object.DestroyImmediate(Owner);
#endif
            }
        }
        private static Fixture Create()
        {
#if LEGACY_PENDING_OFFLINE
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            CharacterCardDatabase.Initialize(pack.CreateCharacters()); EventCardDatabase.InitializeExternal(pack.CreateEvents()); FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
            YC.Domain.CityStyles.CityStyleDatabase.InitializeExternal(pack.CreateCityStyles()); SpecialActionDatabase.InitializeExternal(pack.CreateSpecialActions());
#endif
            var f = new Fixture(); Type bootstrap, launchType; object launch;
#if LEGACY_PENDING_OFFLINE
            bootstrap = typeof(YC.Presentation.GameSessionBootstrapper); launchType = typeof(YC.Presentation.GameLaunchContext); launch = Activator.CreateInstance(launchType);
#else
            bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            launchType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            f.Owner = new UnityEngine.GameObject("旧Pending隔离恢复检查"); f.Owner.SetActive(false); launch = f.Owner.AddComponent(launchType);
#endif
            var configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3, EnabledExpansionIds = new List<string> { "core" } };
            var values = new Dictionary<string, object> { { "Mode", LaunchMode.Host }, { "LocalPlayerId", 1 }, { "RoomId", "旧Pending隔离" }, { "GameId", "legacy.pending.recovery" },
                { "EventDeckSeed", 321 }, { "InitialRandomState", GameplayRandomRuntime.Create("legacy.pending.recovery") }, { "RandomReady", true }, { "RuleConfiguration", configuration },
                { "Players", Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList() }, { "LaunchPlayerOrder", new List<int> { 1, 2, 3 } }, { "MapId", configuration.MapId } };
            foreach (var value in values) launchType.GetField(value.Key)?.SetValue(launch, value.Value);
            var result = bootstrap.GetMethod("BuildForGameplay", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { launch, false, false });
            f.Session = (GameSession)result.GetType().GetField("Session").GetValue(result); f.Registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result);
            return f;
        }
    }
}
