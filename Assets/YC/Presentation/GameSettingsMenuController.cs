using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Sessions;

namespace YC.Presentation
{
    public sealed class GameSettingsMenuController : MonoBehaviour
    {
        private const float PanelHeight = 430f;
        private const float AnimationSpeed = 14f;
        private const string DefaultStartSceneName = "StartScene";

        [SerializeField] private string startSceneName = DefaultStartSceneName;
        [SerializeField] private bool showReturnToStartButton = true;
        [SerializeField] private GameSettingsMenuView view;
        [SerializeField] private MobileCityInteractionController cityInteractionController;
        [SerializeField] private RulebookViewerController rulebookViewer;
        [SerializeField] private ActionLogViewerController actionLogViewer;
        [SerializeField] private ZoomableImageViewerController zoomableImageViewerPrefab;

        private bool initialized;
        private bool isOpen;
        private bool isAnimating;
        private Vector2 targetPosition;

        public bool IsOpen => isOpen;

        private void Awake()
        {
            TryInitialize();
        }

        private void Update()
        {
            if (!initialized)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HandleEscapePressed();
            }

            if (!isAnimating)
            {
                return;
            }

            var panel = view.MenuPanel;
            panel.anchoredPosition = Vector2.Lerp(
                panel.anchoredPosition,
                targetPosition,
                Time.unscaledDeltaTime * AnimationSpeed);

            if (Vector2.Distance(panel.anchoredPosition, targetPosition) > 0.5f)
            {
                return;
            }

            panel.anchoredPosition = targetPosition;
            isAnimating = false;

            if (!isOpen)
            {
                view.OverlayObject.SetActive(false);
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
                Close();
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
            isAnimating = true;
            view.ConfirmationObject.SetActive(false);
            view.OverlayObject.SetActive(true);
            view.MenuPanel.anchoredPosition = new Vector2(0f, GetHiddenPanelY());
            targetPosition = Vector2.zero;
        }

        public void Close()
        {
            if (!TryInitialize())
            {
                return;
            }

            isOpen = false;
            isAnimating = true;
            view.ConfirmationObject.SetActive(false);
            targetPosition = new Vector2(0f, GetHiddenPanelY());
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
            view.ReturnButtonObject.SetActive(showReturnToStartButton);
            view.ActionLogButtonObject.SetActive(false);
            view.ConfirmationObject.SetActive(false);
            view.OverlayObject.SetActive(false);
            view.MenuPanel.anchoredPosition = new Vector2(0f, GetHiddenPanelY());
            initialized = true;
            return true;
        }

        private void BindButtons()
        {
            BindButton(view.GearButton, Open);
            BindButton(view.ActionLogButton, OpenActionLog);
            BindButton(view.OverlayCloseButton, Close);
            BindButton(view.HeaderCloseButton, Close);
            BindButton(view.RulebookButton, OpenRulebook);
            BindButton(view.ReturnButton, ShowConfirmation);
            BindButton(view.ConfirmReturnButton, ReturnToStartScene);
            BindButton(view.CancelReturnButton, HideConfirmation);
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
            GameLaunchContext.ShutdownOnlineSession();
            SceneManager.LoadScene(string.IsNullOrEmpty(startSceneName) ? DefaultStartSceneName : startSceneName);
        }

        private float GetHiddenPanelY()
        {
            var canvas = view == null ? null : view.CanvasTransform;
            var canvasHeight = canvas == null || canvas.rect.height <= 0f
                ? 1080f
                : canvas.rect.height;
            return canvasHeight * 0.5f + PanelHeight * 0.5f + 48f;
        }
    }
}
