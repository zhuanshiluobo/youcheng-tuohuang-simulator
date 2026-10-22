using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class EventChoiceDialogAssetConfigurationTests
    {
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private static readonly string[] ModeFields =
        {
            "eventCardMode", "explorePathMode", "explorePaymentMode", "resourceCollectionPaymentMode",
            "characterSecondEffectDecisionMode", "conditionPaymentMode"
        };
        private static readonly int[] ModeNumbers = { 0, 1, 2, 3, 6, 7 };

        [Test]
        public void HudReferencedEventDialogKeepsAllActualModeReferencesInAssetAndInstance()
        {
            var report = new LoadReport { UnityVersion = UnityEngine.Application.unityVersion, HudPath = HudPath };
            Component instance = null;
            try
            {
                var hud = AssetDatabase.LoadAssetAtPath<GameObject>(HudPath);
                Assert.That(hud, Is.Not.Null, "真实HUD预制体不存在");
                var registryType = Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true);
                var registry = hud.GetComponentInChildren(registryType, true);
                Assert.That(registry, Is.Not.Null, "真实HUD缺少对话框注册组件");
                report.Registry = Identify(registry);
                var registryData = new SerializedObject(registry);
                var prefabProperty = registryData.FindProperty("eventChoiceDialogPrefab");
                Assert.That(prefabProperty, Is.Not.Null);
                var prefab = prefabProperty.objectReferenceValue as Component;
                Assert.That(prefab, Is.Not.Null, "HUD实际序列化的事件窗口引用为空");
                Assert.That(prefab.GetType().FullName, Is.EqualTo("YC.Presentation.EventChoiceDialogView"));
                report.Dialog = Identify(prefab);
                report.Script = Identify(MonoScript.FromMonoBehaviour((MonoBehaviour)prefab));
                report.NativeAssembly = prefab.GetType().Assembly.Location;
                report.NativeMvid = prefab.GetType().Module.ModuleVersionId.ToString();
                CheckActualView(prefab, "共享Prefab", report);

                // 仅实例化HUD实际引用的资产，不构造模式块或补写字段，也不调用显示/重建/保存入口。
                instance = UnityEngine.Object.Instantiate(prefab);
                CheckActualView(instance, "运行时等效实例", report);
                CheckRetiredModes(instance, report.Failures);
                Assert.That(report.Failures, Is.Empty, string.Join("\n", report.Failures));
            }
            catch (Exception error)
            {
                report.Error = error.ToString();
                throw;
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject);
                var json = JsonUtility.ToJson(report, true);
                TestContext.WriteLine(json);
                var output = Environment.GetEnvironmentVariable("YC_EVENT_ASSET_REPORT_PATH");
                if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, json);
            }
        }

        private static void CheckActualView(Component view, string scope, LoadReport report)
        {
            var snapshot = new ViewSnapshot { Scope = scope, Component = Identify(view) };
            report.Views.Add(snapshot);
            var type = view.GetType();
            var data = new SerializedObject(view);
            var actual = new GameObject[ModeFields.Length];
            for (var index = 0; index < ModeFields.Length; index++)
            {
                var field = type.GetField(ModeFields[index], BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null, ModeFields[index]);
                var value = (GameObject)field.GetValue(view);
                actual[index] = value;
                var property = data.FindProperty(ModeFields[index]);
                snapshot.Fields.Add(new FieldSnapshot
                {
                    Field = ModeFields[index], ModeNumber = ModeNumbers[index], Value = Identify(value),
                    SerializedValue = Identify(property == null ? null : property.objectReferenceValue),
                    SerializedInstanceId = property == null ? 0 : property.objectReferenceInstanceIDValue
                });
                if (value == null) report.Failures.Add(scope + "缺失有效模式：" + ModeFields[index]);
            }
            var blocks = (GameObject[])type.GetProperty("ModeBlocks").GetValue(view);
            snapshot.PublicSlotCount = blocks == null ? -1 : blocks.Length;
            if (blocks == null || blocks.Length != 8) report.Failures.Add(scope + "公开模式数组不是八位置");
            else
            {
                for (var index = 0; index < blocks.Length; index++) snapshot.PublicSlots.Add(Identify(blocks[index]));
                if (blocks[4] != null || blocks[5] != null) report.Failures.Add(scope + "退役4/5仍绑定模式块");
                for (var index = 0; index < actual.Length; index++)
                    if (blocks[ModeNumbers[index]] != actual[index]) report.Failures.Add(scope + "公开模式编号与字段不一致：" + ModeFields[index]);
            }
            var internalBlocks = (GameObject[])type.GetMethod("GetModeBlocks", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
            snapshot.InternalSlotCount = internalBlocks.Length;
            if (internalBlocks.Length != ModeFields.Length) report.Failures.Add(scope + "内部模式遍历不是六个有效位置");
            var args = new object[] { null };
            snapshot.Valid = (bool)type.GetMethod("TryValidateConfiguration").Invoke(view, args);
            snapshot.Reason = args[0] as string;
            if (!snapshot.Valid) report.Failures.Add(scope + "配置校验失败：" + snapshot.Reason);
        }

        private static void CheckRetiredModes(Component view, List<string> failures)
        {
            var modeType = Type.GetType("YC.Presentation.EventChoiceDialogMode, Assembly-CSharp", true);
            var method = view.GetType().GetMethod("SetMode");
            foreach (var value in new[] { 4, 5 })
            {
                var rejected = false;
                try { method.Invoke(view, new[] { Enum.ToObject(modeType, value) }); }
                catch (TargetInvocationException error) { rejected = error.InnerException is ArgumentOutOfRangeException; }
                if (!rejected) failures.Add("退役模式未拒绝：" + value);
            }
        }

        private static ObjectIdentity Identify(UnityEngine.Object value)
        {
            var result = new ObjectIdentity { ManagedNull = ReferenceEquals(value, null), UnityNull = value == null };
            if (value == null) return result;
            result.Name = value.name;
            result.Type = value.GetType().FullName;
            result.InstanceId = value.GetInstanceID();
            result.AssetPath = AssetDatabase.GetAssetPath(value);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out result.Guid, out result.FileId);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(value);
            if (source != null)
            {
                result.SourcePath = AssetDatabase.GetAssetPath(source);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out result.SourceGuid, out result.SourceFileId);
            }
            return result;
        }

        [Serializable] private sealed class LoadReport
        {
            public string UnityVersion, HudPath, NativeAssembly, NativeMvid, Error;
            public ObjectIdentity Registry, Dialog, Script;
            public List<ViewSnapshot> Views = new List<ViewSnapshot>();
            public List<string> Failures = new List<string>();
        }
        [Serializable] private sealed class ViewSnapshot
        {
            public string Scope, Reason;
            public ObjectIdentity Component;
            public List<FieldSnapshot> Fields = new List<FieldSnapshot>();
            public List<ObjectIdentity> PublicSlots = new List<ObjectIdentity>();
            public int PublicSlotCount, InternalSlotCount;
            public bool Valid;
        }
        [Serializable] private sealed class FieldSnapshot
        {
            public string Field;
            public int ModeNumber, SerializedInstanceId;
            public ObjectIdentity Value, SerializedValue;
        }
        [Serializable] private sealed class ObjectIdentity
        {
            public bool ManagedNull, UnityNull;
            public string Name, Type, AssetPath, Guid, SourcePath, SourceGuid;
            public long FileId, SourceFileId;
            public int InstanceId;
        }
    }
}
