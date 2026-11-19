using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    /// <summary>从本局真实实例及当前观察者投影派生游戏盒；不写入旧位置列表。</summary>
    public static class GameBoxStateProjector
    {
        public static GameBoxState Project(GameState state, ItemZoneStateView visibleItems)
        {
            var result = new GameBoxState();
            if (state?.ItemZones == null || visibleItems == null) return result;
            var zones = state.ItemZones.Zones.ToDictionary(zone => zone.ZoneId, StringComparer.Ordinal);
            var items = state.ItemZones.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
            var types = ItemTypeRegistry.CreateBuiltIn();
            var definitionBacks = ItemZoneQuery.BuildCharacterDefinitionBacks(state);
            // 元数据只确定语义，盒内数量与身份仍只从已有实例和观察者可见性取得。
            var enterpriseDefinitions = new HashSet<string>(state.ContentDefinitions?.EnterpriseDefinitionIds ??
                new List<string>(), StringComparer.Ordinal);
            foreach (var item in visibleItems.Items)
            {
                if (item.PhysicalKind != ItemPhysicalKind.Card || !items.TryGetValue(item.ItemId, out var actual) ||
                    !TryGetBoxContext(actual, zones, items, out int privateForPlayer)) continue;
                GameBoxCardKind kind;
                if (types.IsAssignableTo(item.TypeId, "core.character_card")) kind = GameBoxCardKind.Character;
                else if (types.IsAssignableTo(item.TypeId, "core.event_card")) kind = GameBoxCardKind.Event;
                else if (item.TypeId == "core.board_card" && enterpriseDefinitions.Contains(item.DefinitionId))
                    kind = GameBoxCardKind.EnterpriseCooperation;
                else continue;
                var face = LocalValue(actual, "faceUp");
                var card = new GameBoxCardState { InstanceId = item.ItemId, CardId = item.DefinitionId,
                    PrivateForPlayerId = privateForPlayer, Kind = kind, BackColor = (PlayerColor)(-1),
                    FaceUp = face?.Kind == NormalizedValueKind.Boolean ? face.BooleanValue : privateForPlayer == -1 };
                PopulateBack(state, actual, kind, types, definitionBacks, card);
                result.Cards.Add(card);
            }
            // 回盒区无历史牌堆顺序；固定种类与实例顺序避免刷新时跳动。
            result.Cards.Sort((left, right) =>
            {
                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0 ? kind : StringComparer.Ordinal.Compare(left.InstanceId, right.InstanceId);
            });
            return result;
        }

        private static NormalizedValue LocalValue(ItemInstanceState item, string name) =>
            item.LocalState?.Properties?.Find(entry => entry.Name == name)?.Value;

        private static void PopulateBack(GameState state, ItemInstanceState item, GameBoxCardKind kind,
            ItemTypeRegistry types, IDictionary<string, string> definitionBacks, GameBoxCardState card)
        {
            // 干员物理背面由类型决定，旧实例的基础颜色配置不能覆盖它。
            if (kind == GameBoxCardKind.Character && types.IsAssignableTo(item.TypeId, "core.operator_card"))
            {
                card.BackVisualKey = "operator_back";
                return;
            }
            bool basic = kind == GameBoxCardKind.Character && types.IsAssignableTo(item.TypeId, "core.basic_character_card");
            bool generated = kind == GameBoxCardKind.Character && types.IsAssignableTo(item.TypeId, "core.generated_character_card");
            if (generated)
            {
                string definedBack = ItemZoneQuery.ResolveGeneratedDefinitionBack(item.DefinitionId, definitionBacks);
                if (!string.IsNullOrEmpty(definedBack)) { card.BackVisualKey = definedBack; return; }
            }
            var visual = LocalValue(item, "backVisualKey");
            if (!basic && visual?.Kind == NormalizedValueKind.String && !string.IsNullOrWhiteSpace(visual.StringValue) &&
                (!generated || ItemZoneQuery.IsPublicCharacterBack(visual.StringValue)))
            {
                card.BackVisualKey = visual.StringValue;
                return;
            }
            if (kind == GameBoxCardKind.Character)
            {
                // 基础角色沿原归属玩家颜色；转交和遗留配置都不能改背面。
                var configured = basic ? null : LocalValue(item, "backColor");
                PlayerColor? color = configured?.Kind == NormalizedValueKind.Integer &&
                    configured.IntegerValue >= int.MinValue && configured.IntegerValue <= int.MaxValue &&
                    Enum.IsDefined(typeof(PlayerColor), (int)configured.IntegerValue) ? (PlayerColor?)configured.IntegerValue : null;
                if (!color.HasValue && item.OwnerSubject.Kind == ItemSubjectKind.Player)
                {
                    var owner = state.FindPlayer(item.OwnerSubject.PlayerId);
                    if (owner != null && Enum.IsDefined(typeof(PlayerColor), owner.Color)) color = owner.Color;
                }
                if (color.HasValue)
                {
                    card.BackColor = color.Value;
                    card.BackVisualKey = "character_back_" + ((int)color.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else if (kind == GameBoxCardKind.EnterpriseCooperation) card.BackVisualKey = "enterprise_back";
        }

        private static bool TryGetBoxContext(ItemInstanceState item, IDictionary<string, ZoneInstanceState> zones,
            IDictionary<string, ItemInstanceState> items, out int privateForPlayer)
        {
            privateForPlayer = -1;
            string zoneId = item.ZoneId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(zoneId) && visited.Add(zoneId) && zones.TryGetValue(zoneId, out var zone))
            {
                if (zone.VisibilityPolicyId == ItemZoneVisibility.SecretRetired)
                    privateForPlayer = item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId > 0
                        ? item.ControllerSubject.PlayerId : 0;
                if (zone.ParentKind == ZoneParentKind.Root) return zone.TemplateId == "core.zone.game_box";
                if (zone.ParentKind == ZoneParentKind.Zone) zoneId = zone.ParentId;
                else if (zone.ParentKind == ZoneParentKind.Item && items.TryGetValue(zone.ParentId, out var carrier))
                {
                    item = carrier;
                    zoneId = carrier.ZoneId;
                }
                else return false;
            }
            return false;
        }
    }
}
