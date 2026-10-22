using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class HostJournalCheckpointTests
    {
        private static GameState State()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            return new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "host-checkpoint-contract", MapId = map.MapId, ContentPackHash = "checkpoint-content",
                StartPlayerId = 1, CurrentPlayerId = 1, LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration
                { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "checkpoint-content" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } }
            }, map, new List<ItemRecipeEntry>());
        }

        private static void Commit(GameState state, int number, bool multiple = false)
        {
            state.Round = number;
            var entries = new List<RuleJournalEntry>
            {
                new RuleJournalEntry
                {
                    Kind = RuleJournalEntryKind.DomainState, EntityId = "round", Detail = "第" + number + "次真实提交",
                    ContentHash = state.ContentPackHash, ContentId = "checkpoint-test", ContentVersion = "v1",
                    Payload = NormalizedValue.CreateInteger(number)
                }
            };
            if (multiple) entries.Add(new RuleJournalEntry
            { Kind = RuleJournalEntryKind.DomainState, EntityId = "second-entity", ContentHash = state.ContentPackHash });
            RuleCommit.Apply(state, "checkpoint-command-" + number, entries.ToArray());
        }

        private static HostSessionArchiveDto Batch(out GameState state)
        {
            state = State();
            var archive = HostRecoveryService.CreateArchive(state);
            Commit(state, 1, true);
            Commit(state, 2);
            HostRecoveryService.AppendJournal(archive, state);
            return archive;
        }

        private static void AssertRecovered(HostSessionArchiveDto archive, GameState expected, EffectRegistry registry = null)
        {
            string original = HostRecoveryService.SerializeArchive(archive);
            var direct = HostRecoveryService.Recover(archive, registry);
            var json = HostRecoveryService.RecoverJson(original, registry);
            Assert.That(direct.Succeeded, Is.True, direct.Diagnostic);
            Assert.That(json.Succeeded, Is.True, json.Diagnostic);
            Assert.That(GameStateCloneService.AreEquivalent(direct.State, expected), Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(json.State, expected), Is.True);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        private static void AssertRejected(HostSessionArchiveDto archive, EffectRegistry registry = null)
        {
            string original = HostRecoveryService.SerializeArchive(archive);
            Assert.That(HostRecoveryService.Recover(archive, registry).Succeeded, Is.False);
            Assert.That(HostRecoveryService.RecoverJson(original, registry).Succeeded, Is.False);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        [Test]
        public void BatchCheckpointPreservesRealCommitsAndOnlyPublishesCompleteBatches()
        {
            var archive = Batch(out var state);
            Assert.That(archive.Snapshot.SchemaVersion, Is.EqualTo(4));
            Assert.That(archive.Journal.SchemaVersion, Is.EqualTo(4));
            Assert.That(archive.Journal.Entries.Count, Is.EqualTo(3));
            Assert.That(archive.Journal.Entries.Take(2).All(entry => entry.StateAfter == null), Is.True);
            Assert.That(archive.Journal.Entries[0].StateRevision, Is.EqualTo(1));
            Assert.That(archive.Journal.Entries[1].CommitId, Is.EqualTo(archive.Journal.Entries[0].CommitId));
            Assert.That(archive.Journal.Entries[2].StateRevision, Is.EqualTo(2));
            Assert.That(archive.Journal.Entries.Last().StateAfter, Is.Not.SameAs(state));
            AssertRecovered(archive, state);

            var firstCheckpoint = archive.Journal.Entries.Last().StateAfter;
            Commit(state, 3, true);
            HostRecoveryService.AppendJournal(archive, state);
            Assert.That(archive.Journal.Entries.Count, Is.EqualTo(5));
            Assert.That(archive.Journal.Entries.Count(entry => entry.StateAfter != null), Is.EqualTo(2));
            Assert.That(archive.Journal.Entries[2].StateAfter, Is.SameAs(firstCheckpoint));
            AssertRecovered(archive, state);
            var lastCheckpoint = archive.Journal.Entries.Last().StateAfter;
            string before = HostRecoveryService.SerializeArchive(archive);
            HostRecoveryService.AppendJournal(archive, state);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
            Assert.That(archive.Journal.Entries.Last().StateAfter, Is.SameAs(lastCheckpoint));
            var legacyOrderSource = GameStateCloneService.DeepClone(state);
            legacyOrderSource.LaunchPlayerOrder.Clear();
            string sourceBefore = JsonConvert.SerializeObject(legacyOrderSource);
            HostRecoveryService.AppendJournal(archive, legacyOrderSource);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
            Assert.That(JsonConvert.SerializeObject(legacyOrderSource), Is.EqualTo(sourceBefore));
        }

        [TestCase("missing-middle")]
        [TestCase("missing-tail")]
        [TestCase("duplicate")]
        [TestCase("backwards")]
        [TestCase("record-revision")]
        [TestCase("record-commit")]
        [TestCase("record-entity")]
        [TestCase("record-command")]
        [TestCase("record-kind")]
        [TestCase("record-detail")]
        [TestCase("record-content-id")]
        [TestCase("record-content-version")]
        [TestCase("record-content-hash")]
        [TestCase("checkpoint-revision")]
        [TestCase("checkpoint-sequence")]
        [TestCase("checkpoint-next")]
        [TestCase("checkpoint-content")]
        [TestCase("checkpoint-launch")]
        [TestCase("checkpoint-game")]
        public void DamagedBatchCannotRecoverThroughObjectOrJson(string damage)
        {
            var archive = Batch(out _);
            var first = archive.Journal.Entries[0];
            var checkpoint = archive.Journal.Entries.Last().StateAfter;
            switch (damage)
            {
                case "missing-middle": archive.Journal.Entries.RemoveAt(1); break;
                case "missing-tail": archive.Journal.Entries.RemoveAt(2); break;
                case "duplicate": archive.Journal.Entries.Insert(1, GameStateCloneService.DeepClone(first)); break;
                case "backwards": archive.Journal.Entries.Reverse(); break;
                case "record-revision": first.StateRevision++; break;
                case "record-commit": first.CommitId += "-changed"; break;
                case "record-entity": first.EntityId += "-changed"; break;
                case "record-command": first.CommandId += "-changed"; break;
                case "record-kind": first.Kind = RuleJournalEntryKind.Command; break;
                case "record-detail": first.Detail += "改写"; break;
                case "record-content-id": first.ContentId += "-changed"; break;
                case "record-content-version": first.ContentVersion += "-changed"; break;
                case "record-content-hash": first.ContentHash += "-changed"; break;
                case "checkpoint-revision": checkpoint.EffectRuntime.StateRevision++; break;
                case "checkpoint-sequence": checkpoint.EffectRuntime.Journal.Last().CommitSequence++; break;
                case "checkpoint-next": checkpoint.EffectRuntime.NextCommitSequence++; break;
                case "checkpoint-content": checkpoint.ContentPackHash += "-changed"; break;
                case "checkpoint-launch": checkpoint.LaunchPlayerOrder.Reverse(); break;
                case "checkpoint-game": checkpoint.GameId += "-changed"; break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
            AssertRejected(archive);
        }

        [TestCase("metadata")]
        [TestCase("payload")]
        public void LaterCheckpointCannotRewriteAcceptedJournalPrefix(string damage)
        {
            var archive = Batch(out var state);
            Commit(state, 3);
            HostRecoveryService.AppendJournal(archive, state);
            var priorEntry = archive.Journal.Entries.Last().StateAfter.EffectRuntime.Journal[0];
            if (damage == "metadata") priorEntry.Detail += "改写";
            else priorEntry.Payload = NormalizedValue.CreateInteger(999);
            AssertRejected(archive);
        }

        [TestCase("late-content")]
        [TestCase("late-gap")]
        [TestCase("late-null")]
        [TestCase("next-sequence")]
        [TestCase("prior-prefix")]
        public void FailedAppendNeverLeavesPartialEntries(string damage)
        {
            var archive = Batch(out var state);
            Commit(state, 3, true);
            switch (damage)
            {
                case "late-content": state.EffectRuntime.Journal.Last().ContentHash = "wrong-content"; break;
                case "late-gap": state.EffectRuntime.Journal.Last().CommitSequence++; break;
                case "late-null": state.EffectRuntime.Journal[state.EffectRuntime.Journal.Count - 1] = null; break;
                case "next-sequence": state.EffectRuntime.NextCommitSequence++; break;
                case "prior-prefix": state.EffectRuntime.Journal[0].Detail += "改写"; break;
            }
            string before = HostRecoveryService.SerializeArchive(archive);
            Assert.That(() => HostRecoveryService.AppendJournal(archive, state), Throws.InvalidOperationException);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
            Assert.That(archive.Journal.Entries.Count, Is.EqualTo(3));
        }

        [TestCase(2)]
        [TestCase(3)]
        public void ValidLegacyArchiveRecoversThenExplicitlyCreatesNewBaseline(int schema)
        {
            var state = State();
            var archive = HostRecoveryService.CreateArchive(state);
            Commit(state, 1);
            HostRecoveryService.AppendJournal(archive, state);
            archive.Snapshot.SchemaVersion = schema; archive.Journal.SchemaVersion = schema;
            if (schema == 2)
            {
                archive.Snapshot.State.LaunchPlayerOrder.Clear();
                archive.Journal.Entries[0].StateAfter.LaunchPlayerOrder.Clear();
            }
            string before = HostRecoveryService.SerializeArchive(archive);
            AssertRecovered(archive, state);
            if (schema == 2)
            {
                var oldJson = JObject.Parse(before);
                ((JObject)oldJson["Snapshot"]["State"]).Remove("LaunchPlayerOrder");
                ((JObject)oldJson["Journal"]["Entries"][0]["StateAfter"]).Remove("LaunchPlayerOrder");
                var fromOldFields = HostRecoveryService.RecoverJson(oldJson.ToString());
                Assert.That(fromOldFields.Succeeded, Is.True, fromOldFields.Diagnostic);
                Assert.That(fromOldFields.State.LaunchPlayerOrder, Is.EqualTo(state.LaunchPlayerOrder));
            }
            Assert.That(() => HostRecoveryService.AppendJournal(archive, state), Throws.InvalidOperationException);
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before));
            var recovered = HostRecoveryService.Recover(archive);
            var newArchive = HostRecoveryService.CreateArchive(recovered.State);
            Assert.That(newArchive.Snapshot.SchemaVersion, Is.EqualTo(4));
            Assert.That(newArchive.Snapshot.State.EffectRuntime.Journal.Count, Is.EqualTo(1));
            Commit(recovered.State, 2);
            HostRecoveryService.AppendJournal(newArchive, recovered.State);
            AssertRecovered(newArchive, recovered.State);
        }

        [Test]
        public void InvalidLegacyThreeWithBatchEndCopiesStillFailsStrictRecovery()
        {
            var archive = Batch(out var state);
            archive.Snapshot.SchemaVersion = 3; archive.Journal.SchemaVersion = 3;
            foreach (var entry in archive.Journal.Entries) entry.StateAfter = GameStateCloneService.DeepClone(state);
            AssertRejected(archive);
        }

        [TestCase("ContentId")]
        [TestCase("StateAfter")]
        [TestCase("CommitSequence")]
        [TestCase("ReceiptId")]
        public void JsonIntermediateRecordsRequireOriginalMetadataFields(string field)
        {
            var archive = Batch(out _);
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            ((JObject)json["Journal"]["Entries"][0]).Remove(field);
            Assert.That(HostRecoveryService.RecoverJson(json.ToString()).Succeeded, Is.False);
        }

        [TestCase("record-receipt")]
        [TestCase("record-event")]
        [TestCase("record-subscription")]
        [TestCase("record-handler")]
        [TestCase("checkpoint-receipt")]
        public void ActualDispatchReceiptIdentityMustMatchItsBatch(string damage)
        {
            var state = State(); var registry = new EffectRegistry();
            registry.Register("test.checkpoint.emit", context => EffectStepResult.Completed()
                .AddEvent(new EffectEventRequest { EventType = "test.checkpoint.signal" }), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.checkpoint.handler", "test.checkpoint.signal",
                "*", "test.checkpoint.source", context => new EffectSpec[0]));
            var archive = HostRecoveryService.CreateArchive(state);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("test.checkpoint.emit", playerId: 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            HostRecoveryService.AppendJournal(archive, state);
            AssertRecovered(archive, state, registry);
            var record = archive.Journal.Entries.First(entry => entry.Kind == RuleJournalEntryKind.DispatchReceipt);
            var checkpoint = archive.Journal.Entries.Last().StateAfter;
            switch (damage)
            {
                case "record-receipt": record.ReceiptId += "-changed"; break;
                case "record-event": record.EventId += "-changed"; break;
                case "record-subscription": record.SubscriptionId += "-changed"; break;
                case "record-handler": record.HandlerId += "-changed"; break;
                case "checkpoint-receipt": checkpoint.EffectRuntime.DispatchReceipts[0].HandlerId += "-changed"; break;
            }
            AssertRejected(archive, registry);
        }
    }
}
