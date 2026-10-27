using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace YC.Tests.PlayMode.ExpansionRules
{
    internal static class CommonUiTestCapture
    {
        // 复用上一轮已核的离屏源UI截图，不运行夹具初始化，不修改源资产。
        internal static void Save(GameObject hud, string label)
        {
            var directory = Environment.GetEnvironmentVariable("YC_QY017_SCREENSHOT_DIRECTORY");
            if (string.IsNullOrEmpty(directory)) return;
            var capture = new FacilitySupplySelectionUiTests();
            typeof(FacilitySupplySelectionUiTests).GetField("hud", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(capture, hud);
            typeof(FacilitySupplySelectionUiTests).GetMethod("CaptureCanvas", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(capture, new object[] { Path.Combine(directory, label + "-源资产隔离渲染.png") });
        }
    }
}
