using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EffectInteractionContractTests
    {
        [Test]
        public void Router_UsesRendererCapabilityAndClearsCompletedOrHiddenRequest()
        {
            var router = new InteractionRequestRouter();
            var renderer = new Renderer();
            router.Register(renderer);
            var request = Request();
            Assert.That(router.RouteOpen(new[] { Request("unhandled"), request }, 1), Is.True);
            Assert.That(renderer.Projection.CandidateIds, Is.EqualTo(new[] { "iron" }));
            renderer.Projection.CandidateIds.Clear();
            Assert.That(request.CandidateIds, Has.Count.EqualTo(1), "UI 只能修改自己的投影。");
            Assert.That(router.RouteOpen(new[] { request }, 2), Is.False);
            Assert.That(renderer.ClearCount, Is.EqualTo(1));
            Assert.That(router.RouteOpen(new[] { request }, 1), Is.True);
            request.Status = "answered";
            Assert.That(router.RouteOpen(new[] { request }, 1), Is.False);
            Assert.That(renderer.ClearCount, Is.EqualTo(2));
        }

        [Test]
        public void VisibleRequestSource_UsesClientViewWithoutFallingBackToHostState()
        {
            var host = new GameState();
            var own = Request(); own.OwnerEffectId = "owner-effect"; own.SourceNodeId = "source-node";
            host.EffectRuntime.InteractionRequests.Add(own);
            var privateOther = Request(); privateOther.InteractionId = "hidden-request"; privateOther.AnsweringPlayerId = 2;
            host.EffectRuntime.InteractionRequests.Add(privateOther);
            var view = GameStateViewProjector.ProjectForPlayer(host, 1);
            var session = new YC.Application.Sessions.GameSession(new GameState());
            session.ReplaceView(view);
            Assert.That(session.State.EffectRuntime, Is.Null);
            var requests = VisibleInteractionRequestSource.Read(session.State, session.View, 1);
            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0].OwnerEffectId, Is.EqualTo("owner-effect"));
            Assert.That(requests[0].SourceNodeId, Is.EqualTo("source-node"));
            var router = new InteractionRequestRouter(); var renderer = new Renderer(); router.Register(renderer);
            Assert.That(router.RouteOpen(requests, 1), Is.True);
            Assert.That(renderer.Projection.CandidateIds, Is.EqualTo(new[] { "iron" }));
            requests[0].CandidateIds.Clear();
            Assert.That(view.Interactions[0].CandidateIds, Has.Count.EqualTo(1), "请求适配不能反写可见快照。");
            view.Interactions.Clear();
            Assert.That(VisibleInteractionRequestSource.Read(host, view, 1), Is.Empty,
                "已有客户端快照时，即使同时传入含隐藏请求的State，也不能回退读取。");
            Assert.That(VisibleInteractionRequestSource.Read(host, GameStateViewProjector.ProjectForPlayer(host, 2), 1), Is.Empty);
        }

        [Test]
        public void AnswerProtocol_PreservesObservedRevisionAndUsesUniqueCommandIdentity()
        {
            var request = Request();
            var projection = InteractionRequestProjector.ProjectForPlayer(request, 1);
            var first = EffectInteractionCommands.Answer(projection, 1, new[] { "iron" });
            var second = EffectInteractionCommands.Answer(projection, 1, new[] { "iron" });
            Assert.That(first.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(first.CommandId, Is.Not.EqualTo(second.CommandId), "重建 UI 后不能重复使用本地计数器 ID。");
            Assert.That(first.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("7"));
            Assert.That(first.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo("choice.1"));
            Assert.That(first.OptionIds, Is.EqualTo(new[] { "iron" }));
            Assert.Throws<System.ArgumentException>(() => EffectInteractionCommands.Answer(projection, 2, new[] { "iron" }));
            Assert.Throws<System.ArgumentException>(() => EffectInteractionCommands.Answer(projection, 1, null, true));
            request.AllowDecline = true;
            var cancel = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, null, true);
            Assert.That(cancel.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter], Is.EqualTo("false"));
            Assert.That(cancel.OptionIds, Is.Empty);
        }

        private static InteractionRequest Request(string type = "lua.choice") => new InteractionRequest
        {
            InteractionId = "choice.1", InteractionTypeId = type, AnsweringPlayerId = 1,
            Visibility = "owner", Status = "open", StateRevision = 7,
            CandidateIds = new List<string> { "iron" }, MinSelections = 1, MaxSelections = 1
        };

        private sealed class Renderer : IInteractionRequestRenderer
        {
            public string Id => "choice";
            public int Priority => 1;
            public InteractionRequestProjection Projection;
            public int ClearCount;
            public bool CanRender(InteractionRequestProjection request) => request.InteractionTypeId == "lua.choice";
            public void Render(InteractionRequestProjection request) { Projection = request; }
            public void Clear() { Projection = null; ClearCount++; }
        }
    }
}
