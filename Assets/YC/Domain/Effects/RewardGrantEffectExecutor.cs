using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class RewardGrantReceipt
    {
        public int ProtocolVersion = 1, RecipientPlayerId, Count;
        public string OperationId = "", RequestDigest = "", AuthorityEffectId = "", DefinitionId = "", DefinitionVersion = "", TypeId = "", DestinationZoneId = "";
        public AbilitySourceRef SourceRef;
        public AbilityVersionRef VersionRef;
        public List<string> ItemIds = new List<string>();
    }

    /// <summary>仅创建冻结声明许可的真实奖励。数量、受益人和调用顺序由Lua决定。</summary>
    public static class RewardGrantEffectExecutor
    {
        public const string TypeId = "effect.reward.grant", ReceiveTypeId = "effect.reward.receive_granted", Version = "1.0.0";
        public const int MaximumCount = 16;
        public static readonly string[] Fields = { "apiVersion", "player", "definitionId", "typeId", "count", "recipientPlayer", "destination", "operationId" };
        public static bool ValidPermission(string permission)
        {
            var p = (permission ?? "").Split(':');
            return p.Length == 6 && p[0] == "reward.grant" && p.Skip(1).All(s => !string.IsNullOrWhiteSpace(s) && s != "*") &&
                int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 1 && count <= MaximumCount &&
                new[] { "self", "other", "any" }.Contains(p[4]) && p[5] == "core.zone.player_rewards" && p[2] == "core.award_card";
        }
        public static string ValidateArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !Fields.Concat(new[] { "kind", "effectTypeId" }).Contains(p.Name)) ||
                args.Properties.Select(p => p.Name).Distinct().Count() != args.Properties.Count ||
                Value(args, "kind") != null && Text(args, "kind") != "effect_spec" || Value(args, "effectTypeId") != null && Text(args, "effectTypeId") != TypeId ||
                Fields.Any(f => Value(args, f) == null) || Value(args, "apiVersion").Kind != NormalizedValueKind.Integer || Number(args, "apiVersion") != 1 ||
                Value(args, "count").Kind != NormalizedValueKind.Integer || Number(args, "count") < 1 || Number(args, "count") > MaximumCount ||
                !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1 ||
                !int.TryParse(Text(args, "recipientPlayer"), NumberStyles.None, CultureInfo.InvariantCulture, out int recipient) || recipient < 1 ||
                new[] { "definitionId", "typeId", "operationId" }.Any(f => string.IsNullOrWhiteSpace(Text(args, f)) || Text(args, f).Length > 128) ||
                Text(args, "destination") != "core.zone.player_rewards") return "reward_grant_arguments_invalid";
            return "";
        }
        private static string Digest(NormalizedValue args) => FacilityBuildQueryService.Hash(JsonConvert.SerializeObject(args));
        private static bool Same(object a, object b) => a != null && b != null && JsonConvert.SerializeObject(a) == JsonConvert.SerializeObject(b);
        private static AbilitySourceRef Authority(GameState state, EffectNodeRuntimeState node, EffectRegistry registry, out string authorityId)
        {
            authorityId = ""; var seen = new HashSet<string>();
            while (node != null && seen.Add(node.EffectId))
            {
                var source = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(r => r.OwnerEffectId == node.EffectId)?.SourceRef ??
                    state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(r => r.OwnerEffectId == node.EffectId)?.SourceRef ??
                    CleanupSourceService.Invocations(state).SingleOrDefault(r => r.TargetEffectId == node.EffectId)?.SourceRef;
                if (source == null && node.EffectTypeId == EntrepreneurFlow.EntryTypeId)
                    source = CardMarkerCarrier.Source(state, node, registry?.AbilityDeclarations ?? state.ContentDefinitions.AbilitySources, registry);
                if (source != null) { authorityId = node.EffectId; return source; }
                node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            }
            return null;
        }
        private static bool HandlerAllowed(EffectNodeRuntimeState node, AbilitySourceDeclaration declaration, EffectRegistry registry)
        {
            if (node.ContinuationAbilityId != declaration.AbilityId || node.ContinuationDefinitionVersion.Length == 0 || node.ContinuationContentHash.Length == 0) return false;
            return registry == null || new[] { declaration.HandlerRef }.Concat(declaration.Actions.SelectMany(a =>
                new[] { a.BodyHandlerRef, a.EntryCostRef, a.EntryAfterEffectsRef })).Where(h => h.Length > 0)
                .Any(h => registry.HandlerHash(h) == node.ContinuationContentHash);
        }
        private static bool Permitted(AbilitySourceDeclaration declaration, NormalizedValue args, int actor)
        {
            int recipient = int.Parse(Text(args, "recipientPlayer"), CultureInfo.InvariantCulture);
            return declaration.AllowedOperations.Where(ValidPermission).Any(permission => {
                var p = permission.Split(':');
                return p[1] == Text(args, "definitionId") && p[2] == Text(args, "typeId") && Number(args, "count") <= int.Parse(p[3], CultureInfo.InvariantCulture) &&
                    (p[4] == "any" || (p[4] == "self") == (actor == recipient)) && p[5] == Text(args, "destination");
            });
        }
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, Version));
            if (!registry.TryGet(ReceiveTypeId, out _)) registry.Register(new EffectRegistration(ReceiveTypeId, Receive, EffectExecutorKind.IntrinsicFlow, Version));
        }
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var state = context.State; var node = context.Node; var args = node.NormalizedArguments;
            string error = ValidateArguments(args);
            if (error.Length > 0 || Text(args, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed(error.Length > 0 ? error : "reward_grant_wrong_actor");
            if (node.RewardGrant != null)
            {
                if (!ValidateReceipt(state, node, context.Registry, out error)) throw new InvalidOperationException(error);
                if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("reward_grant_obligation_failed");
                return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "count", NormalizedValue.CreateInteger(node.RewardGrant.Count)));
            }
            if (node.FlowStage.Length > 0) throw new InvalidOperationException("reward_grant_receipt_missing");
            var source = Authority(state, node, context.Registry, out string authorityId);
            var declaration = context.Registry.AbilityDeclarations.SingleOrDefault(d => d.AbilityId == source?.AbilityId && d.DefinitionId == source.DefinitionId);
            if (declaration == null || source.AuthorizedPlayerId != node.PlayerId || !AbilitySourceService.IsCurrent(state, context.Registry.AbilityDeclarations, source) ||
                !HandlerAllowed(node, declaration, context.Registry) || !Permitted(declaration, args, node.PlayerId)) return EffectStepResult.Failed("reward_grant_permission_denied");
            if (state.EffectRuntime.EffectNodes.Any(n => n.RewardGrant?.AuthorityEffectId == authorityId && n.RewardGrant.OperationId == Text(args, "operationId"))) return EffectStepResult.Failed("reward_grant_operation_reused");
            int recipient = int.Parse(Text(args, "recipientPlayer"), CultureInfo.InvariantCulture);
            var definition = state.ContentDefinitions.Awards.SingleOrDefault(d => d.DefinitionId == Text(args, "definitionId") && d.TypeId == Text(args, "typeId") && d.AcquisitionPolicy == "content_granted");
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.TemplateId == Text(args, "destination") && z.OwnerPlayerId == recipient);
            if (definition == null || definition.DefinitionVersion.Length == 0 || zone == null || state.FindPlayer(recipient) == null) return EffectStepResult.Failed("reward_grant_target_invalid");
            var receipt = new RewardGrantReceipt { OperationId = Text(args, "operationId"), RequestDigest = Digest(args), AuthorityEffectId = authorityId,
                SourceRef = GameStateCloneService.DeepClone(source), VersionRef = new AbilityVersionRef { ContentPackHash = state.ContentPackHash,
                    DefinitionVersion = node.ContinuationDefinitionVersion, HandlerHash = node.ContinuationContentHash, ExecutorVersion = Version },
                DefinitionId = definition.DefinitionId, DefinitionVersion = definition.DefinitionVersion, TypeId = definition.TypeId,
                Count = Number(args, "count"), RecipientPlayerId = recipient, DestinationZoneId = zone.ZoneId };
            // 所有实例及子区都只在副本内创建；任一步失败不发布部分奖励。
            var staged = GameStateCloneService.DeepClone(state); var factory = new ItemZoneStateFactory();
            for (int i = 0; i < receipt.Count; i++)
            {
                if (!factory.TryCreateGrantedItem(staged, node.EffectId, receipt.OperationId + "/" + i, definition.DefinitionId, definition.TypeId, zone.ZoneId,
                    new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = recipient }, out string itemId, out error)) return EffectStepResult.Failed(error);
                foreach (var cardZone in definition.CardZones)
                    if (!factory.TryCreateCardZone(staged, itemId, cardZone, out _, out error)) return EffectStepResult.Failed(error);
                receipt.ItemIds.Add(itemId);
            }
            staged.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.EffectId).RewardGrant = receipt;
            GameStateCloneService.CopyTo(state, staged);
            var step = EffectStepResult.Continue("granted");
            foreach (string id in receipt.ItemIds) step.AddChild(EffectSpec.Create(ReceiveTypeId, Object("rewardItemId", NormalizedValue.CreateString(id)), recipient));
            return step;
        }
        private static EffectStepResult Receive(EffectExecutionContext context)
        {
            var node = context.Node; string id = Text(node.NormalizedArguments, "rewardItemId");
            var parent = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            if (parent?.RewardGrant == null || parent.EffectTypeId != TypeId || parent.RewardGrant.RecipientPlayerId != node.PlayerId || !parent.RewardGrant.ItemIds.Contains(id)) throw new InvalidOperationException("reward_grant_parent_proof_missing");
            if (Flag(node.ContinuationData, "acquisitionCompleted")) return EffectStepResult.Completed(node.NormalizedResult);
            var item = context.State.ItemZones.Items.Find(i => i.ItemId == id);
            if (!ContentLifecycleEffectExecutor.IsHeld(context.State, item, node.PlayerId)) throw new InvalidOperationException("reward_grant_instance_missing");
            Set(node, "selectedRewardItemId", NormalizedValue.CreateString(id)); Set(node, "acquisitionCompleted", NormalizedValue.CreateBoolean(true));
            var result = Object("changed", NormalizedValue.CreateBoolean(true), "rewardItemId", NormalizedValue.CreateString(id), "definitionId", NormalizedValue.CreateString(item.DefinitionId),
                "poolRole", NormalizedValue.CreateString("content_granted"), "playerId", NormalizedValue.CreateInteger(node.PlayerId));
            return EffectStepResult.Completed(result).WithFlowStage("finished").AddEvent(new EffectEventRequest { EventType = RewardAcquisitionFlow.AcquiredEventTypeId,
                RouteKey = item.DefinitionId, PlayerId = node.PlayerId, Visibility = "public", Payload = result.Clone() });
        }
        public static bool IsAcquisition(string type) => type == RewardAcquisitionFlow.TypeId || type == ReceiveTypeId;
        private static bool ValidateReceipt(GameState state, EffectNodeRuntimeState node, EffectRegistry registry, out string reason)
        {
            reason = "reward_grant_receipt_invalid"; var r = node.RewardGrant; var a = node.NormalizedArguments;
            if (r == null || r.ProtocolVersion != 1 || ValidateArguments(a).Length > 0 || r.RequestDigest != Digest(a) || r.OperationId != Text(a, "operationId") ||
                r.Count != Number(a, "count") || r.Count < 1 || r.Count > MaximumCount || r.RecipientPlayerId.ToString(CultureInfo.InvariantCulture) != Text(a, "recipientPlayer") ||
                r.DefinitionId != Text(a, "definitionId") || r.TypeId != Text(a, "typeId") || r.SourceRef == null || r.VersionRef == null || r.SourceRef.AuthorizedPlayerId != node.PlayerId ||
                r.VersionRef.ContractVersion != "QY-COMMON/1.0.1" || r.VersionRef.ContentPackHash != state.ContentPackHash || r.VersionRef.ExecutorVersion != Version || r.VersionRef.HandlerHash != node.ContinuationContentHash || r.VersionRef.DefinitionVersion != node.ContinuationDefinitionVersion ||
                r.ItemIds == null || r.ItemIds.Count != r.Count || r.ItemIds.Distinct().Count() != r.Count || !new[] { "granted" }.Contains(node.FlowStage)) return false;
            var actual = Authority(state, node, registry, out string authorityId);
            var declaration = (registry?.AbilityDeclarations ?? state.ContentDefinitions.AbilitySources).SingleOrDefault(d => d.AbilityId == r.SourceRef.AbilityId && d.DefinitionId == r.SourceRef.DefinitionId);
            var definition = state.ContentDefinitions.Awards.SingleOrDefault(d => d.DefinitionId == r.DefinitionId && d.TypeId == r.TypeId && d.DefinitionVersion == r.DefinitionVersion && d.AcquisitionPolicy == "content_granted");
            if (authorityId != r.AuthorityEffectId || !Same(actual, r.SourceRef) || declaration == null || !Permitted(declaration, a, node.PlayerId) || !HandlerAllowed(node, declaration, registry) || definition == null ||
                !state.ItemZones.Zones.Any(z => z.ZoneId == r.DestinationZoneId && z.TemplateId == Text(a, "destination") && z.OwnerPlayerId == r.RecipientPlayerId)) return false;
            for (int i = 0; i < r.Count; i++)
            {
                string id = StableIdFactory.Create("item", state.GameId, r.DefinitionId, node.EffectId + "/" + r.OperationId + "/" + i, "0");
                var item = state.ItemZones.Items.Find(v => v.ItemId == id);
                // 历史奖励可以合法转移或退出；恢复不能要求它仍在取得时的区域。
                if (id != r.ItemIds[i] || item?.DefinitionId != r.DefinitionId || item.TypeId != r.TypeId || item.OwnerSubject.PlayerId != r.RecipientPlayerId ||
                    !state.EffectRuntime.EffectNodes.Any(n => n.ParentEffectId == node.EffectId && n.EffectTypeId == ReceiveTypeId && n.PlayerId == r.RecipientPlayerId && Text(n.NormalizedArguments, "rewardItemId") == id)) return false;
            }
            reason = ""; return true;
        }
        public static bool TryValidate(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "reward_grant_restore_invalid";
            foreach (var n in state.EffectRuntime.EffectNodes)
            {
                if (n.RewardGrant != null && (n.EffectTypeId != TypeId || !ValidateReceipt(state, n, registry, out reason))) return false;
                if (n.EffectTypeId == TypeId && (ValidateArguments(n.NormalizedArguments).Length > 0 || n.RewardGrant == null && n.FlowStage.Length > 0)) return false;
                if (n.EffectTypeId == ReceiveTypeId)
                {
                    var parent = state.EffectRuntime.EffectNodes.Find(p => p.EffectId == n.ParentEffectId);
                    if (parent?.RewardGrant == null || n.PlayerId != parent.RewardGrant.RecipientPlayerId || n.NormalizedArguments?.Properties.Count != 1 ||
                        !parent.RewardGrant.ItemIds.Contains(Text(n.NormalizedArguments, "rewardItemId")) || !new[] { "", "finished" }.Contains(n.FlowStage) ||
                        n.FlowStage == "finished" && (!Flag(n.ContinuationData, "acquisitionCompleted") || Text(n.ContinuationData, "selectedRewardItemId") != Text(n.NormalizedArguments, "rewardItemId"))) return false;
                }
            }
            var receipts = state.EffectRuntime.EffectNodes.Where(n => n.RewardGrant != null).Select(n => n.RewardGrant);
            if (receipts.GroupBy(r => r.AuthorityEffectId + "\n" + r.OperationId).Any(g => g.Count() > 1)) return false;
            reason = ""; return true;
        }
        public static bool HasRawFields(JToken state)
        {
            bool Shape(JToken value, Type type)
            {
                if (type == typeof(string)) return value?.Type == JTokenType.String;
                if (type == typeof(int)) return value?.Type == JTokenType.Integer;
                if (type == typeof(List<string>)) return value is JArray array && array.All(v => v.Type == JTokenType.String);
                if (value is not JObject obj) return false;
                var fields = type.GetFields();
                return obj.Properties().Count() == fields.Length && fields.All(f => Shape(obj[f.Name], f.FieldType));
            }
            foreach (var node in (state?["EffectRuntime"]?["EffectNodes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var receipt = node["RewardGrant"];
                if (receipt != null && receipt.Type != JTokenType.Null && !Shape(receipt, typeof(RewardGrantReceipt))) return false;
                if ((string)node["EffectTypeId"] == TypeId && (string)node["FlowStage"] == "granted" && (receipt == null || receipt.Type == JTokenType.Null)) return false;
            }
            return true;
        }
    }
}
