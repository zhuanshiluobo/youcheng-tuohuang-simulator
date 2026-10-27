using System;
using System.Collections.Generic;
using YC.Presentation.Validation;

// 独立策略夹具。输入均为模拟；不启动游戏，不算UI、Lua、存档或正常多局验收。
internal static class ActionCleanupPolicySmoke
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("失败：" + name);
        passed++; Console.WriteLine("通过：" + name);
    }
    private static ActionCleanupCandidate C(string key, string coverage = null) => new ActionCleanupCandidate {
        Key = key, CoverageKey = coverage ?? key, Enabled = true, Facts = "代数1-本人库存", Kind = "收尾" };
    public static int Main()
    {
        var p = new ActionCleanupCoveragePolicy();
        var used = C("窗口1:来源1"); var gap = C("窗口1:来源2");
        p.Begin(used);
        Check(p.Completed(used.CoverageKey) == 0, "点击不算结清");
        Check(p.Choose(new[] { used, gap }) == gap, "首次尝试未结清时不补点，可继续独立来源");
        p.Complete(used, "completed");
        var nextWindow = C("窗口2:来源1", used.CoverageKey);
        Check(p.Choose(new[] { nextWindow, gap }) == gap, "优先尚未实际结清的来源");
        var historical = C("新局:旧覆盖来源"); historical.PriorCompleted = 3;
        Check(p.Choose(new[] { historical, gap }) == gap, "同版本历史账只参与排序，优先补跨局缺口");
        Check(p.Completed(historical.CoverageKey) == 0, "历史排序不能冒充本轮实际结清");
        var required = C("必做"); required.Required = true; required.Order = 99;
        Check(p.Choose(new[] { gap, required }) == required, "必做效果先于可选缺口");
        var noResource = C("无资源"); noResource.Enabled = false; noResource.Reason = "insufficient_resources";
        var noTarget = C("无目标"); noTarget.Enabled = false; noTarget.Reason = "no_legal_target";
        var responding = C("响应中"); responding.Pending = true;
        Check(p.Choose(new[] { noResource, noTarget, responding }) == null, "无资源、无目标、响应中均不强行选择");
        Check(p.BlockReason(noResource) == "insufficient_resources" && p.BlockReason(noTarget) == "no_legal_target", "保留原禁用原因，不把缺项伪装通过");
        noResource.Enabled = true; noResource.Reason = ""; noResource.Facts = "正常获得资源后";
        Check(p.Choose(new[] { noResource }) == noResource, "此前只看见禁用来源不耗尝试资格，正常资源变化后可达");

        var repeat = C("重复来源"); repeat.Repeatable = true;
        p.Begin(repeat); repeat.Facts = "代数2";
        Check(p.Choose(new[] { repeat }) == null, "资格代数变化不能替代前一次结清证据");
        p.Complete(repeat, "completed");
        Check(p.Choose(new[] { repeat }) == repeat, "同源结清且正式事实改变后允许有限第二次");
        p.Begin(repeat); p.Complete(repeat, "completed"); repeat.Facts = "代数3";
        Check(p.Choose(new[] { repeat }) == null, "可重复效果有尝试上限，不耗尽库存或无限循环");
        var sameFacts = C("事实未变"); sameFacts.Repeatable = true;
        p.Begin(sameFacts); p.Complete(sameFacts, "completed");
        Check(p.Choose(new[] { sameFacts }) == null, "完成后仍是同一份来源事实，不重复点击旧行");
        foreach (string outcome in new[] { "declined", "condition_failed", "failed" })
        {
            var rejected = C("未执行:" + outcome); rejected.Repeatable = true;
            p.Begin(rejected); p.Complete(rejected, outcome); rejected.Facts = "新代数";
            Check(p.Completed(rejected.CoverageKey) == 0 && p.Choose(new[] { rejected }) == null, outcome + "不算效果通过且不重复发动");
        }
        Check(!p.ChooseDecline("来源", true, true, false), "首次合法条件优先执行，不能总放弃");
        Check(p.ChooseDecline("来源", true, true, true), "已正常结清来源才覆盖一次主动放弃");
        p.ConfirmDecline("来源");
        Check(!p.ChooseDecline("来源", true, true, true), "主动放弃覆盖后回到正常使用");
        Check(p.ChooseDecline("不足", false, true, false) && !p.ChooseDecline("必付", false, false, true), "资源不足仅使用正式放弃权限，必付不虚构拒绝");
        bool duplicateCompletionRejected = false;
        try { p.Complete(repeat, "completed"); } catch (InvalidOperationException) { duplicateCompletionRejected = true; }
        Check(duplicateCompletionRejected, "结清证据不能重复累计");
        Check(p.Choose(new ActionCleanupCandidate[0]) == null, "未出现的来源继续留缺口，不重开或编造候选");
        var laterWindow = C("下一回合:重复来源", repeat.CoverageKey); laterWindow.Repeatable = true;
        Check(p.Choose(new[] { laterWindow }) == laterWindow, "有限次数约束当前窗口，后续正规回合仍可继续覆盖");
        Console.WriteLine("策略夹具通过数：" + passed + "；正常游戏验收数：0；恢复验证数：0");
        return 0;
    }
}
