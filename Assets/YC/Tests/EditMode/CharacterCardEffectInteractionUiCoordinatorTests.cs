using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardEffectInteractionUiCoordinatorTests
    {
        private GameObject canvasObject;
        [Test]
        public void ActiveCharacterRequest_ReopensLostSelectionWithoutSubmittingOrLosingDraft()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "character-page-recovery", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 8,
                CandidateIds = new List<string> { "originium", "iron" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "character.elysium.strategy.choose_resource"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            var projection = YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1);
            renderer.Render(projection);
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            fixture.Dialog.GetType().GetMethod("Hide").Invoke(fixture.Dialog, null);
            renderer.Render(projection);
            Assert.That((bool)fixture.Dialog.GetType().GetProperty("IsShowing").GetValue(fixture.Dialog), Is.True,
                "正式请求仍在等待时，丢失的页面必须恢复，不能被相同 ID/版本缓存永久跳过。");
            Assert.That(commands, Is.Empty);
            ClickButton(GetOverlay(fixture.Dialog), "Confirm Selection");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "originium" }));
            renderer.Clear();
        }

        [Test]
        public void SaleQuantityInput_RejectsInvalidDraftAndSubmitsValidAmountOnlyOnce()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.Originium = 3;
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "sale-input", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "sale|originium|0", "sale|originium|1", "sale|originium|2", "sale|originium|3" },
                MinSelections = 1, MaxSelections = 4, PromptKey = "effect.resource.sell"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            var overlay = GetOverlay(fixture.Dialog);
            var input = FindChild(overlay, "Character Sale Value 0").GetComponent<InputField>();
            Assert.That(input, Is.Not.Null);
            var confirm = FindChild(overlay, "Confirm Character Effect").GetComponent<Button>();
            foreach (var invalid in new[] { "", "-1", "1.5", "4", "2147483648" })
            {
                input.onValueChanged.Invoke(invalid);
                Assert.That(confirm.interactable, Is.False, invalid);
                confirm.onClick.Invoke();
                Assert.That(commands, Is.Empty, invalid);
            }
            input.onValueChanged.Invoke("2");
            Assert.That(confirm.interactable, Is.True);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(3));
            confirm.onClick.Invoke(); confirm.onClick.Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "sale|originium|2" }));
            renderer.Clear();
        }



        [Test]
        public void SaleReceipt_UsesVisibleCompletedResultAndCompletionOrderIncludingZero()
        {
            var state = CreateState();
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState
            {
                EffectId = "sale-first-created", EffectTypeId = "effect.resource.sell", PlayerId = 1,
                Visibility = "owner", Status = EffectNodeStatus.Completed, NormalizedResult = NormalizedValue.CreateInteger(0)
            });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState
            {
                EffectId = "sale-second-created", EffectTypeId = "effect.resource.sell", PlayerId = 1,
                Visibility = "owner", Status = EffectNodeStatus.Completed, NormalizedResult = NormalizedValue.CreateInteger(7)
            });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState
            {
                EffectId = "other-public-sale", EffectTypeId = "effect.resource.sell", PlayerId = 2,
                Visibility = "public", Status = EffectNodeStatus.Completed, NormalizedResult = NormalizedValue.CreateInteger(900)
            });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState
            {
                EffectId = "failed-sale", EffectTypeId = "effect.resource.sell", PlayerId = 1,
                Visibility = "owner", Status = EffectNodeStatus.Failed, NormalizedResult = NormalizedValue.CreateInteger(800)
            });
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            visible.Events.Add(SaleCompletedView("sale-second-created", 2));
            visible.Events.Add(SaleCompletedView("sale-first-created", 3));
            visible.Events.Add(SaleCompletedView("failed-sale", 4));
            var type = Type.GetType("YC.Presentation.ResourceSaleReceiptProjection, Assembly-CSharp", true);
            var latest = type.GetMethod("Latest").Invoke(null, new object[] { visible, 1 });
            Assert.That(latest, Is.Not.Null);
            Assert.That(latest.GetType().GetField("EffectId").GetValue(latest), Is.EqualTo("sale-first-created"));
            Assert.That(latest.GetType().GetField("Revenue").GetValue(latest), Is.EqualTo(0L));
            Assert.That(type.GetMethod("Latest").Invoke(null, new object[] { GameStateViewProjector.ProjectForPlayer(state, 2), 1 }), Is.Null);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(CreateState().FindPlayer(1).Resources.GoldVoucher));
        }

        private static RuleEventView SaleCompletedView(string effectId, int revision)
        {
            return new RuleEventView
            {
                EventType = "EffectCompleted", PlayerId = 1, Visibility = "owner", StateRevision = revision,
                Payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "effectId", Value = NormalizedValue.CreateString(effectId) },
                    new NormalizedValueEntry { Name = "effectTypeId", Value = NormalizedValue.CreateString("effect.resource.sell") },
                    new NormalizedValueEntry { Name = "outcome", Value = NormalizedValue.CreateString("completed") }
                })
            };
        }

        [Test]
        public void ReplacementChoice_MapsStableInfluenceToSlotAndOnlySubmitsAnswer()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "opponent", PlayerId = 2, SlotId = "location:A-02:0" });
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "replace-ui", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "opponent" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "effect.influence.replace.target"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.MapEffectInteractionUiCoordinator, Assembly-CSharp", true);
            string prompt = "";
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(value => prompt = value) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(prompt, Does.Contain("替换"));
            type.GetMethod("OnInfluenceSlotClicked").Invoke(renderer, new object[] { "location:A-02:0" });
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "opponent" }));
            Assert.That(state.Map.Influences.Exists(i => i.InfluenceId == "opponent"), Is.True);
            renderer.Clear();
        }

        [Test]
        public void ZeroSale_OnlyExplicitFinishSubmitsWithoutChangingResources()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "zero-sale", InteractionTypeId = "character.ability.choice.awaiting_trade", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "sale|originium|0", "sale|originium|1" },
                MinSelections = 1, MaxSelections = 4, PromptKey = "character.cannot.strategy.choose_sale"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(_ => { }) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            int before = state.FindPlayer(1).Resources.Originium;
            var saleOverlay = GetOverlay(fixture.Dialog);
            Assert.That(FindChild(saleOverlay, "Confirm Character Effect").GetComponent<Button>().interactable, Is.False);
            ClickButton(saleOverlay, "Confirm Character Effect");
            Assert.That(commands, Is.Empty, "全零草稿不能确认出售。");
            ClickButton(saleOverlay, "Cancel Character Effect");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "sale|originium|0" }));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(before));
            renderer.Clear();
        }

        [TestCase("continue", 0)]
        [TestCase("decline", 1)]
        public void OptionalEffect_UsesExistingDialogAndOnlySubmitsDecision(string choice, int button)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "optional-test", InteractionTypeId = "action.decline_effect", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 3, AllowDecline = true,
                CandidateIds = new List<string> { "continue", "decline" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "character.tin_man.purchase.12"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            string prompt = "";
            var type = Type.GetType("YC.Presentation.MapEffectInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(text => prompt = text) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(prompt, Does.Contain("12 金券"));
            var before = state.FindPlayer(1).Resources.GoldVoucher;
            var row = FindChild(GetOverlay(fixture.Dialog), "Character Effect Option " + button);
            var effectRow = row.GetComponent(Type.GetType("YC.Presentation.UiEffectRowView, Assembly-CSharp", true));
            ((Button)effectRow.GetType().GetProperty("StatusButton").GetValue(effectRow)).onClick.Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { choice }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before));
            renderer.Clear();
        }

        [TestCase(1)]
        [TestCase(2)]
        public void GenericChoice_RendersExistingDialogAndSubmitsOnlyConfirmedCandidates(int maxSelections)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "lua.choice.test", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 8,
                CandidateIds = new List<string> { "originium", "iron" }, MinSelections = 1, MaxSelections = maxSelections,
                PromptKey = "character.elysium.strategy.choose_resource"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var type = Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp", true);
            var renderer = (IInteractionRequestRenderer)Activator.CreateInstance(type,
                new object[] { new Func<GameState>(() => state), new Func<int>(() => 1), fixture.Dialog,
                    new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                    new Action<YC.Domain.Commands.GameCommand>(command => commands.Add(command)), new Action<string>(_ => { }) });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            var before = state.FindPlayer(1).Resources.Originium;
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            if (maxSelections == 2)
            {
                Assert.That(commands, Is.Empty, "多选草稿不能提前提交。");
                ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 1");
                Assert.That(commands, Is.Empty);
            }
            Assert.That(commands, Is.Empty, "单选与多选都只编辑草稿。");
            ClickButton(GetOverlay(fixture.Dialog), "Confirm Selection");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(maxSelections == 1 ? new[] { "originium" } : new[] { "originium", "iron" }));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("8"));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(before), "UI 不能修改权威资源。");
            renderer.Clear();
        }

        [Test]
        public void ClientVisibleChoice_RechecksCurrentPermissionAndIgnoresEmptyOrUnrelatedAcknowledgement()
        {
            var authority = CreateState();
            authority.EffectRuntime.InteractionRequests.Add(new InteractionRequest
            {
                InteractionId = "client-choice", InteractionTypeId = "lua.choice", AnsweringPlayerId = 1,
                Visibility = "owner", Status = "open", StateRevision = 10,
                CandidateIds = new List<string> { "iron" }, MinSelections = 1, MaxSelections = 1
            });
            var visible = GameStateViewProjector.ProjectForPlayer(authority, 1);
            var client = GameStateViewProjector.ToClientState(visible);
            Assert.That(client.EffectRuntime, Is.Null);
            var fixture = CreateCoordinator(client);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(client, fixture.Dialog, commands);
            Func<IReadOnlyList<InteractionRequest>> source = () => VisibleInteractionRequestSource.Read(client, visible, 1);
            renderer.GetType().GetProperty("GetVisibleRequests").SetValue(renderer, source);
            var router = new InteractionRequestRouter(); router.Register(renderer);
            Assert.That(router.RouteOpen(source(), 1), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            var confirm = FindChild(GetOverlay(fixture.Dialog), "Confirm Selection").GetComponent<Button>().onClick;
            visible.Interactions[0].Visibility = "player:2";
            confirm.Invoke();
            Assert.That(commands, Is.Empty, "回调必须重新核对当前可见权限。");
            visible.Interactions[0].Visibility = "owner";
            confirm.Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            var settled = renderer.GetType().GetMethod("NotifyCommandSettled");
            foreach (var id in new[] { "", "unrelated-command" })
            {
                settled.Invoke(renderer, new object[] { id });
                Assert.That(router.RouteOpen(source(), 1), Is.True);
                Assert.That(GetOverlay(fixture.Dialog), Is.Null, "非本命令回包不能释放pending并重建可提交页。");
            }
            settled.Invoke(renderer, new object[] { commands[0].CommandId });
            Assert.That(router.RouteOpen(source(), 1), Is.True);
            Assert.That(FindChild(GetOverlay(fixture.Dialog), "Confirm Selection").GetComponent<Button>().interactable, Is.True,
                "匹配拒绝回包后保留草稿。");
            visible.Interactions.Clear();
            Assert.That(router.RouteOpen(source(), 1), Is.False);
            confirm.Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            renderer.Clear();
        }

        [Test]
        public void GenericDraft_RefreshRejectAndStageChangePreserveOnlyCurrentCandidates()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "draft-refresh", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "originium", "iron" }, MinSelections = 1, MaxSelections = 2
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            var staleConfirm = FindChild(GetOverlay(fixture.Dialog), "Confirm Selection").GetComponent<Button>().onClick;
            request.StateRevision++;
            staleConfirm.Invoke();
            Assert.That(commands, Is.Empty, "旧版本回调不能提交。");
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            ClickButton(GetOverlay(fixture.Dialog), "Confirm Selection");
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "originium" }));
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(GetOverlay(fixture.Dialog), Is.Null, "pending 不能重建可提交页。");
            renderer.GetType().GetMethod("NotifyCommandSettled").Invoke(renderer, new object[] { commands[0].CommandId });
            var olderProjection = YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1);
            olderProjection.StateRevision--;
            renderer.Render(olderProjection);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null, "拒绝释放锁后也不得复活低版本投影。");
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(FindChild(GetOverlay(fixture.Dialog), "Confirm Selection").GetComponent<Button>().interactable, Is.True,
                "拒绝后保留同一请求的有效草稿。");
            request.InteractionId = "new-stage";
            request.CandidateIds = new List<string> { "iron" };
            request.StateRevision++;
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(FindChild(GetOverlay(fixture.Dialog), "Confirm Selection").GetComponent<Button>().interactable, Is.False,
                "新请求不得继承上一阶段选择。");
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            renderer.GetType().GetMethod("NotifyCommandSettled").Invoke(renderer, new object[] { commands[0].CommandId });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            ClickButton(GetOverlay(fixture.Dialog), "Confirm Selection");
            Assert.That(commands, Has.Count.EqualTo(2), "旧回包不能清理新请求的草稿。");
            Assert.That(commands[1].OptionIds, Is.EqualTo(new[] { "iron" }));
            renderer.GetType().GetMethod("NotifyCommandSettled").Invoke(renderer, new object[] { commands[0].CommandId });
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            Assert.That(GetOverlay(fixture.Dialog), Is.Null, "旧回包不能释放新请求的 pending 锁。");
            renderer.Clear();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GenericDraft_CancelAndConfirmShareOneSubmission(bool cancelFirst)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "cancel-confirm", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "iron" }, MinSelections = 1, MaxSelections = 1,
                AllowDecline = true
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");
            var overlay = GetOverlay(fixture.Dialog);
            var confirm = FindChild(overlay, "Confirm Selection").GetComponent<Button>().onClick;
            var cancel = FindChild(overlay, "Cancel Selection").GetComponent<Button>().onClick;
            (cancelFirst ? cancel : confirm).Invoke();
            (cancelFirst ? confirm : cancel).Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            if (cancelFirst)
                Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.AnswerValueParameter], Is.EqualTo("false"));
            else Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "iron" }));
            renderer.Clear();
        }

        [TestCase(ResourceType.Originium, "originium", 0)]
        [TestCase(ResourceType.OriginiumShard, "originium-shard", 1)]
        [TestCase(ResourceType.Iron, "iron", 2)]
        [TestCase(ResourceType.PureOriginium, "pure-originium", 3)]
        public void SaleDraft_InventoryRefreshClampsAndConfirmFinishShareSubmissionGuard(ResourceType resource, string resourceId, int index)
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.Set(resource, 3);
            var fixture = CreateCoordinator(state);
            var request = new InteractionRequest
            {
                InteractionId = "sale-refresh", InteractionTypeId = "lua.choice", Status = "open",
                Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 1,
                CandidateIds = new List<string> { "sale|originium|0", "sale|" + resourceId + "|1", "sale|" + resourceId + "|2", "sale|" + resourceId + "|3" },
                MinSelections = 1, MaxSelections = 4, PromptKey = "effect.resource.sell"
            };
            state.EffectRuntime.InteractionRequests.Add(request);
            var commands = new List<YC.Domain.Commands.GameCommand>();
            var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            ClickButton(GetOverlay(fixture.Dialog), "Character Sale Increase " + index);
            ClickButton(GetOverlay(fixture.Dialog), "Character Sale Increase " + index);
            Assert.That(commands, Is.Empty);
            state.FindPlayer(1).Resources.Set(resource, 1);
            renderer.Render(YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1));
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Sale Value " + index), Is.EqualTo("1"));
            var amounts = new int[4]; amounts[index] = 1;
            var quote = new YC.Domain.Economy.ResourceSaleService().Sell(state.FindPlayer(1).Resources.Clone(),
                new YC.Domain.Economy.ResourceSaleRequest(amounts[0], amounts[1], amounts[2], amounts[3]));
            var profile = fixture.Dialog.GetType().GetField("layoutProfile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Dialog);
            var summaryFormat = (string)profile.GetType().GetProperty("SaleSummaryFormat").GetValue(profile);
            Assert.That(GetText(overlay, "Character Sale Summary"), Is.EqualTo(string.Format(summaryFormat, quote.Revenue)),
                "库存下降后仍按对应资源的正式规则单价重算预期收入。");
            var confirm = FindChild(overlay, "Confirm Character Effect").GetComponent<Button>().onClick;
            var finish = FindChild(overlay, "Cancel Character Effect").GetComponent<Button>().onClick;
            confirm.Invoke();
            finish.Invoke();
            confirm.Invoke();
            Assert.That(commands, Has.Count.EqualTo(1));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { "sale|" + resourceId + "|1" }));
            Assert.That(state.FindPlayer(1).Resources.Get(resource), Is.EqualTo(1), "UI 不提前扣库存。");
            renderer.Clear();
        }

        private static IInteractionRequestRenderer CreateGenericRenderer(GameState state, object dialog,
            List<YC.Domain.Commands.GameCommand> commands)
        {
            var type = Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp", true);
            return (IInteractionRequestRenderer)Activator.CreateInstance(type, new object[]
            {
                new Func<GameState>(() => state), new Func<int>(() => 1), dialog,
                new Action<IReadOnlyList<WorkflowHighlight>>(_ => { }), new Action(() => { }),
                new Action<YC.Domain.Commands.GameCommand>(commands.Add), new Action<string>(_ => { })
            });
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                canvasObject = null;
            }
        }

        [TestCase(CharacterCardEffectKind.CannotRequisition, "tactic")]
        [TestCase(CharacterCardEffectKind.CannotTradeChannel, "strategy")]
        [TestCase(CharacterCardEffectKind.ElysiumLogistics, "strategy")]
        [TestCase(CharacterCardEffectKind.ElysiumNavigation, "tactic")]
        [TestCase(CharacterCardEffectKind.TexasSpecialDelivery, "strategy")]
        [TestCase(CharacterCardEffectKind.TexasRemoveAndDoubleMove, "tactic")]
        [TestCase(CharacterCardEffectKind.LiskarmSecurityProtocol, "strategy")]
        [TestCase(CharacterCardEffectKind.LiskarmControlPosition, "tactic")]
        [TestCase(CharacterCardEffectKind.TinManEstablishPrestige, "strategy")]
        [TestCase(CharacterCardEffectKind.TinManDeepPlanning, "tactic")]
        public void AllCharacterEntrances_SubmitOnceWithoutLegacyPreselection(CharacterCardEffectKind effect, string mode)
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state, mapEffectResult: true);
            Assert.That(BeginEffect(fixture.Coordinator, mode, effect), Is.True);
            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));
            Assert.That(fixture.SubmittedMode, Is.EqualTo(mode));
            Assert.That(fixture.SubmittedEffect.Count, Is.EqualTo(1));
            Assert.That(fixture.SubmittedEffect[CharacterEffectParameterKeys.OfferSecondEffect], Is.EqualTo("true"));
            Assert.That(fixture.MapEffectBegun, Is.False);
            Assert.That(fixture.FacilitySelectionBegun, Is.False);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null, "选项只能由内核 Interaction 创建。");
        }

        [Test]
        public void UnifiedInteraction_FacilitySelectionIsActiveAndEscapeCancelsIt()
        {
            var state = CreateState();
            var effectFixture = CreateCoordinator(state);
            var mapCoordinator = new YC.Presentation.CharacterMapInteractionCoordinator(
                () => state,
                () => 1,
                new CharacterCardPanelPresenter(),
                _ => { },
                () => { },
                (_, __) => { },
                _ => { },
                _ => { });
            var facilitySelectionActive = true;
            var cancellationCount = 0;
            var interactionType = Type.GetType(
                "YC.Presentation.CharacterCardInteraction, Assembly-CSharp",
                false);
            Assert.That(interactionType, Is.Not.Null);
            var constructors = interactionType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));
            var interaction = constructors[0].Invoke(new object[]
            {
                effectFixture.Coordinator,
                mapCoordinator,
                new Func<bool>(() => false),
                new Func<bool>(() => facilitySelectionActive),
                new Func<bool>(() =>
                {
                    cancellationCount += 1;
                    facilitySelectionActive = false;
                    return true;
                })
            });

            Assert.That(
                (bool)interactionType.GetProperty("IsActive").GetValue(interaction, null),
                Is.True);

            var result = (InteractionResult)interactionType
                .GetMethod("OnEscape")
                .Invoke(interaction, null);

            Assert.That(result, Is.SameAs(InteractionResult.Consumed));
            Assert.That(cancellationCount, Is.EqualTo(1));
            Assert.That(
                (bool)interactionType.GetProperty("IsActive").GetValue(interaction, null),
                Is.False);
        }

        [Test]
        public void ResourceSaleDialog_KeepsQuantityPricesAndDisablesTitleDragging()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.Originium = 2;
            state.FindPlayer(1).Resources.Iron = 1;
            var fixture = CreateCoordinator(state);

            IReadOnlyList<int> saleValues = null;
            fixture.Dialog.GetType().GetMethod("ShowResourceSale").Invoke(fixture.Dialog, new object[]
            {
                new[] { "源岩", "源石碎片", "异铁", "至纯源石" }, new[] { 2, 0, 1, 0 },
                new[] { 3, 3, 4, 15 }, new Action<IReadOnlyList<int>>(values => saleValues = values), null
            });
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Character Card Effect Panel").GetComponent<RectTransform>();
            var title = FindChild(overlay, "Title");
            var position = panel.anchoredPosition;
            DragDialogTitle(title, panel, new Vector2(180f, 0f));
            Assert.That(panel.anchoredPosition, Is.EqualTo(position), "拖动标题不能改变窗口位置。");
            ClickButton(overlay, "Character Sale Increase 0");
            ClickButton(overlay, "Character Sale Increase 2");
            Assert.That(GetText(overlay, "Character Sale Summary"), Is.EqualTo("预计获得 7 金券"));
            ClickButton(overlay, "Confirm Character Effect");

            Assert.That(saleValues, Is.EqualTo(new[] { 1, 0, 1, 0 }));
        }

        [Test]
        public void TinManStrategy_StartsAuthoritativeProgressiveSettlement()
        {
            var state = CreateState();
            var fixture = CreateCoordinator(state);

            Assert.That(BeginEffect(
                fixture.Coordinator,
                UseCharacterCardCommandHandler.Strategy,
                CharacterCardEffectKind.TinManEstablishPrestige), Is.True);

            Assert.That(fixture.SubmissionCount, Is.EqualTo(1));
            Assert.That(fixture.SubmittedMode, Is.EqualTo(UseCharacterCardCommandHandler.Strategy));
            Assert.That(
                fixture.SubmittedEffect.ContainsKey(
                    CharacterEffectParameterKeys.TinManPurchasePureOriginium12),
                Is.False);
            Assert.That(
                fixture.SubmittedEffect.ContainsKey(
                    CharacterEffectParameterKeys.TinManPurchasePureOriginium15),
                Is.False);
            Assert.That(fixture.SubmittedEffect[CharacterEffectParameterKeys.OfferSecondEffect],
                Is.EqualTo("true"));
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        [Test]
        public void TinManFirstPurchase_CancelSubmitsFinishChoice()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManFirstPurchase,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Title"), Does.Contain("第一步"));
            Assert.That(FindChild(overlay, "Character Effect Option 0"), Is.Not.Null);
            ClickButton(overlay, "Cancel Character Effect");

            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManFinishPurchasing));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManFirstPurchase_PaySubmitsOnlyCurrentStepChoice()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManFirstPurchase,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Character Effect Option 0");

            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManSecondPurchase_UsesDistinctChoiceAndInsufficientStateOnlyAllowsCancel()
        {
            var state = CreateState();
            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManSecondPurchase,
                CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium);
            state.PendingCharacterEffect.TinManPurchasePureOriginium12 = true;
            var fixture = CreateCoordinator(state);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Title"), Does.Contain("第二步"));
            ClickButton(overlay, "Character Effect Option 0");
            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));

            state.PendingCharacterEffect = TinManPurchasePending(
                CharacterPendingChoiceTypes.TinManSecondPurchase,
                null);
            state.PendingCharacterEffect.TinManPurchasePureOriginium12 = true;
            fixture.SubmittedPending = null;
            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            overlay = GetOverlay(fixture.Dialog);
            Assert.That(FindChild(overlay, "Character Effect Option 0"), Is.Null);
            ClickButton(overlay, "Cancel Character Effect");
            Assert.That(
                fixture.SubmittedPending[CharacterEffectParameterKeys.Choice],
                Is.EqualTo(CharacterEffectChoiceIds.TinManFinishPurchasing));
            Assert.That(
                fixture.SubmittedPending[
                    CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo(state.PendingCharacterEffect.SourceCommandId));
        }

        [Test]
        public void TinManPendingMove_UsesDialogForBranchThenTransfersToMap()
        {
            var state = CreateState();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var sourceLocation = map.Locations[0];
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                LocationId = sourceLocation.LocationId,
                SlotId = InfluenceService.GetLocationSlotId(sourceLocation.LocationId, 0)
            });
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                SourceCommandId = "tin-man-test",
                RemainingCardIds = { "character.red.p1.liskarm" },
                OptionIds = { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }
            };
            var fixture = CreateCoordinator(state, pendingMapResult: true);

            Assert.That(SynchronizePending(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Character Effect Description"), Does.Contain("雷蛇"));
            ClickButton(overlay, "Character Effect Option 1");

            Assert.That(fixture.PendingMapBegun, Is.True);
            Assert.That(fixture.SubmittedPending, Is.Null);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        private CoordinatorFixture CreateCoordinator(
            GameState state,
            bool mapEffectResult = false,
            bool pendingMapResult = false)
        {
            var coordinatorType = Type.GetType(
                "YC.Presentation.CharacterCardEffectInteractionUiCoordinator, Assembly-CSharp",
                false);
            var dialogType = Type.GetType(
                "YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp",
                false);
            var registryType = Type.GetType(
                "YC.Presentation.GameplayDialogRegistry, Assembly-CSharp",
                false);
            var effectViewType = Type.GetType(
                "YC.Presentation.EffectDialogShellView, Assembly-CSharp",
                false);
            Assert.That(coordinatorType, Is.Not.Null);
            Assert.That(dialogType, Is.Not.Null);
            Assert.That(registryType, Is.Not.Null);
            Assert.That(effectViewType, Is.Not.Null);

            canvasObject = new GameObject(
                "Character Effect Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(1920f, 1080f);

            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hudPrefab, Is.Not.Null);
            var registry = hudPrefab.GetComponentInChildren(registryType, true);
            Assert.That(registry, Is.Not.Null);
            var dialog = Activator.CreateInstance(
                dialogType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { registry, canvasObject.GetComponent<RectTransform>() },
                null);
            var presenter = new CharacterCardPanelPresenter();
            var fixture = new CoordinatorFixture(dialog);
            Func<GameState> getState = () => state;
            Func<int> getPlayerId = () => 1;
            Func<string, CharacterCardEffectKind, bool> beginMap = (mode, effect) =>
            {
                fixture.MapEffectBegun = mapEffectResult;
                fixture.MapEffect = effect;
                return mapEffectResult;
            };
            Func<bool> beginPendingMap = () =>
            {
                fixture.PendingMapBegun = pendingMapResult;
                return pendingMapResult;
            };
            Func<IReadOnlyList<string>, Action<string>, Action, bool> beginFacilitySelection =
                (facilityIds, select, cancel) =>
                {
                    fixture.FacilitySelectionBegun = true;
                    fixture.SelectableFacilityIds = facilityIds;
                    fixture.SelectFacility = select;
                    fixture.CancelFacilitySelection = cancel;
                    return true;
                };
            Action endFacilitySelection = () => fixture.FacilitySelectionEnded = true;
            Action<string, IReadOnlyDictionary<string, string>> submitEffect = (mode, parameters) =>
            {
                fixture.SubmissionCount += 1;
                fixture.SubmittedMode = mode;
                fixture.SubmittedEffect = parameters;
            };
            Action<IReadOnlyDictionary<string, string>> submitPending = parameters =>
                fixture.SubmittedPending = parameters;
            Action<string> setPrompt = prompt => fixture.LastPrompt = prompt;

            var constructors = coordinatorType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));
            fixture.Coordinator = constructors[0].Invoke(new object[]
            {
                getState,
                getPlayerId,
                presenter,
                dialog,
                beginPendingMap,
                endFacilitySelection,
                submitEffect,
                submitPending,
                setPrompt
            });
            return fixture;
        }

        private static void DragDialogTitle(
            GameObject title,
            RectTransform panel,
            Vector2 screenDelta)
        {
            var eventSystemObject = new GameObject("Character Effect Drag Test EventSystem", typeof(EventSystem));
            eventSystemObject.transform.SetParent(title.transform.root, false);
            var eventSystem = eventSystemObject.GetComponent<EventSystem>();
            var start = RectTransformUtility.WorldToScreenPoint(null, title.transform.position);
            var eventData = new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                position = start
            };

            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.beginDragHandler),
                Is.False, "已禁用拖动处理器，标题不应接管拖拽。");
            eventData.position = start + screenDelta;
            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.dragHandler),
                Is.False);
            Assert.That(
                ExecuteEvents.Execute(title, eventData, ExecuteEvents.endDragHandler),
                Is.False);
            UnityEngine.Object.DestroyImmediate(eventSystemObject);
        }

        private static bool BeginEffect(object coordinator, string mode, CharacterCardEffectKind effect)
        {
            return (bool)coordinator.GetType()
                .GetMethod("TryBeginEffect", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(coordinator, new object[] { mode, effect });
        }

        private static bool SynchronizePending(object coordinator)
        {
            return (bool)coordinator.GetType()
                .GetMethod("SynchronizePending", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(coordinator, null);
        }

        private static GameObject GetOverlay(object dialog)
        {
            var shell = dialog.GetType()
                .GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(dialog);
            Assert.That(shell, Is.Not.Null);
            var view = shell.GetType()
                .GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(shell) as Component;
            return view == null ? null : view.gameObject;
        }

        private static void ClickButton(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            var button = child.GetComponent<Button>();
            if (button == null)
            {
                var card = child.GetComponent(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
                if (card != null) button = (Button)card.GetType().GetProperty("Button").GetValue(card);
            }
            Assert.That(button, Is.Not.Null, "Missing button binding " + objectName + ".");
            button.onClick.Invoke();
        }

        private static string GetText(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing text " + objectName + ".");
            return child.GetComponent<Text>().text;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            if (root == null)
            {
                return null;
            }

            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].name == objectName)
                {
                    return children[i].gameObject;
                }
            }

            return null;
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Resources = { GoldVoucher = 30 }
                    }
                }
            };
        }

        private static PendingCharacterEffectState TinManPurchasePending(
            string choiceType,
            string purchaseChoiceId)
        {
            var pending = new PendingCharacterEffectState
            {
                ChoiceType = choiceType,
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                SourceCommandId = "tin-man-purchase-test"
            };
            pending.OptionIds.Add(CharacterEffectChoiceIds.TinManFinishPurchasing);
            if (!string.IsNullOrEmpty(purchaseChoiceId))
            {
                pending.OptionIds.Add(purchaseChoiceId);
            }

            return pending;
        }

        private sealed class CoordinatorFixture
        {
            public CoordinatorFixture(object dialog)
            {
                Dialog = dialog;
            }

            public object Coordinator { get; set; }
            public object Dialog { get; private set; }
            public bool MapEffectBegun { get; set; }
            public CharacterCardEffectKind MapEffect { get; set; }
            public bool PendingMapBegun { get; set; }
            public bool FacilitySelectionBegun { get; set; }
            public bool FacilitySelectionEnded { get; set; }
            public IReadOnlyList<string> SelectableFacilityIds { get; set; }
            public Action<string> SelectFacility { get; set; }
            public Action CancelFacilitySelection { get; set; }
            public string SubmittedMode { get; set; }
            public IReadOnlyDictionary<string, string> SubmittedEffect { get; set; }
            public int SubmissionCount { get; set; }
            public IReadOnlyDictionary<string, string> SubmittedPending { get; set; }
            public string LastPrompt { get; set; }
        }
    }
}
