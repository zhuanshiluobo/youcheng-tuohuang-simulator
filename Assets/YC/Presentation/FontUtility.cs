using UnityEngine;

namespace YC.Presentation
{
    public static class FontUtility
    {
        private static readonly string[] CjkFallback = { "SimHei", "Microsoft YaHei", "Arial" };
        private static readonly string[] LatinFallback = { "Arial", "Microsoft YaHei", "SimHei" };

        public static Font GetCjkFont(int fontSize)
        {
            return Font.CreateDynamicFontFromOSFont(CjkFallback, fontSize);
        }

        public static Font GetLatinFont(int fontSize)
        {
            return Font.CreateDynamicFontFromOSFont(LatinFallback, fontSize);
        }
    }
}
