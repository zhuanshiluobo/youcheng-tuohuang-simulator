using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class DispatchDecisionDialogView : MonoBehaviour
    {
        [SerializeField] private RectTransform overlayRect;
        [SerializeField] private Image overlayImage;
        [SerializeField] private RectTransform panel;
        [SerializeField] private Text titleText;
        [SerializeField] private Text messageText;
        [SerializeField] private Button continueButton;
        [SerializeField] private Text continueLabel;
        [SerializeField] private Button finishButton;
        [SerializeField] private Text finishLabel;

        public RectTransform OverlayRect => overlayRect;
        public Image OverlayImage => overlayImage;
        public RectTransform Panel => panel;
        public Text TitleText => titleText;
        public Text MessageText => messageText;
        public Button ContinueButton => continueButton;
        public Text ContinueLabel => continueLabel;
        public Button FinishButton => finishButton;
        public Text FinishLabel => finishLabel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (overlayRect == null || overlayImage == null || panel == null || titleText == null ||
                messageText == null || continueButton == null || continueLabel == null ||
                finishButton == null || finishLabel == null)
            {
                reason = "调度决策对话框的序列化引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void Bind(
            string title,
            string message,
            string continueText,
            Action continueAction,
            string finishText,
            Action finishAction)
        {
            ClearCallbacks();
            titleText.text = title ?? string.Empty;
            messageText.text = message ?? string.Empty;
            continueLabel.text = continueText ?? string.Empty;
            finishLabel.text = finishText ?? string.Empty;
            if (continueAction != null)
            {
                continueButton.onClick.AddListener(() => continueAction());
            }

            if (finishAction != null)
            {
                finishButton.onClick.AddListener(() => finishAction());
            }
        }

        public void ClearCallbacks()
        {
            if (continueButton != null)
            {
                continueButton.onClick.RemoveAllListeners();
            }

            if (finishButton != null)
            {
                finishButton.onClick.RemoveAllListeners();
            }
        }
    }
}
