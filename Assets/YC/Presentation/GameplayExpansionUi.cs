using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Enterprises;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using YC.Domain.Presentation;
using YC.Infrastructure.Lua;

namespace YC.Presentation
{
    /// <summary>局内扩展只读适配器。所有实例、资格、候选和等级来自当前观察者的 Host 投影。</summary>
    public sealed class GameplayExpansionUi : MonoBehaviour
    {
        [Serializable] public sealed class BoardCopy
        {
            public string DefinitionId, VisualKey, Label;
        }
        [Serializable] public sealed class ContentCopy
        {
            public string Key, Title, Description, Cost;
        }
        [Serializable] public sealed class GameplayCopy
        {
            public string EnterpriseContext = "公开企业板与各玩家真实合作等级；科室由全体玩家共享。";
            public string InitialEnterpriseContext = "请选择开局企业，候选仅本人可见。";
            public string InitialDepartmentContext = "请选择共享科室。";
            public string SwitchDepartmentContext = "请选择可切换的共享科室。";
            public string SpecialContext = "直接选择企业特效；等级范围由主机提供，费用在提交后复验。";
            public string SpecialFormat = "{0} · {1}级特效";
            public string LevelRangeFormat = "当前 {0} 级，本次可选上限 {1} 级";
            public string LevelFormat = "{0}级：{1}";
            public string PlayerLevelFormat = "{0}：{1}级";
            public string Reward = "到达奖励", Special = "企业特效", DepartmentReward = "共享科室奖励", Empty = "无效果";
            public string DepartmentFormat = "当前共享科室：{0}";
            public string CostUnknown = "按卡面费用，由主机复验";
            public string NoCost = "无额外费用";
            public string CharacterTitle = "角色模式", CharacterPrompt = "请选择本次使用模式。";
            public string OrderPrompt = "请选择两个模式的结算顺序。";
            public string Strategy = "策略", Tactic = "战术", Both = "双模式";
            public string StrategyFirst = "先策略，后战术", TacticFirst = "先战术，后策略";
            public string DispatchTitle = "干员特派", DispatchPrompt = "候选仅本人可见；接受或放弃由主机确认。";
            public string DispatchReturnPrompt = "请选择主机给出的合法换出牌；换出牌将公开回盒。";
            public string ReplacePrompt = "选择干员及手牌，由主机原子复验交换。";
            public string RetainTitle = "保留基础角色", RetainPrompt = "只保留5张基础角色；未保留的牌秘密回盒。干员与流形不占此限额。";
            public string DiscardTitle, DiscardPrompt, DiscardConfirmLabel, LegacyKeepUnsupported;
            public string OptionalInfluenceTitle, OptionalInfluencePrompt, SkipInitialInfluenceLabel;
            public string RouteTargetPrompt;
            public string MissingEnterpriseText;
            public string RetainCountFormat = "基础角色持有 {0} / {1}";
            public string SupplyTitle = "设施供应", SupplyPrompt = "请选择需要移走的设施，补牌保留原槽位置。";
            public string PersistentTitleFormat = "持续角色 {0} · 待结算 {1}";
            public string Normal = "普通", Persistent = "持续", OneShot = "一次性";
            public string ModeFormat = "{0}：{1}{2}";
            public string ProgressFormat = "本次模式 {0} / {1} 已完成";
            public string SubscriptionActive = "，订阅生效", Completed = "，已完成";
            public string ZoneFormat = "区域：{0} · 持有者：{1}";
            public string OwnerFormat = "原所有者：{0} · 当前持有者：{1}";
            public string ItemFormat = "{0} · 实例 {1}";
            public string StorageFormat = "{0} · 数量 {1}";
            public string EmptySlot = "空槽";
            public string CubeStorage = "牌载Cube", FacilityStorage = "牌载设施";
            public string SourceFormat = "来源：{0} / {1}";
            public string OperationAvailable = "当前请求可操作", OperationUnavailable = "当前请求未授权操作";
            public string ViewerOnly = "仅可查看";
            public string PlayerFormat = "玩家 {0}", NoOwner = "公共";
            public string PersistentArea = "持续区", CoveredArea = "盖牌区", HandArea = "手牌区", DiscardArea = "弃牌区", StagingArea = "待结算区";
            public string SettlementTitle = "结算插入", VoucherCostFormat = "{0} 金券";
            public string SettlementReasonFormat = "主机状态：{0}";
            public string MissingEffectTitle = "缺式记录";
            public string MissingEffectFormat = "{0}\n此记录表示效果缺式跳过，未发生奖励。";
            public string CurrentUseFormat = "当前角色：{0} · 使用实例 {1}";
            public string BindingFormat = "绑定设施：{0} · 实例 {1}";
            public string BindingCostFormat = "设施卡面金券 + 2 = {0} 金券（主机报价）";
            public string CoverSlotFormat = "盖牌槽 · {0}";
            public string CoverRestrictionFormat = "接受：{0} · 本实例禁用行动轮序：{1}";
            public string GeneratedCharacters = "流形实体";
            public string CompanyTitle = "岩蹄安保公司", CompanyToll = "公司为独立主体；向公司支付的路费交给银行。";
            public string CompanyControlFormat = "{0}：公司 {1} 枚 → 计入 {2} 点影响力；余 {3} 枚不计入";
            public string RewardTitle = "合约与授勋", RewardPrompt = "从主机提供的剩余真实授勋中选择一张。";
            public string MapTokenFormat = "{0} · {1} · {2}";
            public string PermanentToken = "永久留在地图", LockExpiryFormat = "行动轮序 {0} 开始前到期";
            public string LockExpiryUnknown = "主机尚未提供锁定期限";
            public string DiceResultFormat = "本次骰点：{0}";
            public string RepeatProgressFormat = "已完成 {0} / {1} 次";
            public string MainCoverType = "角色", GeneratedCoverType = "流形";
            public string CompactSettlementFormat = "{0} · {1}", PendingSettlement = "等待主机确认";
            public string CurrentCharacterFormat = "当前角色：{0}", ModeOrderPending = "等待选择模式或顺序";
            public string MapLockLabel = "影响力锁定", MapPermanentLabel = "嘉维尔", CompactCurrentUseFormat = "{0} → {1}";
            public string MapCandidateFormat = "{0} · 所属区块 {1}";
            public string RetiredArea = "公开回盒区", ReserveArea = "预备区", RewardPoolArea = "公共奖励区", PlayerRewardArea = "持有奖励区";
        }

        [SerializeField] private EnterpriseBoardCatalog enterpriseArtwork;
        [SerializeField] private GameplayCopy copy = new GameplayCopy();
        [SerializeField] private BoardCopy[] boards = Array.Empty<BoardCopy>();
        [SerializeField] private ContentCopy[] content = Array.Empty<ContentCopy>();
        [SerializeField] private RectTransform settlementHost;
        [SerializeField] private UiEffectRowView settlementRowTemplate;
        [SerializeField] private SettlementEffectStripView settlementStrip;
        [SerializeField] private RectTransform mapTokenHost;
        [SerializeField] private RectTransform mapTokenViewport;
        [SerializeField] private Text mapTokenTemplate;
        [SerializeField] private Vector2 mapTokenOffset = new Vector2(0, 24);
        // 原板各等级的查看/点击范围是资产配置，不用等级算法生成布局。
        [SerializeField] private Rect[] levelAreas = {
            new Rect(0, .90f, 1, .10f), new Rect(0, .77f, 1, .13f),
            new Rect(0, .635f, 1, .13f), new Rect(0, .50f, 1, .13f),
            new Rect(0, .365f, 1, .13f), new Rect(0, .22f, 1, .13f) };
        private GameplayInteractionHudView hud;
        private Func<GameStateView> getView;
        private Func<int> getPlayer;
        private Action<GameCommand> submit;
        private EnterpriseDefinitions definitions;
        private IDictionary<string, CharacterModeContract> modes;
        private EnterpriseSelectionDialogView publicBoards;
        private EnterpriseSelectionDialogView publicDepartments;
        private string departmentBrowseGameId, departmentBrowseEnterpriseId;
        private int departmentBrowseViewer;
        private CardViewer cardViewer;
        private string inspectedSourceId, inspectedDefinitionId, inspectedGameId;
        private string inspectedSettlementOptionId, inspectedSettlementWindowId;
        private int inspectedPlayer;
        private Func<bool> inspectedSnapshotIsCurrent;
        private object presentationBinding = new object();
        // 宿主提供只比较会话、观察者和版本的守卫，不在查看器每帧重建玩家投影。
        public Func<GameStateView, Func<bool>> CreateSnapshotGuard { private get; set; }
        public Func<SettlementOptionView, bool> CanInvokeSettlement { private get; set; }
        private bool shuttingDown;
        public void BeginShutdown()
        {
            shuttingDown = true;
            if (settlementStrip != null) settlementStrip.ButtonsVisibilityChanged -= OnSettlementButtonsVisibilityChanged;
            InteractionRequestRouter.RequestRouted -= OnRequestRouted;
        }
        public void InvalidateSettlementAvailability()
        { if (!shuttingDown) { contentKey = string.Empty; RefreshSettlementBar(VisibleView()); } }
        private readonly Dictionary<string, UiEffectRowView> rows = new Dictionary<string, UiEffectRowView>();
        private readonly HashSet<string> pendingOptions = new HashSet<string>();
        private readonly HashSet<string> pendingCharacters = new HashSet<string>();
        private sealed class PendingSettlementSubmission
        {
            public string OptionId, CharacterId, WindowId;
            public SettlementSubmissionToken Token;
        }
        private readonly Dictionary<string, PendingSettlementSubmission> pendingSubmissions = new Dictionary<string, PendingSettlementSubmission>();
        private UiEffectRowView optionalInfluenceSkipRow;
        private InteractionRequestProjection optionalInfluenceRequest;
        private Func<bool> optionalInfluenceIsCurrent;
        private bool optionalInfluencePending;
        private readonly Dictionary<string, Text> mapTokens = new Dictionary<string, Text>();
        private readonly List<MapTokenView> mapTokenGeometry = new List<MapTokenView>();
        private InteractionRequestProjection routedRequest;
        private MapView mapView;
        private int renderedPlayer = -1;
        private string contentKey = string.Empty;
        private GameStateView renderedSnapshot;
        private int stripPresentationGeneration = -1;
        private GameStateView detailSnapshot;
        private readonly Dictionary<Tuple<string, string, string, int, int, string, string>, GameplayTextRef[]> enterpriseDetails =
            new Dictionary<Tuple<string, string, string, int, int, string, string>, GameplayTextRef[]>();
        private Tuple<string, int, int, string> enterpriseDetailsSnapshot;
        private ExternalContentPack enterpriseDetailPack, enterpriseDetailInstalledPack;
        private string enterpriseDetailPackGameId, enterpriseDetailPackHash;
        private IReadOnlyList<PlayerDetailEntry> detailEntries = Array.Empty<PlayerDetailEntry>();
        public GameplayCopy Copy => copy;
        public Func<string, CharacterCardDefinition> CharacterDefinition { private get; set; }
        private CharacterCardDefinition GetCharacterDefinition(string id) => CharacterDefinition?.Invoke(id) ?? CharacterCardDatabase.Get(id);
        public EnterpriseDefinitions Definitions => definitions;
        public IReadOnlyCollection<UiEffectRowView> Rows => rows.Values;
        public IReadOnlyList<PlayerDetailDisplayDefinition> DetailDefinitions { get; set; } = Array.Empty<PlayerDetailDisplayDefinition>();
        public IReadOnlyList<PlayerDetailEntry> DetailEntries => ProjectDetails(VisibleView());
        private IReadOnlyList<PlayerDetailEntry> ProjectDetails(GameStateView view)
        {
            if (!ReferenceEquals(detailSnapshot, view))
            {
                detailSnapshot = view;
                detailEntries = PlayerDetailEntryProjector.Project(view, DetailDefinitions);
            }
            return detailEntries;
        }
        [SerializeField] private RectTransform quickActionHost;
        [SerializeField] private UiQuickActionRow quickActionTemplate;
        [SerializeField] private Text[] cooperationNames = Array.Empty<Text>();
        [SerializeField] private Button cooperationOpenButton;
        [SerializeField] private Image[] cooperationGrades = Array.Empty<Image>();
        [SerializeField] private Image[] cooperationLogos = Array.Empty<Image>();
        [SerializeField] private GameObject[] cooperationEmptySlots = Array.Empty<GameObject>();
        [SerializeField] private Sprite[] cooperationMasks = Array.Empty<Sprite>();
        [SerializeField] private GameObject cooperationLevelHeaders;
        [SerializeField] private GameObject cooperationEmpty;
        private readonly Dictionary<string, UiQuickActionRow> quickRows = new Dictionary<string, UiQuickActionRow>();
        private EffectDialogShell detailViewer;
        public IReadOnlyList<string> InspectedItemIds { get; private set; } = Array.Empty<string>();

        public bool TryValidateConfiguration(out string reason)
        {
            reason = "局内企业与持续区适配引用不完整。";
            return enterpriseArtwork != null && copy != null && boards != null && content != null &&
                settlementHost != null && settlementRowTemplate != null && settlementStrip != null && settlementStrip.transform.IsChildOf(settlementHost) &&
                settlementStrip.ValidateReferences(out reason) && mapTokenHost != null && mapTokenViewport != null && mapTokenTemplate != null && mapTokenTemplate.GetComponent<Button>() != null &&
                quickActionHost != null && quickActionTemplate != null && cooperationNames.Length == 4 && cooperationOpenButton != null && cooperationGrades.Length == 24 &&
                cooperationNames.All(value => value != null) && cooperationGrades.All(value => value != null) &&
                cooperationLogos.Length == 4 && cooperationLogos.All(value => value != null) && cooperationLevelHeaders != null &&
                cooperationEmptySlots.Length == 24 && cooperationEmptySlots.All(value => value != null) &&
                cooperationMasks.Length == 16 && cooperationMasks.Skip(1).All(value => value != null) &&
                settlementRowTemplate.TryValidateConfiguration(out reason) &&
                levelAreas != null && levelAreas.Length == 6 && enterpriseArtwork.TryValidateConfiguration(out reason);
        }

        public void Configure(Func<GameStateView> visible, Func<int> player, EnterpriseDefinitions enterpriseDefinitions,
            IDictionary<string, CharacterModeContract> characterModes, Action<GameCommand> send)
        {
            shuttingDown = false;
            Clear();
            hud = GetComponent<GameplayInteractionHudView>();
            getView = visible; getPlayer = player; definitions = enterpriseDefinitions;
            modes = characterModes; submit = send;
            settlementStrip.ButtonsVisibilityChanged -= OnSettlementButtonsVisibilityChanged;
            settlementStrip.ButtonsVisibilityChanged += OnSettlementButtonsVisibilityChanged;
            InteractionRequestRouter.RequestRouted -= OnRequestRouted;
            InteractionRequestRouter.RequestRouted += OnRequestRouted;
            mapView = FindObjectOfType<MapView>();
        }

        private void OnRequestRouted(InteractionRequestProjection request)
        {
            if (optionalInfluenceRequest != null && !SameOptionalInfluenceRequest(optionalInfluenceRequest, request))
                ReleaseOptionalInfluenceSkip();
            if (routedRequest?.InteractionId == request?.InteractionId && routedRequest?.StateRevision == request?.StateRevision &&
                routedRequest?.CandidateSetId == request?.CandidateSetId && routedRequest?.CandidateSetVersion == request?.CandidateSetVersion &&
                routedRequest?.AnsweringPlayerId == request?.AnsweringPlayerId) return;
            routedRequest = request;
            contentKey = string.Empty;
            RefreshSettlementBar(VisibleView());
        }

        public GameStateView VisibleView()
        {
            var view = getView?.Invoke();
            return view != null && view.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == getPlayer?.Invoke()
                ? view : null;
        }

        private Func<bool> GuardSnapshot(GameStateView view)
        {
            var binding = presentationBinding;
            var current = CreateSnapshotGuard?.Invoke(view);
            return () => view != null && ReferenceEquals(binding, presentationBinding) &&
                view.ViewerPlayerId == getPlayer?.Invoke() &&
                (current != null ? current() : ReferenceEquals(renderedSnapshot, view));
        }

        public string ResolveText(string key) => content.FirstOrDefault(value => value.Key == key)?.Title ?? key ?? string.Empty;
        public BoardCopy Board(string definitionId) => boards.FirstOrDefault(value => value.DefinitionId == definitionId);
        public string Name(string definitionId) => definitions?.FindEnterprise(definitionId)?.DisplayNameKey ??
            definitions?.FindDepartment(definitionId)?.DisplayNameKey ?? Board(definitionId)?.Label ?? ResolveText(definitionId);
        public string Description(string key) => content.FirstOrDefault(value => value.Key == key)?.Description ?? string.Empty;
        public string Cost(string key) => content.FirstOrDefault(value => value.Key == key)?.Cost ?? copy.CostUnknown;
        public Rect LevelArea(int level) => level >= 0 && level < levelAreas.Length ? levelAreas[level] : new Rect(0, 0, 1, 1);
        public string SkillType(string type) => type == "persistent" ? copy.Persistent : type == "one_shot" ? copy.OneShot : copy.Normal;
        public string ModeName(string mode) => mode == "strategy" ? copy.Strategy : mode == "tactic" ? copy.Tactic :
            mode == "both" ? copy.Both : mode == "strategy-first" ? copy.StrategyFirst : mode == "tactic-first" ? copy.TacticFirst : ResolveText(mode);
        public string PlayerName(GameStateView view, ItemSubjectRef subject) => subject?.Kind == ItemSubjectKind.Player
            ? view.Players.Find(player => player.PlayerId == subject.PlayerId)?.Name ?? string.Format(copy.PlayerFormat, subject.PlayerId)
            : subject?.Kind == ItemSubjectKind.Company ? ResolveText(subject.CompanyId) : copy.NoOwner;
        public string CardName(string definitionId) => GetCharacterDefinition(definitionId)?.Name ?? Name(definitionId);

        public EnterpriseBoardProjection ProjectBoard(GameStateView view, string itemId, string definitionId)
            => ProjectBoardCore(view, itemId, definitionId, null);

        internal EnterpriseBoardProjection ProjectCandidateBoard(GameStateView view, string candidateId, string definitionId,
            InteractionRequestProjection request) => ProjectBoardCore(view, candidateId, definitionId, request);

        private EnterpriseBoardProjection ProjectBoardCore(GameStateView view, string itemId, string definitionId,
            InteractionRequestProjection request)
        {
            var metadata = Board(definitionId);
            var board = new EnterpriseBoardProjection { Id = itemId, VisualKey = metadata?.VisualKey ?? definitionId,
                Label = Name(definitionId), ViewerPlayerId = view.ViewerPlayerId };
            var item = view.ItemZones.Items.Find(value => value.ItemId == itemId);
            var zone = view.ItemZones.Zones.Find(value => value.Zone.ZoneId == item?.ZoneId)?.Zone;
            if (zone?.TemplateId == "core.zone.enterprise_display")
            {
                board.SupportsDepartmentBrowsing = definitions?.FindEnterprise(definitionId)?.DepartmentRewardPolicy == "active_department";
                board.PlayerIds = view.Players.Select(player => player.PlayerId).ToArray();
                board.PlayerColors = view.Players.Select(player => (int)player.Color).ToArray();
                // 只读 Host 公开轨道；没有标记不会冒充0级。
                board.PlayerLevels = view.Players.Select(player =>
                {
                    var track = view.ItemZones.Zones.Find(value => value.Zone.ParentKind == ZoneParentKind.Item &&
                        value.Zone.ParentId == itemId && value.Zone.LocalZoneId == "cooperation_" + player.PlayerId)?.Zone;
                    var marker = view.ItemZones.Items.Find(value => value.ZoneId == track?.ZoneId && value.OwnerSubject.PlayerId == player.PlayerId);
                    return marker != null && marker.SlotId.StartsWith("level_", StringComparison.Ordinal) &&
                        int.TryParse(marker.SlotId.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out int level) ? level : -1;
                }).ToArray();
                var active = view.ItemZones.Relations.Find(value => value.SourceItemId == itemId &&
                    value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
                var department = active == null ? null : view.ItemZones.Items.Find(value => value.ItemId == active.TargetItemId);
                var departmentDefinition = definitions?.FindDepartment(department?.DefinitionId);
                var companyDefinition = definitions?.FindEnterprise(definitionId);
                if (department != null && departmentDefinition != null && companyDefinition != null &&
                    departmentDefinition.EnterpriseRuntimeId == companyDefinition.RuntimeId && IsIn(view, department, "core.zone.department_display"))
                    board.ActiveDepartment = new EnterpriseDepartmentProjection
                    {
                        ItemId = department.ItemId, VisualKey = Board(department.DefinitionId)?.VisualKey ?? department.DefinitionId,
                        Label = Name(department.DefinitionId), Summary = BoardSummary(view, department.ItemId, department.DefinitionId),
                        Details = BoardDetails(view, department.ItemId, department.DefinitionId)
                    };
            }
            board.Summary = BoardSummary(view, itemId, definitionId);
            int playerIndex = board.PlayerIds == null ? -1 : Array.IndexOf(board.PlayerIds, view.ViewerPlayerId);
            int? level = playerIndex >= 0 && board.PlayerLevels != null && board.PlayerLevels[playerIndex] >= 0
                ? board.PlayerLevels[playerIndex] : (int?)null;
            board.Details = BoardDetails(view, itemId, definitionId, level, request);
            return board;
        }

        private GameplayTextRef[] BoardDetails(GameStateView view, string itemId, string definitionId, int? level = null,
            InteractionRequestProjection request = null)
        {
            var item = view?.ItemZones.Items.Find(value => value.ItemId == itemId && value.DefinitionId == definitionId);
            if (item == null && !IsAuthorizedEnterpriseDefinitionCandidate(view, request, itemId, definitionId))
                return Array.Empty<GameplayTextRef>();
            var presentation = definitions?.FindEnterprise(definitionId)?.Presentation ?? definitions?.FindDepartment(definitionId)?.Presentation;
            if (string.IsNullOrEmpty(presentation?.DetailScript)) return Array.Empty<GameplayTextRef>();
            var relation = item == null ? null : view.ItemZones.Relations.Find(value => value.SourceItemId == itemId &&
                value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
            var department = relation == null ? null : view.ItemZones.Items.Find(value => value.ItemId == relation.TargetItemId);
            var pack = EnterpriseDetailPack(view);
            if (pack == null) return Array.Empty<GameplayTextRef>();
            var snapshot = Tuple.Create(view.GameId, view.ViewerPlayerId, view.StateRevision, pack.PresentationContentHash);
            if (!snapshot.Equals(enterpriseDetailsSnapshot)) { enterpriseDetails.Clear(); enterpriseDetailsSnapshot = snapshot; }
            var key = Tuple.Create(view.GameId, definitionId, itemId, view.ViewerPlayerId, view.StateRevision,
                department?.DefinitionId ?? string.Empty, pack.PresentationContentHash);
            if (!enterpriseDetails.TryGetValue(key, out var details))
            {
                var facts = new List<NormalizedValueEntry>();
                if (level.HasValue) facts.Add(new NormalizedValueEntry { Name = "currentCooperationLevel", Value = NormalizedValue.CreateInteger(level.Value) });
                if (department != null) facts.Add(new NormalizedValueEntry { Name = "activeDepartmentDefinitionId", Value = NormalizedValue.CreateString(department.DefinitionId) });
                var context = new GameplayPresentationContext { Purpose = "enterprise_detail", SourceDefinitionId = definitionId,
                    SourceItemId = item?.ItemId ?? string.Empty, ViewerPlayerId = view.ViewerPlayerId, StateRevision = view.StateRevision,
                    Facts = NormalizedValue.CreateObject(facts) };
                if (pack.TryPresent(presentation.DetailScript, context, out var result, out var code, out var diagnostic))
                    details = result.Details.Select(value => value.Clone()).ToArray();
                else
                {
                    details = Array.Empty<GameplayTextRef>();
                    Debug.LogWarning("企业／科室正式详情加载失败：" + definitionId + " / " + code + " / " + diagnostic, this);
                }
                enterpriseDetails.Add(key, details);
            }
            return details.Select(value => value.Clone()).ToArray();
        }

        internal static bool IsAuthorizedEnterpriseDefinitionCandidate(GameStateView view, InteractionRequestProjection request,
            string candidateId, string definitionId)
        {
            if (request?.InteractionTypeId != "setup.enterprise.choose" || string.IsNullOrEmpty(definitionId) ||
                !GameplayHudFrame.MatchesSummaryRequest(request, view)) return false;
            var current = view.Interactions.Single(value => value.InteractionId == request.InteractionId);
            int index = current.CandidateIds.IndexOf(candidateId);
            var values = current.PromptParameters?.Properties.Find(value => value.Name == "candidateDefinitionIds")?.Value;
            return index >= 0 && values?.Kind == NormalizedValueKind.Array && index < values.Items.Count &&
                values.Items[index].Kind == NormalizedValueKind.String && values.Items[index].StringValue == definitionId &&
                CandidateDefinition(request, candidateId) == definitionId;
        }

        private ExternalContentPack EnterpriseDetailPack(GameStateView view)
        {
            var installed = ExternalContentRuntime.Pack;
            if (view.RuleConfiguration == null || string.IsNullOrEmpty(view.ContentPackHash)) return null;
            if (ReferenceEquals(enterpriseDetailInstalledPack, installed) &&
                enterpriseDetailPackGameId == view.GameId && enterpriseDetailPackHash == view.ContentPackHash) return enterpriseDetailPack;
            enterpriseDetailPack = null; enterpriseDetailInstalledPack = installed;
            enterpriseDetailPackGameId = view.GameId; enterpriseDetailPackHash = view.ContentPackHash;
            enterpriseDetails.Clear(); enterpriseDetailsSnapshot = null;
            try
            {
                var snapshot = installed.CreateActiveSnapshot(view.RuleConfiguration);
                if (!string.Equals(snapshot.ContentHash, view.ContentPackHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("企业／科室详情活动内容与当前局的 ContentPackHash 不一致。");
                enterpriseDetailPack = snapshot;
            }
            catch (InvalidOperationException exception)
            {
                Debug.LogWarning("企业／科室正式详情暂不可用：" + exception.Message, this);
            }
            return enterpriseDetailPack;
        }

        public GameplayTextRef[] SpecialDetails(GameStateView view, string itemId, string definitionId, string abilityId, int currentLevel, int ceiling)
        {
            var enterprise = definitions?.FindEnterprise(definitionId);
            var description = enterprise?.Levels.Find(value => value.AbilityId == abilityId)?.Description;
            if (description == null && enterprise?.DepartmentRewardPolicy == "active_department")
            {
                var relation = view.ItemZones.Relations.Find(value => value.SourceItemId == itemId &&
                    value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
                var item = relation == null ? null : view.ItemZones.Items.Find(value => value.ItemId == relation.TargetItemId);
                var department = definitions?.FindDepartment(item?.DefinitionId);
                if (department != null && department.EnterpriseRuntimeId == enterprise.RuntimeId)
                    description = department.Rewards.Find(value => value.AbilityId == abilityId)?.Description;
            }
            if (description == null) return Array.Empty<GameplayTextRef>();
            return new[] {
                new GameplayTextRef("core:enterprise.special.level_range",
                    new GameplayTextArgument { Name = "current", Kind = "integer", Value = NormalizedValue.CreateInteger(currentLevel) },
                    new GameplayTextArgument { Name = "ceiling", Kind = "integer", Value = NormalizedValue.CreateInteger(ceiling) }),
                description.Clone()
            };
        }

        public string BoardSummary(GameStateView view, string itemId, string definitionId)
        {
            var parts = new List<string>();
            var definition = definitions?.FindEnterprise(definitionId);
            if (definition != null)
                foreach (var level in definition.Levels.Where(value => value.Level > 0).OrderBy(value => value.Level))
                {
                    var text = Description(definitionId + ".level_" + level.Level);
                    parts.Add(string.Format(copy.LevelFormat, level.Level, string.IsNullOrEmpty(text) ? copy.MissingEnterpriseText : text));
                }
            else
            {
                var text = Description(definitionId);
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
            var active = view.ItemZones.Relations.Find(value => value.SourceItemId == itemId && value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation);
            if (active != null)
            {
                var department = view.ItemZones.Items.Find(value => value.ItemId == active.TargetItemId);
                parts.Add(string.Format(copy.DepartmentFormat, Name(department?.DefinitionId)));
            }
            return string.Join("\n", parts);
        }

        public static string CandidateDefinition(InteractionRequestProjection request, string id)
        {
            int index = request.CandidateIds.IndexOf(id);
            var values = request.PromptParameters?.Properties.Find(value => value.Name == "candidateDefinitionIds")?.Value;
            return index >= 0 && values?.Kind == NormalizedValueKind.Array && index < values.Items.Count ? values.Items[index].StringValue : string.Empty;
        }
        public static IReadOnlyList<EnterpriseSpecialCandidate> SpecialCandidates(InteractionRequestProjection request)
        {
            NormalizedValue Field(string name) => request.PromptParameters?.Properties.Find(value => value.Name == name)?.Value;
            string Text(string name, int index) => Field(name)?.Items.ElementAtOrDefault(index)?.StringValue ?? string.Empty;
            int Number(string name, int index) => (int)(Field(name)?.Items.ElementAtOrDefault(index)?.IntegerValue ?? -1);
            return request.CandidateIds.Select((id, index) => new EnterpriseSpecialCandidate { CandidateId = id,
                EnterpriseItemId = Text("candidateEnterpriseItemIds", index), SpecialId = Text("candidateSpecialIds", index),
                CurrentLevel = Number("candidateCurrentLevels", index), EffectiveLevelCeiling = Number("candidateLevelCeilings", index),
                DisplayNameKey = Text("candidateDisplayNameKeys", index) }).ToList();
        }

        public void Refresh()
        {
            if (shuttingDown) return;
            var view = VisibleView();
            if (view == null || hud == null) { Clear(); return; }
            int player = getPlayer();
            if (renderedPlayer != player)
            {
                Clear(); renderedPlayer = player;
                hud.CharacterHandPanel.CloseCharacterCardViewer(); hud.CharacterHandPanel.CloseDiscardPreview();
            }
            var key = view.GameId + ":" + player + ":" + view.StateRevision + ":" + view.ItemZones.Items.Count + ":" +
                view.Interactions.Count + ":" + view.CharacterSettlementOptions.Count;
            // Refresh 在实际内容变化或宿主被热座清空后才更新条目，避免每帧销毁布局。
            if (ReferenceEquals(renderedSnapshot, view) && contentKey == key && rows.Values.All(value => value != null)) return;
            renderedSnapshot = view;
            RefreshSourceInspection(view);
            if (detailViewer?.IsShowing == true) { detailViewer.Hide(); InspectedItemIds = Array.Empty<string>(); }
            contentKey = key;
            var used = new HashSet<string>();
            var ownHost = hud.MainModules.GetLocalDetailHost(0);
            var characterHost = hud.MainModules.GetLocalDetailHost(1);
            var entries = ProjectDetails(view);
            var snapshotIsCurrent = GuardSnapshot(view);
            foreach (var entry in entries)
            {
                var host = entry.DetailPlayerId == player ? (entry.StorageZoneIds.Count > 0 || entry.HasCoverArea ? characterHost : ownHost)
                    : hud.MainModules.GetOpponentDetailHost(entry.DetailPlayerId);
                var row = GetRow(host, entry.EntryId);
                if (row == null) continue;
                used.Add(entry.EntryId);
                row.Bind(new UiEffectRowView.Binding { StableItemId = entry.EntryId, RequestId = view.GameId, Revision = view.StateRevision,
                    RowMode = UiEffectRowView.Mode.PersistentDetail, Title = entry.DisplayName, Description = entry.EffectText,
                    Markers = DetailMarkers(view, entry) },
                    (request, revision) => snapshotIsCurrent() && view.GameId == request && view.StateRevision == revision,
                    _ => InspectDetail(entry), null);
            }
            RefreshCooperation(view);
            RefreshQuickActions(view);
            RefreshSettlementBar(view);
            RefreshMapTokens(view);
            foreach (var id in rows.Keys.Where(id => !used.Contains(id)).ToArray())
            {
                if (rows[id] != null) { rows[id].ClearBinding(); EnterpriseBoardItemView.Retire(rows[id].gameObject); }
                rows.Remove(id); pendingOptions.Remove(id);
            }
            hud.MainModules.RefreshDetailAvailability();
            if (publicDepartments != null && publicDepartments.gameObject.activeSelf) RefreshPublicDepartments(view);
            else if (publicBoards != null && publicBoards.gameObject.activeSelf) OpenPublicEnterprises();
        }

        private static bool IsIn(GameStateView view, ItemInstanceState item, string template) =>
            view.ItemZones.Zones.Any(value => value.Zone.ZoneId == item.ZoneId && value.Zone.TemplateId == template);

        private UiEffectRowView.Marker[] DetailMarkers(GameStateView view, PlayerDetailEntry entry) => view.ItemZones.Items
            .Where(item => entry.StorageZoneIds.Contains(item.ZoneId) && item.PhysicalKind == ItemPhysicalKind.Cube && item.OwnerSubject.Kind == ItemSubjectKind.Player)
            .GroupBy(item => new { item.TypeId, item.OwnerSubject.PlayerId }).Take(2)
            .Select(group => new UiEffectRowView.Marker { Mask = (int)RegionInformationPageView.PlayerColor(view.Players.Find(player => player.PlayerId == group.Key.PlayerId)?.Color ?? PlayerColor.Red), Count = group.Count() }).ToArray();

        private void InspectDetail(PlayerDetailEntry entry)
        {
            var view = VisibleView();
            if (entry == null || view == null) return;
            var current = ProjectDetails(view).FirstOrDefault(value => value.EntryId == entry.EntryId);
            if (current == null || current.SourceItemId != entry.SourceItemId) return;
            OpenSourceCard(current.SourceItemId, view);
        }

        private bool IsQuickOption(GameStateView view, string sourceItemId)
        {
            var item = view?.ItemZones.Items.Find(value => value.ItemId == sourceItemId);
            return item != null && DetailDefinitions.Any(value => value.DefinitionId == item.DefinitionId && value.DisplayCategory == PlayerDetailDisplayCategory.QuickAction);
        }

        private void RefreshQuickActions(GameStateView view)
        {
            if (quickActionHost == null || quickActionTemplate == null) return;
            var options = view?.CharacterSettlementOptions.Where(option => string.IsNullOrEmpty(option.EntryKind) && option.VisibleToViewer && option.AnsweringPlayerId == getPlayer() && IsQuickOption(view, option.SourceItemId) &&
                    view.ItemZones.Items.Any(item => item.ItemId == option.SourceItemId && item.ControllerSubject.PlayerId == getPlayer() && IsIn(view, item, "core.zone.player_awards")))
                .Select(SettlementOptionView.FromOption).ToList() ?? new List<SettlementOptionView>();
            foreach (var id in quickRows.Keys.Where(id => options.All(option => option.OptionId != id)).ToArray())
            { EnterpriseBoardItemView.Retire(quickRows[id].gameObject); quickRows.Remove(id); }
            quickActionTemplate.gameObject.SetActive(false);
            foreach (var option in options)
            {
                if (!quickRows.TryGetValue(option.OptionId, out var row) || row == null)
                { row = Instantiate(quickActionTemplate, quickActionHost, false); row.name = "Award Quick Action"; quickRows[option.OptionId] = row; }
                var item = view.ItemZones.Items.Find(value => value.ItemId == option.SourceItemId);
                var definition = DetailDefinitions.First(value => value.DefinitionId == item.DefinitionId && value.DisplayCategory == PlayerDetailDisplayCategory.QuickAction);
                row.gameObject.SetActive(true);
                row.Bind(definition.DisplayName, option.IsEnabled && !pendingOptions.Contains(option.OptionId), () => InvokeSettlement(option));
            }
        }

        private void RefreshCooperation(GameStateView view)
        {
            cooperationOpenButton.onClick.RemoveListener(OpenPublicEnterprises);
            cooperationOpenButton.onClick.AddListener(OpenPublicEnterprises);
            var items = view.ItemZones.Items.Where(item => IsIn(view, item, "core.zone.enterprise_display")).ToList();
            if (cooperationEmpty != null) cooperationEmpty.SetActive(items.Count == 0);
            cooperationLevelHeaders.SetActive(items.Count > 0);
            for (var i = 0; i < cooperationNames.Length; i++)
            {
                var exists = i < items.Count;
                cooperationNames[i].transform.parent.gameObject.SetActive(exists);
                if (!exists) continue;
                var board = ProjectBoard(view, items[i].ItemId, items[i].DefinitionId);
                cooperationNames[i].text = board.Label;
                cooperationLogos[i].sprite = enterpriseArtwork.Logo(board.VisualKey);
                cooperationLogos[i].enabled = cooperationLogos[i].sprite != null;
                for (var level = 0; level < 6; level++)
                {
                    // 素材位值：绿 1、黄 2、蓝 4、红 8。同级合并为一张分色标记。
                    int mask = 0;
                    for (var player = 0; player < view.Players.Count; player++)
                        if (board.PlayerLevels[player] == level) mask |= (int)RegionInformationPageView.PlayerColor(view.Players[player].Color);
                    var index = i * 6 + level;
                    cooperationGrades[index].sprite = cooperationMasks[mask];
                    cooperationGrades[index].enabled = mask != 0;
                    cooperationEmptySlots[index].SetActive(mask == 0);
                }
            }
        }
        private UiEffectRowView GetRow(RectTransform host, string id)
        {
            if (host == null) return null;
            if (!rows.TryGetValue(id, out var row) || row == null)
            {
                row = hud.DialogRegistry.InstantiateEffectRow(host); row.name = "GameplayDetail"; row.gameObject.SetActive(true); rows[id] = row;
            }
            if (row.transform.parent != host) row.transform.SetParent(host, false);
            return row;
        }
        public string Reason(string code) => string.IsNullOrEmpty(code) ? string.Empty :
            content.FirstOrDefault(value => value.Key == code)?.Description ?? string.Format(copy.SettlementReasonFormat, code);
        // 当前角色操作页的上下文说明；玩家明细不调用此诊断摘要。
        public string CardDescription(GameStateView view, ItemInstanceState item)
        {
            var zone = view.ItemZones.Zones.Find(value => value.Zone.ZoneId == item.ZoneId)?.Zone;
            string ZoneName(string template) => template == "core.zone.character_persistent" ? copy.PersistentArea :
                template == "core.zone.character_covered" ? copy.CoveredArea : template == "core.zone.character_hand" ? copy.HandArea :
                template == "core.zone.character_discard" ? copy.DiscardArea : template == "core.zone.box_retired" ? copy.RetiredArea :
                template == "core.zone.box_reserve" ? copy.ReserveArea : template == "core.zone.reward_pool" ? copy.RewardPoolArea :
                template == "core.zone.player_awards" ? copy.PlayerRewardArea : copy.StagingArea;
            var parts = new List<string> { string.Format(copy.ZoneFormat, ZoneName(zone?.TemplateId), PlayerName(view, item.ControllerSubject)),
                string.Format(copy.OwnerFormat, PlayerName(view, item.OwnerSubject), PlayerName(view, item.ControllerSubject)) };
            var use = view.CharacterUses.LastOrDefault(value => value.CardInstanceId == item.ItemId);
            if (use != null)
            {
                foreach (var mode in use.Modes)
                    parts.Add(string.Format(copy.ModeFormat, ModeName(mode.Mode), SkillType(mode.SkillType),
                        mode.SubscriptionActive ? copy.SubscriptionActive : use.CompletedModes.Contains(mode.Mode) ? copy.Completed : string.Empty));
                if (!use.Finished) parts.Add(use.ModeOrder.Count == 0 ? copy.ModeOrderPending : string.Format(copy.ProgressFormat, use.CompletedModes.Count, use.ModeOrder.Count));
            }
            else if (modes != null)
            {
                var definition = GetCharacterDefinition(item.DefinitionId);
                foreach (var id in new[] { definition?.StrategyAbilityId, definition?.TacticAbilityId })
                    if (id != null && modes.TryGetValue(id, out var mode)) parts.Add(string.Format(copy.ModeFormat, ModeName(mode.Mode), SkillType(mode.SkillType), string.Empty));
            }
            foreach (var storage in view.ItemZones.Zones.Where(value => value.Zone.ParentKind == ZoneParentKind.Item && value.Zone.ParentId == item.ItemId &&
                (value.Zone.TemplateId == "core.zone.card_cube_storage" || value.Zone.TemplateId == "core.zone.card_facility_storage")))
            {
                var stored = view.ItemZones.Items.Where(value => value.ZoneId == storage.Zone.ZoneId).ToList();
                parts.Add(string.Format(copy.StorageFormat, storage.Zone.TemplateId == "core.zone.card_cube_storage" ? copy.CubeStorage : copy.FacilityStorage, storage.ItemCount));
                if (storage.ItemCount == 0) parts.Add(copy.EmptySlot);
                foreach (var component in stored)
                {
                    bool authorized = view.Interactions.Any(request => request.Status == "open" && request.AnsweringPlayerId == getPlayer() && request.CandidateIds.Contains(component.ItemId));
                    parts.Add(string.Format(copy.ItemFormat, component.PhysicalKind == ItemPhysicalKind.Cube ? copy.CubeStorage : Name(component.DefinitionId), component.ItemId));
                    parts.Add(string.Format(copy.OwnerFormat, PlayerName(view, component.OwnerSubject), PlayerName(view, component.ControllerSubject)));
                    parts.Add(string.Format(copy.SourceFormat, item.ItemId, storage.Zone.LocalZoneId));
                    parts.Add(authorized ? copy.OperationAvailable : copy.OperationUnavailable);
                }
            }
            return string.Join("\n", parts);
        }

        // 通用插槽合同：这里只适配真正出现的 Host DTO，没有阿米娅占位按钮。
        public UiEffectRowView.Binding SettlementBinding(SettlementOptionView option) => new UiEffectRowView.Binding
        {
            StableItemId = option.OptionId, RequestId = option.InteractionId, Revision = option.StateRevision,
            RowMode = UiEffectRowView.Mode.ChoiceExecute, RightStatus = UiEffectRowView.Status.Use,
            Enabled = option.VisibleToViewer && option.IsEnabled && option.AnsweringPlayerId == getPlayer?.Invoke() &&
                (CanInvokeSettlement == null || CanInvokeSettlement(option)),
            Title = string.IsNullOrEmpty(option.TitleKey) ? copy.SettlementTitle : ResolveText(option.TitleKey),
            Description = SettlementDescription(option), Source = option.SourceItemId,
            CostImpact = option.RequiredGoldVoucherAmount > 0 ? string.Format(copy.VoucherCostFormat, option.RequiredGoldVoucherAmount) : string.Empty,
            UnavailableReason = Reason(option.DisabledReasonCode), Quote = UiEffectRowView.QuoteState.NotApplicable
        };
        public string SettlementDescription(SettlementOptionView option)
        {
            var view = VisibleView();
            string description = Description(option.DescriptionKey);
            var parts = new List<string> { string.IsNullOrEmpty(description) ? option.DescriptionKey : description };
            var use = view?.CharacterUses.Find(value => value.UseId == option.TargetUseId);
            var current = view?.ItemZones.Items.Find(value => value.ItemId == use?.CardInstanceId);
            if (current != null) parts.Add(string.Format(copy.CurrentUseFormat, CardName(current.DefinitionId), use.UseId));
            var binding = view?.FacilityCharacterBindings.Find(value => value.CharacterItemId == option.SourceItemId && value.Active);
            var facility = view?.ItemZones.Items.Find(value => value.ItemId == binding?.FacilityItemId);
            if (facility != null)
            {
                parts.Add(string.Format(copy.BindingFormat, ItemName(facility.DefinitionId), facility.ItemId));
                parts.Add(string.Format(copy.BindingCostFormat, option.RequiredGoldVoucherAmount));
            }
            return string.Join("\n", parts.Where(value => !string.IsNullOrEmpty(value)));
        }
        private bool SettlementCurrent(SettlementOptionView option, bool requireEnabled = true)
        {
            var view = VisibleView();
            return view != null && !GameplayHudFrame.EffectInputSuspended &&
                (!requireEnabled || CanInvokeSettlement == null || CanInvokeSettlement(option)) && view.StateRevision == option.StateRevision &&
                (string.IsNullOrEmpty(option.WindowId) || view.ActiveSettlementWindowId == option.WindowId) &&
                view.CharacterSettlementOptions.Any(value => value.OptionId == option.OptionId && value.InteractionId == option.InteractionId &&
                    value.OwnerEffectId == option.OwnerEffectId && value.SourceItemId == option.SourceItemId && value.CandidateId == option.CandidateId &&
                    value.WindowId == option.WindowId && value.EntryKind == option.EntryKind && value.TargetEffectId == option.TargetEffectId &&
                    value.StateRevision == option.StateRevision &&
                    value.TargetUseId == option.TargetUseId && value.CandidateSetVersion == option.CandidateSetVersion && (!requireEnabled || value.IsEnabled) &&
                    value.VisibleToViewer && value.AnsweringPlayerId == getPlayer());
        }
        private void InvokeSettlement(SettlementOptionView option)
        {
            var token = settlementStrip.CapturePending(option.OptionId);
            if (!SettlementCurrent(option) || !pendingOptions.Add(option.OptionId))
            { settlementStrip.SetPending(option.WindowId, option.OptionId, false, token); return; }
            var command = EffectInteractionCommands.InvokeSettlementOption(getPlayer(), option.OptionId, option.TargetUseId, option.StateRevision);
            pendingSubmissions.Add(command.CommandId, new PendingSettlementSubmission { OptionId = option.OptionId, WindowId = option.WindowId, Token = token });
            contentKey = string.Empty;
            RefreshQuickActions(VisibleView());
            try { submit(command); }
            catch { NotifyCommandSettled(command.CommandId); throw; }
        }
        [SerializeField] private string coveredActionTitle = "选择盖放角色";
        [SerializeField] private string coveredActionHint = "选择当前可使用的角色";
        public void OpenCoveredActions()
        {
            var view = VisibleView();
            if (view == null) return;
            var state = FindObjectOfType<MobileCityInteractionController>()?.CurrentState;
            var displayOrder = new CharacterCardDisplayOrder(state?.GameId == view.GameId ? state.ContentDefinitions : null);
            var candidates = displayOrder.Sort(view.Characters.Where(value => value.CanUse).Select(value => value.ItemId),
                id => view.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId, id => id);
            if (candidates.Count == 1) { BeginCharacter(candidates[0]); return; }
            if (candidates.Count == 0) return;
            if (detailViewer == null) detailViewer = new EffectDialogShell(hud.DialogRegistry);
            var layout = hud.DialogRegistry.EffectDialogLayoutProfile;
            var panel = detailViewer.PreparePage(hud.Frame.ContentRect, "Covered Character Actions", "Covered Character Actions Panel",
                layout.SelectionPanelSize, layout.CharacterPanelPosition, true, false, true, cardPicker: true);
            EffectDialogShell.AddHeading(panel, coveredActionTitle, coveredActionHint);
            detailViewer.AddSelection(panel, new EffectDialogSelectionSpec {
                IsEffectPage = false, CloseOnBackgroundClick = true,
                Request = new InteractionRequestProjection { VisibleToViewer = true, CandidateIds = candidates,
                    MinSelections = 1, MaxSelections = 1, AllowDecline = true, InteractionId = "covered-actions", Status = "open" },
                Label = id => CardName(view.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId),
                CardSprite = id => hud.DialogRegistry.CardVisualCatalog.GetCharacterFront(view.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId),
                IsCurrent = () => VisibleView()?.ViewerPlayerId == view.ViewerPlayerId && VisibleView()?.StateRevision == view.StateRevision,
                CanSelect = id => VisibleView()?.Characters.Any(value => value.ItemId == id && value.CanUse) == true,
                Select = id => { detailViewer.Hide(); BeginCharacter(id); },
                Cancel = () => detailViewer.Hide(), CancelLabel = layout.ReadOnlyCloseLabel });
        }
        private void BeginCharacter(string id)
        {
            var view = VisibleView();
            if (view?.Characters.Any(value => value.ItemId == id && value.CanUse) != true || !pendingCharacters.Add(id)) return;
            var command = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = getPlayer(), TargetId = id };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = id;
            command.Parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            pendingSubmissions.Add(command.CommandId, new PendingSettlementSubmission { CharacterId = id });
            try { submit(command); }
            catch { NotifyCommandSettled(command.CommandId); throw; }
        }
        public void NotifyCommandSettled(string commandId)
        {
            if (string.IsNullOrEmpty(commandId) || !pendingSubmissions.TryGetValue(commandId, out var pending)) return;
            pendingSubmissions.Remove(commandId);
            if (!string.IsNullOrEmpty(pending.OptionId))
            { pendingOptions.Remove(pending.OptionId); settlementStrip.SetPending(pending.WindowId, pending.OptionId, false, pending.Token); }
            if (!string.IsNullOrEmpty(pending.CharacterId)) pendingCharacters.Remove(pending.CharacterId);
            contentKey = string.Empty;
        }
        public GameplayTextDocument ResolveGameplayText(GameplayTextRef text)
        {
            hud.Frame.BindGameplaySnapshot(VisibleView());
            return hud.Frame.ResolveGameplayText(text);
        }
        public GameplayTextDocument ResolveEnterpriseDetails(IReadOnlyList<GameplayTextRef> details)
        {
            if (details == null || details.Count == 0) return null;
            hud.Frame.BindGameplaySnapshot(VisibleView());
            return ResolveEnterpriseDetails(details, hud.Frame.ResolveGameplayText);
        }
        internal static GameplayTextDocument ResolveEnterpriseDetails(IReadOnlyList<GameplayTextRef> details,
            Func<GameplayTextRef, GameplayTextDocument> resolve)
        {
            if (details == null || details.Count == 0) return null;
            var runs = new List<GameplayTextRun>();
            var identity = new StringBuilder(); var measurement = new StringBuilder();
            foreach (var reference in details)
            {
                var document = resolve(reference);
                if (document == null) return null;
                if (runs.Count > 0) runs.Add(new LineBreak());
                runs.AddRange(document.Runs);
                identity.Append(document.Identity.Length).Append(':').Append(document.Identity);
                measurement.Append(document.MeasurementIdentity.Length).Append(':').Append(document.MeasurementIdentity);
            }
            return new GameplayTextDocument(runs, identity.ToString(), measurement.ToString());
        }
        public void RenderEnterpriseDetails(Component host, GameplayTextDocument document, bool singleLine) =>
            hud.Frame.RenderGameplayText(host, document, singleLine);
        private void Inspect(string id) => OpenSourceCard(id, VisibleView());

        private void OpenSourceCard(string id, GameStateView view)
        {
            var item = view?.ItemZones.Items.Find(value => value.ItemId == id);
            if (item == null) return;
            inspectedSettlementOptionId = inspectedSettlementWindowId = null;
            var sprite = hud.DialogRegistry.CardVisualCatalog.GetCharacterFront(item.DefinitionId) ?? hud.DialogRegistry.CardVisualCatalog.GetFacility(item.DefinitionId);
            if (sprite == null) sprite = ExternalContentRuntime.GetArtworkSprite("award", item.DefinitionId) ?? ExternalContentRuntime.GetArtworkSprite("contract", item.DefinitionId);
            if (sprite == null) return;
            inspectedPlayer = getPlayer();
            inspectedSourceId = id;
            inspectedDefinitionId = item.DefinitionId;
            inspectedGameId = view.GameId;
            inspectedSnapshotIsCurrent = GuardSnapshot(view);
            InspectedItemIds = new[] { id };
            if (cardViewer == null) cardViewer = hud.DialogRegistry.InstantiateCardViewer();
            cardViewer.OpenInspect(sprite, ClearSourceInspection, SourceInspectionIsCurrent);
        }

        private void RefreshSourceInspection(GameStateView view)
        {
            if (cardViewer == null || !cardViewer.IsShowing) return;
            if (!string.IsNullOrEmpty(inspectedSettlementOptionId))
            {
                var option = view?.CharacterSettlementOptions.Find(value => value.OptionId == inspectedSettlementOptionId &&
                    value.WindowId == inspectedSettlementWindowId && value.VisibleToViewer && value.CanInspectSource && value.AnsweringPlayerId == getPlayer());
                if (view == null || view.GameId != inspectedGameId || getPlayer() != inspectedPlayer ||
                    option == null || option.SourceItemId != inspectedSourceId || option.SourceDefinitionId != inspectedDefinitionId)
                { cardViewer.Dismiss(); ClearSourceInspection(); return; }
                inspectedSnapshotIsCurrent = GuardSnapshot(view); return;
            }
            if (string.IsNullOrEmpty(inspectedSourceId)) return;
            var item = view?.ItemZones.Items.Find(value => value.ItemId == inspectedSourceId);
            if (view == null || view.GameId != inspectedGameId || getPlayer() != inspectedPlayer ||
                item == null || item.DefinitionId != inspectedDefinitionId)
            {
                cardViewer.Dismiss();
                ClearSourceInspection();
                return;
            }
            // 正常同步已得到新的授权快照；只有这里复核来源，逐帧仅比较身份与版本。
            inspectedSnapshotIsCurrent = GuardSnapshot(view);
        }

        private bool SourceInspectionIsCurrent()
        {
            if (inspectedSnapshotIsCurrent != null && inspectedSnapshotIsCurrent()) return true;
            ClearSourceInspection();
            return false;
        }

        private void ClearSourceInspection()
        {
            inspectedSnapshotIsCurrent = null;
            inspectedSourceId = inspectedDefinitionId = inspectedGameId = null;
            inspectedSettlementOptionId = inspectedSettlementWindowId = null;
            InspectedItemIds = Array.Empty<string>();
        }

        public void OpenPublicEnterprises()
        {
            var view = VisibleView(); if (view == null) return;
            if (publicBoards == null) publicBoards = hud.DialogRegistry.InstantiateEnterpriseSelection(hud.Frame.ContentRect, false);
            if (publicDepartments != null && publicDepartments.gameObject.activeSelf) publicDepartments.ClearForNavigation();
            departmentBrowseGameId = departmentBrowseEnterpriseId = null;
            publicBoards.BindSubmission(null, null, null);
            publicBoards.BindBrowseNavigation(OpenPublicDepartments, null);
            publicBoards.BindGameplayText(ResolveEnterpriseDetails, RenderEnterpriseDetails);
            publicBoards.Present(BuildPublicEnterpriseProjection(view));
        }

        private EnterpriseSelectionProjection BuildPublicEnterpriseProjection(GameStateView view)
        {
            var items = view.ItemZones.Items.Where(value => IsIn(view, value, "core.zone.enterprise_display") || IsIn(view, value, "core.zone.department_display") &&
                view.ItemZones.Relations.Any(relation => relation.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation && relation.TargetItemId == value.ItemId)).ToList();
            var boards = items.Select(item => ProjectBoard(view, item.ItemId, item.DefinitionId)).ToArray();
            for (int i = 0; i < boards.Length; i++)
                boards[i].Summary = BuildCooperationBoardSummary(view, items[i].ItemId, items[i].DefinitionId, boards[i]);
            return new EnterpriseSelectionProjection { ReadOnly = true, CanCancel = true, Context = copy.EnterpriseContext,
                RequestId = view.GameId + ":public-enterprises:" + view.ViewerPlayerId, ViewerPlayerId = view.ViewerPlayerId, Revision = view.StateRevision,
                Mode = EnterpriseSelectionMode.Company, Boards = boards,
                Targets = boards.Select(board => new EnterpriseSelectionTarget { Id = board.Id, BoardId = board.Id, CompanyId = board.Id,
                    Label = board.Label, Description = board.Summary, Details = board.Details, Available = false }).ToArray() };
        }

        private string BuildCooperationBoardSummary(GameStateView view, string itemId, string definitionId, EnterpriseBoardProjection board)
        {
            if (!board.SupportsDepartmentBrowsing || board.ActiveDepartment == null || string.IsNullOrEmpty(board.ActiveDepartment.Summary))
                return board.Summary;
            var firstLevel = Description(definitionId + ".level_1");
            // 科室原文自带 2/3/4/5 级分段，与莱茵 1 级并列；不改变任何奖励触发等级。
            return string.Join("\n", new[] {
                string.Format(copy.LevelFormat, 1, string.IsNullOrEmpty(firstLevel) ? copy.MissingEnterpriseText : firstLevel),
                board.ActiveDepartment.Summary,
                string.Format(copy.DepartmentFormat, board.ActiveDepartment.Label)
            });
        }

        private EnterpriseSelectionProjection BuildPublicDepartmentProjection(GameStateView view, string enterpriseItemId)
        {
            var company = view?.ItemZones.Items.FirstOrDefault(item => item.ItemId == enterpriseItemId && IsIn(view, item, "core.zone.enterprise_display"));
            var definition = definitions?.FindEnterprise(company?.DefinitionId);
            if (company == null || definition?.DepartmentRewardPolicy != "active_department") return null;
            var fixedBoard = ProjectBoard(view, company.ItemId, company.DefinitionId);
            string currentId = fixedBoard.ActiveDepartment?.ItemId;
            var boards = view.ItemZones.Items.Where(item => item.ItemId != currentId && IsIn(view, item, "core.zone.department_display") &&
                definitions.FindDepartment(item.DefinitionId)?.EnterpriseRuntimeId == definition.RuntimeId)
                .OrderBy(item => item.ItemId, StringComparer.Ordinal)
                .Select(item => ProjectBoard(view, item.ItemId, item.DefinitionId)).ToArray();
            return new EnterpriseSelectionProjection {
                Mode = EnterpriseSelectionMode.BrowseDepartment, ReadOnly = true, SubmissionAvailable = false, CanCancel = true,
                RequestId = view.GameId + ":public-departments:" + view.ViewerPlayerId + ":" + company.ItemId,
                ViewerPlayerId = view.ViewerPlayerId, Revision = view.StateRevision,
                CurrentDepartmentId = currentId, FixedCompany = fixedBoard, Boards = boards,
                Targets = boards.Select(board => new EnterpriseSelectionTarget {
                    Id = board.Id, BoardId = board.Id, DepartmentId = board.Id, Label = board.Label,
                    Description = board.Summary, Details = board.Details, Available = false }).ToArray()
            };
        }

        private void OpenPublicDepartments(string enterpriseItemId)
        {
            var view = VisibleView();
            if (view == null || publicBoards == null || !publicBoards.gameObject.activeInHierarchy ||
                publicBoards.Draft.Projection?.RequestId != view.GameId + ":public-enterprises:" + view.ViewerPlayerId) return;
            var projection = BuildPublicDepartmentProjection(view, enterpriseItemId);
            if (projection == null) return;
            if (publicDepartments == null) publicDepartments = hud.DialogRegistry.InstantiateEnterpriseSelection(hud.Frame.ContentRect, true);
            publicBoards.SuspendForNavigation();
            departmentBrowseGameId = view.GameId;
            departmentBrowseViewer = view.ViewerPlayerId;
            departmentBrowseEnterpriseId = enterpriseItemId;
            publicDepartments.BindSubmission(null, null, null);
            publicDepartments.BindBrowseNavigation(null, ReturnToPublicEnterprises);
            publicDepartments.BindGameplayText(ResolveEnterpriseDetails, RenderEnterpriseDetails);
            publicDepartments.Present(projection);
        }

        private bool HasCurrentDepartmentBrowse(GameStateView view) => view != null &&
            view.GameId == departmentBrowseGameId && view.ViewerPlayerId == departmentBrowseViewer;

        private void RefreshPublicDepartments(GameStateView view)
        {
            var projection = HasCurrentDepartmentBrowse(view) ? BuildPublicDepartmentProjection(view, departmentBrowseEnterpriseId) : null;
            if (projection == null)
            {
                publicDepartments.ClearPresentation();
                publicBoards?.ClearPresentation();
                departmentBrowseGameId = departmentBrowseEnterpriseId = null;
                return;
            }
            publicDepartments.Present(projection);
        }

        private void ReturnToPublicEnterprises()
        {
            var view = VisibleView();
            bool valid = HasCurrentDepartmentBrowse(view) && BuildPublicDepartmentProjection(view, departmentBrowseEnterpriseId) != null;
            if (valid) publicDepartments?.ClearForNavigation();
            else publicDepartments?.ClearPresentation();
            departmentBrowseGameId = departmentBrowseEnterpriseId = null;
            if (!valid || publicBoards == null) { publicBoards?.ClearPresentation(); return; }
            publicBoards.BindSubmission(null, null, null);
            publicBoards.BindBrowseNavigation(OpenPublicDepartments, null);
            publicBoards.BindGameplayText(ResolveEnterpriseDetails, RenderEnterpriseDetails);
            publicBoards.ResumeFromNavigation(BuildPublicEnterpriseProjection(view));
        }

        public string ItemName(string definitionId) => GetCharacterDefinition(definitionId)?.Name ??
            YC.Domain.Facilities.FacilityCardDatabase.Get(definitionId)?.Name ?? Name(definitionId);

        public string MapCandidateLabel(string id)
        {
            var zones = VisibleView()?.ItemZones.Zones;
            if (zones == null) return string.Empty;
            var zone = zones.Select(value => value.Zone).FirstOrDefault(value =>
                (value.TemplateId == "core.zone.map_location" || value.TemplateId == "core.zone.map_route") && value.LocalZoneId == id ||
                value.LegalSlotIds.Contains(id));
            var visited = new HashSet<string>();
            while (zone != null && visited.Add(zone.ZoneId))
            {
                if (zone.TemplateId == "core.zone.map_region") return string.Format(copy.MapCandidateFormat, id, zone.LocalZoneId);
                zone = zone.ParentKind == ZoneParentKind.Zone ? zones.Find(value => value.Zone.ZoneId == zone.ParentId)?.Zone : null;
            }
            return string.Empty;
        }

        public string RequestProgress(InteractionRequestProjection request)
        {
            var view = VisibleView();
            var current = view?.Effects.Find(value => value.EffectId == request.OwnerEffectId);
            var visited = new HashSet<string>();
            while (current != null && visited.Add(current.EffectId))
            {
                if (current.RequestedCount >= 0 && current.CompletedCount >= 0)
                    return "\n" + string.Format(copy.RepeatProgressFormat, current.CompletedCount, current.RequestedCount);
                current = view.Effects.Find(value => value.EffectId == current.ParentEffectId);
            }
            return string.Empty;
        }

        private string TokenDescription(MapTokenView token) => string.Format(copy.MapTokenFormat, ItemName(token.DefinitionId), string.IsNullOrEmpty(token.LocationId) ? token.RouteId : token.LocationId, token.RegionName) + "\n" +
            (token.TypeId == "infected.company_marker" ? copy.CompanyToll : token.IsPermanent ? copy.PermanentToken : token.ExpiresBeforeActionRoundSequence < 0 ? copy.LockExpiryUnknown :
                string.Format(copy.LockExpiryFormat, token.ExpiresBeforeActionRoundSequence));

        public void ShowOptionalInfluenceSkip(InteractionRequestProjection request, Action skip, Func<bool> isCurrent)
        {
            if (request == null || !request.VisibleToViewer || request.Status != "open" ||
                request.InteractionTypeId != "setup.influence.optional" || request.MinSelections != 0 ||
                !request.AllowDecline || request.AnsweringPlayerId != getPlayer?.Invoke() || skip == null || isCurrent == null)
                throw new ArgumentException("底部跳过入口必须绑定当前观察者的可选初始影响力请求。", nameof(request));
            if (settlementHost == null || settlementRowTemplate == null)
                throw new InvalidOperationException("缺少既有底部结算宿主或条目模板。");
            ReleaseOptionalInfluenceSkip();
            // 按值保存清理身份，调用方后续投影不能改变已绑定按钮的归属。
            var bound = new InteractionRequestProjection { InteractionId = request.InteractionId, StateRevision = request.StateRevision,
                CandidateSetId = request.CandidateSetId, CandidateSetVersion = request.CandidateSetVersion,
                AnsweringPlayerId = request.AnsweringPlayerId };
            optionalInfluenceRequest = bound;
            optionalInfluenceIsCurrent = isCurrent;
            optionalInfluencePending = false;
            optionalInfluenceSkipRow = Instantiate(settlementRowTemplate, settlementHost, false);
            optionalInfluenceSkipRow.name = "OptionalInitialInfluenceSkip";
            optionalInfluenceSkipRow.gameObject.SetActive(true);
            optionalInfluenceSkipRow.Bind(new UiEffectRowView.Binding
            {
                StableItemId = "setup.influence.skip", RequestId = bound.InteractionId, Revision = bound.StateRevision,
                RowMode = UiEffectRowView.Mode.ChoiceExecute, RightStatus = UiEffectRowView.Status.Use,
                Enabled = true, Title = string.Format(copy.CompactSettlementFormat, copy.OptionalInfluenceTitle, copy.SkipInitialInfluenceLabel)
            }, (_, __) => ReferenceEquals(optionalInfluenceRequest, bound) && OptionalInfluenceSkipCurrent(), null, _ =>
            {
                if (!ReferenceEquals(optionalInfluenceRequest, bound) || !OptionalInfluenceSkipCurrent()) return;
                optionalInfluencePending = true;
                optionalInfluenceSkipRow.Refresh();
                try { skip(); }
                catch
                {
                    ClearOptionalInfluenceSkip(bound.InteractionId, bound.StateRevision, bound.CandidateSetId,
                        bound.CandidateSetVersion, bound.AnsweringPlayerId);
                    throw;
                }
            });
            RefreshSettlementHostVisibility();
        }

        public void ClearOptionalInfluenceSkip(string interactionId, int revision, string candidateSetId, int candidateSetVersion, int playerId)
        {
            var bound = optionalInfluenceRequest;
            if (bound == null || bound.InteractionId != interactionId || bound.StateRevision != revision ||
                bound.CandidateSetId != candidateSetId || bound.CandidateSetVersion != candidateSetVersion || bound.AnsweringPlayerId != playerId) return;
            ReleaseOptionalInfluenceSkip();
        }

        private bool OptionalInfluenceSkipCurrent() => optionalInfluenceRequest != null && !optionalInfluencePending &&
            optionalInfluenceRequest.AnsweringPlayerId == getPlayer?.Invoke() && !GameplayHudFrame.EffectInputSuspended &&
            optionalInfluenceIsCurrent != null && optionalInfluenceIsCurrent();

        private static bool SameOptionalInfluenceRequest(InteractionRequestProjection bound, InteractionRequestProjection current) =>
            bound != null && current != null && bound.InteractionId == current.InteractionId &&
            bound.StateRevision == current.StateRevision && bound.CandidateSetId == current.CandidateSetId &&
            bound.CandidateSetVersion == current.CandidateSetVersion && bound.AnsweringPlayerId == current.AnsweringPlayerId;

        private void ReleaseOptionalInfluenceSkip()
        {
            optionalInfluenceRequest = null; optionalInfluenceIsCurrent = null; optionalInfluencePending = false;
            if (optionalInfluenceSkipRow != null)
            {
                optionalInfluenceSkipRow.ClearBinding(); EnterpriseBoardItemView.Retire(optionalInfluenceSkipRow.gameObject);
                optionalInfluenceSkipRow = null;
            }
            RefreshSettlementHostVisibility();
        }

        private void RefreshSettlementHostVisibility()
        {
            if (shuttingDown) return;
            bool visible = settlementStrip != null && settlementStrip.HasVisibleOptions || optionalInfluenceSkipRow != null;
            if (settlementHost != null && settlementHost.gameObject.activeSelf != visible) settlementHost.gameObject.SetActive(visible);
            if (hud != null) hud.Frame.SetSettlementButtonsVisible(visible);
        }
        private void OnSettlementButtonsVisibilityChanged(bool visible) => RefreshSettlementHostVisibility();

        private void RefreshSettlementBar(GameStateView view)
        {
            if (shuttingDown || settlementStrip == null || hud == null) return;
            bool hasOwnContext = view != null && view.ViewerRole == GameStateViewerRole.Player &&
                view.ViewerPlayerId >= 0 && view.ViewerPlayerId == getPlayer?.Invoke() &&
                !string.IsNullOrEmpty(view.GameId) && !string.IsNullOrEmpty(view.ContentPackHash);
            hud.Frame.BindGameplaySnapshot(hasOwnContext ? view : null);
            if (!hasOwnContext || !hud.Frame.GameplayPresentationReady)
            {
                presentationBinding = new object();
                renderedSnapshot = null; contentKey = string.Empty; stripPresentationGeneration = -1;
                settlementStrip.ClearWindow();
                ReleaseOptionalInfluenceSkip();
                if (!string.IsNullOrEmpty(inspectedSettlementOptionId))
                { if (cardViewer != null) cardViewer.Dismiss(); ClearSourceInspection(); }
                RefreshSettlementHostVisibility();
                return;
            }
            settlementStrip.SetScope(view.GameId + ":" + view.ContentPackHash, view.ViewerPlayerId);
            if (string.IsNullOrEmpty(view.ActiveSettlementWindowId))
            { settlementStrip.ClearWindow(); RefreshSettlementHostVisibility(); return; }
            var options = view.CharacterSettlementOptions.Where(option => option.WindowId == view.ActiveSettlementWindowId &&
                option.VisibleToViewer && option.AnsweringPlayerId == view.ViewerPlayerId &&
                (option.EntryKind == "optional_insertion" || option.EntryKind == "cleanup_order"))
                .Select(SettlementOptionView.FromOption).ToList();
            if (CanInvokeSettlement != null)
                foreach (var option in options) option.IsEnabled = option.IsEnabled && CanInvokeSettlement(option);
            if (options.Count > 0) settlementHost.gameObject.SetActive(true);
            if (stripPresentationGeneration != hud.Frame.PresentationGeneration)
            {
                settlementStrip.ConfigurePresentation(hud.Frame.ResolveGameplayText, hud.Frame.RenderGameplayText,
                    hud.Frame.MeasureGameplayText, hud.Frame.ResolveGameplayIcon);
                stripPresentationGeneration = hud.Frame.PresentationGeneration;
            }
            settlementStrip.BindWindow(view.ActiveSettlementWindowId, options, InvokeSettlement, null, InspectSettlementSource);
            RefreshSettlementHostVisibility();
        }

        private void InspectSettlementSource(SettlementOptionView option)
        {
            if (!option.CanInspectSource || !SettlementCurrent(option, false) || string.IsNullOrEmpty(option.SourceDefinitionId)) return;
            var view = VisibleView(); var definition = option.SourceDefinitionId;
            var sprite = hud.DialogRegistry.CardVisualCatalog.GetCharacterFront(definition) ?? hud.DialogRegistry.CardVisualCatalog.GetFacility(definition);
            if (sprite == null) sprite = ExternalContentRuntime.GetArtworkSprite("award", definition) ?? ExternalContentRuntime.GetArtworkSprite("contract", definition);
            if (sprite == null) return;
            inspectedPlayer = view.ViewerPlayerId; inspectedGameId = view.GameId;
            inspectedSourceId = option.SourceItemId; inspectedDefinitionId = definition;
            inspectedSettlementOptionId = option.OptionId; inspectedSettlementWindowId = option.WindowId;
            inspectedSnapshotIsCurrent = GuardSnapshot(view);
            InspectedItemIds = string.IsNullOrEmpty(option.SourceItemId) ? Array.Empty<string>() : new[] { option.SourceItemId };
            if (cardViewer == null) cardViewer = hud.DialogRegistry.InstantiateCardViewer();
            cardViewer.OpenInspect(sprite, ClearSourceInspection, SourceInspectionIsCurrent);
        }

        private static bool IsNonInteractiveMapToken(MapTokenView token) =>
            token.TypeId == "infected.gavial_token" || token.TypeId == "infected.influence_lock_token";

        private void RefreshMapTokens(GameStateView view)
        {
            mapTokenGeometry.Clear();
            if (mapTokenHost == null || mapTokenTemplate == null) return;
            var ids = new HashSet<string>(view.MapTokens.Select(value => value.ItemId));
            foreach (string id in mapTokens.Keys.Where(id => !ids.Contains(id)).ToArray())
            { EnterpriseBoardItemView.Retire(mapTokens[id].gameObject); mapTokens.Remove(id); }
            foreach (var token in view.MapTokens)
            {
                // 帧间只保留已绑定的公开几何，不为相机移动重新投影整局。
                mapTokenGeometry.Add(new MapTokenView { ItemId = token.ItemId, LocationId = token.LocationId,
                    RouteId = token.RouteId, SlotId = token.SlotId });
                if (!mapTokens.TryGetValue(token.ItemId, out var label) || label == null)
                { label = Instantiate(mapTokenTemplate, mapTokenHost, false); label.name = token.TypeId == "infected.company_marker" ? "CompanyMarker" : token.IsPermanent ? "GavialToken" : "InfluenceLockToken"; mapTokens[token.ItemId] = label; }
                var button = label.GetComponent<Button>();
                button.onClick.RemoveAllListeners();
                if (IsNonInteractiveMapToken(token))
                {
                    button.enabled = false;
                    label.raycastTarget = false;
                }
                else button.onClick.AddListener(() => InspectMapToken(token.ItemId));
                label.text = token.TypeId == "infected.company_marker" ? ItemName(token.DefinitionId) : token.IsPermanent ? copy.MapPermanentLabel : copy.MapLockLabel;
            }
            PositionMapTokens();
        }

        public void InspectMapToken(string itemId)
        {
            var view = VisibleView(); var token = view?.MapTokens.Find(value => value.ItemId == itemId);
            if (token == null || IsNonInteractiveMapToken(token)) return;
            if (detailViewer == null) detailViewer = new EffectDialogShell(hud.DialogRegistry);
            var layout = hud.DialogRegistry.EffectDialogLayoutProfile;
            var panel = detailViewer.PreparePage(hud.Frame.ContentRect, "Map Object Details", "Map Object Details Panel",
                layout.SelectionPanelSize, layout.CharacterPanelPosition, true, false, true);
            var details = TokenDescription(token);
            foreach (var control in view.CompanyControl.Where(value => value.RegionId == token.RegionId))
                details += "\n" + PlayerName(view, ItemSubjectRef.Player(control.PlayerId)) + " · " +
                    string.Format(copy.CompanyControlFormat, token.RegionName, control.CompanyMarkerCount, control.Contribution, control.Remainder);
            EffectDialogShell.AddHeading(panel, ItemName(token.DefinitionId), details);
            panel.GetComponentInParent<EffectDialogShellView>().ConfigureBackgroundDismiss(() => detailViewer.Hide());
            InspectedItemIds = new[] { itemId };
        }

        private void PositionMapTokens()
        {
            if (mapTokenGeometry.Count == 0 || mapView == null || Camera.main == null || mapTokenHost == null) return;
            foreach (var token in mapTokenGeometry)
            {
                if (!mapTokens.TryGetValue(token.ItemId, out var label) || label == null) continue;
                var location = mapView.CoordinateSpace.Layout.Locations.Find(value => value.LocationId == token.LocationId);
                var slot = string.IsNullOrEmpty(token.SlotId) ? null : mapView.Buttons.FirstOrDefault(value => value.TargetId == token.SlotId);
                label.gameObject.SetActive((location != null || slot != null) && mapView.gameObject.activeInHierarchy);
                if (location == null && slot == null) continue;
                var screen = Camera.main.WorldToScreenPoint(mapView.CoordinateSpace.ToWorldPosition(slot == null ? location.NormalizedPosition : mapView.GetButtonPosition(slot), 0));
                var canvas = mapTokenHost.GetComponentInParent<Canvas>();
                var viewportCanvas = mapTokenViewport.GetComponentInParent<Canvas>();
                if (!RectTransformUtility.RectangleContainsScreenPoint(mapTokenViewport, screen,
                    viewportCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : viewportCanvas.worldCamera))
                { label.gameObject.SetActive(false); continue; }
                RectTransformUtility.ScreenPointToLocalPointInRectangle(mapTokenHost, screen,
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var local);
                int stackIndex = mapTokenGeometry.TakeWhile(value => value.ItemId != token.ItemId).Count(value => value.LocationId == token.LocationId && value.RouteId == token.RouteId);
                label.rectTransform.anchoredPosition = local + mapTokenOffset + new Vector2(0, stackIndex * label.rectTransform.rect.height);
            }
        }

        public void Clear()
        {
            presentationBinding = new object();
            ClearSourceInspection();
            if (cooperationOpenButton != null) cooperationOpenButton.onClick.RemoveListener(OpenPublicEnterprises);
            ReleaseOptionalInfluenceSkip();
            renderedSnapshot = detailSnapshot = null;
            enterpriseDetails.Clear(); enterpriseDetailsSnapshot = null;
            enterpriseDetailPack = enterpriseDetailInstalledPack = null;
            enterpriseDetailPackGameId = enterpriseDetailPackHash = null;
            detailEntries = Array.Empty<PlayerDetailEntry>();
            foreach (var row in rows.Values) if (row != null) { row.ClearBinding(); EnterpriseBoardItemView.Retire(row.gameObject); }
            rows.Clear();
            foreach (var row in quickRows.Values) if (row != null) EnterpriseBoardItemView.Retire(row.gameObject);
            quickRows.Clear();
            detailViewer?.Hide(); InspectedItemIds = Array.Empty<string>();
            pendingOptions.Clear(); pendingCharacters.Clear(); pendingSubmissions.Clear(); contentKey = string.Empty;
            stripPresentationGeneration = -1;
            if (settlementStrip != null) settlementStrip.ClearWindow();
            if (!shuttingDown && hud != null) hud.Frame.SetSettlementButtonsVisible(false);
            if (settlementHost != null) settlementHost.gameObject.SetActive(false);
            foreach (var token in mapTokens.Values) if (token != null) EnterpriseBoardItemView.Retire(token.gameObject);
            mapTokens.Clear();
            mapTokenGeometry.Clear();
            if (publicBoards != null) publicBoards.ClearPresentation();
            if (publicDepartments != null) publicDepartments.ClearPresentation();
            departmentBrowseGameId = departmentBrowseEnterpriseId = null;
            if (cardViewer != null) cardViewer.Dismiss();
        }
        private void LateUpdate()
        {
            if (shuttingDown) return;
            if (getPlayer != null && renderedPlayer != getPlayer()) Clear();
            PositionMapTokens();
        }
        private void OnDestroy() { BeginShutdown(); Clear(); if (publicBoards != null) Destroy(publicBoards.gameObject); if (publicDepartments != null) Destroy(publicDepartments.gameObject); if (cardViewer != null) Destroy(cardViewer.gameObject); }
    }
}
