using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>
    /// 真实T定义、Lua、选人/选面请求、收尾命令及Host归档。所有输入仅为隔离规则定位，正常覆盖为0。
    /// 不写共享包、不改全局能力；私有副本只构造规则隔离输入，正常模式状态以当前正式入口为准。
    /// 正式manifest必须有唯一资格入口；运行证据由统一运行者另行记录。
    /// </summary>
    internal sealed class FifthNumberContentTestFixture
    {
        internal const string AbilityDefinition = "entrepreneur.ability.T09";
        internal readonly IsolatedTestArtifacts Artifacts;
        internal readonly ExternalContentPack Pack;
        internal readonly EffectRegistry Registry;
        internal readonly RoundExecutionService Rounds;
        internal readonly GameMapDefinition Map;
        internal GameState State;
        internal string CleanupRoot = "";
        private int sequence;
        private bool faultRecorded;
        internal EffectTreeExecutor Engine => Rounds.CreateCommandExecutor(State);
        internal string Card => State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId;
        internal ZoneInstanceState Storage => State.ItemZones.Zones.Single(z => z.ParentId == Card && z.LocalZoneId == "markers");
        internal int Stored => State.ItemZones.Items.Count(i => i.ZoneId == Storage.ZoneId);
        internal SettlementWindowRuntimeState CleanupWindow => State.EffectRuntime.EffectNodes.Single(n => n.EffectId == CleanupRoot).SettlementWindow;
        internal RoundChoiceState Group(int round) => State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == round);
        internal InteractionRequest Open(string type = "", int player = 1) => State.EffectRuntime.InteractionRequests.Last(q => q.Status == "open" && q.AnsweringPlayerId == player && (type.Length == 0 || q.InteractionTypeId == type));

        internal FifthNumberContentTestFixture(IsolatedTestArtifacts artifacts, bool concurrent = false, bool dorothy = false, bool onceCleanup = false)
        {
            Artifacts = artifacts;
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            // 先完整封存一次正式输入；清单、定义补丁与Host副本均只从同一私有基线派生。
            string baselineRoot = artifacts.CopyContent(installed, "第五号正式core封存基线");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(baselineRoot, "pack.json")));
            var handlers = (JArray)manifest["settlementHandlers"];
            Assert.That(handlers.Count(h => (string)h["handlerId"] == "entrepreneur.T09.cleanup.availability"), Is.EqualTo(1),
                "须由007先登记实际资格入口，不在夹具中补造正式装配成功。");
            var cleanupRegistration = handlers.Single(h => (string)h["handlerId"] == "entrepreneur.T09.cleanup.availability");
            var cleanupDefinition = JObject.Parse(File.ReadAllText(Path.Combine(baselineRoot, "entrepreneur/entrepreneur.ability.T09.json")));
            var cleanupAbility = cleanupDefinition["abilities"].Single(a => (string)a["abilityId"] == "entrepreneur.T09.cleanup");
            Assert.That((string)cleanupRegistration["version"], Is.EqualTo((string)cleanupAbility["version"]),
                "正式availability登记须随当前token正文版本统一更新，不能在私有副本补登记。");
            Assert.That((string)cleanupRegistration["script"], Is.EqualTo((string)cleanupAbility["script"]));
            Assert.That((string)cleanupRegistration["entryPoint"], Is.EqualTo("availability"));
            var baselinePack = ExternalContentPack.Load(baselineRoot);
            artifacts.RecordContent("第五号正式core基线内容指纹", baselineRoot, baselinePack.ContentHash);
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var replacements = new Dictionary<string, string>();
            // 只改变私有输入的装配模式筛选，不改脚本、来源权限、费用、数量或规则。
            // 以下保留规则隔离装配，不据历史G0备注判断当前模式状态。
            foreach (string relative in ((JArray)manifest["definitions"]).Values<string>())
            {
                if (!relative.StartsWith("entrepreneur/", StringComparison.Ordinal)) continue;
                var definition = JObject.Parse(File.ReadAllText(Path.Combine(baselineRoot, relative)));
                if ((bool?)definition["enabled"] == false) continue;
                definition["applicableModes"] = new JArray();
                if (onceCleanup && relative == "entrepreneur/entrepreneur.ability.T09.json")
                    definition["abilitySources"].Single(x => (string)x["abilityId"] == "entrepreneur.T09.cleanup")["repeatPolicy"] = "once";
                replacements.Add(relative, definition.ToString());
            }
            replacements["pack.json"] = manifest.ToString();
            if (onceCleanup) artifacts.WriteLabeledText("单次收尾协议隔离", "只把私有T09 cleanup repeatPolicy改为once，以同一正式Lua验证旧单次段与系统后置步骤的调度；不代表官方T09为单次，不计正文规则或正常覆盖。");
            string contentRoot = artifacts.CopyContent(baselineRoot, "第五号真实正文私有隔离输入", replacements);
            Pack = ExternalContentPack.Load(contentRoot);
            artifacts.RecordContent("第五号真实声明及Lua装载", contentRoot, Pack.ContentHash);
            artifacts.WriteLabeledText("隔离输入边界", "私有副本改变entrepreneur定义的applicableModes，使用standard配置装配后切换隔离状态的模式并补既有企业家配方；多萝西组合用例将原配方的同名角色改为初始手牌。其余卡面、来源许可与正文不改。真实选人、选面和完成均走Host。所有运行不计正常菜单或页面验收。");
            Map = StaticMapDefinitions.CreateThreePlayerMap();
            var mapQuery = new MapQueryService(Map);
            var configuration = new GameRuleConfiguration { MapId = Map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" },
                PlayerStageSchedulingMode = concurrent ? PlayerStageSchedulingMode.Concurrent : PlayerStageSchedulingMode.Sequential };
            // 复用真实组合器合并standard/enterprise/infected配方、替换项与公共注册。
            // 配置以standard构造隔离前提，再进入企业家流程；不能据此宣称正常模式已验收。
            var catalog = ActiveContentCatalogBuilder.Build(Pack, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            artifacts.RecordContent("第五号私有活动组合指纹", contentRoot, catalog.ContentPackHash);
            Registry = new EffectRegistry { ContentPackHash = catalog.ContentPackHash };
            ResourceEffectExecutor.Register(Registry);
            GenericLuaEffectExecutor.Register(Registry);
            ItemTransferEffectExecutor.Register(Registry);
            InfluenceEffectExecutor.Register(Registry, new InfluenceService(mapQuery));
            ContentPrimitiveEffectExecutor.Register(Registry, mapQuery, Registry.InfluenceService);
            CharacterCoverEffectExecutor.Register(Registry);
            catalog.Register(Registry);
            Rounds = new RoundExecutionService(Registry);

            var recipe = catalog.SetupRecipe;
            recipe.Entries.AddRange(GameStateCloneService.DeepClone(Pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur").Entries));
            if (dorothy)
            {
                var entry = recipe.Entries.Single(e => e.DefinitionId == "operator.dorothy");
                entry.InitialZoneRole = "hand"; entry.PerPlayer = true;
            }
            var definitions = catalog.ContentDefinitions;
            State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, new[] {
                new PlayerSeat { PlayerId = 1, PlayerName = "第五号隔离玩家" }, new PlayerSeat { PlayerId = 2, PlayerName = "隔离对手" }
            }, configuration, 47, recipe, definitions, new[] { 1, 2 }, "qy012.content.isolated." + artifacts.CaseId, null);
            State.RuleConfiguration.RuleModeId = "entrepreneur";
            State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            Registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            Registry.SetupRecipe = GameStateCloneService.DeepClone(recipe);
            Run(ExpansionSetupFlow.PreparationSpec(1, false));
            foreach (var request in State.EffectRuntime.InteractionRequests.Where(q => q.Status == "open" && q.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToList())
                Answer(request, NormalizedValue.CreateString("continue"));
            while (State.EntrepreneurSetup.Stage != "complete")
            {
                var request = State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && q.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                string definition = request.AnsweringPlayerId == 1 ? AbilityDefinition : "entrepreneur.ability.A01";
                Assert.That(request.CandidateIds, Does.Contain(definition));
                Answer(request, NormalizedValue.CreateString(definition));
            }
            // 完成真实初始企业选择及其到达奖励；不伪造企业选择/升级收据。
            ResolveOpenRequests();
            Assert.That(Stored, Is.EqualTo(2));
            State.Round = 1; State.ActionRound = 1; State.Phase = GamePhase.ActionRound1;
            State.CurrentPlayerId = State.StartPlayerId = 1;
            Snapshot("真实T09一次设置完成");
        }

        internal void Pump()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted && !faultRecorded) { faultRecorded = true; Snapshot("首次真实故障"); }
            Assert.That(result.Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
        }
        internal string Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return id; }
        internal void Answer(InteractionRequest request, NormalizedValue answer)
        {
            bool accepted = Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, answer, out string reason);
            if (!accepted) Snapshot("真实交互拒绝现场");
            Assert.That(accepted, Is.True, reason);
            Pump();
        }
        internal void Answer(string answer, string type = "", int player = 1) => Answer(Open(type, player), NormalizedValue.CreateString(answer));
        internal void AnswerCandidate(InteractionRequest request, string candidate)
        {
            Assert.That(request.CandidateIds, Does.Contain(candidate));
            Answer(request, request.AnswerSchema == "candidate_id_array" ? Strings(new[] { candidate }) : NormalizedValue.CreateString(candidate));
        }
        internal void ResolveOpenRequests(int onlyPlayer = -1)
        {
            for (int i = 0; i < 100; i++)
            {
                var request = State.EffectRuntime.InteractionRequests.FirstOrDefault(q => q.Status == "open" && (onlyPlayer < 0 || q.AnsweringPlayerId == onlyPlayer));
                if (request == null) return;
                if (request.InteractionTypeId == EntrepreneurFlow.FaceInteraction)
                {
                    Assert.That(request.AnsweringPlayerId, Is.EqualTo(2), "不能替本人选择待验T面。");
                    AnswerCandidate(request, request.CandidateIds.First());
                }
                else Answer(request, AutomaticAnswer(request));
            }
            Assert.Fail("正式请求未在有界步骤内结清。");
        }
        internal static NormalizedValue AutomaticAnswer(InteractionRequest request)
        {
            if (request.AllowDecline) return NormalizedValue.CreateBoolean(false);
            if (request.AnswerSchema == "resource_allocation")
            {
                string row = Value(request.PromptParameters, "rowIds").Items.First().StringValue;
                return NormalizedValue.CreateString(row + "=" + Number(request.PromptParameters, "totalAmount"));
            }
            if (request.AnswerSchema == ContentPrimitiveEffectExecutor.QuantityAnswerSchema)
                return NormalizedValue.CreateString(Number(request.PromptParameters, "minAmount").ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (request.AnswerSchema == "candidate_id_array" || request.AnswerSchema == "candidate_ids" && request.MaxSelections > 1)
                return Strings(request.CandidateIds.Take(request.MinSelections));
            if (request.CandidateIds.Count > 0) return NormalizedValue.CreateString(request.CandidateIds.First());
            throw new InvalidOperationException("隔离辅助没有对应正式答案：" + request.InteractionTypeId + "/" + request.AnswerSchema);
        }
        internal void ChooseFace(string face)
        {
            int round = face == "T07" || face == "T08" ? 1 : face == "T05" || face == "T06" ? 3 : face == "T03" || face == "T04" ? 5 : 7;
            State.Round = round; State.ActionRound = 0;
            Run(ExpansionSetupFlow.RoundPreparationSpec(1));
            while (State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && q.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction))
            {
                var request = State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && q.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction);
                AnswerCandidate(request, request.CandidateIds.First());
            }
            var choice = Open(EntrepreneurFlow.FaceInteraction);
            AnswerCandidate(choice, "entrepreneur.candidate." + face);
        }
        internal void FinishOtherPlayer() => ResolveOpenRequests(2);
        internal void Restore(string label)
        {
            Snapshot(label + "原始状态");
            var result = Artifacts.Recover(State, label, Registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); State = result.State;
        }
        internal void Snapshot(string label) => Artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(State));
        internal string FreeSlot() => Registry.InfluenceService.GetLegalPlacementSlotIds(State, 1).First();
        internal string Place()
        {
            string id = Run(InfluenceEffectSpecFactory.PlaceInfluence(1, FreeSlot()));
            return Value(State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).NormalizedResult, "influenceId").ReferenceId;
        }
        internal string Depart(string item, int actor = 1, bool replace = false, bool actionWindow = false)
        {
            var effect = replace ? InfluenceEffectSpecFactory.ReplaceInfluence(actor, item) : InfluenceEffectSpecFactory.RemoveInfluence(actor, item);
            if (!actionWindow) return Run(effect);
            State.Phase = GamePhase.ActionRound1; State.ActionRound = 1;
            Assert.That(Rounds.PrepareActionWindowForSmoke(State, actor).IsValid, Is.True);
            var command = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = actor, CommandId = "qy012.cause." + ++sequence };
            Assert.That(OperationWindowService.TryResolveCommandContext(State, command, out var cause, out var validation), Is.True, validation?.Reason);
            State.EffectRuntime.CurrentOperationContext = cause;
            try
            {
                Assert.That(Engine.TryCreatePlayerActionEffect(actor, effect, "1", "qy012.real-departure", out string id), Is.True);
                Pump(); return id;
            }
            finally { State.EffectRuntime.CurrentOperationContext = null; }
        }
        internal InteractionRequest StoreRequest() => State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && q.AnsweringPlayerId == 1 &&
            State.EffectRuntime.DeferredResponses.Any(r => r.AbilityId == "entrepreneur.T09.departure" && r.Status == DeferredResponseStatus.Pending &&
                InfluenceDepartureProtocol.IsWithin(State, q.OwnerEffectId, r.ResponseRootEffectId)));
        internal void CollectOne()
        {
            string item = Place(); Depart(item); Answer(StoreRequest(), NormalizedValue.CreateString("store"));
        }
        internal void ActivateDorothy()
        {
            var card = State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1);
            // 角色由私有夹具配方放入初始手牌；激活与装载仍逐步回答真实盖放/计谋请求。
            State.Phase = GamePhase.ActionRound1; State.ActionRound = 1;
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            AnswerCandidate(Open(), card.ItemId);
            Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic"));
            var amount = Open();
            Assert.That(amount.AnswerSchema, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityAnswerSchema));
            Answer(amount, NormalizedValue.CreateString("1"));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(State, card.ItemId, "operator.dorothy.tactic"), Is.True);
        }
        internal InteractionRequest PlacementRequest() => State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && q.AnsweringPlayerId == 1 &&
            State.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId).EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence);
        internal void ClearOwnMap()
        {
            foreach (string id in State.Map.Influences.Where(i => i.PlayerId == 1).Select(i => InfluenceIdentity.GetStableId(State, i)).ToArray())
            {
                Depart(id); Answer(StoreRequest(), NormalizedValue.CreateString("decline"));
            }
        }
        internal void CompleteDestinations(string destination, bool restoreEach = false)
        {
            for (int i = 0; i < 8; i++)
            {
                var pending = State.EffectRuntime.DeferredResponses.FirstOrDefault(r => r.Kind == "destination" && r.Status == DeferredResponseStatus.Pending);
                if (pending == null) return;
                var request = State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" && InfluenceDepartureProtocol.IsWithin(State, q.OwnerEffectId, pending.ResponseRootEffectId));
                bool chooseStore = destination == "t" ? pending.AbilityId == "entrepreneur.T09.departure" :
                    destination == "dorothy" && pending.AbilityId == "operator.dorothy.tactic";
                if (restoreEach)
                {
                    string id = request.GetStableInteractionId(); Restore("独立收容待答");
                    request = State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == id);
                }
                Answer(request, NormalizedValue.CreateString(chooseStore ? "store" : "decline"));
                if (restoreEach) Restore("独立收容已答");
            }
            Assert.Fail("同一离场来源机会没有正常结清。");
        }
        internal void OpenCleanup()
        {
            FinishOtherPlayer(); State.Phase = GamePhase.Cleanup; State.ActionRound = 1;
            var chain = new RoundMainChainBuilder().Build(State.GameId, "", State.Round, State.LaunchPlayerOrder, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            State.EffectRuntime.ActiveMainNodeId = main.NodeId; State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            State.EffectRuntime.CurrentRoundNumber = State.Round; State.EffectRuntime.CurrentRoundStartPlayerId = 1;
            State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            CleanupRoot = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.ExecutionEffectId = CleanupRoot; main.Status = EffectNodeStatus.Running; Pump();
        }
        internal void InvokeCleanup(string commandId)
        {
            var option = Registry.GetSettlementOptions(State, 1).Single(o => o.SourceItemId == Card && o.CleanupRequest != null);
            var command = CleanupSourceCommand.Create(1, commandId, option.CleanupRequest, State.EffectRuntime.StateRevision);
            var result = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); Pump();
        }
        internal void StartFinal()
        {
            FinishOtherPlayer(); State.MaxRounds = 8;
            Artifacts.WriteLabeledText("终局隔离入口", "前序行动和采集为隔离前提；末轮实际执行两人收尾并合法关闭，保留RoundEnded未推进断点以逐阶段恢复。正常覆盖0。");
            IsolatedFinalRoundFlow.CreatePendingFinal(State, Rounds, Pump);
        }
    }
}
