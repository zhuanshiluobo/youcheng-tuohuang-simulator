using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Gameplay;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using Property = NUnit.Framework.PropertyAttribute;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class MultiSourceResourcePaymentTests
    {
        private sealed class PaymentModePort : IWritableGameplayContext, IGameCommandPort
        {
            private readonly MultiSourceResourcePaymentTests owner;
            internal readonly GameSession Session;
            internal readonly List<GameCommand> Commands = new List<GameCommand>();
            internal PaymentModePort(MultiSourceResourcePaymentTests owner) { this.owner = owner; Session = new GameSession(owner.f.State); Session.RegisterHandler(new PreparePaymentDraftCommandHandler(owner.f.Registry)); }
            public GameState CurrentState => owner.f.State;
            public int LocalPlayerId => 1;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int id) { }
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                Commands.Add(command); var result = Session.Submit(command); owner.f.State = Session.State;
                owner.f.Artifacts.WriteLabeledText("同Build实际UI方式命令", JsonConvert.SerializeObject(new { command, result }));
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); return new WorkflowSubmissionResult(result, true);
            }
        }
        [Test] public void PaymentModeAdapterUsesTheSameRealBuildPaymentAndRequiresFreshSourceSelection()
        {
            f.Run(RefillSpec(1, FindZone(f.State, "facility_supply_0").ZoneId));
            string target = new ItemZoneQuery().GetItemIds(f.State, FindZone(f.State, "facility_supply_0").ZoneId).Single();
            f.Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            var selection = f.Open();
            Assert.That(f.Engine.TryPrepareEffectBuildPayment(1, selection.OwnerEffectId, selection.GetStableInteractionId(), selection.CandidateSetId, selection.CandidateSetVersion, target,
                Object("expectedSourceZoneId", NormalizedValue.CreateString(FindZone(f.State, "facility_supply_0").ZoneId), "paymentMode", NormalizedValue.CreateString("resources")),
                f.State.EffectRuntime.StateRevision, "R4正式同Build准备", out string reason), Is.True, reason);
            var payment = f.Engine.GetNode(f.Open().OwnerEffectId); string id = payment.Payment.PaymentId;
            rules = ResourcePaymentSourceService.Query(f.State, f.Registry, payment, payment.Payment.PaymentCost).ToList();
            payment = SourceCommand(payment, 0, "save_source", new ResourceSet { Iron = 1 }); int generation = payment.Payment.PaymentGeneration;
            var port = new PaymentModePort(this);
            var adapter = new ActionPaymentPreparation(port, new CommandGateway(port), "build", () => { }, _ => { });
            adapter.GetVisibleState = () => port.Session.ProjectFor(GameStateViewer.Player(1));
            adapter.GetEffectBuildScope = () => {
                var view = port.Session.ProjectFor(GameStateViewer.Player(1));
                Assert.That(EffectBuildScope.TryReadEffectBuildScope(f.State, view, 1, selection.OwnerEffectId, out var scope), Is.True); return scope;
            };
            foreach (string mode in new[] { "gold", "resources" })
            {
                Assert.That(adapter.ChangePaymentMode(mode), Is.True);
                payment = f.Engine.GetNode(payment.EffectId); Assert.That(payment.Payment.PaymentId, Is.EqualTo(id));
                Assert.That(payment.Payment.PaymentGeneration, Is.GreaterThan(generation)); generation = payment.Payment.PaymentGeneration;
                Assert.That(payment.Payment.SourceDrafts.Single().Status, Is.EqualTo("reselect_required"));
                Assert.That(payment.Payment.IsCommitted, Is.False); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
                Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment != null), Is.EqualTo(1));
                Assert.That(port.Commands.Last().Parameters["operation"], Is.EqualTo("change_payment_mode"));
            }
            Assert.That(port.Commands.Count, Is.EqualTo(2)); adapter.BeginShutdown(); f.Restore("同Build UI方式命令后的真实恢复");
        }
    }
}
