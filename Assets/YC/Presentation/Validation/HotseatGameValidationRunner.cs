#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Maps;
using YC.Domain.Travel;
using YC.Domain.Effects;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Presentation.Validation
{
    // 显式验收入口：正式开局与提交，只从各玩家自己的投影决定命令。
    public static class HotseatGameValidationRunner
    {
        public static string Run(string mode, string outputDirectory, string prioritiesPath, int? explicitSeed = null)
        {
            if (!new[] { "core", "enterprise", "infected" }.Contains(mode))
                throw new ArgumentException("未知热座模式。");
            if (!Path.IsPathRooted(outputDirectory) || Directory.Exists(outputDirectory))
                throw new ArgumentException("热座输出必须是尚不存在的绝对路径。");
            Directory.CreateDirectory(outputDirectory);
            var report = new HotseatResult { Mode = mode, PlayerCount = mode == "infected" ? 4 : 3,
                ExplicitSeed = explicitSeed, StartedUtc = DateTime.UtcNow,
                ProcessId = Process.GetCurrentProcess().Id, UnityVersion = UnityEngine.Application.unityVersion };
            var timer = Stopwatch.StartNew();
            GameObject owner = null;
            GameSession session = null;
            ActiveContentCatalog catalog = null;
            var rounds = new HashSet<int>();
            var recordedEffects = new HashSet<string>(StringComparer.Ordinal);
            var recordedModes = new HashSet<string>(StringComparer.Ordinal);
            var policies = new Dictionary<int, ProductionGameRoutePolicy>();
            try
            {
                owner = new GameObject("热座完整局验收上下文") { hideFlags = HideFlags.HideAndDontSave };
                owner.SetActive(false); // 不注册全局 Instance，不进入场景或操作界面。
                var context = owner.AddComponent<GameLaunchContext>();
                var configuration = new GameRuleConfiguration { PlayerCount = report.PlayerCount,
                    MapId = report.PlayerCount == 4 ? "map-four-players" : "map-three-players",
                    EnabledExpansionIds = mode == "core" ? new List<string> { "core" } :
                        mode == "enterprise" ? new List<string> { "core", "enterprise" } :
                        new List<string> { "core", "enterprise", "infected" } };
                var seats = Enumerable.Range(1, report.PlayerCount).Select(id => new PlayerSeat {
                    PlayerId = id, PlayerName = "热座玩家" + id, Color = (PlayerColor)(id - 1) }).ToList();
                if (explicitSeed.HasValue)
                {
                    var configure = typeof(GameLaunchContext).GetMethod("ConfigureNewLocal", new[] {
                        typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration),
                        typeof(int?), typeof(IReadOnlyList<int>) });
                    if (configure == null) throw new InvalidOperationException("当前运行版本未提供正式显式种子入口。");
                    Invoke(configure, context, new object[] { 1, "", seats, configuration, explicitSeed, null });
                }
                else context.Configure(LaunchMode.Local, 1, "", seats, configuration);

                // Assembly-CSharp 内部正式装配；反射只用于保持 EditMode asmdef 无跨 Assembly-CSharp 引用。
                var bootstrapper = typeof(GameLaunchContext).Assembly.GetType("YC.Presentation.GameSessionBootstrapper", true);
                var build = bootstrapper.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
                var built = Invoke(build, null, new object[] { context, false, false });
                session = (GameSession)built.GetType().GetField("Session").GetValue(built);
                report.GameId = session.State.GameId;
                report.Seed = session.State.EventDeckSeed;
                report.ContentPackHash = session.State.ContentPackHash;
                report.LaunchAssemblyVersion = typeof(GameLaunchContext).Assembly.ManifestModule.ModuleVersionId.ToString();
                report.SessionAssemblyVersion = typeof(GameSession).Assembly.ManifestModule.ModuleVersionId.ToString();
                report.RandomAlgorithmVersion = JObject.FromObject(session.State)["RandomRuntime"]?["AlgorithmVersion"]?.Value<string>();
                if (context.Mode != LaunchMode.Local || session.State.Players.Count != report.PlayerCount ||
                    session.State.RuleConfiguration == null ||
                    !session.State.RuleConfiguration.EnabledExpansionIds.SequenceEqual(configuration.EnabledExpansionIds))
                    throw new InvalidOperationException("正式开局事实不匹配请求的热座模式。");
                report.FormalLaunchCompleted = true;
                WriteNew(outputDirectory, "initial-state.json", session.State);
                catalog = ActiveContentCatalogBuilder.Build(ExternalContentRuntime.Pack, configuration);
                if (catalog.ContentPackHash != report.ContentPackHash) throw new InvalidOperationException("活动目录与实际本局版本不一致。");
                WriteNew(outputDirectory, "active-catalog.json", new { catalog.ContentPackHash,
                    catalog.EnabledExpansionIds, definitions = catalog.Definitions.Select(definition => new {
                        definition.DefinitionId, definition.RuntimeId, definition.ContentType, definition.Data, definition.Abilities }),
                    nativeDefinitions = new { SpecialActions = catalog.ContentDefinitions.SpecialActions } });
                var priorities = string.IsNullOrWhiteSpace(prioritiesPath) ? null :
                    JsonConvert.DeserializeObject<ProductionGameRoutePlan>(File.ReadAllText(prioritiesPath));
                if (priorities != null && priorities.ContentPackHash != report.ContentPackHash)
                    throw new InvalidOperationException("覆盖优先级内容版本与正式开局不一致。");
                report.PriorityPlanPath = prioritiesPath;
                var plan = new ProductionGameRoutePlan { RouteName = "hotseat-" + mode,
                    PlayerCount = report.PlayerCount, MapId = configuration.MapId, ContentPackHash = report.ContentPackHash,
                    EnabledExpansionIds = new List<string>(configuration.EnabledExpansionIds),
                    EvaluationMode = "whole-game-health", AllowAdaptiveMainActions = true,
                    MaximumCommands = 2500,
                    SelectionPriorities = priorities?.SelectionPriorities ?? new ProductionGameSelectionPriorities(),
                    CandidatePriorityDefinitionIds = priorities?.CandidatePriorityDefinitionIds ?? new List<string>() };
                WriteNew(outputDirectory, "actual-route-plan.json", plan);
                foreach (int id in seats.Select(seat => seat.PlayerId)) policies[id] = new ProductionGameRoutePolicy(plan);
                using (var trace = NewLines(outputDirectory, "commands.jsonl"))
                using (var coverage = NewLines(outputDirectory, "observed-coverage.jsonl"))
                {
                    Capture(session.State, rounds, recordedEffects, recordedModes, coverage);
                    while (!Terminal(session.State))
                    {
                        if (Faulted(session.State)) { report.Failure = "effect_fault"; break; }
                        if (report.SubmittedCommands >= plan.MaximumCommands) { report.Failure = "command_budget_exhausted"; break; }
                        if (timer.Elapsed.TotalSeconds >= report.MaximumDurationSeconds) { report.Failure = "validation_timeout"; break; }
                        bool advanced = false;
                        var waiting = new List<object>();
                        foreach (int id in seats.Select(seat => seat.PlayerId))
                        {
                            // 每次重新投影后立即同步 Submit；保留正式多玩家任务，不缓存旧 revision。
                            var view = session.ProjectFor(GameStateViewer.Player(id));
                            if (view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != id)
                                throw new InvalidOperationException("热座驱动收到非本人投影。");
                            var command = policies[id].Decide(view, id, out string reason);
                            if (command == null) { waiting.Add(new { playerId = id, reason, view }); continue; }
                            if (!TryCompleteRouteSelectionAnswer(view, command, out string routeDiagnostic))
                            {
                                report.Failure = "route_driver_answer_unavailable";
                                WriteNew(outputDirectory, "route-driver-player-view.json", new { routeDiagnostic, view });
                                break;
                            }
                            report.SubmittedCommands++;
                            var result = session.Submit(command);
                            trace.WriteLine(JsonConvert.SerializeObject(new { utc = DateTime.UtcNow,
                                session.State.GameId, playerId = id, authorizedRevision = view.StateRevision,
                                command, routeDiagnostic, result, finalRevision = session.State.EffectRuntime.StateRevision,
                                session.State.Round, session.State.Phase }));
                            trace.Flush();
                            Capture(session.State, rounds, recordedEffects, recordedModes, coverage);
                            if (!result.Succeeded)
                            {
                                report.RejectionCode = result.Validation?.ErrorCode.ToString();
                                report.Failure = "command_rejected";
                                WriteNew(outputDirectory, "rejected-command-player-view.json", view);
                                break;
                            }
                            // 本地真实 Submit 已完成，才确认原 commandId；不修改网络 pending/确认逻辑。
                            report.AcceptedCommands++;
                            policies[id].Confirm(command.CommandId);
                            advanced = true;
                            break; // 重新逐玩家取最新合法投影，避免同步提交后的过期输入。
                        }
                        if (report.Failure != null) break;
                        if (!advanced)
                        {
                            report.Failure = "no_legal_driver_progress";
                            WriteNew(outputDirectory, "waiting-player-views.json", waiting);
                            break;
                        }
                    }
                    Capture(session.State, rounds, recordedEffects, recordedModes, coverage);
                }
                report.FinalRound = session.State.Round;
                report.FinalPhase = session.State.Phase.ToString();
                report.CompletedRounds = rounds.OrderBy(round => round).ToArray();
                report.CompletedEffectCount = recordedEffects.Count;
                report.CompletedCharacterModeCount = recordedModes.Count;
                report.EffectFaultCode = session.State.EffectRuntime.LastFaultCode;
                report.EffectFaultMessage = session.State.EffectRuntime.LastFaultMessage;
                bool final = Terminal(session.State) && report.CompletedRounds.SequenceEqual(Enumerable.Range(1, 8)) &&
                    session.State.FinalScoring.PlayerScores.Count == report.PlayerCount &&
                    session.State.EffectRuntime.InteractionRequests.All(request => request.Status != "open");
                if (Faulted(session.State)) report.Failure = "effect_fault";
                if (report.Failure == null && !final) report.Failure = "incomplete_final_evidence";
                report.Succeeded = report.Failure == null && final;
                report.EffectChainHealthy = report.Succeeded ? true : Faulted(session.State) ? (bool?)false : null;
                report.RunOutcome = report.Succeeded ? "completed" : Faulted(session.State) ? "game_failure" :
                    report.Failure == "command_rejected" ? "needs_rejection_diagnosis" : "incomplete";
                WriteNew(outputDirectory, "final-state.json", session.State);
                WriteNew(outputDirectory, "host-session-archive.json", session.CreateHostArchive(report.ContentPackHash));
                try { WriteCoverage(outputDirectory, session.State, catalog, report); }
                catch (Exception coverageError)
                {
                    report.CoverageExtractionError = coverageError.ToString();
                    WriteNew(outputDirectory, "coverage-extraction-failure.json", new { report.GameId, report.ContentPackHash, report.CoverageExtractionError });
                }
            }
            catch (Exception error)
            {
                report.Succeeded = false;
                report.Failure = report.Failure ?? "validation_exception";
                report.RunOutcome = "driver_or_initialization_failure";
                report.Exception = error.ToString();
                if (session != null) WriteNew(outputDirectory, "exception-state.json", session.State);
            }
            finally
            {
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                report.ElapsedSeconds = timer.Elapsed.TotalSeconds;
                report.FinishedUtc = DateTime.UtcNow;
            }
            WriteNew(outputDirectory, "result.json", report);
            return JsonConvert.SerializeObject(report);
        }

        private static bool Terminal(GameState state) => state.Round == 8 && state.Phase == GamePhase.FinalScoring && state.FinalScoring?.IsResolved == true;
        private static bool Faulted(GameState state) => state.EffectRuntime.Status == EffectRuntimeStatus.PausedFault ||
            !string.IsNullOrEmpty(state.EffectRuntime.LastFaultCode) || state.EffectRuntime.EffectNodes.Any(node => node.Status == EffectNodeStatus.Faulted);
        private static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            if (method == null) throw new MissingMethodException("缺少正式热座装配入口。");
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static StreamWriter NewLines(string root, string name) => new StreamWriter(
            new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
// 仅暂存供本次验证驱动修补；未纳入活动源码，不改变生产规则。
// 合入 HotseatGameValidationRunner 时使用 Domain.Maps/Travel/Effects 与 Application.Interactions。
public static bool TryCompleteRouteSelectionAnswer(GameStateView view, GameCommand command, out string diagnostic)
{
    diagnostic = "";
    if (command.Kind != GameCommandKind.AnswerInteraction ||
        !command.Parameters.TryGetValue(AnswerInteractionCommandHandler.InteractionIdParameter, out string interactionId)) return true;
    var request = view.Interactions.FirstOrDefault(r => r.InteractionId == interactionId && r.Kind == "route_resource.selection" && r.Status == "open");
    if (request == null) return true;
    var state = GameStateViewProjector.ToClientState(view);
    var map = new MapQueryService(StaticMapDefinitions.Resolve(view.MapId));
    var authority = new RouteSelectionSessionAuthority(map);
    var read = authority.TryRead(view, request, out var session, out var binding);
    if (!read.IsValid) { diagnostic = read.Reason; return false; }
    var service = new RouteResourceSelectionService(map);
    var eligible = RouteResourceSelectionEffectExecutor.GetSelectableLocations(state, session, map)
        .Where(id => session.SelectableLocationUpperBound.Contains(id)).ToArray();
    var routes = new List<string>();
    var recipients = new List<RouteRecipientChoice>();
    string commit = RouteSelectionAnswerCodec.Encode(binding.ScopeKey, RouteSelectionCandidateKind.Commit);
    if (!request.CandidateIds.Contains(commit)) { diagnostic = "正式候选缺少选路确认"; return false; }
    // 从真实只读投影逐步扩展航道边界；每个最终答案仍须通过原选路服务验证。
    for (int expansion = 0; expansion <= request.CandidateIds.Count; expansion++)
    {
        var draft = new RouteSelectionDraft(session.SessionId, expansion, routes, Array.Empty<string>(), recipients);
        var projection = service.Query(state, session, draft, eligible);
        foreach (var location in projection.LocationOptions ?? Array.Empty<RouteSelectionLocationOption>())
        {
            if (!location.IsReachableInOriginalDraft) continue;
            string locationCandidate = RouteSelectionAnswerCodec.Encode(binding.ScopeKey, RouteSelectionCandidateKind.Location, location.LocationId);
            if (!request.CandidateIds.Contains(locationCandidate)) continue;
            var selected = new RouteSelectionDraft(session.SessionId, expansion, routes, new[] { location.LocationId }, recipients);
            var selectedProjection = service.Query(state, session, selected, eligible);
            var valid = selectedProjection.Validation;
            if (!valid.IsValid || selectedProjection.Preview == null || !selectedProjection.Preview.FinalizationValidation.IsValid)
            { diagnostic = valid.IsValid ? selectedProjection.Preview?.FinalizationValidation.Reason : valid.Reason; continue; }
            var answer = new List<string> { commit, locationCandidate };
            foreach (var recipient in recipients)
                answer.Add(RouteSelectionAnswerCodec.Encode(binding.ScopeKey, RouteSelectionCandidateKind.RouteAndRecipient, recipient.RouteId, recipient.ReceiverSubjectKey));
            if (answer.Count < request.MinSelections || answer.Count > request.MaxSelections || answer.Any(id => !request.CandidateIds.Contains(id))) continue;
            command.OptionIds.Clear(); command.OptionIds.AddRange(answer);
            diagnostic = "正式选路服务已验证单地点答案";
            return true;
        }
        RouteSelectionRouteOption next = null;
        RouteRecipientChoice recipientChoice = null;
        foreach (var route in projection.RouteOptions ?? Array.Empty<RouteSelectionRouteOption>())
        {
            if (!route.IsFrontier || route.FreeReason != RouteFreeReason.None || !route.CanAffordIfCharged || routes.Contains(route.RouteId)) continue;
            var recipient = route.EligibleRecipients.FirstOrDefault(r => request.CandidateIds.Contains(
                RouteSelectionAnswerCodec.Encode(binding.ScopeKey, RouteSelectionCandidateKind.RouteAndRecipient, route.RouteId, r.SubjectKey)));
            if (recipient == null) continue;
            next = route; recipientChoice = new RouteRecipientChoice(route.RouteId, recipient.SubjectKey); break;
        }
        if (next == null) break;
        routes.Add(next.RouteId); recipients.Add(recipientChoice);
    }
    // 0点采集和取消均只使用当前正式协议已允许的分支，探索不提交空确认。
    if (session.MinimumLocations == 0)
    {
        var empty = new RouteSelectionDraft(session.SessionId, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RouteRecipientChoice>(), "", true);
        var emptyProjection = service.Query(state, session, empty, eligible);
        var valid = emptyProjection.Validation;
        if (valid.IsValid && emptyProjection.Preview != null && emptyProjection.Preview.FinalizationValidation.IsValid)
        { command.OptionIds.Clear(); command.OptionIds.Add(commit); diagnostic = "正式服务已验证0点采集"; return true; }
        diagnostic = valid.Reason;
    }
    string cancel = RouteSelectionAnswerCodec.Encode(binding.ScopeKey, RouteSelectionCandidateKind.Cancel);
    if (session.AllowCancelBeforeCommit && request.CandidateIds.Contains(cancel))
    {
        command.OptionIds.Clear(); command.OptionIds.Add(cancel); diagnostic = "无已验证地点，使用正式取消候选"; return true;
    }
    diagnostic = "无法构造已验证的正式选路答案：" + diagnostic;
    return false;
}


        private static void WriteNew(string root, string name, object value)
        {
            using (var text = NewLines(root, name))
            using (var writer = new JsonTextWriter(text)) JsonSerializer.CreateDefault().Serialize(writer, value);
        }
        private static void Capture(GameState state, HashSet<int> rounds, HashSet<string> effects, HashSet<string> modes, TextWriter output)
        {
            if (state.Round >= 1 && state.Round <= 8) rounds.Add(state.Round);
            var completed = state.EffectRuntime.EffectNodes.Where(node => node.Status == EffectNodeStatus.Completed && effects.Add(node.EffectId))
                .Select(node => new { node.EffectId, node.ParentEffectId, node.EffectTypeId, node.SourceId, node.PlayerId,
                    node.ContinuationContentId, node.ContinuationAbilityId, node.NormalizedArguments, node.NormalizedResult, node.ContinuationData }).ToArray();
            var characterModes = state.CharacterUses.SelectMany(use => use.CompletedModes.Where(mode => modes.Add(use.UseId + ":" + mode))
                .Select(mode => new { use.UseId, use.CardInstanceId, use.ExecutingPlayerId,
                    DefinitionId = state.ItemZones.Items.FirstOrDefault(item => item.ItemId == use.CardInstanceId)?.DefinitionId, Mode = mode })).ToArray();
            if (completed.Length > 0 || characterModes.Length > 0) output.WriteLine(JsonConvert.SerializeObject(new {
                utc = DateTime.UtcNow, state.GameId, state.ContentPackHash, state.Round, state.Phase,
                state.EffectRuntime.StateRevision, CompletedEffects = completed, CharacterModes = characterModes }));
            output.Flush();
        }

        // 与既有正式归档审计同样仅统计已完成 Effect、实际完成模式/声明；缺抽取不影响整局健康。
        private static void WriteCoverage(string root, GameState state, ActiveContentCatalog catalog, HotseatResult report)
        {
            var definitions = catalog.Definitions.ToDictionary(definition => definition.DefinitionId, StringComparer.Ordinal);
            var byRuntime = catalog.Definitions.Where(definition => !string.IsNullOrEmpty(definition.RuntimeId)).GroupBy(definition => definition.RuntimeId).ToDictionary(group => group.Key, group => group.First().DefinitionId, StringComparer.Ordinal);
            var items = state.ItemZones.Items.ToDictionary(item => item.ItemId, item => item.DefinitionId, StringComparer.Ordinal);
            var observed = new Dictionary<string, object>(StringComparer.Ordinal);
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            Func<string, string> definitionFor = id => {
                if (string.IsNullOrEmpty(id)) return "";
                if (items.TryGetValue(id, out string definition)) return definition;
                if (definitions.ContainsKey(id)) return id;
                if (byRuntime.TryGetValue(id, out definition)) return definition;
                unresolved.Add(id); return "";
            };
            Action<string, string, string, string> add = (category, id, mode, evidenceId) => {
                if (string.IsNullOrEmpty(id)) return;
                string key = category + "|" + id + "|" + mode;
                if (!observed.ContainsKey(key)) observed[key] = new { Category = category, DefinitionId = id, Mode = mode, EvidenceId = evidenceId };
            };
            foreach (var node in state.EffectRuntime.EffectNodes.Where(node => node.Status == EffectNodeStatus.Completed))
            {
                add("definition_effect", definitionFor(node.ContinuationContentId), "", node.EffectId);
                var result = Plain(node.NormalizedResult);
                var resultObject = result as JObject ?? new JObject();
                var args = Plain(node.NormalizedArguments) as JObject ?? new JObject();
                switch (node.EffectTypeId)
                {
                    case "setup.enterprise.select":
                        foreach (var id in resultObject["selectedEnterpriseItemIds"] ?? new JArray()) add("enterprise_selected", definitionFor(id.Value<string>()), "", node.EffectId);
                        break;
                    case "effect.operator.dispatch":
                        if (resultObject["accepted"]?.Value<bool>() == true) add("dispatch_character", definitionFor(resultObject.Value<string>("selectedOperatorItemId")), "", node.EffectId);
                        break;
                    case "effect.facility.build":
                        if (result.Type == JTokenType.String) add("facility_built", definitionFor(result.Value<string>()), "", node.EffectId);
                        break;
                    case "effect.event_card.resolve":
                        add("event_resolved", definitionFor(resultObject.Value<string>("cardId")), "", node.EffectId);
                        if (resultObject["selectedOptionIndex"] != null) add("event_option", definitionFor(resultObject.Value<string>("cardId")), resultObject["selectedOptionIndex"].ToString(), node.EffectId);
                        break;
                    case "effect.city_style.special_action.activate":
                        add("special_action_used", args.Value<string>("specialActionId"), "", node.EffectId); break;
                }
            }
            foreach (var use in state.CharacterUses)
                foreach (string mode in use.CompletedModes) add("character_mode", definitionFor(use.CardInstanceId), mode, use.UseId);
            foreach (var player in state.Players)
                foreach (var declaration in player.DeclaredCityStyles) add("city_style_declared", definitionFor(declaration.CityStyleId), "", declaration.DeclarationId);
            report.ObservedCoverageCount = observed.Count;
            WriteNew(root, "content-coverage.json", new { SchemaVersion = 1, EvaluationMode = "whole-game-health",
                state.GameId, state.ContentPackHash, EnabledExpansionIds = catalog.EnabledExpansionIds,
                MapId = state.MapId, PlayerCount = state.Players.Count, GameplayRunPassed = report.Succeeded,
                report.RunOutcome, report.EffectChainHealthy, ObservedCoverage = observed.Values.ToArray(),
                UnresolvedCoverageIdentities = unresolved.OrderBy(id => id).ToArray(), CoverageIsIndependentOfGameplayPass = true });
        }
        private static JToken Plain(NormalizedValue value)
        {
            if (value == null) return JValue.CreateNull();
            switch (value.Kind.ToString())
            {
                case "Boolean": return new JValue(value.BooleanValue);
                case "Integer": return new JValue(value.IntegerValue);
                case "String": return new JValue(value.StringValue);
                case "StableReference": case "Reference": return new JValue(value.ReferenceId);
                case "Array": return new JArray(value.Items.Select(Plain));
                case "Object": return new JObject(value.Properties.Select(property => new JProperty(property.Name, Plain(property.Value))));
                default: return JValue.CreateNull();
            }
        }

        [Serializable]
        private sealed class HotseatResult
        {
            public int SchemaVersion = 1;
            public string Mode, GameId, ContentPackHash, RandomAlgorithmVersion, PriorityPlanPath;
            public string LaunchAssemblyVersion, SessionAssemblyVersion, CoverageExtractionError;
            public int PlayerCount, ProcessId, SubmittedCommands, AcceptedCommands, FinalRound;
            public int CompletedEffectCount, CompletedCharacterModeCount, ObservedCoverageCount;
            public int MaximumDurationSeconds = 1800;
            public int? ExplicitSeed, Seed;
            public string LaunchMode = "Local", UnityVersion, FinalPhase, Failure, RejectionCode, EffectFaultCode, EffectFaultMessage, Exception, RunOutcome;
            public string FormalLaunchPath = "GameSessionBootstrapper.Build(context, false, false)";
            public string CommandPath = "GameSession.Submit(GameCommand)";
            public bool SmokeStateUsed = false, ExtraResourcesCardsOrFacilitiesInjected = false, UiInputUsed = false;
            public bool OwnPlayerProjectionOnly = true, FormalLaunchCompleted, Succeeded;
            public bool? EffectChainHealthy;
            public int[] CompletedRounds = new int[0];
            public DateTime StartedUtc, FinishedUtc;
            public double ElapsedSeconds;
        }
    }
}
#endif
