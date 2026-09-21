using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class NMC003RuntimeStateEditModeTests
    {
        [Test]
        public void StableId_UsesFullSha256AndDistinguishesNullFromEmpty()
        {
            string id = StableIdFactory.Create("event", "game-1", null, string.Empty, "round_started");
            string hash = StableIdFactory.ComputeSha256(
                StableIdFactory.EncodeCanonicalInput(
                    "event",
                    new[] { "game-1", null, string.Empty, "round_started" }));

            Assert.That(id, Does.Match("^event_[0-9a-f]{64}$"));
            Assert.That(id, Is.EqualTo("event_" + hash));
            Assert.That(
                StableIdFactory.Create("event", "game-1", null),
                Is.Not.EqualTo(StableIdFactory.Create("event", "game-1", string.Empty)));
        }

        [Test]
        public void NormalizedObject_IsSortedAndHasDeterministicRepresentation()
        {
            var first = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "z", Value = NormalizedValue.CreateInteger(2) },
                new NormalizedValueEntry { Name = "a", Value = NormalizedValue.CreateString("值") }
            });
            var second = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "a", Value = NormalizedValue.CreateString("值") },
                new NormalizedValueEntry { Name = "z", Value = NormalizedValue.CreateInteger(2) }
            });

            Assert.That(first.IsValid(), Is.True);
            Assert.That(first.ToDeterministicString(), Is.EqualTo(second.ToDeterministicString()));
            Assert.That(first.ToDeterministicString(), Is.EqualTo("O2{K1:aS3:值K1:zI2;}"));
        }

        [TestCaseSource(nameof(InvalidNormalizedValues))]
        public void InvalidNormalizedValue_IsRejected(NormalizedValue value, string description)
        {
            string reason;
            Assert.That(value.TryValidate(out reason), Is.False, description);
            Assert.That(reason, Is.Not.Empty, description);
        }

        [Test]
        public void RuntimeState_JsonRoundTrip_PreservesEventsNodesAndCommitJournal()
        {
            GameState state = CreateRuntimeState();
            RuleCommit commit = RuleCommit.Apply(
                state,
                "command-roundtrip",
                new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.EffectNode,
                    EntityId = "effect-1",
                    Payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                    {
                        new NormalizedValueEntry
                        {
                            Name = "amount",
                            Value = NormalizedValue.CreateInteger(3)
                        }
                    })
                },
                new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.RuleEvent,
                    EntityId = "event-1",
                    Payload = NormalizedValue.CreateStableReference("location", "D-01")
                });

            state.EffectRuntime.EffectNodes[0].LastCommitSequence = commit.LastCommitSequence;
            state.EffectRuntime.RuleEvents[0].StateRevision = commit.StateRevision;
            state.EffectRuntime.RuleEvents[0].CommitSequence = commit.LastCommitSequence;

            GameState roundTrip = JsonUtility.FromJson<GameState>(JsonUtility.ToJson(state));

            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip.EffectRuntime, Is.Not.Null);
            Assert.That(GameStateCloneService.AreEquivalent(state, roundTrip), Is.True);
            Assert.That(roundTrip.EffectRuntime.StateRevision, Is.EqualTo(1));
            Assert.That(roundTrip.EffectRuntime.NextCommitSequence, Is.EqualTo(3));
            Assert.That(roundTrip.EffectRuntime.Journal, Has.Count.EqualTo(2));
        }

        [Test]
        public void DeepClone_CoversEveryGameStateFieldAndSeparatesNestedCollections()
        {
            GameState source = CreateRichState();
            GameState clone = GameStateCloneService.DeepClone(source);

            Assert.That(clone, Is.Not.SameAs(source));
            Assert.That(GameStateCloneService.AreEquivalent(source, clone), Is.True);

            FieldInfo[] fields = typeof(GameState).GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                object sourceValue = fields[i].GetValue(source);
                object cloneValue = fields[i].GetValue(clone);
                if (sourceValue != null && !fields[i].FieldType.IsValueType && fields[i].FieldType != typeof(string))
                {
                    Assert.That(cloneValue, Is.Not.SameAs(sourceValue), fields[i].Name + " 未深克隆");
                }
            }

            clone.Players[0].Resources.Iron = 99;
            clone.Map.Influences[0].SlotId = "location:changed:0";
            clone.Decks.CardPools[0].RemainingCardIds.Add("card-3");
            clone.FinalScoring.PlayerScores[0].RemainingResources.GoldVoucher = 77;
            clone.EffectRuntime.EffectNodes[0].ChildEffectIds.Add("child-2");

            Assert.That(source.Players[0].Resources.Iron, Is.EqualTo(2));
            Assert.That(source.Map.Influences[0].SlotId, Is.EqualTo("location:A-01:0"));
            Assert.That(source.Decks.CardPools[0].RemainingCardIds, Is.EqualTo(new[] { "card-1", "card-2" }));
            Assert.That(source.FinalScoring.PlayerScores[0].RemainingResources.GoldVoucher, Is.EqualTo(4));
            Assert.That(source.EffectRuntime.EffectNodes[0].ChildEffectIds, Is.EqualTo(new[] { "child-1" }));
        }

        [Test]
        public void NetworkStateDtos_AreDetachedFromAuthoritativeSession()
        {
            GameState state = new GameState
            {
                GameId = "game-network-boundary",
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
            };
            var session = new GameSession(state);
            session.RegisterHandler(new MutatingHandler(true));
            var dispatcher = new AuthoritativeCommandDispatcher(session);
            InitialGameStateDto initial = dispatcher.CreateInitialStateSynchronization();

            initial.State.Players[0].Score = 99;
            Assert.That(session.State.Players[0].Score, Is.Zero);

            ConfirmedGameCommandDto accepted = null;
            dispatcher.CommandAccepted += dto => accepted = dto;
            CommandResult result = dispatcher.SubmitHostCommand(new GameCommandDto
            {
                CommandId = "command-network-boundary",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(accepted, Is.Not.Null);
            accepted.State.Players[0].Score = 101;
            Assert.That(session.State.Players[0].Score, Is.EqualTo(5));
        }

        [TestCase(true, 1)]
        [TestCase(false, 0)]
        public void Submit_UsesIsolatedWorkingCopyAndPublishesOnlySuccess(bool succeed, int expectedRevision)
        {
            GameState state = new GameState
            {
                GameId = "game-transaction",
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
            };
            string before = JsonUtility.ToJson(state);
            var handler = new MutatingHandler(succeed);
            var session = new GameSession(state);
            session.RegisterHandler(handler);

            CommandResult result = session.Submit(new GameCommand
            {
                CommandId = "command-transaction",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.EqualTo(succeed));
            Assert.That(handler.WorkingState, Is.Not.SameAs(state));
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(expectedRevision));
            if (succeed)
            {
                Assert.That(state.Players[0].Score, Is.EqualTo(5));
                Assert.That(state.EffectRuntime.RuleEvents, Has.Count.EqualTo(1));
                Assert.That(state.EffectRuntime.Journal, Has.Count.EqualTo(2));
                Assert.That(state.Logs, Has.Count.EqualTo(1));
                handler.WorkingState.Players[0].Score = 88;
                Assert.That(state.Players[0].Score, Is.EqualTo(5));
            }
            else
            {
                Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
                Assert.That(state.Logs, Is.Empty);
                Assert.That(state.EffectRuntime.Journal, Is.Empty);
            }
        }

        [Test]
        public void Submit_WhenHandlerThrows_DiscardsStateRevisionAndLog()
        {
            GameState state = new GameState
            {
                GameId = "game-throw",
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
            };
            string before = JsonUtility.ToJson(state);
            var session = new GameSession(state);
            session.RegisterHandler(new ThrowingHandler());

            CommandResult result = session.Submit(new GameCommand
            {
                CommandId = "command-throw",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(0));
            Assert.That(state.EffectRuntime.NextCommitSequence, Is.EqualTo(1));
            Assert.That(state.Logs, Is.Empty);
        }

        [Test]
        public void Submit_AllowsMultipleRuleCommitsInsideOneExternalCommand()
        {
            GameState state = new GameState { GameId = "game-multi-commit" };
            var session = new GameSession(state);
            session.RegisterHandler(new MultipleCommitHandler());

            CommandResult result = session.Submit(new GameCommand
            {
                CommandId = "command-multi-commit",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.NextCommitSequence, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.Journal, Has.Count.EqualTo(2));
            Assert.That(state.EffectRuntime.Journal[0].StateRevision, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Journal[1].StateRevision, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.Journal[0].CommitSequence, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Journal[1].CommitSequence, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.Journal[0].CommitId, Is.Not.EqualTo(state.EffectRuntime.Journal[1].CommitId));
        }

        [Test]
        public void Submit_DoesNotDuplicateExplicitRuleCommit()
        {
            GameState state = new GameState
            {
                GameId = "game-explicit-commit",
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
            };
            var session = new GameSession(state);
            session.RegisterHandler(new ExplicitCommitWithDomainMutationHandler());

            CommandResult result = session.Submit(new GameCommand
            {
                CommandId = "command-explicit-commit",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Players[0].Score, Is.EqualTo(7));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.NextCommitSequence, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.Journal, Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.Journal[0].EntityId, Is.EqualTo("score"));
        }

        [Test]
        public void Submit_FailureAfterMultipleRuleCommits_DoesNotConsumeRevisionOrSequence()
        {
            GameState state = new GameState { GameId = "game-failed-multi-commit" };
            var session = new GameSession(state);
            session.RegisterHandler(new FailedMultipleCommitHandler());

            CommandResult result = session.Submit(new GameCommand
            {
                CommandId = "command-failed-multi-commit",
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(0));
            Assert.That(state.EffectRuntime.NextCommitSequence, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Journal, Is.Empty);
        }

        private static IEnumerable<TestCaseData> InvalidNormalizedValues()
        {
            yield return new TestCaseData(
                new NormalizedValue { Kind = (NormalizedValueKind)999 },
                "未知 kind");
            yield return new TestCaseData(
                new NormalizedValue
                {
                    Kind = NormalizedValueKind.String,
                    StringValue = new string('x', NormalizedValue.MaxStringBytes + 1)
                },
                "超长字符串");
            yield return new TestCaseData(
                new NormalizedValue
                {
                    Kind = NormalizedValueKind.Integer,
                    IntegerValue = NormalizedValue.MaxInteger + 1
                },
                "超范围整数");
            yield return new TestCaseData(
                NormalizedValue.CreateArray(new List<NormalizedValue>
                {
                    NormalizedValue.CreateBoolean(true),
                    NormalizedValue.CreateInteger(1)
                }),
                "混合 kind 数组");
            yield return new TestCaseData(
                new NormalizedValue
                {
                    Kind = NormalizedValueKind.Object,
                    Properties = new List<NormalizedValueEntry>
                    {
                        new NormalizedValueEntry { Name = "z", Value = NormalizedValue.CreateNull() },
                        new NormalizedValueEntry { Name = "a", Value = NormalizedValue.CreateNull() }
                    }
                },
                "未排序对象");
        }

        private static GameState CreateRuntimeState()
        {
            GameState state = new GameState
            {
                GameId = "game-runtime",
                EffectRuntime = new EffectRuntimeState
                {
                    ActiveMainNodeId = "main-1",
                    EffectNodes = new List<EffectNodeRuntimeState>
                    {
                        new EffectNodeRuntimeState
                        {
                            EffectId = "effect-1",
                            EffectTypeId = "effect.demo",
                            Status = EffectNodeStatus.Running,
                            NormalizedArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>()),
                            ChildEffectIds = new List<string> { "child-1" }
                        }
                    },
                    MainNodes = new List<MainlineNodeRuntimeState>
                    {
                        new MainlineNodeRuntimeState { NodeId = "main-1", NodeTypeId = "round_started" }
                    },
                    RuleEvents = new List<RuleEvent>
                    {
                        new RuleEvent
                        {
                            EventId = "event-1",
                            EventType = "RoundStarted",
                            Payload = NormalizedValue.CreateStableReference("round", "round-1"),
                            ResponseKind = RuleEventResponseKind.Effects
                        }
                    },
                    InteractionRequests = new List<InteractionRequest>
                    {
                        new InteractionRequest
                        {
                            RequestId = "request-1",
                            InteractionTypeId = "interaction.choose",
                            OwnerEffectId = "effect-1",
                            CandidateIds = new List<string> { "option-1" },
                            NormalizedAnswer = NormalizedValue.CreateNull()
                        }
                    }
                }
            };
            // Unity JsonUtility 会把 null 的可序列化引用还原为默认对象；
            // round-trip 合同在这里显式覆盖这些可选 DTO 的完整形状。
            state.PendingChoice = new PendingChoiceState();
            state.PendingCardSession = new PendingCardSessionState();
            state.PendingCharacterEffect = new PendingCharacterEffectState();
            state.PendingSpecialAction = new PendingSpecialActionState();
            state.FinalScoring = new FinalScoringState();
            return state;
        }

        private static GameState CreateRichState()
        {
            GameState state = CreateRuntimeState();
            state.Phase = GamePhase.Cleanup;
            state.Round = 3;
            state.MaxRounds = 8;
            state.StartPlayerId = 1;
            state.LastFederalCouncilBuilderThisRoundPlayerId = 2;
            state.CurrentPlayerId = 2;
            state.ActionRound = 2;
            state.UseSeatTurnOrder = true;
            state.MapId = "map-4p";
            state.EventDeckSeed = 123;
            state.Players = new List<PlayerState>
            {
                new PlayerState
                {
                    PlayerId = 1,
                    Name = "玩家 1",
                    Color = PlayerColor.Blue,
                    Score = 6,
                    InfluenceSupply = 20,
                    HasScoreTrackMarker = true,
                    CityLocationId = "A-01",
                    HasMovedCityThisRound = true,
                    HasCollectedResourcesThisRound = true,
                    ResourceCollectionStartGoldVoucher = 4,
                    ActedMainActionThisTurn = true,
                    RemainingMainActionsThisTurn = 1,
                    CompletedMainActionsThisTurn = 2,
                    UsedCharacterThisRound = true,
                    UsedCharacterThisTurn = true,
                    CharacterCardLockedThisTurn = true,
                    Resources = new ResourceSet { Originium = 1, Iron = 2, GoldVoucher = 4 },
                    HandCardIds = new List<string> { "hand-1" },
                    DiscardCardIds = new List<string> { "discard-1" },
                    BuiltFacilityIds = new List<string> { "facility-1" },
                    DeclaredCityStyles = new List<CityStyleDeclarationState>
                    {
                        new CityStyleDeclarationState
                        {
                            InfluenceMarkerId = "marker-1",
                            CityStyleId = "style-1",
                            MarkerArea = "used",
                            UnlockedSpecialActionId = "action-1",
                            RemainingSpecialActionUses = 1,
                            UsedFacilityIds = new List<string> { "facility-1" },
                            UsedCityBoardSlotIndexes = new List<int> { 0 }
                        }
                    },
                    UsedSpecialActionIdsThisRound = new List<string> { "action-1" },
                    CoveredCharacterCardId = "character-1"
                }
            };
            state.Map = new MapRuntimeState
            {
                OpenLocationIds = new List<string> { "A-01" },
                RoadRouteIds = new List<string> { "route-1" },
                RemovedFromGameCardIds = new List<string> { "card-removed" },
                Influences = new List<InfluencePlacement>
                {
                    new InfluencePlacement { PlayerId = 1, SlotId = "location:A-01:0", LocationId = "A-01" }
                },
                Facilities = new List<FacilityPlacement>
                {
                    new FacilityPlacement { PlayerId = 1, FacilityCardId = "facility-1", LocationId = "A-01", CityBoardSlotIndex = 0 }
                },
                ResourceTokens = new List<ResourceTokenState>
                {
                    new ResourceTokenState { LocationId = "A-01", ResourceType = ResourceType.Iron, Amount = 2 }
                }
            };
            state.Decks = new DeckRuntimeState
            {
                CharacterDeck = new List<string> { "character-2" },
                CharacterDiscard = new List<string> { "character-3" },
                EventDeckGreen = new List<string> { "event-green" },
                EventDeckYellow = new List<string> { "event-yellow" },
                EventDeckRed = new List<string> { "event-red" },
                CardPools = new List<CardPoolState>
                {
                    new CardPoolState { PoolId = "pool-1", RemainingCardIds = new List<string> { "card-1", "card-2" } }
                },
                FacilityDeck = new List<string> { "facility-2" },
                FacilitySupply = new List<string> { "facility-3" },
                CityStyleSupply = new List<string> { "style-2" }
            };
            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = "choice-1",
                PlayerId = 1,
                ChoiceType = "choice",
                CardId = "card-1",
                TargetId = "target-1",
                OptionIds = new List<string> { "option-1" },
                SourceCommandId = "command-1"
            };
            state.PendingCardSession = new PendingCardSessionState
            {
                SessionId = "session-1",
                ScenarioId = "scenario-1",
                ChoiceType = "choice",
                PoolId = "pool-1",
                CardId = "card-1",
                PlayerId = 1,
                TargetId = "target-1",
                OptionIds = new List<string> { "option-1" },
                SourceCommandId = "command-1",
                ContextData = new List<StringKeyValuePair> { new StringKeyValuePair { Key = "key", Value = "value" } }
            };
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = "choice",
                PlayerId = 1,
                CardId = "character-1",
                SourceCommandId = "command-1",
                RemainingCardIds = new List<string> { "character-2" },
                ResolvedCardIds = new List<string> { "character-3" },
                OptionIds = new List<string> { "option-1" },
                RemainingEffectMode = "tactic"
            };
            state.PendingSpecialAction = new PendingSpecialActionState
            {
                SessionId = "special-session",
                PlayerId = 1,
                SpecialActionId = "special-action-test",
                DeclarationMarkerId = "marker-1",
                SourceCommandId = "command-1",
                Step = "await_target",
                RemainingRepetitions = 1,
                TraversedRouteId = "route-1",
                ResolvedTargetIds = new List<string> { "target-1" }
            };
            state.DelayedCharacterEffects = new List<DelayedCharacterEffectState>
            {
                new DelayedCharacterEffectState { EffectType = "remove_influence", PlayerId = 1, CardId = "character-1" }
            };
            state.FinalScoring = new FinalScoringState
            {
                IsResolved = true,
                PlayerScores = new List<FinalPlayerScoreState>
                {
                    new FinalPlayerScoreState
                    {
                        PlayerId = 1,
                        BaseScore = 6,
                        TotalScore = 9,
                        RemainingResources = new ResourceSet { GoldVoucher = 4 },
                        ControlledRegionIds = new List<string> { "region-1" }
                    }
                },
                RegionScores = new List<FinalRegionScoreState>
                {
                    new FinalRegionScoreState
                    {
                        RegionId = "region-1",
                        ScoreValue = 3,
                        ControllerPlayerId = 1,
                        InfluenceCounts = new List<PlayerInfluenceCountState>
                        {
                            new PlayerInfluenceCountState { PlayerId = 1, Count = 2 }
                        }
                    }
                },
                WinnerPlayerIds = new List<int> { 1 },
                TiebreakSummary = "summary"
            };
            state.Logs = new List<GameLogEntry>
            {
                new GameLogEntry { Sequence = 1, CommandId = "command-1", PlayerId = 1, Message = "日志" }
            };
            return state;
        }

        private sealed class MutatingHandler : IGameCommandHandler
        {
            private readonly bool succeed;

            public MutatingHandler(bool succeed)
            {
                this.succeed = succeed;
            }

            public GameState WorkingState { get; private set; }

            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                WorkingState = state;
                state.Players[0].Score = 5;
                state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = "location:B-01:0" });
                if (!succeed)
                {
                    return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "故意失败"));
                }

                return CommandResult.SuccessResult(new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.ScoreChanged,
                        PlayerId = 1,
                        SubjectId = "score",
                        Data = { { "amount", "5" } }
                    }
                }, "已完成");
            }
        }

        private sealed class ThrowingHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Players.Add(new PlayerState { PlayerId = 9 });
                state.EffectRuntime.StateRevision = 99;
                throw new InvalidOperationException("test handler fault");
            }
        }

        private sealed class MultipleCommitHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                RuleCommit.Apply(state, command.CommandId, new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.DomainState,
                    EntityId = "step-1"
                });
                RuleCommit.Apply(state, command.CommandId, new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.DomainState,
                    EntityId = "step-2"
                });
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }
        }

        private sealed class ExplicitCommitWithDomainMutationHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Players[0].Score = 7;
                RuleCommit.Apply(state, command.CommandId, new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.DomainState,
                    EntityId = "score"
                });
                return CommandResult.SuccessResult(new List<GameEvent>(), string.Empty);
            }
        }

        private sealed class FailedMultipleCommitHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)
            {
                return command.Kind == GameCommandKind.EndAction;
            }

            public CommandResult Handle(GameState state, GameCommand command)
            {
                RuleCommit.Apply(state, command.CommandId);
                RuleCommit.Apply(state, command.CommandId);
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "事务失败"));
            }
        }
    }
}
