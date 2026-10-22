using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;
#if HOST_ARCHIVE_SERIALIZATION_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class HostArchiveSerializationTests
    {
        private static HostSessionArchiveDto SmallArchive()
        {
            var state = new GameState
            {
                GameId = "archive-serialization-contract",
                MapId = "map-three-players", ContentPackHash = "archive-contract-content",
                RuleConfiguration = new GameRuleConfiguration
                { MapId = "map-three-players", PlayerCount = 2, ContentPackHash = "archive-contract-content" },
                LaunchPlayerOrder = new List<int> { 2, 1 }, StartPlayerId = 2, CurrentPlayerId = 2,
                Players = new List<PlayerState>
                { new PlayerState { PlayerId = 1, Name = "甲" }, new PlayerState { PlayerId = 2, Name = "乙" } }
            };
            state.Players[0].HandCardIds.Add("host-private-card");
            state.Players[0].DeclaredCityStyles.Add(new CityStyleDeclarationState
            { CityStyleId = "archive-style", LegacyMarkerArea = "1", LegacyRemainingSpecialActionUses = 1 });
            var archive = HostRecoveryService.CreateArchive(state);
            for (int round = 1; round <= 3; round++)
            {
                state.Round = round;
                RuleCommit.Apply(state, "archive-commit-" + round, new RuleJournalEntry
                { Kind = RuleJournalEntryKind.DomainState, EntityId = "round", ContentHash = state.ContentPackHash,
                    Detail = "中文、引号\"、反斜线\\及换行\n" + round });
                HostRecoveryService.AppendJournal(archive, state);
            }
            return archive;
        }

        private static string StreamText(HostSessionArchiveDto archive)
        {
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                HostRecoveryService.SerializeArchive(archive, writer);
                return writer.ToString();
            }
        }

        [Test]
        public void SmallArchiveMatchesOriginalFormatRecoveryAndUnchangedInput()
        {
            var archive = SmallArchive();
            var snapshot = archive.Snapshot;
            var journal = archive.Journal.Entries;
            var states = journal.Select(entry => entry.StateAfter).ToArray();
            string original = HostRecoveryService.SerializeArchive(archive);
            string streamed = StreamText(archive);
            Assert.That(streamed, Is.EqualTo(original));
            Assert.That(archive.Snapshot, Is.SameAs(snapshot));
            Assert.That(archive.Journal.Entries, Is.SameAs(journal));
            for (int i = 0; i < states.Length; i++) Assert.That(journal[i].StateAfter, Is.SameAs(states[i]));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));

            var raw = JObject.Parse(streamed);
            Assert.That(raw["Snapshot"]["SchemaVersion"].Value<int>(), Is.EqualTo(HostSnapshotDto.CurrentSchemaVersion));
            Assert.That(raw["Journal"]["SchemaVersion"].Value<int>(), Is.EqualTo(HostJournalDto.CurrentSchemaVersion));
            Assert.That(raw["Journal"]["Entries"].Count(), Is.EqualTo(journal.Count));
            Assert.That(raw["Snapshot"]["State"]["EffectRuntimeState"], Is.Null);
            Assert.That(raw["Snapshot"]["State"]["Players"][0]["HandCardIds"]["Values"][0].Value<string>(), Is.EqualTo("host-private-card"));
            var declaration = raw["Snapshot"]["State"]["Players"][0]["DeclaredCityStyles"][0];
            Assert.That(declaration["MarkerArea"].Value<string>(), Is.EqualTo("1"));
            Assert.That(declaration["RemainingSpecialActionUses"].Value<int>(), Is.EqualTo(1));
            Assert.That(declaration["LegacyMarkerArea"], Is.Null);
            for (int i = 0; i < journal.Count; i++)
            {
                Assert.That(raw["Journal"]["Entries"][i]["CommitSequence"].Value<int>(), Is.EqualTo(journal[i].CommitSequence));
                Assert.That(raw["Journal"]["Entries"][i]["ContentHash"].Value<string>(), Is.EqualTo(archive.Snapshot.ContentHash));
                Assert.That(raw["Journal"]["Entries"][i]["StateAfter"], Is.Not.Null);
            }

            var oldRecovery = HostRecoveryService.RecoverJson(original);
            var newRecovery = HostRecoveryService.RecoverJson(streamed);
            Assert.That(oldRecovery.Succeeded, Is.True, oldRecovery.Diagnostic);
            Assert.That(newRecovery.Succeeded, Is.True, newRecovery.Diagnostic);
            Assert.That(GameStateCloneService.AreEquivalent(oldRecovery.State, newRecovery.State), Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(states[states.Length - 1], newRecovery.State), Is.True);
            Assert.That(newRecovery.State.Round, Is.EqualTo(3));
            newRecovery.State.Players[0].Name = "恢复副本";
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        [Test]
        public void SerializationPreservesInvalidRecordsForFormalRecoveryToReject()
        {
            var changes = new Action<HostSessionArchiveDto>[]
            {
                archive => archive.Journal.Entries[0].CommitSequence++,
                archive => archive.Journal.Entries[0].ContentHash = "wrong-content-hash",
                archive => archive.Journal.Entries[0].StateAfter = null
            };
            foreach (var change in changes)
            {
                var archive = SmallArchive();
                change(archive);
                string original = HostRecoveryService.SerializeArchive(archive);
                string streamed = StreamText(archive);
                Assert.That(streamed, Is.EqualTo(original));
                var oldRecovery = HostRecoveryService.RecoverJson(original);
                var newRecovery = HostRecoveryService.RecoverJson(streamed);
                Assert.That(oldRecovery.Succeeded, Is.False);
                Assert.That(newRecovery.Succeeded, Is.False);
                Assert.That(newRecovery.FaultCode, Is.EqualTo(oldRecovery.FaultCode));
                Assert.That(newRecovery.Diagnostic, Is.EqualTo(oldRecovery.Diagnostic));
                Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
            }
        }

        [Test]
        public void WriterArgumentsOwnershipAndOriginalWriteFailureArePreserved()
        {
            var archive = SmallArchive();
            var unused = new CountingWriter();
            Assert.That(Assert.Throws<ArgumentNullException>(() => HostRecoveryService.SerializeArchive(null, unused)).ParamName, Is.EqualTo("archive"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => HostRecoveryService.SerializeArchive(archive, null)).ParamName, Is.EqualTo("writer"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => HostRecoveryService.SerializeArchive(null)).ParamName, Is.EqualTo("archive"));
            Assert.That(unused.TotalCharacters, Is.EqualTo(0));
            Assert.That(unused.DisposeCalls, Is.EqualTo(0));
            string original = HostRecoveryService.SerializeArchive(archive);
            var success = new CountingWriter();
            HostRecoveryService.SerializeArchive(archive, success);
            Assert.That(success.TotalCharacters, Is.EqualTo(original.Length));
            Assert.That(success.DisposeCalls, Is.EqualTo(0));
            Assert.That(success.FlushCalls, Is.EqualTo(0));
            success.Write('!');
            Assert.That(success.LastCharacter, Is.EqualTo('!'));

            foreach (long failureOffset in new long[] { 0, 128, original.Length - 1 })
            {
                var failure = new IOException("原始写入故障", new InvalidOperationException("原始内部原因"));
                var writer = new CountingWriter { FailureOffset = failureOffset, WriteFailure = failure };
                var thrown = Assert.Throws<IOException>(() => HostRecoveryService.SerializeArchive(archive, writer));
                Assert.That(thrown, Is.SameAs(failure));
                Assert.That(thrown.InnerException, Is.SameAs(failure.InnerException));
                Assert.That(writer.FailedWriteCalls, Is.EqualTo(1), "失败后不得自动补尾并制造第二次写入故障。");
                Assert.That(writer.DisposeCalls, Is.EqualTo(0));
                Assert.That(writer.FlushCalls, Is.EqualTo(0));
                writer.FailureOffset = long.MaxValue;
                writer.Write('!');
                Assert.That(writer.LastCharacter, Is.EqualTo('!'));
                Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
            }
        }

        [Test]
        public void SerializationFailureKeepsPartialOutputAndWriterOpen()
        {
            var archive = SmallArchive();
            var cycle = new NormalizedValue { Kind = NormalizedValueKind.Array };
            cycle.Items.Add(cycle);
            archive.Journal.Entries[0].StateAfter.EffectRuntime.Journal[0].Payload = cycle;
            var writer = new CountingWriter();
            var error = Assert.Throws<JsonSerializationException>(() => HostRecoveryService.SerializeArchive(archive, writer));
            Assert.That(error.Message, Does.Contain("Self referencing loop"));
            Assert.That(error.Message, Does.Contain("Items"));
            Assert.That(writer.LastCharacter, Is.EqualTo('['), "序列化失败不得补齐半成品JSON的结束标记。");
            Assert.That(writer.DisposeCalls, Is.EqualTo(0));
            Assert.That(writer.FlushCalls, Is.EqualTo(0));
            Assert.That(cycle.Items[0], Is.SameAs(cycle));
            writer.Write('!');
            Assert.That(writer.LastCharacter, Is.EqualTo('!'));
        }

        [Test]
        public void ThousandsOfEntriesStreamToBoundedCountingWriter()
        {
            // 只测输出机制：复用少量输入，不把该合成夹具当成真实累计档案或恢复证明。
            var archive = new HostSessionArchiveDto();
            archive.Snapshot.LastCommitSequence = 6;
            var sharedState = new GameState { GameId = "bounded-stream-mechanism" };
            string sharedDetail = new string('x', 4096);
            for (int sequence = 7; sequence <= 3485; sequence++)
                archive.Journal.Entries.Add(new HostJournalEntryDto
                { CommitSequence = sequence, CommitId = "stream-" + sequence, ContentHash = "bounded-content",
                    Detail = sharedDetail, StateAfter = sharedState });

            var writer = new CountingWriter { MaximumAllowedWrite = 16384 };
            HostRecoveryService.SerializeArchive(archive, writer);
            Assert.That(writer.TotalCharacters > 16L * 1024 * 1024, Is.True);
            Assert.That(writer.MaximumWriteLength <= 16384, Is.True);
            Assert.That(writer.EntryFields, Is.EqualTo(3479));
            Assert.That(writer.StateAfterFields, Is.EqualTo(3479));
            Assert.That(writer.DisposeCalls, Is.EqualTo(0));
            Assert.That(archive.Journal.Entries.Count, Is.EqualTo(3479));
            Assert.That(archive.Journal.Entries[3478].CommitSequence, Is.EqualTo(3485));
            Assert.That(archive.Journal.Entries[3478].StateAfter, Is.SameAs(sharedState));
            Console.WriteLine("有界机制夹具：3479条记录，字符数=" + writer.TotalCharacters +
                "，最大单次写入=" + writer.MaximumWriteLength + "，未保存输出或构造真实对局累计状态。");
        }

        private sealed class CountingWriter : TextWriter
        {
            private const string EntryField = "\"CommitSequence\":";
            private const string StateAfterField = "\"StateAfter\":";
            private int entryMatch, stateAfterMatch;
            public long TotalCharacters;
            public int MaximumWriteLength, DisposeCalls, FlushCalls, FailedWriteCalls, EntryFields, StateAfterFields;
            public char LastCharacter;
            public long FailureOffset = long.MaxValue;
            public int MaximumAllowedWrite = int.MaxValue;
            public IOException WriteFailure;
            public override Encoding Encoding => Encoding.UTF8;

            private void CheckWrite(int count)
            {
                if (DisposeCalls > 0) throw new ObjectDisposedException(nameof(CountingWriter));
                if (count > MaximumAllowedWrite) throw new IOException("检测到超出夹具上限的单次写入，疑似完整字符串中转。");
                if (TotalCharacters + count > FailureOffset)
                {
                    FailedWriteCalls++;
                    if (FailedWriteCalls > 1) throw new IOException("关闭期间的第二次故障，不能覆盖原始故障。");
                    throw WriteFailure;
                }
                MaximumWriteLength = Math.Max(MaximumWriteLength, count);
            }
            private static void Match(char value, string pattern, ref int position, ref int matches)
            {
                position = value == pattern[position] ? position + 1 : value == pattern[0] ? 1 : 0;
                if (position == pattern.Length) { matches++; position = 0; }
            }
            private void Count(char value)
            {
                TotalCharacters++;
                LastCharacter = value;
                Match(value, EntryField, ref entryMatch, ref EntryFields);
                Match(value, StateAfterField, ref stateAfterMatch, ref StateAfterFields);
            }
            public override void Write(char value) { CheckWrite(1); Count(value); }
            public override void Write(string value)
            {
                if (value == null) return;
                CheckWrite(value.Length);
                for (int i = 0; i < value.Length; i++) Count(value[i]);
            }
            public override void Write(char[] buffer, int index, int count)
            {
                CheckWrite(count);
                for (int i = index; i < index + count; i++) Count(buffer[i]);
            }
            public override void Flush() { FlushCalls++; }
            protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
        }
    }
}
