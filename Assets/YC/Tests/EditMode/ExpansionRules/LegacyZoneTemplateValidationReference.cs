using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
namespace YC.Tests.EditMode.ExpansionRules
{
    // 固定修改前的完整校验主体；只用于结果/短路/异常对照，不计正常界面验收。
    internal sealed class LegacyZoneTemplateValidationReference
    {
        private readonly ItemTypeRegistry types;
        private readonly ZoneTemplateRegistry templates;
        internal LegacyZoneTemplateValidationReference(ItemTypeRegistry types, ZoneTemplateRegistry templates)
        { this.types = types; this.templates = templates; }
        private ZoneTemplateDefinition Find(string id) => templates.Find(id);
        private bool MatchesType(string typeId, List<string> allowed) => allowed.Exists(parent => types.IsAssignableTo(typeId, parent));
        private static bool IsPolicy(string value, params string[] allowed) => Array.IndexOf(allowed, value) >= 0;
        private static bool Reject(string code, out string reasonCode) { reasonCode = code; return false; }
        public bool ValidateCardZoneDeclaration(string carrierTypeId, CardZoneDefinition declaration, out string reasonCode)
        {
            reasonCode = string.Empty;
            if (declaration == null) return Reject("cardZones_required", out reasonCode);
            var template = Find(declaration.ZoneTemplateId);
            if (template == null || !template.AllowExternalCardBinding) return Reject("zoneTemplateId_rejected", out reasonCode);
            if (!MatchesType(carrierTypeId, template.AllowedCarrierTypeIds)) return Reject("carrierTypeId_rejected", out reasonCode);
            if (string.IsNullOrWhiteSpace(declaration.LocalZoneId)) return Reject("localZoneId_required", out reasonCode);
            if (declaration.AcceptedItemKinds == null || declaration.AcceptedItemKinds.Count == 0) return Reject("acceptedItemKinds_required", out reasonCode);
            foreach (var kind in declaration.AcceptedItemKinds)
                if (!Enum.IsDefined(typeof(ItemPhysicalKind), kind) || !template.AcceptedItemKinds.Contains(kind)) return Reject("acceptedItemKinds_rejected", out reasonCode);
            if (declaration.AcceptedTypeIds == null) return Reject("acceptedTypeIds_required", out reasonCode);
            // 空过滤器不能扩大原模板的子类型范围。
            if (template.AcceptedTypeIds.Count > 0 && declaration.AcceptedTypeIds.Count == 0) return Reject("acceptedTypeIds_rejected", out reasonCode);
            foreach (string typeId in declaration.AcceptedTypeIds)
            {
                var type = types.Find(typeId);
                if (type == null || !declaration.AcceptedItemKinds.Contains(type.PhysicalKind) ||
                    (template.AcceptedTypeIds.Count > 0 && !MatchesType(typeId, template.AcceptedTypeIds))) return Reject("acceptedTypeIds_rejected", out reasonCode);
            }
            if (declaration.CapacityPolicy != "bounded" && declaration.CapacityPolicy != "unbounded") return Reject("capacityPolicy_rejected", out reasonCode);
            if ((declaration.CapacityPolicy == "bounded" && (!declaration.Capacity.HasValue || declaration.Capacity.Value < 0)) ||
                (declaration.CapacityPolicy == "unbounded" && declaration.Capacity.HasValue)) return Reject("capacity_rejected", out reasonCode);
            if (template.Capacity >= 0 && (declaration.CapacityPolicy == "unbounded" || declaration.Capacity > template.Capacity)) return Reject("capacity_rejected", out reasonCode);
            if (declaration.OrderingKind != template.OrderingKind) return Reject("orderingKind_rejected", out reasonCode);
            if (!ItemZoneVisibility.IsKnown(declaration.VisibilityPolicyId) || declaration.VisibilityPolicyId == ItemZoneVisibility.SecretRetired) return Reject("visibilityPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.EntryPolicyId, "core.explicit_transfer", "core.owned_player_marker", "core.removed_owned_influence")) return Reject("entryPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.ExitPolicyId, "core.explicit_transfer", "core.card_payment")) return Reject("exitPolicyId_rejected", out reasonCode);
            if (!IsPolicy(declaration.CarrierExitPolicyId, "core.preserve_contents", "core.return_contents_before_exit")) return Reject("carrierExitPolicyId_rejected", out reasonCode);
            if (declaration.Slots == null || declaration.Slots.Any(string.IsNullOrWhiteSpace) || declaration.Slots.Count != declaration.Slots.Distinct().Count() || declaration.Slots.Count > 0 && template.OrderingKind != ZoneOrderingKind.Track) return Reject("slots_invalid", out reasonCode);
            if (!ValidateCarrierPolicies(declaration, out reasonCode)) return false;
            return true;
        }
        public static bool ValidateCarrierPolicies(CardZoneDefinition declaration, out string reason)
        {
            reason = "carrier_payment_policy_invalid";
            if (declaration.EmptyExitPolicyId != "" && declaration.EmptyExitPolicyId != "core.discard_carrier_when_empty") return false;
            if (declaration.PaymentPolicyId == "") { if (declaration.MaxMarkersPerPayment != 0) return false; }
            else if (declaration.PaymentPolicyId != "core.basic_resource_substitution" || declaration.MaxMarkersPerPayment != 2 ||
                declaration.ZoneTemplateId != "core.zone.card_cube_storage" || declaration.ExitPolicyId != "core.card_payment" ||
                declaration.AcceptedTypeIds.Count != 1 || declaration.AcceptedTypeIds[0] != "core.player_marker") return false;
            reason = ""; return true;
        }

    }
}
