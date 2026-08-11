#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YC.Domain.Maps;
using YC.Presentation;

namespace YC.Tests.PlayMode
{
    public static class MapViewPlayModeProbe
    {
        public static void Run()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "SampleScene" || scene.GetRootGameObjects().Length != 6)
                throw new InvalidOperationException("地图 Play Probe 要求 SampleScene roots6。");
            if (UnityEngine.Object.FindObjectsOfType<EventSystem>().Length != 1)
                throw new InvalidOperationException("地图 Play Probe 要求 EventSystem1。");
            var views = UnityEngine.Object.FindObjectsOfType<MapView>();
            if (views.Length != 1)
                throw new InvalidOperationException("运行时必须且只能存在一个 MapView。");
            var view = views[0];
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            if (!view.TryValidateConfiguration(map, out var reason))
                throw new InvalidOperationException("运行时 MapView 无效：" + reason);
            if (view.Locations.Count != 22 || view.InfluenceSlots.Count != 77 ||
                view.CityPool.Count != 4 || view.ScoreMarkerPool.Count != 4 ||
                view.GetComponentsInChildren<MapHotspot>(true).Length != 22 ||
                view.GetComponentsInChildren<MapHighlightPulse>(true).Length != 99 ||
                view.GetComponentsInChildren<MapPlacementFeedback>(true).Length != 99 ||
                view.GetComponentsInChildren<Animator>(true).Length != 99)
                throw new InvalidOperationException("运行时地图固定池拓扑发生变化。");
            var animators = view.GetComponentsInChildren<Animator>(true);
            if (animators.Any(animator =>
                    animator.enabled ||
                    animator.runtimeAnimatorController == null ||
                    animator.updateMode != AnimatorUpdateMode.UnscaledTime ||
                    animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                    animator.applyRootMotion))
                throw new InvalidOperationException("地图反馈 Animator 未保持关闭待机或 UnscaledTime 合同。");
            var materialUsers = view.GetComponentsInChildren<SpriteRenderer>(true)
                .Where(renderer => renderer.sharedMaterial != null &&
                                   renderer.sharedMaterial.shader != null &&
                                   renderer.sharedMaterial.shader.name == "YC/Map Feedback Additive")
                .ToArray();
            if (materialUsers.Length != 297 ||
                materialUsers.Select(renderer => renderer.sharedMaterial).Distinct().Count() != 1)
                throw new InvalidOperationException("地图反馈共享 Material 必须恰好绑定 297 个 renderer。");
            var mapRoot = view.transform.parent;
            if (mapRoot == null || mapRoot.name != "MapRoot" || mapRoot.childCount != 1)
                throw new InvalidOperationException("运行时 MapView 未保持 MapRoot 下唯一 child。");
            if (scene.GetRootGameObjects().Any(root => root.name == "MapView"))
                throw new InvalidOperationException("运行时不允许新增 MapView 场景根。");
        }
    }
}
#endif
