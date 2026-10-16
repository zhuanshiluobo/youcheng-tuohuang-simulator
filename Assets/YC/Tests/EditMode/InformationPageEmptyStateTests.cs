using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.Interactions;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 只验证实际壳层的动态生命周期，不把组件测试称作场景入口或视觉验收。
    public sealed class InformationPageEmptyStateTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject host;
        private object shell;
        private static Type T(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        private static object Read(object o, string name) => o.GetType().GetField(name, Members)?.GetValue(o) ??
            o.GetType().GetProperty(name, Members)?.GetValue(o);
        private static object Call(object o, string name, params object[] args) =>
            o.GetType().GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

        [SetUp]
        public void CreateRuntimeOnlyHostWithActualRegistry()
        {
            Assert.That(T("GameplayHudFrame").GetProperty("Active").GetValue(null), Is.Null,
                "组件测试需要无活动HUD；不得关闭或修改用户当前场景来满足此条件。");
            host = new GameObject("信息页生命周期测试宿主", typeof(RectTransform), typeof(Canvas));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var registry = prefab.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            Assert.That(registry, Is.Not.Null);
            shell = Activator.CreateInstance(T("EffectDialogShell"), Members, null, new object[] { registry }, null);
        }

        [TearDown]
        public void ReleaseRuntimeOnlyObjects()
        {
            if (shell != null) Call(shell, "Hide");
            shell = null;
            if (host != null) Object.DestroyImmediate(host);
        }

        [Test]
        public void EmptyInformationPageCanReopenTenTimesWithoutAccumulatingText()
        {
            int? baseline = null;
            for (var i = 0; i < 10; i++)
            {
                var view = Open(0);
                Assert.That(EmptyCount(view), Is.EqualTo(1), "第" + (i + 1) + "次空页提示数量异常");
                var count = view.GetComponentsInChildren<Transform>(true).Length;
                if (baseline == null) baseline = count;
                Assert.That(count, Is.EqualTo(baseline.Value), "隐藏节点也不能累积");
                Call(shell, "Hide");
                Assert.That(EmptyCount(view), Is.Zero);
            }
        }

        [Test]
        public void EmptyAndNonEmptyInformationPagesShareCleanLifecycleAcrossTenSwitches()
        {
            var counts = new Dictionary<int, int>();
            for (var cycle = 0; cycle < 10; cycle++)
            foreach (var candidates in new[] { 0, 2, 0 })
            {
                var view = Open(candidates);
                Assert.That(EmptyCount(view), Is.EqualTo(candidates == 0 ? 1 : 0));
                var content = (RectTransform)Read(view, "OptionContent");
                Assert.That(content.Cast<Transform>().Count(t => t.gameObject.activeSelf && t.name.StartsWith("生命周期候选 ")),
                    Is.EqualTo(candidates), "实际新增候选数量不正确");
                var count = view.GetComponentsInChildren<Transform>(true).Length;
                if (!counts.ContainsKey(candidates)) counts[candidates] = count;
                Assert.That(count, Is.EqualTo(counts[candidates]), "空非空切换遗留动态节点");
                Call(shell, "Hide");
            }
        }

        private Component Open(int count)
        {
            var panel = Call(shell, "PreparePage", (RectTransform)host.transform,
                "生命周期信息页", "生命周期列表", Vector2.zero, Vector2.zero, true, false, true, false);
            var spec = Activator.CreateInstance(T("EffectDialogSelectionSpec"), true);
            var ids = Enumerable.Range(0, count).Select(i => "lifecycle-" + i).ToList();
            void Set(string n, object v) => spec.GetType().GetField(n, Members).SetValue(spec, v);
            Set("Request", new InteractionRequestProjection { VisibleToViewer = true, CandidateIds = ids,
                MinSelections = 0, MaxSelections = 0, InteractionId = "lifecycle", Status = "open" });
            Set("ReadOnly", true);
            Set("IsEffectPage", false);
            Set("IsCurrent", (Func<bool>)(() => true));
            Set("Label", (Func<string, string>)(id => id));
            Set("OptionNamePrefix", "生命周期候选 ");
            Call(shell, "AddSelection", panel, spec);
            return (Component)Read(shell, "view");
        }

        private static int EmptyCount(Component view) => view.GetComponentsInChildren<Transform>(true)
            .Count(t => t.name == "Selection Empty");
    }
}
