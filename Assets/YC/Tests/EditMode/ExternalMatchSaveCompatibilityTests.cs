using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    public sealed class ExternalMatchSaveCompatibilityTests
    {
        [Test, Explicit("通过 YC_MATCH_SAVE_INPUT 指定待检查存档；只读复制后执行隔离恢复，不计真实联机验收。")]
        public void SavedHostAndColdClientsRestoreTheSameStateWithoutChangingOriginal()
        {
            string input = Environment.GetEnvironmentVariable("YC_MATCH_SAVE_INPUT");
            Assert.That(File.Exists(input), Is.True, "未指定可读的存档文件。");
            string originalHash = IsolatedTestArtifacts.FileHash(input);
            var evidence = IsolatedTestArtifacts.BeginCase();
            try
            {
                var directory = Path.Combine(evidence.InputsDirectory, "只读存档副本");
                Directory.CreateDirectory(directory);
                File.Copy(input, Path.Combine(directory, "auto-1.json"), false);
                var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
                var store = Activator.CreateInstance(storeType, new object[] { directory });
                var save = (MatchSaveData)storeType.GetMethod("Read").Invoke(store, new object[] { 3 });
                var bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var built = bootstrap.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
                var host = (GameSession)built.GetType().GetField("Session").GetValue(built);
                var registry = (EffectRegistry)built.GetType().GetField("EffectRegistry").GetValue(built);
                var state = host.State;
                Assert.That(state.GameId, Is.EqualTo(save.GameId));
                Assert.That(state.Round, Is.EqualTo(save.Round));
                Assert.That(state.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
                string digest = RuleStateDigestService.Compute(state);
                int clients = 0;
                if (!string.IsNullOrEmpty(save.SimulationCheckpointJson))
                {
                    var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(save.SimulationCheckpointJson);
                    foreach (var seat in save.Seats.Where(seat => seat.PlayerId != save.LocalPlayerId))
                    {
                        var initial = GameLaunchStateFactory.CreateInitialState(LaunchMode.Client, seat.PlayerId,
                            save.Seats.Select(value => value.ToSeat()).ToList(), state.RuleConfiguration,
                            state.EventDeckSeed, state.SetupRecipe, state.ContentDefinitions, state.LaunchPlayerOrder,
                            state.GameId, state.RandomRuntime);
                        var client = new GameSession(initial);
                        var installer = new SavedSimulationCheckpointInstaller(client, registry, GameStateViewer.Player(seat.PlayerId),
                            MatchSaveData.OptionsFor(baseline, false, seat.PlayerId), baseline.CheckpointId, baseline.MetadataDigest);
                        Assert.That(installer.TryInstall(baseline.CheckpointId, save.SimulationCheckpointJson, out var result), Is.True, result.Diagnostic);
                        installer.Coordinator.CompleteConfirmedRecovery(true);
                        Assert.That(RuleStateDigestService.Compute(client.State), Is.EqualTo(digest), "客户端恢复状态必须与房主一致。");
                        clients++;
                    }
                }
                string resultText = $"原档哈希={originalHash}\nGameId={state.GameId}\n回合={state.Round}\n行动轮={state.ActionRound}\n当前玩家={state.CurrentPlayerId}\n内容哈希={state.ContentPackHash}\n客户端恢复数={clients}\n恢复状态摘要={digest}\n范围=只读副本的规则恢复与检查点安装，不计真实 Steam 房间验收。";
                Console.WriteLine(resultText);
                evidence.WriteLabeledText("实际存档兼容性结果", resultText);
            }
            finally
            {
                Assert.That(IsolatedTestArtifacts.FileHash(input), Is.EqualTo(originalHash), "原存档不得被检查过程改写。");
                evidence.Finish();
            }
        }
    }
}
