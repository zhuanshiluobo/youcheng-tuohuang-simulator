using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 四个独立正式开局；只运行本次角色使用，不推进八轮或改写盖牌/资源/种子。
    [Timeout(600000)]
    public sealed class EnterpriseCharacterQuickActionUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private IList<IGameCommandHandler> handlers;
        private IGameCommandHandler[] originalHandlers;
        private readonly List<GameCommand> observed = new List<GameCommand>();
        private EffectRegistry effects;
        private string amiyaSource;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (handlers != null && originalHandlers != null)
                for (var i = 0; i < originalHandlers.Length; i++) handlers[i] = originalHandlers[i];
            if (f?.Gameplay != null)
            {
                Save("验收终态", f.State);
                yield return f.Capture("角色验收终态_包含失败现场", (Component)f.Hud);
            }
        }

        [UnityTest] public IEnumerator SampleScene_MlynarStrategy_ActualQuickActionAndConsumption() => Verify(4, "mlynar", "strategy");
        [UnityTest] public IEnumerator ThreePlayerScene_MlynarTactic_ActualQuickActionAndConsumption() => Verify(3, "mlynar", "tactic");
        [UnityTest] public IEnumerator SampleScene_MountainStrategy_ActualQuickActionAndConsumption() => Verify(4, "mountain", "strategy");
        [UnityTest] public IEnumerator ThreePlayerScene_MountainTactic_ActualQuickActionAndConsumption() => Verify(3, "mountain", "tactic");
        [UnityTest] public IEnumerator SampleScene_MlynarStrategyThenTactic_UsesOneOrderedTreeAndRestores() => Verify(4, "mlynar", "strategy", true);
        [UnityTest] public IEnumerator ThreePlayerScene_MlynarTacticThenStrategy_UsesOneOrderedTreeAndRestores() => Verify(3, "mlynar", "tactic", true);
        [UnityTest] public IEnumerator SampleScene_AmiyaSettlementConsumesRealSourceOnCurrentUseAndCompletesBothModes() => Verify(4, "mlynar", "strategy", true, true);

        [UnityTest]
        public IEnumerator SampleScene_SecondModeAutoContinuesAndStopPreservesResultsBeforeMainAction()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, Noop, "enterprise.rhine_lab", "texas");
            ObserveCommands();
            int playerId = (int)Read(f.Gameplay, "localPlayerId");
            Assert.That(playerId, Is.EqualTo(f.State.StartPlayerId), "本例使用正式先手双发资格");
            Assert.That(f.State.FindPlayer(playerId).CompletedMainActionsThisTurn, Is.Zero);
            var windowId = f.State.EffectRuntime.ActiveMainNodeId;
            var oldUses = new HashSet<string>(f.State.CharacterUses.Select(u => u.UseId));
            int originalGold = f.State.FindPlayer(playerId).Resources.GoldVoucher;
            Assert.That(originalGold, Is.GreaterThanOrEqualTo(3), "计谋使用本局原有资源，不补款");
            string cardId = Model().CoveredCardId;
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == cardId).DefinitionId, Is.EqualTo("texas"));

            yield return f.Click(HotseatUiInputDriver.Marker("action.tab.1"), "双发窄验_打开快速行动");
            yield return f.Click(HotseatUiInputDriver.Marker("action.character"), "双发窄验_使用真实德克萨斯");
            var use = f.State.CharacterUses.Single(u => !oldUses.Contains(u.UseId));
            string useId = use.UseId;
            Assert.That(use.SelectionProtocolVersion, Is.EqualTo(3));
            CheckParentTree(use);
            yield return ChooseCharacterOption("tactic");
            for (int step = 0; step < 24 && f.Request?.PromptKey != "character.texas.strategy.choose_facility"; step++)
            {
                Assert.That(f.Request, Is.Not.Null, "自动第二技能必须到达真实设施待选");
                Assert.That(f.Request.InteractionTypeId, Is.Not.EqualTo(CharacterLifecycleService.SecondModeInteractionTypeId));
                yield return SubmitSmallRevisionAnswer();
            }
            var pending = f.Request;
            Assert.That(pending?.PromptKey, Is.EqualTo("character.texas.strategy.choose_facility"));
            use = f.State.CharacterUses.Single(u => u.UseId == useId);
            Assert.That(use.ModeOrder, Is.EqualTo(new[] { "tactic", "strategy" }));
            Assert.That(use.CompletedModes, Is.EqualTo(new[] { "tactic" }));
            Assert.That(f.State.CharacterUses.Count(u => !oldUses.Contains(u.UseId)), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId), Is.False);
            Assert.That(f.State.FindPlayer(playerId).Resources.GoldVoucher, Is.EqualTo(originalGold - 3 + 12));
            Assert.That(pending.CandidateIds, Is.Not.Empty);
            var supplies = f.State.ItemZones.Items.Where(i => f.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId &&
                z.TemplateId == "core.zone.facility_supply_slot")).ToDictionary(i => i.ItemId, i => i.ZoneId);
            Assert.That(supplies, Is.Not.Empty);
            Assert.That((bool)Read(f.Gameplay, "CanStopPendingCharacterMode"), Is.True);
            var end = (Button)Read(f.Frame, "EndActionButton");
            Assert.That(end.IsInteractable(), Is.True, "实际结束按钮须消费 Host 停止能力");
            var actualPage = Active("EffectDialogShellView").Single(p => p.gameObject.activeInHierarchy);
            yield return f.Capture("自动第二技能_已获金券并等待设施", actualPage);
            var beforeStop = GameStateCloneService.DeepClone(f.State);
            Save("第二技能停止前", beforeStop);
            int commandsBefore = observed.Count;
            yield return f.Click(end.gameObject, "双发窄验_实际结束行动停止第二技能剩余");
            yield return Wait(() => f.State.CharacterUses.Single(u => u.UseId == useId).Finished && f.Request == null,
                "停止第二技能后本次使用未正常收尾");
            var submitted = observed.Skip(commandsBefore).ToArray();
            Assert.That(submitted.Length, Is.EqualTo(1), "未做主行动时停止不能额外提交 EndAction");
            var stop = submitted.Single();
            Assert.That(stop.Kind, Is.EqualTo(GameCommandKind.UseCharacterCard));
            Assert.That(stop.Parameters["intent"], Is.EqualTo("stop_remaining_mode"));
            Assert.That(stop.Parameters["useId"], Is.EqualTo(useId));
            Assert.That(stop.Parameters["interactionId"], Is.EqualTo(pending.GetStableInteractionId()));
            Assert.That(stop.Parameters["expectedRevision"], Is.EqualTo(pending.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            use = f.State.CharacterUses.Single(u => u.UseId == useId);
            Assert.That(use.SkippedModes, Is.EqualTo(new[] { "strategy" }));
            Assert.That(use.CompletedModes, Is.EqualTo(new[] { "tactic" }));
            Assert.That(use.StopCommandId, Is.EqualTo(stop.CommandId));
            Assert.That(f.State.FindPlayer(playerId).Resources.GoldVoucher, Is.EqualTo(beforeStop.FindPlayer(playerId).Resources.GoldVoucher));
            foreach (var item in supplies) Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item.Key).ZoneId, Is.EqualTo(item.Value));
            Assert.That(f.State.CurrentPlayerId, Is.EqualTo(playerId));
            Assert.That(f.State.EffectRuntime.ActiveMainNodeId, Is.EqualTo(windowId));
            Assert.That(f.State.FindPlayer(playerId).CompletedMainActionsThisTurn, Is.Zero);
            Assert.That(f.State.FindPlayer(playerId).RemainingMainActionsThisTurn, Is.EqualTo(beforeStop.FindPlayer(playerId).RemainingMainActionsThisTurn));
            Assert.That((bool)Read(f.Gameplay, "CanStopPendingCharacterMode"), Is.False);
            Assert.That(end.IsInteractable(), Is.False, "未完成主要行动不能继续结束窗口");
            var tabs = ((Component)f.Hud).GetComponentsInChildren(T("UiMainActionTabs"), true).Cast<Component>().Single();
            Assert.That((int)Read(tabs, "SelectedIndex"), Is.Zero, "停止第二技能后应直接回到主要行动页签");
            var mainSection = ((GameObject[])Read(tabs, "sections"))[0];
            Assert.That(mainSection.activeInHierarchy, Is.True, "主要行动分区必须在实际 HUD 中可见");
            yield return f.Capture("停止后保留收益_回到原主要行动窗口", (Component)f.Hud);

            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            var deploy = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.DeployInfluence,
                PlayerId = playerId, TargetId = influence.GetLegalPlacementSlotIds(f.State, playerId).First() };
            var result = f.Session.Submit(deploy);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            f.Record("停止后正常主要行动前置", new { deploy.Kind, deploy.TargetId, note = "正式 DeployInfluence 提交，不伪造主行动完成标记" });
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            for (int step = 0; step < 12 && f.Request != null; step++) yield return SubmitSmallRevisionAnswer();
            Assert.That(f.Request, Is.Null);
            Assert.That(f.State.FindPlayer(playerId).CompletedMainActionsThisTurn, Is.GreaterThan(0));
            Assert.That(end.IsInteractable(), Is.True);
            yield return f.Click(end.gameObject, "双发窄验_完成主要行动后真正结束窗口");
            yield return Wait(() => f.State.EffectRuntime.ActiveMainNodeId != windowId, "完成主行动并结束后窗口没有推进");
            f.Record("双发窄验完成", new { useId, cardId, originalGold, use.StopCommandId, use.SkippedModes,
                windowId, nextWindow = f.State.EffectRuntime.ActiveMainNodeId,
                note = "先手单样本实际模式选择及结束按钮；三来源、外部响应和恢复边界采用 009 领域证据" });
        }

        private IEnumerator SubmitSmallRevisionAnswer()
        {
            var request = f.Request; Assert.That(request, Is.Not.Null);
            Assert.That(request.InteractionTypeId, Is.Not.EqualTo(CharacterLifecycleService.SecondModeInteractionTypeId));
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction,
                PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            if (command.OptionIds.Count == 0) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "true";
            var result = f.Session.Submit(command);
            Assert.That(result.Succeeded, Is.True, request.InteractionTypeId + ": " + result.Validation?.Reason);
            f.Record("窄验非待验响应正式辅助提交", new { request.InteractionTypeId, request.PromptKey, command.OptionIds });
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
        }

        private static IEnumerator Noop(Component _) { yield break; }
        private CharacterCardPanelViewModel Model() => (CharacterCardPanelViewModel)Call(Read(f.Gameplay, "characterCardPresenter"),
            "BuildProjectedView", f.State, (int)Read(f.Gameplay, "localPlayerId"), Call(f.Gameplay, "VisibleSaleState"));

        private IEnumerator Verify(int players, string definition, string mode, bool bothModes = false, bool withAmiya = false)
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(players, Noop, "enterprise.rhine_lab", definition);
            effects = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>().OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            if (withAmiya) yield return PrepareRealAmiyaSource();
            var playerId = (int)Read(f.Gameplay, "localPlayerId"); var player = f.State.FindPlayer(playerId);
            Assert.That(f.State.CurrentPlayerId, Is.EqualTo(playerId));
            var model = Model();
            var card = f.State.ItemZones.Items.Single(i => i.ItemId == model.CoveredCardId);
            Assert.That(card.DefinitionId, Is.EqualTo(definition));
            Assert.That(f.State.CharacterCoverSlots.Count(s => s.PlayerId == playerId && !string.IsNullOrEmpty(s.CardInstanceId)), Is.EqualTo(1));
            Assert.That(f.State.CharacterCoverSlots.Any(s => s.PlayerId == playerId && s.CardInstanceId == card.ItemId && s.SlotId == card.SlotId), Is.True);
            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == card.ZoneId).TemplateId, Is.EqualTo("core.zone.character_covered"));
            Assert.That(model.CanUse && model.CanUseStrategy && model.CanUseTactic, Is.True, model.InteractionStatus);
            Assert.That(model.ResolveDefinitionId(card.ItemId), Is.EqualTo(definition));
            foreach (var ability in new[] { definition + ".strategy", definition + ".tactic" })
            {
                Assert.That(effects.CharacterActivationSubscriptions.ContainsKey(ability), Is.True, ability);
                Assert.That(effects.CharacterAbilityContracts.ContainsKey(ability), Is.True, ability);
            }
            ObserveCommands();
            var before = GameStateCloneService.DeepClone(f.State); Save("激活前正式状态", before);
            var beforeUseIds = new HashSet<string>(before.CharacterUses.Select(u => u.UseId));
            var beforeNodes = new HashSet<string>(before.EffectRuntime.EffectNodes.Select(n => n.EffectId));
            var allowance = new[] { player.RemainingMainActionsThisTurn, player.CompletedMainActionsThisTurn, player.RestrictedMainActionsThisTurn };
            if (definition == "mountain" && mode == "tactic")
                Assert.That(player.Resources.GoldVoucher, Is.GreaterThanOrEqualTo(2), "正式起点必须具备真实支付条件；不补资源");

            yield return f.Click(HotseatUiInputDriver.Marker("action.tab.1"), "进入实际快速行动页签");
            var panel = Read(f.Hud, "ActionPanelView"); var useButton = (Button)Read(panel, "UseCharacterButton");
            var row = useButton.GetComponent(T("UiQuickActionRow"));
            RecordQuickActionState("进入快速行动后_断言前", useButton, model);
            Assert.That(row, Is.Not.Null); Assert.That(useButton.gameObject.activeInHierarchy, Is.True);
            var expansion = Read(f.Hud, "ExpansionUi");
            var template = (Component)Read(expansion, "quickActionTemplate");
            Assert.That(template.gameObject, Is.Not.SameAs(useButton.gameObject), "角色行仍与动态授勋模板共用物体");
            Assert.That(template.gameObject.activeSelf, Is.False, "授勋模板本身不应显示");
            var markerType = HotseatUiInputDriver.RuntimeType("YC.PlayerJourney.PlayerAutomationId");
            Assert.That(template.GetComponentsInChildren(markerType, true).Cast<Component>()
                .Any(c => (string)Read(c, "Id") == "action.character"), Is.False, "模板复制了正式角色自动化ID");
            Call(expansion, "Refresh"); yield return Settle();
            RecordQuickActionState("正常扩展刷新后_断言前", useButton, Model());
            Assert.That(useButton.gameObject.activeInHierarchy, Is.True, "刷新隐藏了正式角色行");
            Assert.That(template.gameObject.activeSelf, Is.False);
            Assert.That(HotseatUiInputDriver.Marker("action.character"), Is.SameAs(useButton.gameObject));
            Assert.That(useButton.IsInteractable(), Is.True);
            var rowName = model.CoveredCards.Single(c => c.CardId == card.ItemId).DisplayName;
            Assert.That(((Text)Read(row, "label")).text, Is.EqualTo(string.Format((string)Read(row, "characterFormat"), rowName)));
            var action = Call(Read(f.Gameplay, "turnActionPresenter"), "BuildActionPanelViewModel");
            Assert.That((bool)Read(action, "CanUseCharacter"), Is.True);
            f.Record("真实角色快速行动整行", new { players, definition, mode, card.ItemId, card.DefinitionId, card.ZoneId, card.SlotId,
                card.ControllerSubject, model.CoveredCardId, model.CardDefinitionIds, model.CanUse, model.CanUseStrategy, model.CanUseTactic,
                rowName, ancestors = useButton.GetComponentsInParent<Transform>(true).Select(t => new { t.name, t.gameObject.activeSelf, t.gameObject.activeInHierarchy }).ToArray(),
                geometry = Geometry((RectTransform)useButton.transform), allowance });
            yield return f.Capture("真实盖牌与快速行动整行", (Component)f.Hud);
            yield return f.Click(useButton.gameObject, "角色整行打开使用页");
            var uses = observed.Where(c => c.Kind == GameCommandKind.UseCharacterCard).ToArray();
            Assert.That(uses.Length, Is.EqualTo(1), "首次打开建立唯一使用上下文");
            Assert.That(uses[0].PlayerId, Is.EqualTo(playerId));
            Assert.That(uses[0].TargetId, Is.EqualTo(card.ItemId));
            Assert.That(uses[0].Parameters["cardId"], Is.EqualTo(card.ItemId));
            Assert.That(!uses[0].Parameters.TryGetValue("effectMode", out var initialMode) || string.IsNullOrEmpty(initialMode), Is.True);
            Assert.That(uses[0].Parameters[CharacterEffectParameterKeys.OfferSecondEffect], Is.EqualTo("true"));
            Assert.That(uses[0].CommandId, Is.Not.Null.And.Not.Empty);
            var use = f.State.CharacterUses.Single(u => !beforeUseIds.Contains(u.UseId) && u.CardInstanceId == card.ItemId);
            CheckUnusedContext(use);
            CheckParentTree(use);
            Assert.That(f.Request.InteractionTypeId, Is.EqualTo(CharacterLifecycleService.ModeInteractionTypeId));
            if (bothModes)
            {
                if (!withAmiya) Assert.That(playerId, Is.EqualTo(f.State.StartPlayerId), "先手样本的双发必须来自真实先手资格");
                else
                {
                    Assert.That(playerId, Is.Not.EqualTo(f.State.StartPlayerId), "阿米娅样本保留正式轮换后的非先手身份");
                    Assert.That(use.DoubleUseGranted || use.CanOfferSecondEffect, Is.False, "阿米娅消费前不得已有双发资格");
                }
                if (!withAmiya) yield return EffectTargetUiAcceptance.Restore(f, effects, "未发动模式选择");
                use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                CheckUnusedContext(use);
            }
            UnityEngine.Events.UnityEvent staleClick = null;
            if (withAmiya)
            {
                var oldRequest = f.Request.GetStableInteractionId();
                var option = CharacterSettlementOptionQuery.GetOptions(f.State, playerId, use.UseId).Single(o => o.SourceItemId == amiyaSource);
                Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode);
                var rows = (IDictionary)Read(Read(f.Hud, "ExpansionUi"), "settlementRows");
                yield return Wait(() => rows.Contains(option.OptionId), "阿米娅当前使用结算槽未显示");
                var settlementRow = (Component)rows[option.OptionId];
                yield return f.Capture("真实阿米娅来源与当前Use结算槽", settlementRow);
                yield return f.Click(ButtonObject(settlementRow, "StatusButton"), "阿米娅实际结算槽消费来源");
                Assert.That(observed.Last().Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
                use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                Assert.That(use.SourceConsumed && use.DoubleUseGranted, Is.True);
                Assert.That(use.DoubleUseSourceItemId, Is.EqualTo(amiyaSource));
                Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == amiyaSource).ZoneId, Is.EqualTo(FindZone(f.State, "discard", playerId).ZoneId));
                Assert.That(f.Request.GetStableInteractionId(), Is.Not.EqualTo(oldRequest));
                Assert.That(f.Request.CandidateIds, Does.Not.Contain(CharacterLifecycleService.CancelChoiceId));
                Save("阿米娅已消费待顺序_仅观察非Host恢复", f.State);
                f.Record("阿米娅样本恢复边界", new { use.UseId, amiyaSource,
                    note = "受控补充内容与正式企业房间快照不同，Host 恢复正确拒绝 content_snapshot_mismatch；本例仅验真实结算槽与同 Use 完整双发，不修改内容哈希或绕过校验。恢复／幂等由托管领域样本单列，不当作真实阿米娅 UI Host 恢复。" });
                Assert.That(f.Request.CandidateIds, Does.Not.Contain(CharacterLifecycleService.CancelChoiceId));
                yield return ChooseCharacterOption(mode + "-first", click => staleClick = click);
            }
            else yield return ChooseCharacterOption(mode, click => staleClick = click);
            var afterFirstSelection = observed.Count;
            staleClick.Invoke(); yield return Settle();
            Assert.That(observed.Count, Is.EqualTo(afterFirstSelection), "旧模式确认回调重复提交");
            Assert.That(observed.Count(c => c.Kind == GameCommandKind.UseCharacterCard), Is.EqualTo(1));
            use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
            Assert.That(use.ModeOrder, Does.Contain(mode));
            string upgradedEnterprise = null;
            bool secondChoiceSeen = false;

            for (var step = 0; step < 48 && (!use.Finished || f.Request != null); step++)
            {
                Assert.That(f.State.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
                var request = f.Request;
                Assert.That(request, Is.Not.Null, "角色未结束但没有可回答请求，必须保留故障现场");
                Assert.That(request.CandidateIds, Is.Not.Empty, request.InteractionTypeId + "没有真实合法候选");
                if (request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId)
                {
                    Assert.That(secondChoiceSeen, Is.False, "同次使用重复征集第二模式");
                    secondChoiceSeen = true;
                    Assert.That(use.CompletedModes, Is.EqualTo(new[] { mode }));
                    Assert.That(use.ModeOrder, Is.EqualTo(new[] { mode }));
                    Assert.That(use.Finished, Is.False);
                    Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId,
                        Is.EqualTo(before.ItemZones.Items.Single(i => i.ItemId == card.ItemId).ZoneId));
                    if (bothModes) yield return EffectTargetUiAcceptance.Restore(f, effects, "首技能完成待第二选择");
                    yield return ChooseCharacterOption(bothModes ? (mode == "strategy" ? "tactic" : "strategy") : CharacterLifecycleService.FinishChoiceId);
                    use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                    continue;
                }
                if (request.InteractionTypeId == "enterprise.upgrade" || request.InteractionTypeId == "enterprise.special")
                {
                    var target = request.CandidateIds.FirstOrDefault(id => request.InteractionTypeId == "enterprise.special" && id != upgradedEnterprise)
                        ?? request.CandidateIds.First();
                    if (request.InteractionTypeId == "enterprise.upgrade") upgradedEnterprise = target;
                    yield return EffectTargetUiAcceptance.AnswerEnterprise(f, target);
                    use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                    continue;
                }
                if (request.InteractionTypeId == "effect.route.choose")
                {
                    yield return EffectTargetUiAcceptance.AnswerRoute(f, request.CandidateIds.First());
                    use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                    continue;
                }
                if (request.InteractionTypeId == "enterprise.department")
                {
                    yield return Wait(() => f.RequestPage() != null, "正式科室切换请求未绑定实际页面");
                    var page = f.RequestPage(); var draft = (EnterpriseSelectionDraft)Read(page, "Draft");
                    Assert.That(draft.Projection.Mode, Is.EqualTo(EnterpriseSelectionMode.SwitchDepartment));
                    yield return f.Capture("正式角色效果触发科室切换_确认前", page);
                    var candidate = request.CandidateIds.First(id => id != draft.Projection.CurrentDepartmentId);
                    var enterpriseId = draft.Projection.FixedCompany.Id;
                    yield return f.Click(Target(page, candidate), "正式切换选择其他科室");
                    yield return f.Click(ButtonObject(page, "confirm"), "正式科室切换确认");
                    Assert.That(f.State.ItemZones.Relations.Single(r => r.SourceItemId == enterpriseId &&
                        r.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation).TargetItemId, Is.EqualTo(candidate));
                    f.Record("正式科室切换来源与关系完成", new { enterpriseId, candidate, request.InteractionTypeId,
                        request.StateRevision, request.AnsweringPlayerId, note = "来自正式角色效果请求，真实页面选择/确认；区别于受控View补证" });
                    use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
                    continue;
                }
                var answer = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
                answer.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                answer.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
                var candidates = request.CandidateIds.AsEnumerable();
                Assert.That(request.InteractionTypeId, Is.Not.EqualTo(CharacterLifecycleService.ModeInteractionTypeId), "不能绕过实际模式选择页面");
                Assert.That(request.InteractionTypeId, Is.Not.EqualTo(CharacterLifecycleService.OrderInteractionTypeId), "顺序请求需专用实际 UI 用例");
                answer.OptionIds.AddRange(candidates.Take(Math.Max(1, request.MinSelections)));
                f.Record("角色真实结算候选与正式回答", new { request.InteractionTypeId, request.PromptKey, request.StateRevision,
                    request.CandidateIds, answer.OptionIds, note = "后续结算经正式AnswerInteraction；不声称全部候选通过鼠标完成" });
                var result = f.Session.Submit(answer);
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
                use = f.State.CharacterUses.Single(u => u.UseId == use.UseId);
            }
            // SourceConsumed指授予双模式的来源已消耗，普通角色弃置以实际区域及完成目的地核验。
            Assert.That(use.Finished, Is.True);
            var expectedModes = bothModes ? new[] { mode, mode == "strategy" ? "tactic" : "strategy" } : new[] { mode };
            Assert.That(use.CompletedModes, Is.EqualTo(expectedModes));
            Assert.That(use.ModeOrder, Is.EqualTo(expectedModes));
            if (bothModes && !withAmiya) Assert.That(secondChoiceSeen, Is.True, "先手逐次双发没有经过正式第二选择");
            if (withAmiya)
            {
                Assert.That(secondChoiceSeen, Is.False, "阿米娅顺序授权不应重新征集第二模式");
                Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CharacterLifecycleService.ConsumeSourceTypeId &&
                    Text(n.NormalizedArguments, "sourceItemId") == amiyaSource), Is.EqualTo(1));
            }
            Assert.That(use.Modes.Any(m => m.Mode == mode && m.AbilityId == definition + "." + mode && m.Activated), Is.True);
            Assert.That(f.Request, Is.Null);
            var afterCard = f.State.ItemZones.Items.Single(i => i.ItemId == card.ItemId);
            var destination = f.State.ItemZones.Zones.Single(z => z.ZoneId == afterCard.ZoneId);
            Assert.That(use.FinishDestinationZoneId, Is.EqualTo(destination.ZoneId));
            Assert.That(destination.TemplateId, Is.EqualTo("core.zone.character_discard"));
            Assert.That(destination.OwnerPlayerId, Is.EqualTo(playerId));
            Assert.That(f.State.CharacterCoverSlots.Any(s => s.CardInstanceId == card.ItemId), Is.False);
            var afterPlayer = f.State.FindPlayer(playerId);
            Assert.That(new[] { afterPlayer.RemainingMainActionsThisTurn, afterPlayer.CompletedMainActionsThisTurn, afterPlayer.RestrictedMainActionsThisTurn }, Is.EqualTo(allowance));
            var nodes = f.State.EffectRuntime.EffectNodes.Where(n => !beforeNodes.Contains(n.EffectId)).ToArray();
            Assert.That(nodes.Any(n => n.EffectTypeId == "character.use"), Is.True);
            Assert.That(nodes.Any(n => n.Status == EffectNodeStatus.Faulted), Is.False);
            foreach (var completedMode in expectedModes) CheckRuleFacts(before, f.State, definition, completedMode, playerId, nodes);
            Save("完成后正式状态", f.State);
            f.Record("正式角色使用完成", new { use, destination, nodes, remainingMainActions = afterPlayer.RemainingMainActionsThisTurn });
            var commandCount = observed.Count; staleClick.Invoke(); yield return Settle();
            Assert.That(observed.Count, Is.EqualTo(commandCount), "消费后旧卡面回调仍能提交");
            Assert.That(Model().CoveredCards.Any(c => c.CardId == card.ItemId), Is.False);
            if (definition == "mountain" && mode == "strategy")
            {
                EffectTargetUiAcceptance.CheckRoads(f);
                yield return EffectTargetUiAcceptance.Restore(f, effects, "山公路与角色已完成");
                EffectTargetUiAcceptance.CheckRoads(f);
            }
            yield return f.Capture("角色正常消费后的实际HUD", (Component)f.Hud);
        }

        private void RecordQuickActionState(string stage, Button button, CharacterCardPanelViewModel model)
        {
            var template = (Component)Read(Read(f.Hud, "ExpansionUi"), "quickActionTemplate");
            var tabs = ((Component)f.Hud).GetComponentsInChildren(T("UiMainActionTabs"), true).Cast<Component>().Single();
            var action = Call(Read(f.Gameplay, "turnActionPresenter"), "BuildActionPanelViewModel");
            f.Record("角色入口父链与模板状态", new { stage, selectedIndex = Read(tabs, "SelectedIndex"),
                model.CanUse, model.CanUseStrategy, model.CanUseTactic, actionCanUseCharacter = Read(action, "CanUseCharacter"),
                buttonInstance = button.gameObject.GetInstanceID(), button.gameObject.activeSelf, button.gameObject.activeInHierarchy,
                interactable = button.IsInteractable(), templateInstance = template.gameObject.GetInstanceID(),
                templateActiveSelf = template.gameObject.activeSelf, templateActiveInHierarchy = template.gameObject.activeInHierarchy,
                ancestors = button.GetComponentsInParent<Transform>(true).Select(t => new {
                    path = HotseatUiInputDriver.PathOf(t.gameObject), t.gameObject.activeSelf, t.gameObject.activeInHierarchy }).ToArray() });
        }

        private IEnumerator PrepareRealAmiyaSource()
        {
            int player = f.State.CurrentPlayerId;
            string target = Model().CoveredCardId;
            var contentRoot = Path.Combine(UnityEngine.Application.dataPath, "StreamingAssets/Content/core");
            // 阿米娅尚未列入正式 manifest；按已声明的受控目录边界精确读取其真实定义与 Lua。
            var definition = JsonConvert.DeserializeObject<ExternalContentDefinition>(File.ReadAllText(Path.Combine(contentRoot, "character/infected.character.amiya.json")));
            Assert.That(definition.DefinitionId, Is.EqualTo("infected.character.amiya"));
            var character = definition.Data.ToObject<CharacterCardDefinition>();
            character.CardId = character.TemplateId = definition.DefinitionId; character.Name = definition.DisplayName;
            character.IsExternalDefinition = true; character.StrategyEffect = character.TacticEffect = CharacterCardEffectKind.External;
            character.ConfiguredStrategyAbilityId = definition.Abilities.Single(a => a.Mode == "strategy").AbilityId;
            character.ConfiguredTacticAbilityId = definition.Abilities.Single(a => a.Mode == "tactic").AbilityId;
            Assert.That(f.State.ContentDefinitions.Characters.Any(c => c.CardId == character.CardId), Is.False,
                "此受控补证只补当前企业房间未启用的真实阿米娅，不能覆盖已有定义");
            f.State.ContentDefinitions.Characters.Add(character);
            foreach (var ability in definition.Abilities)
            {
                string source = File.ReadAllText(Path.Combine(contentRoot, ability.Script));
                effects.CharacterActivationSubscriptions.Add(ability.AbilityId, ability.SubscriptionId);
                effects.CharacterAbilityContracts.Add(ability.AbilityId, new CharacterModeContract { Mode = ability.Mode, AbilityId = ability.AbilityId,
                    SkillType = ability.SkillType, SettlementActionId = ability.SettlementActionId, TitleKey = ability.TitleKey, DescriptionKey = ability.DescriptionKey });
                new LuaEffectEventDispatcher(effects).Register(new LuaHandlerDefinition(ability.SubscriptionId,
                    CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability.AbilityId,
                    new LuaScriptDefinition("content." + ability.SubscriptionId, ability.AbilityId, ability.SubscriptionId, ability.Version, source, LuaContentHasher.ComputeSha256(source)),
                    EffectHandlerRole.Primary, 0, CharacterCardLuaCatalog.ContentInstanceId, ability.AbilityId));
            }
            string hand = FindZone(f.State, "hand", player).ZoneId;
            string covered = FindZone(f.State, "covered", player).ZoneId;
            RunPreparation(MoveSpec(player, new[] { target }, covered, hand, "unordered", 1, "core.character_card_transfer"));
            var replacedBasic = f.State.ItemZones.Items.First(i => i.ZoneId == hand && i.ItemId != target &&
                ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(i.TypeId, "core.basic_character_card"));
            RunPreparation(MoveSpec(player, new[] { replacedBasic.ItemId }, hand, FindZone(f.State, "retired").ZoneId,
                "unordered", 1, "core.character_card_transfer"));
            f.Record("阿米娅替换一张保留基础角色的受控前置", new { replacedBasic.ItemId, replacedBasic.DefinitionId,
                note = "先通过真实 Move Effect 将一张未盖牌基础角色移至 retired，再授予阿米娅，遵守最多保留五张基础角色；不增加上限、不伪造来源" });
            const string grant = "acceptance.amiya.grant";
            effects.Register(grant, context =>
            {
                if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "amiya", definition.DefinitionId,
                    definition.ItemTypeId, hand, ItemSubjectRef.Player(player), out string id, out string reason)) return EffectStepResult.Failed(reason);
                return EffectStepResult.Completed(Object("itemId", NormalizedValue.CreateString(id)));
            });
            var grantNode = RunPreparation(EffectSpec.Create(grant, playerId: player));
            amiyaSource = Text(grantNode.NormalizedResult, "itemId");
            RunPreparation(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: player));
            AnswerPreparation(amiyaSource);
            var sourceUse = RunPreparation(CharacterLifecycleService.CreateUseSpec(player, amiyaSource, "tactic"));
            Assert.That(sourceUse.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == amiyaSource).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
            RunPreparation(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: player));
            AnswerPreparation(target);
            yield return AdvanceAmiyaSourceToNextRound(player, target);
            Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            Assert.That(Model().CoveredCardId, Is.EqualTo(target));
            f.Record("真实阿米娅受控来源准备", new { target, amiyaSource, definition.DefinitionId,
                scripts = definition.Abilities.Select(a => a.Script).ToArray(),
                note = "企业真实房间后追加磁盘真实阿米娅目录／注册；通过原生授予、移动、盖牌和真实持续技能建立来源，再经正式部署、结束、空采集和盖牌推进下一回合，未重置使用标记。未开启感染者房间门禁；此补充目录与原房间内容哈希不同，不能作正式组合快照一致性的证据。后续结算槽与顺序选择走实际UI。" });
        }

        private IEnumerator AdvanceAmiyaSourceToNextRound(int player, string target)
        {
            int originalRound = f.State.Round;
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId)));
            for (int guard = 0; guard < 80; guard++)
            {
                if (f.State.Round == originalRound + 1 && f.State.CurrentPlayerId == player &&
                    f.State.Phase == GamePhase.ActionRound1 && f.Request == null) break;
                var request = f.Request;
                GameCommand command;
                if (request != null)
                {
                    command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
                    command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
                    command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
                    if (request.InteractionTypeId == "route_resource.selection")
                        command.OptionIds.Add(request.CandidateIds.Single(id => id.EndsWith(".commit", StringComparison.Ordinal)));
                    else if (request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId && request.AnsweringPlayerId == player)
                    {
                        Assert.That(request.CandidateIds, Does.Contain(target));
                        command.OptionIds.Add(target);
                    }
                    else command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
                    if (command.OptionIds.Count == 0) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "true";
                }
                else
                {
                    int actor = f.State.CurrentPlayerId;
                    bool deploy = (f.State.Phase == GamePhase.ActionRound1 || f.State.Phase == GamePhase.ActionRound2) &&
                        f.State.FindPlayer(actor).RemainingMainActionsThisTurn > 0;
                    command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"),
                        Kind = deploy ? GameCommandKind.DeployInfluence : GameCommandKind.EndAction, PlayerId = actor };
                    if (deploy) command.TargetId = influence.GetLegalPlacementSlotIds(f.State, actor).First();
                }
                var result = f.Session.Submit(command);
                Assert.That(result.Succeeded, Is.True, "阿米娅来源跨回合正式前置：" + command.Kind + "/" + request?.InteractionTypeId + " " + result.Validation?.Reason);
                f.Record("阿米娅来源跨回合正式辅助提交", new { command.Kind, command.PlayerId, command.TargetId, command.OptionIds,
                    requestType = request?.InteractionTypeId, f.State.Round, note = "正式协议加速非待验回合，不冒称鼠标输入，不修改回合或使用标记" });
                Call(f.Gameplay, "SynchronizeInteractionFromState"); yield return Settle();
            }
            Assert.That(f.State.Round, Is.EqualTo(originalRound + 1));
            Assert.That(f.State.CurrentPlayerId, Is.EqualTo(player));
            Assert.That(f.State.FindPlayer(player).UsedCharacterThisRound, Is.False);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == amiyaSource).ZoneId, Is.EqualTo(FindZone(f.State, "persistent", player).ZoneId));
        }

        private EffectNodeRuntimeState RunPreparation(EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(f.State, effects); var id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);
            var node = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(node.Status, Is.Not.EqualTo(EffectNodeStatus.Failed), node.FailureReason);
            return node;
        }

        private void AnswerPreparation(string candidate)
        {
            var request = f.Request; Assert.That(request, Is.Not.Null);
            Assert.That(request.InteractionTypeId, Is.EqualTo(CharacterCoverEffectExecutor.InteractionTypeId));
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
            command.OptionIds.Add(candidate);
            var result = f.Session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
        }

        [UnityTest]
        public IEnumerator SampleScene_UnstartedUseCancelPreservesCardAndAllowanceAndCanBeginAgain()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, Noop, "enterprise.rhine_lab", "mlynar");
            effects = (EffectRegistry)Read(((IEnumerable)Read(f.Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            ObserveCommands();
            var playerId = (int)Read(f.Gameplay, "localPlayerId");
            var cardId = Model().CoveredCardId;
            var zoneId = f.State.ItemZones.Items.Single(i => i.ItemId == cardId).ZoneId;
            var player = f.State.FindPlayer(playerId);
            var resources = JsonConvert.SerializeObject(player.Resources);
            var allowance = new[] { player.RemainingMainActionsThisTurn, player.CompletedMainActionsThisTurn, player.RestrictedMainActionsThisTurn };
            var previous = new HashSet<string>(f.State.CharacterUses.Select(u => u.UseId));
            yield return f.Click(HotseatUiInputDriver.Marker("action.tab.1"), "打开快速行动");
            yield return f.Click(HotseatUiInputDriver.Marker("action.character"), "建立未发动使用");
            var use = f.State.CharacterUses.Single(u => !previous.Contains(u.UseId));
            CheckUnusedContext(use); CheckParentTree(use);
            var firstId = use.UseId;
            var openRequest = f.Request.GetStableInteractionId();
            var beforeFold = observed.Count;
            yield return f.Click(ButtonObject(f.Frame, "FoldButton"), "仅收起当前角色卡面");
            Assert.That(observed.Count, Is.EqualTo(beforeFold));
            Assert.That(f.State.CharacterUses.Single(u => u.UseId == firstId).Finished, Is.False);
            yield return f.Click(ButtonObject(f.Frame, "FoldButton"), "展开未终结的同一卡面");
            Assert.That(observed.Count, Is.EqualTo(beforeFold));
            Assert.That(Model().CurrentUseId, Is.EqualTo(firstId));
            Assert.That(f.Request.GetStableInteractionId(), Is.EqualTo(openRequest));
            yield return ChooseCharacterOption(CharacterLifecycleService.CancelChoiceId);
            use = f.State.CharacterUses.Single(u => u.UseId == firstId);
            Assert.That(use.SelectionCancelled && use.Finished, Is.True);
            Assert.That(use.ModeOrder, Is.Empty); Assert.That(use.SourceConsumed, Is.False);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == cardId).ZoneId, Is.EqualTo(zoneId));
            Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(playerId).Resources), Is.EqualTo(resources));
            Assert.That(new[] { player.RemainingMainActionsThisTurn, player.CompletedMainActionsThisTurn, player.RestrictedMainActionsThisTurn }, Is.EqualTo(allowance));
            Assert.That(Model().CanUse, Is.True, "明确取消不能标记本牌已使用");
            yield return f.Click(HotseatUiInputDriver.Marker("action.character"), "取消后建立新使用");
            var reopened = f.State.CharacterUses.Single(u => !u.Finished && !u.SelectionCancelled && u.CardInstanceId == cardId);
            Assert.That(reopened.UseId, Is.Not.EqualTo(firstId)); CheckUnusedContext(reopened); CheckParentTree(reopened);
            Assert.That(observed.Count(c => c.Kind == GameCommandKind.UseCharacterCard), Is.EqualTo(2));
            yield return ChooseCharacterOption(CharacterLifecycleService.CancelChoiceId);
            Assert.That(f.Request, Is.Null);
            f.Record("明确取消与重新开始", new { firstId, reopened.UseId, cardId, zoneId, allowance });
        }

        private void CheckUnusedContext(CharacterUseRuntimeState use)
        {
            Assert.That(use.SelectionProtocolVersion, Is.EqualTo(2));
            Assert.That(use.Finished || use.SelectionCancelled || use.SourceConsumed, Is.False);
            Assert.That(use.ModeOrder, Is.Empty); Assert.That(use.CompletedModes, Is.Empty);
            Assert.That(use.Modes.Any(m => m.Activated), Is.False);
            Assert.That(f.State.CharacterUses.Count(u => !u.Finished && !u.SelectionCancelled && u.CardInstanceId == use.CardInstanceId), Is.EqualTo(1));
        }

        private void CheckParentTree(CharacterUseRuntimeState use)
        {
            var node = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == use.OwnerEffectId);
            var quick = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == node.ParentEffectId);
            Assert.That(node.EffectTypeId, Is.EqualTo(CharacterLifecycleService.UseTypeId));
            Assert.That(quick.EffectTypeId, Is.EqualTo(CharacterLifecycleService.QuickActionTypeId));
            Assert.That(quick.ChildSchedulingGroups, Is.Empty, "角色双模式不可放入独立组");
            var window = f.State.EffectRuntime.MainNodes.Single(n => n.NodeId == f.State.EffectRuntime.ActiveMainNodeId);
            Assert.That(quick.ParentEffectId, Is.EqualTo(window.ExecutionEffectId));
            f.Record("角色有序父链", new { use.UseId, use.CardInstanceId, owner = node.EffectId, quick = quick.EffectId, window = window.ExecutionEffectId });
        }

        private IEnumerator ChooseCharacterOption(string id, Action<UnityEngine.Events.UnityEvent> remember = null)
        {
            var request = f.Request;
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds, Does.Contain(id));
            Assert.That(request.AnsweringPlayerId, Is.EqualTo((int)Read(f.Gameplay, "localPlayerId")));
            if (request.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId)
            {
                yield return Wait(() => Active("EffectDialogOptionRowView").Any(), "原继续／结束页面未显示");
                var target = HotseatUiInputDriver.Marker("action.character." + id);
                var button = target.GetComponent<Button>(); Assert.That(button, Is.Not.Null);
                remember?.Invoke(button.onClick);
                var beforeCount = observed.Count;
                yield return f.Capture("原继续结束页_" + id, target.GetComponentInParent(T("EffectDialogShellView")));
                yield return f.Click(target, "原继续结束页回答 " + id);
                Assert.That(observed.Count, Is.EqualTo(beforeCount + 1));
                Assert.That(observed.Last().Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
                Assert.That(observed.Last().OptionIds, Is.EqualTo(new[] { id }));
                Assert.That(observed.Last().Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo(request.GetStableInteractionId()));
                yield break;
            }
            if (request.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId)
            {
                Component viewer = null;
                yield return Wait(() => (viewer = Active("CardViewer").SingleOrDefault(v => Read(v, "Mode").ToString() == "UseCharacter")) != null,
                    "首模式必须保留实际 CardViewer 卡面和策略／计谋按钮");
                var use = f.State.CharacterUses.Single(u => !u.Finished && u.ExecutingPlayerId == request.AnsweringPlayerId);
                var definition = f.State.ItemZones.Items.Single(i => i.ItemId == use.CardInstanceId).DefinitionId;
                CheckViewer(viewer, use.CardInstanceId, definition);
                var button = (Button)Read(viewer, id == CharacterLifecycleService.CancelChoiceId ? "CancelButton" : id == "tactic" ? "PlotButton" : "StrategyButton");
                remember?.Invoke(button.onClick);
                var beforeCount = observed.Count;
                yield return f.Capture("原角色卡面_权威候选_" + id, viewer);
                yield return f.Click(button.gameObject, "卡面回答当前角色使用 " + id);
                Assert.That(observed.Count, Is.EqualTo(beforeCount + 1));
                var accepted = observed.Last();
                Assert.That(accepted.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
                Assert.That(accepted.OptionIds, Is.EqualTo(new[] { id }));
                Assert.That(accepted.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo(request.GetStableInteractionId()));
                yield break;
            }
            var index = request.CandidateIds.IndexOf(id);
            Component row = null;
            yield return Wait(() => (row = Active("EffectDialogOptionRowView").SingleOrDefault(r => r.name == "Character Effect Option " + index)) != null,
                "角色权威候选未绑定实际行：" + id);
            var count = observed.Count;
            yield return f.Click(ButtonObject(row, "Button"), "选择角色候选 " + id);
            Assert.That(observed.Count, Is.EqualTo(count), "本地候选选择不应提前提交");
            var selected = (IEnumerable)Read(Read(f.Gameplay, "characterAbilityInteraction"), "selectedCandidates");
            Assert.That(selected.Cast<string>(), Is.EquivalentTo(new[] { id }), "页面候选顺序与协议映射不一致");
            // 选择触发自然刷新，重新解析当前壳和固定确认槽。
            var shell = Active("EffectDialogShellView").Single(s => s.GetComponentsInChildren<Button>()
                .Any(b => b.name == "Confirm Selection" || b.transform.parent.name == "Confirm Selection"));
            var confirm = shell.GetComponentsInChildren<Button>().Single(b => b.name == "Confirm Selection" || b.transform.parent.name == "Confirm Selection");
            remember?.Invoke(confirm.onClick);
            yield return f.Capture("角色权威候选_" + id, shell);
            yield return f.Click(confirm.gameObject, "确认角色候选 " + id);
            Assert.That(observed.Count, Is.EqualTo(count + 1));
            var command = observed.Last();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { id }));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo(request.GetStableInteractionId()));
        }

        private void CheckViewer(Component viewer, string id, string definition)
        {
            Assert.That(Read(viewer, "Mode").ToString(), Is.EqualTo("UseCharacter"));
            Assert.That(Read(viewer, "CardId"), Is.EqualTo(id));
            Assert.That(((Button)Read(viewer, "PlotButton")).IsInteractable(), Is.True);
            Assert.That(((Button)Read(viewer, "StrategyButton")).IsInteractable(), Is.True);
            var sprite = Call(Read(f.Registry, "CardVisualCatalog"), "GetCharacterFront", definition);
            Assert.That(sprite, Is.Not.Null); Assert.That(Read(viewer, "DisplayedSprite"), Is.SameAs(sprite));
        }

        private static void CheckRuleFacts(GameState before, GameState after, string definition, string mode, int player, EffectNodeRuntimeState[] nodes)
        {
            var flow = definition == "mountain" ? (mode == "strategy" ? "effect.map.clear_route_road" : "effect.enterprise.upgrade_then_activate") :
                (mode == "strategy" ? "effect.enterprise.upgrade_all_players" : LuaDomainEffectTypeIds.UpgradeEnterprise);
            Assert.That(nodes.Any(n => n.EffectTypeId == flow && n.Status == EffectNodeStatus.Completed), Is.True, "角色专属正式事实链未完成：" + flow);
            if (definition == "mountain" && mode == "strategy")
            {
                Assert.That(nodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.PlaceRoad && n.Status == EffectNodeStatus.Completed), Is.True);
                Assert.That(after.FindPlayer(player).Score, Is.GreaterThanOrEqualTo(before.FindPlayer(player).Score + 1));
            }
            if (definition == "mlynar" && mode == "tactic")
            {
                foreach (var resource in new[] { nameof(ResourceType.Originium), nameof(ResourceType.OriginiumShard), nameof(ResourceType.Iron) })
                    Assert.That(nodes.Any(n => n.Status == EffectNodeStatus.Completed && Text(n.NormalizedArguments, "resourceType") == resource &&
                        Number(n.NormalizedArguments, "amount") == 1 && n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.True, "缺少真实资源结算：" + resource);
            }
            if (definition == "mountain" && mode == "tactic")
                Assert.That(nodes.Any(n => n.Status == EffectNodeStatus.Completed && n.EffectTypeId == ResourceEffectTypeIds.Pay &&
                    Text(n.NormalizedArguments, "resourceType") == nameof(ResourceType.GoldVoucher) && Number(n.NormalizedArguments, "amount") == 2), Is.True);
            if (definition == "mlynar" || mode == "tactic")
            {
                var query = new EnterpriseStateQuery();
                var companies = before.ItemZones.Items.Where(i => before.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.enterprise_display")).ToArray();
                var players = definition == "mlynar" && mode == "strategy" ? before.Players.Select(p => p.PlayerId).ToArray() : new[] { player };
                Assert.That(companies.Any(c => players.All(id => query.GetLevel(after, id, c.ItemId) >= query.GetLevel(before, id, c.ItemId) + 1)), Is.True, "未观察到目标玩家真实企业升级");
            }
        }

        private void Save(string name, object data)
        {
            Directory.CreateDirectory(f.DirectoryPath);
            File.WriteAllText(Path.Combine(f.DirectoryPath, name + ".json"), JsonConvert.SerializeObject(data, Formatting.Indented));
        }
        private void ObserveCommands()
        {
            observed.Clear(); handlers = (IList<IGameCommandHandler>)Read(f.Session, "commandHandlers"); originalHandlers = handlers.ToArray();
            for (var i = 0; i < handlers.Count; i++) handlers[i] = new Observer(originalHandlers[i], (command, result) =>
            {
                observed.Add(JsonConvert.DeserializeObject<GameCommand>(JsonConvert.SerializeObject(command)));
                f.Record("原处理器透传命令结果", new { command, result.Succeeded, reason = result.Validation?.Reason,
                    note = "测试观察器原样调用原Handler，不改命令、结果或规则State；会话提交后的事实另由最终状态核对" });
            });
        }
        private sealed class Observer : IGameCommandHandler
        {
            private readonly IGameCommandHandler inner;
            private readonly Action<GameCommand, CommandResult> observe;
            internal Observer(IGameCommandHandler inner, Action<GameCommand, CommandResult> observe) { this.inner = inner; this.observe = observe; }
            public bool CanHandle(GameCommand command) => inner.CanHandle(command);
            public CommandResult Handle(GameState state, GameCommand command)
            { var result = inner.Handle(state, command); observe(command, result); return result; }
        }
    }
}
