using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Cards;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅隔离正文/Host测试。正式模式G0不解除；使用当前真实Lua/声明，未注册C#卡面效果。
    // 本夹具待五位停止、共享清单由007整合后统一运行。
    internal sealed class PaulContentTestFixture : IDisposable
    {
        internal readonly IsolatedTestArtifacts Artifacts;
        internal readonly ExternalContentPack Pack;
        internal readonly string BaselineInputRoot, InputRoot;
        internal readonly ActiveContentCatalog Catalog;
        internal readonly EffectRegistry Registry;
        internal readonly GameMapDefinition Map;
        internal readonly RoundExecutionService Rounds;
        internal GameState State;
        private bool firstFault;
        internal EffectTreeExecutor Engine => Rounds.CreateCommandExecutor(State);
        internal IReadOnlyList<InteractionRequest> Open => State.EffectRuntime.InteractionRequests.Where(x => x.Status == "open").ToList();
        internal string AbilityItem => State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId;
        internal PaulContentTestFixture(string mapId = "map-three-players", string primaryEntrepreneur = "entrepreneur.ability.P09",
            PlayerStageSchedulingMode schedulingMode = PlayerStageSchedulingMode.Sequential, Func<string, IDictionary<string, string>> sourceOverrides = null,bool setupOnly=false,string secondaryEntrepreneur="entrepreneur.ability.D09")
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            var installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            BaselineInputRoot = Artifacts.CopyContent(installed, "保罗未修改完整私有基线");
            // 首次语义读取发生在封存后；后续清单、定义和补丁全部来自此基线。
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(BaselineInputRoot, "pack.json")));
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var replacements = new Dictionary<string, string>();
            var recipeFiles = manifest["setupRecipes"].Values<string>().Select(relative => new {
                Path = relative, Data = JObject.Parse(File.ReadAllText(Path.Combine(BaselineInputRoot, relative))) }).ToList();
            var standardRecipe = recipeFiles.Single(r => (string)r.Data["recipeId"] == "standard");
            var entrepreneurRecipe = recipeFiles.Single(r => (string)r.Data["recipeId"] == "entrepreneur");
            foreach (var entry in entrepreneurRecipe.Data["entries"]) ((JArray)standardRecipe.Data["entries"]).Add(entry.DeepClone());
            replacements[standardRecipe.Path] = standardRecipe.Data.ToString();
            if (sourceOverrides != null)
                foreach (var replacement in sourceOverrides(BaselineInputRoot))
                {
                    Assert.That((replacement.Key.StartsWith("lua/", StringComparison.Ordinal) || replacement.Key.StartsWith("entrepreneur/", StringComparison.Ordinal)) && File.Exists(Path.Combine(BaselineInputRoot, replacement.Key)), Is.True);
                    replacements[replacement.Key] = replacement.Value;
                }
            // 只在私有输入把正式企业家定义装入standard隔离catalog；卡效果、权限、Lua及身份不替换。
            // 不触碰全局能力表/G0；不把本夹具计作正常开局。
            foreach (string relative in manifest["definitions"].Values<string>())
            {
                var j = JObject.Parse(replacements.TryGetValue(relative,out var explicitOverride) ? explicitOverride : File.ReadAllText(Path.Combine(BaselineInputRoot, relative)));
                if (j["applicableModes"] is JArray modes && modes.Values<string>().Contains("entrepreneur"))
                { if (!modes.Values<string>().Contains("standard")) modes.Add("standard"); replacements[relative] = j.ToString(); }
            }
            var handlers = (JArray)manifest["settlementHandlers"];
            foreach (string face in new[] { "P03", "P04" })
            {
                string id = "entrepreneur." + face + ".cleanup.availability";
                Assert.That(handlers.Count(h => (string)h["handlerId"] == id && (string)h["script"] == "lua/entrepreneurs/paul.p03_p04.cleanup.lua" &&
                    (string)h["entryPoint"] == "availability"), Is.EqualTo(1), "正式manifest缺少availability登记：" + id);
            }
            replacements["pack.json"] = manifest.ToString();
            string input = InputRoot = Artifacts.CopyContent(BaselineInputRoot, "保罗正式内容私有隔离输入", replacements);
            Pack = ExternalContentPack.Load(input); Artifacts.RecordContent("保罗实际装载输入", input, Pack.ContentHash);
            var config = new GameRuleConfiguration { MapId = mapId, PlayerCount = 2, RuleModeId = "standard", EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" }, PlayerStageSchedulingMode = schedulingMode };
            Catalog = ActiveContentCatalogBuilder.Build(Pack, config);
            Artifacts.WriteLabeledText("完整基线到实际输入关联", JsonConvert.SerializeObject(new { baselineInputRoot = BaselineInputRoot, derivedInputRoot = InputRoot, actualContentHash = Pack.ContentHash, activeContentHash = Catalog.ContentPackHash, evidence = "CopyContent的两份input.json保留完整文件SHA、sourceRoot及输入树指纹；正式availability必须已在共享manifest登记，夹具不补登记。" }));
            Registry = new EffectRegistry(); ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); Catalog.Register(Registry);
            ExpansionSetupFlow.Register(Registry); Rounds = new RoundExecutionService(Registry);
            Map = StaticMapDefinitions.Resolve(mapId);
            var recipe = Catalog.SetupRecipe;
            if (!setupOnly)
                foreach (var location in Map.Locations.Where(l => !l.IsRedZone))
                    recipe.Add("qy015.isolated.resource." + location.LocationId, "core.resource_point", "core.resource_point_token", "location/" + location.LocationId);
            config.ContentPackHash = Catalog.ContentPackHash;
            State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1,
                new List<PlayerSeat> { new PlayerSeat { PlayerId = 1, PlayerName = "保罗隔离甲" }, new PlayerSeat { PlayerId = 2, PlayerName = "隔离乙" } },
                config, 47, recipe, Catalog.ContentDefinitions, new List<int> { 1, 2 }, "qy015.content." + Guid.NewGuid().ToString("N"), null);
            State.RuleConfiguration.RuleModeId = "entrepreneur";
            if (!setupOnly)
            {
                foreach (var token in State.ItemZones.Items.Where(i => i.TypeId == "core.resource_point_token"))
                {
                    var zone = State.ItemZones.Zones.Single(z => z.ZoneId == token.ZoneId);
                    var location = Map.Locations.Single(l => l.LocationId == State.ItemZones.Zones.Single(z => z.ZoneId == zone.ParentId).LocalZoneId);
                    token.LocalState = Object("resourceType", NormalizedValue.CreateInteger((int)location.ResourceType), "amount", NormalizedValue.CreateInteger(1));
                    State.Map.OpenLocationIds.Add(location.LocationId);
                }
                Artifacts.WriteLabeledText("隔离资源点前提", "第一条Journal前由私有配方在非红区建立资源点实体，按地图资源类型和每点1份设置；不代表真实入场揭示或正常流程。帮派放置、采集及城市目的地仍接受生产规则检查。");
            }
            Registry.SetupRecipe = GameStateCloneService.DeepClone(State.SetupRecipe);
            State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            InfluenceEffectExecutor.Register(Registry, State, new MapQueryService(Map));
            Artifacts.WriteLabeledText("隔离前提", "私有装配放入真实企业家定义，随后进入真实004选人/选面树；正式G0仍拒绝。不是正常菜单、页面或多局证据。正文调用桥只路由真实SourceRef/真实Lua，不实现任何卡面收益。");
            // 通用测试调用桥：真实事件/DispatchReceipt/SourceUse；不把正文写成C#假处理器。
            Registry.Register("qy015.fixture.signal", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "QY015ContentBody", PlayerId = c.Node.PlayerId, RouteKey = Text(c.Node.NormalizedArguments, "abilityId"), Payload = Object() }));
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy015.fixture.source", "QY015ContentBody", "*", "qy015.fixture.source", c => {
                var s = Registry.QuerySources(c.State, c.Event.PlayerId).Single(x => x.AbilityId == c.Event.RouteKey);
                var d = Registry.AbilityDeclarations.Single(x => x.AbilityId == s.AbilityId);
                return new List<EffectSpec> { AbilitySourceEffectExecutor.Create(s, c.Event, d.HandlerRef, false, c.State.Round) };
            }, EffectHandlerRole.Observer));
            if(setupOnly) { var mapQuery=new MapQueryService(Map); PlayerEntranceLuaCatalog.Register(Registry); PlayerEntranceEffectExecutor.Register(Registry,mapQuery); EventCardEffectExecutor.Register(Registry,mapQuery,new InfluenceService(mapQuery),new EventDeckService(),new ResourceTokenService()); RoundStartedRedZoneRule.Register(Registry,mapQuery); var launch=Rounds.StartEntrance(State); Assert.That(launch.IsValid,Is.True,launch.Reason); }
            else Run(ExpansionSetupFlow.PreparationSpec(1, false));
            for (int guard = 0; State.EntrepreneurSetup?.Stage != "complete" && guard < 40; guard++)
            {
                var q = Open.LastOrDefault(x => x.InteractionTypeId == EntrepreneurFlow.PreviewInteraction || x.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                Assert.That(q, Is.Not.Null, "真实设置树必须产生预览/选人请求");
                Answer(q, q.InteractionTypeId == EntrepreneurFlow.PreviewInteraction ? "continue" : q.AnsweringPlayerId == 1 ? primaryEntrepreneur : secondaryEntrepreneur);
            }
            Assert.That(State.EntrepreneurSetup.Stage, Is.EqualTo("complete"));
            if (!setupOnly)
            {
                // 只补原夹具声明的隔离行动前提；未经过正常入场，不能计作正常流程。
                State.Round = 1; State.ActionRound = 1; State.Phase = GamePhase.ActionRound1;
                State.CurrentPlayerId = State.StartPlayerId = 1;
            }
        }
        internal LuaInvocationResult InvokeP09IdentityGuard(bool departure, string sourceItemId = null)
        {
            string abilityId = departure ? "entrepreneur.P09.departure" : "entrepreneur.P09.place";
            var source = Registry.QuerySources(State, 1).Single(x => x.AbilityId == abilityId && x.ItemId == AbilityItem);
            var content = Pack.Definitions.Single(x => x.DefinitionId == source.DefinitionId);
            var handler = content.AbilityHandlers.Single(x => x.HandlerId == Registry.AbilityDeclarations.Single(d => d.AbilityId == abilityId).HandlerRef);
            var script = new LuaScriptDefinition(content.DefinitionId, abilityId, handler.HandlerId, handler.Version, Pack.GetScript(handler.Script), LuaContentHasher.ComputeSha256(Pack.GetScript(handler.Script)));
            var definition = new LuaHandlerDefinition(handler.HandlerId, departure ? "InfluenceRemoved" : "InfluencePlaceAttaching", "", script, EffectHandlerRole.Primary, contentInstanceId: content.DefinitionId, abilityId: abilityId);
            var arguments = InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetLocationSlotId(Location, 0), sourceId: "player_supply").NormalizedArguments;
            var payload = Object("sourceItemId", NormalizedValue.CreateString(sourceItemId ?? source.ItemId), "abilityId", NormalizedValue.CreateString(abilityId),
                "grantingSourceRefs", Strings(source.GrantingSourceRefs), "facts", Object("arguments", arguments),
                "eventProtocolVersion", NormalizedValue.CreateInteger(2), "itemTypeId", NormalizedValue.CreateString("entrepreneur.gang_marker"), "ownerPlayerId", NormalizedValue.CreateInteger(1));
            var ruleEvent = new RuleEvent { EventId = "qy015.i01.guard", EventType = definition.EventType, PlayerId = 1, Payload = payload };
            var context = new EffectEventHandlerContext(State, ruleEvent, new EffectNodeRuntimeState { EffectId = source.SourceKey, PlayerId = 1 }, null);
            var dispatcher = new LuaEffectEventDispatcher(Registry);
            var method = typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "须沿真实Dispatcher生成当前只读投影；接口变化不能手写替身");
            var invocation = (LuaInvocationContext)method.Invoke(dispatcher, new object[] { definition, context });
            invocation.ResponseKind = departure ? "effects" : "influencePlaceAttaching";
            Artifacts.WriteLabeledText("身份防护隔离调用", "读取真实State/当前QuerySources，以真实sourceKey作为纯来源上下文owner，沿正式Dispatcher生成SourceData投影并调用本人正式Lua；不构造C#卡面或冒称完整SourceUse/正常流程。sourceItemId错指用例仅显式改变该指针。");
            return new MoonSharpLuaRuntimeHost().Invoke(script, invocation);
        }
        internal void Pump()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted && !firstFault) { firstFault = true; Snapshot("首次真实故障"); }
            Assert.That(result.Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
        }
        internal string Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Pump(); return id; }
        internal void Answer(InteractionRequest q, string value)
        {
            Assert.That(q.CandidateIds, Does.Contain(value));
            Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, NormalizedValue.CreateString(value), out var reason), Is.True, reason); Pump();
        }
        internal bool Within(string node, string root) => InfluenceDepartureProtocol.IsWithin(State, node, root);
        internal InteractionRequest Request(string root, string type = "") => Open.Last(x => Within(x.OwnerEffectId, root) && (type.Length == 0 || x.InteractionTypeId == type));
        internal void Choose(int round, string face, bool stopAtDispatch = false)
        {
            State.Round = round;
            string root = Run(ExpansionSetupFlow.RoundPreparationSpec(1));
            for (int guard = 0; guard < 100; guard++)
            {
                var q = Open.LastOrDefault(x => Within(x.OwnerEffectId, root) && (x.InteractionTypeId == EntrepreneurFlow.FaceInteraction || x.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction));
                if (q != null) { Answer(q, q.InteractionTypeId == EntrepreneurFlow.FaceInteraction && q.AnsweringPlayerId == 1 ? "entrepreneur.candidate." + face : q.CandidateIds.First()); continue; }
                var group = State.EntrepreneurSetup.Players.Single(x => x.PlayerId == 1).RoundGroups.Single(x => x.RoundNumber == round);
                if (group.SelectedCandidateId.Length > 0 && State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed) break;
                q = Open.LastOrDefault(x => Within(x.OwnerEffectId, root));
                Assert.That(q, Is.Not.Null, "独立立即尚未结清却缺少真实请求");
                if (stopAtDispatch && q.AnsweringPlayerId == 1 && q.InteractionTypeId == "operator.dispatch.choose") return;
                var reserved = new HashSet<string>(new[] { InfluenceService.GetLocationSlotId(Location, 0), InfluenceService.GetLocationSlotId(Location, 1), InfluenceService.GetLocationSlotId(OtherLocation, 0), InfluenceService.GetLocationSlotId(OtherLocation, 1) });
                string answer = q.CandidateIds.Contains("decline") ? "decline" : q.CandidateIds.FirstOrDefault(x => !reserved.Contains(x)) ?? q.CandidateIds.First(); Answer(q, answer);
            }
            Assert.That(State.EntrepreneurSetup.Players.Single(x => x.PlayerId == 1).RoundGroups.Single(x => x.RoundNumber == round).ActiveFaceId, Is.EqualTo(face));
        }
        internal void Decline(InteractionRequest q)
        {
            Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, NormalizedValue.CreateBoolean(false), out var reason), Is.True, reason); Pump();
        }
        internal void ConfirmPayment(string root)
        {
            var q = Open.Single(x => Within(x.OwnerEffectId, root) && State.EffectRuntime.EffectNodes.Single(n => n.EffectId == x.OwnerEffectId).EffectTypeId == ResourceEffectTypeIds.Pay);
            Answer(q, q.CandidateIds.Single(x => x != "decline"));
        }
        internal void FinishPreparation()
        {
            for (int guard = 0; Open.Count > 0 && guard < 200; guard++)
            {
                var request = Open.Last();
                var values = request.CandidateIds.Take(Math.Max(1, request.MinSelections)).Select(NormalizedValue.CreateString).ToList();
                var answer = request.AnswerSchema == "candidate_ids" || request.AnswerSchema == "candidate_id_array"
                    ? NormalizedValue.CreateArray(values) : values.Single();
                Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                    answer, out string reason), Is.True, reason); Pump();
            }
            Assert.That(Open, Is.Empty, "初始准备必须沿实际请求完整答完");
        }
        internal string FinishIsolatedLastRound()
        {
            FinishPreparation();
            Artifacts.WriteLabeledText("终局隔离入口", "前序行动和采集为隔离前提；末轮每个收尾窗口实际执行，合法关闭后再推进正式RoundEnded。正常覆盖0。");
            string id = IsolatedFinalRoundFlow.CreatePendingFinal(State, Rounds, Pump); Pump(); return id;
        }
        internal string Invoke(string ability) => Run(EffectSpec.Create("qy015.fixture.signal", Object("abilityId", NormalizedValue.CreateString(ability)), 1));
        internal string Location => Map.Locations.First(x => x.InfluenceSlotCount == 2).LocationId;
        internal string OtherLocation => Map.Locations.First(x => x.LocationId != Location && x.InfluenceSlotCount == 2).LocationId;
        internal string Place(string location, int slot, int player = 1, string type = "core.player_marker")
        {
            var id = Run(InfluenceEffectSpecFactory.PlaceInfluence(player, InfluenceService.GetLocationSlotId(location, slot), itemTypeId: type));
            var n = State.EffectRuntime.EffectNodes.Single(x => x.EffectId == id);
            Assert.That(n.Status, Is.EqualTo(EffectNodeStatus.Completed));
            return Value(n.NormalizedResult, "influenceId").ReferenceId;
        }
        internal void DockCity(string location)
        {
            // 明确标记的隔离初始位置；保留真实城市、区和顺序。不是城市移动合法行动证据。
            var city = State.ItemZones.Items.Single(x => x.TypeId == "core.mobile_city" && x.OwnerSubject.PlayerId == 1);
            var point = State.ItemZones.Zones.Single(x => x.TemplateId == "core.zone.map_location" && x.LocalZoneId == location);
            var dock = State.ItemZones.Zones.Single(x => x.TemplateId == "core.zone.city_dock" && x.ParentId == point.ZoneId);
            foreach (var order in State.ItemZones.Orders) order.ItemIds.Remove(city.ItemId);
            city.ZoneId = dock.ZoneId; city.SlotId = "";
            var target = State.ItemZones.Orders.SingleOrDefault(x => x.ZoneId == dock.ZoneId); if (target != null) target.ItemIds.Add(city.ItemId);
            ItemZoneLegacyProjection.Bind(State);
            Assert.That(new ItemZoneQuery().TryValidate(State, out var why), Is.True, why);
            Artifacts.WriteLabeledText("城市隔离初始位置", location);
        }
        internal int Resource(int player, ResourceType resource) => State.FindPlayer(player).Resources.Get(resource);
        internal void Restore(string label)
        {
            Snapshot(label + "输入状态"); var result = Artifacts.Recover(State, label, Registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); State = result.State;
        }
        internal void Snapshot(string label) => Artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(State));
        public void Dispose() { if (State != null) Snapshot("最终真实状态"); Artifacts.Finish(); }
    }
}
