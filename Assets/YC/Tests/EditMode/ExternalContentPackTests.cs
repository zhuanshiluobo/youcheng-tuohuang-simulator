using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class ExternalContentPackTests
    {
        private static string CoreRoot => Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
        private static string CreatePack(string script = null)
        {
            string root = Path.GetFullPath(Path.Combine("Logs/ExternalContentTests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            var card = new JObject
            {
                ["definitionId"] = "custom", ["contentType"] = "character", ["version"] = "1.0.0", ["displayName"] = "外部测试角色",
                ["artwork"] = "front.jpg", ["tags"] = new JArray(), ["playerMarkerZones"] = new JArray(),
                ["expansionId"] = "test", ["replaces"] = JValue.CreateNull(),
                ["data"] = new JObject(),
                ["abilities"] = new JArray(
                    new JObject { ["abilityId"] = "custom.strategy", ["mode"] = "strategy", ["skillType"] = "normal", ["subscriptionId"] = "custom.strategy.handler", ["script"] = "effect.lua", ["version"] = "1.0.0" },
                    new JObject { ["abilityId"] = "custom.tactic", ["mode"] = "tactic", ["skillType"] = "normal", ["subscriptionId"] = "custom.tactic.handler", ["script"] = "effect.lua", ["version"] = "1.0.0" })
            };
            File.WriteAllText(Path.Combine(root, "card.json"), card.ToString());
            File.WriteAllText(Path.Combine(root, "pack.json"), new JObject { ["schemaVersion"] = 2, ["packId"] = "test", ["version"] = "1", ["definitions"] = new JArray("card.json") }.ToString());
            File.Copy(Path.Combine(CoreRoot, "artwork/character/elysium.jpg"), Path.Combine(root, "front.jpg"));
            File.WriteAllText(Path.Combine(root, "effect.lua"), script ?? "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=7}) } end");
            return root;
        }
        private static void ResetCharacters() => typeof(CharacterCardDatabase).GetMethod("ResetForTests", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        private static EffectRegistry Registry(ExternalContentPack pack)
        {
            var registry = new EffectRegistry();
            new RoundExecutionService(registry);
            ResourceEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            return registry;
        }
        private static GameState State(ExternalContentPack pack)
        {
            ResetCharacters(); CharacterCardDatabase.Initialize(pack.CreateCharacters());
            var player = new PlayerState { PlayerId = 1, Color = PlayerColor.Red, RemainingMainActionsThisTurn = 1 };
            CharacterCardDatabase.InitializePlayerHand(player);
            player.CoveredCharacterCardId = player.HandCardIds[0]; player.HandCardIds.Clear();
            return new GameState { GameId = "external-test", Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, Players = { player } };
        }
        private static void Use(GameState state, EffectRegistry registry)
        {
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = state.FindPlayer(1).CoveredCharacterCardId };
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Strategy;
            var result = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason + state.EffectRuntime.LastFaultMessage);
        }
        [TearDown]
        public void RestoreCharacters()
        {
            ResetCharacters(); FacilityCardDatabaseSetUpFixture.InitializeEventAndCharacterCardDatabases();
        }
        [Test]
        public void CorePack_LoadsAllRuntimeDefinitionsScriptsAndArtwork()
        {
            var pack = ExternalContentPack.Load(CoreRoot);
            Assert.That(pack.Definitions, Has.Count.EqualTo(79));
            Assert.That(pack.CreateCharacters(), Has.Count.EqualTo(5));
            Assert.That(pack.CreateEvents(), Has.Count.EqualTo(22));
            Assert.That(pack.CreateFacilities(), Has.Count.EqualTo(46));
            Assert.That(pack.CreateCityStyles(), Has.Count.EqualTo(6));
            Assert.That(pack.CreateSpecialActions(), Has.Count.EqualTo(5));
            Assert.That(pack.CreateEvents().Single(e => e.CardId == "event_green_01").ChoiceRewards[0].OriginiumShard, Is.EqualTo(3));
            foreach (var card in pack.Definitions)
            {
                Assert.That(pack.GetArtworkBytes(card.Artwork).Length, Is.GreaterThan(100));
                foreach (var ability in card.Abilities)
                    Assert.That(pack.GetScript(ability.Script), Does.Not.Contain("ResolveCharacterCardEffect"), ability.AbilityId);
            }
            // 与当前启动目录合同兼容；不是只验证 JSON 能反序列化。
            Assert.DoesNotThrow(() => EventCardDatabase.Initialize(pack.CreateEvents()));
            Assert.DoesNotThrow(() => FacilityCardDatabase.Initialize(pack.CreateFacilities()));
            Assert.DoesNotThrow(() => CityStyleDatabase.Initialize(pack.CreateCityStyles()));
            Assert.DoesNotThrow(() => SpecialActionDatabase.Initialize(pack.CreateSpecialActions()));
            Assert.That(pack.ContentHash, Is.Not.Empty);
        }
        [Test]
        public void NewExternalCharacter_ActivatesWithoutEnumOrCSharpCardBranch()
        {
            var pack = ExternalContentPack.Load(CreatePack());
            var state = State(pack); var registry = Registry(pack);
            Assert.That(CharacterCardDatabase.Get(state.FindPlayer(1).CoveredCharacterCardId).Name, Is.EqualTo("外部测试角色"));
            Use(state, registry);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
            Assert.That(state.FindPlayer(1).UsedCharacterThisRound, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CharacterAbilityEffectExecutor.AbilityEffectTypeId), Is.False);
            Assert.That(state.ContentPackHash, Is.EqualTo(pack.ContentHash));
            var view = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(GameStateViewProjector.ToClientState(view).ContentPackHash, Is.EqualTo(pack.ContentHash));
        }
        [Test]
        public void ChoiceBranches_OnlySelectedBranchExecutesAfterRecovery()
        {
            var pack = ExternalContentPack.Load(CreatePack(@"return function(ctx) return { Effect.Choice({player=ctx.playerId, branches={
                {id='small',effects={Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=2})}},
                {id='large',effects={Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=9})}}
            }}) } end"));
            var state = State(pack); var registry = Registry(pack); Use(state, registry);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            state = GameStateCloneService.DeepClone(state); registry = Registry(pack);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var command = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { "large" });
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, command).Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(9));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.EqualTo(1));
        }
        [Test]
        public void ChangedExternalScriptChangesHashAndRejectsOldState()
        {
            string root = CreatePack(); var old = ExternalContentPack.Load(root);
            var state = State(old); Use(state, Registry(old));
            File.WriteAllText(Path.Combine(root, "effect.lua"), "return function(ctx) return {} end");
            var changed = ExternalContentPack.Load(root);
            Assert.That(changed.ContentHash, Is.Not.EqualTo(old.ContentHash));
            Assert.Throws<KernelException>(() => new EffectTreeExecutor(GameStateCloneService.DeepClone(state), Registry(changed)));
        }
        [TestCase("escape")]
        [TestCase("duplicate")]
        [TestCase("script")]
        [TestCase("missing_artwork")]
        public void BrokenContentPackFailsBeforeActivation(string fault)
        {
            string root = CreatePack();
            string path = Path.Combine(root, "card.json"); var card = JObject.Parse(File.ReadAllText(path));
            if (fault == "escape") card["artwork"] = "../front.jpg";
            if (fault == "missing_artwork") card["artwork"] = "absent.jpg";
            if (fault == "duplicate") card["abilities"][1]["abilityId"] = "custom.strategy";
            if (fault == "script") File.WriteAllText(Path.Combine(root, "effect.lua"), "return function(");
            File.WriteAllText(path, card.ToString());
            Assert.That(() => ExternalContentPack.Load(root), Throws.Exception);
        }
        [TestCase("normal", false)]
        [TestCase("one_shot", false)]
        [TestCase("persistent", false)]
        [TestCase("persistent", true)]
        public void SkillMetadata_RoundTripsWithOptionalPersistentZone(string type, bool hasZone)
        {
            string root = CreatePack(); string path = Path.Combine(root, "card.json");
            var card = JObject.Parse(File.ReadAllText(path));
            card["abilities"][0]["skillType"] = type;
            card["abilities"][0]["hasSpecialZone"] = hasZone;
            card["abilities"][0]["specialZoneId"] = hasZone ? "skill-area" : "";
            if (hasZone) card["playerMarkerZones"] = new JArray(new JObject { ["zoneId"] = "skill-area", ["capacity"] = 3 });
            card["replaces"] = JValue.CreateNull();
            File.WriteAllText(path, card.ToString());
            var definition = ExternalContentPack.Load(root).Definitions.Single();
            Assert.That(definition.ExpansionId, Is.EqualTo("test"));
            Assert.That(definition.Replaces, Is.Null);
            Assert.That(definition.Abilities[0].SkillType, Is.EqualTo(type));
            Assert.That(definition.Abilities[0].HasSpecialZone, Is.EqualTo(hasZone));
            Assert.That(definition.Abilities[0].SpecialZoneId, Is.EqualTo(hasZone ? "skill-area" : ""));
        }
        [TestCase("expansion_missing")]
        [TestCase("replacement_missing")]
        [TestCase("replacement_type")]
        [TestCase("replacement_self")]
        [TestCase("skill_missing")]
        [TestCase("skill_unknown")]
        [TestCase("persistent_zone_unspecified")]
        [TestCase("zone_missing")]
        [TestCase("normal_zone")]
        [TestCase("zone_disabled_but_referenced")]
        public void InvalidDefinitionMetadata_IsRejected(string fault)
        {
            string root = CreatePack(); string path = Path.Combine(root, "card.json");
            var card = JObject.Parse(File.ReadAllText(path)); var ability = (JObject)card["abilities"][0];
            if (fault == "expansion_missing") card.Remove("expansionId");
            if (fault == "replacement_missing") card.Remove("replaces");
            if (fault.StartsWith("replacement_")) card["replaces"] = new JObject
                { ["expansionId"] = "test", ["contentType"] = fault == "replacement_type" ? "facility" : "character", ["definitionId"] = fault == "replacement_self" ? "custom" : "other" };
            if (fault == "replacement_missing") card.Remove("replaces");
            if (fault == "skill_missing") ability.Remove("skillType");
            if (fault == "skill_unknown") ability["skillType"] = "unknown";
            if (fault == "persistent_zone_unspecified") ability["skillType"] = "persistent";
            if (fault == "zone_missing" || fault == "normal_zone")
            { ability["skillType"] = fault == "normal_zone" ? "normal" : "persistent"; ability["hasSpecialZone"] = true; ability["specialZoneId"] = "missing"; }
            if (fault == "zone_disabled_but_referenced") { ability["hasSpecialZone"] = false; ability["specialZoneId"] = "unexpected"; }
            File.WriteAllText(path, card.ToString());
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }
        [Test]
        public void ReplacementReference_ValidatesLoadedTargetAndRejectsCycles()
        {
            string root = CreatePack(); string path = Path.Combine(root, "card.json");
            var first = JObject.Parse(File.ReadAllText(path)); var other = (JObject)first.DeepClone();
            other["definitionId"] = "other";
            foreach (JObject ability in (JArray)other["abilities"])
            { ability["abilityId"] = "other." + (string)ability["mode"]; ability["subscriptionId"] = "other." + (string)ability["mode"]; }
            first["replaces"] = new JObject { ["expansionId"] = "test", ["contentType"] = "character", ["definitionId"] = "other" };
            File.WriteAllText(path, first.ToString()); File.WriteAllText(Path.Combine(root, "other.json"), other.ToString());
            var manifestPath = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            ((JArray)manifest["definitions"]).Add("other.json"); File.WriteAllText(manifestPath, manifest.ToString());
            Assert.DoesNotThrow(() => ExternalContentPack.Load(root));
            other["expansionId"] = "wrong"; File.WriteAllText(Path.Combine(root, "other.json"), other.ToString());
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
            other["expansionId"] = "test";
            other["replaces"] = new JObject { ["expansionId"] = "test", ["contentType"] = "character", ["definitionId"] = "custom" };
            File.WriteAllText(Path.Combine(root, "other.json"), other.ToString());
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }
        private static void AddReplacement(string root, string id, string target = "custom", string expansion = "expansion")
        {
            var card = JObject.Parse(File.ReadAllText(Path.Combine(root, "card.json")));
            card["definitionId"] = id; card["expansionId"] = expansion; card["displayName"] = id;
            card["artwork"] = id + ".jpg";
            File.Copy(Path.Combine(root, "front.jpg"), Path.Combine(root, id + ".jpg"));
            card["replaces"] = new JObject { ["expansionId"] = target == "custom" ? "test" : "expansion", ["contentType"] = "character", ["definitionId"] = target };
            foreach (JObject ability in (JArray)card["abilities"])
            {
                ability["abilityId"] = id + "." + (string)ability["mode"];
                ability["subscriptionId"] = id + "." + (string)ability["mode"];
                ability["script"] = "replacement.lua";
            }
            File.WriteAllText(Path.Combine(root, "replacement.lua"), "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=9}) } end");
            File.WriteAllText(Path.Combine(root, id + ".json"), card.ToString());
            var path = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(path));
            ((JArray)manifest["definitions"]).Add(id + ".json"); File.WriteAllText(path, manifest.ToString());
        }
        [Test]
        public void ExpansionReplacement_UsesNewScriptAndArtworkInOriginalHandSlot()
        {
            string root = CreatePack(); AddReplacement(root, "replacement");
            var pack = ExternalContentPack.Load(root);
            Assert.That(pack.Definitions, Has.Count.EqualTo(2));
            Assert.That(pack.ActiveDefinitions, Has.Count.EqualTo(1));
            Assert.That(pack.ActiveDefinitions[0].DefinitionId, Is.EqualTo("replacement"));
            Assert.That(pack.ActiveDefinitions[0].RuntimeId, Is.EqualTo("custom"));
            Assert.That(pack.FindArtwork("character", "custom"), Is.EqualTo("replacement.jpg"));
            Assert.That(pack.FindArtwork("character", "replacement"), Is.EqualTo("replacement.jpg"));
            var state = State(pack); var registry = Registry(pack);
            Assert.That(CharacterCardDatabase.Get(state.FindPlayer(1).CoveredCharacterCardId).Name, Is.EqualTo("replacement"));
            Assert.That(registry.CharacterActivationSubscriptions.ContainsKey("custom.strategy"), Is.False);
            Use(state, registry);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(9));
            var saved = GameStateCloneService.DeepClone(state);
            Assert.DoesNotThrow(() => new EffectTreeExecutor(saved, Registry(pack)));
            var manifestPath = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            manifest["enabledExpansionIds"] = new JArray("test"); File.WriteAllText(manifestPath, manifest.ToString());
            var baseOnly = ExternalContentPack.Load(root);
            Assert.That(baseOnly.CreateCharacters()[0].Name, Is.EqualTo("外部测试角色"));
            Assert.That(baseOnly.FindArtwork("character", "custom"), Is.EqualTo("front.jpg"));
            Assert.That(baseOnly.FindArtwork("character", "replacement"), Is.Null);
            Assert.Throws<KernelException>(() => new EffectTreeExecutor(saved, Registry(baseOnly)));
        }
        [Test]
        public void ReplacementChain_IsIndependentOfManifestLoadOrder()
        {
            string root = CreatePack(); AddReplacement(root, "replacement"); AddReplacement(root, "latest", "replacement", "latest_expansion");
            var path = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(path));
            manifest["definitions"] = new JArray("latest.json", "replacement.json", "card.json"); File.WriteAllText(path, manifest.ToString());
            var pack = ExternalContentPack.Load(root);
            Assert.That(pack.ActiveDefinitions.Single().DefinitionId, Is.EqualTo("latest"));
            Assert.That(pack.CreateCharacters().Single().TemplateId, Is.EqualTo("custom"));
            Assert.That(pack.FindArtwork("character", "replacement"), Is.EqualTo("latest.jpg"));
        }
        [TestCase("conflict")]
        [TestCase("missing_target")]
        [TestCase("disabled_target_expansion")]
        [TestCase("unknown_expansion")]
        [TestCase("duplicate_expansion")]
        public void InvalidActiveExpansionSelection_IsRejected(string fault)
        {
            string root = CreatePack(); AddReplacement(root, "replacement", fault == "missing_target" ? "absent" : "custom");
            if (fault == "conflict") AddReplacement(root, "competitor");
            var path = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(path));
            if (fault == "disabled_target_expansion") manifest["enabledExpansionIds"] = new JArray("expansion");
            if (fault == "unknown_expansion") manifest["enabledExpansionIds"] = new JArray("unknown");
            if (fault == "duplicate_expansion") manifest["enabledExpansionIds"] = new JArray("test", "test");
            File.WriteAllText(path, manifest.ToString());
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }
        [TestCase("facility", "building_001")]
        [TestCase("event", "event_green_01")]
        [TestCase("city_style", "city_style_test")]
        public void TypedReplacement_PreservesRuntimeSlotWhileUsingVariantData(string type, string runtimeId)
        {
            string root = CreatePack(); var card = JObject.Parse(File.ReadAllText(Path.Combine(root, "card.json")));
            card["definitionId"] = runtimeId; card["contentType"] = type; card["abilities"] = new JArray();
            card["data"] = new JObject { ["specialActionId"] = "action.original", ["effectScript"] = "effect.lua" };
            var variant = (JObject)card.DeepClone(); variant["definitionId"] = "variant"; variant["displayName"] = "替换名称";
            variant["data"] = new JObject { ["score"] = 9, ["specialActionId"] = "action.variant", ["effectScript"] = "variant.lua",
                ["specialAction"] = new JObject { ["cityStyleId"] = "variant", ["specialActionId"] = "action.variant", ["extraMainActionCount"] = 2 } };
            variant["replaces"] = new JObject { ["expansionId"] = "test", ["contentType"] = type, ["definitionId"] = runtimeId };
            File.WriteAllText(Path.Combine(root, "variant.lua"), "return function(ctx) return {} end");
            File.WriteAllText(Path.Combine(root, "card.json"), card.ToString()); File.WriteAllText(Path.Combine(root, "variant.json"), variant.ToString());
            var path = Path.Combine(root, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(path));
            ((JArray)manifest["definitions"]).Add("variant.json"); File.WriteAllText(path, manifest.ToString());
            var pack = ExternalContentPack.Load(root);
            Assert.That(pack.ActiveDefinitions, Has.Count.EqualTo(1));
            Assert.That(pack.GetContentScript(type, runtimeId), Is.EqualTo("return function(ctx) return {} end"));
            if (type == "facility")
            {
                var value = pack.CreateFacilities().Single(); Assert.That(value.FacilityId, Is.EqualTo(runtimeId));
                Assert.That(value.ManifestId, Is.EqualTo(runtimeId)); Assert.That(value.Name, Is.EqualTo("替换名称")); Assert.That(value.Score, Is.EqualTo(9));
            }
            else if (type == "event")
            { var value = pack.CreateEvents().Single(); Assert.That(value.CardId, Is.EqualTo(runtimeId)); Assert.That(value.Name, Is.EqualTo("替换名称")); }
            else
            {
                var value = pack.CreateCityStyles().Single(); Assert.That(value.CityStyleId, Is.EqualTo(runtimeId)); Assert.That(value.Score, Is.EqualTo(9));
                Assert.That(value.SpecialActionId, Is.EqualTo("action.original")); Assert.That(value.Name, Is.EqualTo("替换名称"));
                var action = pack.CreateSpecialActions().Single(); Assert.That(action.CityStyleId, Is.EqualTo(runtimeId));
                Assert.That(action.SpecialActionId, Is.EqualTo("action.original")); Assert.That(action.ExtraMainActionCount, Is.EqualTo(2));
            }
        }
        [Test]
        public void FacilityTemplate_MergesEntityOverridesAndParticipatesInContentHash()
        {
            string root = CreatePack(); var card = JObject.Parse(File.ReadAllText(Path.Combine(root, "card.json")));
            card["definitionId"] = "building_001"; card["contentType"] = "facility"; card["abilities"] = new JArray();
            card["dataTemplate"] = "template.json";
            card["data"] = new JObject { ["resourceCost"] = new JObject { ["iron"] = 4 }, ["keywords"] = new JArray("override") };
            File.WriteAllText(Path.Combine(root, "card.json"), card.ToString());
            var template = new JObject { ["contentType"] = "facility", ["data"] = new JObject
            { ["score"] = 3, ["color"] = "blue", ["resourceCost"] = new JObject { ["iron"] = 2, ["originium"] = 1 }, ["keywords"] = new JArray("base") } };
            File.WriteAllText(Path.Combine(root, "template.json"), template.ToString());
            var pack = ExternalContentPack.Load(root); var facility = pack.CreateFacilities().Single();
            Assert.That(facility.Score, Is.EqualTo(3)); Assert.That(facility.ResourceCost.Iron, Is.EqualTo(4));
            Assert.That(facility.ResourceCost.Originium, Is.EqualTo(1)); Assert.That(facility.FacilityId, Is.EqualTo("building_001"));
            Assert.That(pack.Definitions[0].Data["keywords"].Values<string>(), Is.EqualTo(new[] { "override" }));
            template["data"]["score"] = 5; File.WriteAllText(Path.Combine(root, "template.json"), template.ToString());
            Assert.That(ExternalContentPack.Load(root).ContentHash, Is.Not.EqualTo(pack.ContentHash));
            Assert.That(pack.CreateFacilities()[0].Score, Is.EqualTo(3));
        }
        [TestCase("missing")]
        [TestCase("wrong_type")]
        [TestCase("escape")]
        public void InvalidFacilityTemplate_IsRejected(string fault)
        {
            string root = CreatePack(); var card = JObject.Parse(File.ReadAllText(Path.Combine(root, "card.json")));
            card["contentType"] = "facility"; card["abilities"] = new JArray(); card["dataTemplate"] = fault == "escape" ? "../outside.json" : "template.json";
            File.WriteAllText(Path.Combine(root, "card.json"), card.ToString());
            if (fault == "wrong_type") File.WriteAllText(Path.Combine(root, "template.json"), "{\"contentType\":\"character\",\"data\":{}}");
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }
        [Test]
        public void RuntimeArtworkUsesExternalPackAndCachesDecodedTexture()
        {
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var method = runtime.GetMethod("GetArtwork");
            var first = method.Invoke(null, new object[] { "character", "elysium" }) as Texture2D;
            var second = method.Invoke(null, new object[] { "character", "elysium" }) as Texture2D;
            Assert.That(first, Is.Not.Null);
            Assert.That(first.name, Is.EqualTo("artwork/character/elysium.jpg"));
            Assert.That(first.width, Is.GreaterThan(100));
            Assert.That(second, Is.SameAs(first));
            UnityEngine.Object.DestroyImmediate(first);
            var reloaded = method.Invoke(null, new object[] { "character", "elysium" }) as Texture2D;
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.width, Is.GreaterThan(100));
        }
    }
}
