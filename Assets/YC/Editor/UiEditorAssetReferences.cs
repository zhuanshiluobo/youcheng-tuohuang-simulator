using System;
using UnityEditor;
using UnityEngine;
using YC.Editor;

namespace YC.EditorTools
{
    internal static class UiEditorAssetReferences
    {
        internal const string CjkFontPath =
            "Assets/YC/Resources/Fonts/CJK/NotoSansCJKsc-Regular.otf";
        internal const string LatinFontPath =
            "Assets/YC/Resources/Fonts/Latin/NotoSans-Regular.ttf";

        internal static Font CjkFont => LoadFont(CjkFontPath, "CJK");
        internal static Font LatinFont => LoadFont(LatinFontPath, "Latin");

        static UiEditorAssetReferences()
        {
            UiThemeBuildReadiness.InitializeRequiredTheme();
        }

        private static Font LoadFont(string path, string family)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(path);
            if (font == null)
            {
                throw new InvalidOperationException(
                    "Missing serialized " + family + " font asset at " + path + ".");
            }

            return font;
        }
    }
}
