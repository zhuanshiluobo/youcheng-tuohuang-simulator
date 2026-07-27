using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class InteractionRouterTests
    {
        [Test]
        public void Constructor_RequiresPromptCallback()
        {
            Assert.That(
                () => new InteractionRouter(null),
                Throws.ArgumentNullException);
        }

        [Test]
        public void Register_RejectsNullInteraction()
        {
            var router = CreateRouter();

            Assert.That(
                () => router.Register(null),
                Throws.ArgumentNullException);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Register_RejectsMissingInteractionId(string id)
        {
            var router = CreateRouter();
            var interaction = new RecordingInteraction(id);

            Assert.That(
                () => router.Register(interaction),
                Throws.ArgumentException);
        }

        [Test]
        public void Register_RejectsDuplicateId()
        {
            var router = CreateRouter();
            router.Register(new RecordingInteraction("duplicate"));

            Assert.That(
                () => router.Register(new RecordingInteraction("duplicate")),
                Throws.InvalidOperationException);
        }

        [Test]
        public void Register_UsesOrdinalCaseSensitiveIds()
        {
            var router = CreateRouter();
            var lower = new RecordingInteraction("route");
            var upper = new RecordingInteraction("ROUTE");
            router.Register(lower);
            router.Register(upper);

            router.OnLocationClicked("A");

            Assert.That(lower.LocationClickCount, Is.EqualTo(1));
            Assert.That(upper.LocationClickCount, Is.EqualTo(1));
        }

        [Test]
        public void Register_RejectsSameInstanceEvenWhenItsIdChanges()
        {
            var router = CreateRouter();
            var interaction = new RecordingInteraction("before");
            router.Register(interaction);
            interaction.IdValue = "after";

            Assert.That(
                () => router.Register(interaction),
                Throws.InvalidOperationException);
        }

        [Test]
        public void Routing_UsesDescendingPriorityAndStableRegistrationOrder()
        {
            var order = new List<string>();
            var defaultRoute = new RecordingInteraction(
                "default",
                InteractionPriority.DefaultRoute,
                order);
            var firstPending = new RecordingInteraction(
                "pending-first",
                InteractionPriority.PendingResolution,
                order);
            var activeAction = new RecordingInteraction(
                "active",
                InteractionPriority.ActiveAction,
                order);
            var secondPending = new RecordingInteraction(
                "pending-second",
                InteractionPriority.PendingResolution,
                order);
            var router = CreateRouter();
            router.Register(defaultRoute);
            router.Register(firstPending);
            router.Register(activeAction);
            router.Register(secondPending);

            router.OnLocationClicked("A");

            Assert.That(
                order,
                Is.EqualTo(new[]
                {
                    "pending-first:location",
                    "pending-second:location",
                    "active:location",
                    "default:location"
                }));
        }

        [Test]
        public void Routing_SkipsInactiveInteractions()
        {
            var inactive = new RecordingInteraction(
                "inactive",
                InteractionPriority.PendingResolution)
            {
                Active = false,
                LocationResult = InteractionResult.Consumed
            };
            var fallback = new RecordingInteraction(
                "fallback",
                InteractionPriority.DefaultRoute);
            var router = CreateRouter();
            router.Register(inactive);
            router.Register(fallback);

            router.OnLocationClicked("A");

            Assert.That(inactive.LocationClickCount, Is.Zero);
            Assert.That(fallback.LocationClickCount, Is.EqualTo(1));
        }

        [Test]
        public void Routing_PassthroughContinuesAndConsumedStops()
        {
            var first = new RecordingInteraction(
                "first",
                InteractionPriority.PendingResolution)
            {
                LocationResult = InteractionResult.Passthrough
            };
            var second = new RecordingInteraction(
                "second",
                InteractionPriority.ActiveAction)
            {
                LocationResult = InteractionResult.Consumed
            };
            var fallback = new RecordingInteraction(
                "fallback",
                InteractionPriority.DefaultRoute);
            var router = CreateRouter();
            router.Register(fallback);
            router.Register(second);
            router.Register(first);

            var result = router.OnLocationClicked("A");

            Assert.That(first.LocationClickCount, Is.EqualTo(1));
            Assert.That(second.LocationClickCount, Is.EqualTo(1));
            Assert.That(fallback.LocationClickCount, Is.Zero);
            Assert.That(result, Is.SameAs(InteractionResult.Consumed));
        }

        [Test]
        public void Routing_RejectedResultShowsPromptAndStops()
        {
            var prompts = new List<string>();
            var rejecting = new RecordingInteraction(
                "rejecting",
                InteractionPriority.PendingResolution)
            {
                LocationResult =
                    InteractionResult.RejectedWithPrompt("目标不可用")
            };
            var fallback = new RecordingInteraction(
                "fallback",
                InteractionPriority.DefaultRoute);
            var router = new InteractionRouter(prompts.Add);
            router.Register(fallback);
            router.Register(rejecting);

            var result = router.OnLocationClicked("A");

            Assert.That(prompts, Is.EqualTo(new[] { "目标不可用" }));
            Assert.That(fallback.LocationClickCount, Is.Zero);
            Assert.That(result, Is.SameAs(rejecting.LocationResult));
        }

        [Test]
        public void Routing_WhenNothingConsumesReturnsPassthrough()
        {
            var router = CreateRouter();
            router.Register(new RecordingInteraction("passthrough"));

            Assert.That(
                router.OnLocationClicked("A"),
                Is.SameAs(InteractionResult.Passthrough));
            Assert.That(
                router.OnInfluenceSlotClicked("slot-A"),
                Is.SameAs(InteractionResult.Passthrough));
            Assert.That(
                router.OnMobileCityClicked(),
                Is.SameAs(InteractionResult.Passthrough));
            Assert.That(
                router.OnEscape(),
                Is.SameAs(InteractionResult.Passthrough));
        }

        [Test]
        public void Routing_ForwardsEveryEntryPointAndArguments()
        {
            var interaction = new RecordingInteraction("all-entry-points");
            var router = CreateRouter();
            router.Register(interaction);

            router.OnLocationClicked("location-A");
            router.OnInfluenceSlotClicked("slot-B");
            router.OnMobileCityClicked();
            router.OnEscape();

            Assert.That(interaction.LocationIds, Is.EqualTo(new[] { "location-A" }));
            Assert.That(interaction.InfluenceSlotIds, Is.EqualTo(new[] { "slot-B" }));
            Assert.That(interaction.MobileCityClickCount, Is.EqualTo(1));
            Assert.That(interaction.EscapeCount, Is.EqualTo(1));
        }

        [Test]
        public void Routing_NullResultFailsWithInteractionContext()
        {
            var interaction = new RecordingInteraction("broken")
            {
                LocationResult = null
            };
            var router = CreateRouter();
            router.Register(interaction);

            Assert.That(
                () => router.OnLocationClicked("A"),
                Throws.InvalidOperationException.With.Message.Contains("broken"));
        }

        [Test]
        public void BuildActivePresentation_ReturnsHighestPriorityActivePresentation()
        {
            var pendingPresentation = Presentation(
                "待选结算",
                InteractionMode.Busy);
            var pending = new RecordingInteraction(
                "pending",
                InteractionPriority.PendingResolution)
            {
                Presentation = pendingPresentation
            };
            var active = new RecordingInteraction(
                "active",
                InteractionPriority.ActiveAction)
            {
                Presentation = Presentation(
                    "进行中动作",
                    InteractionMode.Busy)
            };
            var router = CreateRouter();
            router.Register(active);
            router.Register(pending);

            var result = router.BuildActivePresentation();

            Assert.That(result, Is.SameAs(pendingPresentation));
            Assert.That(pending.BuildPresentationCount, Is.EqualTo(1));
            Assert.That(active.BuildPresentationCount, Is.Zero);
        }

        [Test]
        public void BuildActivePresentation_UsesFirstRegistrationForEqualPriority()
        {
            var firstPresentation = Presentation(
                "第一项",
                InteractionMode.Busy);
            var first = new RecordingInteraction(
                "first",
                InteractionPriority.PendingResolution)
            {
                Presentation = firstPresentation
            };
            var second = new RecordingInteraction(
                "second",
                InteractionPriority.PendingResolution)
            {
                Presentation = Presentation(
                    "第二项",
                    InteractionMode.Busy)
            };
            var router = CreateRouter();
            router.Register(first);
            router.Register(second);

            var result = router.BuildActivePresentation();

            Assert.That(result, Is.SameAs(firstPresentation));
            Assert.That(second.BuildPresentationCount, Is.Zero);
        }

        [Test]
        public void BuildActivePresentation_SkipsEmptyPresentation()
        {
            var empty = new RecordingInteraction(
                "empty",
                InteractionPriority.PendingResolution)
            {
                Presentation = InteractionPresentation.Empty
            };
            var busy = new RecordingInteraction(
                "busy",
                InteractionPriority.ActiveAction)
            {
                Presentation = InteractionPresentation.Busy
            };
            var router = CreateRouter();
            router.Register(empty);
            router.Register(busy);

            var result = router.BuildActivePresentation();

            Assert.That(result, Is.SameAs(InteractionPresentation.Busy));
            Assert.That(empty.BuildPresentationCount, Is.EqualTo(1));
            Assert.That(busy.BuildPresentationCount, Is.EqualTo(1));
        }

        [Test]
        public void BuildActivePresentation_SkipsEquivalentEmptyPresentation()
        {
            var semanticallyEmpty = new RecordingInteraction(
                "semantic-empty",
                InteractionPriority.PendingResolution)
            {
                Presentation = new InteractionPresentation(
                    null,
                    null,
                    InteractionMode.Hidden)
            };
            var visible = new RecordingInteraction(
                "visible",
                InteractionPriority.ActiveAction)
            {
                Presentation = Presentation("进行中", InteractionMode.Busy)
            };
            var router = CreateRouter();
            router.Register(semanticallyEmpty);
            router.Register(visible);

            Assert.That(
                router.BuildActivePresentation(),
                Is.SameAs(visible.Presentation));
        }

        [Test]
        public void BuildActivePresentation_WhenNothingIsActiveReturnsEmpty()
        {
            var router = CreateRouter();
            router.Register(new RecordingInteraction("inactive")
            {
                Active = false
            });

            var result = router.BuildActivePresentation();

            Assert.That(result, Is.SameAs(InteractionPresentation.Empty));
            Assert.That(result.Highlights, Is.Empty);
            Assert.That(result.PromptText, Is.Empty);
            Assert.That(result.PanelMode, Is.EqualTo(InteractionMode.Hidden));
        }

        [Test]
        public void BuildActivePresentation_NullPresentationFailsWithInteractionContext()
        {
            var interaction = new RecordingInteraction("broken-presentation")
            {
                Presentation = null
            };
            var router = CreateRouter();
            router.Register(interaction);

            Assert.That(
                () => router.BuildActivePresentation(),
                Throws.InvalidOperationException.With.Message.Contains(
                    "broken-presentation"));
        }

        [Test]
        public void NotifyCommandSettled_BroadcastsToActiveAndInactiveInteractions()
        {
            var active = new RecordingInteraction(
                "active",
                InteractionPriority.ActiveAction);
            var inactive = new RecordingInteraction(
                "inactive",
                InteractionPriority.PendingResolution)
            {
                Active = false
            };
            var router = CreateRouter();
            router.Register(active);
            router.Register(inactive);

            router.NotifyCommandSettled("command-1");

            Assert.That(active.SettledCommandIds, Is.EqualTo(new[] { "command-1" }));
            Assert.That(inactive.SettledCommandIds, Is.EqualTo(new[] { "command-1" }));
        }

        [Test]
        public void NotifyCommandSettled_ContinuesBroadcastAndAggregatesFailures()
        {
            var failing = new RecordingInteraction(
                "failing",
                InteractionPriority.PendingResolution)
            {
                SettlementFailure = new InvalidOperationException("failure")
            };
            var following = new RecordingInteraction(
                "following",
                InteractionPriority.ActiveAction);
            var router = CreateRouter();
            router.Register(failing);
            router.Register(following);

            Assert.That(
                () => router.NotifyCommandSettled("command-2"),
                Throws.TypeOf<AggregateException>().With.Property("InnerExceptions")
                    .Count.EqualTo(1));
            Assert.That(
                following.SettledCommandIds,
                Is.EqualTo(new[] { "command-2" }));
        }

        [Test]
        public void CancelAll_ContinuesAfterFailureClearsRegistryAndAggregates()
        {
            var failing = new RecordingInteraction(
                "failing",
                InteractionPriority.PendingResolution)
            {
                CancelFailure = new InvalidOperationException("failure")
            };
            var following = new RecordingInteraction(
                "following",
                InteractionPriority.ActiveAction);
            var router = CreateRouter();
            router.Register(failing);
            router.Register(following);

            Assert.That(
                () => router.CancelAll(),
                Throws.TypeOf<AggregateException>().With.Property("InnerExceptions")
                    .Count.EqualTo(1));
            Assert.That(failing.CancelCount, Is.EqualTo(1));
            Assert.That(following.CancelCount, Is.EqualTo(1));

            Assert.That(
                router.OnLocationClicked("A"),
                Is.SameAs(InteractionResult.Passthrough));
            Assert.That(
                router.BuildActivePresentation(),
                Is.SameAs(InteractionPresentation.Empty));
            Assert.That(
                () => router.Register(new RecordingInteraction("failing")),
                Throws.Nothing);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void RejectedWithPrompt_RequiresReason(string reason)
        {
            Assert.That(
                () => InteractionResult.RejectedWithPrompt(reason),
                Throws.ArgumentException);
        }

        [Test]
        public void InteractionResult_ExposesExpectedKindsAndPrompt()
        {
            var rejected = InteractionResult.RejectedWithPrompt("拒绝原因");

            Assert.That(
                InteractionResult.Passthrough.Kind,
                Is.EqualTo(InteractionResultKind.Passthrough));
            Assert.That(
                InteractionResult.Consumed.Kind,
                Is.EqualTo(InteractionResultKind.Consumed));
            Assert.That(
                rejected.Kind,
                Is.EqualTo(InteractionResultKind.RejectedWithPrompt));
            Assert.That(rejected.PromptText, Is.EqualTo("拒绝原因"));
        }

        [Test]
        public void InteractionPresentation_CopiesHighlightsAndNormalizesNullText()
        {
            var highlights = new List<WorkflowHighlight>
            {
                new WorkflowHighlight(
                    WorkflowHighlightTargetKind.Location,
                    "A",
                    WorkflowHighlightSemantic.MoveTarget)
            };
            var presentation = new InteractionPresentation(
                highlights,
                null,
                InteractionMode.Busy);

            highlights.Clear();

            Assert.That(presentation.Highlights, Has.Count.EqualTo(1));
            Assert.That(presentation.Highlights[0].TargetId, Is.EqualTo("A"));
            Assert.That(presentation.PromptText, Is.Empty);
            Assert.That(presentation.ReplacesHighlights, Is.True);
            Assert.That(InteractionPresentation.Busy.ReplacesHighlights, Is.False);
            Assert.That(
                new InteractionPresentation(null, "外部展示", InteractionMode.Busy, false)
                    .ReplacesHighlights,
                Is.False);
        }

        [Test]
        public void InteractionBase_DefaultSettlementNotificationIsNoOp()
        {
            var interaction = new DefaultNotificationInteraction();

            Assert.That(
                () => interaction.NotifyCommandSettled("command"),
                Throws.Nothing);
        }

        private static InteractionRouter CreateRouter()
        {
            return new InteractionRouter(message => { });
        }

        private static InteractionPresentation Presentation(
            string prompt,
            InteractionMode mode)
        {
            return new InteractionPresentation(
                new List<WorkflowHighlight>().AsReadOnly(),
                prompt,
                mode);
        }

        private sealed class RecordingInteraction : InteractionBase
        {
            private readonly List<string> callOrder;

            public RecordingInteraction(
                string id,
                InteractionPriority priority = InteractionPriority.DefaultRoute,
                List<string> callOrder = null)
            {
                IdValue = id;
                PriorityValue = priority;
                this.callOrder = callOrder;
                LocationResult = InteractionResult.Passthrough;
                InfluenceSlotResult = InteractionResult.Passthrough;
                MobileCityResult = InteractionResult.Passthrough;
                EscapeResult = InteractionResult.Passthrough;
                Presentation = InteractionPresentation.Empty;
            }

            public string IdValue { get; set; }

            public override string Id
            {
                get { return IdValue; }
            }

            public InteractionPriority PriorityValue { get; set; }

            public override InteractionPriority Priority
            {
                get { return PriorityValue; }
            }

            public bool Active { get; set; } = true;

            public override bool IsActive
            {
                get { return Active; }
            }

            public InteractionResult LocationResult { get; set; }

            public InteractionResult InfluenceSlotResult { get; set; }

            public InteractionResult MobileCityResult { get; set; }

            public InteractionResult EscapeResult { get; set; }

            public InteractionPresentation Presentation { get; set; }

            public Exception SettlementFailure { get; set; }

            public Exception CancelFailure { get; set; }

            public int LocationClickCount { get; private set; }

            public int MobileCityClickCount { get; private set; }

            public int EscapeCount { get; private set; }

            public int BuildPresentationCount { get; private set; }

            public int CancelCount { get; private set; }

            public List<string> LocationIds { get; } = new List<string>();

            public List<string> InfluenceSlotIds { get; } = new List<string>();

            public List<string> SettledCommandIds { get; } = new List<string>();

            public override InteractionResult OnLocationClicked(string locationId)
            {
                LocationClickCount += 1;
                LocationIds.Add(locationId);
                callOrder?.Add(IdValue + ":location");
                return LocationResult;
            }

            public override InteractionResult OnInfluenceSlotClicked(string slotId)
            {
                InfluenceSlotIds.Add(slotId);
                callOrder?.Add(IdValue + ":influence");
                return InfluenceSlotResult;
            }

            public override InteractionResult OnMobileCityClicked()
            {
                MobileCityClickCount += 1;
                callOrder?.Add(IdValue + ":mobile-city");
                return MobileCityResult;
            }

            public override InteractionResult OnEscape()
            {
                EscapeCount += 1;
                callOrder?.Add(IdValue + ":escape");
                return EscapeResult;
            }

            public override InteractionPresentation BuildPresentation()
            {
                BuildPresentationCount += 1;
                return Presentation;
            }

            public override void Cancel()
            {
                CancelCount += 1;
                if (CancelFailure != null)
                {
                    throw CancelFailure;
                }
            }

            public override void NotifyCommandSettled(string commandId)
            {
                SettledCommandIds.Add(commandId);
                if (SettlementFailure != null)
                {
                    throw SettlementFailure;
                }
            }
        }

        private sealed class DefaultNotificationInteraction : InteractionBase
        {
            public override string Id
            {
                get { return "default-notification"; }
            }

            public override InteractionPriority Priority
            {
                get { return InteractionPriority.DefaultRoute; }
            }

            public override bool IsActive
            {
                get { return true; }
            }

            public override InteractionResult OnLocationClicked(string locationId)
            {
                return InteractionResult.Passthrough;
            }

            public override InteractionResult OnInfluenceSlotClicked(string slotId)
            {
                return InteractionResult.Passthrough;
            }

            public override InteractionResult OnMobileCityClicked()
            {
                return InteractionResult.Passthrough;
            }

            public override InteractionResult OnEscape()
            {
                return InteractionResult.Passthrough;
            }

            public override InteractionPresentation BuildPresentation()
            {
                return InteractionPresentation.Empty;
            }

            public override void Cancel()
            {
            }
        }
    }
}
