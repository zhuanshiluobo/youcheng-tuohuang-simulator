using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static partial class ItemTransferEffectExecutor
    {
        public const string TypeId = "effect.item.move";
        public const string TransferDefinitionVersion = "4.15.0";
        public const int MaximumTokenCount = 99;
        public const string IndependentItemFailurePolicyVersion = "single_item_expected_failure.v1";
        // 单个显式物品的库存/区位过期是可观察的叶子失败，不让成功兄弟或调用续接升级成行动故障。
        // 显式required outcome、权限/类型/参数错误和多枚足额操作仍由各自严格合同处理。
        public static bool IsExpectedIndependentFailure(EffectNodeRuntimeState node) =>
            node?.EffectTypeId == TypeId && node.Status == EffectNodeStatus.Failed && node.RequiredOutcome == null &&
            ItemMovementV4.IsV4(node.EffectTypeId, node.NormalizedArguments) && ItemMovementV4.ValidateArguments(node.NormalizedArguments).Length == 0 &&
            Text(node.NormalizedArguments, "operation") == "transfer" && Number(node.NormalizedArguments, "requiredCount") == 1 && Text(Value(node.NormalizedArguments, "selector"), "mode") == "explicit" && Value(Value(node.NormalizedArguments, "selector"), "itemIds").Items.Count <= 1 &&
            new[] { "insufficient_items", "source_zone_changed", "unknown_item" }.Contains(node.FailureReason);
        // v3只提交Lua已经选定的真实实例。它不解释费用、不选实例、不产生选择请求。
        public static string ValidateTokenArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Select(p => p.Name).Distinct().Count() != args.Properties.Count ||
                args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "itemIds", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" }.Contains(p.Name)) ||
                Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 3 ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1)
                return "token_transfer_arguments_invalid";
            var ids = Value(args, "itemIds");
            if (ids == null || ids.Kind != NormalizedValueKind.Array && !(ids.Kind == NormalizedValueKind.Object && ids.Properties.Count == 0) ||
                ids.Items.Count > MaximumTokenCount || ids.Items.Any(i => i.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(i.StringValue)) ||
                ids.Items.Select(i => i.StringValue).Distinct(StringComparer.Ordinal).Count() != ids.Items.Count) return "token_transfer_items_invalid";
            var count = Value(args, "requiredCount");
            if (count?.Kind != NormalizedValueKind.Integer || count.IntegerValue < 1 || count.IntegerValue > MaximumTokenCount) return "required_count_invalid";
            if (!Reference(Value(args, "expectedSource"), "zoneId")) return "invalid_zone_ref";
            var destination = Value(args, "destination");
            if (!Reference(destination, "zoneId") && !(destination?.Kind == NormalizedValueKind.Object && destination.Properties.Count == 1 && Text(destination, "kind") == "original_supply")) return "invalid_zone_ref";
            if (Value(args, "insertion")?.Kind != NormalizedValueKind.Object || Value(args, "insertion").Properties.Count != 1 || Text(Value(args, "insertion"), "kind") != "unordered") return "insertion_rejected";
            if (!ItemTransferService.IsTokenProfile(Text(args, "operationProfileId")) || Value(args, "carrierPolicy") != null && Text(args, "carrierPolicy") != "preserve") return "operation_profile_rejected";
            return "";
        }
        public static NormalizedValue TokenArguments(int player, IEnumerable<string> ids, string source, string destination, int required, string profile = "core.cube_storage") => Object(
            "apiVersion", NormalizedValue.CreateInteger(3), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),
            "itemIds", Strings(ids), "expectedSource", Object("zoneId", NormalizedValue.CreateString(source)),
            "destination", destination == null ? Object("kind", NormalizedValue.CreateString("original_supply")) : Object("zoneId", NormalizedValue.CreateString(destination)),
            "insertion", Object("kind", NormalizedValue.CreateString("unordered")), "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required));
        public static EffectSpec TokenSpec(int player, IEnumerable<string> ids, string source, string destination, int required, string profile = "core.cube_storage") =>
            new EffectSpec(TypeId, TokenArguments(player, ids, source, destination, required, profile)) { PlayerId = player, DefinitionVersion = TransferDefinitionVersion, Visibility = "owner" };
        public static EffectSpec BatchSpec(ItemTransferBatchRequest request) => new EffectSpec(TypeId, ItemTransferService.BatchArguments(request))
            { PlayerId = request.ExecutingPlayerId, DefinitionVersion = TransferDefinitionVersion, Visibility = "owner" };
        private static string ValidateBatchArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !new[] { "kind", "effectTypeId", "apiVersion", "player", "operationId", "transfers", "expectedOrders" }.Contains(p.Name)) ||
                Number(args, "apiVersion", 0) != 2 || !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                Text(args, "operationId").Length == 0 || Value(args, "transfers")?.Kind != NormalizedValueKind.Array || Value(args, "transfers").Items.Count == 0 ||
                Value(args, "expectedOrders")?.Kind != NormalizedValueKind.Array) return "transfer_batch_arguments_invalid";
            foreach (var leg in Value(args, "transfers").Items)
            {
                if (leg.Kind != NormalizedValueKind.Object || leg.Properties.Any(p => !new[] { "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy" }.Contains(p.Name)) ||
                    Value(leg, "itemRefs")?.Kind != NormalizedValueKind.Array || Value(leg, "itemRefs").Items.Count == 0 ||
                    Value(leg, "itemRefs").Items.Any(v => !Reference(v, "itemId")) ||
                    !Reference(Value(leg, "expectedSource"), "zoneId") || !Reference(Value(leg, "destination"), "zoneId") ||
                    Value(leg, "insertion")?.Kind != NormalizedValueKind.Object || Value(leg, "insertion").Properties.Count != 1 ||
                    !new[] { "top", "bottom", "unordered" }.Contains(Text(Value(leg, "insertion"), "kind")) ||
                    Value(leg, "requiredCount")?.Kind != NormalizedValueKind.Integer || Number(leg, "requiredCount", -1) != Value(leg, "itemRefs").Items.Count ||
                    Text(leg, "operationProfileId") != "core.facility_card_transfer" || Text(leg, "carrierPolicy", "preserve") != "preserve")
                    return "transfer_batch_leg_invalid";
            }
            foreach (var expected in Value(args, "expectedOrders").Items)
                if (expected.Kind != NormalizedValueKind.Object || expected.Properties.Count != 4 ||
                    expected.Properties.Any(p => !new[] { "zoneId", "digest", "orderRevision", "expectedTopItemId" }.Contains(p.Name)) ||
                    Text(expected, "zoneId").Length == 0 || Text(expected, "digest").Length == 0 ||
                    Value(expected, "orderRevision")?.Kind != NormalizedValueKind.Integer || Number(expected, "orderRevision", -1) < 0 ||
                    Value(expected, "expectedTopItemId")?.Kind != NormalizedValueKind.String) return "transfer_batch_order_invalid";
            return "";
        }
        public static ItemTransferBatchRequest BatchRequest(NormalizedValue args)
        {
            string invalid = ValidateBatchArguments(args);
            if (invalid.Length > 0) throw new InvalidOperationException(invalid);
            int player = int.Parse(Text(args, "player"), CultureInfo.InvariantCulture);
            return new ItemTransferBatchRequest { OperationId = Text(args, "operationId"), ExecutingPlayerId = player,
                Transfers = Value(args, "transfers").Items.Select(v => new ItemTransferRequest {
                    ExecutingPlayerId = player, ItemIds = Value(v, "itemRefs").Items.Select(i => Text(i, "itemId")).ToList(),
                    ExpectedSource = new ItemPositionRef { ZoneId = Text(Value(v, "expectedSource"), "zoneId") },
                    Destination = new ZoneRef { ZoneId = Text(Value(v, "destination"), "zoneId") },
                    InsertionKind = Text(Value(v, "insertion"), "kind"), OperationProfileId = Text(v, "operationProfileId"),
                    CarrierPolicyId = Text(v, "carrierPolicy", "preserve"), RequiredCount = Number(v, "requiredCount") }).ToList(),
                ExpectedOrders = Value(args, "expectedOrders").Items.Select(v => new ItemTransferOrderExpectation {
                    ZoneId = Text(v, "zoneId"), Digest = Text(v, "digest"), OrderRevision = Number(v, "orderRevision"),
                    ExpectedTopItemId = Text(v, "expectedTopItemId") }).ToList() };
        }
        public const string RefillTypeId = "effect.facility.refill_slot";
        public const string RefreshTypeId = "effect.facility.refresh_supply";
        // 旧v1/v2不能经定义版本不匹配的兼容通道恢复；新输入先归一化。
        public static bool CanRestoreLegacy(GameState state, EffectNodeRuntimeState node) => false;
        public static string ValidateSupplyArguments(string type, NormalizedValue args)
        {
            if(type==RefreshTypeId&&(args==null||args.Kind==NormalizedValueKind.Null))return ""; // 正常Host刷新工厂的既有空参数形状。
            var fields = type == RefillTypeId ? new[] { "apiVersion", "player", "slotZoneId", "kind", "effectTypeId" } : new[] { "apiVersion", "player", "kind", "effectTypeId" };
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !fields.Contains(p.Name)) || type == RefillTypeId && Text(args,"slotZoneId").Length==0) return "supply_arguments_invalid";
            if(Value(args,"apiVersion")!=null&&(Value(args,"apiVersion").Kind!=NormalizedValueKind.Integer||Value(args,"apiVersion").IntegerValue!=1||
                !int.TryParse(Text(args,"player"),NumberStyles.None,CultureInfo.InvariantCulture,out int player)||player<1))return "supply_arguments_invalid";
            return "";
        }
        private static string ValidateSupplyContext(EffectExecutionContext context)
        {
            string error = ValidateSupplyArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return error;
            return Value(context.Node.NormalizedArguments, "apiVersion") != null && Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) ? "supply_permission_denied" : "";
        }
        public static void Register(EffectRegistry registry)
        {
            if (registry.TryGet(TypeId, out _)) return;
            registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.Atomic, TransferDefinitionVersion) { ExecutionDoesNotRetainState = true, ExecutionPreservesJournalPrefix = true });
            registry.RegisterNativeItemTransferEntryCostPreview((state, source, spec) => {
                if (Text(spec.NormalizedArguments, "player") != source.AuthorizedPlayerId.ToString(CultureInfo.InvariantCulture)) return "transfer_permission_denied";
                // 原生v4与有限旧输入规范后使用同一费用预检；原输入记录只供一致性复验。
                var args = spec.NormalizedArguments;
                if (spec.ItemMoveInput != null)
                {
                    if (!ItemMoveInputNormalization.TryValidate(spec.EffectTypeId, args, spec.ItemMoveInput, out string invalid)) return invalid;
                }
                return new ItemTransferService().TryPlanTokens(state, source, args, registry.AbilityDeclarations,
                    "token-transfer-preview", out _, out string reason) ? "" : reason;
            });
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.MoveFacilityCard, ExecuteFacility, EffectExecutorKind.IntrinsicFlow,FacilityMoveDefinitionVersion));
            registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.MoveCharacterCard, ExecuteCharacter, EffectExecutorKind.IntrinsicFlow, CharacterCardMovement.DefinitionVersion));
            registry.Register(new EffectRegistration(RefillTypeId, ExecuteRefillCommon,EffectExecutorKind.IntrinsicFlow,SupplyDefinitionVersion) { ExecutionDoesNotRetainState = true, ExecutionPreservesJournalPrefix = true });
            registry.Register(new EffectRegistration(RefreshTypeId, ExecuteRefreshCommon,EffectExecutorKind.IntrinsicFlow,SupplyDefinitionVersion));
        }
        // Host 组合工厂；内容脚本由其归属任务装配，不识别具体企业或角色 ID。
        public static EffectSpec MoveSpec(int player, IEnumerable<string> ids, string source, string destination, string insertion, int required = 0, string profile = "core.facility_card_transfer") => EffectSpec.Create(TypeId,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "itemRefs", NormalizedValue.CreateArray(ids.Select(id => Object("itemId", NormalizedValue.CreateString(id))).ToList()), "expectedSource", Object("zoneId", NormalizedValue.CreateString(source)), "destination", Object("zoneId", NormalizedValue.CreateString(destination)), "insertion", Object("kind", NormalizedValue.CreateString(insertion)), "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required)), player);
        public static EffectSpec RefillSpec(int player, string slot) => EffectSpec.Create(RefillTypeId, Object("slotZoneId", NormalizedValue.CreateString(slot)), player);
        public static EffectSpec RefreshSpec(int player) => EffectSpec.Create(RefreshTypeId, playerId: player);
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            if (typeId == LuaDomainEffectTypeIds.MoveCharacterCard && Value(args, "apiVersion") == null) return CharacterCardMovement.UnversionedSignatureRetired;
            if (typeId == LuaDomainEffectTypeIds.MoveFacilityCard && Value(args,"apiVersion")==null) return RetiredFacilityMoveSignature;
            if (typeId == LuaDomainEffectTypeIds.MoveFacilityCard && IsCycle(args)) return ValidateFacilityCycleArguments(args);
            bool typed = typeId != TypeId;
            var version = Value(args, "apiVersion");
            if (!typed && version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 4) return ItemMovementV4.ValidateArguments(args);
            if (!typed && version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 3) return ValidateTokenArguments(args);
            if (!typed && version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 2) return ValidateBatchArguments(args);
            var allowed = new HashSet<string>(new[] { "kind", "effectTypeId", "apiVersion", "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy", "reveal" });
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => !allowed.Contains(entry.Name))) return "invalid_transfer_arguments";
            if (version?.Kind != NormalizedValueKind.Integer || version.IntegerValue != (typed ? 2 : 1)) return "api_version_unsupported";
            if (!int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player <= 0) return "invalid_player";
            var refs = Value(args, "itemRefs");
            // 空 Lua table 规范化为 Object，只有完全空时才等同空数组。
            if (refs == null || refs.Kind != NormalizedValueKind.Array && !(refs.Kind == NormalizedValueKind.Object && refs.Properties.Count == 0)) return "item_refs_required";
            if (refs.Items.Any(item => !Reference(item, "itemId"))) return "invalid_item_ref";
            if (!Reference(Value(args, "expectedSource"), "zoneId", "slotId") || !Reference(Value(args, "destination"), "zoneId")) return "invalid_zone_ref";
            var insertion = Value(args, "insertion");
            if (insertion?.Kind != NormalizedValueKind.Object || insertion.Properties.Any(entry => entry.Name != "kind" && entry.Name != "slotId") || !new[] { "unordered", "top", "bottom", "slot" }.Contains(Text(insertion, "kind"))) return "insertion_rejected";
            if (Text(insertion, "kind") == "slot" ? Text(insertion, "slotId").Length == 0 : Value(insertion, "slotId") != null) return "insertion_rejected";
            if (Value(args, "requiredCount") != null && (Value(args, "requiredCount").Kind != NormalizedValueKind.Integer || Value(args, "requiredCount").IntegerValue < 0 || Value(args, "requiredCount").IntegerValue > int.MaxValue)) return "required_count_invalid";
            if (Text(args, "carrierPolicy", "preserve") != "preserve" || Text(args, "operationProfileId").Length == 0) return "operation_profile_rejected";
            if(typeId==LuaDomainEffectTypeIds.MoveFacilityCard&&Text(args,"operationProfileId")!="core.facility_card_transfer")return "operation_profile_rejected";
            if (typeId == LuaDomainEffectTypeIds.MoveCharacterCard && Text(args, "operationProfileId") != "core.character_card_transfer") return "operation_profile_rejected";
            if (Value(args, "reveal") != null && (Value(args, "reveal").Kind != NormalizedValueKind.Boolean || Text(args, "operationProfileId") != "core.character_card_transfer")) return "character_reveal_invalid";
            return "";
        }
        public static EffectStepResult Execute(EffectExecutionContext context) => Move(context, "");
        private static EffectStepResult Move(EffectExecutionContext context, string requiredType)
        {
            YC.Domain.Cards.CharacterLifecycleService.ResumeDeferredDependencies(context);
            var missing = MissingTransferFields(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (missing.Count > 0) return SkipIncomplete(context, missing);
            string reason = ValidateArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            if (ItemMovementV4.IsV4(context.Node.EffectTypeId, context.Node.NormalizedArguments)) return ItemMovementV4.Execute(context);
            if (requiredType == "core.character_card") return SpecializedItemMovement.ExecuteCharacter(context);
            if(requiredType=="core.facility_card")return SpecializedItemMovement.ExecuteFacility(context);
            return EffectStepResult.Failed("item_move_legacy_input_not_normalized");
        }
        private static EffectStepResult TransferCompleted(ItemTransferResult moved, string emptyReason)
        {
            var result = Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds));
            if (!moved.Changed) { result.Properties.Add(new NormalizedValueEntry { Name = "reasonCode", Value = NormalizedValue.CreateString(emptyReason) }); result.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name)); }
            return EffectStepResult.Completed(result);
        }
        public static List<string> MissingTransferFields(string typeId, NormalizedValue args)
        {
            var version = Value(args, "apiVersion");
            if (version?.Kind != NormalizedValueKind.Integer || version.IntegerValue != (typeId == TypeId ? 1 : 2) || args.Properties.Any(entry => !new[] { "kind", "effectTypeId", "apiVersion", "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy", "reveal" }.Contains(entry.Name))) return new List<string>();
            return new[] { "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId" }.Where(key => Value(args, key) == null).ToList();
        }
        internal static EffectStepResult SkipIncomplete(EffectExecutionContext context, IEnumerable<string> missing)
        {
            return EffectStepResult.Failed("incomplete_effect_skipped", Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("incomplete_effect_skipped")))
                .AddLog(new EffectLogEntrySpec { PlayerId = context.Node.PlayerId, Message = "sourceId=" + context.Node.SourceId + ";effectId=" + context.Node.EffectId + ";missingFieldNames=" + string.Join(",", missing) + ";reasonCode=incomplete_effect_skipped；内容效果缺少必要字段，已跳过。" });
        }
        private static EffectStepResult ExecuteCharacter(EffectExecutionContext context)
        {
            if (Value(context.Node.NormalizedArguments, "apiVersion") != null) return Move(context, "core.character_card");
            return EffectStepResult.Failed(CharacterCardMovement.UnversionedSignatureRetired);
        }
        private static EffectStepResult ExecuteFacility(EffectExecutionContext context)
        {
            if(IsCycle(context.Node.NormalizedArguments))return ExecuteCycleCommon(context);
            if(Value(context.Node.NormalizedArguments,"apiVersion")==null)return EffectStepResult.Failed(RetiredFacilityMoveSignature);
            return Move(context,"core.facility_card");
        }
        // 与节点一起提交候选解析记录；候选按稳定 ID 排序，不泄漏秘密牌堆顺序。
        internal static EffectInteractionSpec PrepareInteraction(EffectExecutionContext context, EffectInteractionSpec interaction)
        {
            string setId = StableIdFactory.Create("candidate", context.Node.EffectId,
                interaction.InteractionTypeId, context.State.EffectRuntime.InteractionRequests.Count(request => request.OwnerEffectId == context.Node.EffectId).ToString(CultureInfo.InvariantCulture));
            var draft = new CandidateSetDraft
            {
                CandidateSetId = setId, Version = 1, CandidateKind = interaction.InteractionTypeId,
                SourceEffectId = context.Node.EffectId, ExecutingPlayerId = context.Node.PlayerId,
                CandidatePoolIds = new List<string>(interaction.CandidateIds),
                DefaultCandidateIds = new List<string>(interaction.CandidateIds),
                CurrentIds = new List<string>(interaction.CandidateIds)
            };
            var resolution = new CandidateSetResolver().Resolve(context.State, draft, new CandidatePolicyRegistry(), context.State.EffectRuntime.StateRevision + 1);
            if (!resolution.Succeeded) throw new InvalidOperationException(resolution.FaultCode);
            context.State.EffectRuntime.CandidateResolutions.Add(resolution.Record);
            interaction.CandidateSetId = setId;
            interaction.CandidateSetVersion = draft.Version;
            interaction.CandidateResolutionId = setId;
            interaction.CandidateIds.Clear();
            interaction.CandidateIds.AddRange(resolution.FinalCandidateIds);
            // 与 CandidateIds 同索引的卡面定义只放在授权交互中，避免 UI 把实例 ID 当文案。
            var definitions = interaction.CandidateIds.Select(id => context.State.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId ?? "").ToList();
            if (definitions.Any(id => id.Length > 0))
            {
                var parameters = interaction.PromptParameters.Clone();
                parameters.Properties.Add(new NormalizedValueEntry { Name = "candidateDefinitionIds", Value = Strings(definitions) });
                parameters.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                interaction.PromptParameters = parameters;
            }
            return interaction;
        }
        public static ItemTransferRequest Request(NormalizedValue args) => new ItemTransferRequest
        {
            ExecutingPlayerId = int.Parse(Text(args, "player"), CultureInfo.InvariantCulture), ItemIds = Value(args, "itemRefs").Items.Select(item => Text(item, "itemId")).ToList(),
            ExpectedSource = new ItemPositionRef { ZoneId = Text(Value(args, "expectedSource"), "zoneId"), SlotId = Text(Value(args, "expectedSource"), "slotId") },
            Destination = new ZoneRef { ZoneId = Text(Value(args, "destination"), "zoneId") }, InsertionKind = Text(Value(args, "insertion"), "kind"), DestinationSlotId = Text(Value(args, "insertion"), "slotId"),
            OperationProfileId = Text(args, "operationProfileId"), RequiredCount = Number(args, "requiredCount"), CarrierPolicyId = Text(args, "carrierPolicy", "preserve")
        };
        private static bool Reference(NormalizedValue value, string key, string optional = "") => value?.Kind == NormalizedValueKind.Object && Text(value, key).Length > 0 && value.Properties.All(entry => (entry.Name == key || entry.Name == optional) && entry.Value.Kind == NormalizedValueKind.String);
        public static ZoneInstanceState FindZone(GameState state, string local, int player = -1) => state.ItemZones.Zones.Single(zone => zone.LocalZoneId == local &&
            (local != "covered" || zone.TemplateId == "core.zone.character_covered") && (player < 0 || zone.OwnerPlayerId == player));
        public static NormalizedValue Value(NormalizedValue value, string name)
        {
            var properties = value?.Properties;
            if (properties == null) return null;
            // 保留Find的首个匹配/空项错误语义，避免每次字段读取分配捕获name的闭包。
            for (int i = 0; i < properties.Count; i++)
                if (properties[i].Name == name) return properties[i].Value;
            return null;
        }
        public static string Text(NormalizedValue value, string name, string fallback = "")
        { var field = Value(value, name); return field?.Kind == NormalizedValueKind.String ? field.StringValue : fallback; }
        public static int Number(NormalizedValue value, string name, int fallback = 0)
        { var field = Value(value, name); return field?.Kind == NormalizedValueKind.Integer ? (int)field.IntegerValue : fallback; }
        public static bool Flag(NormalizedValue value, string name)
        { var field = Value(value, name); return field?.Kind == NormalizedValueKind.Boolean && field.BooleanValue; }
        public static NormalizedValue Strings(IEnumerable<string> ids) => NormalizedValue.CreateArray(ids.Select(NormalizedValue.CreateString).ToList());
        public static NormalizedValue Object(params object[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i < values.Length; i += 2) entries.Add(new NormalizedValueEntry { Name = (string)values[i], Value = (NormalizedValue)values[i + 1] });
            return NormalizedValue.CreateObject(entries);
        }
        public static void Set(EffectNodeRuntimeState node, string key, NormalizedValue value)
        {
            var entry = node.ContinuationData.Properties.Find(field => field.Name == key);
            if (entry == null) node.ContinuationData.Properties.Add(new NormalizedValueEntry { Name = key, Value = value }); else entry.Value = value;
            node.ContinuationData.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        }
        public static string Answer(EffectExecutionContext context)
        {
            var value = context.GetLatestInteractionAnswer();
            if (value?.Kind == NormalizedValueKind.Array && value.Items.Count == 1) value = value.Items[0];
            return value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? "";
        }
    }
}
