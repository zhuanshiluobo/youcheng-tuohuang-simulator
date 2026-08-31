using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YC.Presentation
{
    public static class SceneTransitionContext
    {
        public const string LoadingSceneName = "LoadingScene";

        private static string targetSceneName;
        private static bool transitionInProgress;
        private static bool showProgress = true;
        private static bool quickBlackTransition;

        public static bool IsTransitionInProgress => transitionInProgress;

        public static bool TryBeginTransition(string sceneName)
        {
            return TryBeginTransition(sceneName, true, false);
        }

        public static bool TryBeginBlackTransition(string sceneName)
        {
            return TryBeginTransition(sceneName, false, true);
        }

        private static bool TryBeginTransition(
            string sceneName,
            bool displayProgress,
            bool useQuickBlackTransition)
        {
            if (transitionInProgress)
            {
                return false;
            }

            SetTargetScene(sceneName);
            showProgress = displayProgress;
            quickBlackTransition = useQuickBlackTransition;
            transitionInProgress = true;
            try
            {
                var operation = SceneManager.LoadSceneAsync(LoadingSceneName, LoadSceneMode.Additive);
                if (operation == null)
                {
                    throw new InvalidOperationException("Unity 未能创建加载场景任务。");
                }

                return true;
            }
            catch
            {
                Clear();
                throw;
            }
        }

        public static void SetTargetScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("目标场景名称不能为空。", nameof(sceneName));
            }

            targetSceneName = sceneName.Trim();
        }

        public static string ConsumeTargetScene()
        {
            var sceneName = targetSceneName;
            targetSceneName = null;
            return sceneName;
        }

        public static bool ConsumeShowProgress()
        {
            var value = showProgress;
            showProgress = true;
            return value;
        }

        public static bool ConsumeQuickBlackTransition()
        {
            var value = quickBlackTransition;
            quickBlackTransition = false;
            return value;
        }

        public static void Clear()
        {
            targetSceneName = null;
            transitionInProgress = false;
            showProgress = true;
            quickBlackTransition = false;
        }

        public static void CompleteTransition()
        {
            transitionInProgress = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForRuntimeStart()
        {
            Clear();
        }
    }
}
