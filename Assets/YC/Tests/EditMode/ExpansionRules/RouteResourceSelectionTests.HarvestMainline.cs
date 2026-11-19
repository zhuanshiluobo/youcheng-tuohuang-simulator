using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class RouteResourceSelectionTests
    {
        private static GameCommand HarvestAnswer(InteractionRequest request, IEnumerable<string> ids = null)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters["interactionId"] = request.GetStableInteractionId();
            command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept" || request.AnswerSchema == "boolean") command.Parameters["answerValue"] = request.AllowDecline ? "false" : "true";
            else command.OptionIds.AddRange(ids ?? request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            return command;
        }
        private void RunFormalHarvestBothOrders()
        {
            var evidence = exploreArtifacts;
                foreach (var order in new[] { new[] { 2, 1, 3 }, new[] { 1, 3, 2 } })
                {
                    var pack = ExternalContentPack.Current;
                    var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3,
                        OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion, EnabledExpansionIds = new List<string> { "core" } };
                    var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
                    var recipe = GameStateCloneService.DeepClone(catalog.SetupRecipe);
                    foreach (string location in StaticMapDefinitions.FourPlayerInitialLocationIds)
                        recipe.Add("api06.harvest." + location, "api06.harvest." + location, "core.resource_point_token", "location/" + location);
                    var registry = new EffectRegistry(); ExpansionSetupFlow.Register(registry); catalog.Register(registry);
                    registry.SetupRecipe = GameStateCloneService.DeepClone(recipe); registry.ContentDefinitions = GameStateCloneService.DeepClone(catalog.ContentDefinitions);
                    var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList(), config, 321, recipe, catalog.ContentDefinitions, new[] { 1, 2, 3 });
                    // 隔离初态保留原测试20金券/2异铁的业务边界；首次开局前准备，不计正常游戏验收。
                    var initialGold = PlayerEntranceEffectExecutor.CreateInitialGoldEffects(new[] { 1, 2, 3 });
                    foreach (var player in state.Players)
                    {
                        int entranceGrant = ItemTransferEffectExecutor.Number(initialGold.Single(s => s.PlayerId == player.PlayerId).NormalizedArguments, "amount");
                        player.Resources.GoldVoucher = 20 - entranceGrant; player.Resources.Iron = 0;
                    }
                    foreach (var token in state.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token"))
                        token.LocalState = ItemTransferEffectExecutor.Object("resourceType", NormalizedValue.CreateInteger((int)ResourceType.Iron), "amount", NormalizedValue.CreateInteger(2));
                    var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
                    ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); InfluenceEffectExecutor.Register(registry, influence);
                    EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(), new ResourceTokenService());
                    PlayerEntranceLuaCatalog.Register(registry); PlayerEntranceEffectExecutor.Register(registry, map); RoundStartedRedZoneRule.Register(registry, map);
                    var round = new RoundExecutionService(registry); var session = new GameSession(state);
                    session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
                    evidence.WriteLabeledText("采集正式工厂隔离初态_" + string.Join("", order), JsonConvert.SerializeObject(new { state, recipe, initialGold, pack.ContentHash, seed = 321, scope = "规则隔离；初态减去实际正式入场金券发放，使采集预算为20；非菜单/建房/正常对局/UI验收" }));
                    var started = round.StartEntrance(state); Assert.That(started.IsValid, Is.True, started.Reason);
                    for (int guard = 0; state.Phase != GamePhase.ActionRound1 && guard < 100; guard++)
                    {
                        var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" && !r.IsInternalMainlineCompletion());
                        Assert.That(request, Is.Not.Null, "正式开局/盖放必须有合法必要输入");
                        var accepted = session.Submit(HarvestAnswer(request)); Assert.That(accepted.Succeeded, Is.True, accepted.Validation?.Reason); state = session.State;
                    }
                    Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
                    for (int guard = 0; state.Phase != GamePhase.ResourceCollection && guard < 12; guard++)
                    {
                        var complete = round.CompleteMainAction(state, state.CurrentPlayerId); Assert.That(complete.IsValid, Is.True, complete.Reason);
                    }
                    Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
                    Assert.That(state.Players.All(p => p.ResourceCollectionStartGoldVoucher == 20), Is.True);
                    var main = state.EffectRuntime.MainNodes.Single(m => m.NodeId == state.EffectRuntime.ActiveMainNodeId);
                    Assert.That(main.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.Collection));
                    var root = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == main.ExecutionEffectId);
                    Assert.That(root.ChildSchedulingGroups.Single(g => g.StableGroupKey == "collection_players").ChildEffectIds.Count, Is.EqualTo(3));
                    string collectionRootId = root.EffectId;
                    var requests = state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection").ToList();
                    Assert.That(requests.Select(r => r.AnsweringPlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
                    var commands = new List<object>();
                    foreach (int player in order)
                    {
                        var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection" && r.AnsweringPlayerId == player);
                        string ownLocation = state.FindPlayer(player).CityLocationId;
                        var command = HarvestAnswer(request, SelectionAnswer(request, player == 2 ? ownLocation : ""));
                        Assert.That(InteractionRequestValidity.TryReadRequestAuthority(state, command, out var authority, out var validation), Is.True, validation.Reason);
                        Assert.That(authority.Profile, Is.EqualTo(InteractionRequestValidity.NormalHarvest));
                        var submitted = session.Submit(command); Assert.That(submitted.Succeeded, Is.True, submitted.Validation?.Reason); state = session.State;
                        evidence.WriteLabeledText("采集逐次提交真实现场_" + string.Join("", order) + "_" + player, JsonConvert.SerializeObject(new { command, authority, state }));
                        var actualGroup = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == collectionRootId).ChildSchedulingGroups.Single(g => g.StableGroupKey == "collection_players");
                        var actualTask = state.EffectRuntime.EffectNodes.Single(n => actualGroup.ChildEffectIds.Contains(n.EffectId) && n.PlayerId == player);
                        Assert.That(actualTask.Status, Is.EqualTo(EffectNodeStatus.Completed), "玩家="+player+"；"+actualTask.FlowStage+":"+actualTask.FailureReason);
                        if (state.Phase == GamePhase.ResourceCollection) Assert.That(state.FindPlayer(player).HasCollectedResourcesThisRound, Is.True);
                        string once = JsonConvert.SerializeObject(state);
                        Assert.That(session.Submit(command).Succeeded, Is.False, "普通同步入口对已关闭请求拒绝重发，不借用网络排序器的去重回执"); Assert.That(JsonConvert.SerializeObject(session.State), Is.EqualTo(once));
                        commands.Add(new { command, authority, submitted.Succeeded, state.EffectRuntime.StateRevision });
                    }
                    Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(2));
                    Assert.That(state.Players.All(p => p.Resources.GoldVoucher == 20), Is.True);
                    Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == "route_resource.selection"), Is.False);
                    var recovered = evidence.Recover(state, "采集完成真实恢复_" + string.Join("", order), registry); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                    Assert.That(recovered.State.FindPlayer(2).Resources.Iron, Is.EqualTo(2)); Assert.That(recovered.State.Players.All(p => p.Resources.GoldVoucher == 20), Is.True);
                    evidence.WriteLabeledText("两种采集提交顺序实际结果_" + string.Join("", order), JsonConvert.SerializeObject(new { order, commands, state }));
                }
        }
    }
}
