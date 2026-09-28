using System;
using System.Collections.Generic;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public interface IInteractionRequestRenderer
    {
        string Id { get; }
        int Priority { get; }
        bool CanRender(InteractionRequestProjection request);
        void Render(InteractionRequestProjection request);
        void Clear();
    }

    /// <summary>
    /// 通用 InteractionRequest 展示合同。Renderer 只消费玩家投影，不持有 pending 权威状态，
    /// 也不直接调用领域服务；回答仍由 Host AnswerInteraction 命令提交。
    /// </summary>
    public sealed class InteractionRequestRouter
    {
        public static event Action<InteractionRequestProjection> RequestRouted;
        private readonly List<IInteractionRequestRenderer> renderers =
            new List<IInteractionRequestRenderer>();
        private readonly HashSet<string> rendererIds =
            new HashSet<string>(StringComparer.Ordinal);
        private IInteractionRequestRenderer activeRenderer;

        public void Register(IInteractionRequestRenderer renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (string.IsNullOrEmpty(renderer.Id)) throw new ArgumentException("Renderer ID 不能为空。", nameof(renderer));
            if (!rendererIds.Add(renderer.Id)) throw new InvalidOperationException("Renderer ID 重复：" + renderer.Id);
            renderers.Add(renderer);
            renderers.Sort((left, right) =>
            {
                int comparison = right.Priority.CompareTo(left.Priority);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
        }

        public bool Route(InteractionRequest request, int viewerPlayerId)
        {
            InteractionRequestProjection projection =
                InteractionRequestProjector.ProjectForPlayer(request, viewerPlayerId);
            if (TryRoute(projection, viewerPlayerId)) return true;
            Clear();
            return false;
        }

        public bool RouteOpen(IEnumerable<InteractionRequest> requests, int viewerPlayerId)
        {
            if (requests != null)
                foreach (var request in requests)
                    if (TryRoute(InteractionRequestProjector.ProjectForPlayer(request, viewerPlayerId), viewerPlayerId))
                        return true;
            Clear();
            return false;
        }

        private bool TryRoute(InteractionRequestProjection projection, int viewerPlayerId)
        {
            if (projection == null || !projection.VisibleToViewer || projection.Status != "open" ||
                projection.AnsweringPlayerId != viewerPlayerId) return false;

            for (int i = 0; i < renderers.Count; i++)
            {
                IInteractionRequestRenderer renderer = renderers[i];
                if (!renderer.CanRender(projection)) continue;
                if (activeRenderer != null && activeRenderer != renderer) activeRenderer.Clear();
                RequestRouted?.Invoke(projection);
                renderer.Render(projection);
                activeRenderer = renderer;
                return true;
            }

            return false;
        }

        public void Clear()
        {
            RequestRouted?.Invoke(null);
            if (activeRenderer != null)
            {
                activeRenderer.Clear();
                activeRenderer = null;
            }
        }
    }
}
