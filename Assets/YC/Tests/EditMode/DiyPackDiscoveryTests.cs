using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Infrastructure.Lua;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    public sealed class DiyPackDiscoveryTests
    {
        private static readonly Lazy<GameplayOptionsCatalog> CoreOptionsFixture = new Lazy<GameplayOptionsCatalog>(() =>
            GameplayOptionsCatalog.CoreOnly(ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"))));
        private static string NewRoot() => Path.GetFullPath(Path.Combine("TestResults", "DIY-pack-fixtures", Guid.NewGuid().ToString("N")));
        private static JObject ModeManifest(string version = "1.0.0") => new JObject
        {
            ["format"] = "nomadcity.diy", ["schemaVersion"] = 1, ["packId"] = "demo.modes", ["version"] = version,
            ["displayName"] = "模式夹具", ["author"] = "测试作者", ["description"] = "不携带新牌的模式", ["contentApiVersion"] = 1,
            ["requiredCapabilities"] = new JArray("diy.pack.v1", "diy.rule_modes.v1"),
            ["dependencies"] = new JArray(new JObject { ["packId"] = "core", ["versionRange"] = ">=2.2.0 <3.0.0" }),
            ["ruleModes"] = new JArray(new JObject { ["modeId"] = "demo.modes.expedition", ["displayName"] = "探索", ["description"] = "复用基础配方",
                ["requires"] = new JArray("core"), ["requiredCapabilities"] = new JArray(), ["applicableMapIds"] = new JArray("map-three-players"),
                ["setupRecipeRefs"] = new JArray(new JObject { ["packId"] = "core", ["recipeId"] = "standard" }) }),
            ["definitions"] = new JArray(), ["setupRecipes"] = new JArray()
        };
        private static void Write(string root, string relative, string text)
        {
            string target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target, text, new UTF8Encoding(false));
        }
        private static string MakeMode(string dataRoot, string version = "1.0.0")
        {
            string root = Path.Combine(dataRoot, "Mods", "demo.modes", version);
            Write(root, "pack.json", ModeManifest(version).ToString()); return root;
        }
        private static string MakeEventPack(string dataRoot, string coreVersionRange = ">=2.2.0 <3.0.0")
        {
            string root = Path.Combine(dataRoot, "Mods", "demo.events", "1.0.0");
            var pack = new JObject { ["format"] = "nomadcity.diy", ["schemaVersion"] = 1, ["packId"] = "demo.events", ["version"] = "1.0.0",
                ["displayName"] = "事件夹具", ["author"] = "测试作者", ["description"] = "扩展事件", ["contentApiVersion"] = 1,
                ["requiredCapabilities"] = new JArray("diy.pack.v1", "event.choices.v1"),
                ["dependencies"] = new JArray(new JObject { ["packId"] = "core", ["versionRange"] = coreVersionRange }),
                ["expansions"] = new JArray(new JObject { ["expansionId"] = "demo.events.standard", ["displayName"] = "荒原", ["description"] = "扩展", ["requires"] = new JArray("core") }),
                ["definitions"] = new JArray("event/ruins.json"), ["setupRecipes"] = new JArray("recipes/ruins.json") };
            var eventData = new JObject { ["description"] = "夹具事件", ["color"] = 0, ["resourceType"] = 0, ["resourceAmount"] = 1,
                ["representativeResourceType"] = 0, ["representativeResourceAmount"] = 1, ["eventChoiceSchemaVersion"] = 1,
                ["choices"] = new JArray(new JObject { ["choiceId"] = "collect", ["description"] = "采集", ["button"] = new JObject { ["topPercent"] = 20, ["bottomPercent"] = 60 },
                    ["script"] = "lua/main.lua", ["version"] = "1.0.0" }) };
            var definition = new JObject { ["definitionId"] = "demo.events.ruins", ["contentType"] = "event", ["version"] = "1.0.0", ["displayName"] = "遗迹",
                ["expansionId"] = "demo.events.standard", ["replaces"] = JValue.CreateNull(), ["artwork"] = "artwork/card.png", ["abilities"] = new JArray(), ["data"] = eventData };
            var recipe = new JObject { ["schemaVersion"] = 1, ["recipeId"] = "demo.events.standard", ["expansionId"] = "demo.events.standard",
                ["applicableModes"] = new JArray("standard"), ["entries"] = new JArray(new JObject { ["entryId"] = "demo.events.ruins.main",
                    ["definitionId"] = "demo.events.ruins", ["quantity"] = 1, ["initialZoneRole"] = "event_green", ["applicableMapIds"] = new JArray("map-three-players") }) };
            Write(root, "pack.json", pack.ToString()); Write(root, "event/ruins.json", definition.ToString()); Write(root, "recipes/ruins.json", recipe.ToString());
            Write(root, "lua/main.lua", "error('加载阶段不得执行')\nreturn function(ctx) return {} end");
            string image = Path.Combine(root, "artwork", "card.png"); Directory.CreateDirectory(Path.GetDirectoryName(image));
            File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII="));
            return root;
        }

        [Test]
        public void PureModePackAndEventExpansionHaveSeparateValidatedContracts()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot); MakeEventPack(dataRoot, "=2.2.0");
            var discovered = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot));
            Assert.That(discovered, Has.Count.EqualTo(2));
            Assert.That(discovered.All(x => x.Status == DiyDiscoveryStatus.Parsed), Is.True, string.Join(";", discovered.Select(x => x.Diagnostic)));
            var mode = discovered.Single(x => x.PackId == "demo.modes").Snapshot;
            Assert.That(mode.Manifest.Definitions, Is.Empty);
            Assert.That(mode.Manifest.RuleModes.Single().SetupRecipeRefs.Single().RecipeId, Is.EqualTo("standard"));
            var expansion = discovered.Single(x => x.PackId == "demo.events").Snapshot;
            Assert.That(expansion.Recipes.Single().Entries.Single().DefinitionId, Is.EqualTo("demo.events.ruins"));
            Assert.That(expansion.Manifest.Dependencies.Single().Accepts("2.2.0"), Is.True);
            Assert.That(expansion.Manifest.Dependencies.Single().Accepts("2.2.1"), Is.False);
        }

        [Test]
        public void CoreAndDiyResolutionUseDeclaredModeCatalogRatherThanFixedModeNames()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json")));
            ((JArray)manifest["ruleModes"]).Add(new JObject { ["modeId"] = "core.fixture_mode", ["displayName"] = "目录夹具",
                ["description"] = "复用基础配方", ["requires"] = new JArray("core"), ["requiredCapabilities"] = new JArray(),
                ["setupRecipeRefs"] = new JArray(new JObject { ["packId"] = "core", ["recipeId"] = "standard" }) });
            string copy = artifacts.CopyContent(installed, "新核心模式目录", new System.Collections.Generic.Dictionary<string, string> { ["pack.json"] = manifest.ToString() });
            var core = ExternalContentPack.Load(copy);
            var options = GameplayOptionsCatalog.CoreOnly(core);
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "core.fixture_mode", MapId = "map-three-players", PlayerCount = 2,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            Assert.That(configuration.TryValidate(options, out string reason), Is.True, reason);
            var resolved = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId,
                Array.Empty<DiyPackSnapshot>(), (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, options);
            Assert.That(resolved.Packs, Is.Empty);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = Activator.CreateInstance(providerType, new object[] { (Func<ExternalContentPack>)(() => core) });
            var prepare = new object[] { configuration, null, null };
            Assert.That((bool)providerType.GetMethod("TryPrepareLock").Invoke(provider, prepare), Is.True, (string)prepare[2]);
            Assert.That((System.Collections.Generic.List<YC.Domain.Rules.ContentPackLock>)prepare[1], Is.Empty);
            var validation = new object[] { configuration, null };
            Assert.That((bool)providerType.GetMethod("TryValidate").Invoke(provider, validation), Is.True, (string)validation[1]);
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var seats = new[] { new YC.Application.Sessions.PlayerSeat { PlayerId = 1 }, new YC.Application.Sessions.PlayerSeat { PlayerId = 2 } };
            var state = YC.Application.Sessions.GameLaunchStateFactory.CreateInitialState(YC.Application.Sessions.LaunchMode.Local, 1,
                seats, configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions, null, "core.catalog.fixture", null, catalog);
            Assert.That(state.RuleConfiguration.RuleModeId, Is.EqualTo("core.fixture_mode"));
            var registry = new YC.Domain.Effects.EffectRegistry(); catalog.Register(registry);
            var archive = new YC.Application.Sessions.GameSession(state).CreateHostArchive();
            var recovery = YC.Application.Sessions.HostRecoveryService.Recover(archive, registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            configuration.RuleModeId = "core.unknown_mode";
            Assert.That(configuration.TryValidate(options, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("rule_mode_not_implemented"));
            Assert.Throws<InvalidOperationException>(() => YC.Application.Sessions.GameLaunchStateFactory.CreateInitialState(
                YC.Application.Sessions.LaunchMode.Local, 1, seats, configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions,
                null, "core.catalog.invalid", null, catalog));
        }

        [Test]
        public void SameFilesAtDifferentRootsHaveSameIdentityAndSourceBytesStayFrozen()
        {
            string first = MakeEventPack(NewRoot()), second = MakeEventPack(NewRoot());
            var a = DiyPackSnapshot.Load(first); var b = DiyPackSnapshot.Load(second);
            Assert.That(a.ContentHash, Is.EqualTo(b.ContentHash));
            byte[] returned = a.Files.ReadBytes("lua/main.lua"); returned[0] = 0;
            Write(first, "lua/main.lua", "return function(ctx) return {} end");
            Assert.That(a.Files.ReadText("lua/main.lua"), Does.StartWith("error"));
            Assert.That(DiyPackSnapshot.Load(first).ContentHash, Is.Not.EqualTo(a.ContentHash));
        }

        [Test]
        public void MalformedPackDoesNotPreventDiscoveryOfOtherPack()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot); string eventRoot = MakeEventPack(dataRoot);
            Write(eventRoot, "lua/main.lua", "return function(");
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot));
            Assert.That(found.Single(x => x.PackId == "demo.modes").Status, Is.EqualTo(DiyDiscoveryStatus.Parsed));
            var damaged = found.Single(x => x.PackId == "demo.events");
            Assert.That(damaged.Status, Is.EqualTo(DiyDiscoveryStatus.Damaged));
            Assert.That(damaged.Diagnostic, Does.Contain("lua/main.lua"));
        }

        [Test]
        public void FirstUniqueVersionCanBePinnedAndUpgradeDoesNotSwitchIt()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot);
            var paths = new DiyDataPaths(dataRoot); var profile = DiyVersionProfile.Empty();
            Assert.That(profile.BindUniqueDefaults(DiyPackDiscovery.Scan(paths)), Is.True);
            string profilePath = Path.Combine(paths.Profiles, "default.json"); profile.Save(profilePath);
            MakeMode(dataRoot, "2.0.0");
            var loaded = DiyVersionProfile.Load(profilePath); var discovered = DiyPackDiscovery.Scan(paths);
            Assert.That(loaded.BindUniqueDefaults(discovered), Is.False);
            Assert.That(loaded.Resolve("demo.modes", discovered).Version, Is.EqualTo("1.0.0"));
            loaded.SetBinding("demo.modes", "2.0.0", discovered); loaded.Save(profilePath);
            Assert.That(DiyVersionProfile.Load(profilePath).Resolve("demo.modes", discovered).Version, Is.EqualTo("2.0.0"));
            Assert.Throws<InvalidDataException>(() => DiyVersionProfile.Empty().Resolve("demo.modes", discovered));
        }

        [Test]
        public void ProfileSaveRejectsJunctionBeforeCreatingAnyDescendant()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("该夹具专门验证 Windows 目录联接。");
            string root = NewRoot(), target = Path.Combine(root, "target"), link = Path.Combine(root, "junction");
            Directory.CreateDirectory(target);
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = System.Diagnostics.Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero, output);
            }
            Assert.That(File.GetAttributes(link) & FileAttributes.ReparsePoint, Is.EqualTo(FileAttributes.ReparsePoint));
            var profile = DiyVersionProfile.Empty();
            Assert.That(Assert.Throws<InvalidDataException>(() => profile.Save(Path.Combine(link, "created-child", "default.json"))).Message, Does.Contain("链接"));
            Assert.That(Directory.Exists(Path.Combine(target, "created-child")), Is.False, "拒绝链接之前不能在目标中创建目录");
        }

        [Test]
        public void RoomExternalVersionConfigurationKeepsCurrentRepositoryFrozenUntilRestart()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot, "1.0.0");
            var paths = new DiyDataPaths(dataRoot); var found = DiyPackDiscovery.Scan(paths);
            var current = DiyVersionProfile.Empty(); current.BindUniqueDefaults(found);
            current.Save(Path.Combine(paths.Profiles, "default.json")); MakeMode(dataRoot, "2.0.0");
            found = DiyPackDiscovery.Scan(paths);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var active = new DiyInstalledRepository(core, found, current);
            var local = new DiyLocalContentConfiguration(paths);
            Assert.That(local.Inspect().Single(x => x.IsBound).Version, Is.EqualTo("1.0.0"));
            Assert.That(local.EnsureModsDirectory(), Is.EqualTo(paths.Mods));
            local.SetNextLaunchBinding("demo.modes", "2.0.0");
            Assert.That(local.Inspect().Single(x => x.IsBound).Version, Is.EqualTo("2.0.0"));
            Assert.That(active.GetOptions().Modes.Single(x => x.Id == "demo.modes.expedition").PackVersion, Is.EqualTo("1.0.0"));
            var restarted = new DiyInstalledRepository(core, found, DiyVersionProfile.Load(Path.Combine(paths.Profiles, "default.json")));
            Assert.That(restarted.GetOptions().Modes.Single(x => x.Id == "demo.modes.expedition").PackVersion, Is.EqualTo("2.0.0"));
            Assert.Throws<InvalidDataException>(() => local.SetNextLaunchBinding("demo.modes", "9.9.9"));
            Assert.That(local.Inspect().Single(x => x.IsBound).Version, Is.EqualTo("2.0.0"));
        }

        [Test]
        public void NewGamePreferenceStoresSelectionButNeverRoomLocksOrContentHash()
        {
            var local = new DiyLocalContentConfiguration(new DiyDataPaths(NewRoot()));
            Assert.That(local.LoadNewGamePreference(), Is.Null);
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { MapId = "map-four-players", RuleModeId = "demo.modes.expedition", PlayerCount = 4,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "demo.events.standard" }, ContentPackHash = new string('a', 64),
                LockedContentPacks = new System.Collections.Generic.List<YC.Domain.Rules.ContentPackLock> { new YC.Domain.Rules.ContentPackLock {
                    PackId = "core", Version = "2.2.0", ContentHash = new string('b', 64) } } };
            local.SaveNewGamePreference(configuration);
            var restored = local.LoadNewGamePreference();
            Assert.That(restored.MapId, Is.EqualTo(configuration.MapId));
            Assert.That(restored.RuleModeId, Is.EqualTo(configuration.RuleModeId));
            Assert.That(restored.EnabledExpansionIds, Is.EquivalentTo(configuration.EnabledExpansionIds));
            Assert.That(restored.ContentPackHash, Is.Empty);
            Assert.That(restored.LockedContentPacks, Is.Empty);
            configuration.EnabledExpansionIds.Add("core");
            Assert.Throws<InvalidDataException>(() => local.SaveNewGamePreference(configuration));
            Assert.That(local.LoadNewGamePreference().EnabledExpansionIds, Is.EquivalentTo(new[] { "core", "demo.events.standard" }));
        }

        [Test]
        public void NewGamePreferencePreservesMultipleModesWithoutCopyingRoomLocks()
        {
            var local = new DiyLocalContentConfiguration(new DiyDataPaths(NewRoot()));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.first.mode",
                AdditionalRuleModeIds = new System.Collections.Generic.List<string> { "demo.second.mode" },
                OfficialPreparationContract = YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion };
            local.SaveNewGamePreference(configuration);
            var restored = local.LoadNewGamePreference();
            Assert.That(restored.ActiveRuleModeIds, Is.EqualTo(new[] { "demo.first.mode", "demo.second.mode" }));
            Assert.That(restored.OfficialPreparationContract, Is.EqualTo(YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion));
            Assert.That(restored.LockedContentPacks, Is.Empty);
        }

        [Test]
        public void UnavailableSavedGameplayBlocksStartUntilHostChoosesAgain()
        {
            var paths = new DiyDataPaths(NewRoot()); var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, DiyPackDiscovery.Scan(paths), DiyVersionProfile.Empty());
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var providerField = serviceType.GetField("contentCapabilities", statics); var optionsField = runtimeType.GetField("gameplayOptions", statics);
            object oldProvider = providerField.GetValue(null), oldOptions = optionsField.GetValue(null); UnityEngine.GameObject owner = null;
            try
            {
                providerField.SetValue(null, provider); optionsField.SetValue(null, repository.GetOptions());
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    var room = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                    var added = service.AddLocalPlayerAsync(new YC.Infrastructure.Multiplayer.AddLocalRoomPlayerRequest { CommandId = Guid.NewGuid().ToString("N"),
                        RoomId = room.RoomId, RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision,
                        SeatIndex = 1, PlayerName = "本地玩家" }).GetAwaiter().GetResult();
                    Assert.That(added.Success, Is.True, added.ReasonCode); room = service.GetCurrentRoom();
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
                    owner = UnityEngine.Object.Instantiate(asset);
                    var controllerType = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", true);
                    var panelType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
                    var controller = owner.GetComponent(controllerType); var panel = owner.GetComponentsInChildren(panelType, true).Single();
                    controllerType.GetField("roomService", instance).SetValue(controller, service);
                    controllerType.GetField("pendingSavedGameplayError", instance).SetValue(controller, "夹具缺少锁定包");
                    controllerType.GetField("pendingSavedGameplayRoomId", instance).SetValue(controller, room.RoomId);
                    controllerType.GetMethod("ShowRoomPanel", instance).Invoke(controller, new object[] { room, true });
                    var start = (UnityEngine.UI.Button)panelType.GetProperty("StartButton").GetValue(panel);
                    Assert.That((bool)panelType.GetProperty("CanStartCurrentRoom").GetValue(panel), Is.True);
                    Assert.That(start.interactable, Is.False);
                    Assert.That(((UnityEngine.UI.Text)panelType.GetProperty("StatusText").GetValue(panel)).text, Does.Contain("夹具缺少锁定包"));
                    start.onClick.Invoke(); Assert.That(service.GetCurrentRoom().HasStarted, Is.False);
                    var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => controllerType.GetMethod("InspectLocalDiyContent").Invoke(controller, null));
                    Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                }
            }
            finally
            {
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                providerField.SetValue(null, oldProvider); optionsField.SetValue(null, oldOptions);
            }
        }

        [TestCase("../card.png")] [TestCase("/card.png")] [TestCase("a\\b.png")] [TestCase("C:/card.png")]
        [TestCase("a/../card.png")] [TestCase("a//card.png")] [TestCase("NUL.txt")] [TestCase("a/card.png.")]
        public void RejectsUnsafePortablePaths(string path) => Assert.Throws<InvalidDataException>(() => DiyPackPaths.ValidateRelative(path));

        [Test]
        public void DuplicateJsonUnknownFieldsAndInvalidRangesAreDiagnosed()
        {
            string root = MakeMode(NewRoot());
            Write(root, "pack.json", "{\"format\":\"nomadcity.diy\",\"format\":\"nomadcity.diy\"}");
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyPackSnapshot.Load(root)).Message, Does.Contain("pack.json"));
            var manifest = ModeManifest(); manifest["unexpected"] = true;
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest)).Message, Does.Contain("unexpected"));
            manifest.Remove("unexpected"); manifest["dependencies"][0]["versionRange"] = ">=3.0.0 <2.0.0";
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest)).Message, Does.Contain("versionRange"));
        }

        [Test]
        public void UnselectedPacksDoNotChangeTheResolvedLock()
        {
            var mode = DiyPackSnapshot.Load(MakeMode(NewRoot()));
            var events = DiyPackSnapshot.Load(MakeEventPack(NewRoot()));
            string[] caps = { "diy.pack.v1", "diy.rule_modes.v1", "event.choices.v1" };
            var empty = DiyContentResolution.Resolve("standard", new[] { "core" }, "map-three-players", Array.Empty<DiyPackSnapshot>(), "2.2.0", "core-hash", caps, CoreOptionsFixture.Value);
            var extra = DiyContentResolution.Resolve("standard", new[] { "core" }, "map-three-players", new[] { mode, events }, "2.2.0", "core-hash", caps, CoreOptionsFixture.Value);
            Assert.That(extra.Packs, Is.Empty);
            Assert.That(extra.LockHash, Is.EqualTo(empty.LockHash));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players", new[] { mode, events }, "2.2.0", "core-hash", caps, CoreOptionsFixture.Value);
            Assert.That(selected.Packs.Select(x => x.PackId), Is.EqualTo(new[] { "demo.modes" }));
            Assert.That(selected.LockHash, Is.Not.EqualTo(empty.LockHash));
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-four-players", new[] { mode }, "2.2.0", "core-hash", caps, CoreOptionsFixture.Value)).Message, Does.Contain("地图"));
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players", new[] { mode }, "3.0.0", "core-hash", caps, CoreOptionsFixture.Value)).Message, Does.Contain("版本"));
        }

        [Test]
        public void SameRelativeScriptNameInTwoPacksKeepsItsSourceAndIdentity()
        {
            string firstRoot = MakeEventPack(NewRoot()), secondRoot = MakeEventPack(NewRoot());
            foreach (string relative in new[] { "pack.json", "event/ruins.json", "recipes/ruins.json" })
            {
                string path = Path.Combine(secondRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Write(secondRoot, relative, File.ReadAllText(path).Replace("demo.events", "demo.other"));
            }
            Write(secondRoot, "lua/main.lua", "return function(ctx) return {} end");
            var first = DiyPackSnapshot.Load(firstRoot); var second = DiyPackSnapshot.Load(secondRoot);
            var resolved = DiyContentResolution.Resolve("standard", new[] { "core", "demo.other.standard", "demo.events.standard" },
                "map-three-players", new[] { second, first }, "2.2.0", "core-hash", new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            Assert.That(resolved.Packs.Select(x => x.PackId), Is.EqualTo(new[] { "demo.events", "demo.other" }));
            Assert.That(first.Files.ReadText("lua/main.lua"), Does.StartWith("error"));
            Assert.That(second.Files.ReadText("lua/main.lua"), Does.StartWith("return"));
            Assert.That(first.ContentHash, Is.Not.EqualTo(second.ContentHash));
        }

        [Test]
        public void DependencyOrderIsStableAndCyclesFailWithoutActivatingContent()
        {
            string zRoot = MakeMode(NewRoot()), aRoot = MakeMode(NewRoot()), bRoot = MakeMode(NewRoot());
            foreach (var item in new[] { (zRoot, "demo.z"), (aRoot, "demo.a"), (bRoot, "demo.b") })
            {
                string path = Path.Combine(item.Item1, "pack.json");
                Write(item.Item1, "pack.json", File.ReadAllText(path).Replace("demo.modes", item.Item2));
            }
            var z = JObject.Parse(File.ReadAllText(Path.Combine(zRoot, "pack.json")));
            ((JArray)z["dependencies"]).Add(new JObject { ["packId"] = "demo.b", ["versionRange"] = "=1.0.0" });
            ((JArray)z["dependencies"]).Add(new JObject { ["packId"] = "demo.a", ["versionRange"] = "=1.0.0" });
            Write(zRoot, "pack.json", z.ToString());
            var packs = new[] { DiyPackSnapshot.Load(zRoot), DiyPackSnapshot.Load(bRoot), DiyPackSnapshot.Load(aRoot) };
            var resolved = DiyContentResolution.Resolve("demo.z.expedition", new[] { "core" }, "map-three-players", packs,
                "2.2.0", "core-hash", new[] { "diy.pack.v1", "diy.rule_modes.v1" }, CoreOptionsFixture.Value);
            Assert.That(resolved.Packs.Select(x => x.PackId), Is.EqualTo(new[] { "demo.a", "demo.b", "demo.z" }));
            var a = JObject.Parse(File.ReadAllText(Path.Combine(aRoot, "pack.json")));
            ((JArray)a["dependencies"]).Add(new JObject { ["packId"] = "demo.z", ["versionRange"] = "=1.0.0" }); Write(aRoot, "pack.json", a.ToString());
            packs[2] = DiyPackSnapshot.Load(aRoot);
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("demo.z.expedition", new[] { "core" },
                "map-three-players", packs, "2.2.0", "core-hash", new[] { "diy.pack.v1", "diy.rule_modes.v1" }, CoreOptionsFixture.Value)).Message, Does.Contain("循环"));
        }

        [Test]
        public void CompositeKeepsIndependentSourcesAndAppendsEventsOnlyToDeclaredMap()
        {
            string firstRoot = MakeEventPack(NewRoot()), secondRoot = MakeEventPack(NewRoot());
            foreach (string relative in new[] { "pack.json", "event/ruins.json", "recipes/ruins.json" })
            {
                string path = Path.Combine(secondRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Write(secondRoot, relative, File.ReadAllText(path).Replace("demo.events", "demo.other"));
            }
            Write(secondRoot, "lua/main.lua", "return function(ctx) return {} end");
            var first = DiyPackSnapshot.Load(firstRoot); var second = DiyPackSnapshot.Load(secondRoot);
            var resolved = DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard", "demo.other.standard" }, "map-three-players",
                new[] { first, second }, "2.2.0", "core-hash", new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var composite = DiyCompositeCatalog.Build(core, resolved);
            var one = composite.Definitions.Single(x => x.DefinitionId == "demo.events.ruins");
            var two = composite.Definitions.Single(x => x.DefinitionId == "demo.other.ruins");
            Assert.That(composite.ReadScript(one, "lua/main.lua"), Does.StartWith("error"));
            Assert.That(composite.ReadScript(two, "lua/main.lua"), Does.StartWith("return"));
            Assert.That(one.SourcePackId, Is.EqualTo("demo.events"));
            var changed = one.Definition; changed.Data["description"] = "外部修改";
            Assert.That((string)one.Definition.Data["description"], Is.EqualTo("夹具事件"));
            Assert.That(composite.BuildEventAdditions("map-three-players").Select(x => x.DefinitionId), Is.EquivalentTo(new[] { "demo.events.ruins", "demo.other.ruins" }));
            Assert.That(composite.BuildEventAdditions("map-four-players"), Is.Empty);
        }

        [Test]
        public void FormalLoaderReceivesFrozenSourcesWithoutPathCollisions()
        {
            string firstRoot = MakeEventPack(NewRoot()), secondRoot = MakeEventPack(NewRoot());
            foreach (string relative in new[] { "pack.json", "event/ruins.json", "recipes/ruins.json" })
            {
                string path = Path.Combine(secondRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Write(secondRoot, relative, File.ReadAllText(path).Replace("demo.events", "demo.other"));
            }
            Write(secondRoot, "lua/main.lua", "return function(ctx) return {} end");
            var first = DiyPackSnapshot.Load(firstRoot); var second = DiyPackSnapshot.Load(secondRoot);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var resolved = DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard", "demo.other.standard" }, "map-three-players",
                new[] { first, second }, (string)core.Manifest["version"], core.ContentHash, new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            var combined = ExternalContentPack.ComposeDiy(core, resolved);
            var one = combined.FindDefinition("demo.events.ruins", "event"); var two = combined.FindDefinition("demo.other.ruins", "event");
            string onePath = (string)one.Data["choices"][0]["script"], twoPath = (string)two.Data["choices"][0]["script"];
            Assert.That(onePath, Is.Not.EqualTo(twoPath));
            Assert.That(combined.GetScript(onePath), Does.StartWith("error"));
            Assert.That(combined.GetScript(twoPath), Does.StartWith("return"));
            Assert.That(combined.GetDefinitionSourcePackId(one.DefinitionId), Is.EqualTo("demo.events"));
            Assert.That(combined.GetScriptEntryId(onePath), Is.EqualTo(YC.Domain.State.StableIdFactory.Create("lua-script-entry", "demo.events", "lua/main.lua")));
            Assert.That(combined.GetArtworkBytes(one.Artwork), Is.EqualTo(first.Files.ReadBytes("artwork/card.png")));
            Assert.That(core.FindDefinition("demo.events.ruins"), Is.Null);
        }

        [Test]
        public void FormalCatalogAddsSelectedEventToTheDeclaredDeck()
        {
            string root = MakeEventPack(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray("event.choices.v1"); Write(root, "pack.json", manifest.ToString());
            var source = DiyPackSnapshot.Load(root);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "standard", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "demo.events.standard" } };
            var selected = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, selected);
            Assert.That(catalog.Definitions.Any(x => x.DefinitionId == "demo.events.ruins"), Is.True);
            var entry = catalog.SetupRecipe.Entries.Single(x => x.EntryId == "demo.events.ruins.main");
            Assert.That(entry.DefinitionId, Is.EqualTo("demo.events.ruins"));
            Assert.That(entry.InitialZoneRole, Is.EqualTo("event_green"));
            Assert.That(entry.Quantity, Is.EqualTo(1));
            Assert.That(catalog.ContentDefinitions.Events.Any(x => x.CardId == "demo.events.ruins"), Is.True);
            Assert.That(catalog.Scripts.Values.Any(x => x.StartsWith("error", StringComparison.Ordinal)), Is.True);
            var state = CreateInitialStateFixture(catalog, configuration);
            Assert.That(state.ItemZones.Items.Any(x => x.DefinitionId == "demo.events.ruins"), Is.True);
        }

        [Test]
        public void FormalCatalogCanReuseStandardRecipeForPureConfigurationMode()
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray(); Write(root, "pack.json", manifest.ToString());
            var source = DiyPackSnapshot.Load(root);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            var selected = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, selected);
            Assert.That(catalog.RuleModeId, Is.EqualTo("demo.modes.expedition"));
            Assert.That(catalog.SetupRecipe.Entries.Any(x => x.DefinitionId == "event_green_01"), Is.True);
            Assert.That(catalog.ContentPackHash, Is.Not.Empty);
            var state = CreateInitialStateFixture(catalog, configuration);
            Assert.That(state.RuleConfiguration.RuleModeId, Is.EqualTo("demo.modes.expedition"));
            Assert.That(state.RuleConfiguration.LockedContentPacks, Has.Count.EqualTo(2));
            Assert.That(catalog.Presentation.MatchesConfiguration(configuration, catalog.ContentPackHash, core.ContentHash), Is.True);
            var alteredLock = configuration.Clone(); alteredLock.LockedContentPacks.Single(x => x.PackId == "demo.modes").ContentHash = new string('f', 64);
            Assert.That(catalog.Presentation.MatchesConfiguration(alteredLock, catalog.ContentPackHash, core.ContentHash), Is.False,
                "活动指纹相同也不能让被篡改的包锁定复用展示绑定");
            var archive = new YC.Application.Sessions.GameSession(state).CreateHostArchive();
            var recovered = YC.Application.Sessions.HostRecoveryService.Recover(archive);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var registry = new YC.Domain.Effects.EffectRegistry(); catalog.Register(registry);
            recovered = YC.Application.Sessions.HostRecoveryService.Recover(archive, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            archive.Snapshot.State.RuleConfiguration.LockedContentPacks.Single(x => x.PackId == "demo.modes").ContentHash = new string('f', 64);
            recovered = YC.Application.Sessions.HostRecoveryService.Recover(archive, registry);
            Assert.That(recovered.Succeeded, Is.False, "冻结目录不得接受被篡改的包内容哈希");
            archive.Snapshot.State.RuleConfiguration.LockedContentPacks.Clear();
            recovered = YC.Application.Sessions.HostRecoveryService.Recover(archive);
            Assert.That(recovered.Succeeded, Is.False, "DIY 存档缺少原始锁定时不能按本机默认继续");
        }

        [TestCase("仅模式", "example.basic.mode", "")]
        [TestCase("仅扩展", "standard", "example.cleanup.rules")]
        [TestCase("模式和扩展", "example.mixed.mode", "example.mixed.rules")]
        [TestCase("事件试验", "standard", "example.events.green")]
        [TestCase("完成事件模式", "example.completed.mode", "")]
        public void DistributedExamplesComposeWithTheFormalGameCatalog(string directory, string mode, string expansion)
        {
            string project = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            var source = DiyPackSnapshot.Load(Path.Combine(project, "tools", "packaging", "examples", directory));
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var enabled = new System.Collections.Generic.List<string> { "core" };
            if (!string.IsNullOrEmpty(expansion)) enabled.Add(expansion);
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = mode, MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = enabled };
            var selected = DiyContentResolution.Resolve(mode, enabled, configuration.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, selected);
            var state = CreateInitialStateFixture(catalog, configuration);
            Assert.That(state.RuleConfiguration.RuleModeId, Is.EqualTo(mode));
            Assert.That(state.RuleConfiguration.LockedContentPacks.Any(item => item.PackId == source.Manifest.PackId), Is.True);
            Assert.That(catalog.SetupRecipe.Entries.Any(item => item.DefinitionId == (directory == "事件试验" ? "example.events.dream" : "event_green_01")), Is.True);
            if (directory == "事件试验")
            {
                Assert.That(catalog.SetupRecipe.Entries.Count(item => item.DefinitionId == "example.events.dream"), Is.EqualTo(1));
                Assert.That(catalog.SetupRecipe.Entries.Count(item => item.DefinitionId == "example.events.support"), Is.EqualTo(1));
                var replacement = catalog.Definitions.Single(item => item.RuntimeId == "event_green_01");
                Assert.That(replacement.DefinitionId, Is.EqualTo("example.events.dream"));
                var added = catalog.Definitions.Single(item => item.DefinitionId == "example.events.support");
                Assert.That(added.ArtworkSlice.X, Is.EqualTo(850)); Assert.That(added.ArtworkSlice.Y, Is.EqualTo(600));
                Assert.That(replacement.ArtworkSlice.X, Is.EqualTo(1700)); Assert.That(replacement.ArtworkSlice.Width, Is.EqualTo(850));
                Assert.That(catalog.ContentDefinitions.Events.Single(item => item.CardId == "example.events.dream").Choices.Select(item => item.ChoiceId),
                    Is.EqualTo(new[] { "first", "second" }));
            }
        }

        [Test]
        public void ModeAndExpansionCanReferenceSameCoreContributionOnce()
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray();
            ((JArray)manifest["ruleModes"][0]["requires"]).Add("enterprise");
            ((JArray)manifest["ruleModes"][0]["setupRecipeRefs"]).Add(new JObject { ["packId"] = "core", ["recipeId"] = "enterprise" });
            Write(root, "pack.json", manifest.ToString());
            var source = DiyPackSnapshot.Load(root);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var config = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "enterprise" } };
            var selected = DiyContentResolution.Resolve(config.RuleModeId, config.EnabledExpansionIds, config.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            config.LockedContentPacks = selected.CreateRuleLocks();
            var recipe = ActiveContentCatalogBuilder.Build(core, config, selected).SetupRecipe;
            Assert.That(recipe.InitialBasicCharacterCount, Is.EqualTo(7));
            Assert.That(recipe.EnterpriseEntrance, Is.True);
            Assert.That(recipe.Entries.Count(x => x.DefinitionId == "mountain"), Is.EqualTo(1));
        }

        [Test]
        public void DiyModeCanReuseDeclaredEntrepreneurMechanismAndItsCoreContent()
        {
            string dataRoot = NewRoot(), root = MakeMode(dataRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["ruleModes"][0]["mechanisms"] = new JArray("entrepreneur_selection.v1");
            ((JArray)manifest["ruleModes"][0]["setupRecipeRefs"]).Add(new JObject { ["packId"] = "core", ["recipeId"] = "entrepreneur" });
            Write(root, "pack.json", manifest.ToString());
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var options = repository.GetOptions();
            Assert.That(options.TryPlanModeTransition("demo.modes.expedition", "map-three-players", new[] { "core" }, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.RequiredExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise", "infected" }));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = plan.ExpansionIds.ToList() };
            configuration.OfficialPreparationContract = YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion;
            var selected = repository.ResolveLocal(configuration); configuration.LockedContentPacks = selected.CreateRuleLocks();
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, selected);
            Assert.That(catalog.SetupRecipe.Mechanisms, Does.Contain("entrepreneur_selection.v1"));
            Assert.That(catalog.SetupRecipe.Preparation.Enterprise, Is.Not.Null);
            Assert.That(catalog.SetupRecipe.Preparation.BetweenDistributionAndConfirmation.Single().HandlerId,
                Is.EqualTo(YC.Domain.Rules.EntrepreneurFlow.PreparationHandlerId));
            Assert.That(catalog.ContentDefinitions.Entrepreneurs.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(catalog.SetupRecipe.Entries.Any(x => x.DefinitionId == "entrepreneur.round.A.1"), Is.True);
            var state = CreateInitialStateFixture(catalog, configuration);
            Assert.That(YC.Domain.Rules.EntrepreneurFlow.Enabled(state), Is.True);
            Assert.That(state.RuleConfiguration.RuleModeId, Is.EqualTo("demo.modes.expedition"));

            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var coreField = runtimeType.GetField("pack", statics); var repoField = runtimeType.GetField("diyRepository", statics);
            var providerField = serviceType.GetField("contentCapabilities", statics); var contextField = contextType.GetField("<Instance>k__BackingField", statics);
            object oldCore = coreField.GetValue(null), oldRepo = repoField.GetValue(null), oldProvider = providerField.GetValue(null), oldContext = contextField.GetValue(null);
            var owner = new UnityEngine.GameObject("DIY 企业家机制正式入口隔离夹具");
            try
            {
                coreField.SetValue(null, core); repoField.SetValue(null, repository);
                providerField.SetValue(null, Activator.CreateInstance(providerType,
                    new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) }));
                contextField.SetValue(null, null); object context = owner.AddComponent(contextType);
                var players = Enumerable.Range(1, 3).Select(i => new YC.Application.Sessions.PlayerSeat { PlayerId = i, PlayerName = "玩家" + i,
                    Color = (YC.Domain.Rules.PlayerColor)(i - 1) }).ToList();
                contextType.GetMethod("Configure", new[] { typeof(YC.Application.Sessions.LaunchMode), typeof(int), typeof(string),
                    typeof(System.Collections.Generic.IList<YC.Application.Sessions.PlayerSeat>), typeof(YC.Domain.Rules.GameRuleConfiguration) })
                    .Invoke(context, new object[] { YC.Application.Sessions.LaunchMode.Local, 1, "fixture", players, configuration });
                object built = bootstrapType.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                    new[] { contextType, typeof(bool), typeof(bool) }, null).Invoke(null, new object[] { context, false, false });
                var session = (YC.Application.Sessions.GameSession)built.GetType().GetField("Session").GetValue(built);
                var registry = (YC.Domain.Effects.EffectRegistry)built.GetType().GetField("EffectRegistry").GetValue(built);
                Assert.That(session.State.EntrepreneurSetup?.Stage, Is.EqualTo("preview"));
                Assert.That(session.State.PreparationRuntime?.Stage, Is.EqualTo("middle"));
                Assert.That(session.State.EffectRuntime.InteractionRequests.Count(x => x.Status == "open" &&
                    x.InteractionTypeId == YC.Domain.Rules.EntrepreneurFlow.PreviewInteraction), Is.EqualTo(3));
                var recovered = YC.Application.Sessions.HostRecoveryService.Recover(session.CreateHostArchive(), registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                Assert.That(recovered.State.EntrepreneurSetup.Stage, Is.EqualTo("preview"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                coreField.SetValue(null, oldCore); repoField.SetValue(null, oldRepo); providerField.SetValue(null, oldProvider); contextField.SetValue(null, oldContext);
            }
            var missing = configuration.Clone(); missing.EnabledExpansionIds.Remove("infected"); missing.LockedContentPacks.Clear(); missing.ContentPackHash = "";
            var unresolved = repository.ResolveLocal(missing); missing.LockedContentPacks = unresolved.CreateRuleLocks();
            Assert.That(Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(core, missing, unresolved)).Message,
                Does.Contain("rule_mechanism_dependency_missing"));
        }

        [Test]
        public void ActiveFingerprintIncludesSelectedMechanismDeclaration()
        {
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var cfg = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "entrepreneur", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "enterprise", "infected" } };
            string original = ActiveContentCatalogBuilder.Build(core, cfg).ContentPackHash;
            var manifest = (JObject)typeof(ExternalContentPack).GetField("manifestSnapshot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(core);
            var mechanism = ((JArray)manifest["mechanismDeclarations"]).OfType<JObject>().Single(x => (string)x["mechanismId"] == "entrepreneur_selection.v1");
            ((JArray)mechanism["requires"]).Add("core"); // core 已启用，规则行为不变，但声明身份发生变化。
            string changed = ActiveContentCatalogBuilder.Build(core, cfg).ContentPackHash;
            Assert.That(changed, Is.Not.EqualTo(original));
        }

        [Test]
        public void ApplicableModeMustExistInTheDeclaringPacksDependencyClosure()
        {
            string dataRoot = NewRoot(), modeRoot = MakeMode(dataRoot), eventRoot = MakeEventPack(dataRoot);
            var eventManifest = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "pack.json")));
            eventManifest["expansions"][0]["applicableModes"] = new JArray("standard", "demo.modes.expedition");
            Write(eventRoot, "pack.json", eventManifest.ToString());
            var mode = DiyPackSnapshot.Load(modeRoot); var events = DiyPackSnapshot.Load(eventRoot);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            DiyResolvedContent Resolve(DiyPackSnapshot selected) => DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard" },
                "map-three-players", new[] { mode, selected }, (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            Assert.That(Assert.Throws<InvalidDataException>(() => Resolve(events)).Message, Does.Contain("模式"));
            ((JArray)eventManifest["dependencies"]).Add(new JObject { ["packId"] = "demo.modes", ["versionRange"] = "=1.0.0" });
            Write(eventRoot, "pack.json", eventManifest.ToString());
            Assert.DoesNotThrow(() => Resolve(DiyPackSnapshot.Load(eventRoot)));
            var recipe = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "recipes", "ruins.json")));
            recipe["entries"][0]["applicableMapIds"] = new JArray("missing-map");
            Write(eventRoot, "recipes/ruins.json", recipe.ToString());
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyPackSnapshot.Load(eventRoot)).Message, Does.Contain("未知地图"));
        }

        [Test]
        public void DiscoveryUsesHostImageDecoderAndKeepsBadPackIsolated()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot); string eventRoot = MakeEventPack(dataRoot);
            var source = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
            byte[] image;
            try
            {
                source.SetPixels(new[] { UnityEngine.Color.white, UnityEngine.Color.red, UnityEngine.Color.blue, UnityEngine.Color.black });
                source.Apply(); image = UnityEngine.ImageConversion.EncodeToPNG(source);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
            string path = Path.Combine(eventRoot, "artwork", "card.png"); File.WriteAllBytes(path, image);
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var method = runtime.GetMethod("ValidateDiyImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var decoder = (Action<byte[], string, int, int>)Delegate.CreateDelegate(typeof(Action<byte[], string, int, int>), method);
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot), decoder);
            Assert.That(found.Single(x => x.PackId == "demo.events").Status, Is.EqualTo(DiyDiscoveryStatus.Parsed));
            // 保留 PNG 头和尺寸，截掉其余字节；仅检查头部时会误判为合法。
            File.WriteAllBytes(path, image.Take(24).ToArray());
            bool previous = UnityEngine.TestTools.LogAssert.ignoreFailingMessages;
            try
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true; // 本用例有意让解码器接收损坏图片。
                found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot), decoder);
            }
            finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = previous; }
            Assert.That(found.Single(x => x.PackId == "demo.events").Status, Is.EqualTo(DiyDiscoveryStatus.Damaged));
            Assert.That(found.Single(x => x.PackId == "demo.events").Diagnostic, Does.Contain("图片无法完整解码"));
            Assert.That(found.Single(x => x.PackId == "demo.modes").Status, Is.EqualTo(DiyDiscoveryStatus.Parsed));
        }

        [Test]
        public void UnifiedGameplayOptionsPlanRequiredAndIncompatibleExpansions()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot); string eventRoot = MakeEventPack(dataRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "pack.json")));
            manifest["expansions"][0]["applicableModes"] = new JArray("standard");
            Write(eventRoot, "pack.json", manifest.ToString());
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var options = new DiyInstalledRepository(core, found, profile).GetOptions();
            Assert.That(options.Modes.Select(x => x.Id), Is.EquivalentTo(new[] { "standard", "entrepreneur", "demo.modes.expedition" }));
            Assert.That(options.Expansions.Select(x => x.Id), Is.EquivalentTo(new[] { "core", "enterprise", "infected", "demo.events.standard" }));
            Assert.That(options.Modes.Single(x => x.Id == "demo.modes.expedition").PackId, Is.EqualTo("demo.modes"));
            Assert.That(options.TryPlanModeTransition("entrepreneur", "map-three-players", new[] { "core", "demo.events.standard" }, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.ExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise", "infected" }));
            Assert.That(plan.RequiredExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise", "infected" }));
            Assert.That(plan.RemovedExpansionIds, Is.EqualTo(new[] { "demo.events.standard" }));
            Assert.That(options.TryPlanModeTransition("demo.modes.expedition", "map-four-players", new[] { "core" }, out _, out reason), Is.False);
            Assert.That(reason, Does.Contain("地图"));
        }

        [Test]
        public void UnavailableModeReportsCapabilityWithoutHidingBuiltInOptions()
        {
            string dataRoot = NewRoot(), root = MakeMode(dataRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray("not.implemented.v1"); Write(root, "pack.json", manifest.ToString());
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var options = new DiyInstalledRepository(core, found, profile).GetOptions();
            Assert.That(options.Modes.Single(x => x.Id == "standard").Available, Is.True);
            Assert.That(options.Modes.Single(x => x.Id == "demo.modes.expedition").Available, Is.False);
            Assert.That(options.TryPlanModeTransition("demo.modes.expedition", "map-three-players", new[] { "core" }, out _, out string reason), Is.False);
            Assert.That(reason, Does.Contain("not.implemented.v1"));
        }

        [Test]
        public void FixtureContentRootCannotOverlapTheNormalUserDataRoot()
        {
            string root = NewRoot(), normal = Path.Combine(root, "normal"), fixture = Path.Combine(root, "fixture");
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var select = runtime.GetMethod("SelectFixtureDataRoot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            string Resolve(params string[] arguments) => (string)select.Invoke(null, new object[] { normal, arguments });
            Assert.That(Resolve(), Is.EqualTo(normal));
            Assert.That(Resolve("--yc-test-content-root=" + fixture), Is.EqualTo(Path.GetFullPath(fixture)));
            foreach (var args in new[] { new[] { "--yc-test-content-root=" + normal }, new[] { "--yc-test-content-root=" + Path.Combine(normal, "Mods") },
                new[] { "--yc-test-content-root=relative" }, new[] { "--yc-test-content-root=" + fixture, "--yc-test-content-root=" + fixture } })
                Assert.Throws<System.Reflection.TargetInvocationException>(() => Resolve(args));
            Assert.That(Directory.Exists(root), Is.False, "解析不得创建用户数据或测试目录");
        }

        [Test]
        public void ExistingRoomControlsLockRequiredExpansionsAfterModeTransition()
        {
            string root = NewRoot(); var found = DiyPackDiscovery.Scan(new DiyDataPaths(root)); var profile = DiyVersionProfile.Empty();
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var options = repository.GetOptions();
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var providerField = serviceType.GetField("contentCapabilities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object previous = providerField.GetValue(null); UnityEngine.GameObject rootView = null;
            try
            {
                providerField.SetValue(null, provider);
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    var room = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                    var panelType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
                    rootView = UnityEngine.Object.Instantiate(asset);
                    var panel = rootView.GetComponentsInChildren(panelType, true).Single();
                    panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                    panelType.GetMethod("SetNetworkingState").Invoke(panel, new object[] { false, true });
                    void Render() => panelType.GetMethod("Render").Invoke(panel, new object[] { room, room.LocalPlayerId });
                    UnityEngine.UI.Button ButtonFor(string id)
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var entries = ((System.Collections.IEnumerable)panelType.GetField("expansions", flags).GetValue(panel)).Cast<object>();
                        object item = entries.Single(x => (string)x.GetType().GetField("Id").GetValue(x) == id);
                        return (UnityEngine.UI.Button)item.GetType().GetField("Button").GetValue(item);
                    }
                    Render(); Assert.That(ButtonFor("enterprise").interactable, Is.True);
                    Assert.That(options.TryPlanModeTransition("entrepreneur", room.Configuration.MapId, room.Configuration.EnabledExpansionIds, out var plan, out string reason), Is.True, reason);
                    var request = new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId,
                        RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, MapId = room.Configuration.MapId,
                        RuleModeId = "entrepreneur", EnabledExpansionIds = plan.ExpansionIds.ToList() };
                    var changed = service.ChangeConfigurationAsync(request).GetAwaiter().GetResult();
                    Assert.That(changed.Success, Is.True, changed.ReasonCode); room = service.GetCurrentRoom(); Render();
                    Assert.That(ButtonFor("enterprise").interactable, Is.False);
                    Assert.That(ButtonFor("infected").interactable, Is.False);
                    int requests = 0; Action<string, bool> handler = (id, enabled) => requests++;
                    panelType.GetEvent("ExpansionSelectionRequested").AddEventHandler(panel, handler);
                    ButtonFor("enterprise").onClick.Invoke(); Assert.That(requests, Is.Zero);
                }
            }
            finally
            {
                if (rootView != null) UnityEngine.Object.DestroyImmediate(rootView);
                providerField.SetValue(null, previous);
            }
        }

        [Test]
        public void DiyExpansionUsesExistingScrollableRoomTemplateAndSubmitsItsOwnId()
        {
            string dataRoot = NewRoot(), eventRoot = MakeEventPack(dataRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "pack.json")));
            manifest["expansions"][0]["applicableModes"] = new JArray("standard"); Write(eventRoot, "pack.json", manifest.ToString());
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var options = repository.GetOptions();
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var providerField = serviceType.GetField("contentCapabilities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object previous = providerField.GetValue(null); UnityEngine.GameObject rootView = null;
            try
            {
                providerField.SetValue(null, provider);
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    var room = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                    var panelType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab");
                    rootView = UnityEngine.Object.Instantiate(asset); var panel = rootView.GetComponentsInChildren(panelType, true).Single();
                    panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                    panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                    panelType.GetMethod("SetNetworkingState").Invoke(panel, new object[] { false, true });
                    panelType.GetMethod("Render").Invoke(panel, new object[] { room, room.LocalPlayerId });
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var entries = ((System.Collections.IEnumerable)panelType.GetField("expansions", flags).GetValue(panel)).Cast<object>().ToArray();
                    object Find(string id) => entries.Single(x => (string)x.GetType().GetField("Id").GetValue(x) == id);
                    UnityEngine.UI.Button ButtonOf(object item) => (UnityEngine.UI.Button)item.GetType().GetField("Button").GetValue(item);
                    var custom = ButtonOf(Find("demo.events.standard")); var existing = ButtonOf(Find("enterprise"));
                    Assert.That(custom.transform.parent, Is.SameAs(existing.transform.parent));
                    Assert.That(custom.GetComponentsInChildren<UnityEngine.UI.Text>(true).Select(x => x.text), Does.Contain("荒原").And.Contain("扩展"));
                    Assert.That(custom.interactable, Is.True);
                    string requested = null; bool enabled = false;
                    Action<string, bool> handler = (id, on) => { requested = id; enabled = on; };
                    panelType.GetEvent("ExpansionSelectionRequested").AddEventHandler(panel, handler);
                    custom.onClick.Invoke(); Assert.That(requested, Is.EqualTo("demo.events.standard")); Assert.That(enabled, Is.True);
                    Assert.That(options.TryPlanModeTransition("entrepreneur", room.Configuration.MapId, room.Configuration.EnabledExpansionIds, out var plan, out string reason), Is.True, reason);
                    var request = new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId,
                        RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, MapId = room.Configuration.MapId,
                        RuleModeId = "entrepreneur", EnabledExpansionIds = plan.ExpansionIds.ToList() };
                    Assert.That(service.ChangeConfigurationAsync(request).GetAwaiter().GetResult().Success, Is.True);
                    room = service.GetCurrentRoom(); panelType.GetMethod("Render").Invoke(panel, new object[] { room, room.LocalPlayerId });
                    Assert.That(custom.gameObject.activeSelf, Is.False, "不兼容的扩展不应出现在该模式的选择中");
                }
            }
            finally
            {
                if (rootView != null) UnityEngine.Object.DestroyImmediate(rootView);
                providerField.SetValue(null, previous);
            }
        }

        [Test]
        public void DiyModeAppearsAndCanBeReachedInActualStartSceneRoom()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot);
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var options = repository.GetOptions();
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var providerField = serviceType.GetField("contentCapabilities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object previous = providerField.GetValue(null);
            var scene = default(UnityEngine.SceneManagement.Scene);
            try
            {
                providerField.SetValue(null, provider);
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity", UnityEditor.SceneManagement.OpenSceneMode.Additive);
                var panelType = Type.GetType("YC.Presentation.StartMenuRoomPanelView, Assembly-CSharp", true);
                var panel = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(panelType, true)).Single();
                ((UnityEngine.Component)panel).gameObject.SetActive(true);
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    var room = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                    panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                    panelType.GetMethod("SetGameplayOptions").Invoke(panel, new object[] { options });
                    panelType.GetMethod("SetNetworkingState").Invoke(panel, new object[] { false, true });
                    panelType.GetMethod("Render").Invoke(panel, new object[] { room, room.LocalPlayerId });
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var modes = ((System.Collections.IEnumerable)panelType.GetField("ruleModes", flags).GetValue(panel)).Cast<object>().ToArray();
                    var custom = modes.Single(item => (string)item.GetType().GetField("Id").GetValue(item) == "demo.modes.expedition");
                    var button = (UnityEngine.UI.Button)custom.GetType().GetField("Button").GetValue(custom);
                    var content = (UnityEngine.RectTransform)panelType.GetField("ruleModeContent", flags).GetValue(panel);
                    var scroll = content.GetComponentInParent<UnityEngine.UI.ScrollRect>();
                    Assert.That(scroll, Is.Not.Null);
                    Assert.That(scroll.horizontal && scroll.content == content && scroll.viewport != null && scroll.horizontalScrollbar != null, Is.True);
                    Assert.That(button.transform.parent, Is.SameAs(content));
                    Assert.That(modes.Select(item => (string)item.GetType().GetField("Id").GetValue(item)),
                        Is.EquivalentTo(new[] { "standard", "entrepreneur", "demo.modes.expedition" }));
                    UnityEngine.Canvas.ForceUpdateCanvases();
                    Assert.That(content.rect.width, Is.GreaterThan(scroll.viewport.rect.width));
                    scroll.horizontalNormalizedPosition = 1f;
                    Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(0.99f));
                    string requested = null; Action<string> handler = id => requested = id;
                    panelType.GetEvent("RuleModeSelectionRequested").AddEventHandler(panel, handler);
                    button.onClick.Invoke(); Assert.That(requested, Is.EqualTo("demo.modes.expedition"));
                    Assert.That(options.TryPlanModeTransition(requested, room.Configuration.MapId, room.Configuration.EnabledExpansionIds, out var plan, out string reason), Is.True, reason);
                    var changed = service.ChangeConfigurationAsync(new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { CommandId = Guid.NewGuid().ToString("N"),
                        RoomId = room.RoomId, RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision,
                        MapId = room.Configuration.MapId, RuleModeId = requested, EnabledExpansionIds = plan.ExpansionIds.ToList() }).GetAwaiter().GetResult();
                    Assert.That(changed.Success, Is.True, changed.ReasonCode);
                    Assert.That(changed.Snapshot.Configuration.LockedContentPacks.Select(x => x.PackId), Does.Contain("demo.modes"));
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                providerField.SetValue(null, previous);
            }
        }

        [Test]
        public void ActualStartSceneManagesLocalVersionsAndOpensFixtureFolder()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot, "1.0.0");
            var paths = new DiyDataPaths(dataRoot); var found = DiyPackDiscovery.Scan(paths); var profile = DiyVersionProfile.Empty();
            profile.BindUniqueDefaults(found); profile.Save(Path.Combine(paths.Profiles, "default.json")); MakeMode(dataRoot, "2.0.0");
            found = DiyPackDiscovery.Scan(paths);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var local = new DiyLocalContentConfiguration(paths);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var scene = default(UnityEngine.SceneManagement.Scene);
            try
            {
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity", UnityEditor.SceneManagement.OpenSceneMode.Additive);
                var controllerType = Type.GetType("YC.Presentation.StartMenuController, Assembly-CSharp", true);
                var viewType = Type.GetType("YC.Presentation.StartMenuView, Assembly-CSharp", true);
                var panelType = Type.GetType("YC.Presentation.StartMenuMapSelectionPanelView, Assembly-CSharp", true);
                var controller = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(controllerType, true)).Single();
                var view = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(viewType, true)).Single();
                var panel = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(panelType, true)).Single();
                var validation = new object[] { null };
                Assert.That((bool)viewType.GetMethod("TryValidateConfiguration").Invoke(view, validation), Is.True, (string)validation[0]);
                const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                string opened = null;
                using (var service = new YC.Infrastructure.Multiplayer.RoomSessionService(provider, () => throw new InvalidOperationException("隔离测试不启用网络")))
                {
                    controllerType.GetField("roomService", fields).SetValue(controller, service);
                    controllerType.GetField("localDiySource", fields).SetValue(controller, (Func<DiyLocalContentConfiguration>)(() => local));
                    controllerType.GetField("localDiyFolderOpener", fields).SetValue(controller, (Action<string>)(uri => opened = uri));
                    controllerType.GetMethod("BindStaticUi", fields).Invoke(controller, null);
                    var originalCount = ((UnityEngine.UI.Text)panelType.GetField("countLabel", fields).GetValue(panel)).text;
                    var originalMap = ((UnityEngine.UI.Text)panelType.GetField("mapLabel", fields).GetValue(panel)).text;
                    var originalStart = ((UnityEngine.UI.Text)panelType.GetField("startCaption", fields).GetValue(panel)).text;
                    var mainButton = (UnityEngine.UI.Button)viewType.GetProperty("DiyContentButton").GetValue(view);
                    mainButton.onClick.Invoke(); Assert.That(((UnityEngine.Component)panel).gameObject.activeSelf, Is.True);
                    var mapText = (UnityEngine.UI.Text)panelType.GetProperty("MapText").GetValue(panel);
                    Assert.That(mapText.text, Does.Contain("1.0.0"));
                    ((UnityEngine.UI.Button)panelType.GetProperty("NextMapButton").GetValue(panel)).onClick.Invoke();
                    Assert.That(mapText.text, Does.Contain("2.0.0"));
                    var action = (UnityEngine.UI.Button)panelType.GetProperty("StartButton").GetValue(panel);
                    action.onClick.Invoke();
                    Assert.That(DiyVersionProfile.Load(Path.Combine(paths.Profiles, "default.json")).Bindings["demo.modes"], Is.EqualTo("2.0.0"));
                    Assert.That(repository.GetOptions().Modes.Single(x => x.Id == "demo.modes.expedition").PackVersion, Is.EqualTo("1.0.0"));
                    ((UnityEngine.UI.Button)panelType.GetProperty("NextCountButton").GetValue(panel)).onClick.Invoke();
                    action.onClick.Invoke();
                    Assert.That(opened, Is.EqualTo(new Uri(paths.Mods).AbsoluteUri));
                    ((UnityEngine.UI.Button)panelType.GetProperty("BackButton").GetValue(panel)).onClick.Invoke();
                    Assert.That(((UnityEngine.UI.Text)panelType.GetField("countLabel", fields).GetValue(panel)).text, Is.EqualTo(originalCount));
                    Assert.That(((UnityEngine.UI.Text)panelType.GetField("mapLabel", fields).GetValue(panel)).text, Is.EqualTo(originalMap));
                    Assert.That(((UnityEngine.UI.Text)panelType.GetField("startCaption", fields).GetValue(panel)).text, Is.EqualTo(originalStart));
                }
            }
            finally { if (scene.IsValid() && scene.isLoaded) UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true); }
        }

        private static YC.Domain.State.GameState CreateInitialStateFixture(ActiveContentCatalog catalog, YC.Domain.Rules.GameRuleConfiguration configuration)
        {
            configuration.ContentPackHash = catalog.ContentPackHash;
            var seats = Enumerable.Range(1, configuration.PlayerCount).Select(i => new YC.Application.Sessions.PlayerSeat {
                PlayerId = i, PlayerName = "玩家" + i, Color = (YC.Domain.Rules.PlayerColor)(i - 1) }).ToList();
            return YC.Application.Sessions.GameLaunchStateFactory.CreateInitialState(YC.Application.Sessions.LaunchMode.Local, 1, seats,
                configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
        }

        [Test]
        public void ExactPackLocksSurviveSerializationAndRejectChangedOrDuplicateContent()
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray(); Write(root, "pack.json", manifest.ToString());
            var source = DiyPackSnapshot.Load(root);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            var selected = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<YC.Domain.Rules.GameRuleConfiguration>(Newtonsoft.Json.JsonConvert.SerializeObject(configuration));
            Assert.That(restored.TryValidateStructure(out string reason), Is.True, reason);
            Assert.DoesNotThrow(() => ActiveContentCatalogBuilder.Build(core, restored, selected));
            restored.LockedContentPacks.Single(x => x.PackId == "demo.modes").ContentHash = new string('f', 64);
            Assert.That(Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(core, restored, selected)).Message, Is.EqualTo("diy_content_lock_mismatch"));
            restored.LockedContentPacks.Add(restored.LockedContentPacks[0].Clone());
            Assert.That(restored.TryValidateStructure(out reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_lock_invalid"));
        }

        [Test]
        public void ExactRoomLockOverridesDefaultAndDetectsMissingOrModifiedVersion()
        {
            string dataRoot = NewRoot(), firstRoot = MakeMode(dataRoot);
            var firstManifest = JObject.Parse(File.ReadAllText(Path.Combine(firstRoot, "pack.json")));
            firstManifest["requiredCapabilities"] = new JArray(); Write(firstRoot, "pack.json", firstManifest.ToString());
            var paths = new DiyDataPaths(dataRoot); var profile = DiyVersionProfile.Empty();
            profile.BindUniqueDefaults(DiyPackDiscovery.Scan(paths));
            string secondRoot = MakeMode(dataRoot, "2.0.0");
            var secondManifest = JObject.Parse(File.ReadAllText(Path.Combine(secondRoot, "pack.json")));
            secondManifest["requiredCapabilities"] = new JArray(); Write(secondRoot, "pack.json", secondManifest.ToString());
            var discovered = DiyPackDiscovery.Scan(paths);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var cfg = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            var repository = new DiyInstalledRepository(core, discovered, profile);
            Assert.That(repository.ResolveLocal(cfg).Packs.Single().Version, Is.EqualTo("1.0.0"));
            profile.SetBinding("demo.modes", "2.0.0", discovered);
            cfg.LockedContentPacks = repository.ResolveLocal(cfg).CreateRuleLocks();
            profile.SetBinding("demo.modes", "1.0.0", discovered);
            Assert.That(repository.ResolveExact(cfg).Packs.Single().Version, Is.EqualTo("2.0.0"));
            var missing = new DiyInstalledRepository(core, discovered.Where(x => x.Version != "2.0.0").ToArray(), profile);
            Assert.That(Assert.Throws<InvalidDataException>(() => missing.ResolveExact(cfg)).Message, Does.Contain("缺少锁定包"));
            secondManifest["displayName"] = "本地修改"; Write(secondRoot, "pack.json", secondManifest.ToString());
            var changed = new DiyInstalledRepository(core, DiyPackDiscovery.Scan(paths), profile);
            Assert.That(Assert.Throws<InvalidDataException>(() => changed.ResolveExact(cfg)).Message, Does.Contain("同版本内容被修改"));
        }

        [Test]
        public void RoomConfigurationCommitsModeAndExactLocksTogether()
        {
            string dataRoot = NewRoot(), root = MakeMode(dataRoot);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray(); Write(root, "pack.json", manifest.ToString());
            var paths = new DiyDataPaths(dataRoot); var discovered = DiyPackDiscovery.Scan(paths); var profile = DiyVersionProfile.Empty();
            profile.BindUniqueDefaults(discovered);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, discovered, profile);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var service = new YC.Infrastructure.Multiplayer.RoomConfigurationService(provider, localPlayers: true);
            var room = new YC.Infrastructure.Multiplayer.RoomState { RoomId = "fixture-room", HostPlayerId = 1, LocalPlayerId = 1 };
            room.Configuration.ContentPackHash = provider.GetContentPackHash(room.Configuration);
            for (int i = 1; i <= 2; i++)
                Assert.That(service.TryOccupySeat(room, new YC.Application.Sessions.PlayerSeat { PlayerId = i, PlayerName = "玩家" + i,
                    LobbyMemberPresent = true, TransportConnected = true, IdentityVerified = true, IsReady = true }, out _), Is.True);
            Assert.That(room.Configuration.OfficialPreparationContract, Is.EqualTo(YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion));
            var request = new YC.Infrastructure.Multiplayer.ChangeRoomConfigurationRequest { RoomId = room.RoomId, CommandId = "change-mode", RequestingPlayerId = 1,
                ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, MapId = "map-three-players", RuleModeId = "demo.modes.expedition",
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            var changed = service.ChangeConfiguration(room, request);
            Assert.That(changed.Success, Is.True, changed.ReasonCode);
            Assert.That(room.Configuration.OfficialPreparationContract, Is.EqualTo(YC.Domain.Rules.GameRuleConfiguration.OfficialPreparationVersion));
            Assert.That(room.Configuration.LockedContentPacks.Select(x => x.PackId), Is.EqualTo(new[] { "core", "demo.modes" }));
            Assert.That(room.Configuration.ContentPackHash, Is.Not.Empty);
            Assert.That(provider.TryValidate(room.Configuration, out string reason), Is.True, reason);
            string json = YC.Infrastructure.Multiplayer.RoomStateSerializer.Serialize(room);
            Assert.That(YC.Infrastructure.Multiplayer.RoomStateSerializer.TryDeserialize(json, out var copy, out reason), Is.True, reason);
            Assert.That(copy.Configuration.LockedContentPacks.Select(x => x.ContentHash), Is.EqualTo(room.Configuration.LockedContentPacks.Select(x => x.ContentHash)));
            string before = Newtonsoft.Json.JsonConvert.SerializeObject(room.Configuration);
            request.CommandId = "missing-mode"; request.ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision; request.RuleModeId = "missing.mode";
            var failed = service.ChangeConfiguration(room, request);
            Assert.That(failed.Success, Is.False);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(room.Configuration), Is.EqualTo(before));
        }

        [Test]
        public void DiyInitialCheckpointAndResumeRoomRequireTheSameLockedContent()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot);
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var roomType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var coreField = runtimeType.GetField("pack", statics); var repoField = runtimeType.GetField("diyRepository", statics);
            var providerField = roomType.GetField("contentCapabilities", statics); var contextField = contextType.GetField("<Instance>k__BackingField", statics);
            object oldCore = coreField.GetValue(null), oldRepo = repoField.GetValue(null), oldProvider = providerField.GetValue(null), oldContext = contextField.GetValue(null);
            try
            {
                coreField.SetValue(null, core); repoField.SetValue(null, repository); providerField.SetValue(null, provider);
                var cfg = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                    EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
                Assert.That(((YC.Infrastructure.Multiplayer.IRoomContentLockProvider)provider).TryPrepareLock(cfg, out var locks, out string reason), Is.True, reason);
                cfg.LockedContentPacks = locks; cfg.ContentPackHash = provider.GetContentPackHash(cfg); Assert.That(cfg.ContentPackHash, Is.Not.Empty);
                var players = Enumerable.Range(1, 3).Select(i => new YC.Application.Sessions.PlayerSeat { PlayerId = i, PlayerName = "玩家" + i,
                    SteamId = (ulong)(100 + i), Color = (YC.Domain.Rules.PlayerColor)(i - 1), OperatorId = "operator-" + i }).ToList();
                var order = new System.Collections.Generic.List<int> { 1, 2, 3 }; string gameId = "diy-checkpoint-" + Guid.NewGuid().ToString("N"); const int seed = 42;
                var start = new YC.Domain.State.GameState { GameId = gameId, EventDeckSeed = seed, RandomRuntime = YC.Domain.Rules.GameplayRandomRuntime.Create(gameId) };
                YC.Domain.Rules.GameplayRandomRuntime.Shuffle(order, start, YC.Domain.Rules.GameplayRandomRuntime.LaunchOrder);
                object Build(YC.Application.Sessions.LaunchMode mode, bool deferred)
                {
                    contextField.SetValue(null, null); var owner = new UnityEngine.GameObject("DIY 联机检查点隔离上下文");
                    try
                    {
                        object context = owner.AddComponent(contextType);
                        contextType.GetMethods().Single(x => x.Name == "Configure" && x.GetParameters().Length == 9).Invoke(context,
                            new object[] { mode, mode == YC.Application.Sessions.LaunchMode.Host ? 1 : 2, "fixture-room", players, cfg, order, gameId, seed, start.RandomRuntime.Clone() });
                        var build = deferred ? bootstrapType.GetMethod("Build", statics, null, new[] { contextType, typeof(bool), typeof(bool), typeof(bool) }, null)
                            : bootstrapType.GetMethod("Build", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public, null, new[] { contextType, typeof(bool), typeof(bool) }, null);
                        return build.Invoke(null, deferred ? new object[] { context, false, false, true } : new object[] { context, false, false });
                    }
                    finally { UnityEngine.Object.DestroyImmediate(owner); contextField.SetValue(null, null); }
                }
                var host = Build(YC.Application.Sessions.LaunchMode.Host, false); var client = Build(YC.Application.Sessions.LaunchMode.Client, true);
                var hostSession = (YC.Application.Sessions.GameSession)host.GetType().GetField("Session").GetValue(host);
                var clientSession = (YC.Application.Sessions.GameSession)client.GetType().GetField("Session").GetValue(client);
                var registry = (YC.Domain.Effects.EffectRegistry)client.GetType().GetField("EffectRegistry").GetValue(client);
                string archive = YC.Application.Sessions.HostRecoveryService.SerializeArchive(hostSession.CreateHostArchive());
                var installer = new YC.Application.Sessions.InitialSimulationCheckpointInstaller(clientSession, registry, YC.Domain.State.GameStateViewer.Player(2));
                Assert.That(installer.TryInstall("fixture-checkpoint", archive, out var installed), Is.True, installed?.Diagnostic);
                Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(clientSession.State, hostSession.State), Is.True);
                var other = Build(YC.Application.Sessions.LaunchMode.Client, true);
                var otherSession = (YC.Application.Sessions.GameSession)other.GetType().GetField("Session").GetValue(other);
                var otherRegistry = (YC.Domain.Effects.EffectRegistry)other.GetType().GetField("EffectRegistry").GetValue(other);
                var altered = JObject.Parse(archive);
                var changedLock = ((JArray)altered["Snapshot"]["State"]["RuleConfiguration"]["LockedContentPacks"]).OfType<JObject>().Single(x => (string)x["PackId"] == "demo.modes");
                changedLock["ContentHash"] = new string('f', 64);
                var before = YC.Domain.State.GameStateCloneService.DeepClone(otherSession.State);
                var rejected = new YC.Application.Sessions.InitialSimulationCheckpointInstaller(otherSession, otherRegistry, YC.Domain.State.GameStateViewer.Player(2));
                Assert.That(rejected.TryInstall("changed-checkpoint", altered.ToString(), out _), Is.False);
                Assert.That(YC.Domain.State.GameStateCloneService.AreEquivalent(otherSession.State, before), Is.True);

                var resume = new YC.Infrastructure.Multiplayer.ResumeRoomOptions { MapId = cfg.MapId, GameId = gameId, ContentHash = cfg.ContentPackHash,
                    Round = hostSession.State.Round, HostPlayerId = 1, EventDeckSeed = seed, Configuration = hostSession.State.RuleConfiguration.Clone(),
                    RandomRuntime = hostSession.State.RandomRuntime.Clone(), LaunchPlayerOrder = order,
                    Seats = players.Select(p => new YC.Application.Sessions.MatchSaveSeat { PlayerId = p.PlayerId, PlayerName = p.PlayerName, Color = p.Color,
                        SteamId = p.SteamId, OperatorId = p.OperatorId }).ToList(),
                    SimulationEpoch = 2, MembershipVersion = 1, ConfirmedGlobalSequence = 0, CommandPrefixDigest = "fixture", CheckpointId = "fixture", MetadataDigest = "fixture" };
                var room = new YC.Infrastructure.Multiplayer.RoomState { RoomId = "resume-fixture" };
                Assert.DoesNotThrow(() => resume.ApplyTo(room, players[0], provider));
                Assert.That(room.Configuration.LockedContentPacks.Single(x => x.PackId == "demo.modes").Version, Is.EqualTo("1.0.0"));
                var missing = new DiyInstalledRepository(core, Array.Empty<DiyDiscoveredPack>(), DiyVersionProfile.Empty());
                var missingProvider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                    new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => missing) });
                Assert.Throws<InvalidOperationException>(() => resume.ApplyTo(new YC.Infrastructure.Multiplayer.RoomState(), players[0], missingProvider));
            }
            finally { coreField.SetValue(null, oldCore); repoField.SetValue(null, oldRepo); providerField.SetValue(null, oldProvider); contextField.SetValue(null, oldContext); }
        }

        [Test]
        public void ProductionBootstrapUsesFixtureRepositoryForModeAndNewEvent()
        {
            string dataRoot = NewRoot(), modeRoot = MakeMode(dataRoot), eventRoot = MakeEventPack(dataRoot);
            var modeManifest = JObject.Parse(File.ReadAllText(Path.Combine(modeRoot, "pack.json")));
            ((JArray)modeManifest["requiredCapabilities"]).Add("diy.rule_bindings.cleanup.v1");
            modeManifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.cleanup", ["eventType"] = "PlayerCleanupStarted",
                ["script"] = "lua/cleanup.lua", ["version"] = "1.0.0" });
            Write(modeRoot, "pack.json", modeManifest.ToString());
            Write(modeRoot, "lua/cleanup.lua", "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=1}) } end");
            var eventManifest = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "pack.json")));
            ((JArray)eventManifest["dependencies"]).Add(new JObject { ["packId"] = "demo.modes", ["versionRange"] = "=1.0.0" });
            Write(eventRoot, "pack.json", eventManifest.ToString());
            var recipe = JObject.Parse(File.ReadAllText(Path.Combine(eventRoot, "recipes", "ruins.json")));
            recipe["applicableModes"] = new JArray("demo.modes.expedition"); Write(eventRoot, "recipes/ruins.json", recipe.ToString());
            Write(eventRoot, "lua/main.lua", "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=2}) } end");
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile);
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var serviceType = Type.GetType("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider, Assembly-CSharp", true);
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var coreField = runtimeType.GetField("pack", statics); var repoField = runtimeType.GetField("diyRepository", statics);
            var providerField = serviceType.GetField("contentCapabilities", statics); var contextField = contextType.GetField("<Instance>k__BackingField", statics);
            object oldCore = coreField.GetValue(null), oldRepo = repoField.GetValue(null), oldProvider = providerField.GetValue(null), oldContext = contextField.GetValue(null);
            var host = new UnityEngine.GameObject("DIY 正式启动隔离夹具");
            try
            {
                coreField.SetValue(null, core); repoField.SetValue(null, repository);
                var provider = (YC.Infrastructure.Multiplayer.IRoomContentCapabilityProvider)Activator.CreateInstance(providerType,
                    new object[] { (Func<ExternalContentPack>)(() => core), (Func<DiyInstalledRepository>)(() => repository) });
                providerField.SetValue(null, provider);
                var cfg = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 3,
                    EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "demo.events.standard" } };
                Assert.That(((YC.Infrastructure.Multiplayer.IRoomContentLockProvider)provider).TryPrepareLock(cfg, out var locks, out string reason), Is.True, reason);
                cfg.LockedContentPacks = locks; cfg.ContentPackHash = provider.GetContentPackHash(cfg);
                Assert.That(cfg.ContentPackHash, Is.Not.Empty);
                var seats = Enumerable.Range(1, 3).Select(i => new YC.Application.Sessions.PlayerSeat { PlayerId = i, PlayerName = "玩家" + i,
                    Color = (YC.Domain.Rules.PlayerColor)(i - 1) }).ToList();
                object context = host.AddComponent(contextType);
                var configure = contextType.GetMethod("Configure", new[] { typeof(YC.Application.Sessions.LaunchMode), typeof(int), typeof(string),
                    typeof(System.Collections.Generic.IList<YC.Application.Sessions.PlayerSeat>), typeof(YC.Domain.Rules.GameRuleConfiguration) });
                configure.Invoke(context, new object[] { YC.Application.Sessions.LaunchMode.Local, 1, "fixture", seats, cfg });
                var build = bootstrapType.GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                    new[] { contextType, typeof(bool), typeof(bool) }, null);
                object result = build.Invoke(null, new object[] { context, false, false });
                var session = (YC.Application.Sessions.GameSession)result.GetType().GetField("Session").GetValue(result);
                var registry = (YC.Domain.Effects.EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result);
                Assert.That(session.State.RuleConfiguration.RuleModeId, Is.EqualTo("demo.modes.expedition"));
                Assert.That(session.State.ItemZones.Items.Any(x => x.DefinitionId == "demo.events.ruins"), Is.True);
                Assert.That(session.State.RuleConfiguration.LockedContentPacks.Select(x => x.PackId), Is.EquivalentTo(new[] { "core", "demo.modes", "demo.events" }));
                string subscription = YC.Domain.State.StableIdFactory.Create("diy-rule-binding", "demo.modes", "demo.modes.expedition", "demo.modes.cleanup");
                Assert.That(registry.HasEventHandler(subscription), Is.True);

                var point = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
                var save = new YC.Application.Sessions.MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = YC.Application.Sessions.LaunchMode.Local,
                    LocalPlayerId = 1, HostSteamId = 101, MapId = session.State.MapId, GameId = session.State.GameId, Round = session.State.Round,
                    ContentHash = session.State.ContentPackHash, ArchiveJson = YC.Application.Sessions.HostRecoveryService.SerializeArchive(point.Archive),
                    ContinuationJson = YC.Application.Sessions.SimulationConfirmedRecoveryInstaller.SerializeContinuation(point.Continuation),
                    Seats = session.State.Players.Select(p => new YC.Application.Sessions.MatchSaveSeat { PlayerId = p.PlayerId, Color = p.Color,
                        PlayerName = p.Name, SteamId = (ulong)(100 + p.PlayerId), OperatorId = "operator-" + p.PlayerId }).ToList() };
                MakeMode(dataRoot, "2.0.0");
                var updated = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); profile.SetBinding("demo.modes", "2.0.0", updated);
                repoField.SetValue(null, new DiyInstalledRepository(core, updated, profile));
                var restore = bootstrapType.GetMethod("Restore", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                object restored = restore.Invoke(null, new object[] { context, save });
                var restoredSession = (YC.Application.Sessions.GameSession)restored.GetType().GetField("Session").GetValue(restored);
                Assert.That(restoredSession.State.RuleConfiguration.LockedContentPacks.Single(x => x.PackId == "demo.modes").Version, Is.EqualTo("1.0.0"));
                Assert.That(restoredSession.State.ContentPackHash, Is.EqualTo(session.State.ContentPackHash));
                var restoredRegistry = (YC.Domain.Effects.EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
                Assert.That(restoredRegistry.HasEventHandler(subscription), Is.True);
                repoField.SetValue(null, new DiyInstalledRepository(core, updated.Where(x => x.PackId != "demo.modes" || x.Version != "1.0.0").ToArray(), profile));
                var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => restore.Invoke(null, new object[] { context, save }));
                Assert.That(failure.InnerException.Message, Does.Contain("缺少锁定包"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                coreField.SetValue(null, oldCore); repoField.SetValue(null, oldRepo); providerField.SetValue(null, oldProvider); contextField.SetValue(null, oldContext);
            }
        }

        [Test]
        public void SharedLuaModuleRequiresItsSourcePackInDependencyClosure()
        {
            string firstRoot = MakeEventPack(NewRoot()), secondRoot = MakeEventPack(NewRoot());
            foreach (string relative in new[] { "pack.json", "event/ruins.json", "recipes/ruins.json" })
            {
                string path = Path.Combine(secondRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Write(secondRoot, relative, File.ReadAllText(path).Replace("demo.events", "demo.other"));
            }
            string module = "return { value = 1 }"; string sha = LuaContentHasher.ComputeSha256(module);
            Write(firstRoot, "lua/shared.lua", module);
            var firstManifest = JObject.Parse(File.ReadAllText(Path.Combine(firstRoot, "pack.json")));
            firstManifest["luaModules"] = new JArray(new JObject { ["moduleId"] = "demo.events.shared", ["version"] = "1.0.0", ["script"] = "lua/shared.lua", ["sha256"] = sha });
            Write(firstRoot, "pack.json", firstManifest.ToString());
            Write(secondRoot, "lua/main.lua", "return function(ctx) local n = Shared.shared.value return {} end");
            var secondManifest = JObject.Parse(File.ReadAllText(Path.Combine(secondRoot, "pack.json")));
            secondManifest["scriptDependencies"] = new JArray(new JObject { ["protocolVersion"] = 1, ["script"] = "lua/main.lua",
                ["modules"] = new JArray(new JObject { ["moduleId"] = "demo.events.shared", ["version"] = "1.0.0", ["sha256"] = sha, ["alias"] = "shared" }) });
            Write(secondRoot, "pack.json", secondManifest.ToString());
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var first = DiyPackSnapshot.Load(firstRoot); var second = DiyPackSnapshot.Load(secondRoot);
            DiyResolvedContent Resolve(DiyPackSnapshot other) => DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard", "demo.other.standard" },
                "map-three-players", new[] { first, other }, (string)core.Manifest["version"], core.ContentHash, new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            Assert.That(Assert.Throws<InvalidDataException>(() => ExternalContentPack.ComposeDiy(core, Resolve(second))).Message, Does.Contain("依赖闭包"));
            ((JArray)secondManifest["dependencies"]).Add(new JObject { ["packId"] = "demo.events", ["versionRange"] = "=1.0.0" });
            Write(secondRoot, "pack.json", secondManifest.ToString());
            var combined = ExternalContentPack.ComposeDiy(core, Resolve(DiyPackSnapshot.Load(secondRoot)));
            string path2 = (string)combined.FindDefinition("demo.other.ruins", "event").Data["choices"][0]["script"];
            Assert.That(combined.GetScript(path2), Does.Contain("Shared.shared").And.Contain("lua_frozen_modules.v1"));
        }

        [Test]
        public void DiyPresentationComposesWithExistingRegisteredTextSource()
        {
            string root = MakeEventPack(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["presentation"] = new JObject { ["schemaVersion"] = 1, ["texts"] = new JArray("presentation/texts.json"),
                ["icons"] = "presentation/icons.json", ["defaults"] = "presentation/defaults.json" };
            Write(root, "pack.json", manifest.ToString());
            Write(root, "presentation/texts.json", new JObject { ["schemaVersion"] = 1, ["locale"] = "zh-CN", ["messages"] = new JObject {
                ["demo.events:ruins.name"] = new JObject { ["parameters"] = new JObject(), ["template"] = "荒原遗迹" } } }.ToString());
            Write(root, "presentation/icons.json", new JObject { ["schemaVersion"] = 1, ["assets"] = new JObject(), ["icons"] = new JArray() }.ToString());
            Write(root, "presentation/defaults.json", new JObject { ["schemaVersion"] = 1, ["effectTypes"] = new JObject(), ["interactions"] = new JObject(), ["outcomes"] = new JObject() }.ToString());
            var definition = JObject.Parse(File.ReadAllText(Path.Combine(root, "event", "ruins.json")));
            definition["presentation"] = new JObject { ["name"] = new JObject { ["key"] = "demo.events:ruins.name" } };
            Write(root, "event/ruins.json", definition.ToString());
            string registryRoot = NewRoot();
            Write(registryRoot, "sources.json", new JObject { ["schemaVersion"] = 1, ["sources"] = new JArray("external") }.ToString());
            Write(registryRoot, "external/presentation.json", new JObject { ["schemaVersion"] = 1, ["packId"] = "fixture.locale", ["version"] = "1.0.0",
                ["locale"] = "zh-CN", ["texts"] = new JArray("texts.json") }.ToString());
            Write(registryRoot, "external/texts.json", new JObject { ["schemaVersion"] = 1, ["locale"] = "zh-CN", ["messages"] = new JObject {
                ["fixture.locale:hello"] = new JObject { ["parameters"] = new JObject(), ["template"] = "外部文案" } } }.ToString());
            var core = ExternalContentPack.LoadRegistered(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"), registryRoot);
            var source = DiyPackSnapshot.Load(root);
            var resolved = DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard" }, "map-three-players", new[] { source },
                (string)core.Manifest["version"], core.ContentHash, new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            var combined = ExternalContentPack.ComposeDiy(core, resolved);
            Assert.That(combined.Presentation.TryGetTemplate("fixture.locale:hello", out _), Is.True);
            Assert.That(combined.Presentation.TryGetTemplate("demo.events:ruins.name", out var name), Is.True);
            Assert.That(name.Template, Is.EqualTo("荒原遗迹"));
            Assert.That(combined.FindDefinition("demo.events.ruins").Presentation.Name.Key, Is.EqualTo("demo.events:ruins.name"));
            Assert.That(combined.PresentationSources.Count, Is.EqualTo(1));
            Assert.That(core.Presentation.TryGetTemplate("demo.events:ruins.name", out _), Is.False);
        }

        [Test]
        public void ModeCleanupBindingUsesExistingEffectChainAndRestoresPendingChoiceOnce()
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray("diy.rule_bindings.cleanup.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.cleanup",
                ["eventType"] = "PlayerCleanupStarted", ["script"] = "lua/cleanup.lua", ["version"] = "1.0.0",
                ["completion"] = new JObject { ["handlerId"] = "demo.modes.cleanup.done", ["script"] = "lua/completed.lua", ["version"] = "1.0.0" } });
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/cleanup.lua", "return function(ctx) return { Effect.Choice({player=ctx.playerId, options={'go'}, minSelections=1, maxSelections=1, completionHandlerId='demo.modes.cleanup.done'}) } end");
            Write(root, "lua/completed.lua", "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId, resourceType='Originium', amount=2}) } end");
            var source = DiyPackSnapshot.Load(root);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.modes.expedition", MapId = "map-three-players", PlayerCount = 1,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core" } };
            var selected = DiyContentResolution.Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds, configuration.MapId, new[] { source },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var combined = ExternalContentPack.ComposeDiy(core, selected);
            var registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            YC.Domain.Effects.GenericLuaEffectExecutor.Register(registry);
            DiyRuleBindingCatalog.Register(registry, combined);
            registry.Register("fixture.cleanup.signal", ctx => YC.Domain.Effects.EffectStepResult.Completed().AddEvent(
                new YC.Domain.Effects.EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = 1 }));
            var state = new YC.Domain.State.GameState { GameId = "diy-rule-fixture", Round = 1, MapId = configuration.MapId,
                RuleConfiguration = configuration, Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            engine.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.cleanup.signal", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(x => x.Status == "open");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            state = YC.Domain.State.GameStateCloneService.DeepClone(state);
            engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(x => x.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                YC.Domain.State.NormalizedValue.CreateString("go"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                YC.Domain.State.NormalizedValue.CreateString("go"), out _), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EffectBindingRejectsChangedBodyOrContinuationOnSameRegistry(bool changeCompletion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.cleanup.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.cleanup",
                ["eventType"] = "PlayerCleanupStarted", ["script"] = "lua/cleanup.lua", ["version"] = "1.0.0",
                ["completion"] = new JObject { ["handlerId"] = "demo.modes.done", ["script"] = "lua/completed.lua", ["version"] = "1.0.0" } });
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/cleanup.lua", "return function(ctx) return {} end");
            Write(root, "lua/completed.lua", "return function(ctx) return {} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            ExternalContentPack Compose() => ExternalContentPack.ComposeDiy(core, DiyContentResolution.Resolve(
                "demo.modes.expedition", new[] { "core" }, "map-three-players", new[] { DiyPackSnapshot.Load(root) },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value));
            var registry = new YC.Domain.Effects.EffectRegistry();
            var first = Compose();
            DiyRuleBindingCatalog.Register(registry, first);
            DiyRuleBindingCatalog.Register(registry, first);
            Write(root, changeCompletion ? "lua/completed.lua" : "lua/cleanup.lua", "return function(ctx) return {}, {} end");
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyRuleBindingCatalog.Register(registry, Compose())).Message,
                Does.Contain("effect_conflict"));
        }

        [TestCase(false)][TestCase(true)]
        public void DispatchStartingBindingUsesPurePatchContractAndRejectsCompletion(bool completion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.dispatch.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.dispatch", ["eventType"] = "OperatorDispatchStarting",
                ["script"] = "lua/dispatch.lua", ["version"] = "1.0.0" };
            if (completion) binding["completion"] = new JObject { ["handlerId"] = "demo.modes.dispatch.done", ["script"] = "lua/dispatch.lua", ["version"] = "1.0.0" };
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            if (completion) { Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest)); return; }
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/dispatch.lua", "return function(ctx) assert(Effect==nil and ctx.eventType=='OperatorDispatchStarting'); return {drawCountDelta=2} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players", new[] { DiyPackSnapshot.Load(root) },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            var combined = ExternalContentPack.ComposeDiy(core, selected); var registry = new YC.Domain.Effects.EffectRegistry();
            DiyRuleBindingCatalog.Register(registry, combined); DiyRuleBindingCatalog.Register(registry, combined);
            registry.Register("fixture.dispatch.query", context => YC.Domain.Effects.EffectStepResult.Completed(YC.Domain.State.NormalizedValue.CreateInteger(
                registry.ResolveOperatorDispatchCount(context, YC.Domain.Effects.ItemTransferEffectExecutor.Object(), 2))));
            var state = new YC.Domain.State.GameState { GameId = "dispatch-patch-fixture", MapId = "map-three-players", Round = 1, ActionRound = 1,
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var executor = new YC.Domain.Effects.EffectTreeExecutor(state, registry); string id = executor.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.dispatch.query", playerId: 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(executor.GetNode(id).NormalizedResult.IntegerValue, Is.EqualTo(4));
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(1));
        }

        [TestCase("RoundStarted", true)]
        [TestCase("RoundEnded", true)]
        [TestCase("CharacterCardCovered", false)]
        [TestCase("CharacterCoverCompleted", true)]
        [TestCase("CharacterUseFinished", false)]
        [TestCase("CollectionStarted", false)]
        [TestCase("CityMoveCompleted", false)]
        [TestCase("FacilityPlaced", false)]
        [TestCase("FinalScoringStarted", true)]
        public void ModeLifecycleBindingUsesExistingEventAndEffectChain(string eventType, bool global)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.events.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.lifecycle",
                ["eventType"] = eventType, ["script"] = "lua/lifecycle.lua", ["version"] = "1.0.0" });
            Write(root, "pack.json", manifest.ToString());
            string recipient = !global ? "ctx.playerId" : eventType == "CharacterCoverCompleted"
                ? "{playerId=ctx.payload.coveredPlayerIds[1]}" : "{playerId=ctx.payload.startPlayerId}";
            Write(root, "lua/lifecycle.lua", "return function(ctx) return {Effect.GainResource({recipient=" + recipient + ",resourceType='Originium',amount=1})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            var registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            YC.Domain.Effects.GenericLuaEffectExecutor.Register(registry);
            var combined = ExternalContentPack.ComposeDiy(core, selected);
            DiyRuleBindingCatalog.Register(registry, combined);
            DiyRuleBindingCatalog.Register(registry, combined);
            int eventPlayer = global ? -1 : 1;
            registry.Register("fixture.lifecycle.signal", ctx => YC.Domain.Effects.EffectStepResult.Completed().AddEvent(
                new YC.Domain.Effects.EffectEventRequest { EventType = eventType, PlayerId = eventPlayer,
                    Payload = YC.Domain.Effects.ItemTransferEffectExecutor.Object(
                        "coveredPlayerIds", YC.Domain.State.NormalizedValue.CreateArray(new System.Collections.Generic.List<YC.Domain.State.NormalizedValue> {
                            YC.Domain.State.NormalizedValue.CreateStableReference("player", "p1") }),
                        "startPlayerId", YC.Domain.State.NormalizedValue.CreateStableReference("player", "p1")) }));
            var state = new YC.Domain.State.GameState { GameId = "diy-lifecycle-fixture", Round = 1, ActionRound = 1, MapId = "map-three-players",
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var executor = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            executor.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.lifecycle.signal", playerId: eventPlayer));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
        }

        [TestCase("PlayerCleanupStarted", "diy.rule_bindings.cleanup.v1", false)]
        [TestCase("PlayerCleanupStarted", "diy.rule_bindings.cleanup.v1", true)]
        [TestCase("RoundStarted", "diy.rule_bindings.events.v1", false)]
        [TestCase("RoundStarted", "diy.rule_bindings.events.v1", true)]
        [TestCase("CandidateSetBuilding", "diy.rule_bindings.candidate.v1", false)]
        [TestCase("CandidateSetBuilding", "diy.rule_bindings.candidate.v1", true)]
        [TestCase("EffectCompleted", "diy.rule_bindings.completed.v1", false)]
        [TestCase("EffectCompleted", "diy.rule_bindings.completed.v1", true)]
        public void RuleBindingCapabilityChecksSelectedScopeAndPreservesLegacyPack(string eventType, string capability, bool expansion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            var mode = (JObject)manifest["ruleModes"][0];
            var extension = new JObject { ["expansionId"] = "demo.modes.rules", ["displayName"] = "规则夹具",
                ["description"] = "能力作用域", ["requires"] = new JArray("core"), ["requiredCapabilities"] = new JArray() };
            manifest["expansions"] = new JArray(extension);
            var owner = expansion ? extension : mode;
            var sibling = expansion ? mode : extension;
            var binding = new JObject { ["bindingId"] = "demo.modes.binding", ["eventType"] = eventType,
                ["script"] = "lua/binding.lua", ["version"] = "1.0.0" };
            if (eventType == "CandidateSetBuilding") binding["candidateKind"] = "city_destination";
            if (eventType == "EffectCompleted") binding["completedEffectTypes"] = new JArray("effect.resource.gain");
            owner["ruleBindings"] = new JArray(binding);

            // 完成事件仍须显式声明；较早的其他绑定可以从旧清单推导能力。
            sibling["requiredCapabilities"] = new JArray(capability);
            if (eventType == "EffectCompleted")
                Assert.That(Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest)).Message, Does.Contain(capability));
            else
            {
                var legacy = DiyPackManifest.Parse(manifest);
                var effective = expansion ? legacy.Expansions.Single().EffectiveRequiredCapabilities : legacy.RuleModes.Single().EffectiveRequiredCapabilities;
                Assert.That(effective, Does.Contain(capability));
            }
            sibling["requiredCapabilities"] = new JArray();
            var packageCapabilities = (JArray)manifest["requiredCapabilities"];
            packageCapabilities.Add(capability);
            Assert.DoesNotThrow(() => DiyPackManifest.Parse(manifest));
            packageCapabilities.RemoveAt(packageCapabilities.Count - 1);
            Assert.That(packageCapabilities.Values<string>(), Does.Not.Contain(capability));
            owner["requiredCapabilities"] = new JArray(capability);
            Assert.DoesNotThrow(() => DiyPackManifest.Parse(manifest));
            if (eventType != "EffectCompleted") owner["requiredCapabilities"] = new JArray();
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/binding.lua", "return function(ctx) return {} end");

            string originalManifest = File.ReadAllText(Path.Combine(root, "pack.json"));
            var snapshot = DiyPackSnapshot.Load(root);
            Assert.That(File.ReadAllText(Path.Combine(root, "pack.json")), Is.EqualTo(originalManifest));
            Assert.That(DiyPackSnapshot.Load(root).ContentHash, Is.EqualTo(snapshot.ContentHash));
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var available = YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities;
            var without = available.Where(value => value != capability).ToArray();
            string selectedMode = expansion ? "standard" : "demo.modes.expedition";
            string[] selectedExpansions = expansion ? new[] { "core", "demo.modes.rules" } : new[] { "core" };
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve(selectedMode, selectedExpansions,
                "map-three-players", new[] { snapshot }, (string)core.Manifest["version"], core.ContentHash, without, CoreOptionsFixture.Value)).Message,
                Does.Contain(capability));
            var selected = DiyContentResolution.Resolve(selectedMode, selectedExpansions,
                "map-three-players", new[] { snapshot }, (string)core.Manifest["version"], core.ContentHash, available, CoreOptionsFixture.Value);
            if (eventType != "EffectCompleted")
            {
                var combined = ExternalContentPack.ComposeDiy(core, selected);
                var combinedManifest = combined.Manifest;
                var records = (JArray)combinedManifest[expansion ? "expansionDependencies" : "ruleModes"];
                var record = records.OfType<JObject>().Single(item => (string)item[expansion ? "expansionId" : "modeId"] ==
                    (expansion ? "demo.modes.rules" : "demo.modes.expedition"));
                Assert.That(((JArray)record["requiredCapabilities"]).Values<string>(), Does.Not.Contain(capability),
                    "推导能力不得写入组合清单并改变旧内容身份");
            }

            // 同包中未选中的模式或扩展不应要求自己的局部能力。
            string inactiveMode = expansion ? "demo.modes.expedition" : "standard";
            string[] inactiveExpansions = expansion ? new[] { "core" } : new[] { "core", "demo.modes.rules" };
            Assert.DoesNotThrow(() => DiyContentResolution.Resolve(inactiveMode, inactiveExpansions,
                "map-three-players", new[] { snapshot }, (string)core.Manifest["version"], core.ContentHash, without, CoreOptionsFixture.Value));
        }

        [Test]
        public void CompletedRuleBindingRequiresExactEffectTypesAndValidPolicy()
        {
            var manifest = ModeManifest();
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.completed.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.completed", ["eventType"] = "EffectCompleted",
                ["script"] = "lua/completed.lua", ["version"] = "1.0.0" };
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray("effect.resource.gain", "fixture.signal");
            var parsed = DiyPackManifest.Parse(manifest).RuleModes.Single().RuleBindings.Single();
            Assert.That(parsed.CompletedEffectTypes, Is.EquivalentTo(new[] { "effect.resource.gain", "fixture.signal" }));
            Assert.That(parsed.CompletionPolicy, Is.EqualTo("succeeded"));
            binding["completionPolicy"] = "terminal";
            Assert.That(DiyPackManifest.Parse(manifest).RuleModes.Single().RuleBindings.Single().CompletionPolicy, Is.EqualTo("terminal"));
            ((JArray)manifest["requiredCapabilities"]).RemoveAt(2);
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            ((JArray)manifest["ruleModes"][0]["requiredCapabilities"]).Add("diy.rule_bindings.completed.v1");
            Assert.DoesNotThrow(() => DiyPackManifest.Parse(manifest));
            ((JArray)manifest["ruleModes"][0]["requiredCapabilities"]).RemoveAt(0);
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.completed.v1");
            binding["completionPolicy"] = "other";
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completionPolicy"] = "succeeded";
            binding["completedEffectTypes"] = new JArray("effect.resource.gain", "effect.resource.gain");
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray("effect.ability.source.resolve");
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray("effect resource");
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray("effect.resource.gain\n");
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray(Enumerable.Range(0, 65).Select(index => "fixture." + index));
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
            binding["completedEffectTypes"] = new JArray("effect.resource.gain");
            binding["eventType"] = "RoundStarted";
            Assert.Throws<InvalidDataException>(() => DiyPackManifest.Parse(manifest));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectedModeOrExpansionObservesOnlyFilteredEffectCompletions(bool expansion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray("diy.pack.v1", "diy.rule_modes.v1", "diy.rule_bindings.completed.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.completed", ["eventType"] = "EffectCompleted",
                ["completedEffectTypes"] = new JArray("effect.resource.gain", "fixture.signal"),
                ["script"] = "lua/completed.lua", ["version"] = "1.0.0" };
            var extension = new JObject { ["expansionId"] = "demo.modes.observer", ["displayName"] = "完成事件夹具",
                ["description"] = "完成观察", ["requires"] = new JArray("core") };
            manifest["expansions"] = new JArray(extension);
            if (expansion) extension["ruleBindings"] = new JArray(binding);
            else manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/completed.lua", "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            ExternalContentPack Compose() => ExternalContentPack.ComposeDiy(core, DiyContentResolution.Resolve("demo.modes.expedition",
                expansion ? new[] { "core", "demo.modes.observer" } : new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value));
            var combined = Compose();
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyContentResolution.Resolve("demo.modes.expedition",
                expansion ? new[] { "core", "demo.modes.observer" } : new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities.Where(capability => capability != "diy.rule_bindings.completed.v1"),
                CoreOptionsFixture.Value)).Message, Does.Contain("diy.rule_bindings.completed.v1"));
            var inactive = ExternalContentPack.ComposeDiy(core, DiyContentResolution.Resolve(
                expansion ? "demo.modes.expedition" : "standard", new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value));
            var inactiveRegistry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(inactiveRegistry);
            DiyRuleBindingCatalog.Register(inactiveRegistry, inactive);
            var inactiveState = new YC.Domain.State.GameState { GameId = "diy-inactive-completion", Round = 1, MapId = "map-three-players",
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var inactiveEngine = new YC.Domain.Effects.EffectTreeExecutor(inactiveState, inactiveRegistry);
            inactiveEngine.CreateRoot(YC.Domain.Effects.ResourceEffectSpecFactory.Gain(1, YC.Domain.Rules.ResourceType.Originium, 1));
            Assert.That(inactiveEngine.RunUntilQuiescent().Faulted, Is.False, inactiveState.EffectRuntime.LastFaultMessage);
            Assert.That(inactiveState.FindPlayer(1).Score, Is.Zero);
            var registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            registry.Register("fixture.signal", ctx => YC.Domain.Effects.EffectStepResult.Completed());
            DiyRuleBindingCatalog.Register(registry, combined);
            DiyRuleBindingCatalog.Register(registry, combined);
            var state = new YC.Domain.State.GameState { GameId = "diy-completion-fixture", Round = 1, MapId = "map-three-players",
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            engine.CreateRoot(YC.Domain.Effects.ResourceEffectSpecFactory.Gain(1, YC.Domain.Rules.ResourceType.Originium, 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            engine.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.signal", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count(receipt => receipt.AbilityId == "demo.modes.completed" && receipt.Succeeded), Is.EqualTo(2));
            state = YC.Domain.State.GameStateCloneService.DeepClone(state);
            engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            engine.CreateRoot(YC.Domain.Effects.ResourceEffectSpecFactory.Gain(1, YC.Domain.Rules.ResourceType.Originium, 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count(receipt => receipt.AbilityId == "demo.modes.completed" && receipt.Succeeded), Is.EqualTo(3));
            binding["completedEffectTypes"] = new JArray("fixture.signal");
            Write(root, "pack.json", manifest.ToString());
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyRuleBindingCatalog.Register(registry, Compose())).Message,
                Does.Contain("diy_rule_binding_effect_conflict"));
        }

        [TestCase("succeeded", 0)]
        [TestCase("terminal", 1)]
        public void CompletedRuleBindingUsesExistingTerminalOutcomePolicy(string policy, int expectedScore)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.completed.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.failed",
                ["eventType"] = "EffectCompleted", ["completedEffectTypes"] = new JArray("fixture.failed"),
                ["completionPolicy"] = policy, ["script"] = "lua/completed.lua", ["version"] = "1.0.0" });
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/completed.lua", "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            var registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            registry.Register("fixture.failed", ctx => YC.Domain.Effects.EffectStepResult.Failed("fixture_expected_failure"));
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, selected));
            var state = new YC.Domain.State.GameState { GameId = "diy-failed-completion", Round = 1, MapId = "map-three-players",
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            engine.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.failed", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(expectedScore));
        }

        [Test]
        public void CompletedRuleBindingContinuationSurvivesChoiceAndRunsOnce()
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.completed.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.completed",
                ["eventType"] = "EffectCompleted", ["completedEffectTypes"] = new JArray("fixture.signal"),
                ["script"] = "lua/choice.lua", ["version"] = "1.0.0",
                ["completion"] = new JObject { ["handlerId"] = "demo.modes.completed.done", ["script"] = "lua/after.lua", ["version"] = "1.0.0" } });
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/choice.lua", "return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'go'},minSelections=1,maxSelections=1,completionHandlerId='demo.modes.completed.done'})} end");
            Write(root, "lua/after.lua", "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=2})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players",
                new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            var registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            YC.Domain.Effects.GenericLuaEffectExecutor.Register(registry);
            registry.Register("fixture.signal", ctx => YC.Domain.Effects.EffectStepResult.Completed());
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, selected));
            var state = new YC.Domain.State.GameState { GameId = "diy-completion-continuation", Round = 1, MapId = "map-three-players",
                Players = new System.Collections.Generic.List<YC.Domain.State.PlayerState> { new YC.Domain.State.PlayerState { PlayerId = 1 } } };
            var engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            engine.CreateRoot(YC.Domain.Effects.EffectSpec.Create("fixture.signal", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            state = YC.Domain.State.GameStateCloneService.DeepClone(state);
            registry = new YC.Domain.Effects.EffectRegistry();
            YC.Domain.Effects.ResourceEffectExecutor.Register(registry);
            YC.Domain.Effects.GenericLuaEffectExecutor.Register(registry);
            registry.Register("fixture.signal", ctx => YC.Domain.Effects.EffectStepResult.Completed());
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, selected));
            engine = new YC.Domain.Effects.EffectTreeExecutor(state, registry);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                YC.Domain.State.NormalizedValue.CreateString("go"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                YC.Domain.State.NormalizedValue.CreateString("go"), out _), Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectedModeOrExpansionUsesExistingReadOnlyCityCandidateContract(bool expansion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            manifest["requiredCapabilities"] = new JArray("diy.pack.v1", "diy.rule_modes.v1", "diy.rule_bindings.candidate.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.city", ["eventType"] = "CandidateSetBuilding",
                ["candidateKind"] = "city_destination", ["script"] = "lua/city.lua", ["version"] = "1.0.0" };
            var extension = new JObject { ["expansionId"] = "demo.modes.city_expansion", ["displayName"] = "城市夹具",
                ["description"] = "城市候选策略", ["requires"] = new JArray("core") };
            manifest["expansions"] = new JArray(extension);
            if (expansion) extension["ruleBindings"] = new JArray(binding);
            else manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/city.lua", "return function(ctx) return {Candidate.Remove({ids={ctx.payload.candidatePoolIds[1]},reasonCode='diy.fixture'})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            ExternalContentPack Compose() => ExternalContentPack.ComposeDiy(core, DiyContentResolution.Resolve(
                expansion ? "standard" : "demo.modes.expedition", expansion ? new[] { "core", "demo.modes.city_expansion" } : new[] { "core" },
                "map-three-players", new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value));
            var selected = Compose();
            using (var fixture = new CollectionPlanTestFixture())
            {
                string target = fixture.RemotePoint();
                var query = new YC.Domain.Movement.CityDestinationQuery(1, "diy.fixture", "specified", "standard", new[] { target });
                var service = new YC.Domain.Movement.CityDestinationQueryService(fixture.Map, fixture.Registry);
                Assert.That(service.Query(fixture.State, query).AllowedLocationIds, Does.Contain(target));
                string before = fixture.Snapshot();
                // 同一个包被其他玩法引用时，未选中的模式/扩展不能留下候选规则。
                var inactive = DiyContentResolution.Resolve(expansion ? "demo.modes.expedition" : "standard",
                    expansion ? new[] { "core" } : new[] { "core", "demo.modes.city_expansion" }, "map-three-players",
                    new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
                DiyRuleBindingCatalog.Register(fixture.Registry, ExternalContentPack.ComposeDiy(core, inactive));
                Assert.That(service.Query(fixture.State, query).AllowedLocationIds, Does.Contain(target));
                DiyRuleBindingCatalog.Register(fixture.Registry, selected);
                DiyRuleBindingCatalog.Register(fixture.Registry, selected);
                Assert.That(service.Query(fixture.State, query).AllowedLocationIds, Does.Not.Contain(target));
                Assert.That(fixture.Snapshot(), Is.EqualTo(before));
                Write(root, "lua/city.lua", "return function(ctx) return {} end");
                Assert.That(Assert.Throws<InvalidDataException>(() => DiyRuleBindingCatalog.Register(fixture.Registry, Compose())).Message,
                    Does.Contain("candidate_conflict"));
                Assert.That(service.Query(fixture.State, query).AllowedLocationIds, Does.Not.Contain(target));
                Assert.That(fixture.Snapshot(), Is.EqualTo(before));
            }
        }

        [TestCase("return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=9})} end")]
        [TestCase("return function(ctx) return {Candidate.Remove({ids={'city.move.target:unknown'},reasonCode='invalid'})} end")]
        public void ModeCandidateQueryRejectsEffectOrOutOfPoolWithoutChangingState(string script)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.candidate.v1");
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(new JObject { ["bindingId"] = "demo.modes.city", ["eventType"] = "CandidateSetBuilding",
                ["candidateKind"] = "city_destination", ["script"] = "lua/city.lua", ["version"] = "1.0.0" });
            Write(root, "pack.json", manifest.ToString()); Write(root, "lua/city.lua", script);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var selected = DiyContentResolution.Resolve("demo.modes.expedition", new[] { "core" }, "map-three-players", new[] { DiyPackSnapshot.Load(root) },
                (string)core.Manifest["version"], core.ContentHash, YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            using (var fixture = new CollectionPlanTestFixture())
            {
                DiyRuleBindingCatalog.Register(fixture.Registry, ExternalContentPack.ComposeDiy(core, selected));
                string target = fixture.RemotePoint(), before = fixture.Snapshot();
                Assert.Throws<InvalidOperationException>(() => new YC.Domain.Movement.CityDestinationQueryService(fixture.Map, fixture.Registry).Query(fixture.State,
                    new YC.Domain.Movement.CityDestinationQuery(1, "diy.fixture", "specified", "standard", new[] { target })));
                Assert.That(fixture.Snapshot(), Is.EqualTo(before));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectedModeOrExpansionUsesExistingFacilityCandidateContract(bool expansion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.candidate.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.facility", ["eventType"] = "CandidateSetBuilding",
                ["candidateKind"] = "facility.build.card", ["script"] = "lua/facility.lua", ["version"] = "1.0.0" };
            var extension = new JObject { ["expansionId"] = "demo.modes.facility_expansion", ["displayName"] = "设施夹具",
                ["description"] = "设施候选策略", ["requires"] = new JArray("core") };
            manifest["expansions"] = new JArray(extension);
            if (expansion) extension["ruleBindings"] = new JArray(binding);
            else manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            Write(root, "pack.json", manifest.ToString());
            Write(root, "lua/facility.lua", "return function(ctx) return {Candidate.Remove({ids={ctx.payload.candidatePoolIds[1]},reasonCode='diy.fixture'})} end");
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = expansion ? "standard" : "demo.modes.expedition",
                MapId = "map-three-players", PlayerCount = 2, EnabledExpansionIds = expansion
                    ? new System.Collections.Generic.List<string> { "core", "demo.modes.facility_expansion" }
                    : new System.Collections.Generic.List<string> { "core" } };
            DiyResolvedContent Resolve(string mode, System.Collections.Generic.IEnumerable<string> enabled) => DiyContentResolution.Resolve(
                mode, enabled, configuration.MapId, new[] { DiyPackSnapshot.Load(root) }, (string)core.Manifest["version"], core.ContentHash,
                YC.Domain.Rules.RuleModeReadiness.ImplementedCapabilities, CoreOptionsFixture.Value);
            var selected = Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds);
            configuration.LockedContentPacks = selected.CreateRuleLocks();
            var state = CreateInitialStateFixture(ActiveContentCatalogBuilder.Build(core, configuration, selected), configuration);
            // 隔离纯查询夹具需处于设施建设可发生的行动轮，不能用开局入场状态调用行动候选。
            state.Round = 1; state.ActionRound = 1; state.Phase = YC.Domain.Rules.GamePhase.ActionRound1;
            var registry = new YC.Domain.Effects.EffectRegistry();
            var options = new YC.Domain.Facilities.FacilityBuildCandidateOptions { SourceZone = "deck" };
            string before = Newtonsoft.Json.JsonConvert.SerializeObject(state);
            var baseline = registry.FacilityBuildCandidates.Resolve(state, 1, options).FinalItemIds.ToArray();
            Assert.That(baseline, Is.Not.Empty);
            var inactive = Resolve(expansion ? "demo.modes.expedition" : "standard", expansion
                ? new[] { "core" } : new[] { "core", "demo.modes.facility_expansion" });
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, inactive));
            Assert.That(registry.FacilityBuildCandidates.Resolve(state, 1, options).FinalItemIds, Is.EqualTo(baseline));
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, selected));
            DiyRuleBindingCatalog.Register(registry, ExternalContentPack.ComposeDiy(core, selected));
            var filtered = registry.FacilityBuildCandidates.Resolve(state, 1, options).FinalItemIds;
            Assert.That(filtered.Count, Is.EqualTo(baseline.Length - 1));
            Assert.That(filtered.All(baseline.Contains), Is.True);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Write(root, "lua/facility.lua", "return function(ctx) return {} end");
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyRuleBindingCatalog.Register(registry,
                ExternalContentPack.ComposeDiy(core, Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds)))).Message,
                Does.Contain("candidate_conflict"));
            Write(root, "lua/facility.lua", "return function(ctx) return {Candidate.Remove({ids={ctx.payload.candidatePoolIds[1]},reasonCode='diy.fixture'})} end");
            binding["candidateKind"] = "city_destination";
            Write(root, "pack.json", manifest.ToString());
            Assert.That(Assert.Throws<InvalidDataException>(() => DiyRuleBindingCatalog.Register(registry,
                ExternalContentPack.ComposeDiy(core, Resolve(configuration.RuleModeId, configuration.EnabledExpansionIds)))).Message,
                Does.Contain("candidate_conflict"));
        }

        [TestCase("CandidateSetBuilding", "", false)]
        [TestCase("CandidateSetBuilding", "unknown", false)]
        [TestCase("CandidateSetBuilding", "city_destination", true)]
        [TestCase("CandidateSetBuilding", "facility.build.card", true)]
        [TestCase("PlayerCleanupStarted", "city_destination", false)]
        public void RuleBindingRejectsInvalidQueryShape(string eventType, string kind, bool completion)
        {
            string root = MakeMode(NewRoot());
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.cleanup.v1");
            ((JArray)manifest["requiredCapabilities"]).Add("diy.rule_bindings.candidate.v1");
            var binding = new JObject { ["bindingId"] = "demo.modes.test", ["eventType"] = eventType, ["script"] = "lua/test.lua", ["version"] = "1.0.0" };
            if (kind.Length > 0) binding["candidateKind"] = kind;
            if (completion) binding["completion"] = new JObject { ["handlerId"] = "demo.modes.done", ["script"] = "lua/test.lua", ["version"] = "1.0.0" };
            manifest["ruleModes"][0]["ruleBindings"] = new JArray(binding);
            Write(root, "pack.json", manifest.ToString()); Write(root, "lua/test.lua", "return function(ctx) return {} end");
            Assert.Throws<InvalidDataException>(() => DiyPackSnapshot.Load(root));
        }

        [Test]
        public void ConflictingModeIdentityDoesNotHideIndependentInstalledModes()
        {
            string dataRoot = NewRoot(); MakeMode(dataRoot);
            string otherRoot = Path.Combine(dataRoot, "Mods", "demo", "1.0.0");
            var other = ModeManifest(); other["packId"] = "demo";
            var independent = (JObject)other["ruleModes"][0].DeepClone(); independent["modeId"] = "demo.other";
            ((JArray)other["ruleModes"]).Add(independent); Write(otherRoot, "pack.json", other.ToString());
            var found = DiyPackDiscovery.Scan(new DiyDataPaths(dataRoot)); var profile = DiyVersionProfile.Empty(); profile.BindUniqueDefaults(found);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var repository = new DiyInstalledRepository(core, found, profile); var options = repository.GetOptions();
            Assert.That(options.Modes.Select(x => x.Id), Does.Contain("standard").And.Contain("demo.other"));
            Assert.That(options.Modes.Select(x => x.Id), Does.Not.Contain("demo.modes.expedition"));
            Assert.That(options.Diagnostics.Single(x => x.Contains("模式身份冲突")), Does.Contain("demo/1.0.0").And.Contain("demo.modes/1.0.0"));
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { RuleModeId = "demo.other", MapId = "map-three-players", PlayerCount = 2 };
            Assert.DoesNotThrow(() => repository.ResolveLocal(configuration));
            configuration.RuleModeId = "demo.modes.expedition";
            Assert.That(Assert.Throws<InvalidDataException>(() => repository.ResolveLocal(configuration)).Message, Does.Contain("多个包声明相同模式"));
        }

        [Test]
        public void ReplacementChainsKeepRootSlotAndRejectIndependentAppend()
        {
            string firstRoot = MakeEventPack(NewRoot()), secondRoot = MakeEventPack(NewRoot());
            foreach (string relative in new[] { "pack.json", "event/ruins.json", "recipes/ruins.json" })
            {
                string path = Path.Combine(secondRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Write(secondRoot, relative, File.ReadAllText(path).Replace("demo.events", "demo.other"));
            }
            var firstDefinition = JObject.Parse(File.ReadAllText(Path.Combine(firstRoot, "event", "ruins.json")));
            firstDefinition["replaces"] = new JObject { ["expansionId"] = "core", ["contentType"] = "event", ["definitionId"] = "event_green_01" };
            Write(firstRoot, "event/ruins.json", firstDefinition.ToString());
            var secondDefinition = JObject.Parse(File.ReadAllText(Path.Combine(secondRoot, "event", "ruins.json")));
            secondDefinition["replaces"] = new JObject { ["expansionId"] = "demo.events.standard", ["contentType"] = "event", ["definitionId"] = "demo.events.ruins" };
            Write(secondRoot, "event/ruins.json", secondDefinition.ToString());
            var secondManifest = JObject.Parse(File.ReadAllText(Path.Combine(secondRoot, "pack.json")));
            ((JArray)secondManifest["dependencies"]).Add(new JObject { ["packId"] = "demo.events", ["versionRange"] = "=1.0.0" });
            Write(secondRoot, "pack.json", secondManifest.ToString());
            var first = DiyPackSnapshot.Load(firstRoot); var second = DiyPackSnapshot.Load(secondRoot);
            var resolved = DiyContentResolution.Resolve("standard", new[] { "core", "demo.events.standard", "demo.other.standard" }, "map-three-players",
                new[] { second, first }, "2.2.0", "core-hash", new[] { "diy.pack.v1", "event.choices.v1" }, CoreOptionsFixture.Value);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var composite = DiyCompositeCatalog.Build(core, resolved);
            var leaf = composite.Definitions.Single(x => x.RuntimeId == "event_green_01");
            Assert.That(leaf.DefinitionId, Is.EqualTo("demo.other.ruins"));
            Assert.That(leaf.SourcePackId, Is.EqualTo("demo.other"));
            Assert.That(composite.Definitions.Any(x => x.DefinitionId == "event_green_01" || x.DefinitionId == "demo.events.ruins"), Is.False);
            Assert.That(Assert.Throws<InvalidDataException>(() => composite.BuildEventAdditions("map-three-players")).Message, Does.Contain("不能重复追加替换项"));
        }
    }
}
