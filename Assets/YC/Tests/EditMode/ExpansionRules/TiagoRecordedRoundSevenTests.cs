using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 自然存档的只读副本用于隔离定位；不代表可见程序或真实 Steam 联机验收。
    public sealed class TiagoRecordedRoundSevenTests
    {
        [TestCase(false)] [TestCase(true)]
        public void RecordedCleanupInvestmentOffersItsConfirmation(bool exchangeFirst)
        {
            string path = Environment.GetEnvironmentVariable("YC_DICE_REPRO_SAVE");
            string hash = Environment.GetEnvironmentVariable("YC_DICE_REPRO_SAVE_SHA256");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash))
                Assert.Ignore("仅在显式提供隔离存档副本及摘要时运行。");
            path = Path.GetFullPath(path);
            string root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.That(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(IsolatedTestArtifacts.FileHash(path), Is.EqualTo(hash.ToLowerInvariant()));
            var evidence = IsolatedTestArtifacts.BeginCase();
            try
            {
                var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(path))["Payload"]);
                string content = evidence.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "基金投资正式内容隔离副本");
                var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
                runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { content, Path.Combine(evidence.InputsDirectory, "独立文案登记") });
                var bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var restored = bootstrap.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
                var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
                var registry = (EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
                var state = session.State;
                Assert.That(state.Round, Is.EqualTo(7));
                Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
                var advance = new RoundExecutionService(registry).Advance(state);
                Assert.That(advance.IsValid, Is.True, advance.Reason);
                if (exchangeFirst)
                {
                    int score = state.FindPlayer(3).Score;
                    for (int count = 0; count < 2; count++)
                    {
                        var exchange = registry.GetSettlementOptions(session.State, 3).Single(o =>
                            o.SourceDefinitionId == "entrepreneur.round.D.1" && o.CleanupRequest != null);
                        Assert.That(exchange.IsEnabled, Is.True, exchange.DisabledReasonCode);
                        var exchangeCommand = CleanupSourceCommand.Create(3, Guid.NewGuid().ToString("N"), exchange.CleanupRequest, exchange.StateRevision);
                        var exchangeResult = session.Submit(exchangeCommand);
                        Assert.That(exchangeResult.Succeeded, Is.True, exchangeResult.Validation?.Reason ?? exchangeResult.LogMessage);
                        var exchangeRequest = session.State.EffectRuntime.InteractionRequests.Last(q => q.Status == "open" &&
                            q.AnsweringPlayerId == 3 && q.Presentation?.Prompt?.Key == "core:entrepreneur.D.exchange.condition");
                        var answer = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(exchangeRequest, 3), 3, new[] { "continue" });
                        var exchanged = session.Submit(answer);
                        Assert.That(exchanged.Succeeded, Is.True, exchanged.Validation?.Reason ?? exchanged.LogMessage);
                        Assert.That(session.State.FindPlayer(3).Score, Is.EqualTo(score + count + 1));
                    }
                    state = session.State;
                }
                var options = registry.GetSettlementOptions(state, 3);
                var investment = options.Single(o => o.SourceDefinitionId == "entrepreneur.ability.D09");
                Assert.That(investment.IsEnabled, Is.True, investment.DisabledReasonCode);
                var bindingType = Type.GetType("YC.Presentation.SettlementEffectStripBinding, Assembly-CSharp", true);
                var binding = Activator.CreateInstance(bindingType);
                bindingType.GetMethod("SetScope").Invoke(binding, new object[] { state.GameId, 3 });
                bindingType.GetMethod("BindWindow").Invoke(binding, new object[] {
                    investment.WindowId, options.Where(o => o.WindowId == investment.WindowId).Select(SettlementOptionView.FromOption).ToArray() });
                var command = investment.CleanupRequest == null
                    ? EffectInteractionCommands.InvokeSettlementOption(3, investment.OptionId, investment.TargetUseId, investment.StateRevision)
                    : CleanupSourceCommand.Create(3, Guid.NewGuid().ToString("N"), investment.CleanupRequest, investment.StateRevision);
                if (string.IsNullOrEmpty(command.CommandId)) command.CommandId = Guid.NewGuid().ToString("N");
                var result = session.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason ?? result.LogMessage);
                var request = session.State.EffectRuntime.InteractionRequests.LastOrDefault(q => q.Status == "open" &&
                    q.AnsweringPlayerId == 3 && q.Presentation?.Prompt?.Key == "core:entrepreneur.D.investment.condition");
                Assert.That(request, Is.Not.Null, "提交收尾投资后应生成付款条件确认请求");
                Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                var view = session.ProjectFor(GameStateViewer.Player(3));
                Assert.That(view.Interactions.Any(i => i.InteractionId == request.GetStableInteractionId()), Is.True,
                    "本人投影应包含实际确认请求");
                var confirm = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 3), 3, new[] { "continue" });
                var confirmed = session.Submit(confirm);
                Assert.That(confirmed.Succeeded, Is.True, confirmed.Validation?.Reason ?? confirmed.LogMessage);
                TestContext.WriteLine("节点=" + session.State.EffectRuntime.EffectNodes.Count + "，事件=" +
                    session.State.EffectRuntime.RuleEvents.Count + "，故障=" + session.State.EffectRuntime.LastFaultCode);
                Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
                Assert.That(session.State.EffectRuntime.LastFaultCode, Is.Empty);
            }
            finally
            {
                Assert.That(IsolatedTestArtifacts.FileHash(path), Is.EqualTo(hash.ToLowerInvariant()));
                evidence.Finish();
            }
        }

        [Test]
        public void RecordedAwardThenFiveBTacticCanProceedPastTheOldNodeLimit()
        {
            string path = Environment.GetEnvironmentVariable("YC_DICE_REPRO_SAVE");
            string hash = Environment.GetEnvironmentVariable("YC_DICE_REPRO_SAVE_SHA256");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash))
                Assert.Ignore("仅在显式提供隔离存档副本及摘要时运行。");
            path = Path.GetFullPath(path);
            string root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.That(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(IsolatedTestArtifacts.FileHash(path), Is.EqualTo(hash.ToLowerInvariant()));
            var evidence = IsolatedTestArtifacts.BeginCase();
            try
            {
                var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(path))["Payload"]);
                string content = evidence.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "黄色玩家正式内容隔离副本");
                var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
                runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { content, Path.Combine(evidence.InputsDirectory, "独立文案登记") });
                var bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var restored = bootstrap.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
                var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
                var registry = (EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
                void Submit(GameCommand command)
                {
                    if (string.IsNullOrEmpty(command.CommandId)) command.CommandId = Guid.NewGuid().ToString("N");
                    var result = session.Submit(command);
                    Assert.That(result.Succeeded, Is.True, result.Validation?.Reason ?? result.LogMessage);
                    Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
                }
                void Invoke(YC.Domain.Cards.CharacterSettlementOption option, int player) => Submit(option.CleanupRequest == null
                    ? EffectInteractionCommands.InvokeSettlementOption(player, option.OptionId, option.TargetUseId, option.StateRevision)
                    : CleanupSourceCommand.Create(player, Guid.NewGuid().ToString("N"), option.CleanupRequest, option.StateRevision));
                void Answer(InteractionRequest request, string choice) => Submit(EffectInteractionCommands.Answer(
                    InteractionRequestProjector.ProjectForPlayer(request, request.AnsweringPlayerId), request.AnsweringPlayerId, new[] { choice }));
                InteractionRequest Open() => session.State.EffectRuntime.InteractionRequests.LastOrDefault(q =>
                    q.Status == "open" && q.AnsweringPlayerId == 2 && !q.IsInternalMainlineInteraction());
                Assert.That(new RoundExecutionService(registry).Advance(session.State).IsValid, Is.True);
                var end = session.ProjectFor(GameStateViewer.Player(3)).SettlementEndAction;
                Assert.That(end?.CanCloseCleanupWindow, Is.True);
                Submit(new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 3,
                    Parameters = new System.Collections.Generic.Dictionary<string, string> { ["windowId"] = end.WindowId,
                        ["currentNodeEffectId"] = end.CurrentNodeEffectId, ["interactionId"] = end.InteractionId,
                        ["expectedRevision"] = end.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
                Assert.That(session.State.CurrentPlayerId, Is.EqualTo(2));
                Invoke(registry.GetSettlementOptions(session.State, 2).Single(o => o.SourceDefinitionId == "infected.award.permanent_support"), 2);
                var resource = Open();
                Assert.That(resource.InteractionTypeId, Is.EqualTo("facility.entry.choice"));
                var allocation = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(resource, 2), 2, Array.Empty<string>());
                allocation.Parameters["answerValue"] = "Originium=1";
                Submit(allocation);
                var awardMove = Open();
                Assert.That(awardMove.InteractionTypeId, Is.EqualTo("character.ability.choice.move"));
                Answer(awardMove, awardMove.CandidateIds.First());
                var cleanup = registry.GetSettlementOptions(session.State, 2).Single(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order");
                Assert.That(cleanup.IsEnabled, Is.True, cleanup.DisabledReasonCode);
                Invoke(cleanup, 2);
                var gate = Open(); Assert.That(gate.CandidateIds, Does.Contain("continue")); Answer(gate, "continue");
                var tactic = registry.QueryActions(session.State, 2).Single(a => a.ActionId == "entrepreneur.A05.tactic.quick");
                var choose = Open(); Assert.That(choose.CandidateIds, Does.Contain(tactic.OptionId)); Answer(choose, tactic.OptionId);
                var branch = Open();
                Assert.That(branch.CandidateIds, Does.Contain("move"));
                Assert.That(branch.CandidateIds, Does.Contain("remove"));
                Answer(branch, "move");
                var move = Open();
                Assert.That(move.InteractionTypeId, Is.EqualTo("character.ability.choice.move"));
                Assert.That(move.CandidateIds, Is.Not.Empty);
                Answer(move, move.CandidateIds.First());
                Assert.That(session.State.EffectRuntime.EffectNodes.Count, Is.GreaterThan(2048));
                Assert.That(session.State.EffectRuntime.LastFaultCode, Is.Empty);
            }
            finally
            {
                Assert.That(IsolatedTestArtifacts.FileHash(path), Is.EqualTo(hash.ToLowerInvariant()));
                evidence.Finish();
            }
        }
    }
}
