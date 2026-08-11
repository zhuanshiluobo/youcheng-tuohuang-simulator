using System;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class DispatchDecisionView
    {
        private readonly GameplayDialogRegistry dialogRegistry;
        private readonly Func<RectTransform> getCanvas;
        private DispatchDecisionDialogView view;

        public DispatchDecisionView(
            GameplayDialogRegistry configuredDialogRegistry,
            Func<RectTransform> getCanvas)
        {
            dialogRegistry = configuredDialogRegistry ??
                             throw new ArgumentNullException(nameof(configuredDialogRegistry));
            this.getCanvas = getCanvas ?? throw new ArgumentNullException(nameof(getCanvas));
        }

        public void Show(DispatchDecisionViewModel viewModel)
        {
            Hide();
            var canvas = getCanvas();
            if (viewModel == null || canvas == null)
            {
                return;
            }

            view = dialogRegistry.InstantiateDispatchDecision(canvas);
            if (view == null)
            {
                return;
            }

            view.gameObject.name = "Dispatch Decision Overlay";

            if (!view.TryValidateConfiguration(out var reason))
            {
                var invalidView = view;
                view = null;
                DestroyView(invalidView);
                throw new InvalidOperationException(reason);
            }

            var boundView = view;
            var decisionInvoked = false;
            view.Bind(
                viewModel.Title,
                viewModel.Message,
                viewModel.ContinueLabel,
                viewModel.ContinueAction == null
                    ? null
                    : (Action)(() =>
                    {
                        if (decisionInvoked)
                        {
                            return;
                        }

                        decisionInvoked = true;
                        HideBoundView(boundView);
                        viewModel.ContinueAction();
                    }),
                viewModel.FinishLabel,
                viewModel.FinishAction == null
                    ? null
                    : (Action)(() =>
                    {
                        if (decisionInvoked)
                        {
                            return;
                        }

                        decisionInvoked = true;
                        HideBoundView(boundView);
                        viewModel.FinishAction();
                    }));
        }

        public void Hide()
        {
            if (view == null)
            {
                return;
            }

            var releasedView = view;
            view = null;
            DestroyView(releasedView);
        }

        private void HideBoundView(DispatchDecisionDialogView boundView)
        {
            if (view != boundView)
            {
                return;
            }

            Hide();
        }

        private static void DestroyView(DispatchDecisionDialogView target)
        {
            if (target == null)
            {
                return;
            }

            target.ClearCallbacks();
            target.gameObject.SetActive(false);
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target.gameObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target.gameObject);
            }
        }
    }
}
