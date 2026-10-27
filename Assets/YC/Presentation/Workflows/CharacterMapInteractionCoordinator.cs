using System;
using System.Collections.Generic;
using System.Linq;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.State;
using YC.Domain.Presentation;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>在地图上收集角色牌的放置、替换、移除与移动目标，不直接修改共享状态。</summary>
    public sealed class CharacterMapInteractionCoordinator
    {
        private enum SelectionKind
        {
            None,
            CharacterEffect
        }

        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly CharacterCardPanelPresenter presenter;
        private readonly Action<IReadOnlyList<WorkflowHighlight>> setHighlights;
        private readonly Action clearHighlights;
        private readonly Action<string, IReadOnlyDictionary<string, string>> submitEffect;
        private readonly Action<string> setPrompt;
        private readonly Dictionary<string, string> draft = new Dictionary<string, string>();
        private readonly List<string> parameterKeys = new List<string>();

        public Func<GameplayTextRef, GameplayTextDocument> ResolveGameplayText { private get; set; }
        public Action<GameplayTextRef, Func<bool>> ShowGameplayPrompt { private get; set; }
        public bool OwnsPromptDocument(GameplayTextDocument value) => false;

        private SelectionKind kind;
        private CharacterCardEffectKind effect;
        private string effectMode = string.Empty;
        private int stepIndex;

        public CharacterMapInteractionCoordinator(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            CharacterCardPanelPresenter presenter,
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            Action clearHighlights,
            Action<string, IReadOnlyDictionary<string, string>> submitEffect,
            Action<IReadOnlyDictionary<string, string>> submitPending,
            Action<string> setPrompt)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            this.setHighlights = setHighlights ?? throw new ArgumentNullException(nameof(setHighlights));
            this.clearHighlights = clearHighlights ?? throw new ArgumentNullException(nameof(clearHighlights));
            this.submitEffect = submitEffect ?? throw new ArgumentNullException(nameof(submitEffect));
            _ = submitPending ?? throw new ArgumentNullException(nameof(submitPending));
            this.setPrompt = setPrompt ?? throw new ArgumentNullException(nameof(setPrompt));
        }

        public bool IsActive
        {
            get { return kind != SelectionKind.None; }
        }

        public string MapConfirmationScope => !IsActive ? string.Empty : kind + ":" + effectMode + ":" + stepIndex + ":" +
            getState()?.Round + ":" + getState()?.Phase + ":" + CurrentPending()?.SourceCommandId;

        public InteractionPresentation BuildMapPresentation()
        {
            var targets = new List<WorkflowHighlight>();
            var key = CurrentKey();
            foreach (var option in CurrentOptions())
                targets.Add(new WorkflowHighlight(key == CharacterEffectParameterKeys.TargetLocationId
                    ? WorkflowHighlightTargetKind.Location : WorkflowHighlightTargetKind.InfluenceSlot,
                    option.Id, SemanticFor(key)));
            return new InteractionPresentation(targets, PromptFor(key),
                InteractionMode.Busy, true);
        }

        public bool TryBeginEffect(string selectedEffectMode, CharacterCardEffectKind selectedEffect)
        {
            var keys = KeysFor(selectedEffect);
            if (keys.Count == 0)
            {
                return false;
            }

            Reset(false);
            kind = SelectionKind.CharacterEffect;
            effect = selectedEffect;
            effectMode = selectedEffectMode ?? string.Empty;
            parameterKeys.AddRange(keys);
            draft[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            stepIndex = 0;
            if (!RenderCurrentStep())
            {
                Reset(true);
                setPrompt("当前角色牌效果没有合法的地图目标。");
            }

            return true;
        }

        public bool TryBeginTinManPendingMove()
        {
            // 旧Pending恢复/命令合同独立拒绝；保留controller接线，不再制造旧地图选择。
            return false;
        }

        public bool Synchronize()
        {
            if (kind == SelectionKind.CharacterEffect)
            {
                var state = getState();
                var view = state == null ? null : presenter.BuildView(state, getLocalPlayerId());
                if (view == null || !view.CanUse)
                {
                    Reset(true);
                    return false;
                }

                RenderCurrentStep();
                return true;
            }

            return false;
        }

        public bool TryHandleInfluenceSlotClicked(string slotId)
        {
            if (!IsActive)
            {
                return false;
            }

            if (CurrentKey() == CharacterEffectParameterKeys.TargetLocationId)
            {
                setPrompt("当前步骤需要点击地图上高亮的资源点。");
                return true;
            }

            Select(slotId);
            return true;
        }

        public bool TryHandleLocationClicked(string locationId)
        {
            if (!IsActive)
            {
                return false;
            }

            if (CurrentKey() != CharacterEffectParameterKeys.TargetLocationId)
            {
                setPrompt("当前步骤需要点击地图上高亮的影响力槽位。");
                return true;
            }

            Select(locationId);
            return true;
        }

        public void Cancel()
        {
            Reset(true);
        }

        private void Select(string optionId)
        {
            var key = CurrentKey();
            if (string.IsNullOrEmpty(key) || !ContainsCurrentOption(optionId))
            {
                setPrompt("请选择地图上高亮的合法目标。");
                return;
            }

            draft[key] = optionId ?? string.Empty;
            stepIndex += 1;
            if (stepIndex < parameterKeys.Count)
            {
                if (!RenderCurrentStep())
                {
                    Reset(true);
                    setPrompt("后续步骤没有合法目标，请重新选择该角色牌效果。");
                }
                return;
            }

            CompleteSelection();
        }

        private void CompleteSelection()
        {
            var completedMode = effectMode;
            var completedDraft = new Dictionary<string, string>(draft);
            Reset(true);
            submitEffect(completedMode, completedDraft);
        }

        private bool RenderCurrentStep()
        {
            var options = CurrentOptions();
            if (options.Count == 0)
            {
                clearHighlights();
                return false;
            }

            var key = CurrentKey();
            var targetKind = key == CharacterEffectParameterKeys.TargetLocationId
                ? WorkflowHighlightTargetKind.Location
                : WorkflowHighlightTargetKind.InfluenceSlot;
            var semantic = SemanticFor(key);
            var highlights = new List<WorkflowHighlight>();
            for (var i = 0; i < options.Count; i++)
            {
                highlights.Add(new WorkflowHighlight(targetKind, options[i].Id, semantic));
            }

            setHighlights(highlights);
            setPrompt(PromptFor(key));
            return true;
        }

        private bool ContainsCurrentOption(string optionId)
        {
            var options = CurrentOptions();
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Id == optionId)
                {
                    return true;
                }
            }

            return false;
        }

        private IReadOnlyList<CharacterCardOption> CurrentOptions()
        {
            var state = getState();
            if (state == null)
            {
                return new List<CharacterCardOption>().AsReadOnly();
            }

            var key = CurrentKey();
            var result = presenter.QueryOptions(state, getLocalPlayerId(), effect, draft);
            var parentKey = ParentKeyFor(key);
            string parentId;
            draft.TryGetValue(parentKey, out parentId);
            return string.IsNullOrEmpty(parentKey)
                ? result.Get(key)
                : result.Get(key, parentId);
        }

        private PendingCharacterEffectState CurrentPending()
        {
            var state = getState();
            var pending = state == null ? null : state.PendingCharacterEffect;
            return pending != null && pending.IsValid() && pending.PlayerId == getLocalPlayerId()
                ? pending
                : null;
        }

        private string CurrentKey()
        {
            return stepIndex >= 0 && stepIndex < parameterKeys.Count
                ? parameterKeys[stepIndex]
                : string.Empty;
        }

        private void Reset(bool clearMapHighlights)
        {
            kind = SelectionKind.None;
            effect = CharacterCardEffectKind.Unsupported;
            effectMode = string.Empty;
            stepIndex = 0;
            draft.Clear();
            parameterKeys.Clear();
            if (clearMapHighlights)
            {
                clearHighlights();
            }
        }

        private static List<string> KeysFor(CharacterCardEffectKind selectedEffect)
        {
            switch (selectedEffect)
            {
                case CharacterCardEffectKind.LiskarmSecurityProtocol:
                    return new List<string>
                    {
                        CharacterEffectParameterKeys.PlacementSlotId1,
                        CharacterEffectParameterKeys.PlacementSlotId2
                    };
                case CharacterCardEffectKind.LiskarmControlPosition:
                    return new List<string> { CharacterEffectParameterKeys.TargetInfluenceSlotId };
                case CharacterCardEffectKind.ElysiumNavigation:
                    return new List<string> { CharacterEffectParameterKeys.TargetLocationId };
                case CharacterCardEffectKind.TexasRemoveAndDoubleMove:
                    return new List<string>
                    {
                        CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId,
                        CharacterEffectParameterKeys.MoveSourceSlotId1,
                        CharacterEffectParameterKeys.MoveTargetSlotId1,
                        CharacterEffectParameterKeys.MoveSourceSlotId2,
                        CharacterEffectParameterKeys.MoveTargetSlotId2
                    };
                default:
                    return new List<string>();
            }
        }

        private static string ParentKeyFor(string key)
        {
            if (key == CharacterEffectParameterKeys.MoveTargetSlotId1)
            {
                return CharacterEffectParameterKeys.MoveSourceSlotId1;
            }
            if (key == CharacterEffectParameterKeys.MoveTargetSlotId2)
            {
                return CharacterEffectParameterKeys.MoveSourceSlotId2;
            }
            if (key == CharacterEffectParameterKeys.TargetInfluenceSlotId)
            {
                return CharacterEffectParameterKeys.SourceInfluenceSlotId;
            }
            return string.Empty;
        }

        private static WorkflowHighlightSemantic SemanticFor(string key)
        {
            if (key == CharacterEffectParameterKeys.PlacementSlotId1 ||
                key == CharacterEffectParameterKeys.PlacementSlotId2)
            {
                return WorkflowHighlightSemantic.DeployTarget;
            }
            if (key == CharacterEffectParameterKeys.MoveSourceSlotId1 ||
                key == CharacterEffectParameterKeys.MoveSourceSlotId2 ||
                key == CharacterEffectParameterKeys.SourceInfluenceSlotId)
            {
                return WorkflowHighlightSemantic.DispatchSource;
            }
            if (key == CharacterEffectParameterKeys.MoveTargetSlotId1 ||
                key == CharacterEffectParameterKeys.MoveTargetSlotId2)
            {
                return WorkflowHighlightSemantic.DispatchTarget;
            }
            if (key == CharacterEffectParameterKeys.TargetLocationId)
            {
                return WorkflowHighlightSemantic.MoveTarget;
            }
            return WorkflowHighlightSemantic.EventInfluenceTarget;
        }

        public Func<bool> CapturePendingPromptCurrentness(bool includeStep = true)
        {
            // 旧Pending文档不再由本协调器拥有；调用方接线待所属作者独立收口。
            return () => false;
        }

        private string PromptFor(string key)
        {
            if (key == CharacterEffectParameterKeys.PlacementSlotId1)
            {
                return "雷蛇策略：在地图上选择第一个放置影响力的空格。";
            }
            if (key == CharacterEffectParameterKeys.PlacementSlotId2)
            {
                return "雷蛇策略：在地图上选择第二个放置影响力的空格。";
            }
            if (key == CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId)
            {
                return "请在地图上选择要移除的影响力。";
            }
            if (key == CharacterEffectParameterKeys.TargetInfluenceSlotId && kind == SelectionKind.CharacterEffect)
            {
                return "雷蛇计谋：点击地图上高亮的对手影响力进行替换。";
            }
            if (key == CharacterEffectParameterKeys.MoveSourceSlotId1 ||
                key == CharacterEffectParameterKeys.MoveSourceSlotId2 ||
                key == CharacterEffectParameterKeys.SourceInfluenceSlotId)
            {
                return "请点击地图上高亮的己方影响力作为移动来源。";
            }
            if (key == CharacterEffectParameterKeys.MoveTargetSlotId1 ||
                key == CharacterEffectParameterKeys.MoveTargetSlotId2 ||
                key == CharacterEffectParameterKeys.TargetInfluenceSlotId)
            {
                return "请点击地图上高亮的空格作为影响力移动目标。";
            }
            return "请点击地图上高亮的资源点。";
        }
    }
}
