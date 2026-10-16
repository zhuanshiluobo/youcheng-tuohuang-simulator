using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class HostRecoveryCollectionTests
    {
        // 使用实际内部快照字段设置；没有独立的产品存读档入口。
        private static JsonSerializerSettings ArchiveSettings => (JsonSerializerSettings)typeof(HostRecoveryService)
            .GetField("ArchiveSettings", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        [Test]
        public void FieldReaderPreservesExplicitCollectionsAndDefaultsOnlyOmittedFields()
        {
            CoverSlotState Read(string json) => JsonConvert.DeserializeObject<CoverSlotState>(json, ArchiveSettings);
            Assert.That(Read("{\"AllowedActionRounds\":[1,2]}").AllowedActionRounds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(Read("{\"AllowedActionRounds\":[2]}").AllowedActionRounds, Is.EqualTo(new[] { 2 }));
            Assert.That(Read("{\"AllowedActionRounds\":[]}").AllowedActionRounds, Is.Empty);
            Assert.That(Read("{\"AllowedActionRounds\":null}").AllowedActionRounds, Is.Null);
            Assert.That(Read("{}").AllowedActionRounds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(Read("{\"AllowedActionRounds\":[2,1,2,1]}").AllowedActionRounds, Is.EqualTo(new[] { 2, 1, 2, 1 }), "保留显式重复项及原序，不能用去重掩盖输入。");
            Assert.That(ArchiveSettings.MissingMemberHandling, Is.EqualTo(MissingMemberHandling.Error));
            bool rejected = false;
            try { Read("{\"UnknownCollection\":[]}"); }
            catch (JsonSerializationException) { rejected = true; }
            Assert.That(rejected, Is.True, "未知字段仍拒绝。");
            var legacy = JsonConvert.DeserializeObject<LegacyPositionList<string>>("{\"Values\":[\"b\",\"a\",\"b\"]}", ArchiveSettings);
            Assert.That(legacy.Values, Is.EqualTo(new[] { "b", "a", "b" }), "既有位置兼容转换器保留原序与重复项。");
        }

        [Test]
        public void FormalPersistentFieldsDoNotGrowAcrossActualRecoveryRounds()
        {
            string path = Environment.GetEnvironmentVariable("YC_HOST_FORMAL_STATE");
            GameState original;
            if (string.IsNullOrEmpty(path)) original = BuildHost(false).Item1.State;
            else
            {
                // 固定正式终局字段输入，诊断基线显式保持原值，不修改实际 ArchiveSettings。
                var serializer = JsonSerializer.Create(ArchiveSettings);
                serializer.ObjectCreationHandling = ObjectCreationHandling.Replace;
                using (var input = new JsonTextReader(File.OpenText(path))) original = serializer.Deserialize<GameState>(input);
                ItemZoneLegacyProjection.Bind(original);
                Assert.That(original.CharacterCoverSlots.Count, Is.EqualTo(32));
                Assert.That(original.CharacterCoverSlots.All(slot => slot.AllowedActionRounds.SequenceEqual(new[] { 1, 2 })), Is.True);
            }
            var registry = BuildHost(false, original.RuleConfiguration).Item2;
            var before = GameStateCloneService.DeepClone(original);
            var current = original;
            string firstJson = null;
            for (int round = 0; round < 3; round++)
            {
                string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(current));
                if (round == 0) firstJson = json;
                var recovered = HostRecoveryService.RecoverJson(json, registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                AssertSamePersistentFacts(before, recovered.State);
                current = recovered.State;
            }
            Assert.That(GameStateCloneService.AreEquivalent(original, before), Is.True, "序列化和恢复不能污染源状态。");
            if (original.CharacterCoverSlots.Count > 0)
            {
                var raw = JObject.Parse(firstJson);
                var slot = (JObject)raw["Snapshot"]["State"]["CharacterCoverSlots"][0];
                slot["AllowedActionRounds"] = JValue.CreateNull();
                Assert.That(HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), registry).Succeeded, Is.False, "显式 null 在字段读取中保留，在正式校验中仍非法。");
                slot["AllowedActionRounds"] = new JArray(3);
                Assert.That(HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), registry).Succeeded, Is.False, "非法行动轮继续拒绝。");
                slot.Remove("AllowedActionRounds");
                Assert.That(HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), registry).Succeeded, Is.False, "已有角色事实缺少原始字段时，原校验仍禁止猜测默认值。");
            }
        }

        [Test]
        public void RecoveredSessionContinuesFromTheSameWindowAndRandomFacts()
        {
            var host = BuildHost(true);
            var first = host.Item1.State.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.AnsweringPlayerId == 3);
            Assert.That(host.Item1.Submit(Answer(first, "collection-before-recovery")).Succeeded, Is.True);
            var original = GameStateCloneService.DeepClone(host.Item1.State);
            Assert.That(original.EffectRuntime.OperationWindows.Count > 0, Is.True);
            Assert.That(original.EffectRuntime.RuleEvents.Any(e => e.OperationContext != null), Is.True);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(host.Item1.CreateHostArchive()), host.Item2);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            AssertSamePersistentFacts(original, recovered.State);
            var continuation = BuildHost(true);
            continuation.Item1.ReplaceState(recovered.State);
            Assert.That(GameStateCloneService.AreEquivalent(original, continuation.Item1.State), Is.True);
            var next = original.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.AnsweringPlayerId == 1);
            var command = Answer(next, "collection-after-recovery");
            Assert.That(host.Item1.Submit(command).Succeeded, Is.True);
            Assert.That(continuation.Item1.Submit(command).Succeeded, Is.True);
            AssertSamePersistentFacts(host.Item1.State, continuation.Item1.State);
        }

        private static void AssertSamePersistentFacts(GameState expected, GameState actual)
        {
            Assert.That(GameStateCloneService.AreEquivalent(expected, actual), Is.True, "完整权威字段图必须一致。");
            Assert.That(JsonConvert.SerializeObject(actual.CharacterCoverSlots), Is.EqualTo(JsonConvert.SerializeObject(expected.CharacterCoverSlots)));
            Assert.That(JsonConvert.SerializeObject(actual.RandomRuntime), Is.EqualTo(JsonConvert.SerializeObject(expected.RandomRuntime)));
            Assert.That(JsonConvert.SerializeObject(actual.EffectRuntime), Is.EqualTo(JsonConvert.SerializeObject(expected.EffectRuntime)), "窗口、因果和继续点保持原值。");
            Assert.That(JsonConvert.SerializeObject(actual.ItemZones), Is.EqualTo(JsonConvert.SerializeObject(expected.ItemZones)), "实体归属与区域牌序保持原值。");
        }

        private static GameCommand Answer(InteractionRequest request, string commandId)
        {
            var command = new GameCommand { CommandId = commandId, Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters["interactionId"] = request.GetStableInteractionId();
            command.Parameters["expectedRevision"] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept") command.Parameters["answerValue"] = "true";
            else command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            return command;
        }

        private static Tuple<GameSession, EffectRegistry> BuildHost(bool enterprise, GameRuleConfiguration suppliedConfiguration = null)
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true);
            GameObject host = null;
            try
            {
                setter.Invoke(null, new object[] { null }); host = new GameObject("HostRecoveryCollection"); var context = host.AddComponent(contextType);
                const string gameId = "collection-recovery-match";
                var config = suppliedConfiguration == null ? new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } }
                    : GameStateCloneService.DeepClone(suppliedConfiguration);
                var order = Enumerable.Range(1, config.PlayerCount).ToArray();
                var seats = order.Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList();
                contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                    new object[] { LaunchMode.Host, 1, "collection-recovery-room", seats, config, order, gameId, 42, GameplayRandomRuntime.Create(gameId) });
                var result = bootstrapper.GetMethod("Build", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { context, false, false });
                return Tuple.Create((GameSession)result.GetType().GetField("Session").GetValue(result), (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result));
            }
            finally { if (host != null) UnityEngine.Object.DestroyImmediate(host); setter.Invoke(null, new[] { previous }); }
        }
    }
}
