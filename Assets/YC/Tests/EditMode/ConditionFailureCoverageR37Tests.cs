using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 合成只读运行树的驱动归因检查；不建立对局，不计真实付款、奖励或正常游戏验收。
    public sealed class ConditionFailureCoverageR37Tests
    {
        [TestCase("accepted_left", "condition_not_met")]
        [TestCase("wrong_failed_id", "failed")]
        [TestCase("met_result", "failed")]
        [TestCase("right_attached", "failed")]
        [TestCase("right_unlisted", "failed")]
        [TestCase("faulted_left", "failed")]
        [TestCase("unknown_failure", "failed")]
        [TestCase("missing_groups", "failed")]
        public void OnlyExactAcceptedLeftFailureWithoutExecutedRightCanSettleSource(string variant, string expected)
        {
            var root = new EffectNodeRuntimeState { EffectId = "source", EffectTypeId = "fixture.source",
                Status = EffectNodeStatus.Completed, ChildEffectIds = new List<string> { "condition" } };
            var condition = new EffectNodeRuntimeState { EffectId = "condition", ParentEffectId = root.EffectId,
                EffectTypeId = EffectTypeIds.Condition, Status = EffectNodeStatus.Completed, FlowStage = "left:0",
                NormalizedArguments = Object("ruleCondition", NormalizedValue.CreateBoolean(true)),
                NormalizedResult = Object("conditionMet", NormalizedValue.CreateBoolean(false),
                    "failedChildId", NormalizedValue.CreateString("left"), "outcome", NormalizedValue.CreateString("condition_not_met")),
                ChildEffectIds = new List<string> { "left" }, NestedGroupSizes = new List<int> { 1, 1 },
                NestedEffects = new List<EffectSpecRuntimeState> {
                    new EffectSpecRuntimeState { EffectTypeId = "fixture.left" },
                    new EffectSpecRuntimeState { EffectTypeId = "fixture.right" } } };
            var left = new EffectNodeRuntimeState { EffectId = "left", ParentEffectId = condition.EffectId,
                EffectTypeId = "fixture.left", Status = EffectNodeStatus.Failed, FailureReason = "fixture_unmet" };
            var nodes = new List<EffectNodeRuntimeState> { root, condition, left };
            if (variant == "wrong_failed_id") condition.NormalizedResult.Properties.Find(p => p.Name == "failedChildId").Value = NormalizedValue.CreateString("another");
            if (variant == "met_result") condition.NormalizedResult.Properties.Find(p => p.Name == "conditionMet").Value = NormalizedValue.CreateBoolean(true);
            if (variant == "right_attached" || variant == "right_unlisted")
            {
                nodes.Add(new EffectNodeRuntimeState { EffectId = "right", ParentEffectId = condition.EffectId,
                    EffectTypeId = "fixture.right", Status = EffectNodeStatus.Completed });
                if (variant == "right_attached") condition.ChildEffectIds.Add("right");
            }
            if (variant == "faulted_left") left.Status = EffectNodeStatus.Faulted;
            if (variant == "unknown_failure")
            {
                root.ChildEffectIds.Add("unknown");
                nodes.Add(new EffectNodeRuntimeState { EffectId = "unknown", ParentEffectId = root.EffectId,
                    EffectTypeId = "fixture.unknown", Status = EffectNodeStatus.Failed, FailureReason = "unexplained" });
            }
            if (variant == "missing_groups") condition.NestedGroupSizes.Clear();
            var type = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            var method = type.GetMethod("DriverCleanupSourceOutcome", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var args = new object[] { root.EffectId, nodes, null, null };
            Assert.That((string)method.Invoke(null, args), Is.EqualTo(expected));
            Assert.That(left.Status, Is.EqualTo(variant == "faulted_left" ? EffectNodeStatus.Faulted : EffectNodeStatus.Failed));
            Assert.That(left.FailureReason, Is.EqualTo("fixture_unmet"), "归因不能改写原失败");
            if (variant == "accepted_left")
            {
                Assert.That(((IList)args[2]).Count, Is.EqualTo(1));
                Assert.That(((IList)args[3]).Count, Is.Zero);
            }
            else Assert.That(((IList)args[3]).Count, Is.GreaterThan(0));
        }
    }
}
