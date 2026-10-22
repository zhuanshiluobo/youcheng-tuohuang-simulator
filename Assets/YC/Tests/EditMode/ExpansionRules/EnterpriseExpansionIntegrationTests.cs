using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class EnterpriseExpansionIntegrationTests
    {
        private static readonly string Root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
        private static string EvidenceDirectory => Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ENTERPRISE_INTEGRATION_EVIDENCE") ??
            "Logs/ExpansionValidation/EnterpriseExpansionIntegration");
        private static ExternalContentPack installed;
        private static ExternalContentPack Installed => installed ?? (installed = ExternalContentPack.Load(Root));
        private static GameRuleConfiguration Configuration(int players, bool enterprise) => new GameRuleConfiguration
        {
            PlayerCount = players, MapId = players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId,
            EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" }
        };
        private static Type Presentation(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void FormalDirectoryHasCompleteEnterpriseContentAndUniqueReplacementSlots()
        {
            var catalog = ActiveContentCatalogBuilder.Build(Installed, Configuration(3, true));
            var definitions = catalog.Definitions;
            Assert.That(definitions.Count(d => d.ExpansionId == "enterprise"), Is.EqualTo(33));
            Assert.That(definitions.Select(d => d.RuntimeId).Distinct().Count(), Is.EqualTo(definitions.Count));
            Assert.That(definitions.Count(d => d.ContentType == "enterprise"), Is.EqualTo(8));
            Assert.That(definitions.Count(d => d.ContentType == "department"), Is.EqualTo(3));
            Assert.That(definitions.Count(d => d.ContentType == "character"), Is.EqualTo(15));
            Assert.That(definitions.Count(d => d.ContentType == "facility" && d.ExpansionId == "enterprise"), Is.EqualTo(3));
            Assert.That(definitions.Single(d => d.RuntimeId == "event_red_03").DefinitionId, Is.EqualTo("enterprise.event.collection_platform"));
            Assert.That(definitions.Single(d => d.RuntimeId == "event_yellow_02").DefinitionId, Is.EqualTo("enterprise.event.cave_ruins"));
            Assert.That(definitions.Any(d => d.DefinitionId == "facility_enterprise_office"), Is.False);
            foreach (var definition in definitions.Where(d => d.ExpansionId == "enterprise"))
            {
                Assert.That(definition.ArtworkSlice, Is.Not.Null, definition.DefinitionId);
                Assert.That(Installed.GetArtworkBytes(definition.Artwork).Length, Is.GreaterThan(100));
                foreach (var ability in definition.Abilities)
                {
                    Assert.That(ability.Version, Is.Not.Empty);
                    Assert.That(catalog.Scripts[ability.Script], Does.Match(@"\b(Effect|Flow)\s*\.\s*\w+\s*\("), ability.AbilityId);
                }
            }
            var recipe = catalog.SetupRecipe;
            Assert.That(recipe.InitialBasicCharacterCount, Is.EqualTo(7));
            Assert.That(recipe.BasicCharacterKeepCount, Is.EqualTo(5));
            Assert.That(recipe.Entries.Where(e => e.InitialZoneRole == "operator_deck").Sum(e => e.Quantity), Is.EqualTo(8));
            Assert.That(definitions.Single(d => d.DefinitionId == "enterprise.component.road").Data["quantityPolicy"].ToString(), Is.EqualTo("infinite"));
            Assert.That(Installed.FindSharedArtwork("enterprise_back"), Is.Not.Null);
            Assert.That(Installed.FindSharedArtwork("operator_back"), Is.Not.Null);
            var output = EvidenceDirectory;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "formal-content-catalog.json"), JsonConvert.SerializeObject(definitions.Where(d => d.ExpansionId == "enterprise").Select(d => new
            {
                d.DefinitionId, d.RuntimeId, d.ContentType, d.Artwork, d.ArtworkSpriteId,
                abilities = d.Abilities.Select(a => new { a.AbilityId, a.Version, a.Script }),
                recipes = recipe.Entries.Where(e => e.DefinitionId == d.DefinitionId), catalog.ContentPackHash
            }), Formatting.Indented));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ReturningToCoreClearsEnterpriseContentAndSubscriptions(int players)
        {
            var enterprise = ActiveContentCatalogBuilder.Build(Installed, Configuration(players, true));
            var core = ActiveContentCatalogBuilder.Build(Installed, Configuration(players, false));
            var registry = new EffectRegistry(); core.Register(registry);
            Assert.That(core.Definitions.All(d => d.ExpansionId == "core"), Is.True);
            Assert.That(core.Scripts.Keys.Any(p => p.Contains("enterprises/") || p.Contains("operator.")), Is.False);
            Assert.That(core.SetupRecipe.Entries.Where(e => e.InitialZoneRole == "hand").Sum(e => e.Quantity), Is.EqualTo(5));
            Assert.That(core.ContentPackHash, Is.Not.EqualTo(enterprise.ContentPackHash));
            Assert.That(Installed.ActiveDefinitions.All(d => d.ExpansionId == "core"), Is.True);
            var again = ActiveContentCatalogBuilder.Build(Installed, Configuration(players, true));
            Assert.That(again.ContentPackHash, Is.EqualTo(enterprise.ContentPackHash));
            // 同一真实启动上下文先企业后基础，验证新会话不继承前一局目录、实例或订阅。
            var contextObject = new GameObject("组合切换验证上下文");
            try
            {
                var context = contextObject.AddComponent(Presentation("GameLaunchContext"));
                var seats = Enumerable.Range(1, players).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList();
                GameSession previous = null;
                foreach (bool enabled in new[] { true, false })
                {
                    var configuration = Configuration(players, enabled);
                    configuration.ContentPackHash = enabled ? enterprise.ContentPackHash : core.ContentPackHash;
                    Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                        .Invoke(context, new object[] { LaunchMode.Local, 1, "", seats, configuration });
                    var result = Presentation("GameSessionBootstrapper").GetMethod("Build").Invoke(null, new object[] { context, false, false });
                    var current = (GameSession)result.GetType().GetField("Session").GetValue(result);
                    if (!enabled)
                    {
                        Assert.That(current, Is.Not.SameAs(previous));
                        Assert.That(current.State.EffectRuntime, Is.Not.SameAs(previous.State.EffectRuntime));
                        Assert.That(current.State.RuleConfiguration.EnabledExpansionIds, Is.EqualTo(new[] { "core" }));
                        var enterpriseIds = new HashSet<string>(Installed.Definitions.Where(d => d.ExpansionId == "enterprise").Select(d => d.DefinitionId));
                        Assert.That(current.State.ItemZones.Items.Any(i => enterpriseIds.Contains(i.DefinitionId)), Is.False);
                        Assert.That(current.State.ContentPackHash, Is.EqualTo(core.ContentPackHash));
                        Assert.That(current.State.SetupRecipe.Entries.Where(e => e.InitialZoneRole == "hand").Sum(e => e.Quantity), Is.EqualTo(5));
                    }
                    previous = current;
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(contextObject); }
        }

        [Test]
        public void UnfinishedCombinationsRemainExplicitlyUnavailable()
        {
            var configuration = Configuration(3, true); configuration.EnabledExpansionIds.Add("infected");
            Assert.That(() => ActiveContentCatalogBuilder.Build(Installed, configuration), Throws.InvalidOperationException.With.Message.EqualTo("content_not_ready"));
            configuration = Configuration(3, true); configuration.RuleModeId = "entrepreneur";
            Assert.That(() => ActiveContentCatalogBuilder.Build(Installed, configuration), Throws.InvalidOperationException.With.Message.EqualTo("rule_mode_not_implemented"));
        }

        [TestCase(2, false, TestName = "CoreTwoPlayerEightRounds")]
        [TestCase(3, false, TestName = "CoreThreePlayerEightRounds")]
        [TestCase(4, false, TestName = "CoreFourPlayerEightRounds")]
        [TestCase(2, true, TestName = "EnterpriseTwoPlayerEightRounds")]
        [TestCase(3, true, TestName = "EnterpriseThreePlayerEightRounds")]
        [TestCase(4, true, TestName = "EnterpriseFourPlayerEightRounds")]
        public void FormalBootstrapCommandsReachEightRoundsWithPrivateRecovery(int players, bool enterprise)
        {
            var configuration = Configuration(players, enterprise);
            configuration.ContentPackHash = ActiveContentCatalogBuilder.Build(Installed, configuration).ContentPackHash;
            var contextObject = new GameObject("企业整合会话上下文");
            try
            {
                var context = contextObject.AddComponent(Presentation("GameLaunchContext"));
                var seats = Enumerable.Range(1, players).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p, Color = (PlayerColor)(p - 1) }).ToList();
                Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                    .Invoke(context, new object[] { LaunchMode.Local, 1, "enterprise-integration", seats, configuration });
                var result = Presentation("GameSessionBootstrapper").GetMethod("Build").Invoke(null, new object[] { context, false, false });
                var session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                var handlers = (List<IGameCommandHandler>)typeof(GameSession).GetField("commandHandlers", Fields).GetValue(session);
                var answerHandler = handlers.OfType<AnswerInteractionCommandHandler>().Single();
                var registry = (EffectRegistry)typeof(AnswerInteractionCommandHandler).GetField("registry", Fields).GetValue(answerHandler);
                var state = session.State;
                var rounds = new HashSet<int>(); var phases = new HashSet<GamePhase>(); var requests = new HashSet<string>();
                var progressDirectory = EvidenceDirectory;
                Directory.CreateDirectory(progressDirectory);
                string progressPath = Path.Combine(progressDirectory, (enterprise ? "enterprise" : "core") + "-" + players + "-progress.json");
                int commands = 0, recoveries = 0, privateChecks = 0;
                bool checkedSetup = false;
                while (state.Phase != GamePhase.FinalScoring && state.Phase != GamePhase.GameOver && commands < 750)
                {
                    rounds.Add(state.Round); phases.Add(state.Phase);
                    Assert.That(state.EffectRuntime.LastFaultMessage, Is.Empty);
                    if (!checkedSetup && state.Phase == GamePhase.CharacterCover)
                    {
                        checkedSetup = true;
                        Assert.That(state.Players.All(p => CharacterLifecycleService.GetRetainedBasicCharacterCount(state, p.PlayerId) == 5), Is.True);
                        Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "secret_retired")), Is.EqualTo(enterprise ? players * 2 : 0));
                        Assert.That(state.ItemZones.Items.Count(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "enterprises")), Is.EqualTo(enterprise ? 4 : 0));
                        var secretIds = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "secret_retired")).Select(i => i.ItemId).ToArray();
                        foreach (var seat in seats)
                        {
                            string visible = JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(state, seat.PlayerId));
                            foreach (string secretId in secretIds) Assert.That(visible, Does.Not.Contain(secretId), "开局秘密回盒身份不能在原持有者投影中重新出现。");
                        }
                    }
                    var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());
                    GameCommand command;
                    if (request != null && request.InteractionTypeId != "collection.task")
                    {
                        requests.Add(request.InteractionTypeId);
                        if (request.Visibility != GameStateVisibilityPolicy.Public)
                        {
                            var ownerView = GameStateViewProjector.ProjectForPlayer(state, request.AnsweringPlayerId);
                            Assert.That(ownerView.Interactions.SingleOrDefault(r => r.InteractionId == request.GetStableInteractionId())?.CandidateIds,
                                Is.EqualTo(request.CandidateIds), "私密候选必须完整送达有权回答的玩家。");
                            foreach (int other in seats.Select(s => s.PlayerId).Where(p => p != request.AnsweringPlayerId))
                                Assert.That(GameStateViewProjector.ProjectForPlayer(state, other).Interactions.Any(r => r.InteractionId == request.GetStableInteractionId()), Is.False);
                            privateChecks++;
                        }
                        if (state.Round <= 1 && recoveries < 2)
                        {
                            string id = request.GetStableInteractionId(); var candidates = request.CandidateIds.ToArray();
                            var recovered = session.RecoverHost(session.CreateHostArchive(), registry);
                            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id).CandidateIds, Is.EqualTo(candidates));
                            recoveries++;
                            request = state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id);
                        }
                        command = Answer(request);
                    }
                    else if (state.Phase == GamePhase.ResourceCollection)
                    {
                        var player = state.Players.First(p => !p.HasCollectedResourcesThisRound);
                        command = new GameCommand { Kind = GameCommandKind.CollectResource, PlayerId = player.PlayerId };
                        if (!string.IsNullOrEmpty(player.CityLocationId)) command.OptionIds.Add(player.CityLocationId);
                    }
                    else if ((state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) && !state.FindPlayer(state.CurrentPlayerId).ActedMainActionThisTurn)
                        command = MainAction(state, state.CurrentPlayerId);
                    else command = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = state.CurrentPlayerId };
                    command.CommandId = Guid.NewGuid().ToString("N");
                    File.WriteAllText(progressPath, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, players, enterprise, commands, state.Round, state.Phase, request = request?.InteractionTypeId, command.Kind, nodes = state.EffectRuntime.EffectNodes.Count }));
                    var submitted = session.Submit(command);
                    Assert.That(submitted.Succeeded, Is.True, "round=" + state.Round + " phase=" + state.Phase + " request=" + request?.InteractionTypeId + " command=" + command.Kind + " " + submitted.Validation?.Reason + " " + state.EffectRuntime.LastFaultMessage);
                    if (request != null && state.Round <= 1 && recoveries <= 2)
                    {
                        string beforeRepeat = JsonConvert.SerializeObject(state.ItemZones);
                        var repeated = session.Submit(command);
                        Assert.That(repeated.Succeeded, Is.False, "已关闭的交互不能再次提交。");
                        Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(beforeRepeat));
                    }
                    commands++;
                    Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
                }
                Assert.That(checkedSetup, Is.True);
                Assert.That(state.Round, Is.EqualTo(8));
                Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
                Assert.That(state.FinalScoring, Is.Not.Null);
                Assert.That(state.FinalScoring.IsResolved, Is.True, "终局阶段必须已经实际完成计分。");
                Assert.That(rounds.Where(r => r > 0), Is.EquivalentTo(Enumerable.Range(1, 8)));
                Assert.That(phases, Does.Contain(GamePhase.ActionRound1)); Assert.That(phases, Does.Contain(GamePhase.ActionRound2));
                Assert.That(phases, Does.Contain(GamePhase.ResourceCollection));
                Assert.That(state.Logs.Any(l => l.Message.Contains("incomplete_effect_skipped")), Is.False);
                var output = EvidenceDirectory; Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, (enterprise ? "enterprise" : "core") + "-" + players + "-players-bootstrap.json"), JsonConvert.SerializeObject(new { players, enterprise, state.Round, state.Phase, state.FinalScoring, commands, recoveries, privateChecks, requests, configuration, state.ContentPackHash }, Formatting.Indented));
            }
            finally { UnityEngine.Object.DestroyImmediate(contextObject); }
        }

        private static GameCommand MainAction(GameState state, int player)
        {
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            foreach (var item in state.Map.Influences.Where(i => i.PlayerId == player))
            {
                string id = InfluenceIdentity.GetStableId(state, item);
                foreach (string target in influence.GetLegalMovementSlotIds(state, id))
                    if (influence.CanMoveAtomically(state, player, new[] { new InfluenceMoveRequest(id, target) }).IsValid)
                        return new GameCommand { Kind = GameCommandKind.DispatchInfluence, PlayerId = player, SourceId = id, TargetId = target };
            }
            return new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = player };
        }

        private static GameCommand Answer(InteractionRequest request)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
            var total = request.CandidateIds.FirstOrDefault(id => id.StartsWith("total:", StringComparison.Ordinal));
            if (request.AnswerSchema == "resource_allocation" && total != null)
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "Originium=" + total.Substring(6);
            else if (request.CandidateIds.Count > 0)
            {
                var candidates = request.CandidateIds;
                string skip = candidates.FirstOrDefault(id => id == "choice.skip" || id == "decline");
                if (skip != null && request.MinSelections <= 1) command.OptionIds.Add(skip);
                else command.OptionIds.AddRange(candidates.Take(Math.Max(1, request.MinSelections)));
            }
            else command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "True";
            return command;
        }
    }
}
