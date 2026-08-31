using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class LoadingSceneController : MonoBehaviour
    {
        private const string FallbackSceneName = "StartScene";
        private const float MaximumFadeFrameStep = 1f / 30f;
        private const float QuickBlackFadeDuration = 0.1f;
        private const float QuickBlackRevealDuration = 0.1f;

        [SerializeField] private Camera loadingCamera;
        [SerializeField] private GameObject loadingModel;
        [SerializeField] private GameObject loadingModelDisplay;
        [SerializeField] private CanvasGroup blackScreen;
        [SerializeField] private GameObject progressRoot;
        [SerializeField] private CanvasGroup progressGroup;
        [SerializeField] private RectTransform progressContainer;
        [SerializeField] private Text progressText;
        [SerializeField] private Text tipsText;
        [SerializeField] private CanvasGroup tipsGroup;
        [SerializeField] private RectTransform progressFill;
        [SerializeField, Min(1f)] private float progressFillWidth = 520f;
        [SerializeField, Min(0.05f)] private float fadeToBlackDuration = 0.8f;
        [SerializeField, Min(0.05f)] private float revealDuration = 1.04f;
        [SerializeField, Min(0.05f)] private float progressAppearanceDuration = 0.35f;
        [SerializeField, Min(0.1f)] private float tipDisplayDuration = 2.8f;
        [SerializeField, Min(0.05f)] private float tipFadeDuration = 0.2f;
        [SerializeField] private Vector2 progressHiddenOffset = new Vector2(0f, -18f);
        [SerializeField] private string[] loadingTips =
        {
            "拓荒提示：合理规划路线，可以更快连接城市。",
            "拓荒提示：留意资源储备，为下一回合做好准备。",
            "拓荒提示：地图加载完成后即可开始新的旅程。"
        };

        private Vector2 progressShownPosition;
        private Coroutine tipLoop;

        public CanvasGroup BlackScreen => blackScreen;
        public Camera LoadingCamera => loadingCamera;
        public GameObject LoadingModel => loadingModel;
        public GameObject LoadingModelDisplay => loadingModelDisplay;
        public Text ProgressText => progressText;
        public Text TipsText => tipsText;
        public CanvasGroup ProgressGroup => progressGroup;
        public RectTransform ProgressContainer => progressContainer;
        public RectTransform ProgressFill => progressFill;
        public float RevealDuration => revealDuration;

        private void Awake()
        {
            if (loadingCamera == null || loadingModel == null || loadingModelDisplay == null ||
                blackScreen == null ||
                progressRoot == null || progressGroup == null ||
                progressContainer == null || progressText == null || tipsText == null ||
                tipsGroup == null || progressFill == null)
            {
                Debug.LogError("LoadingSceneController 的遮罩或进度引用不完整。", this);
                enabled = false;
                return;
            }

            blackScreen.alpha = 0f;
            blackScreen.blocksRaycasts = true;
            blackScreen.interactable = true;
            progressShownPosition = progressContainer.anchoredPosition;
            progressGroup.alpha = 0f;
            tipsGroup.alpha = 1f;
            progressRoot.SetActive(false);
            SetProgress(0f);
        }

        private IEnumerator Start()
        {
            var targetScene = SceneTransitionContext.ConsumeTargetScene();
            var showProgress = SceneTransitionContext.ConsumeShowProgress();
            var quickBlackTransition = SceneTransitionContext.ConsumeQuickBlackTransition();
            if (quickBlackTransition)
            {
                PrepareQuickBlackTransition();
            }
            if (string.IsNullOrEmpty(targetScene))
            {
                targetScene = FallbackSceneName;
            }

            yield return FadeBlackScreen(
                0f,
                1f,
                quickBlackTransition ? QuickBlackFadeDuration : fadeToBlackDuration);
            if (showProgress)
            {
                yield return AnimateProgressPanel(true);
            }
            yield return UnloadSourceScenes();

            AsyncOperation operation;
            try
            {
                operation = SceneManager.LoadSceneAsync(targetScene, LoadSceneMode.Additive);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("加载目标场景失败：" + ex.Message, this);
                targetScene = FallbackSceneName;
                operation = SceneManager.LoadSceneAsync(targetScene, LoadSceneMode.Additive);
            }

            if (operation == null)
            {
                Debug.LogError("Unity 未能创建场景加载任务。", this);
                yield break;
            }

            operation.allowSceneActivation = false;
            var displayedProgress = 0f;
            while (operation.progress < 0.9f)
            {
                if (showProgress)
                {
                    var actualProgress = Mathf.Clamp01(operation.progress / 0.9f);
                    displayedProgress = Mathf.MoveTowards(
                        displayedProgress,
                        actualProgress,
                        Time.unscaledDeltaTime * 0.65f);
                    SetProgress(displayedProgress);
                }
                yield return null;
            }

            while (showProgress && displayedProgress < 1f)
            {
                displayedProgress = Mathf.MoveTowards(displayedProgress, 1f, Time.unscaledDeltaTime * 1.5f);
                SetProgress(displayedProgress);
                yield return null;
            }

            if (showProgress)
            {
                SetProgress(1f);
                yield return new WaitForSecondsRealtime(0.18f);
                yield return AnimateProgressPanel(false);
            }
            operation.allowSceneActivation = true;
            while (!operation.isDone)
            {
                yield return null;
            }

            var loadedTargetScene = SceneManager.GetSceneByName(targetScene);
            if (!loadedTargetScene.IsValid() || !loadedTargetScene.isLoaded)
            {
                Debug.LogError("目标场景激活后不可用：" + targetScene, this);
                SceneTransitionContext.Clear();
                yield break;
            }

            SceneManager.SetActiveScene(loadedTargetScene);

            if (quickBlackTransition)
            {
                loadingCamera.enabled = false;
                blackScreen.alpha = 1f;
                Canvas.ForceUpdateCanvases();
                yield return null;
                yield return FadeBlackScreen(1f, 0f, QuickBlackRevealDuration);
                blackScreen.blocksRaycasts = false;
                blackScreen.interactable = false;
                SceneTransitionContext.CompleteTransition();
                SceneManager.UnloadSceneAsync(gameObject.scene);
                yield break;
            }

            // 场景激活帧可能因初始化耗时而产生很大的 unscaledDeltaTime。
            // 先让新场景在纯黑遮罩下完整渲染两帧，再让黑幕和模型一起淡出。
            // 模型不能在渐亮前关闭，否则会比背景提前消失，产生明显跳变。
            blackScreen.alpha = 1f;
            Canvas.ForceUpdateCanvases();
            yield return null;
            yield return null;

            var elapsed = 0f;
            while (elapsed < revealDuration)
            {
                elapsed += GetFadeDeltaTime();
                var progress = Mathf.Clamp01(elapsed / revealDuration);
                blackScreen.alpha = 1f - Mathf.SmoothStep(0f, 1f, progress);
                yield return null;
            }

            blackScreen.alpha = 0f;
            loadingCamera.enabled = false;
            loadingModel.SetActive(false);
            blackScreen.blocksRaycasts = false;
            blackScreen.interactable = false;
            SceneTransitionContext.CompleteTransition();
            SceneManager.UnloadSceneAsync(gameObject.scene);
        }

        private void PrepareQuickBlackTransition()
        {
            loadingModel.SetActive(false);
            loadingModelDisplay.SetActive(false);
            loadingCamera.targetTexture = null;
            loadingCamera.cullingMask = 0;
            loadingCamera.clearFlags = CameraClearFlags.SolidColor;
            loadingCamera.backgroundColor = Color.black;
        }

        private IEnumerator AnimateProgressPanel(bool show)
        {
            if (show)
            {
                progressRoot.SetActive(true);
                progressContainer.anchoredPosition = progressShownPosition + progressHiddenOffset;
                progressGroup.alpha = 0f;
                StartTipLoop();
            }
            else
            {
                StopTipLoop();
            }

            var fromAlpha = progressGroup.alpha;
            var toAlpha = show ? 1f : 0f;
            var fromPosition = progressContainer.anchoredPosition;
            var toPosition = show
                ? progressShownPosition
                : progressShownPosition + progressHiddenOffset;
            var elapsed = 0f;
            while (elapsed < progressAppearanceDuration)
            {
                elapsed += GetFadeDeltaTime();
                var normalized = Mathf.Clamp01(elapsed / progressAppearanceDuration);
                var eased = Mathf.SmoothStep(0f, 1f, normalized);
                progressGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, eased);
                progressContainer.anchoredPosition = Vector2.Lerp(fromPosition, toPosition, eased);
                yield return null;
            }

            progressGroup.alpha = toAlpha;
            progressContainer.anchoredPosition = toPosition;
            if (!show)
            {
                progressRoot.SetActive(false);
            }
        }

        private void StartTipLoop()
        {
            StopTipLoop();
            tipLoop = StartCoroutine(CycleTips());
        }

        private void StopTipLoop()
        {
            if (tipLoop != null)
            {
                StopCoroutine(tipLoop);
                tipLoop = null;
            }
        }

        private IEnumerator CycleTips()
        {
            var index = 0;
            while (progressRoot.activeInHierarchy)
            {
                if (loadingTips == null || loadingTips.Length == 0)
                {
                    tipsText.text = "正在准备新的拓荒旅程…";
                }
                else
                {
                    tipsText.text = loadingTips[index % loadingTips.Length];
                    index++;
                }

                tipsGroup.alpha = 1f;
                yield return new WaitForSecondsRealtime(tipDisplayDuration);
                yield return FadeTip(1f, 0f);
                yield return null;
                yield return FadeTip(0f, 1f);
            }
        }

        private IEnumerator FadeTip(float from, float to)
        {
            var elapsed = 0f;
            while (elapsed < tipFadeDuration)
            {
                elapsed += GetFadeDeltaTime();
                var normalized = Mathf.Clamp01(elapsed / tipFadeDuration);
                tipsGroup.alpha = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, normalized));
                yield return null;
            }

            tipsGroup.alpha = to;
        }

        private IEnumerator UnloadSourceScenes()
        {
            var transitionScene = gameObject.scene;
            var scenesToUnload = new List<Scene>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene != transitionScene && scene.isLoaded)
                {
                    scenesToUnload.Add(scene);
                }
            }

            foreach (var scene in scenesToUnload)
            {
                var unloadOperation = SceneManager.UnloadSceneAsync(scene);
                if (unloadOperation == null)
                {
                    continue;
                }

                while (!unloadOperation.isDone)
                {
                    yield return null;
                }
            }
        }

        private IEnumerator FadeBlackScreen(float from, float to, float duration)
        {
            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += GetFadeDeltaTime();
                var progress = Mathf.Clamp01(elapsed / duration);
                blackScreen.alpha = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, progress));
                yield return null;
            }

            blackScreen.alpha = to;
        }

        private static float GetFadeDeltaTime()
        {
            return Mathf.Min(Time.unscaledDeltaTime, MaximumFadeFrameStep);
        }

        public void SetProgress(float normalizedProgress)
        {
            var progress = Mathf.Clamp01(normalizedProgress);
            progressText.text = "加载中 " + Mathf.RoundToInt(progress * 100f) + "%";
            progressFill.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, progressFillWidth * progress);
        }

        public void SetLoadingTips(string[] tips)
        {
            loadingTips = tips;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Camera transitionCamera,
            GameObject model,
            GameObject modelDisplay,
            CanvasGroup group,
            GameObject progressContainer,
            CanvasGroup progressCanvasGroup,
            RectTransform progressRect,
            Text percentage,
            Text tipLabel,
            CanvasGroup tipCanvasGroup,
            RectTransform fill,
            float fillWidth,
            float fadeOutDuration,
            float duration)
        {
            loadingCamera = transitionCamera;
            loadingModel = model;
            loadingModelDisplay = modelDisplay;
            blackScreen = group;
            progressRoot = progressContainer;
            progressGroup = progressCanvasGroup;
            this.progressContainer = progressRect;
            progressText = percentage;
            tipsText = tipLabel;
            tipsGroup = tipCanvasGroup;
            progressFill = fill;
            progressFillWidth = fillWidth;
            fadeToBlackDuration = fadeOutDuration;
            revealDuration = duration;
        }
#endif
    }
}
