using System;
using System.Linq;
using System.Text.RegularExpressions;
using YC.Domain.State;

namespace YC.Domain.Items
{
    [Serializable] public sealed class CubeColorDeclaration
    {
        public string Mode = "", Name = "", Rgb = "";
    }
    [Serializable] public sealed class CubeAppearanceDeclaration
    {
        public CubeColorDeclaration Color;
    }
    [Serializable] public sealed class CubeColorView
    {
        public string ItemId = "", DefinitionId = "", TypeId = "", Mode = "", Name = "", Rgb = "";
        public int OwnerPlayerId = -1;
        public YC.Domain.Rules.PlayerColor PlayerColor;
    }
    /// <summary>只读冻结定义及真实所有者；不接受观看者参数，也不推导规则归属。</summary>
    public static class CubeAppearance
    {
        public const string Version = "1.0.0";
        public static CubeAppearanceDeclaration PlayerDefault => new CubeAppearanceDeclaration { Color = new CubeColorDeclaration { Mode = "owner_player" } };
        public static bool TryNormalize(CubeAppearanceDeclaration input, out CubeAppearanceDeclaration result, out string reason)
        {
            result = null; reason = "cube_color_invalid";
            var color = input?.Color;
            if (color == null || color.Mode == null || color.Name == null || color.Rgb == null) return false;
            if (color.Mode == "owner_player") { if (color.Name.Length != 0 || color.Rgb.Length != 0) return false; }
            else if (color.Mode == "fixed") { if (string.IsNullOrWhiteSpace(color.Name) || !Regex.IsMatch(color.Rgb, "\\A[0-9A-Fa-f]{6}\\z")) return false; }
            else return false;
            result = GameStateCloneService.DeepClone(input); result.Color.Rgb = result.Color.Rgb.ToUpperInvariant(); reason = ""; return true;
        }
        public static CubeColorView Resolve(GameState state, string itemId)
        {
            var item = state?.ItemZones?.Items.SingleOrDefault(i => i.ItemId == itemId);
            if (item?.PhysicalKind != ItemPhysicalKind.Cube) throw new InvalidOperationException("cube_item_required");
            var component = state.ContentDefinitions?.Components.SingleOrDefault(d => d.DefinitionId == item.DefinitionId);
            var declaration = component?.CubeAppearance ?? (item.TypeId == "core.player_marker" ? PlayerDefault : null);
            if (component != null && component.TypeId != item.TypeId || !TryNormalize(declaration, out var normalized, out _)) throw new InvalidOperationException("cube_frozen_appearance_missing_or_invalid");
            var view = new CubeColorView { ItemId = item.ItemId, DefinitionId = item.DefinitionId, TypeId = item.TypeId, Mode = normalized.Color.Mode, Name = normalized.Color.Name, Rgb = normalized.Color.Rgb,
                OwnerPlayerId = item.OwnerSubject.Kind == ItemSubjectKind.Player ? item.OwnerSubject.PlayerId : -1 };
            if (view.Mode == "owner_player")
            {
                var player = item.OwnerSubject.Kind == ItemSubjectKind.Player ? state.FindPlayer(item.OwnerSubject.PlayerId) : null;
                if (player == null || !Enum.IsDefined(typeof(YC.Domain.Rules.PlayerColor), player.Color)) throw new InvalidOperationException("cube_owner_player_required");
                view.PlayerColor = player.Color;
            }
            return view;
        }
        public static bool ValidateFrozen(GameState state, out string reason)
        {
            reason = "cube_frozen_appearance_invalid";
            var types = ItemTypeRegistry.CreateBuiltIn();
            foreach (var definition in state.ContentDefinitions?.Components ?? new System.Collections.Generic.List<YC.Domain.Rules.ComponentContentDefinition>())
            {
                bool cube = types.Find(definition.TypeId)?.PhysicalKind == ItemPhysicalKind.Cube;
                var appearance = definition.CubeAppearance ?? (definition.TypeId == "core.player_marker" ? PlayerDefault : null);
                if (!cube) { if (appearance != null) return false; continue; }
                if (!TryNormalize(appearance, out var normalized, out _) || normalized.Color.Rgb != appearance.Color.Rgb) return false;
            }
            try { foreach (var item in state.ItemZones.Items.Where(i => i.PhysicalKind == ItemPhysicalKind.Cube)) Resolve(state, item.ItemId); }
            catch (InvalidOperationException) { return false; }
            reason = ""; return true;
        }
    }
}
