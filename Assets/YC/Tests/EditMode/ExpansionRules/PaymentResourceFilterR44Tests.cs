using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离规则投影与真实原页组件检查，不计正常页面或Steam验收。
    public sealed class PaymentResourceFilterR44Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value)
            ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args) =>
            value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);

        [TestCase("Originium,OriginiumShard,Iron")]
        [TestCase("PureOriginium,GoldVoucher")]
        [TestCase("Iron")]
        public void TotalRowsFollowDemandRatherThanStockOrSelectedAmount(string resourceTypes)
        {
            using var fixture = new PaymentTestFixture();
            fixture.State.FindPlayer(1).Resources.Originium = 0;
            var allowed = resourceTypes.Split(',');
            var args = PaymentTestFixture.TotalArgs(2, new ResourceSet { GoldVoucher = 4 });
            Value(args, "allowedResourceTypes").Items = allowed.Select(NormalizedValue.CreateString).ToList();
            fixture.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, args, 1));
            var binding = Activator.CreateInstance(T("PaymentPageDraftBinding"), true);
            Call(binding, "Bind", "isolated/player/1", fixture.Open());
            var expected = PaymentResources.Types.Select(type => allowed.Contains(type.ToString())).ToArray();
            Assert.That(Read(binding, "VisibleRows"), Is.EqualTo(expected));
            var values = (int[])Read(binding, "InitialCost");
            Call(binding, "SetDesired", values);
            Assert.That(Read(binding, "VisibleRows"), Is.EqualTo(expected), "已选数量不能使允许行消失");
            var empty = new int[5]; Call(binding, "SetDesired", empty);
            Assert.That(Read(binding, "VisibleRows"), Is.EqualTo(expected), "未满额草稿也不能改变资源身份");
            Assert.That(fixture.State.FindPlayer(1).Resources.Originium, Is.Zero);
            fixture.Artifacts.WriteLabeledText("合计付款资源显示投影", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                request = fixture.Open(), expected, selected = values, Isolated = true, NormalUiAcceptance = false }));
        }

        [Test]
        public void FixedCostPromptContainsOnlyPositiveTypedResourcesAndKeepsIcons()
        {
            var refs = ((IEnumerable<GameplayTextRef>)T("PaymentPageDraftBinding").GetMethod("FixedCostPresentation", Flags)
                .Invoke(null, new object[] { ResourcePaymentPreparationService.Cost(new ResourceSet { Iron = 2, GoldVoucher = 4 }) })).ToArray();
            Assert.That(refs.Length, Is.EqualTo(3));
            Assert.That(refs.Skip(1).Select(r => r.Arguments.Single(a => a.Name == "amount").Value.IntegerValue), Is.EqualTo(new long[] { 2, 4 }));
            Assert.That(refs.Skip(1).Select(r => r.Arguments.Single(a => a.Kind == "resource").Value.ReferenceId), Is.EqualTo(new[] { "Iron", "GoldVoucher" }));
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var resolver = new GameplayTextResolver(pack.Presentation);
            var context = new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                Locale = pack.Presentation.Locale, ResourceIconKeys = new Dictionary<string, string> {
                    { "Iron", "core:resource.iron" }, { "GoldVoucher", "core:resource.gold_voucher" } } };
            var runs = refs.SelectMany(reference => resolver.Resolve(reference, context).Runs).ToArray();
            Assert.That(runs.OfType<IconRun>().Select(r => r.IconKey), Is.EqualTo(new[] { "core:resource.iron", "core:resource.gold_voucher" }));
            Assert.That(runs.OfType<NumberRun>().Select(r => r.Value), Is.EqualTo(new long[] { 2, 4 }));
        }

        [TestCase(2, true)]
        [TestCase(3, false)]
        public void OriginalPaymentPageFiltersRowsButKeepsFullVectorAndDecrease(int selectedIndex, bool basicResources)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var registry = asset.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var canvas = new GameObject("R44 Isolated Payment Canvas", typeof(RectTransform), typeof(Canvas));
            object dialog = null;
            try
            {
                dialog = Activator.CreateInstance(T("SpecialActionChoiceDialog"), Flags, null,
                    new object[] { registry, (Func<RectTransform>)(() => (RectTransform)canvas.transform) }, null);
                var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
                var resolver = new GameplayTextResolver(pack.Presentation);
                var context = new GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash, Locale = pack.Presentation.Locale };
                var keys = (string[])T("PaymentPageDraftBinding").GetField("ResourceLabelKeys", Flags).GetValue(null);
                var labels = keys.Select(key => resolver.Resolve(new GameplayTextRef(key), context).ToPlainText()).ToArray();
                var visible = basicResources ? new[] { true, true, true, false, false } : new[] { false, false, false, true, true };
                var maximums = visible.Select(v => v ? 2 : 0).ToArray();
                var initial = new int[5]; initial[selectedIndex] = 2;
                IReadOnlyList<int> latest = null, submitted = null;
                Call(dialog, "ShowPaymentDraft", "", labels, maximums, initial,
                    (Action<IReadOnlyList<int>>)(values => latest = values), (Action<bool>)(_ => { }),
                    (Func<bool>)(() => true), (Func<bool>)(() => true), (Func<bool>)(() => true),
                    (Func<IReadOnlyList<int>, bool>)(values => values.Sum() == 2),
                    (Action<IReadOnlyList<int>>)(values => submitted = values), null, "r44-payment", null, null, visible, 2);
                var shell = Read(dialog, "shell");
                var view = (Component)Read(shell, "view");
                var bound = new object[] { "r44-payment", null, null };
                Assert.That(Call(view, "TryGetResourceAllocation", bound), Is.True);
                var rows = ((IEnumerable)bound[2]).Cast<Component>().ToArray();
                Assert.That(rows.Length, Is.EqualTo(visible.Count(v => v)));
                Assert.That(rows.All(row => row != null && row.gameObject.activeSelf), Is.True);
                Assert.That(latest, Is.EqualTo(initial));
                Assert.That(rows.All(row => !((Button)Read(row, "IncreaseButton")).interactable), Is.True,
                    "合计已足额只禁用增加，不隐藏仍可减回的允许行");
                var selectedRow = rows.Single(row => row.name == "Resource Allocation Row " + selectedIndex);
                var summary = (Text)Read(view, "ResourceSummaryText");
                foreach (var index in Enumerable.Range(0, 5).Where(i => !visible[i]))
                    Assert.That(summary.text, Does.Not.Contain(labels[index]));
                ((Button)Read(selectedRow, "DecreaseButton")).onClick.Invoke();
                Assert.That(latest.Count, Is.EqualTo(5)); Assert.That(latest[selectedIndex], Is.EqualTo(1));
                Assert.That(((Button)bound[1]).interactable, Is.False);
                Assert.That(selectedRow.gameObject.activeSelf, Is.True);
                ((Button)Read(selectedRow, "IncreaseButton")).onClick.Invoke();
                Assert.That(latest, Is.EqualTo(initial));
                Assert.That(((Button)bound[1]).interactable, Is.True);
                ((Button)bound[1]).onClick.Invoke();
                Assert.That(submitted, Is.EqualTo(initial), "回调保持原五资源索引，隐藏行不压缩或串位");
            }
            finally
            {
                if (dialog != null) Call(dialog, "Hide");
                Object.DestroyImmediate(canvas);
            }
        }
    }
}
