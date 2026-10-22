using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class RuleStateDigestTests
    {
        private static JsonSerializerSettings ArchiveSettings => (JsonSerializerSettings)typeof(HostRecoveryService)
            .GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        private static GameState SmallState()
        {
            var state = new GameState { GameId = "规则摘要-α", Round = 2, ActionRound = 1, CurrentPlayerId = 1,
                EventDeckSeed = 71, ContentPackHash = "fixed-content", RandomRuntime = GameplayRandomRuntime.Create("规则摘要-α") };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "玩家甲", Score = 3 });
            state.Decks.CharacterDeck.Values.AddRange(new[] { "hidden-a", "hidden-b", "hidden-c" });
            GameplayRandomRuntime.Next(state.RandomRuntime, state.GameId, state.EventDeckSeed, GameplayRandomRuntime.DeckShuffle, 10);
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest { InteractionId = "digest-request", RequestId = "digest-request",
                AnsweringPlayerId = 1, CandidateIds = new List<string> { "choice-a", "choice-b" }, Status = "open" });
            state.Logs.Add(new GameLogEntry { Sequence = 1, CommandId = "digest-command", PlayerId = 1, Message = "本地显示正文" });
            return state;
        }

        [Test]
        public void FormalState_CloneAndHostSerializationRoundTripMatchWithoutMutation()
        {
            string path = Environment.GetEnvironmentVariable("YC_RULE_DIGEST_STATE");
            GameState state;
            if (string.IsNullOrEmpty(path)) state = SmallState();
            else
            {
                // 检查输入按持久字段原值加载；避免初始化列表与输入叠加。
                var inputSerializer = JsonSerializer.Create(ArchiveSettings);
                inputSerializer.ObjectCreationHandling = ObjectCreationHandling.Replace;
                using (var input = new JsonTextReader(File.OpenText(path))) state = inputSerializer.Deserialize<GameState>(input);
            }
            ItemZoneLegacyProjection.Bind(state);
            string digest = RuleStateDigestService.Compute(state);
            Assert.That(digest, Does.Match("^[0-9a-f]{64}$"));
            var clone = GameStateCloneService.DeepClone(state);
            Assert.That(RuleStateDigestService.Compute(clone), Is.EqualTo(digest));
            string archive = HostRecoveryService.SerializeArchive(new HostSessionArchiveDto { Snapshot = new HostSnapshotDto { State = state } });
            var restored = JsonConvert.DeserializeObject<HostSessionArchiveDto>(archive, ArchiveSettings).Snapshot.State;
            ItemZoneLegacyProjection.Bind(restored);
            Assert.That(RuleStateDigestService.Compute(restored), Is.EqualTo(digest));
            Assert.That(GameStateCloneService.AreEquivalent(state, clone), Is.True, "摘要计算必须不修改权威字段图。");
        }

        [Test]
        public void RuleFacts_RandomHiddenOrderRequestResultAndRevisionChangeDigest()
        {
            var state = SmallState();
            string baseline = RuleStateDigestService.Compute(state);
            GameplayRandomRuntime.Next(state.RandomRuntime, state.GameId, state.EventDeckSeed, GameplayRandomRuntime.DeckShuffle, 10);
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline), "真实连续随机推进必须被捕获。");
            baseline = RuleStateDigestService.Compute(state);
            string first = state.Decks.CharacterDeck.Values[0];
            state.Decks.CharacterDeck.Values[0] = state.Decks.CharacterDeck.Values[1]; state.Decks.CharacterDeck.Values[1] = first;
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline), "隐藏牌原顺序必须保留。");
            baseline = RuleStateDigestService.Compute(state);
            state.EffectRuntime.InteractionRequests[0].NormalizedAnswer = NormalizedValue.CreateString("choice-a");
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline), "开放请求结果不能漏掉。");
            baseline = RuleStateDigestService.Compute(state);
            state.EffectRuntime.StateRevision++;
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void Encoding_ExcludesOnlyProvenLogTextAndKeepsCultureLengthAndNullDistinct()
        {
            var state = SmallState();
            string baseline = RuleStateDigestService.Compute(state);
            state.Logs[0].Message = "不同语言的本地日志正文";
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(baseline));
            state.Logs[0].CommandId = "different-id";
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline), "日志幂等事实不能一并排除。");
            baseline = RuleStateDigestService.Compute(state);
            var culture = CultureInfo.CurrentCulture; var uiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
                Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(baseline));
            }
            finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
            state.Players[0].Name = null; baseline = RuleStateDigestService.Compute(state);
            state.Players[0].Name = ""; Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline));
            state.PendingChoice = new PendingChoiceState { OptionIds = new List<string> { "ab", "c" } }; baseline = RuleStateDigestService.Compute(state);
            state.PendingChoice.OptionIds = new List<string> { "a", "bc" }; Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline));
            baseline = RuleStateDigestService.Compute(state);
            state.EffectRuntime.LastFaultMessage = "不同的规则故障";
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline));
            Assert.Throws<ArgumentNullException>(() => RuleStateDigestService.Compute(null));
        }

        public static readonly List<string> VerifiedOperationFields = new List<string>();

        [Test]
        public void NewOperationWindowAndEveryPersistedContextMemberRemainVisible()
        {
            var state = SmallState();
            FieldInfo windows = typeof(EffectRuntimeState).GetField("OperationWindows");
            Assert.That(windows, Is.Not.Null, "需要共享稳定后的真实Domain字段，不能用替身宣称窗口兼容。");
            var records = (IList)windows.GetValue(state.EffectRuntime);
            var window = Activator.CreateInstance(windows.FieldType.GetGenericArguments()[0]); records.Add(window);
            VerifiedOperationFields.Clear();
            CheckMembers(state, window, "EffectRuntime.OperationWindows[*]");
            CheckMember(state, state.EffectRuntime, typeof(EffectRuntimeState).GetField("OperationWindowSchemaVersion"), "EffectRuntime");
            CheckMember(state, state.EffectRuntime, typeof(EffectRuntimeState).GetField("NextOperationWindowOrdinal"), "EffectRuntime");
            Type contextType = typeof(GameState).Assembly.GetType("YC.Domain.State.OperationCommandContext", true);
            CheckContext(state, state.EffectRuntime, "CurrentOperationContext", contextType, "EffectRuntime");
            var node = new EffectNodeRuntimeState(); state.EffectRuntime.EffectNodes.Add(node);
            CheckContext(state, node, "OperationContext", contextType, "EffectRuntime.EffectNodes[*]");
            CheckContext(state, node, "ContinuationOperationContext", contextType, "EffectRuntime.EffectNodes[*]");
            CheckContext(state, state.EffectRuntime.InteractionRequests[0], "OperationContext", contextType, "EffectRuntime.InteractionRequests[*]");
            var ruleEvent = new RuleEvent(); state.EffectRuntime.RuleEvents.Add(ruleEvent);
            CheckContext(state, ruleEvent, "OperationContext", contextType, "EffectRuntime.RuleEvents[*]");
            Assert.That(RuleStateDigestService.Compute(GameStateCloneService.DeepClone(state)), Is.EqualTo(RuleStateDigestService.Compute(state)));
        }

        private static void CheckContext(GameState state, object target, string name, Type type, string path)
        {
            FieldInfo field = target.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, path + "." + name);
            string baseline = RuleStateDigestService.Compute(state);
            var context = Activator.CreateInstance(type); field.SetValue(target, context);
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(baseline), path + "." + name);
            CheckMembers(state, context, path + "." + name);
        }

        private static void CheckMembers(GameState state, object target, string path)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !field.IsNotSerialized))
                CheckMember(state, target, field, path);
        }

        private static void CheckMember(GameState state, object target, FieldInfo field, string path)
        {
            Assert.That(field, Is.Not.Null);
            string before = RuleStateDigestService.Compute(state);
            if (field.FieldType == typeof(string)) field.SetValue(target, ((string)field.GetValue(target) ?? "") + "-changed");
            else if (field.FieldType == typeof(int)) field.SetValue(target, (int)field.GetValue(target) + 1);
            else if (field.FieldType == typeof(long)) field.SetValue(target, (long)field.GetValue(target) + 1);
            else if (field.FieldType.IsEnum) field.SetValue(target, Enum.ToObject(field.FieldType, Convert.ToInt32(field.GetValue(target)) == 0 ? 1 : 0));
            else if (field.FieldType == typeof(List<int>)) ((List<int>)field.GetValue(target)).Add(99);
            else if (field.FieldType.FullName == "YC.Domain.State.OperationWindowState")
            {
                var nested = Activator.CreateInstance(field.FieldType); field.SetValue(target, nested);
                Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(before), path + "." + field.Name);
                VerifiedOperationFields.Add(path + "." + field.Name);
                CheckMembers(state, nested, path + "." + field.Name);
                return;
            }
            else Assert.Fail("新增窗口字段需要明确兼容小例：" + path + "." + field.Name);
            Assert.That(RuleStateDigestService.Compute(state), Is.Not.EqualTo(before), path + "." + field.Name);
            VerifiedOperationFields.Add(path + "." + field.Name);
        }
    }
}
