using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 事务/事件身份窄验，不计作可见四端正常联机或完整局验收。
    public sealed class GameSessionCommandEventIdentityTests
    {
        public static Action<string, object> Evidence;

        private static GameState State(string gameId, int currentPlayer = 1)
        {
            return new GameState {
                GameId = gameId, Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1,
                StartPlayerId = 1, CurrentPlayerId = currentPlayer, UseSeatTurnOrder = true,
                LaunchPlayerOrder = new List<int> { 1, 2, 3, 4 },
                RandomRuntime = GameplayRandomRuntime.Create(gameId),
                Players = Enumerable.Range(1, 4).Select(id => new PlayerState {
                    PlayerId = id, ActedMainActionThisTurn = id == currentPlayer }).ToList()
            };
        }

        [Test]
        public void EndActionFourReplicasAndReplayReturnTheSameCommittedEventIdentity()
        {
            var initial = State("command-event-four-replicas", 4);
            const string commandId = "command-event-end-four";
            var results = new List<CommandResult>();
            var ruleEvents = new List<RuleEvent>();
            var randomStates = new List<string>();
            string originalRandom = JsonConvert.SerializeObject(initial.RandomRuntime);
            // 第五份从同一输入重放；不从已执行状态重复提交同一命令。
            for (int i = 0; i < 5; i++)
            {
                var session = new GameSession(GameStateCloneService.DeepClone(initial));
                session.RegisterHandler(new EndActionCommandHandler());
                var result = session.Submit(new GameCommand { CommandId = commandId, Kind = GameCommandKind.EndAction, PlayerId = 4 });
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                results.Add(result);
                ruleEvents.Add(session.State.EffectRuntime.RuleEvents.Single(e => e.EventType == GameEventKind.PlayerAdvanced.ToString()));
                randomStates.Add(JsonConvert.SerializeObject(session.State.RandomRuntime));
            }
            Evidence?.Invoke("四副本及重放原始结果", new { Results = results, RuleEvents = ruleEvents, RandomStates = randomStates });
            string expected = JsonConvert.SerializeObject(results[0]);
            foreach (var result in results) Assert.That(JsonConvert.SerializeObject(result), Is.EqualTo(expected));
            for (int i = 0; i < results.Count; i++)
            {
                Assert.That(results[i].Events, Has.Count.EqualTo(1));
                Assert.That(results[i].Events[0].EventId, Is.EqualTo(ruleEvents[i].EventId));
                Assert.That(ruleEvents[i].EventId, Is.EqualTo(ruleEvents[0].EventId));
                Assert.That(randomStates[i], Is.EqualTo(originalRandom));
            }
        }

        [Test]
        public void DifferentCommandsGamesAndEventOrdinalsDoNotShareIdentities()
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (string game in new[] { "identity-game-a", "identity-game-b" })
            {
                var session = new GameSession(State(game)); session.RegisterHandler(new EventHandler());
                foreach (string command in new[] { "identity-action-one", "identity-action-two" })
                {
                    var result = session.Submit(new GameCommand { CommandId = command, Kind = GameCommandKind.EndAction, PlayerId = 1 });
                    Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                    for (int i = 0; i < result.Events.Count; i++)
                    {
                        var e = result.Events[i];
                        string expected = StableIdFactory.Create("event", game, command, i.ToString(CultureInfo.InvariantCulture),
                            e.Kind.ToString(), e.SubjectId ?? "", e.PlayerId.ToString(CultureInfo.InvariantCulture));
                        Assert.That(e.EventId, Is.EqualTo(expected));
                        Assert.That(identities.Add(e.EventId), Is.True, "跨局/命令/事件序号身份碰撞");
                    }
                }
            }
            Evidence?.Invoke("不同局命令序号的身份", identities.OrderBy(id => id).ToArray());
            Assert.That(identities, Has.Count.EqualTo(8));
        }

        [Test]
        public void ResultEventOrderPayloadAndCommittedRuleEventCorrespondenceArePreserved()
        {
            var session = new GameSession(State("identity-payload")); session.RegisterHandler(new EventHandler());
            string random = JsonConvert.SerializeObject(session.State.RandomRuntime);
            var result = session.Submit(new GameCommand { CommandId = "identity-payload-command", Kind = GameCommandKind.EndAction, PlayerId = 1 });
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(result.Events, Has.Count.EqualTo(2));
            for (int i = 0; i < 2; i++)
            {
                var e = result.Events[i];
                Assert.That(e.Kind, Is.EqualTo(GameEventKind.PlayerAdvanced));
                Assert.That(e.PlayerId, Is.EqualTo(1)); Assert.That(e.SubjectId, Is.EqualTo("同一对象"));
                Assert.That(e.Message, Is.EqualTo("身份夹具事件" + i));
                Assert.That(e.Data, Is.EquivalentTo(new Dictionary<string, string> { { "序号", i.ToString(CultureInfo.InvariantCulture) }, { "原始值", "保留" } }));
                var recorded = session.State.EffectRuntime.RuleEvents.Single(r => r.EventId == e.EventId);
                Assert.That(recorded.EventType, Is.EqualTo(e.Kind.ToString()));
                Assert.That(recorded.PlayerId, Is.EqualTo(e.PlayerId)); Assert.That(recorded.TargetEntityId, Is.EqualTo(e.SubjectId));
                foreach (var pair in e.Data) Assert.That(recorded.Payload.Properties.Single(p => p.Name == pair.Key).Value.StringValue, Is.EqualTo(pair.Value));
                Assert.That(session.State.EffectRuntime.Journal.Count(j => j.Kind == RuleJournalEntryKind.RuleEvent && j.EntityId == e.EventId), Is.EqualTo(1));
            }
            Assert.That(result.LogMessage, Is.EqualTo("身份夹具提交"));
            Assert.That(JsonConvert.SerializeObject(session.State.RandomRuntime), Is.EqualTo(random));
            Evidence?.Invoke("结果与规则事件及journal", new { Result = result, Events = session.State.EffectRuntime.RuleEvents, Journal = session.State.EffectRuntime.Journal });
        }

        [Test]
        public void StoredLegacyResultEventIdsAreReadBackWithoutRegeneration()
        {
            const string oldId = "41850995f3714a669afe68994ad2e053";
            var old = new GameSessionLogContinuationDto { InitialResult = new GameSessionCommandResultDto {
                Succeeded = true, IsValid = true, ErrorCode = CommandErrorCode.None,
                Events = new List<GameSessionEventDto> { new GameSessionEventDto { EventId = oldId, Kind = GameEventKind.PlayerAdvanced, PlayerId = 4,
                    SubjectId = "原对象", Message = "原存档消息", Data = new List<GameCommandParameterDto> { new GameCommandParameterDto { Key = "原键", Value = "原值" } } } } } };
            string json = SimulationConfirmedRecoveryInstaller.SerializeContinuation(old);
            var loaded = SimulationConfirmedRecoveryInstaller.ReadContinuation(json);
            var restore = typeof(GameSession).GetMethod("RestoreResult", BindingFlags.Static | BindingFlags.NonPublic);
            var restored = (CommandResult)restore.Invoke(null, new object[] { loaded.InitialResult });
            Assert.That(restored.Events.Single().EventId, Is.EqualTo(oldId));
            var capture = typeof(GameSession).GetMethod("CaptureResult", BindingFlags.Static | BindingFlags.NonPublic);
            var captured = (GameSessionCommandResultDto)capture.Invoke(null, new object[] { restored });
            Assert.That(JsonConvert.SerializeObject(captured), Is.EqualTo(JsonConvert.SerializeObject(loaded.InitialResult)));
            var eventJson = JsonConvert.SerializeObject(restored.Events.Single());
            Assert.That(JsonConvert.DeserializeObject<GameEvent>(eventJson).EventId, Is.EqualTo(oldId));
            Evidence?.Invoke("旧结果读回", new { Original = old.InitialResult, ReadBack = captured, EventJson = eventJson });
        }

        private sealed class EventHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.EndAction;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                return CommandResult.SuccessResult(Enumerable.Range(0, 2).Select(i => new GameEvent {
                    Kind = GameEventKind.PlayerAdvanced, PlayerId = command.PlayerId, SubjectId = "同一对象", Message = "身份夹具事件" + i,
                    Data = new Dictionary<string, string> { { "序号", i.ToString(CultureInfo.InvariantCulture) }, { "原始值", "保留" } } }).ToList(), "身份夹具提交");
            }
        }
    }
}
