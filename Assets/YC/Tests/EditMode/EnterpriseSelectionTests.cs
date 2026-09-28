using System;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EnterpriseSelectionTests
    {
        private static EnterpriseSelectionProjection Projection(EnterpriseSelectionMode mode = EnterpriseSelectionMode.Company,
            string request = "a", int revision = 1) => new EnterpriseSelectionProjection
        {
            RequestId = request, Revision = revision, Mode = mode, SubmissionAvailable = true,
            Boards = new[] { new EnterpriseBoardProjection { Id = "board" } },
            Targets = new[] { new EnterpriseSelectionTarget { Id = "first", BoardId = "board", CompanyId = "company",
                DepartmentId = "department", EffectId = "special", Tier = 2, Available = true },
                new EnterpriseSelectionTarget { Id = "locked", BoardId = "board", CompanyId = "company-2",
                    DepartmentId = "department-2", EffectId = "special-2", Tier = 3, Available = false } }
        };

        [TestCase(EnterpriseSelectionMode.Company)] [TestCase(EnterpriseSelectionMode.Effect)]
        [TestCase(EnterpriseSelectionMode.InitialDepartment)] [TestCase(EnterpriseSelectionMode.SwitchDepartment)]
        public void Draft_PreservesStableSelectionButNeverConfirmsAnUnavailableTarget(EnterpriseSelectionMode mode)
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(mode);
            draft.Refresh(p); Assert.That(draft.Select("first"),Is.True);
            draft.Inspect("locked"); Assert.That(draft.SelectedId,Is.EqualTo("first"));
            var next = Projection(mode,revision:2); Array.Reverse(next.Targets); draft.Refresh(next);
            Assert.That(draft.SelectedId,Is.EqualTo("first"));
            draft.Select("locked"); Assert.That(draft.CanConfirm,Is.False); Assert.That(draft.InspectedId,Is.EqualTo("locked"));
            draft.Select("first"); next = Projection(mode,revision:3); next.Targets = Array.Empty<EnterpriseSelectionTarget>();
            draft.Refresh(next); Assert.That(draft.SelectedId,Is.Null);
        }

        [Test] public void SharedDepartment_CurrentIsViewableButNotConfirmable_InitialMayHaveNoCurrent()
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(EnterpriseSelectionMode.SwitchDepartment);
            p.CurrentDepartmentId = "department"; draft.Refresh(p);
            Assert.That(draft.Select("first"),Is.False); Assert.That(draft.InspectedId,Is.EqualTo("first"));
            p = Projection(EnterpriseSelectionMode.InitialDepartment); draft.Refresh(p);
            Assert.That(draft.Select("first"),Is.True);
        }

        [Test] public void Pending_DuplicateAndLateCompletionsCannotAffectNewRequest()
        {
            var draft = new EnterpriseSelectionDraft(); draft.Refresh(Projection()); draft.Select("first");
            Assert.That(draft.TryBeginSubmit(out var old),Is.True);
            Assert.That(draft.TryBeginSubmit(out _),Is.False); Assert.That(draft.Select("locked"),Is.False);
            draft.Refresh(Projection(request:"b")); draft.Select("first");
            Assert.That(draft.Complete(old),Is.False); Assert.That(draft.SelectedId,Is.EqualTo("first"));
            draft.TryBeginSubmit(out var current); Assert.That(draft.Complete(current),Is.True);
            Assert.That(draft.Complete(current),Is.False); Assert.That(draft.CanConfirm,Is.True);
        }

        [Test] public void MissingCapabilityAndOlderProjectionFailClosed()
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(revision:5); p.SubmissionAvailable = false;
            draft.Refresh(p); draft.Select("first"); Assert.That(draft.TryBeginSubmit(out _),Is.False);
            Assert.That(draft.Refresh(Projection(revision:4)),Is.False); Assert.That(draft.CanConfirm,Is.False);
        }

        [Test] public void ProjectionMutationCannotRewriteDraftAndRemovedPendingTargetInvalidatesReply()
        {
            var p = Projection(); var draft = new EnterpriseSelectionDraft(); draft.Refresh(p); draft.Select("first");
            draft.TryBeginSubmit(out var intent);
            Assert.That(intent.CompanyId,Is.EqualTo("company")); Assert.That(intent.EffectId,Is.Null);
            Assert.That(intent.DepartmentId,Is.Null);
            p.RequestId="changed"; p.Targets[0].Available=false;
            Assert.That(draft.Projection.RequestId,Is.EqualTo("a")); Assert.That(draft.Selected.Available,Is.True);
            var next=Projection();next.Targets=Array.Empty<EnterpriseSelectionTarget>();draft.Refresh(next);
            Assert.That(draft.Complete(intent),Is.False);Assert.That(draft.IsPending,Is.False);
        }

        [TestCase(LuaDomainEffectTypeIds.UpgradeEnterprise)]
        [TestCase(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial)]
        [TestCase(LuaDomainEffectTypeIds.SwitchDepartment)]
        public void HostCapability_IsStillMissing_NotAnExampleSuccessCallback(string type)
        {
            var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry);
            var state = new GameState(); var executor = new EffectTreeExecutor(state,registry);
            var id = executor.CreateRoot(new EffectSpec(type)); var result = executor.RunUntilQuiescent();
            Assert.That(result.Faulted,Is.True); Assert.That(executor.GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Faulted));
            Assert.That(state.EffectRuntime.LastFaultMessage,Does.Contain("尚未实现宿主结算"));
        }
    }
}
