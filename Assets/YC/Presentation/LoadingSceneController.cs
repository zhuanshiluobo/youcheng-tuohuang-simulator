using System.Collections;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
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
        [SerializeField] private Button returnToMenuButton;
        [SerializeField, Min(1f)] private float offerReturnAfterSeconds = 30f;
        [SerializeField] private string percentageFormat = "{0}%";
        [SerializeField] private string preparingStatus = "准备中";
        [SerializeField] private string restoringStatus = "恢复中";
        [SerializeField] private string waitingStatus = "等待中";
        [SerializeField] private string failureStatus = "未完成";
        [SerializeField] private string sceneStageText = "正在加载场景资源…";
        [SerializeField] private string hostInitializationText = "房主正在初始化对局…";
        [SerializeField] private string localInitializationText = "正在初始化对局…";
        [SerializeField] private string clientInitializationText = "正在准备客户端界面…";
        [SerializeField] private string restoreStageText = "正在恢复存档和结算状态…";
        [SerializeField] private string preparingSynchronizationText = "正在准备联机同步数据…";
        [SerializeField] private string awaitingHostText = "正在等待房主准备对局数据…";
        [SerializeField] private string receivingFormat = "正在同步并恢复对局数据：{0:0.00} / {1:0.00} MB";
        [SerializeField] private string hostWaitingText = "正在等待玩家接收并恢复对局数据…";
        [SerializeField] private string playerCountFormat = "{0}/{1} 人";
        [SerializeField] private string confirmingText = "正在确认所有玩家的对局状态…";
        [SerializeField] private string readyText = "对局已就绪，正在进入…";
        [SerializeField] private string failedText = "加载未完成，请返回菜单后重试。";
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
        private bool hadGameplaySource;
        private bool sourceScenesUnloaded;
        private static LoadingSceneController active;
        private GameplayLoadingProgress gameplayProgress;
        private MobileCityInteractionController gameplayController;
        private string gameplaySceneName;
        private string gameplayGameId;
        private bool aborted;
        private bool activationComplete;

        public static void TrackGameplayStartup(MobileCityInteractionController controller)
        {
            if (active != null && active.gameplayProgress != null && controller.gameObject.scene.name == active.gameplaySceneName)
                active.gameplayController = controller;
        }

        public static bool DeferGameplaySynchronization(MobileCityInteractionController controller)
        {
            if (active == null || active.gameplayController != controller || active.gameplayProgress == null) return false;
            active.gameplayProgress.MarkSessionPrepared();
            active.RenderGameplayProgress();
            return active.gameplayProgress.Online;
        }

        public static bool IsBlockingGameplay(Scene scene) => active != null && active.gameplayProgress != null &&
            active.gameplaySceneName == scene.name;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForRuntimeStart() { active = null; }

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
            active = this;
            if (returnToMenuButton != null)
            {
                returnToMenuButton.gameObject.SetActive(false);
                returnToMenuButton.onClick.AddListener(ReturnToMenu);
            }
        }

        private IEnumerator Start()
        {
            var targetScene = SceneTransitionContext.ConsumeTargetScene();
            var releaseGameplayArtwork = targetScene == FallbackSceneName &&
                GameLaunchContext.Instance != null && GameLaunchContext.Instance.IsLeavingGameplay;
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
            if (showProgress && !quickBlackTransition && (targetScene == "SampleScene" || targetScene == "ThreePlayerScene"))
            {
                var launch = GameLaunchContext.Instance;
                gameplaySceneName = targetScene;
                gameplayGameId = launch?.GameId;
                gameplayProgress = new GameplayLoadingProgress(launch != null && launch.Mode != LaunchMode.Local,
                    launch?.Mode == LaunchMode.Host, launch?.RestoreData != null || launch?.ResumeInfo != null);
                RenderGameplayProgress();
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
                releaseGameplayArtwork = false;
                gameplayProgress = null;
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
                    SetSceneProgress(displayedProgress);
                }
                yield return null;
            }

            while (showProgress && displayedProgress < 1f)
            {
                displayedProgress = Mathf.MoveTowards(displayedProgress, 1f, Time.unscaledDeltaTime * 1.5f);
                SetSceneProgress(displayedProgress);
                yield return null;
            }

            if (gameplayProgress != null)
            {
                gameplayProgress.BeginInitialization();
                RenderGameplayProgress();
                // 激活场景会同步执行 Awake；先绘制阶段说明，耗时步骤不伪造逐帧百分比。
                yield return null;
                yield return null;
            }
            else if (showProgress)
            {
                SetProgress(1f);
                yield return new WaitForSecondsRealtime(0.18f);
                yield return AnimateProgressPanel(false);
            }
            // 旧页已卸载、目标尚未激活；先让动态对象的Destroy在本帧结束生效，再允许新菜单取图。
            if (TryReleaseGameplayArtworkBeforeActivation(targetScene, releaseGameplayArtwork, operation))
                yield return null;
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
            activationComplete = true;
            if (gameplayProgress != null)
            {
                var waitingSince = Time.realtimeSinceStartup;
                while (gameplayProgress.Stage != GameplayLoadingStage.Ready && !aborted)
                {
                    ObserveGameplayReadiness();
                    RenderGameplayProgress();
                    if (returnToMenuButton != null)
                        returnToMenuButton.gameObject.SetActive(gameplayProgress.Stage == GameplayLoadingStage.Failed ||
                            Time.realtimeSinceStartup - waitingSince >= offerReturnAfterSeconds);
                    yield return null;
                }
                if (aborted) yield break;
                if (returnToMenuButton != null) returnToMenuButton.gameObject.SetActive(false);
                yield return new WaitForSecondsRealtime(0.18f);
                yield return AnimateProgressPanel(false);
            }

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
                if (gameplayProgress == null) StartTipLoop();
                else RenderGameplayProgress();
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
            hadGameplaySource = false;
            sourceScenesUnloaded = false;
            var transitionScene = gameObject.scene;
            var scenesToUnload = new List<Scene>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene != transitionScene && scene.isLoaded)
                {
                    scenesToUnload.Add(scene);
                    if (scene.name == "SampleScene" || scene.name == "ThreePlayerScene") hadGameplaySource = true;
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
            foreach (var scene in scenesToUnload)
                if (scene.IsValid() && scene.isLoaded) yield break;
            sourceScenesUnloaded = true;
        }

        private bool TryReleaseGameplayArtworkBeforeActivation(string targetScene, bool requestedMenuReturn, AsyncOperation operation)
        {
            if (!requestedMenuReturn || targetScene != FallbackSceneName || !hadGameplaySource || !sourceScenesUnloaded ||
                GameLaunchContext.Instance == null || !GameLaunchContext.Instance.IsLeavingGameplay ||
                operation == null || operation.allowSceneActivation || operation.isDone || operation.progress < 0.9f) return false;
            // 包括意外并行加载的场景；不扫描对象或猜测仍显示的页面是否使用卡图。
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene != gameObject.scene && scene.isLoaded) return false;
            }
            return ExternalContentRuntime.ReleaseUnloadedGameplayArtwork();
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
            progressText.text = string.Format(percentageFormat, Mathf.RoundToInt(progress * 100f));
            progressFill.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, progressFillWidth * progress);
        }

        private void SetSceneProgress(float value)
        {
            if (gameplayProgress == null) { SetProgress(value); return; }
            gameplayProgress.SetSceneProgress(value);
            RenderGameplayProgress();
        }

        private void ObserveGameplayReadiness()
        {
            var launch = GameLaunchContext.Instance;
            if (gameplayController == null || !string.IsNullOrEmpty(gameplayController.StartupFailure) ||
                launch?.IsLeavingGameplay == true || gameplayProgress.Online && launch?.GameId != gameplayGameId)
            { gameplayProgress.Fail(); return; }
            if (!gameplayProgress.Online)
            { gameplayProgress.CompleteLocal(gameplayController.IsStartupInitialized); return; }
            var transport = MirrorCommandTransport.Instance;
            if (transport == null || transport.InitialLoadingGameId != gameplayGameId) return;
            SimulationPublishedSnapshot snapshot = null;
            transport.SimulationCoordinator?.TryReadPublishedSnapshot(out snapshot);
            bool recoveringConnection = transport.IsAwaitingLocalSimulationConnectionRecovery;
            bool failed = !string.IsNullOrEmpty(transport.InitialLoadingFailure) ||
                !recoveringConnection && snapshot != null && (snapshot.IsStopped || snapshot.RequiresRecovery);
            bool boundaryReady = !recoveringConnection && GameplayLoadingProgress.HasConfirmedSimulationState(snapshot, gameplayGameId);
            gameplayProgress.ObserveNetwork(transport.InitialLoadingReceivedBytes, transport.InitialLoadingTotalBytes,
                transport.InitialLoadingReadyPlayerCount, transport.InitialLoadingPlayerCount,
                transport.IsInitialLoadingStateInstalled, gameplayController.IsStartupSimulationReady, boundaryReady, failed);
        }

        private void RenderGameplayProgress()
        {
            var state = gameplayProgress;
            if (state == null) return;
            progressFill.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, progressFillWidth * state.Fill);
            tipsGroup.alpha = 1f;
            progressText.text = waitingStatus;
            switch (state.Stage)
            {
                case GameplayLoadingStage.Scene:
                    tipsText.text = sceneStageText;
                    progressText.text = string.Format(percentageFormat, Mathf.RoundToInt(state.SceneProgress * 100f));
                    break;
                case GameplayLoadingStage.Initializing:
                    tipsText.text = state.Host ? hostInitializationText : state.Online ? clientInitializationText : localInitializationText;
                    progressText.text = preparingStatus;
                    break;
                case GameplayLoadingStage.Restoring:
                    tipsText.text = state.Online && !state.Host ? clientInitializationText : restoreStageText;
                    progressText.text = state.Online && !state.Host ? preparingStatus : restoringStatus;
                    break;
                case GameplayLoadingStage.PreparingSynchronization:
                    tipsText.text = state.Host ? preparingSynchronizationText : awaitingHostText;
                    progressText.text = preparingStatus;
                    break;
                case GameplayLoadingStage.Synchronizing:
                    tipsText.text = state.TotalBytes == 0 ? awaitingHostText :
                        string.Format(receivingFormat, state.ReceivedBytes / 1048576f, state.TotalBytes / 1048576f);
                    if (state.TotalBytes > 0) progressText.text = string.Format(percentageFormat,
                        Mathf.FloorToInt(100f * state.ReceivedBytes / state.TotalBytes));
                    break;
                case GameplayLoadingStage.WaitingForPlayers:
                    tipsText.text = state.Host && state.ReadyPlayers < state.TotalPlayers ? hostWaitingText : confirmingText;
                    if (state.Host) progressText.text = string.Format(playerCountFormat, state.ReadyPlayers, state.TotalPlayers);
                    break;
                case GameplayLoadingStage.Ready:
                    tipsText.text = readyText;
                    progressText.text = string.Format(percentageFormat, 100);
                    break;
                case GameplayLoadingStage.Failed:
                    tipsText.text = failedText;
                    progressText.text = failureStatus;
                    break;
            }
        }

        private void ReturnToMenu()
        {
            if (!activationComplete || aborted) return;
            GameLaunchContext.ShutdownOnlineSession();
            SceneTransitionContext.CompleteTransition();
            aborted = SceneTransitionContext.TryBeginBlackTransition(FallbackSceneName);
            if (returnToMenuButton != null) returnToMenuButton.interactable = !aborted;
        }

        private void OnDestroy()
        {
            if (active == this) active = null;
            if (returnToMenuButton != null) returnToMenuButton.onClick.RemoveListener(ReturnToMenu);
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
