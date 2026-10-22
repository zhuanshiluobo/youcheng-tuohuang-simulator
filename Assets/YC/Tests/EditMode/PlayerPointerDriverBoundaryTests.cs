using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Tests.EditMode
{
    public sealed class PlayerPointerDriverBoundaryTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly List<Behaviour> suspended = new List<Behaviour>();
        private EventSystem testEventSystem;
        private static Type Driver => Type.GetType("YC.PlayerJourney.PlayerPointerDriver, YC.PlayerJourney", true);

        [SetUp]
        public void SetUp()
        {
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>()) Suspend(camera);
            foreach (var raycaster in UnityEngine.Object.FindObjectsOfType<BaseRaycaster>()) Suspend(raycaster);
            testEventSystem = Make("指针边界测试 EventSystem").AddComponent<EventSystem>();
            // EditMode 不执行普通 MonoBehaviour 的启用回调；显式登记测试输入系统。
            typeof(EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(testEventSystem, null);
            EventSystem.current = testEventSystem;
        }

        [TearDown]
        public void TearDown()
        {
            if (testEventSystem != null)
                typeof(EventSystem).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(testEventSystem, null);
            for (var i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null)
                {
                    foreach (var raycaster in objects[i].GetComponents<BaseRaycaster>())
                        InvokeRaycasterLifecycle(raycaster, "OnDisable");
                    UnityEngine.Object.DestroyImmediate(objects[i]);
                }
            foreach (var item in suspended) if (item != null) item.enabled = true;
            testEventSystem = null;
            objects.Clear();
            suspended.Clear();
        }

        [Test]
        public void MissingCameraOrInactiveWorldTarget_IsUnreachableWithoutThrowing()
        {
            var target = Make("无相机地图目标");
            target.AddComponent<BoxCollider2D>();
            Assert.That(Camera.main, Is.Null);
            Assert.That(Reachable(target), Is.False);
            Assert.That(float.IsNaN(Position(target).x), Is.True);
            target.SetActive(false);
            Assert.That(Reachable(target), Is.False);
            Assert.That(Reachable(null), Is.False);
            Assert.That(Hit(new Vector2(float.NaN, float.NaN)), Is.Null);
        }

        [Test]
        public void UntaggedPhysicsCamera_ProjectsAndHitsWorldMapTarget()
        {
            var camera = Make("无 MainCamera 标签的地图相机").AddComponent<Camera>();
            camera.transform.position = new Vector3(1000, 0, -10);
            camera.orthographic = true;
            camera.orthographicSize = 5;
            InvokeRaycasterLifecycle(camera.gameObject.AddComponent<Physics2DRaycaster>(), "OnEnable");
            var target = Make("地图物理点击目标");
            target.transform.position = new Vector3(1000, 0, 0);
            var collider = target.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(2, 2);
            Physics2D.SyncTransforms();
            Assert.That(Camera.main, Is.Null);

            var expected = (Vector2)camera.WorldToScreenPoint(collider.bounds.center);
            Assert.That(Vector2.Distance(Position(target), expected), Is.LessThan(.01f));
            Assert.That(Reachable(target), Is.True);
            Assert.That(Hit(expected), Is.SameAs(target));

            collider.enabled = false;
            Assert.That(Reachable(target), Is.False, "禁用碰撞区不得仍然作为玩家可点击目标。");
            collider.enabled = true;
            camera.enabled = false;
            Assert.That(Reachable(target), Is.False, "已关闭的地图相机不能用于投影。");
        }

        [Test]
        public void WorldTargetBehindCamera_IsNotReachable()
        {
            var camera = Make("地图相机").AddComponent<Camera>();
            InvokeRaycasterLifecycle(camera.gameObject.AddComponent<Physics2DRaycaster>(), "OnEnable");
            var target = Make("相机背后目标");
            target.transform.position = new Vector3(0, 0, -5);
            target.AddComponent<BoxCollider2D>();
            Physics2D.SyncTransforms();
            Assert.That(Reachable(target), Is.False);
            Assert.That(float.IsNaN(Position(target).x), Is.True);
        }

        [Test]
        public void WorldCanvasWithoutAssignedCamera_IsNotReachable()
        {
            var canvasObject = new GameObject("未配置相机的世界画布", typeof(RectTransform), typeof(Canvas));
            objects.Add(canvasObject);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var target = new GameObject("画布目标", typeof(RectTransform));
            objects.Add(target);
            target.transform.SetParent(canvasObject.transform, false);
            Assert.That(Reachable(target), Is.False);
            Assert.That(float.IsNaN(Position(target).x), Is.True);
        }

        private GameObject Make(string name)
        {
            var item = new GameObject(name);
            objects.Add(item);
            return item;
        }
        private void Suspend(Behaviour item)
        {
            if (!item.enabled) return;
            suspended.Add(item);
            item.enabled = false;
        }
        private static void InvokeRaycasterLifecycle(BaseRaycaster raycaster, string method) =>
            typeof(BaseRaycaster).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(raycaster, null);
        private static object Call(string name, params object[] args) => Driver.GetMethod(name,
            BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        private static bool Reachable(GameObject target) => (bool)Call("Reachable", target);
        private static Vector2 Position(GameObject target) => (Vector2)Call("Position", target);
        private static GameObject Hit(Vector2 position) => (GameObject)Call("Hit", position);
    }
}
