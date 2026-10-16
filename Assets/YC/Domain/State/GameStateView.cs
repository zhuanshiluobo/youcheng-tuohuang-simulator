using System;
using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.Items;
using System.Linq;
using YC.Domain.Travel;

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
        public string ActiveSettlementWindowId = string.Empty;
        public YC.Domain.Presentation.SettlementEndActionView SettlementEndAction;
        public YC.Domain.Presentation.ResolutionMessageView ResolutionMessage;
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion = CurrentSchemaVersion;
        public GameStateViewerRole ViewerRole = GameStateViewerRole.Unknown;
        public int ViewerPlayerId = -1;
        public string GameId = string.Empty;
        public string ContentPackHash = string.Empty;
        public GameRuleConfiguration RuleConfiguration;
        public GamePhase Phase = GamePhase.Setup;
        public int Round;
        public int MaxRounds;
        public int StartPlayerId = -1;
        public List<int> LaunchPlayerOrder = new List<int>();
        public int CurrentPlayerId = -1;
        public int ActionRound;
        public string MapId = string.Empty;
        public int StateRevision;
        public string RuntimeStatus = "active";
        public string ActiveMainNodeId = string.Empty;
        public List<PlayerStateView> Players = new List<PlayerStateView>();
        public PublicMapStateView Map = new PublicMapStateView();
        public PublicDeckStateView Decks = new PublicDeckStateView();
        public GameBoxState GameBox = new GameBoxState();
        public ItemZoneStateView ItemZones = new ItemZoneStateView();
        public List<YC.Domain.Cards.CharacterAvailability> Characters = new List<YC.Domain.Cards.CharacterAvailability>();
        public List<YC.Domain.Cards.CharacterUseRuntimeState> CharacterUses = new List<YC.Domain.Cards.CharacterUseRuntimeState>();
        public List<YC.Domain.Cards.CharacterSettlementOption> CharacterSettlementOptions = new List<YC.Domain.Cards.CharacterSettlementOption>();
        public List<YC.Domain.Cards.CoverSlotState> CharacterCoverSlots = new List<YC.Domain.Cards.CoverSlotState>();
        public List<YC.Domain.Cards.FacilityCharacterBindingState> FacilityCharacterBindings = new List<YC.Domain.Cards.FacilityCharacterBindingState>();
        public List<CompanyControlView> CompanyControl = new List<CompanyControlView>();
        public List<MapTokenView> MapTokens = new List<MapTokenView>();
        public List<FacilityBindingDetailView> FacilityBindingDetails = new List<FacilityBindingDetailView>();
        public FinalScoringView FinalScoring;
        public List<InteractionView> Interactions = new List<InteractionView>();
        public List<PreparedActionPaymentView> PreparedActionPayments = new List<PreparedActionPaymentView>();
        public List<int> WaitingForPlayerIds = new List<int>();
        public List<EffectNodeView> Effects = new List<EffectNodeView>();
        public List<PlayerEffectFactView> PlayerEffects = new List<PlayerEffectFactView>();
        public List<RuleEventView> Events = new List<RuleEventView>();
        public List<GameLogView> Logs = new List<GameLogView>();
        public string FaultCode = string.Empty;

        public bool HasValidPlayerOrderAndSlots()
        {
            if (Players == null || Players.Any(player => player == null || player.PlayerId <= 0) ||
                !TurnOrderService.IsValidPlayerOrder(LaunchPlayerOrder, Players.Select(player => player.PlayerId))) return false;
            foreach (var player in Players)
            {
                if (!player.HasConsistentCityStyleDeclarations()) return false;
                var previous = -1;
                foreach (var slot in player.CityStyleUsedSlotIndexes)
                {
                    if (slot <= previous || slot >= 12) return false;
                    previous = slot;
                }
            }
            return true;
        }

        public bool IsPausedFault
        {
            get { return string.Equals(RuntimeStatus, "paused_fault", StringComparison.Ordinal); }
        }
    }

    [Serializable]
    public sealed class PreparedActionPaymentView
    {
        public string PreparationOwnerEffectId = "", PaymentOwnerEffectId = "", PaymentId = "";
        public int PaymentGeneration, WindowGeneration;
        public string OperationWindowId = "", ActionKind = "", TargetId = "", InteractionId = "", PaymentMode = "";
        public bool IsReady, CanConfirm, CanCancel;
        public ResourceSet OriginalCost = new ResourceSet(), SubstitutionCost = new ResourceSet(), RemainingCost = new ResourceSet();
    }

    [Serializable]
    public sealed class CompanyControlView
    {
        public string RegionId = string.Empty, RegionName = string.Empty;
        public int PlayerId, CompanyMarkerCount, Contribution, Remainder;
    }

    /// <summary>公开地图实例的显示信息；未知锁期限保持-1，不能被客户端解释为到期。</summary>
    [Serializable]
    public sealed class FacilityBindingDetailView
    {
        public string BindingId = string.Empty, CharacterItemId = string.Empty, FacilityItemId = string.Empty;
        public int RequiredGoldVoucherAmount;
    }

    [Serializable]
    public sealed class MapTokenView
    {
        public string ItemId = string.Empty, DefinitionId = string.Empty, TypeId = string.Empty;
        public string LocationId = string.Empty, RouteId = string.Empty, SlotId = string.Empty, RegionId = string.Empty, RegionName = string.Empty, BoundCityItemId = string.Empty;
        public int ExpiresBeforeActionRoundSequence = -1;
        public bool IsPermanent;
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
        public int RestrictedMainActionsThisTurn;
        public int CompletedMainActionsThisTurn;
        public bool UsedCharacterThisRound;
        public bool UsedCharacterThisTurn;
        public bool CharacterCardLockedThisTurn;
        public ResourceSet Resources;
        public int AvailableGoldVoucher;
        public int ReservedGoldVoucher;
        public int HandCardCount;
        public int RetainedBasicCharacterCount;
        public int DiscardCardCount;
        public bool HasCoveredCharacterCard;
        public bool PrivateCardIdsVisible;
        public List<string> HandCardIds = new List<string>();
        public List<string> DiscardCardIds = new List<string>();
        public string CoveredCharacterCardId = string.Empty;
        public List<string> BuiltFacilityIds = new List<string>();
        public List<CityStyleDeclarationView> DeclaredCityStyles = new List<CityStyleDeclarationView>();
        public List<string> DeclaredCityStyleIds = new List<string>();
        public List<int> CityStyleUsedSlotIndexes = new List<int>();

        public bool HasConsistentCityStyleDeclarations()
        {
            if (DeclaredCityStyles == null || DeclaredCityStyleIds == null || CityStyleUsedSlotIndexes == null ||
                DeclaredCityStyles.Any(declaration => declaration == null || string.IsNullOrEmpty(declaration.CityStyleId) ||
                    declaration.UsedFacilityIds == null || declaration.UsedCityBoardSlotIndexes == null ||
                    declaration.UsedCityBoardSlotIndexes.Any(index => index < 0 || index >= 12))) return false;
            return DeclaredCityStyleIds.SequenceEqual(DeclaredCityStyles.Select(declaration => declaration.CityStyleId)) &&
                CityStyleUsedSlotIndexes.SequenceEqual(DeclaredCityStyles.SelectMany(declaration => declaration.UsedCityBoardSlotIndexes)
                    .Distinct().OrderBy(index => index));
        }
    }

    [Serializable]
    public sealed class CityStyleDeclarationView
    {
        public string DeclarationId = string.Empty;
        public string CityStyleItemId = string.Empty;
        public string InfluenceMarkerId = string.Empty;
        public string CityStyleId = string.Empty;
        public string UnlockedSpecialActionId = string.Empty;
        public List<string> UsedFacilityIds = new List<string>();
        public List<int> UsedCityBoardSlotIndexes = new List<int>();
        public string MarkerArea = string.Empty;
        public int RemainingSpecialActionUses;
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
        public YC.Domain.Presentation.GameplayInteractionPresentation Presentation;
        public string InteractionId = string.Empty;
        public string SourceNodeId = string.Empty;
        public string OwnerEffectId = string.Empty;
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
        public YC.Domain.Presentation.EffectPresentationDefinition Presentation;
        public int RequestedCount = -1, CompletedCount = -1;
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
    public static partial class GameStateViewProjector
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
            => Project(state, viewer, null);

        public static GameStateView Project(GameState state, GameStateViewer viewer, IReadOnlyList<YC.Domain.Cards.CharacterSettlementOption> settlementOptions)
        {
            var view = new GameStateView
            {
                ViewerRole = viewer.Role,
                ViewerPlayerId = viewer.Role == GameStateViewerRole.Player ? viewer.PlayerId : -1,
                GameId = state == null ? string.Empty : state.GameId ?? string.Empty,
                ContentPackHash = state == null ? string.Empty : state.ContentPackHash ?? string.Empty,
                RuleConfiguration = state?.RuleConfiguration?.Clone(),
                Phase = state == null ? GamePhase.Setup : state.Phase,
                Round = state == null ? 0 : state.Round,
                MaxRounds = state == null ? 0 : state.MaxRounds,
                StartPlayerId = state == null ? -1 : state.StartPlayerId,
                LaunchPlayerOrder = state == null ? new List<int>() : new List<int>(new TurnOrderService().GetLaunchPlayerOrder(state)),
                CurrentPlayerId = state == null ? -1 : state.CurrentPlayerId,
                ActionRound = state == null ? 0 : state.ActionRound,
                MapId = state == null ? string.Empty : state.MapId ?? string.Empty
            };

            if (state == null) return view;
            if (ItemZoneLegacyProjection.IsActive(state)) state = GameStateCloneService.DeepClone(state);
            view.ItemZones = new ItemZoneQuery().Project(state, viewer);
            if (ItemZoneLegacyProjection.IsActive(state) && viewer.Role == GameStateViewerRole.Player)
            {
                var types = ItemTypeRegistry.CreateBuiltIn();
                foreach (var item in view.ItemZones.Items.Where(value => types.HasOperationProfile(value.TypeId, "core.character_card_use")))
                    view.Characters.Add(YC.Domain.Cards.CharacterLifecycleService.GetAvailability(state, viewer.PlayerId, item.ItemId));
                view.CharacterUses.AddRange(state.CharacterUses.Where(value => value.ExecutingPlayerId == viewer.PlayerId &&
                    view.ItemZones.Items.Any(item => item.ItemId == value.CardInstanceId && item.ControllerSubject.PlayerId == viewer.PlayerId))
                    .Select(GameStateCloneService.DeepClone));
                var use = state.CharacterUses.LastOrDefault(value => !value.Finished && !value.SelectionCancelled && value.ExecutingPlayerId == viewer.PlayerId);
                view.CharacterSettlementOptions.AddRange(YC.Domain.Cards.CharacterSettlementOptionQuery.GetOptions(state, viewer.PlayerId, use?.UseId ?? ""));
            }

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
            if (ItemZoneLegacyProjection.IsActive(state)) ProjectExpansionDetails(state, viewer, view);
            if (ItemZoneLegacyProjection.IsActive(state)) view.PlayerEffects = PlayerEffectFactProjector.Project(state, view);
            if (ItemZoneLegacyProjection.IsActive(state)) view.Map.FacilityInstances.RemoveAll(item => !view.ItemZones.Items.Exists(entity => entity.ItemId == item.ContentInstanceId));
            ProjectDecks(state.Decks, view.Decks);
            view.GameBox = GameBoxState.ForViewer(state.GameBox, viewer);
            if (ItemZoneLegacyProjection.IsActive(state)) view.GameBox = GameBoxStateProjector.Project(state, view.ItemZones);
            ProjectFinalScoring(state.FinalScoring, viewer, view);
            ProjectInteractions(state, viewer, view);
            if (viewer.Role == GameStateViewerRole.Player && runtime != null)
                foreach (var owner in runtime.EffectNodes.Where(node => YC.Domain.Effects.ResourcePaymentPreparationService.IsCurrentPreparation(state, node, viewer.PlayerId)))
                {
                    var payment = owner.Payment; var preparation = payment.Preparation;
                    var request = runtime.InteractionRequests.LastOrDefault(value => value.OwnerEffectId == owner.EffectId && value.Status == "open" &&
                        value.InteractionTypeId == YC.Domain.Effects.ResourceEffectExecutor.PaymentConfirmationInteractionTypeId);
                    bool pending = YC.Domain.Effects.ResourcePaymentPreparationService.HasPendingResponse(state, owner);
                    view.PreparedActionPayments.Add(new PreparedActionPaymentView { PreparationOwnerEffectId = preparation.PreparationOwnerEffectId,
                        PaymentOwnerEffectId = owner.EffectId, PaymentId = payment.PaymentId, PaymentGeneration = payment.PaymentGeneration,
                        OperationWindowId = preparation.OperationWindowId, WindowGeneration = preparation.WindowGeneration,
                        ActionKind = preparation.ActionKind, TargetId = preparation.TargetId, InteractionId = request?.GetStableInteractionId() ?? "",
                        PaymentMode = preparation.ActionKind == "build" ? YC.Domain.Effects.ItemTransferEffectExecutor.Text(preparation.ActionArguments, "paymentMode") : "",
                        IsReady = request != null && payment.BeforePaymentReady && !pending,
                        CanConfirm = request != null && !pending && YC.Domain.Effects.ResourcePaymentPreparationService.CanConfirm(state, owner),
                        CanCancel = !pending && !payment.IsCommitted && !payment.IsCommitRequested,
                        OriginalCost = payment.PaymentCost.Clone(), SubstitutionCost = payment.SelectedSubstitution?.Allocation.Clone() ?? new ResourceSet(),
                        RemainingCost = YC.Domain.Effects.ResourcePaymentPreparationService.Remaining(payment) });
                }
            ProjectEffects(runtime, viewer, view.Effects);
            ProjectEvents(runtime, viewer, view.Events);
            ProjectLogs(state.Logs, viewer, view.Logs);
            if (settlementOptions != null)
            {
                view.CharacterSettlementOptions.Clear();
                if (viewer.Role == GameStateViewerRole.Player)
                    view.CharacterSettlementOptions.AddRange(settlementOptions.Where(o => o != null && o.VisibleToViewer && o.AnsweringPlayerId == viewer.PlayerId).Select(GameStateCloneService.DeepClone));
            }
            ProjectGameplayPresentation(state, viewer, view);
            if (!viewer.IsHost) RedactSecretHistory(state, view);
            return view;
        }

        private static void ProjectExpansionDetails(GameState state, GameStateViewer viewer, GameStateView view)
        {
            var visibleIds = new HashSet<string>(view.ItemZones.Items.Select(item => item.ItemId), StringComparer.Ordinal);
            if (viewer.Role == GameStateViewerRole.Player)
                foreach (var slot in state.CharacterCoverSlots.Where(slot => slot.PlayerId == viewer.PlayerId))
                {
                    var projected = GameStateCloneService.DeepClone(slot);
                    if (!visibleIds.Contains(projected.CardInstanceId)) projected.CardInstanceId = string.Empty;
                    projected.SourceEffectId = string.Empty;
                    view.CharacterCoverSlots.Add(projected);
                }
            foreach (var binding in state.FacilityCharacterBindings.Where(binding => binding.Active &&
                visibleIds.Contains(binding.CharacterItemId) && visibleIds.Contains(binding.FacilityItemId)))
            {
                var projected = GameStateCloneService.DeepClone(binding);
                projected.SourceEffectId = string.Empty;
                view.FacilityCharacterBindings.Add(projected);

            }
            // 实体投影也服务自定义地图；正式目录只补显示名，区域归属以本局区域父链为准。
            var regionNames = state.MapId == YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId || state.MapId == YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId
                ? YC.Domain.Maps.StaticMapDefinitions.Resolve(state.MapId).Regions : new List<YC.Domain.Maps.MapRegionDefinition>();
            string RegionName(string id) => regionNames.Find(value => value.RegionId == id)?.DisplayName ?? id;
            var rules = new CompanyInfluenceRules();
            var query = new ItemZoneQuery();
            foreach (string regionId in state.ItemZones.Zones.Where(zone => zone.TemplateId == "core.zone.map_region").Select(zone => zone.LocalZoneId).Distinct())
            {
                int count = state.ItemZones.Items.Count(item => item.OwnerSubject.Kind == ItemSubjectKind.Company &&
                    item.OwnerSubject.CompanyId == CompanyInfluenceRules.CompanyId && query.IsInfluence(state, item.ItemId) &&
                    InfluenceSubjectQuery.GetRegionId(state, item.ItemId) == regionId);
                foreach (var player in state.Players.Where(player => CompanyInfluenceRules.HasContract(state, player.PlayerId)))
                    view.CompanyControl.Add(new CompanyControlView { RegionId = regionId, RegionName = RegionName(regionId),
                        PlayerId = player.PlayerId, CompanyMarkerCount = count, Contribution = rules.GetControlContribution(state, regionId, player.PlayerId), Remainder = count % 2 });
            }
            foreach (var item in view.ItemZones.Items.Where(item => item.TypeId == "infected.gavial_token" || item.TypeId == "infected.influence_lock_token"))
            {
                string location = YC.Domain.Movement.TokenMovementProfileRegistry.GetLocationId(state, item.ItemId);
                if (string.IsNullOrEmpty(location)) continue;
                string region = InfluenceSubjectQuery.GetRegionId(state, item.ItemId);
                var influenceLock = state.InfluenceLocks.SingleOrDefault(value => value.Active && value.SourceItemId == item.ItemId);
                view.MapTokens.Add(new MapTokenView { ItemId = item.ItemId, DefinitionId = item.DefinitionId, TypeId = item.TypeId,
                    LocationId = location, RegionId = region, RegionName = RegionName(region),
                    BoundCityItemId = influenceLock?.BoundCityItemId ?? string.Empty,
                    ExpiresBeforeActionRoundSequence = influenceLock?.ExpiresBeforeActionRoundSequence ?? -1,
                    IsPermanent = item.TypeId == "infected.gavial_token" });
            }
            foreach (var item in view.ItemZones.Items.Where(item => item.OwnerSubject.Kind == ItemSubjectKind.Company && query.IsInfluence(state, item.ItemId)))
            {
                var parent = InfluenceSubjectQuery.GetMapParent(state, item.ZoneId);
                string region = InfluenceSubjectQuery.GetRegionId(state, item.ItemId);
                view.MapTokens.Add(new MapTokenView { ItemId = item.ItemId, DefinitionId = item.DefinitionId, TypeId = item.TypeId, SlotId = item.SlotId,
                    LocationId = parent?.TemplateId == "core.zone.map_location" ? parent.LocalZoneId : string.Empty,
                    RouteId = parent?.TemplateId == "core.zone.map_route" ? parent.LocalZoneId : string.Empty,
                    RegionId = region, RegionName = RegionName(region) });
            }
        }

        private static void RedactSecretHistory(GameState state, GameStateView view)
        {
            var secrets = new HashSet<string>(StringComparer.Ordinal);
            var secretInstances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in state.ItemZones.Items)
            {
                if (state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId != "core.zone.box_secret_retired") continue;
                secrets.Add(item.ItemId);
                secretInstances.Add(item.ItemId);
                secrets.Add(item.DefinitionId);
                if (YC.Domain.Cards.CharacterCardDatabase.IsInitialized)
                {
                    var definition = YC.Domain.Cards.CharacterCardDatabase.Get(state, item.DefinitionId);
                    if (!string.IsNullOrEmpty(definition?.Name)) secrets.Add(definition.Name);
                }
            }
            bool Leaks(NormalizedValue value, bool instanceOnly = false)
            {
                if (value == null) return false;
                var hidden = instanceOnly ? secretInstances : secrets;
                if (hidden.Any(secret => !string.IsNullOrEmpty(secret) && ((value.ReferenceId ?? string.Empty).Contains(secret) || (value.StringValue ?? string.Empty).Contains(secret)))) return true;
                return value.Items != null && value.Items.Any(item => Leaks(item, instanceOnly)) || value.Properties != null && value.Properties.Any(entry => entry != null && Leaks(entry.Value, instanceOnly));
            }
            view.Effects.RemoveAll(node => Leaks(node.NormalizedArguments) || Leaks(node.NormalizedResult));
            view.Events.RemoveAll(value => secrets.Contains(value.TargetEntityId) || Leaks(value.Payload) || Leaks(value.HostOnlyPayload));
            // 活跃请求已经按回答者授权投影。另一玩家回盒同名牌不能抹掉本人的合法候选；
            // 仍禁止任何活跃请求携带秘密回盒的具体实例，已结束请求继续清理定义/名称历史。
            view.Interactions.RemoveAll(value => value.CandidateIds.Any((value.Status == "open" ? secretInstances : secrets).Contains) ||
                Leaks(value.Answer, value.Status == "open") || Leaks(value.PromptParameters, value.Status == "open"));
            view.Logs.RemoveAll(log => secrets.Any(secret => !string.IsNullOrEmpty(secret) && log.Message.Contains(secret)));
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
                RuleConfiguration = view?.RuleConfiguration?.Clone(),
                Phase = view == null ? GamePhase.Setup : view.Phase,
                Round = view == null ? 0 : view.Round,
                MaxRounds = view == null ? 0 : view.MaxRounds,
                StartPlayerId = view == null ? -1 : view.StartPlayerId,
                LaunchPlayerOrder = view == null ? new List<int>() : new List<int>(new TurnOrderService().GetLaunchPlayerOrder(view)),
                CurrentPlayerId = view == null ? -1 : view.CurrentPlayerId,
                ActionRound = view == null ? 0 : view.ActionRound,
                MapId = view == null ? string.Empty : view.MapId ?? string.Empty,
                EffectRuntime = null
            };
            if (view == null) return state;
            state.GameBox = GameBoxState.ForViewer(view.GameBox, new GameStateViewer { Role = view.ViewerRole, PlayerId = view.ViewerPlayerId });

            for (int i = 0; i < view.Players.Count; i++)
            {
                PlayerStateView source = view.Players[i];
                if (source == null) continue;
                if (!source.HasConsistentCityStyleDeclarations())
                    throw new InvalidOperationException("city_style_declaration_projection_incomplete");
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
                    RestrictedMainActionsThisTurn = source.RestrictedMainActionsThisTurn,
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

                foreach (var declaration in source.DeclaredCityStyles)
                    player.DeclaredCityStyles.Add(new CityStyleDeclarationState
                    {
                        DeclarationId = declaration.DeclarationId,
                        CityStyleItemId = declaration.CityStyleItemId,
                        InfluenceMarkerId = declaration.InfluenceMarkerId,
                        CityStyleId = declaration.CityStyleId,
                        UnlockedSpecialActionId = declaration.UnlockedSpecialActionId,
                        UsedFacilityIds = new List<string>(declaration.UsedFacilityIds),
                        UsedCityBoardSlotIndexes = new List<int>(declaration.UsedCityBoardSlotIndexes),
                        MarkerArea = declaration.MarkerArea,
                        RemainingSpecialActionUses = declaration.RemainingSpecialActionUses
                    });

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

            if (view.ItemZones != null && view.ItemZones.Zones.Count > 0)
            {
                state.ItemZones = new ItemZoneRuntimeState
                {
                    Items = view.ItemZones.Items.Select(GameStateCloneService.DeepClone).ToList(),
                    Zones = view.ItemZones.Zones.Select(value => GameStateCloneService.DeepClone(value.Zone)).ToList(),
                    Orders = view.ItemZones.Orders.Select(GameStateCloneService.DeepClone).ToList(),
                    Relations = view.ItemZones.Relations.Select(GameStateCloneService.DeepClone).ToList()
                };
                ItemZoneLegacyProjection.Bind(state, true);
                if (view.ViewerRole == GameStateViewerRole.Player)
                    state.CharacterUses.AddRange((view.CharacterUses ?? new List<YC.Domain.Cards.CharacterUseRuntimeState>()).Where(use => use.ExecutingPlayerId == view.ViewerPlayerId &&
                        state.ItemZones.Items.Any(item => item.ItemId == use.CardInstanceId && item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == view.ViewerPlayerId))
                        .Select(GameStateCloneService.DeepClone));
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
                var goldBalance = state.EffectRuntime == null || source.Resources == null ? null : RouteTollEscrowService.QueryBalance(state, source.PlayerId);
                var handCardIds = source.HandCardIds?.ToArray();
                int retainedBasicCharacterCount = privateVisible && ItemZoneLegacyProjection.IsActive(state)
                    ? YC.Domain.Cards.CharacterLifecycleService.GetRetainedBasicCharacterCount(state, source.PlayerId) : 0;
                var discardCardIds = source.DiscardCardIds?.ToArray();
                string coveredCharacterCardId = source.CoveredCharacterCardId;
                var builtFacilityIds = source.BuiltFacilityIds?.ToArray();
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
                    RestrictedMainActionsThisTurn = source.RestrictedMainActionsThisTurn,
                    CompletedMainActionsThisTurn = source.CompletedMainActionsThisTurn,
                    UsedCharacterThisRound = source.UsedCharacterThisRound,
                    UsedCharacterThisTurn = source.UsedCharacterThisTurn,
                    CharacterCardLockedThisTurn = source.CharacterCardLockedThisTurn,
                    // 资源数量属于主界面的公开玩家摘要，只有卡牌身份等私有数据按拥有者过滤。
                    Resources = source.Resources == null ? new ResourceSet() : source.Resources.Clone(),
                    AvailableGoldVoucher = goldBalance == null ? source.Resources?.GoldVoucher ?? 0 : goldBalance.Available,
                    ReservedGoldVoucher = goldBalance == null ? 0 : goldBalance.Reserved,
                    HandCardCount = handCardIds == null ? 0 : handCardIds.Length,
                    RetainedBasicCharacterCount = retainedBasicCharacterCount,
                    DiscardCardCount = discardCardIds == null ? 0 : discardCardIds.Length,
                    HasCoveredCharacterCard = !string.IsNullOrEmpty(coveredCharacterCardId),
                    PrivateCardIdsVisible = privateVisible,
                    BuiltFacilityIds = builtFacilityIds == null
                        ? new List<string>() : new List<string>(builtFacilityIds)
                };
                if (source.DeclaredCityStyles != null)
                {
                    foreach (var declaration in source.DeclaredCityStyles)
                    {
                        if (declaration == null || declaration.UsedFacilityIds == null || declaration.UsedCityBoardSlotIndexes == null)
                            throw new InvalidOperationException("city_style_declaration_projection_invalid");
                        target.DeclaredCityStyles.Add(new CityStyleDeclarationView
                        {
                            DeclarationId = declaration.DeclarationId,
                            CityStyleItemId = declaration.CityStyleItemId,
                            InfluenceMarkerId = declaration.InfluenceMarkerId,
                            CityStyleId = declaration.CityStyleId,
                            UnlockedSpecialActionId = declaration.UnlockedSpecialActionId,
                            UsedFacilityIds = new List<string>(declaration.UsedFacilityIds),
                            UsedCityBoardSlotIndexes = new List<int>(declaration.UsedCityBoardSlotIndexes),
                            MarkerArea = declaration.MarkerArea,
                            RemainingSpecialActionUses = declaration.RemainingSpecialActionUses
                        });
                    }
                    target.DeclaredCityStyleIds = target.DeclaredCityStyles.Select(declaration => declaration.CityStyleId).ToList();
                    target.CityStyleUsedSlotIndexes = target.DeclaredCityStyles.SelectMany(declaration => declaration.UsedCityBoardSlotIndexes)
                        .Distinct().OrderBy(index => index).ToList();
                    if (!target.HasConsistentCityStyleDeclarations())
                        throw new InvalidOperationException("city_style_declaration_projection_invalid");
                }
                if (privateVisible)
                {
                    if (handCardIds != null) target.HandCardIds.AddRange(handCardIds);
                    if (discardCardIds != null) target.DiscardCardIds.AddRange(discardCardIds);
                    target.CoveredCharacterCardId = coveredCharacterCardId ?? string.Empty;
                }
                destination.Add(target);
            }
        }

        private static void ProjectMap(MapRuntimeState source, PublicMapStateView target, GameStateViewer viewer)
        {
            if (source == null || target == null) return;
            if (source.OpenLocationIds != null) target.OpenLocationIds.AddRange(source.OpenLocationIds);
            var roadRouteIds = source.RoadRouteIds?.ToArray();
            if (roadRouteIds != null) target.RoadRouteIds.AddRange(roadRouteIds);
            if (source.Influences != null)
            {
                var influences = source.Influences.ToArray();
                for (int i = 0; i < influences.Length; i++)
                {
                    InfluencePlacement item = influences[i];
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
                var facilities = source.Facilities.ToArray();
                for (int i = 0; i < facilities.Length; i++)
                {
                    FacilityPlacement item = facilities[i];
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
                var resourceTokens = source.ResourceTokens.ToArray();
                for (int i = 0; i < resourceTokens.Length; i++)
                {
                    ResourceTokenState item = resourceTokens[i];
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
            var facilitySupply = source.FacilitySupply?.ToArray();
            target.FacilitySupplyCount = facilitySupply == null ? 0 : facilitySupply.Length;
            var cityStyleSupply = source.CityStyleSupply?.ToArray();
            target.CityStyleSupplyCount = cityStyleSupply == null ? 0 : cityStyleSupply.Length;
            if (facilitySupply != null) target.PublicFacilitySupplyIds.AddRange(facilitySupply);
            if (cityStyleSupply != null) target.PublicCityStyleSupplyIds.AddRange(cityStyleSupply);
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
                OwnerEffectId = request.OwnerEffectId ?? string.Empty,
                Kind = request.InteractionTypeId ?? string.Empty,
                AnsweringPlayerId = request.AnsweringPlayerId,
                Visibility = request.Visibility ?? string.Empty,
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(request.Presentation),
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
                    Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(source.Presentation),
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
                if (source.EffectTypeId == "effect.flow.repeat" && (viewer.IsHost || viewer.PlayerId == source.PlayerId))
                {
                    projected.RequestedCount = YC.Domain.Effects.ItemTransferEffectExecutor.Number(source.ContinuationData, "requestedCount", -1);
                    projected.CompletedCount = YC.Domain.Effects.ItemTransferEffectExecutor.Number(source.ContinuationData, "completedCount", -1);
                }
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

        private static int Count<T>(ICollection<T> values)
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
