#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Effects;
using YC.Presentation.Workflows;
using YC.Domain.Interactions;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // Router的基础投影可能已裁去typed玩家标签；答案反查必须使用实际页面绑定的同一请求投影。
        private static string[] PlayerSelectionFormalAnswers(string gameId, string renderedGameId, int viewer,
            InteractionRequestProjection routed, InteractionRequestProjection rendered, PlayerSelectionConfig config,
            IReadOnlyList<int> selected)
        {
            if (config?.Players == null || routed == null || rendered == null || selected == null ||
                !InteractionCommandIntent.SameContract(gameId, routed, renderedGameId, rendered) ||
                routed.AnsweringPlayerId != viewer || routed.StateRevision != rendered.StateRevision ||
                config.RequestId != routed.InteractionId || config.Revision != routed.StateRevision ||
                config.Min != routed.MinSelections || config.Max != routed.MaxSelections ||
                selected.Count < config.Min || selected.Count > config.Max || selected.Distinct().Count() != selected.Count ||
                selected.Any(id => !config.Players.Any(player => player.Id == id && player.Eligible))) return null;
            try
            {
                var ids = PlayerSelectionRequestAdapter.CandidateIds(rendered, selected).ToArray();
                return ids.All(routed.CandidateIds.Contains) ? ids : null;
            }
            catch (InvalidOperationException) { return null; }
        }

        // API-06/07/25：只扩充正常控件夹具的决策与采证，不建立对局或修改规则状态。
        private bool LuaApiAcceptanceMode => Argument("--yc-lua-api-acceptance=") == "true";
        private string[] RequiredSaleSources => LuaApiAcceptanceMode
            ? new[] { "cannot-sale", "trade-sale", "tiago-sale" } : new[] { "cannot-sale", "trade-sale" };
        private string luaApiLastMarkerFacts;
        private int luaApiMarkerObservation;

        private static bool LuaApiSaleSemantic(InteractionRequestProjection request) => request != null &&
            ItemTransferEffectExecutor.Text(request.PromptParameters, "interactionSemantic") == ContentPrimitiveEffectExecutor.ResourceSaleSemantic;

        private static bool LuaApiTiagoSource(string definition, string ability) =>
            definition == "entrepreneur.ability.D09" ||
            definition?.StartsWith("entrepreneur.round.D.", StringComparison.Ordinal) == true ||
            new[] { "entrepreneur.D09.investment", "entrepreneur.D07.quick", "entrepreneur.D08.quick" }.Contains(ability);

        private static string LuaApiEntrepreneurCandidate(IEnumerable<string> formal, IEnumerable<string> rendered)
        {
            var available = formal.Where(new HashSet<string>(rendered, StringComparer.Ordinal).Contains).ToArray();
            return available.Contains("entrepreneur.ability.D09") ? "entrepreneur.ability.D09" : available.FirstOrDefault();
        }

        private static bool LuaApiPreferSalePoint(InteractionRequestProjection request, string candidate) =>
            request?.InteractionTypeId == "effect.choice.execute" && request.PromptKey == "core:entrepreneur.D.points.prompt" &&
            request.VisibleToViewer && request.Status == "open" && request.CandidateIds.Contains(candidate) && candidate == "4";

        private static bool LuaApiPreferInvestmentExecution(InteractionRequestProjection request, string sourceKind,
            bool actualTiagoSource, bool canExecute, bool saleCovered) =>
            !saleCovered && canExecute && actualTiagoSource && sourceKind == "condition" &&
            request?.InteractionTypeId == "action.decline_effect" && request.VisibleToViewer && request.Status == "open" &&
            request.CandidateIds.Contains("continue") &&
            request.Presentation?.Prompt?.Key == "core:entrepreneur.D.investment.condition";

        private void RecordLuaApiSaleRequest(string source, InteractionRequestProjection request)
        {
            if (!LuaApiAcceptanceMode) return;
            var visible = CurrentPaymentView();
            Require(visible != null && request.AnsweringPlayerId == visible.ViewerPlayerId && request.VisibleToViewer &&
                request.StateRevision == visible.StateRevision && LuaApiSaleSemantic(request),
                "API-07出售采证须来自本人当前版本正式出售语义请求");
            var chain = new List<object>();
            var seen = new HashSet<string>();
            string owner = request.OwnerEffectId;
            while (!string.IsNullOrEmpty(owner) && seen.Add(owner))
            {
                var node = State.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == owner);
                if (node == null) break;
                chain.Add(new { node.EffectId, node.ParentEffectId, node.PlayerId, node.EffectTypeId, node.SourceId,
                    node.ContinuationContentId, node.ContinuationAbilityId, node.ContinuationContentInstanceId,
                    node.ContinuationData, node.NormalizedArguments });
                owner = node.ParentEffectId;
            }
            Append("API-07三个出售入口原件.jsonl", new { visible.GameId, visible.ContentPackHash, visible.StateRevision,
                visible.ViewerPlayerId, Source = source, Request = request, SourceChain = chain,
                CurrentReceipt = ResourceSaleReceiptProjection.Latest(visible, visible.ViewerPlayerId),
                VisibleText = FindObjectsOfType<Text>().Where(value => value.isActiveAndEnabled).Select(value => value.text).ToArray(),
                CurrentOnly = true, StateReplacementCount = 0 });
        }

        private void RecordLuaApiMarkerFacts()
        {
            if (!LuaApiAcceptanceMode) return;
            var visible = CurrentPaymentView();
            Require(visible != null && visible.ViewerPlayerId == LocalPlayer, "API-25采证缺少当前本人正式投影");
            var facts = new { visible.CompanyControl, visible.UsageTracks,
                Declarations = visible.Players.Select(player => new { player.PlayerId, player.DeclaredCityStyles }).ToArray(),
                Markers = visible.ItemZones.Items.Where(item => item.TypeId == "core.player_marker").ToArray(),
                Zones = visible.ItemZones.Zones,
                visible.AbilityActions };
            string serialized = JsonConvert.SerializeObject(facts);
            if (serialized == luaApiLastMarkerFacts) return;
            luaApiLastMarkerFacts = serialized;
            Append("API-25正常标记与权限变化.jsonl", new { Observation = ++luaApiMarkerObservation,
                visible.GameId, visible.ContentPackHash, visible.StateRevision, visible.ViewerPlayerId, State.Round, State.Phase,
                Facts = facts, Commands = commands.Count, LastCommand = commands.LastOrDefault(), Request,
                VisibleText = FindObjectsOfType<Text>().Where(value => value.isActiveAndEnabled).Select(value => value.text).ToArray(),
                ReadOnlyObservation = true, StateReplacementCount = 0 });
        }
    }
}
#endif
