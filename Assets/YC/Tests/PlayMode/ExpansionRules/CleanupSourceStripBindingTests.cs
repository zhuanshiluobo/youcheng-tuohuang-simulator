using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Presentation;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 生产FromOption→结算绑定→命令请求的隔离合同检查；不执行正常局或改卡面。
    public sealed class CleanupSourceStripBindingTests
    {
        private object binding;
        private static readonly Type Binding = Type.GetType("YC.Presentation.SettlementEffectStripBinding, Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        private object Call(string method, params object[] args)
        {
            try { return Binding.GetMethods().Single(value => value.Name == method && value.GetParameters().Length == args.Length).Invoke(binding, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        [SetUp] public void Setup() { binding = Activator.CreateInstance(Binding); Call("SetScope", "isolated:pack", 1); }
        private static SettlementOptionView Source(int generation = 1, int revision = 10)
        {
            var request = new CleanupSourceRequestRef { WindowId = "window", OwnerEffectId = "owner", OptionId = "generic-repeat-source",
                InteractionId = "request-" + generation, AnsweringPlayerId = 1, RequestGeneration = generation,
                CandidateSetVersion = generation, FactsDigest = "facts-" + generation };
            return SettlementOptionView.FromOption(new CharacterSettlementOption {
                WindowId = request.WindowId, OwnerEffectId = request.OwnerEffectId, OptionId = request.OptionId,
                InteractionId = request.InteractionId, AnsweringPlayerId = 1, CandidateSetVersion = generation, StateRevision = revision,
                EntryKind = "cleanup_order", TargetEffectId = "", SettlementState = "available", CleanupRequest = request,
                RepeatPolicy = "unbounded", ActiveInvocationId = "", IsEnabled = true, VisibleToViewer = true,
                SourceItemId = "source-item", SourceDefinitionId = "generic-source-definition",
                Presentation = new SettlementPresentationDefinition { Label = new GameplayTextRef("core:isolated.name"), Description = new GameplayTextRef("core:isolated.description") }
            });
        }
        private void Bind(SettlementOptionView option) => Call("BindWindow", option.WindowId, new[] { option });
        private object Entry => ((IEnumerable)Read(binding, "Entries")).Cast<object>().Single();
        private object[] Lease => new[] { Read(binding, "Scope"), Read(Entry, "Identity"), ((SettlementOptionView)Read(Entry, "Option")).StateRevision,
            (object)((SettlementOptionView)Read(Entry, "Option")).OptionId, null };
        private SettlementOptionView Begin()
        {
            var lease = Lease; Assert.That(Call("TryBeginUse", lease), Is.EqualTo(true)); return (SettlementOptionView)lease[4];
        }

        [Test] public void FormalAvailableSourceNeedsNoInvocationTargetAndKeepsCompleteRequest()
        {
            var option = Source(); Bind(option);
            var submitted = Begin();
            Assert.That(submitted.TargetEffectId, Is.Empty);
            Assert.That(submitted.RepeatPolicy, Is.EqualTo("unbounded"));
            Assert.That(submitted.CleanupRequest.InteractionId, Is.EqualTo(option.CleanupRequest.InteractionId));
            Assert.That(submitted.CleanupRequest.FactsDigest, Is.EqualTo(option.CleanupRequest.FactsDigest));
            option.CleanupRequest.FactsDigest = "caller-mutation";
            Assert.That(submitted.CleanupRequest.FactsDigest, Is.EqualTo("facts-1"));
            submitted.CleanupRequest.FactsDigest = "submission-mutation";
            Assert.That(((SettlementOptionView)Read(Entry, "Option")).CleanupRequest.FactsDigest, Is.EqualTo("facts-1"));
        }

        [Test] public void RepeatedSourceReturnsFromExecutingAndRejectsOldReceiptOrNewWindow()
        {
            var first = Source(); Bind(first); var oldLease = Lease; Begin();
            var oldToken = Call("CapturePending", first.OptionId);
            var executing = Source(2, 11); executing.SettlementState = "executing"; executing.IsEnabled = false;
            executing.TargetEffectId = "actual-invocation-target"; executing.ActiveInvocationId = "actual-invocation";
            Bind(executing);
            Assert.That(Call("TryBeginUse", Lease), Is.EqualTo(false));
            var again = Source(2, 12); Bind(again);
            Assert.That(Call("SetPending", again.WindowId, again.OptionId, false, oldToken), Is.EqualTo(true));
            Assert.That(Call("TryBeginUse", oldLease), Is.EqualTo(false));
            var second = Begin(); Assert.That(second.CleanupRequest.RequestGeneration, Is.EqualTo(2));
            Assert.That(Call("SetPending", again.WindowId, again.OptionId, false, oldToken), Is.EqualTo(false));
            var newToken = Call("CapturePending", again.OptionId);
            var next = Source(1, 13); next.WindowId = next.CleanupRequest.WindowId = "next-window"; Bind(next);
            Assert.That(Call("SetPending", again.WindowId, again.OptionId, false, newToken), Is.EqualTo(false));
            var previousViewerLease = Lease; Call("SetScope", "isolated:pack", 2); Bind(next);
            Assert.That(Call("TryBeginUse", previousViewerLease), Is.EqualTo(false));
            Assert.That(Call("TryBeginUse", Lease), Is.EqualTo(false), "另一观察者不可用本人来源");
            Call("Clear"); Assert.That((IEnumerable)Read(binding, "Entries"), Is.Empty);
        }

        [Test] public void MalformedCleanupRequestStillRejectsAtomicallyAndLegacyTargetStillWorks()
        {
            var legacy = Source(); legacy.OptionId = "legacy-once"; legacy.CleanupRequest = null;
            legacy.TargetEffectId = "existing-effect"; legacy.RepeatPolicy = "once"; Bind(legacy);
            foreach (var kind in new[] { "window", "player", "owner", "generation", "missing" })
            {
                var bad = Source();
                if (kind == "window") bad.CleanupRequest.WindowId = "wrong-window";
                if (kind == "player") bad.CleanupRequest.AnsweringPlayerId = 2;
                if (kind == "owner") bad.CleanupRequest.OwnerEffectId = "wrong-owner";
                if (kind == "generation") bad.CleanupRequest.RequestGeneration = 0;
                if (kind == "missing") bad.CleanupRequest = null;
                Assert.Throws<ArgumentException>(() => Bind(bad));
                Assert.That(((SettlementOptionView)Read(Entry, "Option")).OptionId, Is.EqualTo(legacy.OptionId));
            }
            Assert.That(Begin().TargetEffectId, Is.EqualTo("existing-effect"));
        }
    }
}
