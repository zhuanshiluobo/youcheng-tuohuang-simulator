using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class CollectionRoomController : MonoBehaviour
    {
        [SerializeField] private Transform modelPivot;
        [SerializeField] private LoadingModelDragController dragController;
        [SerializeField] private Button backButton;
        [SerializeField] private Button resetViewButton;
        [SerializeField] private string returnSceneName = "StartScene";

        private Quaternion initialRotation;

        private void Awake()
        {
            if (modelPivot == null || dragController == null)
            {
                Debug.LogError("收藏室缺少源石模型或拖拽控制器。", this);
                enabled = false;
                return;
            }

            initialRotation = modelPivot.localRotation;
            if (backButton != null)
            {
                backButton.onClick.AddListener(ReturnToStart);
            }

            if (resetViewButton != null)
            {
                resetViewButton.onClick.AddListener(ResetView);
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ReturnToStart();
            }
        }

        private void OnDestroy()
        {
            if (backButton != null)
            {
                backButton.onClick.RemoveListener(ReturnToStart);
            }

            if (resetViewButton != null)
            {
                resetViewButton.onClick.RemoveListener(ResetView);
            }
        }

        public void ResetView()
        {
            dragController.StopMotion();
            modelPivot.localRotation = initialRotation;
        }

        public void ReturnToStart()
        {
            if (SceneTransitionContext.IsTransitionInProgress)
            {
                return;
            }

            try
            {
                SceneTransitionContext.TryBeginBlackTransition(returnSceneName);
            }
            catch (System.Exception ex)
            {
                SceneTransitionContext.Clear();
                Debug.LogError("返回主页的过场加载失败：" + ex.Message, this);
            }
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            Transform pivot,
            LoadingModelDragController drag,
            Button back,
            Button reset)
        {
            modelPivot = pivot;
            dragController = drag;
            backButton = back;
            resetViewButton = reset;
        }
#endif
    }
}
