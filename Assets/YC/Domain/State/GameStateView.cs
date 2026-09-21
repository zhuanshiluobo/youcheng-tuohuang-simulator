using System;
using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    /// <summary>
    /// 网络和展示边界使用的观察者身份。Host 是唯一可以看到完整权威运行状态的观察者；
    /// Player 只能看到自己的私有数据，Spectator/Unknown 只能看到公开数据。
    /// </summary>
    public enum GameStateViewerRole
    {
        Host,
        Player,
        Spectator,
        Unknown
    }

    [Serializable]
    public struct GameStateViewer
    {
        public GameStateViewerRole Role;
        public int PlayerId;

        public static GameStateViewer Host
        {
            get { return new GameStateViewer { Role = GameStateViewerRole.Host, PlayerId = -1 }; }
        }

        public static GameStateViewer Player(int playerId)
        {
            return new GameStateViewer { Role = GameStateViewerRole.Player, PlayerId = playerId };
        }

        public static GameStateViewer Spectator
        {
            get { return new GameStateViewer { Role = GameStateViewerRole.Spectator, PlayerId = -1 }; }
        }

        public static GameStateViewer Unknown
        {
            get { return new GameStateViewer { Role = GameStateViewerRole.Unknown, PlayerId = -1 }; }
        }

        public bool IsHost
        {
            get { return Role == GameStateViewerRole.Host; }
        }
    }

    /// <summary>
    /// 所有可见性判断的唯一矩阵。投影前执行判断，网络层永远不会拿到完整 GameState。
    /// </summary>
    public static class GameStateVisibilityPolicy
    {
        public const string Public = "public";
        public const string Owner = "owner";
        public const string Spectator = "spectator";

        public static bool IsVisible(string visibility, int ownerPlayerId, GameStateViewer viewer)
        {
            if (viewer.IsHost) return true;

            string scope = visibility ?? string.Empty;
            if (string.Equals(scope, Public, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scope, Spectator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(scope, Owner, StringComparison.OrdinalIgnoreCase))
            {
                return viewer.Role == GameStateViewerRole.Player &&
                       ownerPlayerId >= 0 && ownerPlayerId == viewer.PlayerId;
            }

            const string playerPrefix = "player:";
            if (scope.StartsWith(playerPrefix, StringComparison.Ordinal))
            {
                int playerId;
                return viewer.Role == GameStateViewerRole.Player &&
                       int.TryParse(scope.Substring(playerPrefix.Length), out playerId) &&
                       playerId == viewer.PlayerId;
            }

            // 未声明的私有范围默认按拥有者处理；不能因为内容漏填 visibility 就公开。
            if (ownerPlayerId >= 0)
            {
                return viewer.Role == GameStateViewerRole.Player && ownerPlayerId == viewer.PlayerId;
            }

            return false;
        }
    }

    [Serializable]
    public sealed class GameStateView
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;
        public GameStateViewerRole ViewerRole = GameStateViewerRole.Unknown;
        public int ViewerPlayerId = -1;
        public string GameId = string.Empty;
        public string ContentPackHash = string.Empty;
        public GamePhase Phase = GamePhase.Setup;
        public int Round;
        public int MaxRounds;
        public int StartPlayerId = -1;
        public int CurrentPlayerId = -1;
        public int ActionRound;
        public string MapId = string.Empty;
        public int StateRevision;
        public string RuntimeStatus = "active";
        public string ActiveMainNodeId = string.Empty;
        public List<PlayerStateView> Players = new List<PlayerStateView>();
        public PublicMapStateView Map = new PublicMapStateView();
        public PublicDeckStateView Decks = new PublicDeckStateView();
        public FinalScoringView FinalScoring;
        public List<InteractionView> Interactions = new List<InteractionView>();
        public List<int> WaitingForPlayerIds = new List<int>();
        public List<EffectNodeView> Effects = new List<EffectNodeView>();
        public List<RuleEventView> Events = new List<RuleEventView>();
        public List<GameLogView> Logs = new List<GameLogView>();
        public string FaultCode = string.Empty;

        public bool IsPausedFault
        {
            get { return string.Equals(RuntimeStatus, "paused_fault", StringComparison.Ordinal); }
        }
    }

    [Serializable]
    public sealed class PlayerStateView
    {
        public int PlayerId;
        public string Name = string.Empty;
        public PlayerColor Color;
        public int Score;
        public int InfluenceSupply;
        public bool HasScoreTrackMarker;
        public string CityLocationId = string.Empty;
        public bool HasMovedCityThisRound;
        public bool HasCollectedResourcesThisRound;
        public bool ActedMainActionThisTurn;
        public int RemainingMainActionsThisTurn;
        public int CompletedMainActionsThisTurn;
        public bool UsedCharacterThisRound;
        public bool UsedCharacterThisTurn;
        public bool CharacterCardLockedThisTurn;
        public ResourceSet Resources;
        public int HandCardCount;
        public int DiscardCardCount;
        public bool HasCoveredCharacterCard;
        public bool PrivateCardIdsVisible;
        public List<string> HandCardIds = new List<string>();
        public List<string> DiscardCardIds = new List<string>();
        public string CoveredCharacterCardId = string.Empty;
        public List<string> BuiltFacilityIds = new List<string>();
        public List<string> DeclaredCityStyleIds = new List<string>();
    }

    [Serializable]
    public sealed class PublicMapStateView
    {
        public List<string> OpenLocationIds = new List<string>();
        public List<string> RoadRouteIds = new List<string>();
        public List<InfluencePlacementView> Influences = new List<InfluencePlacementView>();
        public List<FacilityPlacementView> Facilities = new List<FacilityPlacementView>();
        public List<FacilityInstanceView> FacilityInstances = new List<FacilityInstanceView>();
        public List<ResourceTokenView> ResourceTokens = new List<ResourceTokenView>();
    }

    [Serializable]
    public sealed class InfluencePlacementView
    {
        public string InfluenceId = string.Empty;
        public int PlayerId;
        public string SlotId = string.Empty;
        public string LocationId = string.Empty;
        public string RouteId = string.Empty;
        public RuleSubjectReference OwnerSubject = new RuleSubjectReference();
        public InfluenceSourceReference Source = new InfluenceSourceReference();
    }

    [Serializable]
    public sealed class FacilityPlacementView
    {
        public int PlayerId;
        public string FacilityCardId = string.Empty;
        public string ContentInstanceId = string.Empty;
        public string LocationId = string.Empty;
        public int CityBoardSlotIndex = -1;
    }

    [Serializable]
    public sealed class FacilityInstanceView
    {
        public string ContentInstanceId = string.Empty;
        public string FacilityCardId = string.Empty;
        public int OwnerPlayerId = -1;
        public int CityBoardSlotIndex = -1;
        public bool PrivateStateVisible;
        public int ActivationCount;
        public int LastActivatedRound = -1;
        public bool CleanupMarkerRegistered;
    }

    [Serializable]
    public sealed class ResourceTokenView
    {
        public string LocationId = string.Empty;
        public ResourceType ResourceType;
        public int Amount = 1;
    }

    [Serializable]
    public sealed class PublicDeckStateView
    {
        public int CharacterDeckCount;
        public int CharacterDiscardCount;
        public int EventDeckGreenCount;
        public int EventDeckYellowCount;
        public int EventDeckRedCount;
        public int FacilityDeckCount;
        public int FacilitySupplyCount;
        public int CityStyleSupplyCount;
        public List<string> PublicFacilitySupplyIds = new List<string>();
        public List<string> PublicCityStyleSupplyIds = new List<string>();
    }

    [Serializable]
    public sealed class FinalScoringView
    {
        public bool IsResolved;
        public List<FinalPlayerScoreView> PlayerScores = new List<FinalPlayerScoreView>();
        public List<FinalRegionScoreView> RegionScores = new List<FinalRegionScoreView>();
        public List<int> WinnerPlayerIds = new List<int>();
        public string TiebreakSummary = string.Empty;
    }

    [Serializable]
    public sealed class FinalPlayerScoreView
    {
        public int PlayerId;
        public int BaseScore;
        public int RegionScore;
        public int ResourceScore;
        public int FacilityScore;
        public int CityStyleScore;
        public int TotalScore;
        public int GoldVoucherTiebreaker;
        public int PureOriginiumTiebreaker;
        public ResourceSet RemainingResources;
        public List<string> ControlledRegionIds = new List<string>();
    }

    [Serializable]
    public sealed class FinalRegionScoreView
    {
        public string RegionId = string.Empty;
        public int ScoreValue;
        public int ControllerPlayerId = -1;
        public List<PlayerInfluenceCountView> InfluenceCounts = new List<PlayerInfluenceCountView>();
    }

    [Serializable]
    public sealed class PlayerInfluenceCountView
    {
        public int PlayerId;
        public int Count;
    }

    [Serializable]
    public sealed class InteractionView
    {
        public string InteractionId = string.Empty;
        public string SourceNodeId = string.Empty;
        public string Kind = string.Empty;
        public int AnsweringPlayerId = -1;
        public string Visibility = string.Empty;
        public string PromptKey = string.Empty;
        public NormalizedValue PromptParameters = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public string CandidateSetId = string.Empty;
        public int CandidateSetVersion;
        public List<string> CandidateIds = new List<string>();
        public int MinSelections;
        public int MaxSelections;
        public bool AllowDecline;
        public string AnswerSchema = string.Empty;
        public string Status = string.Empty;
        public int StateRevision;
        public NormalizedValue Answer;
    }

    [Serializable]
    public sealed class EffectNodeView
    {
        public string EffectId = string.Empty;
        public string ParentEffectId = string.Empty;
        public string EffectTypeId = string.Empty;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public EffectNodeStatus Status = EffectNodeStatus.Created;
        public string FlowStage = string.Empty;
        public NormalizedValue NormalizedArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public NormalizedValue NormalizedResult = NormalizedValue.CreateNull();
        public List<string> ChildEffectIds = new List<string>();
        public List<string> BlockerIds = new List<string>();
    }

    [Serializable]
    public sealed class RuleEventView
    {
        public string EventId = string.Empty;
        public string EventType = string.Empty;
        public string TargetEntityId = string.Empty;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public NormalizedValue Payload = NormalizedValue.CreateNull();
        public NormalizedValue HostOnlyPayload = NormalizedValue.CreateNull();
        public RuleEventResponseKind ResponseKind = RuleEventResponseKind.None;
        public int StateRevision;
    }

    [Serializable]
    public sealed class GameLogView
    {
        public int Sequence;
        public int PlayerId = -1;
        public string Visibility = string.Empty;
        public string Message = string.Empty;
    }

    /// <summary>
    /// 从 Host 权威状态构造 view。此类不提供反向还原完整 GameState 的能力；客户端只能
    /// 得到一个丢弃了 EffectRuntime、receipt、隐藏候选和他人私有牌正文的兼容状态。
    /// </summary>
    public static class GameStateViewProjector
    {
        public static GameStateView ProjectForHost(GameState state)
        {
            return Project(state, GameStateViewer.Host);
        }

        public static GameStateView ProjectForPlayer(GameState state, int playerId)
        {
            return Project(state, GameStateViewer.Player(playerId));
        }

        public static GameStateView ProjectForSpectator(GameState state)
        {
            return Project(state, GameStateViewer.Spectator);
        }

        public static GameStateView ProjectForUnknown(GameState state)
        {
            return Project(state, GameStateViewer.Unknown);
        }

        public static GameStateView Project(GameState state, GameStateViewer viewer)
        {
            var view = new GameStateView
            {
                ViewerRole = viewer.Role,
                ViewerPlayerId = viewer.Role == GameStateViewerRole.Player ? viewer.PlayerId : -1,
                GameId = state == null ? string.Empty : state.GameId ?? string.Empty,
                ContentPackHash = state == null ? string.Empty : state.ContentPackHash ?? string.Empty,
                Phase = state == null ? GamePhase.Setup : state.Phase,
                Round = state == null ? 0 : state.Round,
                MaxRounds = state == null ? 0 : state.MaxRounds,
                StartPlayerId = state == null ? -1 : state.StartPlayerId,
                CurrentPlayerId = state == null ? -1 : state.CurrentPlayerId,
                ActionRound = state == null ? 0 : state.ActionRound,
                MapId = state == null ? string.Empty : state.MapId ?? string.Empty
            };

            if (state == null) return view;

            EffectRuntimeState runtime = state.EffectRuntime;
            view.StateRevision = runtime == null ? 0 : runtime.StateRevision;
            if (runtime != null)
            {
                view.RuntimeStatus = runtime.Status == EffectRuntimeStatus.PausedFault ? "paused_fault" : "active";
                view.ActiveMainNodeId = viewer.IsHost ? runtime.ActiveMainNodeId ?? string.Empty : string.Empty;
                view.FaultCode = viewer.IsHost && runtime.Status == EffectRuntimeStatus.PausedFault
                    ? runtime.LastFaultCode ?? string.Empty
                    : string.Empty;
            }

            ProjectPlayers(state, viewer, view.Players);
            ProjectMap(state.Map, view.Map, viewer);
            ProjectDecks(state.Decks, view.Decks);
            ProjectFinalScoring(state.FinalScoring, viewer, view);
            ProjectInteractions(state, viewer, view);
            ProjectEffects(runtime, viewer, view.Effects);
            ProjectEvents(runtime, viewer, view.Events);
            ProjectLogs(state.Logs, viewer, view.Logs);
            return view;
        }

        /// <summary>
        /// 仅供兼容旧 Presenter 的公开状态适配器。它显式丢弃 EffectRuntime，而不是把
        /// view 当作完整状态反序列化，因此客户端不会拥有 Host receipt/节点/隐藏对象图。
        /// </summary>
        public static GameState ToClientState(GameStateView view)
        {
            var state = new GameState
            {
                GameId = view == null ? string.Empty : view.GameId ?? string.Empty,
                ContentPackHash = view == null ? string.Empty : view.ContentPackHash ?? string.Empty,
                Phase = view == null ? GamePhase.Setup : view.Phase,
                Round = view == null ? 0 : view.Round,
                MaxRounds = view == null ? 0 : view.MaxRounds,
                StartPlayerId = view == null ? -1 : view.StartPlayerId,
                CurrentPlayerId = view == null ? -1 : view.CurrentPlayerId,
                ActionRound = view == null ? 0 : view.ActionRound,
                MapId = view == null ? string.Empty : view.MapId ?? string.Empty,
                EffectRuntime = null
            };
            if (view == null) return state;

            for (int i = 0; i < view.Players.Count; i++)
            {
                PlayerStateView source = view.Players[i];
                if (source == null) continue;
                var player = new PlayerState
                {
                    PlayerId = source.PlayerId,
                    Name = source.Name ?? string.Empty,
                    Color = source.Color,
                    Score = source.Score,
                    InfluenceSupply = source.InfluenceSupply,
                    HasScoreTrackMarker = source.HasScoreTrackMarker,
                    CityLocationId = source.CityLocationId ?? string.Empty,
                    HasMovedCityThisRound = source.HasMovedCityThisRound,
                    HasCollectedResourcesThisRound = source.HasCollectedResourcesThisRound,
                    ActedMainActionThisTurn = source.ActedMainActionThisTurn,
                    RemainingMainActionsThisTurn = source.RemainingMainActionsThisTurn,
                    CompletedMainActionsThisTurn = source.CompletedMainActionsThisTurn,
                    UsedCharacterThisRound = source.UsedCharacterThisRound,
                    UsedCharacterThisTurn = source.UsedCharacterThisTurn,
                    CharacterCardLockedThisTurn = source.CharacterCardLockedThisTurn,
                    Resources = source.Resources == null ? new ResourceSet() : source.Resources.Clone(),
                    BuiltFacilityIds = source.BuiltFacilityIds == null
                        ? new List<string>() : new List<string>(source.BuiltFacilityIds)
                };
                if (source.PrivateCardIdsVisible)
                {
                    if (source.HandCardIds != null) player.HandCardIds.AddRange(source.HandCardIds);
                    if (source.DiscardCardIds != null) player.DiscardCardIds.AddRange(source.DiscardCardIds);
                    player.CoveredCharacterCardId = source.CoveredCharacterCardId ?? string.Empty;
                }

                state.Players.Add(player);
            }

            if (view.Map != null)
            {
                state.Map.OpenLocationIds.AddRange(view.Map.OpenLocationIds ?? new List<string>());
                state.Map.RoadRouteIds.AddRange(view.Map.RoadRouteIds ?? new List<string>());
                for (int i = 0; i < view.Map.Influences.Count; i++)
                {
                    InfluencePlacementView item = view.Map.Influences[i];
                    if (item == null) continue;
                    state.Map.Influences.Add(new InfluencePlacement
                    {
                        InfluenceId = item.InfluenceId ?? string.Empty,
                        PlayerId = item.PlayerId, SlotId = item.SlotId ?? string.Empty,
                        LocationId = item.LocationId ?? string.Empty, RouteId = item.RouteId ?? string.Empty,
                        OwnerSubject = item.OwnerSubject == null ? new RuleSubjectReference() : new RuleSubjectReference
                        {
                            SubjectType = item.OwnerSubject.SubjectType ?? string.Empty,
                            InstanceId = item.OwnerSubject.InstanceId ?? string.Empty,
                            DefinitionId = item.OwnerSubject.DefinitionId ?? string.Empty,
                            PlayerId = item.OwnerSubject.PlayerId
                        },
                        Source = InfluenceIdentity.CloneSource(item.Source)
                    });
                }
                for (int i = 0; i < view.Map.Facilities.Count; i++)
                {
                    FacilityPlacementView item = view.Map.Facilities[i];
                    if (item == null) continue;
                    state.Map.Facilities.Add(new FacilityPlacement
                    {
                        PlayerId = item.PlayerId, FacilityCardId = item.FacilityCardId ?? string.Empty,
                        ContentInstanceId = item.ContentInstanceId ?? string.Empty,
                        LocationId = item.LocationId ?? string.Empty, CityBoardSlotIndex = item.CityBoardSlotIndex
                    });
                }
                for (int i = 0; i < view.Map.FacilityInstances.Count; i++)
                {
                    FacilityInstanceView item = view.Map.FacilityInstances[i];
                    if (item == null) continue;
                    state.Map.FacilityInstances.Add(new FacilityInstanceRuntimeState
                    {
                        ContentInstanceId = item.ContentInstanceId ?? string.Empty,
                        FacilityCardId = item.FacilityCardId ?? string.Empty,
                        OwnerPlayerId = item.OwnerPlayerId,
                        CityBoardSlotIndex = item.CityBoardSlotIndex,
                        ActivationCount = item.ActivationCount,
                        LastActivatedRound = item.LastActivatedRound,
                        CleanupMarkerRegistered = item.CleanupMarkerRegistered
                    });
                }
                for (int i = 0; i < view.Map.ResourceTokens.Count; i++)
                {
                    ResourceTokenView item = view.Map.ResourceTokens[i];
                    if (item == null) continue;
                    state.Map.ResourceTokens.Add(new ResourceTokenState
                    {
                        LocationId = item.LocationId ?? string.Empty, ResourceType = item.ResourceType, Amount = item.Amount
                    });
                }
            }

            if (view.Decks != null)
            {
                state.Decks.FacilitySupply.AddRange(view.Decks.PublicFacilitySupplyIds ?? new List<string>());
                state.Decks.CityStyleSupply.AddRange(view.Decks.PublicCityStyleSupplyIds ?? new List<string>());
            }
            if (view.FinalScoring != null)
            {
                state.FinalScoring = new FinalScoringState
                {
                    IsResolved = view.FinalScoring.IsResolved,
                    TiebreakSummary = view.FinalScoring.TiebreakSummary ?? string.Empty,
                    WinnerPlayerIds = view.FinalScoring.WinnerPlayerIds == null
                        ? new List<int>() : new List<int>(view.FinalScoring.WinnerPlayerIds)
                };
                if (view.FinalScoring.PlayerScores != null)
                {
                    for (int i = 0; i < view.FinalScoring.PlayerScores.Count; i++)
                    {
                        FinalPlayerScoreView source = view.FinalScoring.PlayerScores[i];
                        if (source == null) continue;
                        state.FinalScoring.PlayerScores.Add(new FinalPlayerScoreState
                        {
                            PlayerId = source.PlayerId,
                            BaseScore = source.BaseScore,
                            RegionScore = source.RegionScore,
                            ResourceScore = source.ResourceScore,
                            FacilityScore = source.FacilityScore,
                            CityStyleScore = source.CityStyleScore,
                            TotalScore = source.TotalScore,
                            GoldVoucherTiebreaker = source.GoldVoucherTiebreaker,
                            PureOriginiumTiebreaker = source.PureOriginiumTiebreaker,
                            RemainingResources = source.RemainingResources == null
                                ? new ResourceSet() : source.RemainingResources.Clone(),
                            ControlledRegionIds = source.ControlledRegionIds == null
                                ? new List<string>() : new List<string>(source.ControlledRegionIds)
                        });
                    }
                }
                if (view.FinalScoring.RegionScores != null)
                {
                    for (int i = 0; i < view.FinalScoring.RegionScores.Count; i++)
                    {
                        FinalRegionScoreView source = view.FinalScoring.RegionScores[i];
                        if (source == null) continue;
                        var region = new FinalRegionScoreState
                        {
                            RegionId = source.RegionId ?? string.Empty,
                            ScoreValue = source.ScoreValue,
                            ControllerPlayerId = source.ControllerPlayerId
                        };
                        if (source.InfluenceCounts != null)
                        {
                            for (int j = 0; j < source.InfluenceCounts.Count; j++)
                            {
                                PlayerInfluenceCountView count = source.InfluenceCounts[j];
                                if (count != null)
                                {
                                    region.InfluenceCounts.Add(new PlayerInfluenceCountState
                                    {
                                        PlayerId = count.PlayerId, Count = count.Count
                                    });
                                }
                            }
                        }
                        state.FinalScoring.RegionScores.Add(region);
                    }
                }
            }
            for (int i = 0; i < view.Logs.Count; i++)
            {
                GameLogView log = view.Logs[i];
                if (log == null) continue;
                state.Logs.Add(new GameLogEntry
                {
                    Sequence = log.Sequence,
                    PlayerId = log.PlayerId,
                    Visibility = GameStateVisibilityPolicy.Public,
                    Message = log.Message ?? string.Empty
                });
            }

            return state;
        }

        private static void ProjectPlayers(GameState state, GameStateViewer viewer, List<PlayerStateView> destination)
        {
            if (state.Players == null) return;
            for (int i = 0; i < state.Players.Count; i++)
            {
                PlayerState source = state.Players[i];
                if (source == null) continue;
                bool privateVisible = viewer.IsHost ||
                    (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == source.PlayerId);
                var target = new PlayerStateView
                {
                    PlayerId = source.PlayerId,
                    Name = source.Name ?? string.Empty,
                    Color = source.Color,
                    Score = source.Score,
                    InfluenceSupply = source.InfluenceSupply,
                    HasScoreTrackMarker = source.HasScoreTrackMarker,
                    CityLocationId = source.CityLocationId ?? string.Empty,
                    HasMovedCityThisRound = source.HasMovedCityThisRound,
                    HasCollectedResourcesThisRound = source.HasCollectedResourcesThisRound,
                    ActedMainActionThisTurn = source.ActedMainActionThisTurn,
                    RemainingMainActionsThisTurn = source.RemainingMainActionsThisTurn,
                    CompletedMainActionsThisTurn = source.CompletedMainActionsThisTurn,
                    UsedCharacterThisRound = source.UsedCharacterThisRound,
                    UsedCharacterThisTurn = source.UsedCharacterThisTurn,
                    CharacterCardLockedThisTurn = source.CharacterCardLockedThisTurn,
                    Resources = privateVisible && source.Resources != null ? source.Resources.Clone() : null,
                    HandCardCount = source.HandCardIds == null ? 0 : source.HandCardIds.Count,
                    DiscardCardCount = source.DiscardCardIds == null ? 0 : source.DiscardCardIds.Count,
                    HasCoveredCharacterCard = !string.IsNullOrEmpty(source.CoveredCharacterCardId),
                    PrivateCardIdsVisible = privateVisible,
                    BuiltFacilityIds = source.BuiltFacilityIds == null
                        ? new List<string>() : new List<string>(source.BuiltFacilityIds)
                };
                if (source.DeclaredCityStyles != null)
                {
                    for (int j = 0; j < source.DeclaredCityStyles.Count; j++)
                    {
                        CityStyleDeclarationState declaration = source.DeclaredCityStyles[j];
                        if (declaration != null && !string.IsNullOrEmpty(declaration.CityStyleId))
                            target.DeclaredCityStyleIds.Add(declaration.CityStyleId);
                    }
                }
                if (privateVisible)
                {
                    if (source.HandCardIds != null) target.HandCardIds.AddRange(source.HandCardIds);
                    if (source.DiscardCardIds != null) target.DiscardCardIds.AddRange(source.DiscardCardIds);
                    target.CoveredCharacterCardId = source.CoveredCharacterCardId ?? string.Empty;
                }
                destination.Add(target);
            }
        }

        private static void ProjectMap(MapRuntimeState source, PublicMapStateView target, GameStateViewer viewer)
        {
            if (source == null || target == null) return;
            if (source.OpenLocationIds != null) target.OpenLocationIds.AddRange(source.OpenLocationIds);
            if (source.RoadRouteIds != null) target.RoadRouteIds.AddRange(source.RoadRouteIds);
            if (source.Influences != null)
            {
                for (int i = 0; i < source.Influences.Count; i++)
                {
                    InfluencePlacement item = source.Influences[i];
                    if (item == null) continue;
                    target.Influences.Add(new InfluencePlacementView
                    {
                        InfluenceId = item.InfluenceId ?? string.Empty,
                        PlayerId = item.PlayerId, SlotId = item.SlotId ?? string.Empty,
                        LocationId = item.LocationId ?? string.Empty, RouteId = item.RouteId ?? string.Empty,
                        OwnerSubject = InfluenceIdentity.CloneSubject(item.OwnerSubject),
                        Source = InfluenceIdentity.CloneSource(item.Source)
                    });
                }
            }
            if (source.Facilities != null)
            {
                for (int i = 0; i < source.Facilities.Count; i++)
                {
                    FacilityPlacement item = source.Facilities[i];
                    if (item == null) continue;
                    target.Facilities.Add(new FacilityPlacementView
                    {
                        PlayerId = item.PlayerId, FacilityCardId = item.FacilityCardId ?? string.Empty,
                        ContentInstanceId = item.ContentInstanceId ?? string.Empty,
                        LocationId = item.LocationId ?? string.Empty, CityBoardSlotIndex = item.CityBoardSlotIndex
                    });
                }
            }
            if (source.FacilityInstances != null)
            {
                for (int i = 0; i < source.FacilityInstances.Count; i++)
                {
                    FacilityInstanceRuntimeState item = source.FacilityInstances[i];
                    if (item == null) continue;
                    bool privateVisible = viewer.IsHost ||
                        (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == item.OwnerPlayerId);
                    target.FacilityInstances.Add(new FacilityInstanceView
                    {
                        ContentInstanceId = item.ContentInstanceId ?? string.Empty,
                        FacilityCardId = item.FacilityCardId ?? string.Empty,
                        OwnerPlayerId = item.OwnerPlayerId,
                        CityBoardSlotIndex = item.CityBoardSlotIndex,
                        PrivateStateVisible = privateVisible,
                        ActivationCount = privateVisible ? item.ActivationCount : 0,
                        LastActivatedRound = privateVisible ? item.LastActivatedRound : -1,
                        CleanupMarkerRegistered = privateVisible && item.CleanupMarkerRegistered
                    });
                }
            }
            if (source.ResourceTokens != null)
            {
                for (int i = 0; i < source.ResourceTokens.Count; i++)
                {
                    ResourceTokenState item = source.ResourceTokens[i];
                    if (item == null) continue;
                    target.ResourceTokens.Add(new ResourceTokenView
                    {
                        LocationId = item.LocationId ?? string.Empty, ResourceType = item.ResourceType, Amount = item.Amount
                    });
                }
            }
        }

        private static void ProjectDecks(DeckRuntimeState source, PublicDeckStateView target)
        {
            if (source == null || target == null) return;
            target.CharacterDeckCount = Count(source.CharacterDeck);
            target.CharacterDiscardCount = Count(source.CharacterDiscard);
            target.EventDeckGreenCount = Count(source.EventDeckGreen);
            target.EventDeckYellowCount = Count(source.EventDeckYellow);
            target.EventDeckRedCount = Count(source.EventDeckRed);
            target.FacilityDeckCount = Count(source.FacilityDeck);
            target.FacilitySupplyCount = Count(source.FacilitySupply);
            target.CityStyleSupplyCount = Count(source.CityStyleSupply);
            if (source.FacilitySupply != null) target.PublicFacilitySupplyIds.AddRange(source.FacilitySupply);
            if (source.CityStyleSupply != null) target.PublicCityStyleSupplyIds.AddRange(source.CityStyleSupply);
        }

        private static void ProjectFinalScoring(
            FinalScoringState source,
            GameStateViewer viewer,
            GameStateView target)
        {
            if (source == null || target == null) return;
            target.FinalScoring = new FinalScoringView
            {
                IsResolved = source.IsResolved,
                TiebreakSummary = source.TiebreakSummary ?? string.Empty,
                WinnerPlayerIds = source.WinnerPlayerIds == null
                    ? new List<int>() : new List<int>(source.WinnerPlayerIds)
            };
            if (source.PlayerScores != null)
            {
                for (int i = 0; i < source.PlayerScores.Count; i++)
                {
                    FinalPlayerScoreState score = source.PlayerScores[i];
                    if (score == null) continue;
                    bool privateVisible = viewer.IsHost ||
                        (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == score.PlayerId);
                    target.FinalScoring.PlayerScores.Add(new FinalPlayerScoreView
                    {
                        PlayerId = score.PlayerId,
                        BaseScore = score.BaseScore,
                        RegionScore = score.RegionScore,
                        ResourceScore = score.ResourceScore,
                        FacilityScore = score.FacilityScore,
                        CityStyleScore = score.CityStyleScore,
                        TotalScore = score.TotalScore,
                        GoldVoucherTiebreaker = score.GoldVoucherTiebreaker,
                        PureOriginiumTiebreaker = score.PureOriginiumTiebreaker,
                        RemainingResources = privateVisible && score.RemainingResources != null
                            ? score.RemainingResources.Clone() : null,
                        ControlledRegionIds = score.ControlledRegionIds == null
                            ? new List<string>() : new List<string>(score.ControlledRegionIds)
                    });
                }
            }
            if (source.RegionScores != null)
            {
                for (int i = 0; i < source.RegionScores.Count; i++)
                {
                    FinalRegionScoreState region = source.RegionScores[i];
                    if (region == null) continue;
                    var projected = new FinalRegionScoreView
                    {
                        RegionId = region.RegionId ?? string.Empty,
                        ScoreValue = region.ScoreValue,
                        ControllerPlayerId = region.ControllerPlayerId
                    };
                    if (region.InfluenceCounts != null)
                    {
                        for (int j = 0; j < region.InfluenceCounts.Count; j++)
                        {
                            PlayerInfluenceCountState count = region.InfluenceCounts[j];
                            if (count != null)
                            {
                                projected.InfluenceCounts.Add(new PlayerInfluenceCountView
                                {
                                    PlayerId = count.PlayerId, Count = count.Count
                                });
                            }
                        }
                    }
                    target.FinalScoring.RegionScores.Add(projected);
                }
            }
        }

        private static void ProjectInteractions(GameState state, GameStateViewer viewer, GameStateView target)
        {
            EffectRuntimeState runtime = state.EffectRuntime;
            if (runtime != null && runtime.InteractionRequests != null)
            {
                for (int i = 0; i < runtime.InteractionRequests.Count; i++)
                {
                    InteractionRequest request = runtime.InteractionRequests[i];
                    if (request == null) continue;
                    if (GameStateVisibilityPolicy.IsVisible(request.Visibility, request.AnsweringPlayerId, viewer))
                    {
                        target.Interactions.Add(ProjectInteraction(request, viewer.IsHost ||
                            viewer.PlayerId == request.AnsweringPlayerId));
                    }
                    else if (request.Status == "open" && request.AnsweringPlayerId >= 0 &&
                             !target.WaitingForPlayerIds.Contains(request.AnsweringPlayerId))
                    {
                        target.WaitingForPlayerIds.Add(request.AnsweringPlayerId);
                    }
                }
            }

            // 兼容仍未迁移到统一 Interaction 的旧 pending 字段；只投影给其目标玩家。
            if (state.PendingChoice != null && state.PendingChoice.IsValid())
            {
                ProjectLegacyChoice(state.PendingChoice, viewer, target);
            }
            if (state.PendingCardSession != null && state.PendingCardSession.IsValid())
            {
                ProjectLegacyCardSession(state.PendingCardSession, viewer, target);
            }
            if (state.PendingCharacterEffect != null && state.PendingCharacterEffect.IsValid())
            {
                if (viewer.IsHost || viewer.PlayerId == state.PendingCharacterEffect.PlayerId)
                {
                    var pending = state.PendingCharacterEffect;
                    target.Interactions.Add(new InteractionView
                    {
                        InteractionId = StableIdFactory.Create("legacy-interaction", pending.SourceCommandId ?? string.Empty,
                            pending.ChoiceType ?? string.Empty, pending.CardId ?? string.Empty),
                        Kind = pending.ChoiceType ?? string.Empty,
                        AnsweringPlayerId = pending.PlayerId,
                        Visibility = GameStateVisibilityPolicy.Owner,
                        CandidateIds = pending.OptionIds == null ? new List<string>() : new List<string>(pending.OptionIds),
                        MinSelections = 1,
                        MaxSelections = 1,
                        Status = "open"
                    });
                }
                else if (!target.WaitingForPlayerIds.Contains(state.PendingCharacterEffect.PlayerId))
                {
                    target.WaitingForPlayerIds.Add(state.PendingCharacterEffect.PlayerId);
                }
            }
        }

        private static InteractionView ProjectInteraction(InteractionRequest request, bool includeAnswer)
        {
            return new InteractionView
            {
                InteractionId = request.GetStableInteractionId(),
                SourceNodeId = request.SourceNodeId ?? string.Empty,
                Kind = request.InteractionTypeId ?? string.Empty,
                AnsweringPlayerId = request.AnsweringPlayerId,
                Visibility = request.Visibility ?? string.Empty,
                PromptKey = request.PromptKey ?? string.Empty,
                PromptParameters = request.PromptParameters == null ? NormalizedValue.CreateObject(new List<NormalizedValueEntry>()) : request.PromptParameters.Clone(),
                CandidateSetId = request.CandidateSetId ?? string.Empty,
                CandidateSetVersion = request.CandidateSetVersion,
                CandidateIds = request.CandidateIds == null ? new List<string>() : new List<string>(request.CandidateIds),
                MinSelections = request.MinSelections,
                MaxSelections = request.MaxSelections,
                AllowDecline = request.AllowDecline,
                AnswerSchema = request.AnswerSchema ?? string.Empty,
                Status = request.Status ?? string.Empty,
                StateRevision = request.StateRevision,
                Answer = includeAnswer && request.NormalizedAnswer != null ? request.NormalizedAnswer.Clone() : null
            };
        }

        private static void ProjectLegacyChoice(PendingChoiceState source, GameStateViewer viewer, GameStateView target)
        {
            if (viewer.IsHost || viewer.PlayerId == source.PlayerId)
            {
                target.Interactions.Add(new InteractionView
                {
                    InteractionId = string.IsNullOrEmpty(source.ChoiceId) ? source.SourceCommandId ?? string.Empty : source.ChoiceId,
                    Kind = source.ChoiceType ?? string.Empty,
                    AnsweringPlayerId = source.PlayerId,
                    Visibility = GameStateVisibilityPolicy.Owner,
                    CandidateIds = source.OptionIds == null ? new List<string>() : new List<string>(source.OptionIds),
                    MinSelections = 1,
                    MaxSelections = 1,
                    Status = "open"
                });
            }
            else if (!target.WaitingForPlayerIds.Contains(source.PlayerId))
            {
                target.WaitingForPlayerIds.Add(source.PlayerId);
            }
        }

        private static void ProjectLegacyCardSession(PendingCardSessionState source, GameStateViewer viewer, GameStateView target)
        {
            if (viewer.IsHost || viewer.PlayerId == source.PlayerId)
            {
                target.Interactions.Add(new InteractionView
                {
                    InteractionId = source.SessionId ?? string.Empty,
                    Kind = source.ChoiceType ?? string.Empty,
                    AnsweringPlayerId = source.PlayerId,
                    Visibility = GameStateVisibilityPolicy.Owner,
                    CandidateIds = source.OptionIds == null ? new List<string>() : new List<string>(source.OptionIds),
                    MinSelections = 1,
                    MaxSelections = 1,
                    Status = "open"
                });
            }
            else if (!target.WaitingForPlayerIds.Contains(source.PlayerId))
            {
                target.WaitingForPlayerIds.Add(source.PlayerId);
            }
        }

        private static void ProjectEffects(EffectRuntimeState runtime, GameStateViewer viewer, List<EffectNodeView> destination)
        {
            if (runtime == null || runtime.EffectNodes == null) return;
            var visibleNodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState source = runtime.EffectNodes[i];
                if (source == null) continue;
                string visibility = string.IsNullOrEmpty(source.Visibility)
                    ? (source.PlayerId >= 0 ? GameStateVisibilityPolicy.Owner : GameStateVisibilityPolicy.Public)
                    : source.Visibility;
                if (GameStateVisibilityPolicy.IsVisible(visibility, source.PlayerId, viewer))
                    visibleNodeIds.Add(source.EffectId ?? string.Empty);
            }
            for (int i = 0; i < runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState source = runtime.EffectNodes[i];
                if (source == null) continue;
                string visibility = string.IsNullOrEmpty(source.Visibility)
                    ? (source.PlayerId >= 0 ? GameStateVisibilityPolicy.Owner : GameStateVisibilityPolicy.Public)
                    : source.Visibility;
                if (!GameStateVisibilityPolicy.IsVisible(visibility, source.PlayerId, viewer)) continue;
                destination.Add(new EffectNodeView
                {
                    EffectId = source.EffectId ?? string.Empty,
                    ParentEffectId = source.ParentEffectId ?? string.Empty,
                    EffectTypeId = source.EffectTypeId ?? string.Empty,
                    PlayerId = source.PlayerId,
                    Visibility = visibility,
                    Status = source.Status,
                    FlowStage = source.FlowStage ?? string.Empty,
                    NormalizedArguments = source.NormalizedArguments == null ? NormalizedValue.CreateNull() : source.NormalizedArguments.Clone(),
                    NormalizedResult = source.NormalizedResult == null ? NormalizedValue.CreateNull() : source.NormalizedResult.Clone(),
                    ChildEffectIds = source.ChildEffectIds == null ? new List<string>() : new List<string>(source.ChildEffectIds),
                    BlockerIds = source.BlockerIds == null ? new List<string>() : new List<string>(source.BlockerIds)
                });
                EffectNodeView projected = destination[destination.Count - 1];
                if (!visibleNodeIds.Contains(projected.ParentEffectId)) projected.ParentEffectId = string.Empty;
                for (int j = projected.ChildEffectIds.Count - 1; j >= 0; j--)
                {
                    if (!visibleNodeIds.Contains(projected.ChildEffectIds[j])) projected.ChildEffectIds.RemoveAt(j);
                }
            }
        }

        private static void ProjectEvents(EffectRuntimeState runtime, GameStateViewer viewer, List<RuleEventView> destination)
        {
            if (runtime == null || runtime.RuleEvents == null) return;
            for (int i = 0; i < runtime.RuleEvents.Count; i++)
            {
                RuleEvent source = runtime.RuleEvents[i];
                if (source == null || !GameStateVisibilityPolicy.IsVisible(source.Visibility, source.PlayerId, viewer)) continue;
                destination.Add(new RuleEventView
                {
                    EventId = source.EventId ?? string.Empty,
                    EventType = source.EventType ?? string.Empty,
                    TargetEntityId = source.TargetEntityId ?? string.Empty,
                    PlayerId = source.PlayerId,
                    Visibility = source.Visibility ?? string.Empty,
                    Payload = source.Payload == null ? NormalizedValue.CreateNull() : source.Payload.Clone(),
                    HostOnlyPayload = viewer.IsHost ||
                                      (viewer.Role == GameStateViewerRole.Player && viewer.PlayerId == source.PlayerId)
                        ? (source.HostOnlyPayload == null ? NormalizedValue.CreateNull() : source.HostOnlyPayload.Clone())
                        : NormalizedValue.CreateNull(),
                    ResponseKind = source.ResponseKind,
                    StateRevision = source.StateRevision
                });
            }
        }

        private static void ProjectLogs(List<GameLogEntry> source, GameStateViewer viewer, List<GameLogView> destination)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                GameLogEntry item = source[i];
                if (item == null || !GameStateVisibilityPolicy.IsVisible(item.Visibility, item.PlayerId, viewer)) continue;
                destination.Add(new GameLogView
                {
                    Sequence = item.Sequence,
                    PlayerId = item.PlayerId,
                    Visibility = item.Visibility ?? GameStateVisibilityPolicy.Public,
                    Message = item.Message ?? string.Empty
                });
            }
        }

        private static int Count<T>(List<T> values)
        {
            return values == null ? 0 : values.Count;
        }
    }

    /// <summary>兼容调用方使用 Factory 命名的薄入口；权限规则仍只有 Projector 一份。</summary>
    public static class GameStateViewFactory
    {
        public static GameStateView Create(GameState state, GameStateViewer viewer)
        {
            return GameStateViewProjector.Project(state, viewer);
        }

        public static GameStateView ForPlayer(GameState state, int playerId)
        {
            return GameStateViewProjector.ProjectForPlayer(state, playerId);
        }

        public static GameStateView ForSpectator(GameState state)
        {
            return GameStateViewProjector.ProjectForSpectator(state);
        }
    }
}
