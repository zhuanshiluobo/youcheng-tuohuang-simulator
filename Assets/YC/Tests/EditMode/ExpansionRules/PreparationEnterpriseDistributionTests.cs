using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PreparationEnterpriseDistributionTests
    {
        private const string Owner = "fixture-owner", Mode = "fixture.mode", Hash = "fixture-content";
        private static ItemInstanceState[] Items(int count) => Enumerable.Range(0, count).Select(i => new ItemInstanceState {
            ItemId = "instance-" + i, DefinitionId = "fixture.enterprise." + i, TypeId = "core.board_card", PhysicalKind = ItemPhysicalKind.Card
        }).ToArray();
        private static PreparationEnterprisePlan Official() => new PreparationEnterprisePlan {
            PlayerCounts = new List<PreparationEnterpriseCountRule> {
                new PreparationEnterpriseCountRule { PlayerCount = 2, Draw = 4, Keep = 2, FinalCount = 4 },
                new PreparationEnterpriseCountRule { PlayerCount = 3, Draw = 2, Keep = 1, ExtraRandom = 1, FinalCount = 4 },
                new PreparationEnterpriseCountRule { PlayerCount = 4, Draw = 2, Keep = 1, FinalCount = 4 }
            }
        };
        private static bool Confirm(PreparationEnterprisePlan plan, PreparationEnterpriseDistributionRecord record,
            ItemInstanceState[] items, int player, IEnumerable<string> selected) =>
            PreparationEnterpriseDistribution.TryConfirm(plan, record, items, Owner, Mode, Hash, record.PlayerOrder, player, selected, out _);

        [TestCase(2, 4, 2, 0)] [TestCase(3, 2, 1, 1)] [TestCase(4, 2, 1, 0)]
        public void OfficialCountsDistributeOnceAndRequireExplicitConfirmation(int players, int draw, int keep, int extra)
        {
            var order = Enumerable.Range(1, players).ToArray();
            var items = Items(8);
            var plan = Official();
            var random = new Random(135);
            var record = PreparationEnterpriseDistribution.Create(plan, Owner, Mode, Hash, order, items, random.Next);
            Assert.That(record.Assignments.Select(a => a.PlayerId), Is.EqualTo(order));
            Assert.That(record.Assignments.All(a => a.CandidateItemIds.Count == draw && !a.Confirmed && a.SelectedItemIds.Count == 0), Is.True);
            Assert.That(record.ExtraItemIds.Count, Is.EqualTo(extra));
            Assert.That(record.Assignments.SelectMany(a => a.CandidateItemIds).Concat(record.UnassignedItemIds).Distinct().Count(), Is.EqualTo(8));
            var saved = GameStateCloneService.DeepClone(record);
            foreach (var assignment in saved.Assignments)
                Assert.That(Confirm(plan, saved, items, assignment.PlayerId, assignment.CandidateItemIds.Take(keep)), Is.True);
            Assert.That(saved.Assignments.All(a => a.Confirmed), Is.True);
            Assert.That(record.Assignments.All(a => !a.Confirmed), Is.True);
            Assert.That(saved.Assignments.Sum(a => a.SelectedItemIds.Count) + saved.ExtraItemIds.Count, Is.EqualTo(4));
            Assert.That(Confirm(plan, saved, items, order[0], saved.Assignments[0].SelectedItemIds), Is.False);
        }

        [Test]
        public void RequiredAndAutomaticFixedEnterprisesSurviveRandomPoolAndHaveDistinctRules()
        {
            var order = new[] { 7, 4, 9 };
            var items = Items(10);
            var plan = new PreparationEnterprisePlan {
                Pool = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 8,
                    CandidateDefinitionIds = items.Select(x => x.DefinitionId).ToList(),
                    RequiredDefinitionIds = new List<string> { "fixture.enterprise.2" } },
                Fixed = new List<PreparationFixedEnterprise> {
                    new PreparationFixedEnterprise { DefinitionId = "fixture.enterprise.0", OwnerOrderIndex = 1, RequireConfirmation = true },
                    new PreparationFixedEnterprise { DefinitionId = "fixture.enterprise.1", OwnerOrderIndex = -1, RequireConfirmation = false }
                },
                PlayerCounts = new List<PreparationEnterpriseCountRule> {
                    new PreparationEnterpriseCountRule { PlayerCount = 3, Draw = 2, Keep = 1, FinalCount = 4 }
                }
            };
            var random = new Random(22);
            var record = PreparationEnterpriseDistribution.Create(plan, Owner, Mode, Hash, order, items, random.Next);
            var assigned = record.Assignments[1];
            Assert.That(assigned.PlayerId, Is.EqualTo(4));
            Assert.That(assigned.RequiredItemIds, Is.EqualTo(new[] { "instance-0" }));
            Assert.That(record.Automatic.Single().ItemId, Is.EqualTo("instance-1"));
            Assert.That(record.Automatic.Single().InitialOwnerPlayerId, Is.EqualTo(-1));
            Assert.That(Confirm(plan, record, items, 4, assigned.CandidateItemIds.Where(id => id != "instance-0")), Is.False);
            Assert.That(assigned.Confirmed, Is.False);
            Assert.That(Confirm(plan, record, items, 4, new[] { "instance-0" }), Is.True);
            Assert.That(record.Assignments.SelectMany(a => a.CandidateItemIds).Concat(record.UnassignedItemIds)
                .Concat(record.Automatic.Select(x => x.ItemId)), Does.Contain("instance-2"));
        }

        [Test]
        public void ForgedOwnershipDuplicateOrDifferentSourceCannotPassRecoveryValidation()
        {
            var order = new[] { 1, 2, 3 };
            var plan = Official();
            var items = Items(8);
            var random = new Random(19);
            var record = PreparationEnterpriseDistribution.Create(plan, Owner, Mode, Hash, order, items, random.Next);
            Assert.That(PreparationEnterpriseDistribution.Validate(plan, record, items, Owner, Mode, Hash, order, out _), Is.True);
            var changed = GameStateCloneService.DeepClone(record);
            changed.Assignments[0].CandidateItemIds[0] = changed.Assignments[1].CandidateItemIds[0];
            Assert.That(PreparationEnterpriseDistribution.Validate(plan, changed, items, Owner, Mode, Hash, order, out _), Is.False);
            Assert.That(PreparationEnterpriseDistribution.Validate(plan, record, items, Owner, Mode, "different", order, out _), Is.False);
            Assert.That(PreparationEnterpriseDistribution.Validate(plan, record, items, Owner, Mode, Hash, order.Reverse().ToArray(), out _), Is.False);
            changed = GameStateCloneService.DeepClone(record);
            changed.ExtraItemIds[0] = changed.Assignments[0].CandidateItemIds[0];
            Assert.That(PreparationEnterpriseDistribution.Validate(plan, changed, items, Owner, Mode, Hash, order, out _), Is.False);
        }
    }
}
