using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Reflection;
using System.Linq;

namespace YC.Application.Sessions
{
    /// <summary>Host 本地持久化快照；该 DTO 严禁进入 Mirror/客户端消息。</summary>
    [Serializable]
    public sealed class HostSnapshotDto
    {
        public const int CurrentSchemaVersion = GameProtocolVersions.Host;

        public int SchemaVersion = CurrentSchemaVersion;
        public bool RandomStateRequired;
        public int StateRevision;
        public int LastCommitSequence;
        public string ContentHash = string.Empty;
        public string RuleContractVersion = YC.Domain.Rules.GameRuleConfiguration.CurrentRuleContractVersion;
        public string ActiveMainNodeId = string.Empty;
        public List<string> ActiveBlockerIds = new List<string>();
        public GameState State = new GameState();
    }

    /// <summary>
    /// Host-only append journal。schema4/5仅在每次完整追加的末条保存StateAfter；
    /// schema2/3仍按原逐条checkpoint读取。严禁进入网络或客户端投影。
    /// </summary>
    [Serializable]
    public sealed class HostJournalEntryDto
    {
        public int CommitSequence;
        public int StateRevision;
        public string CommitId = string.Empty;
        public string CommandId = string.Empty;
        public RuleJournalEntryKind Kind = RuleJournalEntryKind.Commit;
        public string EntityId = string.Empty;
        public string Detail = string.Empty;
        public string ContentId = string.Empty;
        public string ContentVersion = string.Empty;
        public string ContentHash = string.Empty;
        public string ReceiptId = string.Empty;
        public string EventId = string.Empty;
        public string SubscriptionId = string.Empty;
        public string HandlerId = string.Empty;
        public GameState StateAfter;
    }

    [Serializable]
    public sealed class HostJournalDto
    {
        public const int CurrentSchemaVersion = GameProtocolVersions.Journal;

        public int SchemaVersion = CurrentSchemaVersion;
        public List<HostJournalEntryDto> Entries = new List<HostJournalEntryDto>();
    }

    [Serializable]
    public sealed class HostSessionArchiveDto
    {
        public HostSnapshotDto Snapshot = new HostSnapshotDto();
        public HostJournalDto Journal = new HostJournalDto();
    }

    public sealed class HostRecoveryResult
    {
        internal HostRecoveryResult(bool succeeded, GameState state, string faultCode, string diagnostic)
        {
            Succeeded = succeeded;
            State = state;
            FaultCode = faultCode ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
        }

        public bool Succeeded { get; }
        public GameState State { get; }
        public string FaultCode { get; }
        public string Diagnostic { get; }
        public bool PausedFault { get { return !Succeeded; } }
    }

    /// <summary>
    /// Host snapshot + append journal 的唯一恢复入口。它先做结构/连续性/内容版本检查，
    /// 再选择最后一个已落盘 StateAfter；任何不确定性都返回 paused_fault，不猜测补段。
    /// </summary>
    public static partial class HostRecoveryService
    {
        public const string SnapshotInvalid = "snapshot_invalid";
        public const string JournalGap = "journal_gap";
        public const string JournalConflict = "journal_conflict";
        public const string ContentHashMismatch = "content_hash_mismatch";
        public const string UnknownHandler = "unknown_handler";
        public const string LegacyPendingUnsupported = "legacy_pending_unsupported";
        public const string ActiveNodeMissing = "active_node_missing";
        public const string BlockerReferenceMissing = "blocker_reference_missing";
        public const string ReceiptConflict = "receipt_conflict";

        private static readonly JsonSerializerSettings ArchiveSettings = new JsonSerializerSettings
        {
            ContractResolver = new HostFieldContractResolver(), TypeNameHandling = TypeNameHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            // 显式集合以快照原值为准；缺省字段仍保留 DTO 初始化语义。
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Converters = new List<JsonConverter> { new LegacyPositionListConverter() }
        };

        public static string SerializeArchive(HostSessionArchiveDto archive)
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            return JsonConvert.SerializeObject(archive, ArchiveSettings);
        }

        /// <summary>
        /// 沿原 Host 格式直接写入，不构造完整 JSON 字符串或重复校验档案。
        /// writer 由调用者持有并负责 flush/关闭；失败保留已写出的部分及原异常。
        /// </summary>
        public static void SerializeArchive(HostSessionArchiveDto archive, System.IO.TextWriter writer)
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            using (var jsonWriter = new JsonTextWriter(writer)
            {
                CloseOutput = false,
                // 失败时不再补写结束标记，避免第二次 writer 异常覆盖最初的失败。
                AutoCompleteOnClose = false
            })
            {
                JsonSerializer.CreateDefault(ArchiveSettings).Serialize(jsonWriter, archive);
            }
        }

        /// <summary>先核验原 JSON 字段存在性；初始化器不参与旧版本判断。</summary>
        public static HostRecoveryResult RecoverJson(string json, EffectRegistry registry = null, GameMapDefinition legacyMap = null, ItemZoneMigrationEvidence evidence = null)
        {
            try
            {
                var raw = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var snapshot = raw["Snapshot"] as JObject;
                var journal = raw["Journal"] as JObject;
                if (snapshot == null || journal == null || snapshot["SchemaVersion"] == null || journal["SchemaVersion"] == null)
                    return Fault(null, SnapshotInvalid, "缺少原始 SchemaVersion。");
                if (!HasCurrentProtocolEnvelope(snapshot, journal, out string protocolReason))
                    return Fault(null, protocolReason == GlobalVersionUnsupported ? GlobalVersionUnsupported : SnapshotInvalid, protocolReason);
                int snapshotVersion = snapshot.Value<int>("SchemaVersion");
                int journalVersion = journal.Value<int>("SchemaVersion");
                if ((snapshotVersion < 1 || snapshotVersion > HostSnapshotDto.CurrentSchemaVersion) || snapshotVersion != journalVersion)
                    return Fault(null, SnapshotInvalid, "Host schemaVersion 不受支持或不一致。");
                var states = new List<JObject> { snapshot["State"] as JObject };
                if (!(journal["Entries"] is JArray entries)) return Fault(null, SnapshotInvalid, "缺少 journal Entries。");
                foreach (var entry in entries)
                {
                    if (snapshotVersion >= 4 && !HasRawBatchJournalFields(entry as JObject))
                        return Fault(null, SnapshotInvalid, "批次journal缺少完整原始记录字段。");
                    if (snapshotVersion < 4 || entry["StateAfter"]?.Type != JTokenType.Null)
                        states.Add(entry["StateAfter"] as JObject);
                }
                if (snapshot["ContentHash"] == null || string.IsNullOrWhiteSpace(snapshot.Value<string>("ContentHash")) || snapshot["RuleContractVersion"] == null)
                    return Fault(null, ItemZoneStateMigrator.Unsupported, "缺少原局内容或规则版本。");
                // 历史checkpoint只校验/复制，不回答旧选择；保护的是本次最终安装状态。
                if (!LegacyPendingRecoveryPolicy.TryValidateRaw(states.LastOrDefault(), out string pendingDiagnostic))
                    return Fault(null, LegacyPendingUnsupported, pendingDiagnostic);
                foreach (var state in states)
                {
                    if (state == null) return Fault(null, ItemZoneStateMigrator.Unsupported, "缺少 StateAfter。");
                    if (!TryValidateRetiredCardMarkerRawState(state, out string retiredMarkerReason)) return Fault(null, SnapshotInvalid, retiredMarkerReason);
                    if (!HasCurrentStateProtocol(state)) return Fault(null, SnapshotInvalid, "host_state_protocol_or_required_fields_missing");
                    if (!HasRawAbilityActionState(state)) return Fault(null, SnapshotInvalid, "ability_action.v1:raw_fields_missing");
                    if (!HasRawPersistentSourceFacts(state, out string persistentFactsReason)) return Fault(null, SnapshotInvalid, persistentFactsReason);
                    if (!YC.Domain.Rules.EntrepreneurRecovery.HasRawFields(state)) return Fault(null, SnapshotInvalid, "entrepreneur_selection.v1:raw_fields_missing");
                    if (!UsageTrackRecovery.HasRawFields(state)) return Fault(null, SnapshotInvalid, "usage_tracks.v1:raw_fields_missing_or_legacy_naming");
                    if (!CleanupObligation.HasRawFields(state)) return Fault(null,SnapshotInvalid,"cleanup_obligation.v1:raw_fields_missing");
                    if (!CleanupSourceRecovery.HasRawFields(state)) return Fault(null, SnapshotInvalid, "cleanup_source.v1:raw_fields_missing");
                    if (!RewardGrantEffectExecutor.HasRawFields(state)) return Fault(null, SnapshotInvalid, "reward_grant.v1:raw_fields_missing");
                    if (!FinalScoringRecovery.ValidateRaw(state, out string finalRawReason)) return Fault(null, SnapshotInvalid, finalRawReason);
                    if (!HasRawRandomState(state) || snapshot.Value<bool?>("RandomStateRequired") == true &&
                        (state["RandomRuntime"] == null || state["RandomRuntime"].Type == JTokenType.Null))
                        return Fault(null, SnapshotInvalid, "random_state_invalid");
                    if (snapshotVersion >= 3 &&
                        (state["LaunchPlayerOrder"] is not JArray launchOrder || launchOrder.Any(id => id.Type != JTokenType.Integer)))
                        return Fault(null, SnapshotInvalid, "launch_player_order_missing");
                    if (snapshotVersion < 3 &&
                        (state["UseSeatTurnOrder"]?.Type != JTokenType.Boolean || state["Players"] is not JArray legacyPlayers ||
                         legacyPlayers.Any(player => player["PlayerId"]?.Type != JTokenType.Integer)))
                        return Fault(null, SnapshotInvalid, "legacy_player_order_missing");
                    if (!HasRawRouteTollFields(state, out string tollDiagnostic))
                        return Fault(null, SnapshotInvalid, tollDiagnostic);
                    if (!HasRawResourcePaymentFields(state))
                        return Fault(null, SnapshotInvalid, "付款准备缺少原始身份、代次或事件进度字段。");
                    if (registry?.SetupRecipe != null && (state["SetupRecipe"] is not JObject || state["ContentDefinitions"] is not JObject))
                        return Fault(null, SnapshotInvalid, "content_snapshot_missing：缺少原始本局配方或有效定义快照。");
                    if (state["EntitySchemaVersion"] == null)
                    {
                        if (evidence == null || legacyMap == null || evidence.ContentPackHash != snapshot.Value<string>("ContentHash") || evidence.RuleContractVersion != snapshot.Value<string>("RuleContractVersion") ||
                            !HasLegacyPositionFields(state)) return Fault(null, ItemZoneStateMigrator.Unsupported, "缺少原始实体字段或确切配方/牌序证据。");
                        ConvertLegacyLists(state);
                        var legacy = state.ToObject<GameState>(JsonSerializer.Create(ArchiveSettings));
                        legacy.EntitySchemaVersion = 0;
                        if (!ItemZoneStateMigrator.TryMigrate(legacy, legacyMap, evidence, out var migrated, out var reason)) return Fault(null, reason, "旧状态无法无歧义恢复。");
                        migrated.EffectRuntime.SchemaVersion = EffectRuntimeState.CurrentSchemaVersion;
                        state.Replace(JObject.FromObject(migrated, JsonSerializer.Create(ArchiveSettings)));
                    }
                    else
                    {
                        if (snapshotVersion < 2) return Fault(null, SnapshotInvalid, "新实体状态要求 Host schema2 或更新版本。");
                        if (state["RuleConfiguration"] is not JObject configuration || configuration["SchemaVersion"] == null || configuration["MapId"] == null || configuration["PlayerCount"] == null ||
                            configuration["RuleModeId"] == null || configuration["EnabledExpansionIds"] == null || configuration["ConfigurationRevision"] == null ||
                            configuration["ContentPackHash"] == null || configuration["RuleContractVersion"] == null)
                            return Fault(null, ItemZoneStateMigrator.Unsupported, "新状态缺少原始 RuleConfiguration。");
                        if (state.Value<int>("EntitySchemaVersion") != GameProtocolVersions.Entity || !(state["ItemZones"] is JObject entities) ||
                            entities["Items"] == null || entities["Zones"] == null || entities["Orders"] == null || entities["Relations"] == null ||
                            state["EffectRuntime"]?["SchemaVersion"]?.Type != JTokenType.Integer)
                            return Fault(null, SnapshotInvalid, "实体或 EffectRuntime 原始版本不受支持。");
                        if (!ValidateRawDeferredResponses((JObject)state["EffectRuntime"]) || !ValidateRawInfluenceProtocol((JObject)state["EffectRuntime"]))
                            return Fault(null, SnapshotInvalid, "旁路义务或实际挂载收据缺少原始字段。");
                        if (!HasEntityFields(entities)) return Fault(null, SnapshotInvalid, "ItemZones 缺少原始身份、主体、父区或顺序字段。");
                        if (state["EffectRuntime"]?["EffectNodes"] is JArray budgetNodes && budgetNodes.Any(node =>
                            (string)node["EffectTypeId"] == CityStyleSpecialActionEffectTypeIds.GrantMainActions &&
                            node["NormalizedArguments"]?["Properties"] is JArray arguments && arguments.Any(entry => (string)entry["Name"] == "allowedActionTypes")) &&
                            (state["Players"] is not JArray budgetPlayers || budgetPlayers.Any(player => player["RestrictedMainActionsThisTurn"] == null)))
                            return Fault(null, SnapshotInvalid, "受限主要行动缺少原始额度字段，不能恢复为任意行动。");
                        if (state["InfluenceLocks"] == null)
                        {
                            if (((JArray)entities["Items"]).Any(item => (string)item["TypeId"] == "infected.influence_lock_token")) return Fault(null, SnapshotInvalid, "锁定指示物缺少原始绑定与到期记录。");
                            state["InfluenceLocks"] = new JArray();
                        }
                        if (state["InfluenceLocks"] is not JArray locks || locks.Any(item => new[] { "LockId", "SourceItemId", "BoundCityItemId", "ExpiresBeforeActionRoundSequence", "Active" }.Any(field => item[field] == null)))
                            return Fault(null, SnapshotInvalid, "锁定指示物的绑定或到期字段不完整。");
                        if (!HasCharacterLifecycleFields(state, entities)) return Fault(null, SnapshotInvalid, "角色生命周期缺少原始实例、模式、盖牌槽或消费字段，不能默认补齐。");
                        if (state["EffectRuntime"]?["EffectNodes"] is JArray nodes && nodes.Any(node => node["ContinuationData"] == null))
                            return Fault(null, ItemZoneStateMigrator.Unsupported, "节点缺少 ContinuationData；不能默认补齐未完成节点。");
                        if (state["EffectRuntime"]?["EffectNodes"] is JArray mapNodes && mapNodes.Any(node => (string)node["DefinitionVersion"] == "v1" &&
                            new[] { InfluenceEffectTypeIds.PlaceInfluence, InfluenceEffectTypeIds.RemoveInfluence, InfluenceEffectTypeIds.MoveInfluence, InfluenceEffectTypeIds.ReplaceInfluence, LuaDomainEffectTypeIds.PlaceRoad, LuaDomainEffectTypeIds.OperateToken, ContentPrimitiveEffectExecutor.SelectInfluence }.Contains((string)node["EffectTypeId"]) && node["PositionChanges"] is not JArray))
                            return Fault(null, SnapshotInvalid, "地图节点缺少原始位置结果，不能默认补齐。");
                        if (state["EffectRuntime"]?["EffectNodes"] is JArray enterpriseNodes && enterpriseNodes.Any(node =>
                            YC.Domain.Enterprises.EnterpriseEffectExecutor.IsEnterpriseType((string)node["EffectTypeId"]) && (string)node["DefinitionVersion"] == "v1" &&
                            (node["EnterpriseArrivalReceipts"] is not JArray arrivals || node["FrozenEnterpriseSpecials"] is not JArray frozen ||
                             arrivals.Any(receipt => new[] { "EventId", "EnterpriseItemId", "DepartmentItemId", "RewardAbilityId", "PlayerId", "OldLevel", "NewLevel" }.Any(key => receipt[key] == null)) ||
                             frozen.Any(selection => selection["EnterpriseDefinitionId"] == null || selection["UnlockedByPlayerId"] == null || selection["Candidate"] is not JObject candidate ||
                                 new[] { "CandidateId", "EnterpriseItemId", "SpecialId", "CurrentLevel", "EffectiveLevelCeiling", "DisplayNameKey" }.Any(key => candidate[key] == null)))))
                            return Fault(null, SnapshotInvalid, "企业节点缺少原始到达收据或冻结快照，不能默认补齐。");
                        if (state["EffectRuntime"]?["EffectNodes"] is JArray resultNodes && resultNodes.Any(node => (string)node["DefinitionVersion"] == "v1" && new[] { ItemTransferEffectExecutor.TypeId, LuaDomainEffectTypeIds.MoveFacilityCard, LuaDomainEffectTypeIds.MoveCharacterCard, DeckShuffleEffectExecutor.TypeId, LuaDomainEffectTypeIds.ShuffleFacilityDeck, LuaDomainEffectTypeIds.Repeat }.Contains((string)node["EffectTypeId"]) && (node["PositionChanges"] == null || node["ShuffleInputItemIds"] == null)))
                            return Fault(null, ItemZoneStateMigrator.Unsupported, "节点缺少 Host 位置或洗牌结果，不能补默认结果。");
                    }
                    if (state["ItemZones"]?["FacilityLayerVersion"] == null)
                    {
                        var original = state.ToObject<GameState>(JsonSerializer.Create(ArchiveSettings));
                        if (!FacilityLayerRecovery.TryMigrateLegacy(state, original, out var layered, out string layerMigrationReason))
                            return Fault(null, SnapshotInvalid, layerMigrationReason);
                        var replacement = JObject.FromObject(layered, JsonSerializer.Create(ArchiveSettings));
                        state.RemoveAll(); foreach (var property in replacement.Properties().ToList()) state.Add(property.Name, property.Value.DeepClone());
                    }
                    if (!FacilityLayerRecovery.TryValidateRaw(state, out string layerRawReason))
                        return Fault(null, SnapshotInvalid, layerRawReason);
                }
                // 旧实体迁移维持原有流程；玩家环序在 Recover 的旧版本分支中确定性继承。
                snapshot["SchemaVersion"] = snapshotVersion < 3 ? 2 : snapshotVersion;
                journal["SchemaVersion"] = snapshot["SchemaVersion"];
                var archive = raw.ToObject<HostSessionArchiveDto>(JsonSerializer.Create(ArchiveSettings));
                return Recover(archive, registry);
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException || exception is InvalidOperationException || exception is OverflowException)
            {
                return Fault(null, SnapshotInvalid, "Host JSON 校验失败：" + exception.GetType().Name + ":" + exception.Message);
            }
        }

        private static bool HasRawResourcePaymentFields(JObject state)
        {
            if (state["EffectRuntime"]?["EffectNodes"] is not JArray nodes) return false;
            bool Fields(JObject value, params string[] names) => value != null && names.All(name => value.Property(name) != null);
            foreach (var node in nodes)
            {
                if (node["Payment"] is not JObject payment) continue;
                if ((int?)payment["PaymentProtocolVersion"] == 2)
                {
                    if (!ResourcePaymentRecovery.HasRawFields(payment, out _)) return false;
                    continue;
                }
                if ((int?)payment["PaymentProtocolVersion"] != 1) return false;
                if (!Fields(payment, "PaymentId", "OwnerEffectId", "PlayerId", "PaymentCost", "SubstitutionBudget", "SelectedSubstitution",
                    "IsCommitted", "IsCommitRequested", "BeforePaymentEventId", "BeforePaymentReady", "SourceKind", "RuleConditionOwnerEffectId",
                    "PaymentGeneration", "AllowsSubstitution", "Preparation")) return false;
                if (payment["Preparation"] is JObject preparation && (!Fields(preparation, "PreparationOwnerEffectId", "ActionKind", "TargetId",
                    "OperationWindowId", "WindowGeneration", "ActionArguments", "IsPromoted", "IsCancelled", "BusinessBeforeEventId", "BusinessBeforeReady",
                    "SourceLocationId", "PromotedActionEffectId") || !HasRawNormalizedFields(preparation["ActionArguments"] as JObject, 0))) return false;
            }
            return true;
        }

        private static bool ValidateRawDeferredResponses(JObject runtime)
        {
            if (runtime["DispatchReceipts"] is not JArray receipts) return false;
            if (runtime.Value<int>("SchemaVersion") == 2)
            {
                if (runtime["DeferredResponses"] != null && (runtime["DeferredResponses"] is not JArray legacy || legacy.Count != 0) ||
                    receipts.Any(r => r["ActualAttachmentKind"] != null && (r["ActualAttachmentKind"].Type != JTokenType.Integer || (int)r["ActualAttachmentKind"] != 0) ||
                        r["DeferredResponseIds"] != null && (r["DeferredResponseIds"] is not JArray list || list.Count != 0))) return false;
                runtime["SchemaVersion"] = EffectRuntimeState.CurrentSchemaVersion;
                runtime["DeferredResponses"] = new JArray();
                foreach (var receipt in receipts.OfType<JObject>())
                { receipt["ActualAttachmentKind"] = 0; receipt["DeferredResponseIds"] = new JArray(); }
                return true;
            }
            if (runtime["DeferredResponses"] is not JArray responses || receipts.Any(r => r["ActualAttachmentKind"]?.Type != JTokenType.Integer || r["DeferredResponseIds"] is not JArray)) return false;
            var texts = new[] { "DeferredResponseId", "ResponseRootEffectId", "SourceEventId", "SourceDepartureEffectId", "SourceCardInstanceId", "AbilityId",
                "OriginMainOperationWindowId", "ReservedItemId", "ReservationZoneId", "OriginalSupplyZoneId", "DestinationHandlerId", "ResolvedDestinationZoneId" };
            return responses.All(r => texts.All(key => r[key]?.Type == JTokenType.String) &&
                new[] { "AnsweringPlayerId", "MainWindowPlayerIdAtCreation", "Status" }.All(key => r[key]?.Type == JTokenType.Integer));
        }

        private static bool HasRawBatchJournalFields(JObject entry)
        {
            if (entry == null || entry["StateAfter"] == null ||
                entry["StateAfter"].Type != JTokenType.Null && entry["StateAfter"].Type != JTokenType.Object)
                return false;
            foreach (string field in new[] { "CommitSequence", "StateRevision", "Kind" })
                if (entry[field]?.Type != JTokenType.Integer) return false;
            foreach (string field in new[] { "CommitId", "CommandId", "EntityId", "Detail", "ContentId",
                "ContentVersion", "ContentHash", "ReceiptId", "EventId", "SubscriptionId", "HandlerId" })
                if (entry[field]?.Type != JTokenType.String) return false;
            return true;
        }

        // 从原始JSON检查实际字段；IntegerValue=0等初始化器不能替代存档中的零值证据。
        private static bool HasRawRouteTollFields(JObject state, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (!(state["EffectRuntime"]?["EffectNodes"] is JArray nodes)) return true;
            foreach (var node in nodes.OfType<JObject>())
            {
                var arguments = node["NormalizedArguments"] as JObject;
                var continuation = node["ContinuationData"] as JObject;
                var protocolValue = RawNormalizedField(arguments, "paymentProtocol");
                var receiptValue = RawNormalizedField(continuation, YC.Domain.Travel.RouteTollEscrowService.ReceiptField);
                bool newStage = (string)node["FlowStage"] == YC.Domain.Travel.RouteTollEscrowService.AwaitingEventStage;
                if (protocolValue == null && receiptValue == null && !newStage) continue;
                if ((string)node["EffectTypeId"] != ExplorationEffectTypeIds.Explore ||
                    !HasRawNormalizedFields(arguments, 0) || !HasRawNormalizedFields(continuation, 0) ||
                    protocolValue?["Kind"]?.Value<int>() != (int)NormalizedValueKind.String ||
                    (string)protocolValue["StringValue"] != YC.Domain.Travel.RouteTollEscrowService.Protocol ||
                    node["FlowStage"]?.Type != JTokenType.String || node["Status"]?.Type != JTokenType.Integer)
                {
                    diagnostic = "route_toll_raw_protocol_invalid"; return false;
                }
                foreach (string name in new[] { "selectionOwnerEffectId", "selectionSessionId", "selectionRevision",
                    "commitmentId", "targetLocation", "influenceSlot", "allowFacilityEntry", "consumeMainAction" })
                {
                    if (RawNormalizedField(arguments, name) == null)
                    { diagnostic = "route_toll_raw_arguments_missing:" + name; return false; }
                }
                if (receiptValue == null)
                {
                    if (newStage || !string.IsNullOrEmpty((string)node["FlowStage"]))
                    { diagnostic = "route_toll_raw_receipt_missing"; return false; }
                    continue;
                }
                if (receiptValue["Kind"]?.Value<int>() != (int)NormalizedValueKind.Object)
                { diagnostic = "route_toll_raw_receipt_invalid"; return false; }
                foreach (string name in new[] { "reservationId", "ownerEffectId", "selectionOwnerEffectId", "selectionSessionId",
                    "selectionRevision", "payerPlayerId", "startAvailableGoldVoucher", "originalAmount", "escrowAmount", "state",
                    "frozenPayments", "settlementReceiptId", "releaseReceiptId" })
                {
                    if (RawNormalizedField(receiptValue, name) == null)
                    { diagnostic = "route_toll_raw_receipt_field_missing:" + name; return false; }
                }
            }
            return true;
        }

        private static JObject RawNormalizedField(JObject value, string name)
        {
            if (!(value?["Properties"] is JArray properties)) return null;
            return properties.OfType<JObject>().FirstOrDefault(entry => (string)entry["Name"] == name)?["Value"] as JObject;
        }

        internal static bool HasRawNormalizedFields(JObject value, int depth)
        {
            if (depth > NormalizedValue.MaxDepth || value?["Kind"]?.Type != JTokenType.Integer) return false;
            int kind = value["Kind"].Value<int>();
            if (kind == (int)NormalizedValueKind.Null) return true;
            if (kind == (int)NormalizedValueKind.Boolean) return value["BooleanValue"]?.Type == JTokenType.Boolean;
            if (kind == (int)NormalizedValueKind.Integer) return value["IntegerValue"]?.Type == JTokenType.Integer;
            if (kind == (int)NormalizedValueKind.String) return value["StringValue"]?.Type == JTokenType.String;
            if (kind == (int)NormalizedValueKind.StableReference)
                return value["ReferenceType"]?.Type == JTokenType.String && value["ReferenceId"]?.Type == JTokenType.String;
            if (kind == (int)NormalizedValueKind.Array)
                return value["Items"] is JArray items && items.All(item => item is JObject obj && HasRawNormalizedFields(obj, depth + 1));
            if (kind != (int)NormalizedValueKind.Object || !(value["Properties"] is JArray properties)) return false;
            return properties.All(entry => entry is JObject obj && obj["Name"]?.Type == JTokenType.String &&
                obj["Value"] is JObject child && HasRawNormalizedFields(child, depth + 1));
        }

        private static bool TryValidateRouteTollState(GameState state, out string diagnostic)
        {
            diagnostic = string.Empty;
            var tollNodes = state.EffectRuntime.EffectNodes.Where(node => node != null &&
                (ItemTransferEffectExecutor.Value(node.ContinuationData, YC.Domain.Travel.RouteTollEscrowService.ReceiptField) != null ||
                 node.FlowStage == YC.Domain.Travel.RouteTollEscrowService.AwaitingEventStage)).ToList();
            if (tollNodes.Count == 0) return true;
            IMapQueryService map;
            try { map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); }
            catch (ArgumentException) { diagnostic = "route_toll_map_restore_unsupported"; return false; }
            var escrow = new YC.Domain.Travel.RouteTollEscrowService(map);
            if (!escrow.TryValidateStateBase(state, out diagnostic)) return false;
            var authority = new YC.Domain.Travel.RouteSelectionSessionAuthority(map);
            foreach (var owner in tollNodes)
            {
                if (!YC.Domain.Travel.RouteTollEscrowService.TryReadReceipt(owner, out var receipt, out diagnostic)) return false;
                var resolved = authority.TryResolveFinalized(state, receipt.SelectionOwnerEffectId,
                    out var session, out var commitment);
                if (!resolved.IsValid) { diagnostic = resolved.Reason; return false; }
                if (session.SessionId != receipt.SelectionSessionId || session.StartGoldVoucherAmount != receipt.StartAvailableGoldVoucher ||
                    session.PlayerId != receipt.PayerPlayerId || commitment.AcceptedAtStateRevision != receipt.SelectionRevision ||
                    commitment.CommitmentId != ItemTransferEffectExecutor.Text(owner.NormalizedArguments, "commitmentId") ||
                    commitment.AcceptedPreview?.PaymentPlan?.Lines == null ||
                    commitment.AcceptedPreview.PaymentPlan.TotalGoldVoucherAmount != receipt.OriginalAmount ||
                    commitment.AcceptedPreview.PaymentPlan.Lines.Count != receipt.FrozenPayments.Items.Count)
                { diagnostic = "route_toll_frozen_selection_mismatch"; return false; }
                for (int i = 0; i < receipt.FrozenPayments.Items.Count; i++)
                {
                    var frozen = receipt.FrozenPayments.Items[i]; var line = commitment.AcceptedPreview.PaymentPlan.Lines[i];
                    if (ItemTransferEffectExecutor.Value(frozen, "routeId").ReferenceId != line.RouteId ||
                        ItemTransferEffectExecutor.Text(frozen, "receiverSubjectKey") != line.ReceiverSubjectKey ||
                        ItemTransferEffectExecutor.Number(frozen, "amount") != line.GoldVoucherAmount)
                    { diagnostic = "route_toll_frozen_payment_mismatch"; return false; }
                }
                var selection = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == receipt.SelectionOwnerEffectId);
                var record = ItemTransferEffectExecutor.Value(selection.ContinuationData, "routeSelection");
                if (ItemTransferEffectExecutor.Text(record, "consumerEffectId") != owner.EffectId)
                { diagnostic = "route_toll_claim_restore_mismatch"; return false; }
            }
            return true;
        }

        // 只识别并拒绝已退役wire；不保留装载/付款保留/交付执行器，也不改归档或捕获图。
        private static bool TryValidateRetiredCardMarkerRawState(JObject state, out string diagnostic)
        {
            diagnostic = "";
            string NodeReason(JObject node)
            {
                if (node.Value<string>("EffectTypeId") == ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId)
                    return ContentPrimitiveEffectExecutor.RetiredMarkerLoadReason;
                if (node["FixedDelivery"] is JToken receipt && receipt.Type != JTokenType.Null)
                    return ContentPrimitiveEffectExecutor.RetiredFixedDeliveryReason;
                var binding = node["Payment"] is JObject payment ? payment["FixedDeliveryReceiptId"] : null;
                if (binding != null && binding.Type != JTokenType.Null && (binding.Type != JTokenType.String || binding.Value<string>() != ""))
                    return ContentPrimitiveEffectExecutor.RetiredFixedDeliveryReason;
                if (node["NormalizedArguments"]?["Properties"] is JArray properties && properties.OfType<JObject>().Any(p =>
                    p.Value<string>("Name") == "fixedDeliveryRequirement" || p.Value<string>("Name") == "deliveryRef"))
                    return ContentPrimitiveEffectExecutor.RetiredFixedDeliveryReason;
                if (node["NestedEffects"] is JArray nested)
                    foreach (var child in nested.OfType<JObject>()) { string found = NodeReason(child); if (found.Length > 0) return found; }
                return "";
            }
            if (state?["EffectRuntime"]?["EffectNodes"] is JArray nodes)
                foreach (var node in nodes.OfType<JObject>()) { diagnostic = NodeReason(node); if (diagnostic.Length > 0) return false; }
            if (state?["ItemZones"]?["Zones"] is JArray zones && zones.OfType<JObject>().Any(z => z.Value<string>("LocalZoneId") == "fixed_delivery"))
            { diagnostic = ContentPrimitiveEffectExecutor.RetiredFixedDeliveryReason; return false; }
            return true;
        }

        private static bool HasLegacyPositionFields(JObject state)
        {
            if (state["Players"] is not JArray players || state["Decks"] is not JObject decks || state["Map"] is not JObject map) return false;
            return players.All(player => new[] { "PlayerId", "HandCardIds", "DiscardCardIds", "CoveredCharacterCardId", "InfluenceSupply", "CityLocationId", "HasScoreTrackMarker" }.All(field => player[field] != null)) &&
                new[] { "FacilityDeck", "FacilitySupply", "CityStyleSupply", "CharacterDeck", "EventDeckGreen", "EventDeckYellow", "EventDeckRed", "CardPools" }.All(field => decks[field] != null) &&
                new[] { "Influences", "Facilities", "RoadRouteIds", "ResourceTokens" }.All(field => map[field] != null);
        }
        private static bool HasEntityFields(JObject entities)
        {
            bool Fields(JToken value, params string[] names) => value is JObject obj && names.All(name => obj.Property(name) != null);
            if (!(entities["Items"] is JArray items) || !(entities["Zones"] is JArray zones) || !(entities["Orders"] is JArray orders) || !(entities["Relations"] is JArray relations)) return false;
            return items.All(item => Fields(item, "ItemId", "DefinitionId", "TypeId", "PhysicalKind", "ZoneId", "SlotId", "OwnerSubject", "ControllerSubject", "LocalState") &&
                Fields(item["OwnerSubject"], "Kind", "PlayerId", "CompanyId") && Fields(item["ControllerSubject"], "Kind", "PlayerId", "CompanyId")) &&
                zones.All(zone => Fields(zone, "ZoneId", "TemplateId", "LocalZoneId", "ParentKind", "ParentId", "OwnerPlayerId", "VisibilityPolicyId", "OwnerEffectId", "LegalSlotIds")) &&
                orders.All(order => Fields(order, "ZoneId", "ItemIds", "OrderRevision")) && relations.All(relation => Fields(relation, "RelationId", "RelationTypeId", "SourceItemId", "TargetItemId"));
        }
        private static bool HasCharacterLifecycleFields(JObject state, JObject entities)
        {
            bool Fields(JToken value, params string[] names) => value is JObject obj && names.All(name => obj.Property(name) != null);
            string[] lists = { "CharacterUses", "CharacterCoverSlots", "FacilityCharacterBindings", "CharacterActionRoundPermissions" };
            if (lists.Any(name => state[name] == null))
            {
                // 无角色运行事实的旧实体存档才可明确迁入空集合；已有使用或盖牌不可猜槽/禁用/消费状态。
                if ((state["EffectRuntime"]?["EffectNodes"] as JArray)?.Any(node => (string)node["EffectTypeId"] == YC.Domain.Cards.CharacterLifecycleService.UseTypeId) == true ||
                    (entities["Items"] as JArray)?.Any(item => (entities["Zones"] as JArray)?.Any(zone => (string)zone["ZoneId"] == (string)item["ZoneId"] && (string)zone["TemplateId"] == "core.zone.character_covered") == true) == true) return false;
                foreach (string name in lists.Where(name => state[name] == null)) state[name] = new JArray();
            }
            if (lists.Any(name => state[name] is not JArray)) return false;
            return ((JArray)state["CharacterUses"]).All(use => Fields(use, "UseId", "OwnerEffectId", "CardInstanceId", "ExecutingPlayerId", "ActionRoundSequence", "ModeOrder", "CompletedModes", "DoubleUseGranted", "SourceConsumed", "Finished", "DoubleUseSourceItemId", "FinishDestinationZoneId", "Modes") &&
                (((int?)use["SelectionProtocolVersion"] ?? 1) < 3 || Fields(use, "SkippedModes", "StopCommandId", "StoppedActivationEffectId")) &&
                use["ModeOrder"] is JArray && use["CompletedModes"] is JArray && use["Modes"] is JArray modes && modes.All(mode => Fields(mode, "Mode", "AbilityId", "SkillType", "Activated", "SubscriptionActive", "SettlementActionId", "TitleKey", "DescriptionKey"))) &&
                ((JArray)state["CharacterCoverSlots"]).All(slot => Fields(slot, "SlotId", "CardInstanceId", "SourceEffectId", "PlayerId", "BlockedActionRoundSequence", "AcceptedTypeId", "Reusable", "AllowedActionRounds")) &&
                ((JArray)state["FacilityCharacterBindings"]).All(binding => Fields(binding, "BindingId", "CharacterItemId", "FacilityItemId", "SourceEffectId", "ControllerPlayerId", "Active")) &&
                ((JArray)state["CharacterActionRoundPermissions"]).All(permission => Fields(permission, "PlayerId", "ExpiresActionRoundSequence", "SourceEffectId"));
        }
        private static void ConvertLegacyLists(JObject state)
        {
            if (state["Map"]?["FacilityInstances"] is JArray facilities)
                foreach (var facility in facilities.OfType<JObject>())
                    foreach (string field in new[] { "OwnerPlayerId", "CityBoardSlotIndex" })
                        if (facility[field] != null) { facility["Legacy" + field] = facility[field]; facility.Remove(field); }
            foreach (var obj in state.DescendantsAndSelf().OfType<JObject>().ToList())
            {
                foreach (string scalar in new[] { "InfluenceSupply", "CityLocationId", "CoveredCharacterCardId", "HasScoreTrackMarker" })
                    if (obj[scalar] != null) { obj["Legacy" + scalar] = obj[scalar]; obj.Remove(scalar); }
                foreach (string name in new[] { "HandCardIds", "DiscardCardIds", "BuiltFacilityIds", "RoadRouteIds", "RemovedFromGameCardIds", "Influences", "Facilities", "ResourceTokens", "CharacterDeck", "CharacterDiscard", "EventDeckGreen", "EventDeckYellow", "EventDeckRed", "FacilityDeck", "FacilitySupply", "CityStyleSupply" })
                    if (obj[name] is JArray array) obj[name] = new JObject { ["Values"] = array };
            }
        }
        private sealed class HostFieldContractResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType) => objectType.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsNotSerialized).Cast<MemberInfo>().ToList();
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization serialization)
            {
                var property = base.CreateProperty(member, serialization);
                // 旧档的标量只映射到兼容字段；新局位置仍由真实标记实例投影。
                if (member.DeclaringType == typeof(CityStyleDeclarationState))
                {
                    if (member.Name == nameof(CityStyleDeclarationState.LegacyMarkerArea)) property.PropertyName = "MarkerArea";
                    if (member.Name == nameof(CityStyleDeclarationState.LegacyRemainingSpecialActionUses)) property.PropertyName = "RemainingSpecialActionUses";
                }
                return property;
            }
        }
        private sealed class LegacyPositionListConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LegacyPositionList<>);
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteStartObject(); writer.WritePropertyName("Values");
                serializer.Serialize(writer, value.GetType().GetField("Values").GetValue(value));
                writer.WriteEndObject();
            }
            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                var raw = JObject.Load(reader);
                if (!(raw["Values"] is JArray)) throw new JsonSerializationException("LegacyPositionList.Values 必须为原始数组。");
                var instance = Activator.CreateInstance(type);
                var field = type.GetField("Values");
                field.SetValue(instance, raw["Values"].ToObject(field.FieldType, serializer));
                return instance;
            }
        }

        public static HostSessionArchiveDto CreateArchive(GameState state, string contentHash = "")
        {
            HostSnapshotDto snapshot = CreateSnapshot(state, contentHash);
            return new HostSessionArchiveDto
            {
                Snapshot = snapshot,
                Journal = new HostJournalDto()
            };
        }

        public static HostSnapshotDto CreateSnapshot(GameState state, string contentHash = "")
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            state = WithLegacyLaunchOrder(state);
            string diagnostic;
            if (!TryValidateHostState(state, null, out diagnostic, requireDeferredRegistry: false))
                throw new InvalidOperationException("无法创建 Host snapshot：" + diagnostic);

            EffectRuntimeState runtime = state.EffectRuntime;
            string resolvedContentHash = string.IsNullOrEmpty(contentHash) ? state.ContentPackHash : contentHash;
            if (ItemZoneLegacyProjection.IsActive(state) && resolvedContentHash != state.ContentPackHash)
                throw new InvalidOperationException(ContentHashMismatch);
            var snapshot = new HostSnapshotDto
            {
                RandomStateRequired = state.RandomRuntime != null,
                StateRevision = runtime.StateRevision,
                LastCommitSequence = GetLastCommitSequence(runtime),
                ContentHash = resolvedContentHash ?? string.Empty,
                ActiveMainNodeId = runtime.ActiveMainNodeId ?? string.Empty,
                State = GameStateCloneService.DeepClone(state)
            };
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && !blocker.IsResolved && !string.IsNullOrEmpty(blocker.BlockerId))
                    snapshot.ActiveBlockerIds.Add(blocker.BlockerId);
            }
            return snapshot;
        }

        /// <summary>把 snapshot 之后的新 runtime journal 记录追加到 Host journal。</summary>
        public static void AppendJournal(
            HostSessionArchiveDto archive,
            GameState committedState,
            string contentHash = "")
        {
            if (archive == null) throw new ArgumentNullException(nameof(archive));
            if (archive.Snapshot == null) throw new InvalidOperationException("Host archive 缺少 snapshot。");
            if (archive.Snapshot.SchemaVersion != GameProtocolVersions.Host || archive.Journal?.SchemaVersion != GameProtocolVersions.Journal)
                throw new InvalidOperationException("当前构建仅允许向Host5/Journal5基线追加；旧全局归档不支持自动迁移。");
            if (archive.Snapshot.State?.EffectRuntime == null ||
                archive.Snapshot.State.EffectRuntime.StateRevision != archive.Snapshot.StateRevision ||
                GetLastCommitSequence(archive.Snapshot.State.EffectRuntime) != archive.Snapshot.LastCommitSequence ||
                archive.Snapshot.State.EffectRuntime.NextCommitSequence != archive.Snapshot.LastCommitSequence + 1)
                throw new InvalidOperationException("Host snapshot的revision/commit边界无效。");
            if (committedState == null || committedState.EffectRuntime == null)
                throw new ArgumentNullException(nameof(committedState));
            var launchOrder = committedState.LaunchPlayerOrder != null && committedState.LaunchPlayerOrder.Count > 0
                ? committedState.LaunchPlayerOrder : new TurnOrderService().GetLaunchPlayerOrder(committedState).ToList();
            if (archive.Snapshot.State?.GameId != committedState.GameId ||
                archive.Snapshot.State.LaunchPlayerOrder == null || !archive.Snapshot.State.LaunchPlayerOrder.SequenceEqual(launchOrder))
                throw new InvalidOperationException("launch_player_order_changed");

            string expectedHash = archive.Snapshot.ContentHash ?? string.Empty;
            string actualHash = string.IsNullOrEmpty(contentHash) ? expectedHash : contentHash;
            if (!string.IsNullOrEmpty(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                throw new InvalidOperationException(ContentHashMismatch + "：本次提交内容哈希与 snapshot 不一致。");

            string diagnostic;
            if (!TryValidateJournalShape(archive.Snapshot, archive.Journal, out diagnostic))
                throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);
            GameState previousState = archive.Snapshot.State;
            var existingBatch = new List<HostJournalEntryDto>();
            for (int i = 0; i < archive.Journal.Entries.Count; i++)
            {
                HostJournalEntryDto existing = archive.Journal.Entries[i];
                existingBatch.Add(existing);
                if (existing.StateAfter == null) continue;
                if (!TryValidateBatchCheckpoint(existingBatch, previousState, existing.StateAfter, actualHash, out diagnostic))
                    throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);
                previousState = existing.StateAfter;
                existingBatch.Clear();
            }
            if (existingBatch.Count != 0)
                throw new InvalidOperationException(JournalConflict + "：原journal尾部缺少批末checkpoint。");
            int lastSequence = GetLastCommitSequence(previousState.EffectRuntime);
            var pending = new List<HostJournalEntryDto>();
            IList<RuleJournalEntry> source = committedState.EffectRuntime.Journal;
            for (int i = 0; i < source.Count; i++)
            {
                RuleJournalEntry entry = source[i];
                if (entry == null || entry.CommitSequence <= lastSequence) continue;
                if (entry.CommitSequence != lastSequence + 1)
                    throw new InvalidOperationException(JournalGap + "：追加 journal 缺少连续 commit 序号。");
                if (!string.IsNullOrEmpty(entry.ContentHash) &&
                    !string.Equals(entry.ContentHash, actualHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(ContentHashMismatch + "：journal 条目的内容哈希与 snapshot 不一致。");

                pending.Add(CreateJournalEntry(entry, committedState));
                lastSequence = entry.CommitSequence;
            }
            if (!TryValidateBatchCheckpoint(pending, previousState, committedState, actualHash, out diagnostic))
                throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);
            if (pending.Count == 0)
            {
                // 没有新journal也必须拒绝损坏的当前业务状态；只读校验，不制造checkpoint。
                if (!TryValidateHostState(committedState, null, out diagnostic, launchOrder, false))
                    throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);
                return;
            }

            // 所有元数据先在局部列表核验；只为本批真实最终状态克隆一次。
            var checkpoint = GameStateCloneService.DeepClone(committedState);
            checkpoint.LaunchPlayerOrder = new List<int>(launchOrder);
            if (!TryValidateHostState(checkpoint, null, out diagnostic, requireDeferredRegistry: false))
                throw new InvalidOperationException("无法追加 Host journal：" + diagnostic);
            pending[pending.Count - 1].StateAfter = checkpoint;
            archive.Journal.Entries.AddRange(pending);
        }

        public static HostRecoveryResult Recover(
            HostSessionArchiveDto archive,
            EffectRegistry registry = null)
        {
            if (archive == null)
                return Fault(null, SnapshotInvalid, "Host archive 为空。");
            if (archive.Snapshot == null)
                return Fault(null, SnapshotInvalid, "Host archive 缺少 snapshot。");
            if (archive.Snapshot.SchemaVersion != GameProtocolVersions.Host || archive.Journal?.SchemaVersion != GameProtocolVersions.Journal)
                return Fault(null, GlobalVersionUnsupported, "当前构建仅支持Host5/Journal5；旧归档须使用匹配原内容和执行器，不自动补造迁移事实。");
            // 只读预检最终恢复目标；历史已完成的旧选择不被重做，原日志/形状校验仍逐条执行。
            var recoveryTarget = archive.Snapshot.State;
            if (archive.Journal?.Entries != null)
                foreach (var checkpoint in archive.Journal.Entries)
                    if (checkpoint?.StateAfter != null) recoveryTarget = checkpoint.StateAfter;
            if (!LegacyPendingRecoveryPolicy.TryValidate(recoveryTarget, out string pendingDiagnostic))
                return Fault(recoveryTarget, LegacyPendingUnsupported, pendingDiagnostic);
            // 旧runtime及checkpoint的兼容校验可能规范化字段；只允许改独立恢复图。
            try
            {
                archive = GameStateCloneService.DeepClone(archive);
            }
            catch (Exception exception)
            {
                return Fault(archive.Snapshot.State, SnapshotInvalid, "Host archive 克隆失败：" + exception.Message);
            }
            if (archive.Snapshot.SchemaVersion == 2 && archive.Journal?.SchemaVersion == 2)
            {
                if (archive.Snapshot.State?.Players == null || archive.Journal.Entries == null ||
                    archive.Journal.Entries.Any(entry => entry?.StateAfter?.Players == null))
                    return Fault(null, SnapshotInvalid, "legacy_player_order_missing");
                archive.Snapshot.State = WithLegacyLaunchOrder(archive.Snapshot.State);
                foreach (var entry in archive.Journal.Entries) entry.StateAfter = WithLegacyLaunchOrder(entry.StateAfter);
                archive.Snapshot.SchemaVersion = 3;
                archive.Journal.SchemaVersion = 3;
            }
            if (registry != null && !string.IsNullOrEmpty(registry.ContentPackHash) && registry.ContentPackHash != archive.Snapshot.ContentHash)
                return Fault(archive.Snapshot.State, ContentHashMismatch, "注册表活动内容哈希与 snapshot 不一致。");
            if (ItemZoneLegacyProjection.IsActive(archive.Snapshot.State) && (archive.Snapshot.RuleContractVersion != archive.Snapshot.State.RuleConfiguration?.RuleContractVersion ||
                archive.Snapshot.ContentHash != archive.Snapshot.State.ContentPackHash))
                return Fault(archive.Snapshot.State, ContentHashMismatch, "snapshot 内容或规则版本与 State 不一致。");

            string diagnostic;
            if (!TryValidateSnapshot(archive.Snapshot, registry, out diagnostic))
                return Fault(archive.Snapshot.State, SnapshotInvalid, diagnostic);
            if (registry == null && ContainsHandlerState(archive.Snapshot.State))
                return Fault(archive.Snapshot.State, UnknownHandler, "恢复包含 Effect/continuation，但没有提供 handler registry。");

            HostJournalDto journal = archive.Journal ?? new HostJournalDto();
            if (journal.SchemaVersion != GameProtocolVersions.Journal ||
                journal.SchemaVersion != archive.Snapshot.SchemaVersion || journal.Entries == null)
                return Fault(archive.Snapshot.State, SnapshotInvalid, "Host journal schemaVersion 不受支持。");

            GameState recovered;
            try
            {
                recovered = GameStateCloneService.DeepClone(archive.Snapshot.State);
            }
            catch (Exception exception)
            {
                return Fault(archive.Snapshot.State, SnapshotInvalid, "snapshot 克隆失败：" + exception.Message);
            }

            int expectedSequence = archive.Snapshot.LastCommitSequence + 1;
            int previousRevision = archive.Snapshot.StateRevision;
            string previousCommitId = string.Empty;
            HostJournalEntryDto previous = null;
            var batch = new List<HostJournalEntryDto>();
            for (int i = 0; i < journal.Entries.Count; i++)
            {
                HostJournalEntryDto entry = journal.Entries[i];
                if (entry == null)
                    return Fault(recovered, JournalConflict, "Host journal 包含空记录。");
                if (entry.CommitSequence < expectedSequence)
                    return Fault(recovered, JournalConflict, "Host journal 包含重复或倒退的 commit 序号 " + entry.CommitSequence + "。");
                if (entry.CommitSequence > expectedSequence)
                    return Fault(recovered, JournalGap, "期望 commit 序号 " + expectedSequence + "，实际为 " + entry.CommitSequence + "。");
                if (string.IsNullOrEmpty(entry.CommitId) ||
                    (previous != null && entry.StateRevision == previousRevision &&
                     !string.Equals(entry.CommitId, previousCommitId, StringComparison.Ordinal)))
                    return Fault(recovered, JournalConflict, "同一 revision 的 journal 记录必须属于同一 commit。");
                if (entry.StateRevision < previousRevision || entry.StateRevision > previousRevision + 1)
                    return Fault(recovered, JournalGap, "journal stateRevision 不连续。");
                if (!string.Equals(archive.Snapshot.ContentHash ?? string.Empty,
                    journal.SchemaVersion >= 4 && string.IsNullOrEmpty(entry.ContentHash)
                        ? archive.Snapshot.ContentHash ?? string.Empty : entry.ContentHash ?? string.Empty, StringComparison.Ordinal))
                    return Fault(recovered, ContentHashMismatch, "journal 内容哈希与 snapshot 不一致。");
                if (journal.SchemaVersion >= 4)
                {
                    if (!HasBatchJournalMetadata(entry))
                        return Fault(recovered, JournalConflict, "批次journal元数据无效。");
                    batch.Add(entry);
                    previousRevision = entry.StateRevision;
                    previousCommitId = entry.CommitId;
                    previous = entry;
                    expectedSequence++;
                    if (entry.StateAfter == null) continue;
                }
                if (entry.StateAfter == null)
                    return Fault(recovered, JournalConflict, "journal 记录缺少已提交状态 checkpoint。");
                if (entry.StateAfter.GameId != archive.Snapshot.State.GameId || entry.StateAfter.LaunchPlayerOrder == null ||
                    !archive.Snapshot.State.LaunchPlayerOrder.SequenceEqual(entry.StateAfter.LaunchPlayerOrder))
                    return Fault(recovered, JournalConflict, "launch_player_order_changed");

                if (registry == null && ContainsHandlerState(entry.StateAfter))
                    return Fault(recovered, UnknownHandler, "恢复包含 Effect/continuation，但没有提供 handler registry。");

                if (journal.SchemaVersion >= 4 ?
                    !TryValidateBatchCheckpoint(batch, recovered, entry.StateAfter, archive.Snapshot.ContentHash, out diagnostic) ||
                    !TryValidateHostState(entry.StateAfter, registry, out diagnostic) :
                    !TryValidateJournalState(entry, registry, out diagnostic))
                    return Fault(recovered, JournalConflict, diagnostic);

                recovered = GameStateCloneService.DeepClone(entry.StateAfter);
                if (journal.SchemaVersion >= 4)
                {
                    batch.Clear();
                    continue;
                }
                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
                previous = entry;
                expectedSequence++;
            }

            if (batch.Count != 0)
                return Fault(recovered, JournalConflict, "journal尾部缺少完整批末checkpoint。");

            if (!TryValidateHostState(recovered, registry, out diagnostic))
                return Fault(recovered, SnapshotInvalid, diagnostic);

            EffectRuntimeState runtime = recovered.EffectRuntime;
            int recoveredLastSequence = GetLastCommitSequence(runtime);
            if (runtime.StateRevision != previousRevision || recoveredLastSequence != expectedSequence - 1)
                return Fault(recovered, JournalConflict, "恢复后的 runtime revision/commit 序号与 journal 不一致。");

            if (recovered.ContentDefinitions != null &&
                (recovered.ContentDefinitions.EnterpriseDefinitionIds == null || recovered.ContentDefinitions.EnterpriseDefinitionIds.Count == 0) &&
                registry?.ContentDefinitions?.EnterpriseDefinitionIds != null && registry.ContentPackHash == recovered.ContentPackHash)
                recovered.ContentDefinitions.EnterpriseDefinitionIds = new List<string>(registry.ContentDefinitions.EnterpriseDefinitionIds);

            // 只迁移旧v2尚未回答的中间页；全部归档验证通过后在恢复副本上正常推进。
            if (registry != null && recovered.CharacterUses.Any(use => use.SelectionProtocolVersion == 2 && !use.Finished &&
                recovered.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == use.OwnerEffectId && request.Status == "open" &&
                    request.InteractionTypeId == YC.Domain.Cards.CharacterLifecycleService.SecondModeInteractionTypeId)))
            {
                var migrated = new EffectTreeExecutor(recovered, registry).RunUntilQuiescent();
                if (migrated.Faulted) return Fault(recovered, SnapshotInvalid, recovered.EffectRuntime.LastFaultMessage);
            }

            return new HostRecoveryResult(true, recovered, string.Empty, string.Empty);
        }

        public static bool HasReceipt(GameState state, string eventId, string subscriptionId)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.DispatchReceipts == null)
                return false;
            for (int i = 0; i < state.EffectRuntime.DispatchReceipts.Count; i++)
            {
                DispatchReceipt receipt = state.EffectRuntime.DispatchReceipts[i];
                if (receipt != null && receipt.EventId == eventId && receipt.SubscriptionId == subscriptionId)
                    return true;
            }
            return false;
        }

        public static bool HasReceipt(GameState state, string receiptId)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.DispatchReceipts == null)
                return false;
            return state.EffectRuntime.DispatchReceipts.Exists(item => item != null && item.ReceiptId == receiptId);
        }

        private static HostJournalEntryDto CreateJournalEntry(
            RuleJournalEntry source,
            GameState committedState)
        {
            var result = new HostJournalEntryDto
            {
                CommitSequence = source.CommitSequence,
                StateRevision = source.StateRevision,
                CommitId = source.CommitId ?? string.Empty,
                CommandId = source.CommandId ?? string.Empty,
                Kind = source.Kind,
                EntityId = source.EntityId ?? string.Empty,
                Detail = source.Detail ?? string.Empty,
                ContentId = source.ContentId ?? string.Empty,
                ContentVersion = source.ContentVersion ?? string.Empty,
                ContentHash = source.ContentHash ?? string.Empty
            };
            if (source.Kind == RuleJournalEntryKind.DispatchReceipt)
            {
                result.ReceiptId = source.EntityId ?? string.Empty;
                DispatchReceipt receipt = committedState.EffectRuntime.DispatchReceipts.Find(
                    item => item != null && item.ReceiptId == result.ReceiptId);
                if (receipt != null)
                {
                    result.EventId = receipt.EventId ?? string.Empty;
                    result.SubscriptionId = receipt.SubscriptionId ?? string.Empty;
                    result.HandlerId = receipt.HandlerId ?? string.Empty;
                }
            }
            return result;
        }

        private static bool HasBatchJournalMetadata(HostJournalEntryDto entry)
        {
            return entry != null && entry.CommitSequence > 0 && entry.StateRevision > 0 &&
                !string.IsNullOrEmpty(entry.CommitId) && Enum.IsDefined(typeof(RuleJournalEntryKind), entry.Kind) &&
                entry.CommandId != null && entry.EntityId != null && entry.Detail != null &&
                entry.ContentId != null && entry.ContentVersion != null && entry.ContentHash != null &&
                entry.ReceiptId != null && entry.EventId != null && entry.SubscriptionId != null && entry.HandlerId != null &&
                (entry.Kind == RuleJournalEntryKind.DispatchReceipt ||
                 entry.ReceiptId.Length == 0 && entry.EventId.Length == 0 && entry.SubscriptionId.Length == 0 && entry.HandlerId.Length == 0);
        }

        private static bool SameJournalMetadata(RuleJournalEntry source, HostJournalEntryDto entry)
        {
            return source != null && HasBatchJournalMetadata(entry) &&
                source.CommitSequence == entry.CommitSequence && source.StateRevision == entry.StateRevision &&
                (source.CommitId ?? string.Empty) == entry.CommitId && (source.CommandId ?? string.Empty) == entry.CommandId &&
                source.Kind == entry.Kind && (source.EntityId ?? string.Empty) == entry.EntityId &&
                (source.Detail ?? string.Empty) == entry.Detail && (source.ContentId ?? string.Empty) == entry.ContentId &&
                (source.ContentVersion ?? string.Empty) == entry.ContentVersion && (source.ContentHash ?? string.Empty) == entry.ContentHash;
        }

        private static bool SameRuleJournalEntry(RuleJournalEntry left, RuleJournalEntry right)
        {
            if (left == null || right == null) return false;
            return left.CommitSequence == right.CommitSequence && left.StateRevision == right.StateRevision &&
                left.CommitId == right.CommitId && left.CommandId == right.CommandId && left.Kind == right.Kind &&
                left.EntityId == right.EntityId && left.Detail == right.Detail && left.ContentId == right.ContentId &&
                left.ContentVersion == right.ContentVersion && left.ContentHash == right.ContentHash &&
                JsonConvert.SerializeObject(left.Payload, ArchiveSettings) == JsonConvert.SerializeObject(right.Payload, ArchiveSettings);
        }

        // 不运行规则或捕获旧提交状态；精确核对一个批次及其已接受的完整journal前缀。
        private static bool TryValidateBatchCheckpoint(IList<HostJournalEntryDto> batch, GameState previousState,
            GameState checkpoint, string contentHash, out string diagnostic)
        {
            diagnostic = JournalConflict + "：批末checkpoint与完整journal不一致。";
            if (previousState?.RandomRuntime != null && (checkpoint == null || checkpoint.RandomRuntime == null ||
                checkpoint.EventDeckSeed != previousState.EventDeckSeed ||
                !GameplayRandomRuntime.IsContinuation(previousState.RandomRuntime, checkpoint.RandomRuntime, previousState.EventDeckSeed))) return false;
            var previousRuntime = previousState?.EffectRuntime;
            var runtime = checkpoint?.EffectRuntime;
            if (previousRuntime?.Journal == null || runtime?.Journal == null || runtime.DispatchReceipts == null ||
                checkpoint.GameId != previousState.GameId) return false;
            var launchOrder = checkpoint.LaunchPlayerOrder != null && checkpoint.LaunchPlayerOrder.Count > 0
                ? checkpoint.LaunchPlayerOrder : new TurnOrderService().GetLaunchPlayerOrder(checkpoint).ToList();
            if (previousState.LaunchPlayerOrder == null || !previousState.LaunchPlayerOrder.SequenceEqual(launchOrder))
            {
                diagnostic = "launch_player_order_changed";
                return false;
            }
            if (ItemZoneLegacyProjection.IsActive(checkpoint) && checkpoint.ContentPackHash != contentHash)
            {
                diagnostic = ContentHashMismatch + "：checkpoint内容哈希不一致。";
                return false;
            }
            int previousSequence = GetLastCommitSequence(previousRuntime);
            int lastSequence = batch.Count == 0 ? previousSequence : batch[batch.Count - 1].CommitSequence;
            int revision = batch.Count == 0 ? previousRuntime.StateRevision : batch[batch.Count - 1].StateRevision;
            if (runtime.StateRevision != revision || GetLastCommitSequence(runtime) != lastSequence ||
                runtime.NextCommitSequence != lastSequence + 1 ||
                runtime.Journal.Count != previousRuntime.Journal.Count + batch.Count) return false;
            for (int i = 0; i < previousRuntime.Journal.Count; i++)
                if (!SameRuleJournalEntry(previousRuntime.Journal[i], runtime.Journal[i])) return false;
            if (previousRuntime.DispatchReceipts == null) return false;
            foreach (var acceptedReceipt in previousRuntime.DispatchReceipts)
            {
                var currentReceipt = runtime.DispatchReceipts.Find(item => item != null &&
                    item.ReceiptId == acceptedReceipt?.ReceiptId);
                if (acceptedReceipt == null || currentReceipt == null ||
                    JsonConvert.SerializeObject(acceptedReceipt, ArchiveSettings) !=
                    JsonConvert.SerializeObject(currentReceipt, ArchiveSettings))
                {
                    diagnostic = ReceiptConflict + "：已接受的分发收据被后批改写。";
                    return false;
                }
            }

            int previousRevision = previousRuntime.StateRevision;
            string previousCommitId = previousRuntime.Journal.Count == 0 ? string.Empty :
                previousRuntime.Journal[previousRuntime.Journal.Count - 1]?.CommitId;
            for (int i = 0; i < batch.Count; i++)
            {
                var entry = batch[i];
                if (!HasBatchJournalMetadata(entry) || entry.CommitSequence != previousSequence + i + 1 ||
                    entry.StateRevision < previousRevision || entry.StateRevision > previousRevision + 1 ||
                    entry.StateRevision == previousRevision && entry.CommitId != previousCommitId ||
                    !SameJournalMetadata(runtime.Journal[previousRuntime.Journal.Count + i], entry)) return false;
                if (!string.IsNullOrEmpty(entry.ContentHash) && entry.ContentHash != (contentHash ?? string.Empty))
                {
                    diagnostic = ContentHashMismatch + "：批次记录内容哈希不一致。";
                    return false;
                }
                if (entry.Kind == RuleJournalEntryKind.DispatchReceipt)
                {
                    var receipt = runtime.DispatchReceipts.Find(item => item != null && item.ReceiptId == entry.ReceiptId);
                    if (string.IsNullOrEmpty(entry.ReceiptId) || entry.ReceiptId != entry.EntityId || receipt == null ||
                        (receipt.EventId ?? string.Empty) != entry.EventId ||
                        (receipt.SubscriptionId ?? string.Empty) != entry.SubscriptionId ||
                        (receipt.HandlerId ?? string.Empty) != entry.HandlerId)
                    {
                        diagnostic = ReceiptConflict + "：分发收据身份与批末checkpoint不一致。";
                        return false;
                    }
                }
                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
            }
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryValidateSnapshot(HostSnapshotDto snapshot, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (snapshot.SchemaVersion != GameProtocolVersions.Host ||
                snapshot.State == null || snapshot.State.EffectRuntime == null || snapshot.StateRevision < 0 ||
                snapshot.LastCommitSequence < 0)
            {
                diagnostic = "snapshot schema、revision 或状态为空。";
                return false;
            }

            if (snapshot.RandomStateRequired && snapshot.State.RandomRuntime == null)
            { diagnostic = "random_state_missing"; return false; }
            EffectRuntimeState runtime = snapshot.State.EffectRuntime;
            if (runtime.StateRevision != snapshot.StateRevision ||
                GetLastCommitSequence(runtime) != snapshot.LastCommitSequence ||
                runtime.NextCommitSequence != snapshot.LastCommitSequence + 1 ||
                !string.Equals(runtime.ActiveMainNodeId ?? string.Empty, snapshot.ActiveMainNodeId ?? string.Empty, StringComparison.Ordinal))
            {
                diagnostic = "snapshot revision、journal 序号或 active node 不一致。";
                return false;
            }

            HashSet<string> activeBlockers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker != null && !blocker.IsResolved) activeBlockers.Add(blocker.BlockerId ?? string.Empty);
            }
            if (snapshot.ActiveBlockerIds == null || snapshot.ActiveBlockerIds.Count != activeBlockers.Count)
            {
                diagnostic = "snapshot active blocker 引用不一致。";
                return false;
            }
            for (int i = 0; i < snapshot.ActiveBlockerIds.Count; i++)
            {
                if (!activeBlockers.Contains(snapshot.ActiveBlockerIds[i]))
                {
                    diagnostic = "snapshot 引用了未知 active blocker。";
                    return false;
                }
            }

            return TryValidateHostState(snapshot.State, registry, out diagnostic);
        }

        private static bool TryValidateJournalState(HostJournalEntryDto entry, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (entry.StateAfter == null || entry.StateAfter.EffectRuntime == null)
            {
                diagnostic = "journal checkpoint 缺少 EffectRuntime。";
                return false;
            }
            EffectRuntimeState runtime = entry.StateAfter.EffectRuntime;
            if (runtime.StateRevision != entry.StateRevision ||
                !runtime.Journal.Exists(item => item != null && item.CommitSequence == entry.CommitSequence &&
                    item.CommitId == entry.CommitId && item.EntityId == entry.EntityId))
            {
                diagnostic = "journal checkpoint 与记录的 commit/entity 不一致。";
                return false;
            }

            if (entry.Kind == RuleJournalEntryKind.DispatchReceipt)
            {
                if (string.IsNullOrEmpty(entry.ReceiptId))
                {
                    diagnostic = ReceiptConflict + "：分发收据 journal 缺少 receiptId。";
                    return false;
                }

                DispatchReceipt receipt = runtime.DispatchReceipts.Find(
                    item => item != null && item.ReceiptId == entry.ReceiptId);
                if (receipt == null ||
                    !string.Equals(receipt.EventId ?? string.Empty, entry.EventId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(receipt.SubscriptionId ?? string.Empty, entry.SubscriptionId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(receipt.HandlerId ?? string.Empty, entry.HandlerId ?? string.Empty, StringComparison.Ordinal))
                {
                    diagnostic = ReceiptConflict + "：分发收据身份与 checkpoint 不一致。";
                    return false;
                }
            }

            return TryValidateHostState(entry.StateAfter, registry, out diagnostic);
        }

        private static bool TryValidateJournalShape(
            HostSnapshotDto snapshot,
            HostJournalDto journal,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (snapshot == null || journal == null || journal.Entries == null)
            {
                diagnostic = JournalConflict + "：Host journal 集合为空。";
                return false;
            }

            int expectedSequence = snapshot.LastCommitSequence + 1;
            int previousRevision = snapshot.StateRevision;
            string previousCommitId = string.Empty;
            for (int i = 0; i < journal.Entries.Count; i++)
            {
                HostJournalEntryDto entry = journal.Entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.CommitId) ||
                    journal.SchemaVersion >= 4 && !HasBatchJournalMetadata(entry))
                {
                    diagnostic = JournalConflict + "：Host journal 包含空记录或缺少 commitId。";
                    return false;
                }
                if (entry.CommitSequence < expectedSequence)
                {
                    diagnostic = JournalConflict + "：Host journal 包含重复或倒退的 commit 序号。";
                    return false;
                }
                if (entry.CommitSequence > expectedSequence)
                {
                    diagnostic = JournalGap + "：Host journal 缺少连续 commit 序号。";
                    return false;
                }
                if (entry.StateRevision < previousRevision || entry.StateRevision > previousRevision + 1)
                {
                    diagnostic = JournalGap + "：Host journal stateRevision 不连续。";
                    return false;
                }
                if (entry.StateRevision == previousRevision &&
                    !string.IsNullOrEmpty(previousCommitId) &&
                    !string.Equals(entry.CommitId, previousCommitId, StringComparison.Ordinal))
                {
                    diagnostic = JournalConflict + "：同一 revision 的 journal 记录必须属于同一 commit。";
                    return false;
                }
                if (!string.Equals(snapshot.ContentHash ?? string.Empty,
                    journal.SchemaVersion >= 4 && string.IsNullOrEmpty(entry.ContentHash)
                        ? snapshot.ContentHash ?? string.Empty : entry.ContentHash ?? string.Empty, StringComparison.Ordinal))
                {
                    diagnostic = ContentHashMismatch + "：journal 内容哈希与 snapshot 不一致。";
                    return false;
                }

                previousRevision = entry.StateRevision;
                previousCommitId = entry.CommitId;
                expectedSequence++;
            }

            return true;
        }

        private static GameState WithLegacyLaunchOrder(GameState state)
        {
            if (state.LaunchPlayerOrder != null && state.LaunchPlayerOrder.Count > 0) return state;
            var copy = GameStateCloneService.DeepClone(state);
            if (copy.Players != null && copy.Players.All(player => player != null))
                copy.LaunchPlayerOrder = new List<int>(new TurnOrderService().GetLaunchPlayerOrder(copy));
            return copy;
        }

        private static bool TryValidateLaunchOrder(GameState state, IReadOnlyList<int> launchOrderOverride = null)
        {
            var launchOrder = launchOrderOverride ?? state.LaunchPlayerOrder;
            if (state.Players == null || state.Players.Any(player => player == null || player.PlayerId <= 0) ||
                !TurnOrderService.IsValidPlayerOrder(launchOrder, state.Players.Select(player => player.PlayerId))) return false;
            var runtime = state.EffectRuntime;
            if (runtime.PlayerOrderSnapshot == null) return false;
            var entranceSetups = runtime.MainNodes.Where(node => node != null &&
                node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup).ToList();
            if (entranceSetups.Count > 1 || entranceSetups.Any(node => node.RoundNumber != 1 ||
                node.ActionRound != 0 || node.PlayerId != -1 ||
                !launchOrder.Contains(node.StartPlayerId) ||
                node.RoundExecutionId == runtime.CurrentRoundExecutionId &&
                    node.StartPlayerId != runtime.CurrentRoundStartPlayerId ||
                runtime.MainNodes.Any(other => other != null && other.RoundExecutionId == node.RoundExecutionId &&
                    other.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance))) return false;
            // 独立 Effect 可持有自己的全员迭代顺序，但尚未创建回合主链。
            if (string.IsNullOrEmpty(runtime.CurrentRoundExecutionId))
                return entranceSetups.Count == 0 && (runtime.PlayerOrderSnapshot.Count == 0 ||
                    TurnOrderService.IsValidPlayerOrder(runtime.PlayerOrderSnapshot, launchOrder));
            if (runtime.PlayerOrderSnapshot.Count == 0) return false;
            if (!launchOrder.Contains(runtime.CurrentRoundStartPlayerId)) return false;
            var order = new TurnOrderService().GetTurnOrder(state, runtime.CurrentRoundStartPlayerId);
            if (!order.SequenceEqual(runtime.PlayerOrderSnapshot)) return false;
            foreach (var group in runtime.MainNodes.Where(node => node != null && node.RoundExecutionId == runtime.CurrentRoundExecutionId &&
                (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance || node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow ||
                 node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)).GroupBy(node => new { node.NodeTypeId, node.ActionRound }))
                if (group.Any(node => node.StartPlayerId != runtime.CurrentRoundStartPlayerId) ||
                    !order.SequenceEqual(group.OrderBy(node => node.MainlineIndex).Select(node => node.PlayerId))) return false;
            return true;
        }

        private static bool HasRawRandomState(JObject state)
        {
            // 旧档无状态原样保留；后续随机操作明确拒绝，不能猜测旧游标。
            if (state["RandomRuntime"] == null || state["RandomRuntime"].Type == JTokenType.Null) return true;
            if (state["GameId"]?.Type != JTokenType.String || state["EventDeckSeed"]?.Type != JTokenType.Integer ||
                state["RandomRuntime"] is not JObject random || random["GameId"]?.Type != JTokenType.String ||
                random["AlgorithmVersion"]?.Type != JTokenType.String || random["Streams"] is not JArray streams) return false;
            return streams.All(stream => stream is JObject && stream["StreamId"]?.Type == JTokenType.String &&
                stream["State"]?.Type == JTokenType.Integer && stream["WordsConsumed"]?.Type == JTokenType.Integer &&
                stream["RequestsConsumed"]?.Type == JTokenType.Integer);
        }

        private static bool HasRawAbilityActionState(JToken state)
        {
            var runtime = state["EffectRuntime"];
            bool usesNewNodes = runtime?["EffectNodes"] is JArray nodes && nodes.Any(n => ((string)n["EffectTypeId"] ?? "").StartsWith("effect.ability.", StringComparison.Ordinal));
            int version = runtime?.Value<int?>("AbilityActionProtocolVersion") ?? 0;
            if (version == 0) return !usesNewNodes;
            if (version != 1 || runtime["AbilityActionInvocations"] is not JArray invocations || runtime["AbilitySourceUses"] is not JArray uses) return false;
            bool Fields(JToken value, params string[] fields) => value is JObject && fields.All(field => value[field] != null && value[field].Type != JTokenType.Null);
            return invocations.All(i => Fields(i, "ProtocolVersion", "InvocationId", "OwnerEffectId", "CommandId", "CommandDigest", "OptionId", "ActionId", "OccupancyKey", "ParentEffectId", "CallPolicyRef", "Phase", "Outcome", "FailureReason", "FirstFaultPhase", "TrackBindingId", "UsageProviderVersion", "PaymentCommitted", "ActivationCommitted", "MainActionCommitted", "SourceRef", "VersionRef", "RequestRef", "PaymentEffectIds", "BodyEffectIds", "ReceiptIds") &&
                Fields(i["SourceRef"], "SourceKey", "ItemId", "DefinitionId", "AbilityId", "ActiveFaceId", "DeclarationVersion", "AuthorizedPlayerId", "AuthorizationRef", "EffectiveZoneId", "RuleOwnerItemId", "GrantingSourceRefs") &&
                Fields(i["VersionRef"], "ContractVersion", "ContentPackHash", "DefinitionVersion", "HandlerHash", "ExecutorVersion") &&
                Fields(i["RequestRef"], "InteractionId", "OwnerEffectId", "WindowId", "FactsDigest", "AnsweringPlayerId", "CandidateSetVersion", "RequestGeneration", "AllowedAnswers")) &&
                uses.All(i => Fields(i, "ProtocolVersion", "ReceiptId", "SourceKey", "ItemId", "AbilityId", "ScopeKey", "OwnerEffectId", "Outcome", "PlayerId", "SourceRef", "VersionRef", "TriggerEventId") &&
                    Fields(i["SourceRef"], "SourceKey", "ItemId", "DefinitionId", "AbilityId", "ActiveFaceId", "DeclarationVersion", "AuthorizedPlayerId", "AuthorizationRef", "EffectiveZoneId", "RuleOwnerItemId", "GrantingSourceRefs") &&
                    Fields(i["VersionRef"], "ContractVersion", "ContentPackHash", "DefinitionVersion", "HandlerHash", "ExecutorVersion"));
        }

        private static bool TryValidateHostState(GameState state, EffectRegistry registry, out string diagnostic,
            IReadOnlyList<int> launchOrderOverride = null, bool requireDeferredRegistry = true)
        {
            diagnostic = string.Empty;
            if (state == null || state.EffectRuntime == null)
            {
                diagnostic = "Host 状态缺少 EffectRuntime。";
                return false;
            }
            if (state.RandomRuntime != null && !GameplayRandomRuntime.TryValidate(state.RandomRuntime, state.GameId, state.EventDeckSeed, out diagnostic)) return false;
            if (state.Players == null || state.Players.Any(player => player.RestrictedMainActionsThisTurn < 0 || player.RestrictedMainActionsThisTurn > player.RemainingMainActionsThisTurn))
            { diagnostic = "main_action_budget_invalid"; return false; }
            if (!state.EffectRuntime.TryValidate(out diagnostic)) return false;
            if (!CleanupObligation.TryValidate(state,registry,out diagnostic)) return false;
            if (!AbilityActionRecovery.TryValidateAbilityActions(state, registry, out diagnostic)) return false;
            if (!YC.Domain.Cards.CharacterLifecycleService.TryValidatePersistentSourceFacts(state, registry, out diagnostic)) return false;
            if (!YC.Domain.Rules.EntrepreneurRecovery.TryValidateEntrepreneurState(state, registry, out diagnostic)) return false;
            if (!UsageTrackRecovery.Validate(state, registry, out diagnostic)) return false;
            if (ItemZoneLegacyProjection.IsActive(state) && !CubeAppearance.ValidateFrozen(state, out diagnostic)) return false;
            if (!OperationWindowService.TryValidate(state, out diagnostic)) return false;
            foreach (var owner in state.EffectRuntime.EffectNodes.Where(n => n.SettlementWindow != null))
                if (!SettlementEntryService.TryValidateWindow(state, owner.SettlementWindow, out diagnostic)) return false;
            if (!SettlementHostProtocol.TryValidateRecoveryOrigins(state, registry, out diagnostic)) return false;
            if (!TryValidateLaunchOrder(state, launchOrderOverride)) { diagnostic = "launch_player_order_invalid"; return false; }
            if (!RoundExecutionService.TryValidateSetupTasks(state, registry, out diagnostic)) return false;
            if (!RoundExecutionService.TryValidateParallelCoverTasks(state, registry, out diagnostic)) return false;
            if (!YC.Domain.Enterprises.EnterpriseEffectExecutor.TryValidateUpgradeSelections(state, registry?.EnterpriseDefinitions, out diagnostic)) return false;
            if (!YC.Domain.Enterprises.EnterpriseEffectExecutor.TryValidateUpgradeAuthorizations(state, out diagnostic)) return false;
            if (!YC.Domain.Effects.FacilityBuildEffectExecutor.TryValidateBuildSources(state, out diagnostic)) return false;
            if (!FacilityLayerRecovery.TryValidateState(state, registry, out diagnostic)) return false;
            if (!ResourcePaymentRecovery.TryValidate(state, registry, out diagnostic)) return false;
            if (!CleanupSourceRecovery.TryValidate(state, registry, out diagnostic)) return false;
            if (!YC.Domain.Travel.CollectionPlanRecordCodec.TryValidateRestoredPlans(state, registry, out diagnostic, requireRegistry: requireDeferredRegistry)) return false;
            if (!FinalScoringRecovery.TryValidate(state, registry, YC.Domain.Facilities.FacilityLayerEvidence.Validate, out diagnostic)) return false;
            if (!ResourceEffectExecutor.TryValidatePendingPayments(state, out diagnostic)) return false;
            if (!TryValidateRouteTollState(state, out diagnostic)) return false;
            if (!ContentPrimitiveEffectExecutor.TryValidateContentContinuations(state, out diagnostic)) return false;
            if (!YC.Domain.Cards.CharacterContentEffectExecutor.ValidateState(state)) { diagnostic = "character_content_restore_invalid"; return false; }
            if (!DiceRollEffectExecutor.ValidateState(state)) { diagnostic = "dice_receipt_invalid"; return false; }
            if (!YC.Domain.Influence.InfluenceLockRules.Validate(state)) { diagnostic = "influence_lock_state_invalid"; return false; }
            if (!ContentLifecycleEffectExecutor.TryValidate(state, out diagnostic)) return false;
            if (!RewardGrantEffectExecutor.TryValidate(state, registry, out diagnostic)) return false;
            if (!YC.Domain.Enterprises.EnterpriseDistinctUpgradeFlow.TryValidate(state, out diagnostic)) return false;
            if (state.ContentDefinitions != null && (state.ContentDefinitions.Components == null || state.ContentDefinitions.Components.Any(c => c == null || string.IsNullOrEmpty(c.DefinitionId) || string.IsNullOrEmpty(c.TypeId) || c.QuantityPolicy != "infinite" && c.QuantityPolicy != "finite") || state.ContentDefinitions.Components.Select(c => c.DefinitionId).Distinct().Count() != state.ContentDefinitions.Components.Count || state.ContentDefinitions.Components.Count(c => c.TypeId == "core.road_token") > 1))
            { diagnostic = "component_content_state_invalid"; return false; }
            if (state.EffectRuntime.SchemaVersion != EffectRuntimeState.CurrentSchemaVersion)
            { diagnostic = ItemZoneStateMigrator.Unsupported + ": EffectRuntime.SchemaVersion"; return false; }
            if (!InfluenceDepartureProtocol.Validate(state, out diagnostic) || !InfluencePlaceAttachment.Validate(state, registry, out diagnostic)) return false;
            if (state.EffectRuntime.DeferredResponses.Count > 0 && (registry != null || requireDeferredRegistry) &&
                !EffectTreeExecutor.TryValidateDeferredResponses(state, registry, out diagnostic)) return false;
            if (ItemZoneLegacyProjection.IsActive(state) && !new ItemZoneQuery().TryValidate(state, out diagnostic)) return false;
            if (!EffectContinuationContext.TryValidate(state.EffectRuntime, out diagnostic)) return false;
            if (!ContentTransactionEffectExecutor.TryValidateRuntime(state, out diagnostic)) return false;
            if (ItemZoneLegacyProjection.IsActive(state) && !YC.Domain.Cards.CharacterLifecycleService.TryValidate(state, out diagnostic)) return false;
            if (registry?.SetupRecipe != null && (state.SetupRecipe == null || state.ContentDefinitions == null)) { diagnostic = "content_snapshot_missing"; return false; }
            if (state.SetupRecipe != null)
            {
                try { state.SetupRecipe.Validate(); }
                catch (InvalidOperationException error) { diagnostic = error.Message; return false; }
                var storedContent = state.ContentDefinitions;
                if (storedContent != null &&
                    (storedContent.EnterpriseDefinitionIds == null || storedContent.EnterpriseDefinitionIds.Count == 0) &&
                    registry?.ContentDefinitions?.EnterpriseDefinitionIds != null && registry.ContentPackHash == state.ContentPackHash)
                {
                    storedContent = GameStateCloneService.DeepClone(storedContent);
                    storedContent.EnterpriseDefinitionIds = new List<string>(registry.ContentDefinitions.EnterpriseDefinitionIds);
                }
                if (storedContent == null || registry?.ContentDefinitions != null && JsonConvert.SerializeObject(storedContent) != JsonConvert.SerializeObject(registry.ContentDefinitions))
                { diagnostic = "content_snapshot_mismatch"; return false; }
                if (registry?.SetupRecipe != null && JsonConvert.SerializeObject(state.SetupRecipe) != JsonConvert.SerializeObject(registry.SetupRecipe)) { diagnostic = "setup_recipe_restore_mismatch"; return false; }
                foreach (var player in state.Players)
                    foreach (var declaration in player.DeclaredCityStyles)
                    {
                        var styleItem = state.ItemZones.Items.Find(i => i.ItemId == declaration.CityStyleItemId);
                        var marker = state.ItemZones.Items.Find(i => i.ItemId == declaration.InfluenceMarkerId);
                        int ordinal = player.DeclaredCityStyles.Where(d => d.CityStyleId == declaration.CityStyleId).ToList().IndexOf(declaration);
                        var track = state.ItemZones.Zones.Find(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == styleItem?.ItemId && z.LocalZoneId == "declaration_" + player.PlayerId + "_" + ordinal);
                        bool returned = track != null && state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentLifecycleEffectExecutor.ReturnMarkerTypeId && n.PlayerId == player.PlayerId &&
                            n.PositionChanges.Any(c => c.ItemId == declaration.InfluenceMarkerId && c.From.ZoneId == track.ZoneId &&
                                state.ItemZones.Zones.Any(z => z.ZoneId == c.To.ZoneId && z.LocalZoneId == "supply" && z.OwnerPlayerId == player.PlayerId)));
                        if (string.IsNullOrEmpty(declaration.DeclarationId) || styleItem?.DefinitionId != declaration.CityStyleId ||
                            marker?.TypeId != "core.player_marker" || marker.OwnerSubject.PlayerId != player.PlayerId || track == null || marker.ZoneId != track.ZoneId && !returned ||
                            declaration.UsedFacilityIds.Count != declaration.UsedCityBoardSlotIndexes.Count || declaration.UsedFacilityIds.Count != declaration.UsedFacilityIds.Distinct().Count() ||
                            declaration.UsedFacilityIds.Any(id => !state.ItemZones.Items.Any(i => i.ItemId == id && ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(i.TypeId, "core.facility_card"))))
                        { diagnostic = "city_style_declaration_restore_mismatch"; return false; }
                    }
            }
            if ((ItemZoneLegacyProjection.IsActive(state) || state.RuleConfiguration != null) && (state.RuleConfiguration == null || state.RuleConfiguration.SchemaVersion != YC.Domain.Rules.GameRuleConfiguration.CurrentSchemaVersion ||
                state.RuleConfiguration.MapId != state.MapId || state.RuleConfiguration.PlayerCount != state.Players.Count || state.RuleConfiguration.ContentPackHash != state.ContentPackHash ||
                state.RuleConfiguration.ConfigurationRevision < 0 ||
                state.RuleConfiguration.RuleContractVersion != YC.Domain.Rules.GameRuleConfiguration.CurrentRuleContractVersion))
            { diagnostic = "RuleConfiguration_restore_mismatch"; return false; }

            EffectRuntimeState runtime = state.EffectRuntime;
            if (!string.IsNullOrEmpty(runtime.FirstMainNodeId) &&
                !runtime.MainNodes.Exists(item => item != null && item.NodeId == runtime.FirstMainNodeId))
            {
                diagnostic = ActiveNodeMissing + "：firstMainNodeId 不存在。";
                return false;
            }
            if (!string.IsNullOrEmpty(runtime.ActiveMainNodeId) &&
                !runtime.MainNodes.Exists(item => item != null && item.NodeId == runtime.ActiveMainNodeId))
            {
                diagnostic = ActiveNodeMissing + "：activeMainNodeId 不存在。";
                return false;
            }

            HashSet<string> nodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.EffectNodes.Count; i++)
            {
                EffectNodeRuntimeState node = runtime.EffectNodes[i];
                if (node == null || string.IsNullOrEmpty(node.EffectId) || !nodeIds.Add(node.EffectId))
                {
                    diagnostic = "Effect 节点为空或 ID 重复。";
                    return false;
                }
                if (registry != null && !registry.TryGet(node.EffectTypeId, out _))
                {
                    diagnostic = UnknownHandler + "：未知 Effect 类型 " + node.EffectTypeId;
                    return false;
                }
                if (node.EffectTypeId == YC.Domain.Cards.CharacterCoverEffectExecutor.EffectTypeId &&
                    ItemTransferEffectExecutor.Value(node.NormalizedArguments, "apiVersion") != null)
                {
                    diagnostic = YC.Domain.Cards.CharacterCoverEffectExecutor.ValidateArguments(node.NormalizedArguments);
                    if (diagnostic.Length > 0) return false;
                }
                if (!TryValidateCoverSpecs(node.NestedEffects, out diagnostic)) return false;
                if (registry != null && registry.TryGet(node.EffectTypeId, out var effectRegistration) &&
                    new[] { ItemTransferEffectExecutor.TypeId, LuaDomainEffectTypeIds.MoveFacilityCard, LuaDomainEffectTypeIds.MoveCharacterCard, DeckShuffleEffectExecutor.TypeId, LuaDomainEffectTypeIds.ShuffleFacilityDeck, LuaDomainEffectTypeIds.Repeat, InfluenceEffectTypeIds.PlaceInfluence, InfluenceEffectTypeIds.RemoveInfluence, InfluenceEffectTypeIds.MoveInfluence, InfluenceEffectTypeIds.ReplaceInfluence, LuaDomainEffectTypeIds.PlaceRoad, LuaDomainEffectTypeIds.OperateToken, ContentPrimitiveEffectExecutor.SelectInfluence }.Contains(node.EffectTypeId) &&
                    node.DefinitionVersion != effectRegistration.DefinitionVersion && !ItemTransferEffectExecutor.CanRestoreLegacy(state, node) && !InfluenceEffectExecutor.CanRestoreLegacy(state, node) && node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed)
                { diagnostic = ItemZoneStateMigrator.Unsupported + ": " + node.EffectTypeId; return false; }
                if (ItemZoneLegacyProjection.IsActive(state) && (node.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence || node.EffectTypeId == InfluenceEffectTypeIds.ReplaceInfluence) &&
                    (node.FlowStage == "awaiting_destination" || node.FlowStage == "placing_replacement") && node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed)
                {
                    string pendingId = ItemTransferEffectExecutor.Text(node.ContinuationData, "pendingItemId");
                    var pendingItem = new ItemZoneQuery().FindItem(state, pendingId);
                    var zone = pendingItem == null ? null : new ItemZoneQuery().FindZone(state, pendingItem.ZoneId);
                    string kind = ItemTransferEffectExecutor.Text(node.ContinuationData, "pendingDepartureKind");
                    var resolved = ItemTransferEffectExecutor.Value(node.ContinuationData, "destinationResolved");
                    if (pendingItem?.PhysicalKind != ItemPhysicalKind.Cube || kind != "remove" && kind != "replace" || resolved?.Kind != NormalizedValueKind.Boolean ||
                        (zone?.TemplateId == "core.zone.effect_staging" ? zone.OwnerEffectId != node.EffectId || resolved.BooleanValue : !resolved.BooleanValue || ItemTransferEffectExecutor.Text(node.ContinuationData, "destinationZoneId") != pendingItem.ZoneId))
                    { diagnostic = "pending_influence_destination_invalid"; return false; }
                }
                for (int j = 0; j < node.ChildEffectIds.Count; j++)
                {
                    if (!nodeIds.Contains(node.ChildEffectIds[j]) &&
                        !runtime.EffectNodes.Exists(item => item != null && item.EffectId == node.ChildEffectIds[j]))
                    {
                        diagnostic = BlockerReferenceMissing + "：子节点不存在。";
                        return false;
                    }
                }
                if (!string.IsNullOrEmpty(node.ContinuationHandlerId))
                {
                    if (registry == null) continue;
                    EffectEventHandlerRegistration registration;
                    if (!registry.TryGetContinuation(node.ContinuationHandlerId, "EffectCompleted", out registration))
                    {
                        diagnostic = UnknownHandler + "：" + node.ContinuationHandlerId;
                        return false;
                    }
                    if (!string.Equals(registration.DefinitionVersion, node.ContinuationDefinitionVersion ?? string.Empty, StringComparison.Ordinal) ||
                        !string.Equals(registration.ContentHash, node.ContinuationContentHash ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic = ContentHashMismatch + "：continuation handler " + node.ContinuationHandlerId;
                        return false;
                    }
                }
            }

            HashSet<string> blockerIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runtime.Blockers.Count; i++)
            {
                EffectBlockerRuntimeState blocker = runtime.Blockers[i];
                if (blocker == null || string.IsNullOrEmpty(blocker.BlockerId) || !blockerIds.Add(blocker.BlockerId) ||
                    !nodeIds.Contains(blocker.OwnerEffectId) ||
                    (!string.IsNullOrEmpty(blocker.TargetEffectId) && !nodeIds.Contains(blocker.TargetEffectId)) ||
                    (!string.IsNullOrEmpty(blocker.InteractionRequestId) &&
                     !runtime.InteractionRequests.Exists(item => item != null && item.GetStableInteractionId() == blocker.InteractionRequestId)))
                {
                    diagnostic = BlockerReferenceMissing + "：blocker owner/target/interaction 不存在。";
                    return false;
                }
            }
            return true;
        }

        private static bool TryValidateCoverSpecs(IEnumerable<EffectSpecRuntimeState> specs, out string diagnostic)
        {
            diagnostic = string.Empty;
            foreach (var spec in specs)
            {
                if (spec.EffectTypeId == YC.Domain.Cards.CharacterCoverEffectExecutor.EffectTypeId &&
                    ItemTransferEffectExecutor.Value(spec.NormalizedArguments, "apiVersion") != null)
                {
                    diagnostic = YC.Domain.Cards.CharacterCoverEffectExecutor.ValidateArguments(spec.NormalizedArguments);
                    if (diagnostic.Length > 0) return false;
                }
                if (!TryValidateCoverSpecs(spec.NestedEffects, out diagnostic)) return false;
            }
            return true;
        }

        private static bool ContainsHandlerState(GameState state)
        {
            return state != null && state.EffectRuntime != null &&
                   state.EffectRuntime.EffectNodes != null && state.EffectRuntime.EffectNodes.Count > 0;
        }

        private static int GetLastCommitSequence(EffectRuntimeState runtime)
        {
            if (runtime == null || runtime.Journal == null || runtime.Journal.Count == 0) return 0;
            return runtime.Journal[runtime.Journal.Count - 1] == null
                ? 0 : runtime.Journal[runtime.Journal.Count - 1].CommitSequence;
        }

        private static HostRecoveryResult Fault(GameState source, string faultCode, string diagnostic)
        {
            GameState faulted;
            try
            {
                faulted = source == null ? new GameState() : GameStateCloneService.DeepClone(source);
            }
            catch
            {
                faulted = new GameState();
            }
            if (faulted.EffectRuntime == null) faulted.EffectRuntime = new EffectRuntimeState();
            if (faulted.EffectRuntime.Diagnostics == null) faulted.EffectRuntime.Diagnostics = new List<EffectDiagnosticRuntimeState>();
            faulted.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            faulted.EffectRuntime.LastFaultCode = faultCode ?? SnapshotInvalid;
            faulted.EffectRuntime.LastFaultMessage = diagnostic ?? string.Empty;
            faulted.EffectRuntime.Diagnostics.Add(new EffectDiagnosticRuntimeState
            {
                Code = faulted.EffectRuntime.LastFaultCode,
                Message = faulted.EffectRuntime.LastFaultMessage
            });
            return new HostRecoveryResult(false, faulted, faulted.EffectRuntime.LastFaultCode, faulted.EffectRuntime.LastFaultMessage);
        }
    }
}
