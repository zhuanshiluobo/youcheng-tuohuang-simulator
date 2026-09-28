using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Interactions;

namespace YC.Tests.PlayMode
{
    public sealed class EffectPagePlayModeTests
    {
        [UnityTest]
        public IEnumerator SuspendedEffect_StaysHiddenOnRefreshAndMovesToCurrentStep()
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var context = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null) as Component;
            if (context != null)
            {
                UnityEngine.Object.Destroy(context.gameObject);
                yield return null;
            }
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var type = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
            var frame = UnityEngine.Object.FindObjectOfType(type) as Component;
            Assert.That(frame, Is.Not.Null);
            var content = (RectTransform)type.GetProperty("ContentRect").GetValue(frame);
            var fold = (Button)type.GetProperty("FoldButton").GetValue(frame);
            var routed = type.GetMethod("HandleRequestRouted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var first = new InteractionRequestProjection
            {
                InteractionId = "request-1", OwnerEffectId = "effect-a", StateRevision = 10,
                Status = "open", MinSelections = 1, MaxSelections = 1
            };
            routed.Invoke(frame, new object[] { first });
            var pageA = NewPage(content, "effect-step-1");
            type.GetMethod("ShowPage").Invoke(frame, new object[] { pageA, true });
            Assert.That(pageA.activeSelf, Is.True);
            fold.onClick.Invoke();
            Assert.That(pageA.activeSelf, Is.False);
            first.StateRevision = 11;
            routed.Invoke(frame, new object[] { first });
            Assert.That(pageA.activeSelf, Is.False, "同一请求刷新不得自动弹回");
            var next = new InteractionRequestProjection
            {
                InteractionId = "request-2", OwnerEffectId = "effect-a", StateRevision = 12,
                Status = "open", MinSelections = 1, MaxSelections = 2
            };
            routed.Invoke(frame, new object[] { next });
            var pageB = NewPage(content, "effect-step-2");
            type.GetMethod("ShowPage").Invoke(frame, new object[] { pageB, true });
            Assert.That(pageB.activeSelf, Is.False, "同一流程的新步骤应保持收起");
            fold.onClick.Invoke();
            Assert.That(pageB.activeSelf, Is.True);
            Assert.That(pageA.activeSelf, Is.False);
            routed.Invoke(frame, new object[] { first });
            Assert.That(Field(frame, "currentRequestId"), Is.EqualTo("request-2"),
                "迟到的旧版本不得覆盖当前步骤");
            routed.Invoke(frame, new object[] { null });
            Assert.That(fold.interactable, Is.False);
            UnityEngine.Object.Destroy(pageA);
            UnityEngine.Object.Destroy(pageB);
            yield return null;
        }

        private static GameObject NewPage(RectTransform parent, string name)
        {
            var page = new GameObject(name, typeof(RectTransform));
            page.transform.SetParent(parent, false);
            return page;
        }

        private static object Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }
}
