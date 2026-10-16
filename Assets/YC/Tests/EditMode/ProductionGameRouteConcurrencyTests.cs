using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
#if PRODUCTION_ROUTE_CONCURRENCY_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    // 使用本人授权投影验证实际Policy状态机；不替代Host生成请求、网络或Unity实局。
    public sealed class ProductionGameRouteConcurrencyTests
    {
        [Test]
        public void DeterministicReceiptsRequireExactIdentityAndRetirementDoesNotCompleteSteps()
        {
            var policy = Policy(); var view = View("setup.enterprise.choose");
            var command = Decide(policy, view); var dto = GameCommandDto.FromCommand(command);
            Assert.That((bool)Call(policy, "ApplyLocalResult", "异局", dto, true), Is.False);
            var wrong = Clone(dto); wrong.OptionIds.Add("未知候选");
            Assert.That((bool)Call(policy, "ApplyLocalResult", view.GameId, wrong, true), Is.False);
            Assert.That((bool)Call(policy, "ApplyLocalResult", view.GameId, dto, false), Is.False);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
            Assert.That((bool)Call(policy, "RetireUnappliedSubmission", view.GameId, dto, "confirmed_recovery_retired"), Is.True);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.False);
            Assert.That(Read<IEnumerable<string>>(policy, "CompletedSteps"), Is.Empty);
            var current = Clone(view); Request(current).CandidateIds = new List<string> { "新候选" };
            var next = Decide(policy, current);
            Assert.That(next.CommandId, Is.Not.EqualTo(command.CommandId));
            Assert.That(next.OptionIds, Is.EqualTo(new[] { "新候选" }));
            Assert.That((bool)Call(policy, "ApplyLocalResult", view.GameId, dto, true), Is.False);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
            Assert.That((bool)Call(policy, "ApplyLocalResult", view.GameId, GameCommandDto.FromCommand(next), true), Is.True);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.False);
            Assert.That((bool)Call(policy, "ApplyLocalResult", view.GameId, GameCommandDto.FromCommand(next), true), Is.False);
        }
        private const string LegacyReason = "交互已过期，请刷新当前交互。";
        private static readonly string[] Kinds = { "route_resource.selection", "setup.enterprise.choose", "character.cover.card" };
        private static Type Target(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("YC.Presentation.Validation." + name)).First(type => type != null);
        private static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        private static object Call(object target, string method, params object[] arguments)
        {
            try { return target.GetType().GetMethod(method).Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static T Read<T>(object policy, string name) => (T)policy.GetType().GetProperty(name).GetValue(policy);
        private static object Plan() => JsonConvert.DeserializeObject(@"{
            'RouteName':'并发交互窄检查','PlayerCount':3,'MapId':'map-three-players',
            'ContentPackHash':'冻结夹具','EnabledExpansionIds':['core','enterprise'],
            'CandidatePriorityDefinitionIds':['定义甲','定义乙']}", Target("ProductionGameRoutePlan"));
        private static object Policy(Func<double> clock = null, object plan = null) =>
            Activator.CreateInstance(Target("ProductionGameRoutePolicy"), plan ?? Plan(), clock);
        private static GameCommand Decide(object policy, GameStateView view) =>
            (GameCommand)Call(policy, "Decide", view, view.ViewerPlayerId, "");
        private static bool Begin(object policy, RejectedGameCommandDto rejected, GameStateView view) =>
            (bool)Call(policy, "TryBeginStaleInteractionRefresh", rejected, view, "");
        private static RejectedGameCommandDto Rejection(GameCommand command, bool legacy = false) => new RejectedGameCommandDto
        {
            Command = GameCommandDto.FromCommand(command),
            ErrorCode = legacy ? CommandErrorCode.InvalidTarget : CommandErrorCode.StateRevisionMismatch,
            Reason = legacy ? LegacyReason : "严格版本拒绝"
        };
        private static void Fails(Action action)
        {
            Exception failure = null;
            try { action(); } catch (Exception error) { failure = error; }
            Assert.That(failure?.GetType() == typeof(InvalidOperationException), Is.True);
        }
        private static NormalizedValue Arguments(params object[] properties) => JsonConvert.DeserializeObject<NormalizedValue>(
            JsonConvert.SerializeObject(new { Kind = "Object", Properties = properties }));
        private static object Integer(string name, long value) => new { Name = name, Value = new { Kind = "Integer", IntegerValue = value } };
        private static object String(string name, string value) => new { Name = name, Value = new { Kind = "String", StringValue = value } };
        private static NormalizedValue Value(NormalizedValue args, string name) => args.Properties.Single(p => p.Name == name).Value;
        private static InteractionView Request(GameStateView view) => view.Interactions.Single();
        private static EffectNodeView Owner(GameStateView view) => view.Effects.Single();
        private static GameStateView View(string kind)
        {
            var view = new GameStateView
            {
                GameId = "同局", ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 2,
                CurrentPlayerId = -1, Round = 1, StateRevision = 100,
                Phase = kind == Kinds[0] ? GamePhase.ResourceCollection : kind == Kinds[1] ? GamePhase.Entrance : GamePhase.CharacterCover,
                MapId = "map-three-players", ContentPackHash = "冻结夹具"
            };
            view.Interactions.Add(new InteractionView
            {
                InteractionId = "稳定请求", SourceNodeId = "稳定来源", OwnerEffectId = "本人owner", Kind = kind,
                AnsweringPlayerId = 2, Visibility = "owner", PromptKey = kind,
                PromptParameters = Arguments(Integer("requestRevision", 100), Integer("purpose", 0), Integer("budget", 8),
                    String("scopeKey", "稳定scope"), String("factsDigest", "稳定事实"), String("price", "真实费用"),
                    new { Name = "candidateDefinitionIds", Value = new { Kind = "Array", Items = new[] {
                        new { Kind = "String", StringValue = "定义甲" }, new { Kind = "String", StringValue = "定义乙" } } } }),
                CandidateSetId = "稳定候选scope", CandidateSetVersion = 1, CandidateIds = new List<string> { "候选甲", "候选乙" },
                MinSelections = 1, MaxSelections = 1, AllowDecline = false, AnswerSchema = "candidate_id", Status = "open", StateRevision = 100
            });
            view.Effects.Add(new EffectNodeView
            {
                EffectId = "本人owner", ParentEffectId = "稳定父owner", PlayerId = 2,
                EffectTypeId = kind == Kinds[0] ? "effect.route_resource.select" : kind == Kinds[1] ? "setup.enterprise.select" : "character.cover.task",
                NormalizedArguments = Arguments(Integer("purpose", 0), Integer("keepCount", 1), Integer("round", 1))
            });
            return view;
        }
        private static GameStateView Renew(GameStateView old, int revision)
        {
            var view = Clone(old); view.StateRevision = Request(view).StateRevision = revision;
            Value(Request(view).PromptParameters, "requestRevision").IntegerValue = revision;
            return view;
        }
        private static JObject Intent(GameCommandDto command)
        {
            var value = JObject.FromObject(command); value.Remove("CommandId");
            ((JArray)value["Parameters"]).Children<JObject>().Single(p => (string)p["Key"] == "expectedRevision")["Value"] = "仅版本变化";
            return value;
        }

        private sealed class ActualHostLinkFixture
        {
            public GameSession Session;
            public RoundExecutionService Round;
            public InfluenceService Influence;
            public string EvidencePath;
            public readonly List<object> Calls = new List<object>();
            public void Save(string name, object value) => File.WriteAllText(Path.Combine(EvidencePath, name + ".json"),
                JsonConvert.SerializeObject(value, Formatting.Indented));
            public CommandResult Submit(GameCommand command)
            {
                if (string.IsNullOrEmpty(command.CommandId)) command.CommandId = Guid.NewGuid().ToString("N");
                int before = Session.State.EffectRuntime.StateRevision;
                var result = Session.Submit(command);
                Calls.Add(new { Command = GameCommandDto.FromCommand(command), result.Succeeded,
                    ErrorCode = result.Validation?.ErrorCode, Reason = result.Validation?.Reason,
                    BeforeRevision = before, AfterRevision = Session.State.EffectRuntime.StateRevision });
                Save("实际外层提交", Calls);
                return result;
            }
        }

        private static ActualHostLinkFixture CreateActualHostLink(string stage)
        {
            string root = Environment.GetEnvironmentVariable("NMC_ACTUAL_HOST_POLICY_EVIDENCE");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Directory.GetCurrentDirectory(),
                "Logs/ExpansionValidation/ProductionGameRoutes/Preparation/ActualHostPolicyLink/原始联接证据", Guid.NewGuid().ToString("N"));
            var fixture = new ActualHostLinkFixture { EvidencePath = Path.Combine(root, stage) };
            if (Directory.Exists(fixture.EvidencePath)) throw new InvalidOperationException("真实联接证据目录已存在，不覆盖原件。");
            Directory.CreateDirectory(fixture.EvidencePath);
            string content = Environment.GetEnvironmentVariable("NMC_ACTUAL_HOST_POLICY_CONTENT_ROOT") ??
                Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var pack = ExternalContentPack.Load(content);
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3,
                EnabledExpansionIds = stage == "enterprise" || stage == "entrance-location"
                    ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config);
            config.ContentPackHash = catalog.ContentPackHash;
            var recipe = catalog.SetupRecipe;
            // 与主链专用夹具相同的合法地点指示物配方，抑制随机入场事件；不是事件正文覆盖。
            foreach (string location in StaticMapDefinitions.FourPlayerInitialLocationIds)
                recipe.Add("test.policy_link.indicator." + location, "test.policy_link.indicator." + location,
                    "core.resource_point_token", "location/" + location);
            var definitions = catalog.ContentDefinitions;
            var registry = new EffectRegistry();
            catalog.Register(registry);
            registry.SetupRecipe = GameStateCloneService.DeepClone(recipe);
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1,
                Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id }).ToList(),
                config, 321, recipe, definitions, new[] { 1, 2, 3 });
            foreach (var token in state.ItemZones.Items.Where(item => item.TypeId == "core.resource_point_token"))
                token.LocalState = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateInteger((int)ResourceType.Iron) },
                    new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(1) }
                });
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            fixture.Influence = new InfluenceService(map);
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, fixture.Influence);
            EventCardEffectExecutor.Register(registry, map, fixture.Influence, new EventDeckService(), new ResourceTokenService());
            PlayerEntranceEffectExecutor.Register(registry, map);
            RoundStartedRedZoneRule.Register(registry, map);
            fixture.Round = new RoundExecutionService(registry);
            var advance = new RoundAdvanceService(fixture.Round);
            fixture.Session = new GameSession(state);
            fixture.Session.RegisterHandler(new AnswerInteractionCommandHandler(registry, fixture.Round));
            fixture.Session.RegisterHandler(new DeployInfluenceCommandHandler(fixture.Influence, advance, registry));
            fixture.Session.RegisterHandler(new EndActionCommandHandler(advance, null, registry, fixture.Round));
            var start = fixture.Round.StartEntrance(fixture.Session.State);
            fixture.Save("正式开局入口", new { start.IsValid, start.Reason, Config = config,
                CatalogHash = catalog.ContentPackHash, Recipe = recipe, fixture.Session.State.GameId,
                fixture.Session.State.Phase, fixture.Session.State.EffectRuntime.StateRevision,
                Scope = "正式内容目录/开局工厂/主链；只增加地点指示物夹具，未反射设置生产私有状态或伪造请求/拒绝。" });
            Assert.That(start.IsValid, Is.True, start.Reason);
            return fixture;
        }

        private static GameCommand FormalCandidateAnswer(InteractionRequest request)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean" || request.AnswerSchema == "boolean_accept" || request.AnswerSchema == "bool")
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "true";
            else if (request.AnswerSchema == "bounded_amount")
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = Value(request.PromptParameters, "minAmount").IntegerValue.ToString(CultureInfo.InvariantCulture);
            else if (new[] { "candidate_id", "candidate_ids", "candidate_id_array" }.Contains(request.AnswerSchema))
                command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            else throw new InvalidOperationException("联接前置需要明确正式回答：" + request.InteractionTypeId + "/" + request.AnswerSchema);
            return command;
        }

        private static void ReachActualLinkStage(ActualHostLinkFixture fixture, string stage)
        {
            if (stage == "enterprise") return;
            for (int guard = 0; fixture.Session.State.Phase == GamePhase.Entrance && guard < 64; guard++)
            {
                var request = fixture.Session.State.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" &&
                    !r.InteractionTypeId.StartsWith(InteractionRequest.MainlineCompletionInteractionPrefix, StringComparison.Ordinal));
                Assert.That(request != null, Is.True, "正式入场前置没有可回答请求。");
                var result = fixture.Submit(FormalCandidateAnswer(request));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            Assert.That(fixture.Session.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            if (stage == "cover") return;
            for (int guard = 0; fixture.Session.State.Phase != GamePhase.ResourceCollection && guard < 64; guard++)
            {
                var state = fixture.Session.State;
                var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" &&
                    !r.InteractionTypeId.StartsWith(InteractionRequest.MainlineCompletionInteractionPrefix, StringComparison.Ordinal));
                GameCommand command;
                if (request != null) command = FormalCandidateAnswer(request);
                else
                {
                    Assert.That(state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2, Is.True);
                    var view = fixture.Session.ProjectFor(GameStateViewer.Player(state.CurrentPlayerId));
                    if (view.Players.Single(p => p.PlayerId == view.ViewerPlayerId).ActedMainActionThisTurn)
                        command = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = view.ViewerPlayerId };
                    else
                    {
                        string slot = fixture.Influence.GetLegalPlacementSlotIds(state, view.ViewerPlayerId).FirstOrDefault();
                        Assert.That(!string.IsNullOrEmpty(slot), Is.True, "前置主行动没有真实合法放置目标。");
                        command = new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = view.ViewerPlayerId, TargetId = slot };
                    }
                }
                var result = fixture.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            Assert.That(fixture.Session.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
        }

        [TestCase("enterprise")]
        [TestCase("cover")]
        [TestCase("collection")]
        public void ActualHostStaleRejectionAndFreshProjectionRebuildOneOuterCommand(string stage)
        {
            var fixture = CreateActualHostLink(stage); ReachActualLinkStage(fixture, stage);
            string kind = stage == "enterprise" ? Kinds[1] : stage == "cover" ? Kinds[2] : Kinds[0];
            var originalView = fixture.Session.ProjectFor(GameStateViewer.Player(2));
            var competitorView = fixture.Session.ProjectFor(GameStateViewer.Player(3));
            Assert.That(originalView.CurrentPlayerId, Is.EqualTo(-1));
            Assert.That(originalView.Interactions.Count(r => r.Status == "open" && r.Kind == kind && r.AnsweringPlayerId == 2), Is.EqualTo(1));
            Assert.That(competitorView.Interactions.Count(r => r.Status == "open" && r.Kind == kind && r.AnsweringPlayerId == 3), Is.EqualTo(1));
            var planJson = JObject.FromObject(Plan());
            planJson["MapId"] = originalView.MapId; planJson["ContentPackHash"] = originalView.ContentPackHash;
            planJson["EnabledExpansionIds"] = JArray.FromObject(fixture.Session.State.RuleConfiguration.EnabledExpansionIds);
            var policy = Policy(plan: planJson.ToObject(Target("ProductionGameRoutePlan")));
            var original = Decide(policy, originalView);
            Assert.That(original != null && original.Kind == GameCommandKind.AnswerInteraction, Is.True);
            Assert.That(original.Parameters["interactionId"], Is.EqualTo(originalView.Interactions.Single(r => r.Status == "open" &&
                r.Kind == kind && r.AnsweringPlayerId == 2).InteractionId));
            var savedIntent = GameCommandDto.FromCommand(original);
            fixture.Save("本人原始命令与投影", new { Command = savedIntent, View = originalView });
            var competitorRequest = fixture.Session.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.InteractionTypeId == kind && r.AnsweringPlayerId == 3);
            var competitorResult = fixture.Submit(FormalCandidateAnswer(competitorRequest));
            Assert.That(competitorResult.Succeeded, Is.True, competitorResult.Validation?.Reason);
            var fresh = fixture.Session.ProjectFor(GameStateViewer.Player(2));
            fixture.Save("另一玩家真实提交后本人投影", fresh);
            Assert.That(fresh.StateRevision > originalView.StateRevision, Is.True);
            var freshRequest = fresh.Interactions.Single(r => r.Status == "open" && r.Kind == kind && r.AnsweringPlayerId == 2);
            Assert.That(fresh.GameId == originalView.GameId && freshRequest.InteractionId == original.Parameters["interactionId"] &&
                freshRequest.StateRevision == fresh.StateRevision, Is.True);
            Assert.That(Decide(policy, fresh) == null && Read<bool>(policy, "HasPendingCommand"), Is.True,
                "仅新快照不能解除未知pending或重发。");
            string stateBeforeRejection = JsonConvert.SerializeObject(fixture.Session.State);
            var result = fixture.Submit(original);
            fixture.Save("真实Host原命令拒绝", new { Original = savedIntent, result.Succeeded,
                ErrorCode = result.Validation?.ErrorCode, Reason = result.Validation?.Reason });
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(fixture.Session.State), Is.EqualTo(stateBeforeRejection));
            // DTO只是封装这个实际Host返回，错误码与Reason不由夹具常量赋值。
            var rejected = new RejectedGameCommandDto { Command = GameCommandDto.FromCommand(original),
                ErrorCode = result.Validation.ErrorCode, Reason = result.Validation.Reason };
            var foreign = Clone(rejected); foreign.Command.CommandId += "-无关";
            Assert.That(!Begin(policy, foreign, fresh) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            Assert.That(Begin(policy, rejected, originalView), Is.True);
            Assert.That(Decide(policy, originalView) == null && Read<bool>(policy, "IsWaitingForStaleInteractionRefresh"), Is.True);
            var rebuilt = Decide(policy, fixture.Session.ProjectFor(GameStateViewer.Player(2)));
            Assert.That(rebuilt != null && rebuilt.CommandId != savedIntent.CommandId &&
                rebuilt.Parameters["expectedRevision"] == fresh.StateRevision.ToString(CultureInfo.InvariantCulture), Is.True);
            Assert.That(JToken.DeepEquals(Intent(savedIntent), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
            fixture.Save("本人新授权重建", new { Command = rebuilt, Refresh = Read<object>(policy, "StaleInteractionRefreshEvidence") });
            Call(policy, "Confirm", original.CommandId);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
            var accepted = fixture.Submit(rebuilt);
            Assert.That(accepted.Succeeded, Is.True, accepted.Validation?.Reason);
            Assert.That(fixture.Session.State.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" &&
                request.GetStableInteractionId() == rebuilt.Parameters["interactionId"]), Is.False,
                "正式Host接受后，原请求必须实际消费，不能只登记成功。");
            var matchingOuterCalls = fixture.Calls.Select(JObject.FromObject).Where(call =>
                (string)call["Command"]["CommandId"] == rebuilt.CommandId).ToList();
            Assert.That(matchingOuterCalls.Count, Is.EqualTo(1));
            Assert.That((bool)matchingOuterCalls.Single()["Succeeded"], Is.True);
            var journal = fixture.Session.State.EffectRuntime.Journal.Where(entry => entry.CommandId == rebuilt.CommandId).ToList();
            Assert.That(journal.Count > 0, Is.True, "真实外层命令必须进入正式journal；内部可以有多行。");
            Call(policy, "Confirm", rebuilt.CommandId);
            Assert.That(!Read<bool>(policy, "HasPendingCommand") && !Read<IEnumerable<string>>(policy, "CompletedSteps").Any() &&
                Read<IReadOnlyDictionary<string, string>>(policy, "SkippedSteps").Count == 0, Is.True);
            fixture.Save("最终真实联接结果", new { Stage = stage, fixture.Session.State.GameId,
                OriginalCommand = savedIntent, RealHostRejection = rejected, RebuiltCommand = GameCommandDto.FromCommand(rebuilt),
                MatchingOuterCalls = matchingOuterCalls, InternalJournalRows = journal.Count, Journal = journal,
                FinalPlayerView = fixture.Session.ProjectFor(GameStateViewer.Player(2)),
                Scope = "当前冻结DLL上的正式GameSession/handler/主链/本人投影联接；非Unity、真实网络、R6或整局。采集以Policy实际自动候选为准，不宣称非空路线收费/收益分支覆盖。" });
        }

        [Test]
        public void ActualHostEntranceDiscardStaleRejectionPreservesIntent()
        {
            var fixture = CreateActualHostLink("entrance-location");
            for (int player = 1; player <= 3; player++)
                Assert.That(fixture.Submit(FormalCandidateAnswer(fixture.Session.State.EffectRuntime.InteractionRequests.Single(r =>
                    r.Status == "open" && r.AnsweringPlayerId == player && r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction))).Succeeded, Is.True);
            for (int guard = 0; !fixture.Session.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" &&
                r.AnsweringPlayerId == 1 && r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId); guard++)
            {
                Assert.That(guard < 16, Is.True, "有限完成企业初始化的正式前置请求。");
                var prerequisite = fixture.Session.State.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" &&
                    !r.InteractionTypeId.StartsWith(InteractionRequest.MainlineCompletionInteractionPrefix, StringComparison.Ordinal));
                Assert.That(prerequisite, Is.Not.Null);
                Assert.That(fixture.Submit(FormalCandidateAnswer(prerequisite)).Succeeded, Is.True);
            }
            Assert.That(fixture.Submit(FormalCandidateAnswer(fixture.Session.State.EffectRuntime.InteractionRequests.Single(r =>
                r.Status == "open" && r.AnsweringPlayerId == 1 && r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId))).Succeeded, Is.True);
            var old = fixture.Session.ProjectFor(GameStateViewer.Player(1));
            var request = old.Interactions.Single(r => r.Status == "open" && r.Kind == ExpansionSetupFlow.BasicCharacterInteraction);
            var policy = Policy(); var original = Decide(policy, old); var saved = GameCommandDto.FromCommand(original);
            Assert.That(original.Parameters["interactionId"], Is.EqualTo(request.InteractionId));
            Assert.That(fixture.Submit(FormalCandidateAnswer(fixture.Session.State.EffectRuntime.InteractionRequests.Single(r =>
                r.Status == "open" && r.AnsweringPlayerId == 2 && r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId))).Succeeded, Is.True);
            var fresh = fixture.Session.ProjectFor(GameStateViewer.Player(1));
            Assert.That(fresh.StateRevision > old.StateRevision, Is.True);
            Assert.That(Decide(policy, fresh), Is.Null, "未知pending不能提前重发。");
            var before = JsonConvert.SerializeObject(fixture.Session.State); var rejected = fixture.Submit(original);
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(fixture.Session.State), Is.EqualTo(before));
            fixture.Save("真实裁牌刷新前态与严格拒绝", new { Original = saved, OldView = old, FreshView = fresh, Rejection = rejected.Validation });
            Assert.That(Begin(policy, new RejectedGameCommandDto { Command = saved, ErrorCode = rejected.Validation.ErrorCode, Reason = rejected.Validation.Reason }, fresh), Is.True);
            var rebuilt = Decide(policy, fresh);
            Assert.That(rebuilt.CommandId, Is.Not.EqualTo(original.CommandId));
            Assert.That(JToken.DeepEquals(Intent(saved), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
            Assert.That(fixture.Submit(rebuilt).Succeeded, Is.True);
            var after = fixture.Session.ProjectFor(GameStateViewer.Player(1));
            Assert.That(after.Interactions.Any(r => r.Status == "open" && r.InteractionId == request.InteractionId), Is.False);
            Assert.That(after.Players.Single(p => p.PlayerId == 2).CityLocationId.Length > 0, Is.True);
            Call(policy, "Confirm", rebuilt.CommandId);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.False);
            fixture.Save("真实裁牌同意图刷新", new { Original = saved, OldView = old, FreshView = fresh, Rejection = rejected.Validation,
                Rebuilt = GameCommandDto.FromCommand(rebuilt), After = after });
        }

        [TestCase("event_card.option")]
        [TestCase("event_card.influence_targets")]
        [TestCase("setup.basic_character.discard")]
        [TestCase("setup.influence.optional")]
        public void EntranceSourceRefreshRequiresCompleteUnchangedChain(string kind)
        {
            var old = View(Kinds[1]); Request(old).Kind = kind; Request(old).SourceNodeId = Owner(old).EffectId;
            Owner(old).EffectTypeId = kind.StartsWith("event_card.") ? "effect.event_card.resolve" :
                kind == "setup.basic_character.discard" ? "setup.basic_character.retain" : "setup.initial_influence";
            old.ActiveMainNodeId = "正式入场主节点"; Owner(old).ParentEffectId = "本人入场";
            if (kind.StartsWith("event_card."))
            {
                Owner(old).ParentEffectId = "本人移动城市";
                old.Effects.Add(new EffectNodeView { EffectId = "本人移动城市", ParentEffectId = "本人入场", EffectTypeId = "flow.player.enter", PlayerId = 2, ChildEffectIds = new List<string> { "本人owner" } });
            }
            old.Effects.Add(new EffectNodeView { EffectId = "本人入场", ParentEffectId = "入场Effect根", EffectTypeId = "setup.player.entrance", PlayerId = 2,
                ChildEffectIds = new List<string> { kind.StartsWith("event_card.") ? "本人移动城市" : "本人owner" },
                NormalizedArguments = Arguments(Integer("setupProtocolVersion", 2), String("mainNodeId", old.ActiveMainNodeId)) });
            old.Effects.Add(new EffectNodeView { EffectId = "入场Effect根", EffectTypeId = "round.mainline.node", PlayerId = -1,
                ChildEffectIds = new List<string> { "本人入场" }, NormalizedArguments = Arguments(Integer("setupProtocolVersion", 2),
                    String("mainNodeId", old.ActiveMainNodeId), String("nodeTypeId", "player_entrance_setup")) });
            var renewed = Clone(old); renewed.StateRevision = Request(renewed).StateRevision = 101;
            Value(Request(renewed).PromptParameters, "requestRevision").IntegerValue = 101;
            var policy = Policy(); var command = Decide(policy, old);
            Assert.That(Begin(policy, Rejection(command), renewed), Is.True);
            var rebuilt = Decide(policy, renewed);
            Assert.That(JToken.DeepEquals(Intent(GameCommandDto.FromCommand(command)), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
            var changed = Clone(renewed); changed.Effects.Single(n => n.EffectId == "本人入场").ChildEffectIds.Clear();
            policy = Policy(); command = Decide(policy, old);
            Fails(() => Begin(policy, Rejection(command), changed));
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
        }

        [Test]
        public void ActualHostEntranceLocationStaleRejectionPreservesIntent()
        {
            var fixture = CreateActualHostLink("entrance-location");
            for (int guard = 0; guard < 3; guard++)
            {
                var choice = fixture.Session.State.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" &&
                    r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction);
                Assert.That(choice != null, Is.True, "企业配方须先完成三人的真实企业选择。");
                var chosen = fixture.Submit(FormalCandidateAnswer(choice));
                Assert.That(chosen.Succeeded, Is.True, chosen.Validation?.Reason);
            }
            var firstRequest = fixture.Session.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 1);
            var placed = fixture.Submit(FormalCandidateAnswer(firstRequest));
            Assert.That(placed.Succeeded, Is.True, placed.Validation?.Reason);
            var firstView = fixture.Session.ProjectFor(GameStateViewer.Player(1));
            string firstLocation = firstView.Players.Single(p => p.PlayerId == 1).CityLocationId;
            Assert.That(!string.IsNullOrEmpty(firstLocation), Is.True);
            var oldView = fixture.Session.ProjectFor(GameStateViewer.Player(2));
            var request = oldView.Interactions.Single(r => r.Status == "open" &&
                r.Kind == PlayerEntranceEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 2);
            Assert.That(oldView.CurrentPlayerId, Is.EqualTo(2), "前位只提交占点即放行后位。");
            Assert.That(oldView.Effects.Single(e => e.EffectId == request.OwnerEffectId).EffectTypeId,
                Is.EqualTo(PlayerEntranceEffectExecutor.TypeId));
            var priorDuty = fixture.Session.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.AnsweringPlayerId == 1 && r.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction);
            var policy = Policy(); var original = Decide(policy, oldView); var saved = GameCommandDto.FromCommand(original);
            Assert.That(original.OptionIds.Count == 1 && request.CandidateIds.Contains(original.OptionIds[0]), Is.True);
            fixture.Save("本人原始选点意图", new { Command = saved, View = oldView, FirstLocation = firstLocation });
            var priorCompleted = fixture.Submit(FormalCandidateAnswer(priorDuty));
            Assert.That(priorCompleted.Succeeded, Is.True, priorCompleted.Validation?.Reason);
            var fresh = fixture.Session.ProjectFor(GameStateViewer.Player(2));
            Assert.That(fresh.StateRevision > oldView.StateRevision, Is.True);
            Assert.That(fresh.Interactions.Single(r => r.InteractionId == request.InteractionId).CandidateIds,
                Is.EqualTo(request.CandidateIds));
            Assert.That(Decide(policy, fresh) == null && Read<bool>(policy, "HasPendingCommand"), Is.True);
            string beforeReject = JsonConvert.SerializeObject(fixture.Session.State);
            var result = fixture.Submit(original);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(JsonConvert.SerializeObject(fixture.Session.State), Is.EqualTo(beforeReject));
            Assert.That(fresh.Players.Single(p => p.PlayerId == 2).CityLocationId, Is.EqualTo(""));
            var rejected = new RejectedGameCommandDto { Command = saved,
                ErrorCode = result.Validation.ErrorCode, Reason = result.Validation.Reason };
            fixture.Save("真实版本拒绝与新授权", new { Rejection = rejected, View = fresh });
            var foreign = Clone(rejected); foreign.Command.CommandId += "-其他命令";
            Assert.That(!Begin(policy, foreign, fresh) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            Assert.That(Begin(policy, rejected, oldView), Is.True);
            Assert.That(Decide(policy, oldView) == null && Read<bool>(policy, "IsWaitingForStaleInteractionRefresh"), Is.True);
            var rebuilt = Decide(policy, fixture.Session.ProjectFor(GameStateViewer.Player(2)));
            Assert.That(rebuilt.CommandId != original.CommandId && rebuilt.Parameters["expectedRevision"] ==
                fresh.StateRevision.ToString(CultureInfo.InvariantCulture), Is.True);
            Assert.That(JToken.DeepEquals(Intent(saved), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
            Call(policy, "Confirm", original.CommandId); Call(policy, "Confirm", new object[] { null });
            Assert.That(Read<bool>(policy, "HasPendingCommand") && !Begin(policy, rejected, fresh), Is.True);
            Assert.That((bool)Call(policy, "IsRetiredStaleInteractionRejection", rejected, fresh), Is.True);
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
            var accepted = fixture.Submit(rebuilt);
            Assert.That(accepted.Succeeded, Is.True, accepted.Validation?.Reason);
            var finalView = fixture.Session.ProjectFor(GameStateViewer.Player(2));
            Assert.That(finalView.Players.Single(p => p.PlayerId == 2).CityLocationId, Is.EqualTo(original.OptionIds[0]));
            Assert.That(finalView.Players.Single(p => p.PlayerId == 1).CityLocationId, Is.EqualTo(firstLocation));
            Assert.That(fixture.Session.State.EffectRuntime.InteractionRequests.Single(r =>
                r.GetStableInteractionId() == priorDuty.GetStableInteractionId()).Status, Is.EqualTo("answered"));
            Assert.That(finalView.Interactions.Any(r => r.Status == "open" && r.InteractionId == request.InteractionId), Is.False);
            var journal = fixture.Session.State.EffectRuntime.Journal.Where(e => e.CommandId == rebuilt.CommandId).ToList();
            Assert.That(journal.Count > 0 && fixture.Calls.Select(JObject.FromObject).Count(c =>
                (string)c["Command"]["CommandId"] == rebuilt.CommandId && (bool)c["Succeeded"]) == 1, Is.True);
            string afterAccepted = JsonConvert.SerializeObject(fixture.Session.State);
            var duplicateIntent = Clone(rebuilt); duplicateIntent.CommandId = Guid.NewGuid().ToString("N");
            var duplicate = fixture.Submit(duplicateIntent);
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(JsonConvert.SerializeObject(fixture.Session.State), Is.EqualTo(afterAccepted));
            Call(policy, "Confirm", rebuilt.CommandId);
            Assert.That(!Read<bool>(policy, "HasPendingCommand") && !Read<IEnumerable<string>>(policy, "CompletedSteps").Any(), Is.True);
            fixture.Save("最终真实选点结果", new { Original = saved, Rejection = rejected,
                Rebuilt = GameCommandDto.FromCommand(rebuilt), Journal = journal, FinalView = finalView,
                FirstLocation = firstLocation, PriorDuty = priorDuty.GetStableInteractionId(),
                Scope = "正式GameSession生成入場选点与前位裁牌义务；当前冻结Unity程序集离线联接，非真实网络、Unity或完整局。" });
        }

        [TestCase("candidate")]
        [TestCase("source")]
        [TestCase("closed")]
        [TestCase("unowned")]
        [TestCase("foreign-game")]
        [TestCase("duplicate-request")]
        public void EntranceLocationRefreshRejectsChangedAuthority(string change)
        {
            var old = View(Kinds[1]); Request(old).Kind = PlayerEntranceEffectExecutor.InteractionTypeId;
            Owner(old).EffectTypeId = PlayerEntranceEffectExecutor.TypeId;
            Request(old).SourceNodeId = Owner(old).EffectId;
            var changed = Renew(old, 101);
            switch (change)
            {
                case "candidate": Request(changed).CandidateIds.RemoveAt(1); break;
                case "source": Request(changed).SourceNodeId = "不同来源"; break;
                case "closed": Request(changed).Status = "answered"; break;
                case "unowned": changed.ViewerPlayerId = 1; break;
                case "foreign-game": changed.GameId = "不同权威局"; break;
                case "duplicate-request": changed.Interactions.Add(Clone(Request(changed))); break;
                default: throw new InvalidOperationException("未知入场选点反例。");
            }
            var pending = Policy(); var command = Decide(pending, old);
            Fails(() => Begin(pending, Rejection(command), changed));
            Assert.That(Read<bool>(pending, "HasPendingCommand"), Is.True);
            var waiting = Policy(); command = Decide(waiting, old); Assert.That(Begin(waiting, Rejection(command), old), Is.True);
            Fails(() => Decide(waiting, changed));
            Assert.That(Read<bool>(waiting, "IsWaitingForStaleInteractionRefresh") && !Read<bool>(waiting, "HasPendingCommand"), Is.True);
        }

        [Test]
        public void ExactTypedAndLegacyRejectionRebuildsSameIntentForThreeConcurrentKinds()
        {
            foreach (string kind in Kinds)
            foreach (bool legacy in new[] { false, true })
            {
                var view = View(kind); var plan = Plan(); var policy = Policy(plan: plan);
                var original = Decide(policy, view); var originalDto = GameCommandDto.FromCommand(original);
                Assert.That(original != null && original.PlayerId == 2 && original.OptionIds.Single() == "候选甲", Is.True);
                Assert.That(Decide(policy, Renew(view, 101)) == null && Read<bool>(policy, "HasPendingCommand"), Is.True);
                var rejected = Rejection(original, legacy);
                Assert.That(Begin(policy, rejected, view) && !Read<bool>(policy, "HasPendingCommand"), Is.True);
                Assert.That(Decide(policy, view) == null && Read<bool>(policy, "IsWaitingForStaleInteractionRefresh"), Is.True);
                // 如果重新运行Answer，这个优先级变化会选乙；重建必须仍然保留甲。
                var priority = (List<string>)plan.GetType().GetField("CandidatePriorityDefinitionIds").GetValue(plan);
                priority.Reverse();
                var newer = Renew(view, 101); newer.CurrentPlayerId = 1;
                var rebuilt = Decide(policy, newer);
                Assert.That(rebuilt.CommandId != original.CommandId && rebuilt.Parameters["expectedRevision"] == "101", Is.True);
                Assert.That(JToken.DeepEquals(Intent(originalDto), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
                Assert.That(original.Parameters["expectedRevision"], Is.EqualTo("100"));
                Call(policy, "Confirm", new object[] { null }); Call(policy, "Confirm", original.CommandId); Call(policy, "Confirm", "别人命令");
                Assert.That(Read<bool>(policy, "HasPendingCommand") && !Begin(policy, rejected, newer), Is.True);
                Assert.That((bool)Call(policy, "IsRetiredStaleInteractionRejection", rejected, newer), Is.True);
                var foreign = Clone(newer); foreign.GameId = "别局";
                Assert.That((bool)Call(policy, "IsRetiredStaleInteractionRejection", rejected, foreign), Is.False);
                Call(policy, "Confirm", rebuilt.CommandId);
                Assert.That(!Read<bool>(policy, "HasPendingCommand") && !Read<IEnumerable<string>>(policy, "CompletedSteps").Any() &&
                    Read<IReadOnlyDictionary<string, string>>(policy, "SkippedSteps").Count == 0, Is.True);
            }
        }

        [Test]
        public void RejectionMustMatchEverySubmittedCommandFieldAndExactErrorClassification()
        {
            var mutations = new Action<RejectedGameCommandDto>[] {
                r => r.Command.CommandId = "别人命令", r => r.Command.Kind = GameCommandKind.EndAction,
                r => r.Command.PlayerId = 1, r => r.Command.SourceId = "其他来源", r => r.Command.TargetId = "其他目标",
                r => r.Command.OptionIds.Reverse(), r => r.Command.OptionIds.Add("其他候选"),
                r => r.Command.Parameters.Single(p => p.Key == "expectedRevision").Value = "99",
                r => r.Command.Parameters.Single(p => p.Key == "interactionId").Value = "别的请求",
                r => r.Command.Parameters.RemoveAt(0), r => r.Command.Parameters.Add(r.Command.Parameters[0]),
                r => r.Command.Parameters.Add(null), r => r.Command.Parameters = null,
                r => r.Command = null, r => r.ErrorCode = CommandErrorCode.None,
                r => { r.ErrorCode = CommandErrorCode.InvalidTarget; r.Reason = "候选无效"; },
                r => { r.ErrorCode = CommandErrorCode.InvalidTarget; r.Reason = LegacyReason + " "; }
            };
            foreach (var mutate in mutations)
            {
                var view = View(Kinds[1]); Request(view).MinSelections = Request(view).MaxSelections = 2;
                var policy = Policy(); var command = Decide(policy, view); var rejected = Rejection(command); mutate(rejected);
                Assert.That(!Begin(policy, rejected, view) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            }
            var identityPolicy = Policy(); var identityView = View(Kinds[0]); var exact = Decide(identityPolicy, identityView);
            var reordered = Rejection(exact); reordered.Command.Parameters.Reverse();
            Assert.That(Begin(identityPolicy, reordered, identityView), Is.True);
            var confirmed = Policy(); var confirmedCommand = Decide(confirmed, identityView); Call(confirmed, "Confirm", confirmedCommand.CommandId);
            Assert.That(Begin(confirmed, Rejection(confirmedCommand), identityView), Is.False);
            Assert.That(Begin(confirmed, null, identityView), Is.False);
        }

        [Test]
        public void ChangedProjectionOrDependenciesFailBeforeAnyIntentCanBeRebuilt()
        {
            var changes = new Action<GameStateView>[] {
                v => v.GameId = "别局", v => v.Round++, v => v.Phase = GamePhase.Cleanup,
                v => v.ViewerRole = GameStateViewerRole.Host, v => v.ViewerPlayerId = 1,
                v => Request(v).AnsweringPlayerId = 1, v => Request(v).InteractionId = "别请求",
                v => Request(v).SourceNodeId = "其他来源", v => Request(v).OwnerEffectId = "其他owner",
                v => Request(v).Kind = "其他请求类型", v => Request(v).Status = "answered", v => v.Interactions.Clear(),
                v => Request(v).CandidateSetId = "别scope", v => Request(v).CandidateSetVersion++,
                v => Request(v).CandidateIds.Reverse(), v => Request(v).CandidateIds.RemoveAt(1),
                v => Request(v).MinSelections = 0, v => Request(v).MaxSelections = 2,
                v => Request(v).AllowDecline = true, v => Request(v).AnswerSchema = "boolean",
                v => Request(v).PromptKey = "其他提示", v => Request(v).Visibility = "public",
                v => Value(Request(v).PromptParameters, "budget").IntegerValue++,
                v => Value(Request(v).PromptParameters, "price").StringValue = "费用变化",
                v => Value(Request(v).PromptParameters, "factsDigest").StringValue = "事实变化",
                v => Value(Request(v).PromptParameters, "scopeKey").StringValue = "scope变化",
                v => Value(Request(v).PromptParameters, "requestRevision").Kind = NormalizedValueKind.String,
                v => Value(Owner(v).NormalizedArguments, "keepCount").IntegerValue++,
                v => Owner(v).ParentEffectId = "其他父owner", v => Owner(v).EffectTypeId = "其他owner类型",
                v => Owner(v).PlayerId = 1, v => v.Effects.Clear()
            };
            foreach (string kind in Kinds)
            foreach (var change in changes)
            {
                var old = View(kind); var changed = Renew(old, 101); change(changed);
                var before = Policy(); var command = Decide(before, old);
                Fails(() => Begin(before, Rejection(command), changed));
                Assert.That(Read<bool>(before, "HasPendingCommand"), Is.True);
                var waiting = Policy(); command = Decide(waiting, old); Begin(waiting, Rejection(command), old);
                Fails(() => Decide(waiting, changed));
                Assert.That(Read<bool>(waiting, "IsWaitingForStaleInteractionRefresh") && !Read<bool>(waiting, "HasPendingCommand"), Is.True);
            }
        }

        [Test]
        public void AdditionalOrUnownedRequestsAndPlannedCommandsDoNotAcquireAutomaticRefresh()
        {
            foreach (var change in new Action<GameStateView>[] {
                v => Value(Request(v).PromptParameters, "purpose").IntegerValue = 1,
                v => Value(Request(v).PromptParameters, "purpose").IntegerValue = 2,
                v => Value(Owner(v).NormalizedArguments, "purpose").IntegerValue = 1,
                v => Owner(v).NormalizedArguments = Arguments(), v => v.Effects.Clear(),
                v => Owner(v).EffectTypeId = "其他选路owner" })
            {
                var view = View(Kinds[0]); change(view); var policy = Policy(); var command = Decide(policy, view);
                Assert.That(command != null && !Begin(policy, Rejection(command), view) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            }
            foreach (var args in new[] { Arguments(Integer("apiVersion", 2)), Arguments(String("coverMode", "additional")) })
            {
                var view = View(Kinds[2]); Owner(view).NormalizedArguments = args; var policy = Policy(); var command = Decide(policy, view);
                Assert.That(!Begin(policy, Rejection(command), view) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            }
            foreach (string kind in new[] { Kinds[1], Kinds[2] })
            {
                var view = View(kind); view.Effects.Clear(); var policy = Policy(); var command = Decide(policy, view);
                Assert.That(!Begin(policy, Rejection(command), view) && Read<bool>(policy, "HasPendingCommand"), Is.True);
            }
            var plannedView = View(Kinds[1]); var plannedJson = JObject.FromObject(Plan());
            plannedJson["Steps"] = JArray.Parse(@"[{'StepId':'精确反例','PlayerId':2,'Round':1,'Phase':'Entrance',
                'CommandKind':'AnswerInteraction','RequestKind':'setup.enterprise.choose','CandidateIds':['候选甲'],
                'ExpectRejection':true,'ExpectedErrorCode':'StateRevisionMismatch'}]");
            var planned = Policy(plan: plannedJson.ToObject(Target("ProductionGameRoutePlan"))); var plannedCommand = Decide(planned, plannedView);
            Assert.That(Begin(planned, Rejection(plannedCommand), plannedView), Is.False);
            Assert.That((bool)Call(planned, "Reject", plannedCommand.CommandId, "InvalidTarget"), Is.False);
            Assert.That((bool)Call(planned, "Reject", plannedCommand.CommandId, "StateRevisionMismatch"), Is.True);
            Assert.That(Read<IEnumerable<string>>(planned, "CompletedSteps").Single(), Is.EqualTo("精确反例"));
        }

        [Test]
        public void OnlyConsistentNewAuthorizationReleasesWaitAndRevisionCannotRegress()
        {
            foreach (string kind in Kinds)
            {
                var old = View(kind); var policy = Policy(); var command = Decide(policy, old); Begin(policy, Rejection(command), old);
                Assert.That(Decide(policy, old) == null, Is.True);
                var inconsistent = Renew(old, 101); Request(inconsistent).StateRevision = 100;
                Value(Request(inconsistent).PromptParameters, "requestRevision").IntegerValue = 100;
                Assert.That(Decide(policy, inconsistent) == null && Read<bool>(policy, "IsWaitingForStaleInteractionRefresh"), Is.True);
                var regressed = Renew(old, 99); Fails(() => Decide(policy, regressed));
                var promptAhead = Renew(old, 101); Value(Request(promptAhead).PromptParameters, "requestRevision").IntegerValue = 102;
                Fails(() => Decide(policy, promptAhead));
                var promptBack = Renew(old, 101); Value(Request(promptBack).PromptParameters, "requestRevision").IntegerValue = 99;
                Fails(() => Decide(policy, promptBack));
                Assert.That(Decide(policy, Renew(old, 101)) != null && Read<bool>(policy, "HasPendingCommand"), Is.True);
            }
        }

        [Test]
        public void ThreeRebuildLimitAndTenSecondWaitRemainFiniteForEveryKind()
        {
            foreach (string kind in Kinds)
            {
                double now = 0; var view = View(kind); var policy = Policy(() => now); var command = Decide(policy, view);
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    Assert.That(Begin(policy, Rejection(command), view), Is.True);
                    now++; view = Renew(view, 100 + attempt); command = Decide(policy, view);
                    Assert.That(command != null && command.Parameters["expectedRevision"] == (100 + attempt).ToString(), Is.True);
                }
                Fails(() => Begin(policy, Rejection(command), view));
                Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
                now = 0; view = View(kind); var timeout = Policy(() => now); command = Decide(timeout, view); Begin(timeout, Rejection(command), view);
                now = 9.99; Assert.That(Decide(timeout, view) == null, Is.True);
                now = 10; Fails(() => Call(timeout, "CheckStaleInteractionRefreshTimeout"));
                Assert.That(!Read<IEnumerable<string>>(timeout, "CompletedSteps").Any() &&
                    Read<IReadOnlyDictionary<string, string>>(timeout, "SkippedSteps").Count == 0, Is.True);
            }
        }

        private static object WholeGamePlan()
        {
            var json = JObject.FromObject(Plan());
            json["EvaluationMode"] = "whole-game-health";
            json["AllowAdaptiveMainActions"] = true;
            return json.ToObject(Target("ProductionGameRoutePlan"));
        }

        [Test]
        public void WholeGameEntranceRefreshCoalescesChangesAndPreservesTheOriginalIntent()
        {
            double now = 0; var old = View(Kinds[1]); var policy = Policy(() => now, WholeGamePlan());
            var original = Decide(policy, old); var fresh = Renew(old, 101);
            Assert.That(Decide(policy, fresh) == null && Read<bool>(policy, "HasPendingCommand"), Is.True);
            Assert.That(Begin(policy, Rejection(original), fresh), Is.True);
            Assert.That(Decide(policy, fresh) == null, Is.True);
            now = 0.75; Assert.That(Decide(policy, fresh) == null, Is.True);
            now = 1.5; fresh = Renew(old, 102); Assert.That(Decide(policy, fresh) == null, Is.True);
            now = 2.49; Assert.That(Decide(policy, fresh) == null, Is.True);
            now = 2.5; var rebuilt = Decide(policy, fresh);
            Assert.That(rebuilt != null && rebuilt.CommandId != original.CommandId && rebuilt.Parameters["expectedRevision"] == "102", Is.True);
            Assert.That(JToken.DeepEquals(Intent(GameCommandDto.FromCommand(original)), Intent(GameCommandDto.FromCommand(rebuilt))), Is.True);
            Assert.That((int)JObject.FromObject(Read<object>(policy, "StaleInteractionRefreshEvidence"))["Attempts"], Is.EqualTo(1));
        }

        [Test]
        public void WholeGameEntranceCoalescingKeepsCurrentnessBeyondTheFormerDeadline()
        {
            double now = 0; var view = View(Kinds[1]); var policy = Policy(() => now, WholeGamePlan()); var command = Decide(policy, view);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Assert.That(Begin(policy, Rejection(command), view), Is.True);
                now += 0.1; view = Renew(view, 100 + attempt); Assert.That(Decide(policy, view) == null, Is.True);
                now += 1.6; command = Decide(policy, view); Assert.That(command != null, Is.True);
            }
            Fails(() => Begin(policy, Rejection(command), view));
            Assert.That(Read<bool>(policy, "HasPendingCommand"), Is.True);
            now = 0; view = View(Kinds[1]); var timeout = Policy(() => now, WholeGamePlan()); command = Decide(timeout, view);
            Assert.That(Begin(timeout, Rejection(command), view), Is.True);
            for (int second = 1; second < 10; second++) { now = second; view = Renew(view, 100 + second); Assert.That(Decide(timeout, view) == null, Is.True); }
            now = 10; Call(timeout, "CheckStaleInteractionRefreshTimeout");
            Assert.That(Read<bool>(timeout, "IsWaitingForStaleInteractionRefresh"), Is.True);
            now = 600; Call(timeout, "CheckStaleInteractionRefreshTimeout");
            Fails(() => Decide(timeout, Renew(view, 99)));
            var renewed = Decide(timeout, view);
            Assert.That(renewed != null && renewed.Parameters["expectedRevision"] == "109", Is.True,
                "超过旧时限仍等待真实当前授权，不吞掉倒退投影守卫。");
        }
    }
}
