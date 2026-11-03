#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // 驾驶策略只读本人真实库存及正式价格；不改规则候选、次数上限或付款资格。
        private static bool TryPlanTradeAllocation(InteractionRequestProjection request, ResourceSet wallet, out string[] sequence)
        {
            sequence = Array.Empty<string>();
            if (request?.InteractionTypeId != AllocationEffects.InteractionType || request.AnswerSchema != AllocationEffects.AnswerSchema ||
                wallet == null || request.CandidateIds == null || request.Presentation?.QuantityRows?.Count != request.CandidateIds.Count) return false;
            var total = PaymentField(request, "totalExecutions");
            var limits = PaymentField(request, "rowMaximums");
            if (total?.Kind != NormalizedValueKind.Integer || total.IntegerValue < 1 || total.IntegerValue > 99 || limits?.Kind != NormalizedValueKind.Object) return false;
            var stock = new Dictionary<string, int> { { "originium", wallet.Originium }, { "originium_shard", wallet.OriginiumShard },
                { "iron", wallet.Iron }, { "pure_originium", wallet.PureOriginium } };
            long gold = wallet.GoldVoucher;
            var plan = new List<string>();
            foreach (string id in request.CandidateIds)
            {
                bool buy = id.StartsWith("buy_", StringComparison.Ordinal), sell = id.StartsWith("sell_", StringComparison.Ordinal);
                if (!buy && !sell) return false;
                string resource = id.Substring(buy ? 4 : 5);
                if (!stock.ContainsKey(resource)) return false;
                var rows = request.Presentation.QuantityRows.Where(row => row.RowId == id).ToArray();
                var maximum = limits.Properties.SingleOrDefault(value => value.Name == id)?.Value;
                if (rows.Length != 1 || rows[0].Label?.Key != "core:interaction.trade." + (buy ? "buy." : "sell.") + resource ||
                    maximum?.Kind != NormalizedValueKind.Integer || maximum.IntegerValue < 0 || maximum.IntegerValue > 99) return false;
                var prices = rows[0].Label.Arguments.Where(arg => arg.Name == (buy ? "cost" : "price") && arg.Kind == "integer").ToArray();
                if (prices.Length != 1 || prices[0].Value?.Kind != NormalizedValueKind.Integer || prices[0].Value.IntegerValue < 0 || prices[0].Value.IntegerValue > int.MaxValue) return false;
                long unit = prices[0].Value.IntegerValue;
                long available = buy ? unit == 0 ? total.IntegerValue : Math.Max(0, gold) / unit : Math.Max(0, stock[resource]);
                int count = (int)Math.Min(Math.Min(maximum.IntegerValue, total.IntegerValue - plan.Count), available);
                for (int n = 0; n < count; n++) plan.Add(id);
                if (buy) gold -= unit * count;
                else stock[resource] -= count;
                // 不预支尚未执行的出售收入，也不先花尚未取得的资源。
            }
            if (plan.Count == total.IntegerValue) sequence = plan.ToArray();
            return true;
        }

        private IEnumerator RequiredTradeAllocation(InteractionRequestProjection request)
        {
            var wallet = CurrentPaymentView()?.Players.FirstOrDefault(player => player.PlayerId == LocalPlayer)?.Resources;
            Require(TryPlanTradeAllocation(request, wallet, out var sequence), "次数式交易缺少完整正式价格或本人库存，保留现场，不盲填次数");
            bool end = sequence.Length == 0;
            Require(!end || PaymentField(request, "allowEndAction")?.BooleanValue == true, "当前已知可付计划不足且请求不允许结束，保留现场");
            Write("次数式交易计划-" + request.InteractionId + ".json", new { State.GameId, State.Round, Request = request,
                Wallet = wallet, Sequence = sequence, EndAction = end, OnlyVisibleOwnResources = true, ProjectedIncomeSpent = false });
            bool Current() => Request?.InteractionId == request.InteractionId && Request.StateRevision == request.StateRevision;
            EffectDialogResourceRowView Row(int index)
            {
                var shell = FindObjectsOfType<EffectDialogShellView>().SingleOrDefault(value => value.TryGetResourceAllocation(request.InteractionId, out _, out _));
                IReadOnlyList<EffectDialogResourceRowView> rows = null;
                Require(shell != null && shell.TryGetResourceAllocation(request.InteractionId, out _, out rows) && rows.Count == request.CandidateIds.Count,
                    "次数式原行没有绑定完整当前候选");
                return rows[index];
            }
            for (int index = 0; index < request.CandidateIds.Count; index++)
            {
                var row = Row(index);
                Require(int.TryParse(row.ValueInput.text, out int value), "次数草稿不是合法整数");
                while (value > 0)
                {
                    Require(Current() && Row(index).DecreaseButton.IsInteractable(), "清理原次数草稿时请求已变更");
                    yield return Press(Row(index).DecreaseButton, "原次数控件清除旧草稿");
                    Require(int.TryParse(Row(index).ValueInput.text, out int next) && next < value, "次数减号没有减少原草稿"); value = next;
                }
            }
            foreach (string id in sequence)
            {
                int index = request.CandidateIds.IndexOf(id);
                Require(Current() && Row(index).IncreaseButton.IsInteractable(), "可付次数计划与原控件限制不一致");
                yield return Press(Row(index).IncreaseButton, "按本人实际可付库存分配一次交易");
            }
            var button = FindObjectsOfType<UnityEngine.UI.Button>().SingleOrDefault(value => value.name == (end ? "End Effect Allocation" : "Confirm Effect Allocation") && value.IsActive());
            Require(Current() && button != null && button.IsInteractable(), "次数式缺少当前合法确认或结束控件");
            int before = commands.Count;
            yield return Press(button, end ? "无完整可付计划，正常结束当前次数效果" : "确认当前可付交易次数计划");
            Require(commands.Count == before + 1, "次数式确认应只提交一次正式答案");
            var command = Read(commands[before], "Command") as GameCommand;
            Require(TradeAllocationAnswerMatchesPlan(command, sequence, end), "次数式实际提交未匹配可付计划及结束边界");
        }

        private static bool TradeAllocationAnswerMatchesPlan(GameCommand command, string[] sequence, bool end)
        {
            string json = null;
            return command?.Kind == GameCommandKind.AnswerInteraction && command.Parameters.TryGetValue(AllocationSequenceAnswerJson.Parameter, out json) &&
                AllocationSequenceAnswerJson.TryDecode(json, out var answer) && ItemTransferEffectExecutor.Text(answer, "submitKind") == (end ? "end_action" : "confirm") &&
                ItemTransferEffectExecutor.Value(answer, "sequence").Items.Select(value => value.StringValue).SequenceEqual(sequence);
        }
    }
}
#endif
