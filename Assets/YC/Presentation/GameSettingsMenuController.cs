using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;

namespace YC.Presentation
{
    public sealed class GameSettingsMenuController : MonoBehaviour
    {
        private const string DefaultStartSceneName = "StartScene";
        private const string ResolutionPreferenceKey = "YC.Settings.ResolutionIndex";

        private static readonly ResolutionOption[] ResolutionOptions =
        {
            new ResolutionOption(1280, 720),
            new ResolutionOption(1600, 900),
            new ResolutionOption(1920, 1080),
            ResolutionOption.FullScreen
        };

        [SerializeField] private string startSceneName = DefaultStartSceneName;
        [SerializeField] private bool showReturnToStartButton = true;
        [SerializeField] private GameSettingsMenuView view;
        [SerializeField] private MobileCityInteractionController cityInteractionController;
        [SerializeField] private RulebookViewerController rulebookViewer;
        [SerializeField] private ActionLogViewerController actionLogViewer;
        [SerializeField] private ZoomableImageViewerController zoomableImageViewerPrefab;

        private bool initialized;
        private bool isOpen;

        public bool IsOpen => isOpen;

        private void Awake()
        {
            TryInitialize();
        }

        private void Update()
        {
            if (initialized && Input.GetKeyDown(KeyCode.Escape))
            {
                HandleEscapePressed();
            }
        }

        public void HandleEscapePressed()
        {
            if (!TryInitialize())
            {
                return;
            }

            if (MobileCityInteractionController.WasInteractionEscapeConsumedThisFrame() ||
                CityStyleDeclarationPreviewInputHandler.WasEscapeConsumedThisFrame() ||
                CityStyleDeclarationPreviewInputHandler.HasOpenDialog() ||
                ZoomableImageViewerController.WasEscapeConsumedThisFrame() ||
                ZoomableImageViewerController.HasOpenViewer())
            {
                return;
            }

            if (cityInteractionController != null && cityInteractionController.TryHandleInteractionEscape())
            {
                return;
            }

            if (view.ConfirmationObject.activeSelf)
            {
                HideConfirmation();
                return;
            }

            if (isOpen)
            {
                view.CloseInputHandler.RequestClose();
                return;
            }

            Open();
        }

        public void Open()
        {
            if (!TryInitialize())
            {
                return;
            }

            isOpen = true;
            view.CloseInputHandler.Configure(Close);
            view.ConfirmationObject.SetActive(false);
            view.MenuPanel.anchoredPosition = Vector2.zero;
            view.OverlayObject.SetActive(true);
        }

        public void Close()
        {
            if (!TryInitialize())
            {
                return;
            }

            isOpen = false;
            view.ConfirmationObject.SetActive(false);
            view.MenuPanel.anchoredPosition = Vector2.zero;
            view.OverlayObject.SetActive(false);
        }

        public void SetReturnToStartButtonVisible(bool visible)
        {
            showReturnToStartButton = visible;

            if (!TryInitialize())
            {
                return;
            }

            view.ReturnButtonObject.SetActive(visible);
            if (!visible)
            {
                view.ConfirmationObject.SetActive(false);
            }
        }

        public void ConfigureActionLog(GameSession session)
        {
            if (session == null || !TryInitialize())
            {
                return;
            }

            actionLogViewer.Configure(() => session.State);
            view.ActionLogButtonObject.SetActive(true);
        }

        private bool TryInitialize()
        {
            if (initialized)
            {
                return true;
            }

            if (view == null)
            {
                Debug.LogError(
                    "GameSettingsMenuController 缺少 GameSettingsMenuView 编辑器引用。请使用 " +
                    "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab 装配场景。",
                    this);
                enabled = false;
                return false;
            }

            if (!view.TryValidateConfiguration(out var reason))
            {
                Debug.LogError("GameSettingsMenuView 编辑器装配不完整：" + reason, view);
                enabled = false;
                return false;
            }

            if (rulebookViewer == null || actionLogViewer == null || zoomableImageViewerPrefab == null)
            {
                Debug.LogError("GameSettingsMenuController 缺少显式 Viewer 或 Viewer Prefab 引用。", this);
                enabled = false;
                return false;
            }

            ZoomableImageViewerController.RegisterPrefab(zoomableImageViewerPrefab);
            BindButtons();
            InitializeResolutionDropdown();
            SelectTab(SettingsTab.General);
            view.ReturnButtonObject.SetActive(showReturnToStartButton);
            view.ActionLogButtonObject.SetActive(false);
            view.ConfirmationObject.SetActive(false);
            view.OverlayObject.SetActive(false);
            view.MenuPanel.anchoredPosition = Vector2.zero;
            view.CloseInputHandler.Configure(Close);
            initialized = true;
            return true;
        }

        private void BindButtons()
        {
            BindButton(view.GearButton, Open);
            BindButton(view.ActionLogButton, OpenActionLog);
            view.OverlayCloseButton.onClick.RemoveAllListeners();
            BindButton(view.HeaderCloseButton, view.CloseInputHandler.RequestClose);
            BindButton(view.GeneralTabButton, () => SelectTab(SettingsTab.General));
            BindButton(view.RulebookButton, SelectRulebookTab);
            BindButton(view.PlaceholderTabButton, () => SelectTab(SettingsTab.Placeholder));
            BindButton(view.ReturnButton, ShowConfirmation);
            BindButton(view.ConfirmReturnButton, ReturnToStartScene);
            BindButton(view.CancelReturnButton, HideConfirmation);
        }

        private void InitializeResolutionDropdown()
        {
            var labels = new List<string>(ResolutionOptions.Length);
            for (var index = 0; index < ResolutionOptions.Length; index++)
            {
                labels.Add(ResolutionOptions[index].Label);
            }

            var dropdown = view.ResolutionDropdown;
            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.ClearOptions();
            dropdown.AddOptions(labels);

            var hasSavedPreference = PlayerPrefs.HasKey(ResolutionPreferenceKey);
            var defaultIndex = Screen.fullScreenMode == FullScreenMode.Windowed
                ? FindClosestResolutionIndex(Screen.width, Screen.height)
                : ResolutionOptions.Length - 1;
            var selectedIndex = PlayerPrefs.GetInt(
                ResolutionPreferenceKey,
                defaultIndex);
            selectedIndex = Mathf.Clamp(selectedIndex, 0, ResolutionOptions.Length - 1);
            dropdown.SetValueWithoutNotify(selectedIndex);
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(ApplyResolution);

            if (hasSavedPreference && !UnityEngine.Application.isEditor)
            {
                ApplyResolutionOption(ResolutionOptions[selectedIndex]);
            }
        }

        private void SelectRulebookTab()
        {
            SelectTab(SettingsTab.Rulebook);
            OpenRulebook();
        }

        private void SelectTab(SettingsTab tab)
        {
            view.GeneralContentObject.SetActive(tab == SettingsTab.General);
            view.FutureContentObject.SetActive(tab == SettingsTab.Placeholder);
            view.GeneralTabButton.interactable = tab != SettingsTab.General;
            view.RulebookButton.interactable = tab != SettingsTab.Rulebook;
            view.PlaceholderTabButton.interactable = tab != SettingsTab.Placeholder;
        }

        private static int FindClosestResolutionIndex(int width, int height)
        {
            var closestIndex = 0;
            var closestDistance = int.MaxValue;
            for (var index = 0; index < ResolutionOptions.Length; index++)
            {
                var option = ResolutionOptions[index];
                var distance = Mathf.Abs(option.Width - width) + Mathf.Abs(option.Height - height);
                if (distance >= closestDistance)
                {
                    continue;
                }

                closestIndex = index;
                closestDistance = distance;
            }

            return closestIndex;
        }

        private static void ApplyResolution(int index)
        {
            if (index < 0 || index >= ResolutionOptions.Length)
            {
                return;
            }

            PlayerPrefs.SetInt(ResolutionPreferenceKey, index);
            PlayerPrefs.Save();
            ApplyResolutionOption(ResolutionOptions[index]);
        }

        private static void ApplyResolutionOption(ResolutionOption option)
        {
            if (option.IsFullScreen)
            {
                Screen.SetResolution(
                    Display.main.systemWidth,
                    Display.main.systemHeight,
                    FullScreenMode.FullScreenWindow);
                return;
            }

            Screen.SetResolution(option.Width, option.Height, FullScreenMode.Windowed);
        }

        private static void BindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        private void ShowConfirmation()
        {
            view.ConfirmationObject.SetActive(true);
        }

        private void HideConfirmation()
        {
            view.ConfirmationObject.SetActive(false);
        }

        private void OpenRulebook()
        {
            rulebookViewer.Open();
        }

        private void OpenActionLog()
        {
            actionLogViewer.Open();
        }

        private void ReturnToStartScene()
        {
            GameLaunchContext.ReturnToStartScene(
                string.IsNullOrEmpty(startSceneName) ? DefaultStartSceneName : startSceneName);
        }

        private enum SettingsTab
        {
            General,
            Rulebook,
            Placeholder
        }

        private readonly struct ResolutionOption
        {
            private ResolutionOption(bool isFullScreen)
            {
                Width = 0;
                Height = 0;
                IsFullScreen = isFullScreen;
            }

            public ResolutionOption(int width, int height)
            {
                Width = width;
                Height = height;
                IsFullScreen = false;
            }

            public static ResolutionOption FullScreen => new ResolutionOption(true);
            public int Width { get; }
            public int Height { get; }
            public bool IsFullScreen { get; }
            public string Label => IsFullScreen ? "全屏" : Width + " × " + Height;
        }
    }
}
