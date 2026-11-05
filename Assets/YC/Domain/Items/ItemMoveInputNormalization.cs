using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class ItemMoveInputNormalizationV1
    {
        public string ProtocolVersion = "item.move.input.v1";
        public NormalizedValue OriginalArguments;
        public string OriginalArgumentsDigest = "", CanonicalArgumentsDigest = "";
    }

    // 仅新输入的有限语法归一化；恢复入口从不据此升级旧节点或旧收据。
    public static partial class ItemMoveInputNormalization
    {
        public const string InvalidProvenance = "item_move_input_normalization_invalid";
        public static string ValidateRaw(JObject node) => node["ItemMoveInput"] is JToken input && input.Type != JTokenType.Null &&
            !ItemMovementV4.RawShape(input,typeof(ItemMoveInputNormalizationV1)) ? InvalidProvenance : "";
        public static bool IsLegacySingle(string type, NormalizedValue args) => type == TypeId &&
            Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Number(args, "apiVersion") == 1;
        public static bool IsSupportedLegacy(string type, NormalizedValue args) => type == TypeId &&
            Value(args,"apiVersion")?.Kind == NormalizedValueKind.Integer && new[]{1,2,3}.Contains(Number(args,"apiVersion"));

        private static NormalizedValue MapLeg(NormalizedValue original)
        {
            var source=Value(original,"expectedSource").Clone();
            if(Value(source,"slotId")!=null&&Text(source,"slotId").Length==0)source.Properties.RemoveAll(p=>p.Name=="slotId");
            var leg=Object("selector",Object("mode",NormalizedValue.CreateString("explicit"),
                    "itemIds",Number(original,"apiVersion")==3?Strings(Value(original,"itemIds").Items.Select(i=>i.StringValue)):
                        Strings(Value(original,"itemRefs").Items.Select(i=>Text(i,"itemId"))),"expectedSource",source),
                "destination",Value(original,"destination").Clone(),"insertion",Value(original,"insertion").Clone(),
                "operationProfileId",Value(original,"operationProfileId").Clone(),"requiredCount",NormalizedValue.CreateInteger(Number(original,"requiredCount")));
            foreach(string name in new[]{"carrierPolicy","reveal"})
                if(Value(original,name)!=null)leg.Properties.Add(new NormalizedValueEntry{Name=name,Value=Value(original,name).Clone()});
            leg.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return leg;
        }

        private static bool TryMap(NormalizedValue original, out NormalizedValue canonical, out string reason)
        {
            canonical = null;
            reason = ValidateArguments(TypeId, original);
            if (reason.Length > 0) return false;
            if (!IsSupportedLegacy(TypeId, original)) { reason = InvalidProvenance; return false; }
            bool batch=Number(original,"apiVersion")==2;
            canonical = Object("apiVersion", NormalizedValue.CreateInteger(4), "player", NormalizedValue.CreateString(
                    int.Parse(Text(original,"player"),NumberStyles.None,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                "operation", NormalizedValue.CreateString(batch?"transfer_batch":"transfer"));
            if(batch)
            {
                var legs=Value(original,"transfers").Items;
                var touched=legs.SelectMany(l=>new[]{Text(Value(l,"expectedSource"),"zoneId"),Text(Value(l,"destination"),"zoneId")}).ToHashSet(StringComparer.Ordinal);
                var expected=Value(original,"expectedOrders");
                if(!touched.SetEquals(expected.Items.Select(e=>Text(e,"zoneId"))))
                {reason="item_move_legacy_input_not_representable:transfer_batch_all_zones_required";return false;}
                canonical.Properties.Add(new NormalizedValueEntry{Name="transfers",Value=NormalizedValue.CreateArray(legs.Select(MapLeg).ToList())});
                canonical.Properties.Add(new NormalizedValueEntry{Name="expectedOrders",Value=expected.Clone()});
                // 外部operationId仅保留于原参数记录，执行/恢复使用真实Host节点的共同收据身份。
            }
            else canonical.Properties.AddRange(MapLeg(original).Properties);
            canonical.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
            reason = ItemMovementV4.ValidateArguments(canonical);
            if (reason.Length > 0) { reason = "item_move_legacy_input_not_representable:" + reason; return false; }
            return true;
        }

        public static bool TryNormalize(EffectSpec spec, out string reason)
        {
            if (!IsSupportedLegacy(spec.EffectTypeId, spec.NormalizedArguments))
                return TryValidate(spec.EffectTypeId, spec.NormalizedArguments, spec.ItemMoveInput, out reason);
            if (spec.ItemMoveInput != null) { reason = InvalidProvenance; return false; }
            if (!string.IsNullOrEmpty(spec.DefinitionVersion) && spec.DefinitionVersion != TransferDefinitionVersion)
            { reason = "item_move_contract_version_unsupported"; return false; }
            if (!TryMap(spec.NormalizedArguments, out var canonical, out reason)) return false;
            var original = spec.NormalizedArguments.Clone();
            spec.NormalizedArguments = canonical;
            spec.DefinitionVersion = TransferDefinitionVersion;
            spec.ItemMoveInput = new ItemMoveInputNormalizationV1 { OriginalArguments = original,
                OriginalArgumentsDigest = ItemTransferService.MovementV4Digest(original), CanonicalArgumentsDigest = ItemTransferService.MovementV4Digest(canonical) };
            return true;
        }

        public static bool TryValidate(string type, NormalizedValue args, ItemMoveInputNormalizationV1 input, out string reason)
        {
            if (input == null) { reason = ""; return true; }
            var memo = Newtonsoft.Json.JsonConvert.DefaultSettings == null ? currentValidationMemo : null;
            if (memo != null && memo.Matches(type, args, input))
            {
                using var diagnostic = GameStateCloneService.MeasureDiagnostic("item_move_input.reuse");
                reason = "";
                return true;
            }
            bool valid = TryValidateUncached(type, args, input, out reason);
            if (valid) memo?.Remember(type, args, input);
            return valid;
        }

        private static bool TryValidateUncached(string type, NormalizedValue args, ItemMoveInputNormalizationV1 input, out string reason)
        {
            reason = "";
            if (input == null) return true; // 原生v4没有旧输入；此记录不授予额外权限。
            reason = InvalidProvenance;
            if (input.ProtocolVersion != "item.move.input.v1" || !ItemMovementV4.IsV4(type,args) ||
                input.OriginalArguments == null || !input.OriginalArguments.IsValid() ||
                !IsSupportedLegacy(type,input.OriginalArguments) || !TryMap(input.OriginalArguments,out var mapped,out _) ||
                input.OriginalArgumentsDigest != ItemTransferService.MovementV4Digest(input.OriginalArguments) ||
                input.CanonicalArgumentsDigest != ItemTransferService.MovementV4Digest(args) ||
                input.CanonicalArgumentsDigest != ItemTransferService.MovementV4Digest(mapped)) return false;
            reason = ""; return true;
        }
    }
}
