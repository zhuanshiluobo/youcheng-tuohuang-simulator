using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Cards;
using YC.Application.Sessions;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class DiyPreparationDeclarationTests
    {
        private static JObject Step(string id, string source, string script) => new JObject {
            ["stepId"] = id, ["sourcePackId"] = source, ["sourcePackVersion"] = "1.0.0", ["kind"] = "lua",
            ["handlerId"] = source + ".handler", ["handlerVersion"] = "1.0.0", ["execution"] = "once", ["script"] = script,
            ["arguments"] = new JObject { ["enabled"] = true, ["count"] = 3, ["ids"] = new JArray("a", "b") }
        };
        private static JObject Declaration() => new JObject {
            ["version"] = 1,
            ["beforeDistribution"] = new JArray(Step("before", "demo.main", "lua/main.lua")),
            ["afterConfirmation"] = new JArray(Step("after", "demo.helper", "lua/main.lua"))
        };

        private static JObject ModePack(string coreVersion, bool includeHelper)
        {
            var dependencies = new JArray(new JObject { ["packId"] = "core", ["versionRange"] = "=" + coreVersion });
            if (includeHelper) dependencies.Add(new JObject { ["packId"] = "demo.helper", ["versionRange"] = "=1.0.0" });
            return new JObject {
                ["format"] = "nomadcity.diy", ["schemaVersion"] = 1, ["packId"] = "demo.main", ["version"] = "1.0.0",
                ["displayName"] = "模式夹具", ["author"] = "测试", ["description"] = "准备阶段夹具", ["contentApiVersion"] = 1,
                ["requiredCapabilities"] = new JArray("diy.pack.v1", "diy.rule_modes.v1"), ["dependencies"] = dependencies,
                ["ruleModes"] = new JArray(new JObject { ["modeId"] = "demo.main.mode", ["displayName"] = "测试模式", ["description"] = "测试",
                    ["requires"] = new JArray("core"), ["setupRecipeRefs"] = new JArray(new JObject { ["packId"] = "core", ["recipeId"] = "standard" }),
                    ["preparation"] = Declaration() }), ["definitions"] = new JArray(), ["setupRecipes"] = new JArray()
            };
        }
        private static JObject HelperPack(string coreVersion) => new JObject {
            ["format"] = "nomadcity.diy", ["schemaVersion"] = 1, ["packId"] = "demo.helper", ["version"] = "1.0.0",
            ["displayName"] = "步骤来源夹具", ["author"] = "测试", ["description"] = "依赖包", ["contentApiVersion"] = 1,
            ["requiredCapabilities"] = new JArray("diy.pack.v1"),
            ["dependencies"] = new JArray(new JObject { ["packId"] = "core", ["versionRange"] = "=" + coreVersion }),
            ["expansions"] = new JArray(new JObject { ["expansionId"] = "demo.helper.extension", ["displayName"] = "测试扩展",
                ["description"] = "测试", ["requires"] = new JArray("core") }), ["definitions"] = new JArray(), ["setupRecipes"] = new JArray()
        };
        private static string WritePack(string parent, string name, JObject manifest, string script)
        {
            string root = Path.Combine(parent, name);
            Directory.CreateDirectory(Path.Combine(root, "lua"));
            File.WriteAllText(Path.Combine(root, "pack.json"), manifest.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "lua", "main.lua"), script, new UTF8Encoding(false));
            return root;
        }

        [Test]
        public void DeclarationFreezesDistinctSourcesAndDoesNotRunScriptsDuringDiscovery()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string coreVersion = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "declaration-fixtures", Guid.NewGuid().ToString("N")));
            string aScript = "error('发现阶段不能执行')\nreturn function(ctx) return {} end";
            string bScript = "return function(ctx) return {} end";
            var a = DiyPackSnapshot.Load(WritePack(root, "main", ModePack(coreVersion, true), aScript));
            var b = DiyPackSnapshot.Load(WritePack(root, "helper", HelperPack(coreVersion), bScript));
            var mode = a.Manifest.RuleModes.Single();
            Assert.That(mode.EffectiveRequiredCapabilities, Does.Contain(DiyPreparationDeclaration.Capability));
            Assert.That(RuleModeReadiness.ImplementedCapabilities, Does.Contain(DiyPreparationDeclaration.Capability));
            var capabilities = RuleModeReadiness.ImplementedCapabilities.Concat(new[] { DiyPreparationDeclaration.Capability });
            var resolved = DiyContentResolution.Resolve(mode.ModeId, new[] { "core" }, "map-three-players", new[] { a, b },
                coreVersion, core.ContentHash, capabilities, GameplayOptionsCatalog.CoreOnly(core));
            var plan = mode.Preparation.Resolve((id, version, path) => {
                var pack = resolved.GetPack(id);
                Assert.That(pack.Manifest.Version, Is.EqualTo(version));
                return pack.Files.ReadText(path);
            });
            Assert.That(plan.BeforeDistribution.Single().ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(aScript)));
            Assert.That(plan.AfterConfirmation.Single().ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(bScript)));
            Assert.That(plan.BeforeDistribution.Single().ScriptHash, Is.Not.EqualTo(plan.AfterConfirmation.Single().ScriptHash));
            Assert.That(plan.BeforeDistribution.Single().Arguments.Properties.Select(x => x.Name), Is.EqualTo(new[] { "count", "enabled", "ids" }));
        }

        [Test]
        public void ActiveSnapshotLoadsPreparationScriptsFromFrozenSourcePacks()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string coreVersion = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "declaration-fixtures", Guid.NewGuid().ToString("N")));
            const string first = "return function(ctx) return {} end\n";
            const string second = "return function(ctx) return { } end\n";
            string mainRoot = WritePack(root, "main", ModePack(coreVersion, true), first);
            string helperRoot = WritePack(root, "helper", HelperPack(coreVersion), second);
            var main = DiyPackSnapshot.Load(mainRoot);
            var helper = DiyPackSnapshot.Load(helperRoot);
            var resolved = DiyContentResolution.Resolve("demo.main.mode", new[] { "core" }, "map-three-players", new[] { main, helper },
                coreVersion, core.ContentHash, RuleModeReadiness.ImplementedCapabilities.Concat(new[] { DiyPreparationDeclaration.Capability }), GameplayOptionsCatalog.CoreOnly(core));
            File.WriteAllText(Path.Combine(mainRoot, "lua", "main.lua"), "error('磁盘内容已变化')", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(helperRoot, "lua", "main.lua"), "error('磁盘内容已变化')", new UTF8Encoding(false));
            var combined = ExternalContentPack.ComposeDiy(core, resolved);
            var snapshot = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(IEnumerable<string>), typeof(string), typeof(string), typeof(IEnumerable<string>) }, null);
            Assert.That(snapshot, Is.Not.Null);
            var active = (ExternalContentPack)snapshot.Invoke(combined, new object[] { new[] { "core" }, "", "demo.main.mode", Array.Empty<string>() });
            var config = new GameRuleConfiguration { RuleModeId = "demo.main.mode", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new List<string> { "core" } };
            var composer = typeof(ExternalContentPack).Assembly.GetType("YC.Infrastructure.Lua.SetupRecipeComposer", true)
                .GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { active, combined.Manifest, config, Array.Empty<string>(), null };
            var recipe = (GameSetupRecipe)composer.Invoke(null, args);
            Assert.That(recipe.Preparation.BeforeDistribution.Single().ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(first)));
            Assert.That(recipe.Preparation.AfterConfirmation.Single().ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(second)));
            Assert.That(active.GetScript("lua/.diy/demo.main/1.0.0/main.lua"), Is.EqualTo(first));
            Assert.That(active.GetScript("lua/.diy/demo.helper/1.0.0/main.lua"), Is.EqualTo(second));
        }

        [Test]
        public void CompositeFreezesCorePreparationAndCompletionScriptsEvenWhenOtherwiseUnused()
        {
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "declaration-fixtures", Guid.NewGuid().ToString("N")));
            string original = Path.GetFullPath(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string coreRoot = Path.Combine(root, "core");
            foreach (string file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(coreRoot, file.Substring(original.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination);
            }
            const string first = "return function(ctx) return {} end\n";
            const string second = "return function(ctx) return { } end\n";
            string firstPath = Path.Combine(coreRoot, "lua", "fixture-preparation-extra.lua");
            string secondPath = Path.Combine(coreRoot, "lua", "fixture-preparation-completion.lua");
            File.WriteAllText(firstPath, first, new UTF8Encoding(false));
            File.WriteAllText(secondPath, second, new UTF8Encoding(false));
            var core = ExternalContentPack.Load(coreRoot);
            string coreVersion = (string)core.Manifest["version"];
            var manifest = ModePack(coreVersion, false);
            var preparation = (JObject)manifest["ruleModes"][0]["preparation"];
            var before = (JObject)preparation["beforeDistribution"][0];
            before["sourcePackId"] = "core"; before["sourcePackVersion"] = coreVersion;
            before["script"] = "lua/fixture-preparation-extra.lua";
            before["completion"] = new JObject { ["handlerId"] = "core.fixture.completion", ["handlerVersion"] = "1.0.0",
                ["script"] = "lua/fixture-preparation-completion.lua" };
            preparation["afterConfirmation"] = new JArray();
            var modePack = DiyPackSnapshot.Load(WritePack(root, "main", manifest, first));
            var resolved = DiyContentResolution.Resolve("demo.main.mode", new[] { "core" }, "map-three-players", new[] { modePack },
                coreVersion, core.ContentHash, RuleModeReadiness.ImplementedCapabilities.Concat(new[] { DiyPreparationDeclaration.Capability }), GameplayOptionsCatalog.CoreOnly(core));
            var combined = ExternalContentPack.ComposeDiy(core, resolved);
            File.WriteAllText(firstPath, "error('冻结后被修改')", new UTF8Encoding(false));
            File.WriteAllText(secondPath, "error('冻结后被修改')", new UTF8Encoding(false));
            var snapshot = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(IEnumerable<string>), typeof(string), typeof(string), typeof(IEnumerable<string>) }, null);
            var active = (ExternalContentPack)snapshot.Invoke(combined, new object[] { new[] { "core" }, "", "demo.main.mode", Array.Empty<string>() });
            var config = new GameRuleConfiguration { RuleModeId = "demo.main.mode", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new List<string> { "core" } };
            var composer = typeof(ExternalContentPack).Assembly.GetType("YC.Infrastructure.Lua.SetupRecipeComposer", true)
                .GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic);
            var recipe = (GameSetupRecipe)composer.Invoke(null, new object[] { active, combined.Manifest, config, Array.Empty<string>(), null });
            Assert.That(recipe.Preparation.BeforeDistribution.Single().ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(first)));
            Assert.That(recipe.Preparation.BeforeDistribution.Single().Completion.ScriptHash, Is.EqualTo(LuaContentHasher.ComputeSha256(second)));
            Assert.That(active.GetScript("lua/fixture-preparation-extra.lua"), Is.EqualTo(first));
            Assert.That(active.GetScript("lua/fixture-preparation-completion.lua"), Is.EqualTo(second));
        }

        [TestCase("standard")]
        [TestCase("entrepreneur")]
        public void OfficialPreparationHasExplicitVersionAndKeepsLegacyDeclarationUntouched(string modeId)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string originalHash = core.ContentHash;
            var configuration = new GameRuleConfiguration { RuleModeId = modeId, MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = modeId == "entrepreneur" ? new List<string> { "core", "enterprise", "infected" } : new List<string> { "core", "enterprise" },
                OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion };
            var legacy = configuration.Clone(); legacy.OfficialPreparationContract = null;
            Assert.That(JsonConvert.SerializeObject(legacy), Does.Not.Contain("OfficialPreparationContract"));
            Assert.That(configuration.Clone().OfficialPreparationContract, Is.EqualTo(GameRuleConfiguration.OfficialPreparationVersion));
            var manifest = core.Manifest;
            var apply = typeof(ExternalContentPack).GetMethod("ApplyOfficialPreparation", BindingFlags.Instance | BindingFlags.NonPublic);
            apply.Invoke(core, new object[] { manifest, configuration });
            Assert.That(core.Manifest["ruleModes"].OfType<JObject>().Single(item => (string)item["modeId"] == modeId)["preparation"], Is.Null);
            Assert.That(core.ContentHash, Is.EqualTo(originalHash));
            string[] mechanisms = modeId == "entrepreneur" ? new[] { "entrepreneur_selection.v1" } : Array.Empty<string>();
            var snapshot = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(IEnumerable<string>), typeof(string), typeof(string), typeof(IEnumerable<string>) }, null);
            var active = (ExternalContentPack)snapshot.Invoke(core, new object[] { configuration.EnabledExpansionIds, "", modeId, mechanisms });
            var composer = typeof(ExternalContentPack).Assembly.GetType("YC.Infrastructure.Lua.SetupRecipeComposer", true)
                .GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic);
            var recipe = (GameSetupRecipe)composer.Invoke(null, new object[] { active, manifest, configuration, mechanisms, null });
            var legacyRecipe = (GameSetupRecipe)composer.Invoke(null, new object[] { active, core.Manifest, legacy, mechanisms, null });
            Assert.That(legacyRecipe.Preparation, Is.Null);
            var comparable = YC.Domain.State.GameStateCloneService.DeepClone(recipe);
            comparable.Preparation = null;
            Assert.That(JsonConvert.SerializeObject(comparable), Is.EqualTo(JsonConvert.SerializeObject(legacyRecipe)),
                "新合同以外的官方配方保持原参数和条目");
            Assert.That(recipe.EnterpriseEntrance, Is.True);
            Assert.That(recipe.Preparation.Enterprise.PlayerCounts.Select(rule => (rule.PlayerCount, rule.Draw, rule.Keep, rule.ExtraRandom, rule.FinalCount)),
                Is.EqualTo(new[] { (2, 4, 2, 0, 4), (3, 2, 1, 1, 4), (4, 2, 1, 0, 4) }));
            var middle = recipe.Preparation.BetweenDistributionAndConfirmation;
            if (modeId == "entrepreneur")
            {
                Assert.That(middle.Count, Is.EqualTo(1));
                Assert.That(middle[0].HandlerId, Is.EqualTo(EntrepreneurFlow.PreparationHandlerId));
                Assert.That(middle[0].SourcePackVersion, Is.EqualTo((string)core.Manifest["version"]));
            }
            else Assert.That(middle, Is.Empty);
            Assert.That(recipe.Preparation.BeforeDistribution, Is.Empty);
            Assert.That(recipe.Preparation.AfterConfirmation, Is.Empty);
            Assert.That(recipe.Preparation.CityStyles, Is.Null, "沿用原配方的城市样式数量");
        }

        [Test]
        public void OfficialBaseModeUsesEmptyFrameworkAndRejectsUnknownVersion()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new GameRuleConfiguration { RuleModeId = "standard", PlayerCount = 3,
                EnabledExpansionIds = new List<string> { "core" }, OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion };
            var apply = typeof(ExternalContentPack).GetMethod("ApplyOfficialPreparation", BindingFlags.Instance | BindingFlags.NonPublic);
            var manifest = core.Manifest;
            apply.Invoke(core, new object[] { manifest, configuration });
            var selected = manifest["ruleModes"].OfType<JObject>().Single(item => (string)item["modeId"] == "standard");
            var plan = DiyPreparationDeclaration.Parse((JObject)selected["preparation"], "standard", "fixture")
                .Resolve((id, version, path) => throw new AssertionException("空基础准备流程不应读取脚本"));
            Assert.That(plan.Enterprise, Is.Null); Assert.That(plan.BeforeDistribution, Is.Empty);
            Assert.That(plan.BetweenDistributionAndConfirmation, Is.Empty); Assert.That(plan.AfterConfirmation, Is.Empty);
            configuration.OfficialPreparationContract = "official.preparation.unknown";
            Assert.That(configuration.TryValidateStructure(out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("preparation_contract_invalid"));
        }

        [TestCase("standard", false)] [TestCase("standard", true)] [TestCase("entrepreneur", true)]
        public void NewOfficialGamesChooseEnabledPreparationFramework(string mode, bool enterprise)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new GameRuleConfiguration { RuleModeId = mode, MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = mode == "entrepreneur" ? new List<string> { "core", "enterprise", "infected" } :
                    enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
            configuration.OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion;
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration);
            Assert.That(catalog.SetupRecipe.Preparation, Is.Not.Null);
            Assert.That(catalog.SetupRecipe.Preparation.Enterprise != null, Is.EqualTo(enterprise));
            Assert.That(catalog.SetupRecipe.Preparation.BetweenDistributionAndConfirmation.Any(step => step.HandlerId == EntrepreneurFlow.PreparationHandlerId), Is.EqualTo(mode == "entrepreneur"));
        }

        [TestCase(false)] [TestCase(true)]
        public void DiyModeWithoutPreparationUsesCommonFrameworkForSelectedExpansions(bool enterprise)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "declaration-fixtures", Guid.NewGuid().ToString("N")));
            var manifest = ModePack(version, false);
            ((JObject)manifest["ruleModes"][0]).Remove("preparation");
            var diy = DiyPackSnapshot.Load(WritePack(root, "main", manifest, "return function(ctx) return {} end"));
            var expansions = enterprise ? new[] { "core", "enterprise" } : new[] { "core" };
            var resolved = DiyContentResolution.Resolve("demo.main.mode", expansions, "map-three-players", new[] { diy },
                version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core));
            var config = new GameRuleConfiguration { RuleModeId = "demo.main.mode", PlayerCount = 3, MapId = "map-three-players",
                EnabledExpansionIds = expansions.ToList(), OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion,
                LockedContentPacks = resolved.CreateRuleLocks() };
            var catalog = ActiveContentCatalogBuilder.Build(core, config, resolved);
            Assert.That(catalog.SetupRecipe.Preparation, Is.Not.Null);
            Assert.That(catalog.SetupRecipe.Preparation.ModeId, Is.EqualTo(config.RuleModeId));
            Assert.That(catalog.SetupRecipe.Preparation.Enterprise != null, Is.EqualTo(enterprise));
            Assert.That(catalog.SetupRecipe.Preparation.BetweenDistributionAndConfirmation, Is.Empty);
        }

        [Test]
        public void SingleHostRoomCanSelectEnterpriseAndDiyPreparationBeforeOtherPlayersJoin()
        {
            // 配置与开局是不同门禁；只有房主时可选择内容，仍不能开始不足人数的对局。
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string coreVersion = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "one-host-room", Guid.NewGuid().ToString("N")));
            var manifest = ModePack(coreVersion, false);
            var mode = (JObject)manifest["ruleModes"][0];
            mode["requires"] = new JArray("core", "enterprise");
            var preparation = (JObject)mode["preparation"];
            preparation["afterConfirmation"] = new JArray();
            preparation["enterprise"] = new JObject { ["pool"] = new JObject { ["kind"] = "all" },
                ["playerCounts"] = new JArray(new JObject { ["playerCount"] = 4, ["draw"] = 2, ["keep"] = 1, ["finalCount"] = 4 }) };
            WritePack(Path.Combine(root, "Mods", "demo.main"), "1.0.0", manifest, "return function(ctx) return {} end");
            var discovered = DiyPackDiscovery.Scan(new DiyDataPaths(root));
            var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(discovered);
            var repository = new DiyInstalledRepository(core, discovered, profile);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var service = new YC.Infrastructure.Multiplayer.RoomConfigurationService(provider, localPlayers: true);
            var room = new YC.Infrastructure.Multiplayer.RoomState { RoomId = "preparation-room", HostPlayerId = 1, LocalPlayerId = 1 };
            room.Configuration.OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion;
            room.Configuration.ContentPackHash = provider.GetContentPackHash(room.Configuration);
            Assert.That(service.TryOccupySeat(room, new YC.Application.Sessions.PlayerSeat { PlayerId = 1, PlayerName = "房主",
                LobbyMemberPresent = true, TransportConnected = true, IdentityVerified = true, IsReady = true }, out var seatReason), Is.True, seatReason);
            YC.Infrastructure.Multiplayer.RoomConfigurationResult Change(string id, params string[] expansions) => service.ChangeConfiguration(room,
                new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { RoomId = room.RoomId, CommandId = Guid.NewGuid().ToString("N"),
                    RequestingPlayerId = 1, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision,
                    MapId = room.Configuration.MapId, RuleModeId = id, EnabledExpansionIds = expansions.ToList() });
            var enterprise = Change("standard", "core", "enterprise");
            Assert.That(enterprise.Success, Is.True, enterprise.ReasonCode);
            var diy = Change("demo.main.mode", "core", "enterprise");
            Assert.That(diy.Success, Is.True, diy.ReasonCode);
            Assert.That(room.Configuration.RuleModeId, Is.EqualTo("demo.main.mode"));
            Assert.That(service.TryFreezeForLaunch(room, new YC.Infrastructure.Multiplayer.StartConfiguredGameRequest {
                RoomId = room.RoomId, CommandId = "start-too-few", RequestingPlayerId = 1,
                ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision }, out _, out _), Is.False);
        }

        [TestCase("enterprise.rhodes_island", false, false)]
        [TestCase("infected.enterprise.rhodes_island", false, true)]
        [TestCase("enterprise.rhodes_island", true, true)]
        public void FixedEnterprisePoolIsCheckedAgainstTheActiveRecipeBeforeStarting(string rhodesId, bool dynamicCandidates, bool expected)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "active-pool", Guid.NewGuid().ToString("N")));
            var manifest = ModePack(version, false);
            var mode = (JObject)manifest["ruleModes"][0];
            mode["requires"] = new JArray("core", "enterprise");
            var preparation = (JObject)mode["preparation"];
            preparation["afterConfirmation"] = new JArray();
            var ids = new JArray("enterprise.blacksteel", "enterprise.fussen_bank", "enterprise.hyde_brothers", "enterprise.mama_johns",
                "enterprise.raythean", "enterprise.rhine_lab", rhodesId, "enterprise.rim_billiton");
            preparation["enterprise"] = new JObject { ["pool"] = new JObject { ["kind"] = "fixed", ["candidateDefinitionIds"] = ids, ["count"] = 8 },
                ["playerCounts"] = new JArray(new JObject { ["playerCount"] = 3, ["draw"] = 2, ["keep"] = 1, ["extraRandom"] = 1, ["finalCount"] = 4 }),
                ["fixed"] = new JArray(new JObject { ["definitionId"] = "enterprise.hyde_brothers", ["ownerOrderIndex"] = 0, ["requireConfirmation"] = true }) };
            if (dynamicCandidates) preparation["distributionInputs"] = new JArray(new JObject { ["stepId"] = "before",
                ["effectTypeId"] = "fixture.result", ["effectStableKey"] = "fixture:result", ["resultField"] = "definitions",
                ["playerScope"] = "once", ["target"] = "candidateDefinitionIds" });
            var diy = DiyPackSnapshot.Load(WritePack(root, "main", manifest, "return function(ctx) return {} end"));
            var expansions = new[] { "core", "enterprise", "infected" };
            var resolved = DiyContentResolution.Resolve("demo.main.mode", expansions, "map-three-players", new[] { diy },
                version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core));
            var config = new GameRuleConfiguration { RuleModeId = "demo.main.mode", PlayerCount = 3, MapId = "map-three-players",
                EnabledExpansionIds = expansions.ToList(), OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion,
                LockedContentPacks = resolved.CreateRuleLocks() };
            if (expected) Assert.DoesNotThrow(() => ActiveContentCatalogBuilder.Build(core, config, resolved));
            else Assert.That(Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(core, config, resolved)).Message,
                Is.EqualTo("preparation_pool_definition_missing:enterprise:enterprise.rhodes_island"));
        }

        [Test]
        public void ASelectedButUndeclaredPackCannotSupplyPreparationSteps()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string coreVersion = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "declaration-fixtures", Guid.NewGuid().ToString("N")));
            const string script = "return function(ctx) return {} end";
            var a = DiyPackSnapshot.Load(WritePack(root, "main", ModePack(coreVersion, false), script));
            var b = DiyPackSnapshot.Load(WritePack(root, "helper", HelperPack(coreVersion), script));
            var capabilities = RuleModeReadiness.ImplementedCapabilities.Concat(new[] { DiyPreparationDeclaration.Capability });
            var error = Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("demo.main.mode", new[] { "core", "demo.helper.extension" },
                "map-three-players", new[] { a, b }, coreVersion, core.ContentHash, capabilities, GameplayOptionsCatalog.CoreOnly(core)));
            Assert.That(error.Message, Does.Contain("步骤来源不在模式依赖闭包"));
        }

        [TestCase("lua/../escape.lua")]
        [TestCase("lua/CON.lua")]
        [TestCase("lua/bad?.lua")]
        public void DeclarationRejectsUnsafeScriptPaths(string path)
        {
            var declaration = Declaration();
            declaration["beforeDistribution"][0]["script"] = path;
            Assert.Throws<InvalidDataException>(() => DiyPreparationDeclaration.Parse(declaration, "demo.main.mode", "fixture"));
        }

        [Test]
        public void UnknownFieldsAndUnsupportedParameterTypesFailWithTheirLocation()
        {
            var declaration = Declaration();
            declaration["beforeDistribution"][0]["arguments"]["fraction"] = 1.5;
            var error = Assert.Throws<InvalidDataException>(() => DiyPreparationDeclaration.Parse(declaration, "demo.main.mode", "fixture"));
            Assert.That(error.Message, Does.Contain("fraction"));
            declaration = Declaration();
            declaration["beforeDistribution"][0]["unexpected"] = true;
            error = Assert.Throws<InvalidDataException>(() => DiyPreparationDeclaration.Parse(declaration, "demo.main.mode", "fixture"));
            Assert.That(error.Message, Does.Contain("unexpected"));
        }

        private static JObject OrderedModePack(string coreVersion, string owner, int order, bool enterprise)
        {
            var preparation = new JObject {
                ["version"] = 1,
                ["beforeDistribution"] = new JArray(Step("before", owner, "lua/main.lua")),
                ["afterConfirmation"] = new JArray(Step("after", owner, "lua/main.lua"))
            };
            if (enterprise)
            {
                preparation["betweenDistributionAndConfirmation"] = new JArray(Step("middle", owner, "lua/main.lua"));
                preparation["enterprise"] = new JObject {
                    ["pool"] = new JObject { ["kind"] = "all" },
                    ["playerCounts"] = new JArray(
                        new JObject { ["playerCount"] = 2, ["draw"] = 4, ["keep"] = 2, ["finalCount"] = 4 },
                        new JObject { ["playerCount"] = 3, ["draw"] = 2, ["keep"] = 1, ["extraRandom"] = 1, ["finalCount"] = 4 },
                        new JObject { ["playerCount"] = 4, ["draw"] = 2, ["keep"] = 1, ["finalCount"] = 4 })
                };
            }
            return new JObject {
                ["format"] = "nomadcity.diy", ["schemaVersion"] = 1, ["packId"] = owner, ["version"] = "1.0.0",
                ["displayName"] = owner, ["author"] = "测试", ["description"] = "多模式准备夹具", ["contentApiVersion"] = 1,
                ["requiredCapabilities"] = new JArray("diy.pack.v1", "diy.rule_modes.v1"),
                ["dependencies"] = new JArray(new JObject { ["packId"] = "core", ["versionRange"] = "=" + coreVersion }),
                ["ruleModes"] = new JArray(new JObject { ["modeId"] = owner + ".mode", ["displayName"] = owner,
                    ["description"] = "测试", ["requires"] = enterprise ? new JArray("core", "enterprise") : new JArray("core"),
                    ["preparationOrder"] = order, ["setupRecipeRefs"] = new JArray(new JObject { ["packId"] = "core", ["recipeId"] = "standard" }),
                    ["ruleBindings"] = new JArray(new JObject { ["bindingId"] = owner + ".round", ["eventType"] = "RoundStarted", ["script"] = "lua/main.lua", ["version"] = "1.0.0" }),
                    ["preparation"] = preparation }),
                ["definitions"] = new JArray(), ["setupRecipes"] = new JArray()
            };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MultipleModesFreezeAndOrderEveryPreparationWindow(bool enterprise)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "preparation-sequence-fixtures", Guid.NewGuid().ToString("N")));
            const string script = "return function(ctx) return {} end";
            var first = DiyPackSnapshot.Load(WritePack(root, "first", OrderedModePack(version, "test.first", 20, enterprise), script));
            var second = DiyPackSnapshot.Load(WritePack(root, "second", OrderedModePack(version, "test.second", 10, enterprise), script));
            var expansions = enterprise ? new[] { "core", "enterprise" } : new[] { "core" };
            var extraModes = new[] { "test.second.mode" };
            var resolved = DiyContentResolution.Resolve("test.first.mode", expansions, "map-three-players", new[] { first, second },
                version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core), extraModes);
            var configuration = new GameRuleConfiguration { RuleModeId = "test.first.mode", AdditionalRuleModeIds = extraModes.ToList(),
                PlayerCount = 3, MapId = "map-three-players", EnabledExpansionIds = expansions.ToList(),
                OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion, LockedContentPacks = resolved.CreateRuleLocks() };
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, resolved);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var registry = new EffectRegistry(); catalog.Register(registry);
            Assert.That(registry.HasEventHandler(StableIdFactory.Create("diy-rule-binding", "test.first", "test.first.mode", "test.first.round")), Is.True);
            Assert.That(registry.HasEventHandler(StableIdFactory.Create("diy-rule-binding", "test.second", "test.second.mode", "test.second.round")), Is.True);
            var plan = catalog.SetupRecipe.Preparation;
            Assert.That(plan.BeforeDistribution.Select(step => step.StepId), Is.EqualTo(new[] { StableIdFactory.Create("preparation-step", "test.second.mode", "before"), StableIdFactory.Create("preparation-step", "test.first.mode", "before") }));
            Assert.That(plan.AfterConfirmation.Select(step => step.StepId), Is.EqualTo(new[] { StableIdFactory.Create("preparation-step", "test.second.mode", "after"), StableIdFactory.Create("preparation-step", "test.first.mode", "after") }));
            if (enterprise)
            {
                Assert.That(plan.BetweenDistributionAndConfirmation.Select(step => step.StepId), Is.EqualTo(new[] { StableIdFactory.Create("preparation-step", "test.second.mode", "middle"), StableIdFactory.Create("preparation-step", "test.first.mode", "middle") }));
                Assert.That(plan.Enterprise, Is.Not.Null);
            }
            else Assert.That(plan.Enterprise, Is.Null);
            ExerciseOrderedWindows(configuration, catalog.SetupRecipe, enterprise);
            ExerciseFormalPreparationRecovery(configuration, catalog, enterprise);
            var roundTrip = JsonConvert.DeserializeObject<GameRuleConfiguration>(JsonConvert.SerializeObject(configuration));
            Assert.That(roundTrip.ActiveRuleModeIds, Is.EqualTo(configuration.ActiveRuleModeIds));
            Assert.That(catalog.TryValidateConfiguration(roundTrip, out var reason), Is.True, reason);
            var altered = roundTrip.Clone(); altered.AdditionalRuleModeIds.Clear();
            Assert.That(catalog.TryValidateConfiguration(altered, out _), Is.False, "不能用单模式冒充已冻结的多模式");
            var reversed = DiyContentResolution.Resolve("test.first.mode", expansions, "map-three-players", new[] { second, first },
                version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core), extraModes);
            Assert.That(reversed.LockHash, Is.EqualTo(resolved.LockHash));
            Assert.That(ActiveContentCatalogBuilder.Build(core, configuration, reversed).ContentPackHash, Is.EqualTo(catalog.ContentPackHash));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MultipleModesRejectEqualPreparationOrdersAndDeclaredConflicts(bool declaredConflict)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "preparation-sequence-fixtures", Guid.NewGuid().ToString("N")));
            const string script = "return function(ctx) return {} end";
            var firstSource = OrderedModePack(version, "test.first", 10, false);
            var secondSource = OrderedModePack(version, "test.second", declaredConflict ? 20 : 10, false);
            if (declaredConflict) secondSource["ruleModes"][0]["conflicts"] = new JArray("test.first.mode");
            var first = DiyPackSnapshot.Load(WritePack(root, "first", firstSource, script));
            var second = DiyPackSnapshot.Load(WritePack(root, "second", secondSource, script));
            var error = Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("test.first.mode", new[] { "core" },
                "map-three-players", new[] { first, second }, version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities,
                GameplayOptionsCatalog.CoreOnly(core), new[] { "test.second.mode" }));
            Assert.That(error.Message, Does.Contain(declaredConflict ? "模式冲突" : "准备阶段序号冲突"));
        }

        [TestCase("standard", "entrepreneur")]
        [TestCase("entrepreneur", "standard")]
        public void BuiltinModesShareOneEnterpriseDistributionAndKeepEntrepreneurStep(string primary, string additional)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new GameRuleConfiguration { RuleModeId = primary, AdditionalRuleModeIds = new List<string> { additional },
                MapId = "map-three-players", PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" },
                OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion };
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration);
            var plan = catalog.SetupRecipe.Preparation;
            Assert.That(plan.ModeId, Is.EqualTo(primary));
            Assert.That(plan.Enterprise, Is.Not.Null);
            Assert.That(plan.BetweenDistributionAndConfirmation.Count(step => step.HandlerId == EntrepreneurFlow.PreparationHandlerId), Is.EqualTo(1));
            Assert.That(catalog.SetupRecipe.Mechanisms, Does.Contain("entrepreneur_selection.v1"));
            Assert.DoesNotThrow(() => catalog.Register(new EffectRegistry()));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SharedEntrepreneurMechanismInstallsItsPreparationExactlyOnce(bool alsoEnableOfficialMode)
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "preparation-sequence-fixtures", Guid.NewGuid().ToString("N")));
            var source = OrderedModePack(version, "test.mechanism", 15, true);
            var mode = (JObject)source["ruleModes"][0]; mode.Remove("preparation");
            mode["mechanisms"] = new JArray("entrepreneur_selection.v1");
            ((JArray)mode["setupRecipeRefs"]).Add(new JObject { ["packId"] = "core", ["recipeId"] = "entrepreneur" });
            var diy = DiyPackSnapshot.Load(WritePack(root, "mechanism", source, "return function(ctx) return {} end"));
            var extras = new List<string> { "test.mechanism.mode" };
            if (alsoEnableOfficialMode) extras.Add("entrepreneur");
            var expansions = new[] { "core", "enterprise", "infected" };
            var resolved = DiyContentResolution.Resolve("standard", expansions, "map-three-players", new[] { diy }, version,
                core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core), extras);
            var configuration = new GameRuleConfiguration { RuleModeId = "standard", AdditionalRuleModeIds = extras, PlayerCount = 3,
                MapId = "map-three-players", EnabledExpansionIds = expansions.ToList(), OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion,
                LockedContentPacks = resolved.CreateRuleLocks() };
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, resolved);
            Assert.That(catalog.SetupRecipe.Mechanisms, Does.Contain("entrepreneur_selection.v1"));
            Assert.That(catalog.SetupRecipe.Preparation.BetweenDistributionAndConfirmation.Count(step => step.HandlerId == EntrepreneurFlow.PreparationHandlerId), Is.EqualTo(1));
            Assert.DoesNotThrow(() => catalog.Register(new EffectRegistry()));
        }

        [Test]
        public void RoomCommitsMultipleModesAndRejectsConflictingSequenceWithoutChangingState()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            string root = Path.GetFullPath(Path.Combine("TestResults", "preparation-sequence-room-fixtures", Guid.NewGuid().ToString("N")));
            const string script = "return function(ctx) return {} end";
            foreach (var item in new[] { (Owner: "test.first", Order: 20), (Owner: "test.second", Order: 10), (Owner: "test.third", Order: 10) })
                WritePack(Path.Combine(root, "Mods", item.Owner), "1.0.0", OrderedModePack(version, item.Owner, item.Order, false), script);
            var discovered = DiyPackDiscovery.Scan(new DiyDataPaths(root));
            var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(discovered);
            var repository = new DiyInstalledRepository(core, discovered, profile);
            var options = repository.GetOptions();
            Assert.That(options.TryPlanModesTransition(new[] { "test.first.mode", "test.second.mode" }, "map-three-players",
                new[] { "core" }, out _, out var transitionReason), Is.True, transitionReason);
            Assert.That(options.TryPlanModesTransition(new[] { "test.first.mode", "test.second.mode", "test.third.mode" }, "map-three-players",
                new[] { "core" }, out _, out transitionReason), Is.False);
            Assert.That(transitionReason, Does.Contain("准备阶段序号冲突"));
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var service = new YC.Infrastructure.Multiplayer.RoomConfigurationService(provider, localPlayers: true);
            var room = new YC.Infrastructure.Multiplayer.RoomState { RoomId = "multi-preparation-room", HostPlayerId = 1, LocalPlayerId = 1 };
            room.Configuration.OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion;
            room.Configuration.ContentPackHash = provider.GetContentPackHash(room.Configuration);
            Assert.That(service.TryOccupySeat(room, new YC.Application.Sessions.PlayerSeat { PlayerId = 1, PlayerName = "房主",
                LobbyMemberPresent = true, TransportConnected = true, IdentityVerified = true, IsReady = true }, out var seatReason), Is.True, seatReason);
            var request = new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { RoomId = room.RoomId, CommandId = "two-modes",
                RequestingPlayerId = 1, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, MapId = room.Configuration.MapId,
                RuleModeId = "test.first.mode", AdditionalRuleModeIds = new List<string> { "test.second.mode" }, EnabledExpansionIds = new List<string> { "core" } };
            var changed = service.ChangeConfiguration(room, request);
            Assert.That(changed.Success, Is.True, changed.ReasonCode);
            Assert.That(room.Configuration.ActiveRuleModeIds, Is.EqualTo(new[] { "test.first.mode", "test.second.mode" }));
            Assert.That(room.Configuration.LockedContentPacks.Select(item => item.PackId), Is.EqualTo(new[] { "core", "test.first", "test.second" }));
            AssertRoomModeControls(options, room, provider, root);
            string json = YC.Infrastructure.Multiplayer.RoomStateSerializer.Serialize(room);
            Assert.That(YC.Infrastructure.Multiplayer.RoomStateSerializer.TryDeserialize(json, out var copy, out var reason), Is.True, reason);
            Assert.That(copy.Configuration.ActiveRuleModeIds, Is.EqualTo(room.Configuration.ActiveRuleModeIds));
            Assert.That(provider.TryValidate(copy.Configuration, out reason), Is.True, reason);
            var corruptRoom = JObject.Parse(json); corruptRoom["Configuration"]["AdditionalRuleModeIds"] = new JArray(123);
            Assert.That(YC.Infrastructure.Multiplayer.RoomStateSerializer.TryDeserialize(corruptRoom.ToString(), out _, out _), Is.False);
            var message = YC.Infrastructure.Multiplayer.RoomConfigurationMessage.Create(request, provider, room.Configuration);
            string messageJson = JsonConvert.SerializeObject(message);
            Assert.That(YC.Infrastructure.Multiplayer.RoomConfigurationMessage.TryDeserialize(messageJson, out var decoded), Is.True);
            Assert.That(((YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest)decoded.ReadRequest()).AdditionalRuleModeIds, Is.EqualTo(request.AdditionalRuleModeIds));
            var corruptMessage = JObject.Parse(messageJson);
            var corruptRequest = JObject.Parse((string)corruptMessage["RequestJson"]); corruptRequest["AdditionalRuleModeIds"] = new JArray(123);
            corruptMessage["RequestJson"] = corruptRequest.ToString(Formatting.None);
            Assert.That(YC.Infrastructure.Multiplayer.RoomConfigurationMessage.TryDeserialize(corruptMessage.ToString(), out _), Is.False);
            string before = JsonConvert.SerializeObject(room.Configuration);
            request.CommandId = "conflicting-mode"; request.ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision;
            request.AdditionalRuleModeIds.Add("test.third.mode");
            var rejected = service.ChangeConfiguration(room, request);
            Assert.That(rejected.Success, Is.False);
            Assert.That(rejected.ReasonCode, Does.Contain("准备阶段序号冲突"));
            Assert.That(JsonConvert.SerializeObject(room.Configuration), Is.EqualTo(before));
        }

        private static void AssertRoomModeControls(GameplayOptionsCatalog options, YC.Infrastructure.Multiplayer.RoomState room,
            YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider provider, string fixtureRoot)
        {
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var providerField = serviceType.GetField("contentCapabilities", BindingFlags.Static | BindingFlags.NonPublic);
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var optionsField = runtimeType.GetField("gameplayOptions", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = providerField.GetValue(null), previousOptions = optionsField.GetValue(null);
            var scene = default(UnityEngine.SceneManagement.Scene);
            try
            {
                providerField.SetValue(null, provider); optionsField.SetValue(null, options);
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity", UnityEditor.SceneManagement.OpenSceneMode.Additive);
                var panelType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
                var controllerType = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", true);
                var panel = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(panelType, true)).Single();
                var controller = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(controllerType, true)).Single();
                ((UnityEngine.Component)panel).gameObject.SetActive(true);
                panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                panelType.GetMethod("SetNetworkingState").Invoke(panel, new object[] { false, true });
                panelType.GetMethod("Render").Invoke(panel, new object[] { room, room.LocalPlayerId });
                var items = ((System.Collections.IEnumerable)panelType.GetField("ruleModes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel)).Cast<object>().ToArray();
                object Find(string id) => items.Single(item => (string)item.GetType().GetField("Id").GetValue(item) == id);
                bool Selected(string id) => ((UnityEngine.UI.Image)Find(id).GetType().GetField("Selection").GetValue(Find(id))).gameObject.activeSelf;
                Assert.That(Selected("test.first.mode") && Selected("test.second.mode"), Is.True);
                Assert.That(Selected("test.third.mode"), Is.False);
                string clicked = null; Action<string> handler = id => clicked = id;
                panelType.GetEvent("RuleModeSelectionRequested").AddEventHandler(panel, handler);
                ((UnityEngine.UI.Button)Find("test.second.mode").GetType().GetField("Button").GetValue(Find("test.second.mode"))).onClick.Invoke();
                Assert.That(clicked, Is.EqualTo("test.second.mode"));
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    var initial = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                    const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
                    controllerType.GetField("roomService", instance).SetValue(controller, service);
                    controllerType.GetField("localDiySource", instance).SetValue(controller,
                        (Func<DiyLocalContentConfiguration>)(() => new DiyLocalContentConfiguration(new DiyDataPaths(fixtureRoot))));
                    controllerType.GetMethod("BindStaticUi", instance).Invoke(controller, null);
                    controllerType.GetMethod("ShowRoomPanel", instance).Invoke(controller, new object[] { initial, true });
                    void Click(string id) => ((UnityEngine.UI.Button)Find(id).GetType().GetField("Button").GetValue(Find(id))).onClick.Invoke();
                    Click("test.first.mode");
                    Assert.That(service.GetCurrentRoom().Configuration.ActiveRuleModeIds, Is.EqualTo(new[] { "test.first.mode" }));
                    Click("test.second.mode");
                    Assert.That(service.GetCurrentRoom().Configuration.ActiveRuleModeIds, Is.EqualTo(new[] { "test.first.mode", "test.second.mode" }));
                    Assert.That(Selected("test.first.mode") && Selected("test.second.mode"), Is.True);
                    Click("test.third.mode");
                    Assert.That(service.GetCurrentRoom().Configuration.ActiveRuleModeIds, Is.EqualTo(new[] { "test.first.mode", "test.second.mode" }));
                    Assert.That(((UnityEngine.UI.Text)panelType.GetProperty("StatusText").GetValue(panel)).text, Does.Contain("准备阶段序号冲突"));
                    Click("test.second.mode");
                    Assert.That(service.GetCurrentRoom().Configuration.ActiveRuleModeIds, Is.EqualTo(new[] { "test.first.mode" }));
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                providerField.SetValue(null, previous); optionsField.SetValue(null, previousOptions);
            }
        }

        private static void ExerciseOrderedWindows(GameRuleConfiguration configuration, GameSetupRecipe recipe, bool enterprise)
        {
            var state = new GameState { GameId = "multi-mode-window-fixture", MapId = "map-three-players", ContentPackHash = configuration.ContentPackHash,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = configuration.Clone(), SetupRecipe = recipe,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 }, new PlayerState { PlayerId = 3 } } };
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
            var registry = new EffectRegistry(); PreparationWindowFlow.Register(registry);
            var calls = new List<string>();
            var all = recipe.Preparation.BeforeDistribution.Concat(recipe.Preparation.BetweenDistributionAndConfirmation).Concat(recipe.Preparation.AfterConfirmation);
            foreach (var step in all.GroupBy(value => value.SourcePackId + "/" + value.HandlerId).Select(group => group.First()))
                registry.RegisterPreparationStep(new PreparationStepRegistration(step, (context, arguments) => {
                    calls.Add(context.Node.NormalizedArguments.Properties.Single(item => item.Name == "stepId").Value.StringValue);
                    return new List<EffectSpec> { EffectSpec.Create("test.multi.choice", playerId: context.Node.PlayerId) };
                }));
            registry.Register("test.multi.choice", context => context.GetLatestInteractionAnswer() != null
                ? EffectStepResult.Completed() : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "test.multi.choice", AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", AnswerSchema = "boolean" }));
            var windows = enterprise ? new[] { PreparationWindowFlow.Before, PreparationWindowFlow.Middle, PreparationWindowFlow.After }
                : new[] { PreparationWindowFlow.Before, PreparationWindowFlow.After };
            foreach (string window in windows)
            {
                var engine = new EffectTreeExecutor(state, registry);
                string root = engine.CreateRoot(PreparationWindowFlow.WindowSpec(window, 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                string suffix = window == PreparationWindowFlow.Before ? "before" : window == PreparationWindowFlow.Middle ? "middle" : "after";
                Assert.That(calls.Last(), Is.EqualTo(StableIdFactory.Create("preparation-step", "test.second.mode", suffix)));
                state = GameStateCloneService.DeepClone(state); // 待选择时模拟断线/读档续行，不重做已执行步骤。
                var windowNode = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == root);
                Assert.That(PreparationWindowFlow.ValidateWindow(state, windowNode, state.SetupRecipe.Preparation, registry, out var diagnostic), Is.True, diagnostic);
                while (state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"))
                {
                    var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
                    request.StateRevision = state.EffectRuntime.StateRevision;
                    engine = new EffectTreeExecutor(state, registry);
                    Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                        NormalizedValue.CreateBoolean(true), out var reason), Is.True, reason);
                    Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                }
                Assert.That(calls.TakeLast(2), Is.EqualTo(new[] { StableIdFactory.Create("preparation-step", "test.second.mode", suffix), StableIdFactory.Create("preparation-step", "test.first.mode", suffix) }));
                Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
        }

        private static void ExerciseFormalPreparationRecovery(GameRuleConfiguration configuration, ActiveContentCatalog catalog, bool enterprise)
        {
            var registry = new EffectRegistry(); catalog.Register(registry);
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            var seats = Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "夹具玩家" + id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influence = new YC.Domain.Influence.InfluenceService(map);
            InfluenceEffectExecutor.Register(registry, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(), new ResourceTokenService());
            PlayerEntranceEffectExecutor.Register(registry, map); RoundStartedRedZoneRule.Register(registry, map);
            var mainline = new RoundExecutionService(registry);
            var started = mainline.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Assert.That(state.PreparationRuntime, Is.Not.Null);
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo(enterprise ? "confirming" : "complete"));
            var stepsBefore = state.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == PreparationWindowFlow.StepTypeId).Select(node => node.EffectId).ToArray();
            var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var recovered = HostRecoveryService.RecoverJson(archive, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.RuleConfiguration.ActiveRuleModeIds, Is.EqualTo(configuration.ActiveRuleModeIds));
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == PreparationWindowFlow.StepTypeId).Select(node => node.EffectId), Is.EqualTo(stepsBefore));
            if (!enterprise) return;
            var pending = recovered.State.EffectRuntime.InteractionRequests.First(request => request.Status == "open" && request.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction);
            pending.StateRevision = recovered.State.EffectRuntime.StateRevision;
            var engine = new EffectTreeExecutor(recovered.State, registry);
            Assert.That(engine.TrySubmitInteraction(pending.GetStableInteractionId(), pending.AnsweringPlayerId, pending.StateRevision,
                NormalizedValue.CreateString(pending.CandidateIds[0]), out var reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == PreparationWindowFlow.StepTypeId).Select(node => node.EffectId), Is.EqualTo(stepsBefore),
                "恢复后提交企业选择不能重新执行此前的模式步骤");
        }
    }
}
