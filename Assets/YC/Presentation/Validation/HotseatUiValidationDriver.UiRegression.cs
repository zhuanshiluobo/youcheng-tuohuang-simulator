#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.IO;
using UnityEngine;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private bool UiRegressionMode => Argument("--yc-ui-regression=") == "true";
        private bool uiRegressionPointerPaused;
        private IEnumerator UiRegressionCheckpoint()
        {
            if (!UiRegressionMode || uiRegressionPointerPaused || State.Phase != GamePhase.ActionRound1 || Request != null) yield break;
            uiRegressionPointerPaused = true;
            stage = "限定UI回归等待真实鼠标检查";
            yield return Capture("限定回归正常入口与五槽");
            Write("等待真实鼠标检查.json", new {State.GameId, State.ContentPackHash, Revision = State.EffectRuntime.StateRevision, LocalPlayer,
                UI = CurrentPaymentView()?.Entrepreneurs, Commands = commands.Count,
                Instruction = "驱动暂停，无输入；外部鼠标检查后由本轮控制文件继续"});
            var start = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(output, "继续正常流程.txt")) && Time.realtimeSinceStartup - start < 600f) yield return null;
            Require(File.Exists(Path.Combine(output, "继续正常流程.txt")), "真实鼠标检查等待超时；未自动跳过");
            Require(CurrentPaymentView() != null, "真实鼠标检查后正式显示身份不匹配");
        }
        private bool UiRegressionShouldStop => UiRegressionMode && uiRegressionPointerPaused && State.Round >= 2;
        private IEnumerator UiRegressionStop()
        {
            stage = "限定UI回归首回合正常流程结束";
            yield return Capture("限定回归首回合结束边界");
            Write("限定正常回归停止.json", new {State.GameId, State.ContentPackHash, State.Round, State.Phase, LocalPlayer,
                Revision = State.EffectRuntime.StateRevision, Commands = commands, WholeGameCompleted = false,
                RemainingRounds = "后续交019；本轮不冒称完整局"});
        }
    }
}
#endif
