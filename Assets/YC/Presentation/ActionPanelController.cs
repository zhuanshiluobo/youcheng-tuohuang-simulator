using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    internal sealed class ActionPanelController
    {
        private const float ActionPanelWidth = 360f;
        private const float ActionPanelHeight = 488f;

        private readonly GameObject panelObject;
        private readonly Text currentPlayerText;
        private readonly Text phaseText;
        private readonly Image localPlayerColorSwatch;
        private readonly Text remainingInfluenceText;
        private readonly Text statusText;
        private readonly Button useCharacterButton;
        private readonly Button declareCityStyleButton;
        private readonly Button deployButton;
        private readonly Button dispatchButton;
        private readonly Button exploreButton;
        private readonly Button moveCityButton;
        private readonly Button buildButton;
        private readonly Button specialActionButton;
        private readonly Button endRoundButton;

        private ActionPanelController(
            GameObject panelObject,
            Text currentPlayerText,
            Text phaseText,
            Image localPlayerColorSwatch,
            Text remainingInfluenceText,
            Text statusText,
            Button useCharacterButton,
            Button declareCityStyleButton,
            Button deployButton,
            Button dispatchButton,
            Button exploreButton,
            Button moveCityButton,
            Button buildButton,
            Button specialActionButton,
            Button endRoundButton)
        {
            this.panelObject = panelObject;
            this.currentPlayerText = currentPlayerText;
            this.phaseText = phaseText;
            this.localPlayerColorSwatch = localPlayerColorSwatch;
            this.remainingInfluenceText = remainingInfluenceText;
            this.statusText = statusText;
            this.useCharacterButton = useCharacterButton;
            this.declareCityStyleButton = declareCityStyleButton;
            this.deployButton = deployButton;
            this.dispatchButton = dispatchButton;
            this.exploreButton = exploreButton;
            this.moveCityButton = moveCityButton;
            this.buildButton = buildButton;
            this.specialActionButton = specialActionButton;
            this.endRoundButton = endRoundButton;
        }

        public bool IsReady
        {
            get { return panelObject != null; }
        }

        public static ActionPanelController Build(
            Canvas uiCanvas,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onBuild,
            Action onSpecialAction,
            Action onEndRound)
        {
            if (uiCanvas == null)
            {
                return null;
            }

            var canvasTransform = uiCanvas.GetComponent<RectTransform>();
            var panelObject = new GameObject("Action Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(canvasTransform, false);

            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.sizeDelta = new Vector2(ActionPanelWidth, ActionPanelHeight);
            panelRect.anchoredPosition = Vector2.zero;

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            var localPlayerColorSwatch = CreateColorSwatch(panelRect, new Vector2(-150f, -58f));
            var remainingInfluenceText = CreateText(panelRect, "× 0", 16, new Vector2(-105f, -58f), FontStyle.Bold);
            remainingInfluenceText.gameObject.name = "Remaining Influence Text";
            remainingInfluenceText.rectTransform.sizeDelta = new Vector2(64f, 30f);
            remainingInfluenceText.alignment = TextAnchor.MiddleLeft;

            var currentPlayerText = CreateText(panelRect, "当前玩家", 20, new Vector2(0f, -26f), FontStyle.Bold);
            var phaseText = CreateText(panelRect, "阶段", 16, new Vector2(0f, -58f), FontStyle.Normal);

            CreateText(panelRect, "快速行动", 16, new Vector2(0f, -98f), FontStyle.Bold);
            var useCharacterButton = CreateButton(panelRect, "使用角色牌", new Vector2(-86f, -134f), onUseCharacter);
            var declareCityStyleButton = CreateButton(panelRect, "宣告样式", new Vector2(86f, -134f), onDeclareCityStyle);

            CreateText(panelRect, "主要行动", 16, new Vector2(0f, -180f), FontStyle.Bold);
            var deployButton = CreateButton(panelRect, "部署", new Vector2(-86f, -216f), onDeploy);
            var dispatchButton = CreateButton(panelRect, "调度", new Vector2(86f, -216f), onDispatch);
            var exploreButton = CreateButton(panelRect, "探索", new Vector2(-86f, -270f), onExplore);
            var moveCityButton = CreateButton(panelRect, "城市移动", new Vector2(86f, -270f), onMoveCity);
            var buildButton = CreateButton(panelRect, "建设", new Vector2(-86f, -324f), onBuild);
            var specialActionButton = CreateButton(panelRect, "特殊行动", new Vector2(86f, -324f), onSpecialAction);
            var endRoundButton = CreateButton(panelRect, "结束本回合", new Vector2(0f, -378f), onEndRound);

            var statusText = CreateText(panelRect, "状态", 15, new Vector2(0f, -438f), FontStyle.Normal);
            statusText.resizeTextForBestFit = true;
            statusText.resizeTextMinSize = 11;
            statusText.resizeTextMaxSize = 15;

            var controller = new ActionPanelController(
                panelObject,
                currentPlayerText,
                phaseText,
                localPlayerColorSwatch,
                remainingInfluenceText,
                statusText,
                useCharacterButton,
                declareCityStyleButton,
                deployButton,
                dispatchButton,
                exploreButton,
                moveCityButton,
                buildButton,
                specialActionButton,
                endRoundButton);
            return controller;
        }

        public void SetHeader(string currentPlayer, string phase)
        {
            currentPlayerText.text = currentPlayer ?? string.Empty;
            phaseText.text = phase ?? string.Empty;
        }

        public void SetLocalPlayerColor(Color color)
        {
            if (localPlayerColorSwatch != null)
            {
                localPlayerColorSwatch.color = color;
            }
        }

        public void SetRemainingInfluence(int amount)
        {
            if (remainingInfluenceText != null)
            {
                remainingInfluenceText.text = "× " + Mathf.Max(0, amount);
            }
        }

        public void SetButtonStates(
            bool canUseCharacter,
            bool canDeclareCityStyle,
            bool canDeploy,
            bool canDispatch,
            bool canExplore,
            bool canMoveCity,
            bool canBuild,
            bool canUseSpecialAction,
            bool canEndRound)
        {
            SetButtonInteractable(useCharacterButton, canUseCharacter);
            SetButtonInteractable(declareCityStyleButton, canDeclareCityStyle);
            SetButtonInteractable(deployButton, canDeploy);
            SetButtonInteractable(dispatchButton, canDispatch);
            SetButtonInteractable(exploreButton, canExplore);
            SetButtonInteractable(moveCityButton, canMoveCity);
            SetButtonInteractable(buildButton, canBuild);
            SetButtonInteractable(specialActionButton, canUseSpecialAction);
            SetButtonInteractable(endRoundButton, canEndRound);
        }

        public void SetStatus(string status)
        {
            statusText.text = status ?? string.Empty;
        }

        private static Text CreateText(RectTransform parent, string value, int size, Vector2 position, FontStyle style)
        {
            var textObject = new GameObject(value + " Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(316f, 30f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = size;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(size);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateButton(
            RectTransform parent,
            string label,
            Vector2 position,
            Action action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(150f, 42f);
            rect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var button = buttonObject.GetComponent<Button>();
            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            var text = labelObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = 16;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(16);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = 16;
            return button;
        }

        private static Image CreateColorSwatch(RectTransform parent, Vector2 position)
        {
            var swatchObject = new GameObject("Local Player Color Swatch", typeof(RectTransform), typeof(Image), typeof(Outline));
            swatchObject.transform.SetParent(parent, false);

            var rect = swatchObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(22f, 22f);
            rect.anchoredPosition = position;

            var image = swatchObject.GetComponent<Image>();
            image.color = Color.white;

            var outline = swatchObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            return image;
        }

        private static void SetButtonInteractable(Button button, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = interactable;
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = interactable ? UiTheme.ButtonBackground : UiTheme.DisabledButtonBackground;
            }
        }
    }
}
