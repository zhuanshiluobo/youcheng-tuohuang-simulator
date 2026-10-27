using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只在私有输入目录正式恢复原3031副本；不是正常Player或原槽续局，不改输入状态/资源。
    public sealed class DeployTypedSupplyConsistencyR6Tests
    {
        private const string ExpectedHash = "8cf5167287f208682673cbb5cd09699ba1e0e22e4dc7c30b931e47d92e9cd8d4";
        [Test]
        public void Original3031DeploymentRejectsDeclaredTypedShortageWithoutConsumingOrdinarySupply()
        {
            var evidence = IsolatedTestArtifacts.BeginCase();
            try
            {
                string input = Path.Combine(Directory.GetCurrentDirectory(), "prompt/企业家内容实施规划-20261019/实施记录/QY-002-DEPLOY-TYPED-SUPPLY-CONSISTENCY-FIX-R6/原3031正常档副本.json");
                Assert.That(IsolatedTestArtifacts.FileHash(input), Is.EqualTo(ExpectedHash));
                string directory = Path.Combine(evidence.InputsDirectory, "原3031独立恢复输入"); Directory.CreateDirectory(directory);
                string copy = Path.Combine(directory, "auto-1.json"); File.Copy(input, copy, false);
                var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
                var store = Activator.CreateInstance(storeType, new object[] { directory });
                var data = (MatchSaveData)storeType.GetMethod("Read").Invoke(store, new object[] { 3 });
                var bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var built = bootstrap.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, data });
                var session = (GameSession)built.GetType().GetField("Session").GetValue(built);
                var influence = (InfluenceService)built.GetType().GetField("InfluenceService").GetValue(built);
                var registry = (EffectRegistry)built.GetType().GetField("EffectRegistry").GetValue(built);
                var state = session.State;
                Assert.That(state.GameId, Is.EqualTo("8cea6a46d03242b7892f6d1013bfee03"));
                Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(3031));
                Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
                const string target = "location:A-01:1";
                int player = state.CurrentPlayerId;
                var supply = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == player);
                var beforeTokens = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.ZoneId == supply.ZoneId &&
                    i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == player).Select(GameStateCloneService.DeepClone).ToList();
                var clone = GameStateCloneService.DeepClone(state);
                var legal = influence.GetLegalPlacementSlotIds(state, player);
                var validation = influence.CanPlace(state, player, target);
                var clonedValidation = influence.CanPlace(clone, player, target);
                var types = ItemTypeRegistry.CreateBuiltIn();
                var client = GameStateViewProjector.ToClientState(GameStateViewProjector.ProjectForPlayer(state, player));
                var gang = state.ItemZones.Items.Where(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == player).ToList();
                var gangInSupply = gang.Where(i => i.ZoneId == supply.ZoneId).ToList();
                var typedLegal = influence.GetLegalPlacementSlotIds(state, ItemSubjectRef.Player(player), "entrepreneur.gang_marker");
                evidence.WriteLabeledText("3031恢复后候选与命令前typed事实", JsonConvert.SerializeObject(new { state.GameId, state.ContentPackHash,
                    revision = state.EffectRuntime.StateRevision, player, target, supply, beforeTokens, state.EntitySchemaVersion,
                    typed = ItemZoneLegacyProjection.IsActive(state), legacy = state.FindPlayer(player).LegacyInfluenceSupply,
                    readSupply = state.FindPlayer(player).InfluenceSupply, cloneReadSupply = clone.FindPlayer(player).InfluenceSupply,
                    legal, validation, clonedValidation, ordinaryProfile = types.HasOperationProfile("core.player_marker", "core.influence_cube"),
                    gang, gangInSupply, typedLegal, clientSupply = client.FindPlayer(player).InfluenceSupply,
                    clientBasicLegal = influence.GetLegalPlacementSlotIds(client, player),
                    clientGangLegal = influence.GetLegalPlacementSlotIds(client, ItemSubjectRef.Player(player), "entrepreneur.gang_marker") }));
                Assert.That(beforeTokens.Count, Is.EqualTo(22));
                Assert.That(legal, Does.Contain(target)); Assert.That(validation.IsValid, Is.True, validation.Reason);
                Assert.That(clonedValidation.IsValid, Is.True, clonedValidation.Reason);
                Assert.That(gang.Count, Is.EqualTo(4)); Assert.That(gangInSupply, Is.Empty); Assert.That(typedLegal, Is.Empty);
                Assert.That(client.FindPlayer(player).InfluenceSupply, Is.EqualTo(22));
                Assert.That(influence.GetLegalPlacementSlotIds(client, player), Does.Contain(target));
                Assert.That(influence.GetLegalPlacementSlotIds(client, ItemSubjectRef.Player(player), "entrepreneur.gang_marker"), Is.Empty);
                string digest = RuleStateDigestService.Compute(state);
                foreach (var invalid in new[] {
                    new GameCommand { PlayerId = player - 1, TargetId = target, Kind = GameCommandKind.DeployInfluence },
                    new GameCommand { PlayerId = player, TargetId = gang.First().SlotId, Kind = GameCommandKind.DeployInfluence },
                    new GameCommand { PlayerId = player, TargetId = "location:not-a-map-location:0", Kind = GameCommandKind.DeployInfluence } })
                {
                    var rejected = session.Submit(invalid);
                    evidence.WriteLabeledText("3031原会话非法主体或目标拒绝", JsonConvert.SerializeObject(new { invalid, rejected }));
                    Assert.That(rejected.Succeeded, Is.False); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
                }
                var command = new GameCommand { CommandId = "qy002-r6-" + Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence,
                    PlayerId = player, TargetId = target };
                var handlers = (Dictionary<string, Func<GameState, AbilitySourceRef, NormalizedValue, InfluencePlaceRewrite>>)typeof(EffectRegistry)
                    .GetField("influencePlaceHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(registry);
                var originals = handlers.ToDictionary(pair => pair.Key, pair => pair.Value);
                var rewrites = new List<InfluencePlaceRewrite>();
                foreach (var pair in originals)
                    handlers[pair.Key] = (current, source, facts) => {
                        var rewrite = pair.Value(current, source, facts); // 观测生产Lua原返回值，原样交回，不改来源、参数、状态或效果。
                        rewrites.Add(rewrite);
                        evidence.WriteLabeledText("3031正式来源改写实际返回", JsonConvert.SerializeObject(new { source, facts, rewrite }));
                        return rewrite;
                    };
                YC.Domain.Commands.CommandResult result;
                try { result = session.Submit(command); }
                finally { foreach (var pair in originals) handlers[pair.Key] = pair.Value; }
                var moved = state.ItemZones.Items.Where(i => beforeTokens.Any(before => before.ItemId == i.ItemId) && i.ZoneId != supply.ZoneId).ToList();
                evidence.WriteLabeledText("3031原部署命令实际结果", JsonConvert.SerializeObject(new { command, result, moved,
                    revision = state.EffectRuntime.StateRevision, state.EffectRuntime.Status, state.EffectRuntime.LastFaultMessage,
                    ordinarySupplyAfter = state.FindPlayer(player).InfluenceSupply, inputUnchanged = IsolatedTestArtifacts.FileHash(copy) == ExpectedHash }));
                var actual = rewrites.Single(r => r.Operation == "replace");
                Assert.That(actual.Effect.EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
                Assert.That(ItemTransferEffectExecutor.Text(actual.Effect.NormalizedArguments, "itemTypeId"), Is.EqualTo("entrepreneur.gang_marker"));
                Assert.That(result.Succeeded, Is.False); Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientInfluence));
                Assert.That(result.Validation.Reason, Is.EqualTo("insufficientsupply"));
                Assert.That(moved, Is.Empty); Assert.That(state.FindPlayer(player).InfluenceSupply, Is.EqualTo(beforeTokens.Count));
                Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
                Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(3031));
                Assert.That(IsolatedTestArtifacts.FileHash(copy), Is.EqualTo(ExpectedHash));
                Assert.That(IsolatedTestArtifacts.FileHash(input), Is.EqualTo(ExpectedHash));
            }
            finally { evidence.Finish(); }
        }
    }
}
