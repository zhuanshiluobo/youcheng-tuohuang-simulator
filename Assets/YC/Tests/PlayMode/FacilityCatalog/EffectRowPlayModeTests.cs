#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace YC.Tests.PlayMode
{
    public sealed class EffectRowPlayModeTests
    {
        [UnityTest]
        public IEnumerator ExecutionTemplateRetired_UsesCurrentLogicRow()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab");
            var shellType = Type.GetType("YC.Presentation.EffectDialogShellView, Assembly-CSharp", true);
            var shell = prefab.GetComponent(shellType);
            Assert.That(shellType.GetField("executionRowTemplate", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null);
            var current = (Component)shellType.GetField("effectRowTemplate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
            Assert.That(current, Is.Not.Null);
            Assert.That(current.GetType().Name, Is.EqualTo("UiEffectRowView"));
            foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(child.name, Is.Not.EqualTo("Execution Option Row Template"));
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                yield return null;
                var args = new object[] { null };
                Assert.That((bool)shellType.GetMethod("TryValidateConfiguration").Invoke(instance.GetComponent(shellType), args), Is.True, (string)args[0]);
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
#endif
