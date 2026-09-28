using System;

namespace YC.Presentation
{
    [Serializable]
    public sealed class MainActionInteractionText
    {
        public string title = "主要行动";
        public string confirmPrompt = "确认后开始支付和结算，不能再撤销；确认前可按 Esc 或左下角撤销取消整个行动。";
        public string confirm = "确认执行";
        public string dispatchSource = "在地图上点击高亮的己方影响力；两次移动都只记录草案，确认后一起执行。";
        public string dispatchTarget = "在地图上点击高亮槽位，选择此影响力的目标位置。";
        public string dispatchContinue = "继续规划第二次调度，或完成当前草案。";
        public string dispatchFinish = "完成调度草案";
        public string dispatchAdd = "再调度一个影响力";
        public string moveTarget = "在地图上点击高亮地点，选择城市移动目的地。";
        public string specialChoose = "选择特殊行动与宣告标记。";
        public string specialPayment = "选择特殊行动的支付组合，最终确认前不会扣费。";
        public string buildChoose = "选择要建设的设施。";
        public string paymentFormat = "源石 {0}，铁 {1}";
        public string specialFormat = "{0}（标记 {1}）";

        public string dispatchSummary = "调度：{0}";
        public string moveSummary = "城市移动目的地：{0}";
        public string buildSummary = "建设：{0}；支付：{1}；城市面板位置：{2}";
        public string confirmStatus = "请在确认窗核对行动；确认前可按 Esc 取消。";
        public string automaticRecipients = "按路线收费规则结算";
        public string locationSlotFormat = "{0} 地点的第 {1} 个影响力槽位";
        public string routeSlotFormat = "{0} 航道的第 {1} 个影响力槽位";
        public string recipientFormat = "{0}：玩家 {1}";
        public string exploreSummary = "探索路线：{0}\n航道：{1}\n落点：{2}\n指定收费方：{3}";
        public string specialSummary = "特殊行动：{0}；宣告标记：{1}";
        public string explorePath = "在地图上选择高亮的探索目的地；自动选择最低费用路线，最终确认前不会支付或抽牌。";
        public string exploreRoute = "候选路线的收费方不同，请点击地图上高亮航道的槽位选择所需路线；无需逐段经过中间地点。";
        public string exploreFinishFormat = "选择探索落点：{0}";
        public string buildSlot = "选择建设位置，下一步确认完整草案。";
        public string invalidMapTarget = "请在地图上点击高亮的合法目标；Esc 或左下角撤销可取消整个草案。";

        public string Prompt(string key)
        {
            switch (key)
            {
                case "action.main.confirm": return confirmPrompt;
                case "action.dispatch.source": return dispatchSource;
                case "action.dispatch.target": return dispatchTarget;
                case "action.dispatch.continue": return dispatchContinue;
                case "action.move.target": return moveTarget;
                case "action.explore.target": return explorePath;
                case "action.explore.path": return exploreRoute;
                case "action.special.choose": return specialChoose;
                case "action.special.payment": return specialPayment;
                default: return null;
            }
        }
    }
}
