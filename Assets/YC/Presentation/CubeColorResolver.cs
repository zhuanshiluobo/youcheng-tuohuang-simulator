using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>地图、供应和卡上详情共用。017把返回色用于获准的物体或贴图组件，类型/数量仍单独绑定。</summary>
    public static class CubeColorResolver
    {
        public static Color Resolve(GameState state, string itemId, float alpha = 1f) => Resolve(CubeAppearance.Resolve(state, itemId), alpha);
        public static Color Resolve(GameStateView state, string itemId, float alpha = 1f) => Resolve(state.CubeColors.Single(c => c.ItemId == itemId), alpha);
        public static Color Resolve(CubeColorView color, float alpha = 1f)
        {
            if (color.Mode == "owner_player") return UiTheme.GetPlayerColor(color.PlayerColor, alpha);
            if (color.Mode != "fixed" || color.Rgb.Length != 6) throw new InvalidOperationException("cube_color_projection_invalid");
            int rgb = int.Parse(color.Rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);
        }
    }
}
