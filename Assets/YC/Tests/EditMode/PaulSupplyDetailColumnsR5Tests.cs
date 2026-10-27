using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 原HUD与生产Refresh的有限组件观察；公开事实为隔离输入，不代表Host授权或正常对局。
    public sealed class PaulSupplyDetailColumnsR5Tests
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        GameObject hudObject;
        static Type Production(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
            catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        static string Hash(string path)
        { using var sha = SHA256.Create(); using var input = File.OpenRead(path); return string.Concat(sha.ComputeHash(input).Select(b => b.ToString("x2"))); }
        static void Save(string name, object value)
        {
            string root = Environment.GetEnvironmentVariable("QY004_R5_OUTPUT") ?? throw new InvalidOperationException("缺少R5证据目录");
            using var output = new FileStream(Path.Combine(root, name + ".json"), FileMode.CreateNew);
            using var writer = new StreamWriter(output); writer.Write(JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        static void Click(Button button)
        {
            Assert.That(button.isActiveAndEnabled && button.IsInteractable(), Is.True);
            var events = EventSystem.current ?? new GameObject("R5隔离指针", typeof(EventSystem)).GetComponent<EventSystem>();
            var pointer = new PointerEventData(events) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }
        [UnityTest] public IEnumerator ProductionRefreshSplitsPaulRowsAndReparentsExistingRowAfterVisibleSetChanges()
        {
            yield return new EnterPlayMode();
            var pack = ExternalContentPack.Current;
            var source = pack.Definitions.Single(d => d.DefinitionId == "entrepreneur.ability.P09");
            var metadata = PlayerDetailDisplayCatalog.Read(new[] { source }, pack.Presentation);
            var view = new GameStateView { GameId = "qy004.r5.component-only", ContentPackHash = pack.Presentation.ContentHash,
                ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1, HasAbilityActionCatalog = true, LaunchPlayerOrder = new List<int> { 1, 2 } };
            foreach (int id in new[] { 1, 2 }) view.Players.Add(new PlayerStateView { PlayerId = id, Name = "组件玩家" + id, Color = (PlayerColor)(id - 1), Resources = new ResourceSet() });
            view.ItemZones.Zones.Add(new ZoneStateView { Zone = new ZoneInstanceState { ZoneId = "r5.active", TemplateId = "core.zone.entrepreneur_active", OwnerPlayerId = 1 } });
            view.ItemZones.Items.Add(new ItemInstanceState { ItemId = "r5.ability", DefinitionId = source.DefinitionId, TypeId = source.ItemTypeId,
                PhysicalKind = ItemPhysicalKind.Card, ZoneId = "r5.active", OwnerSubject = ItemSubjectRef.Player(1), ControllerSubject = ItemSubjectRef.Player(1) });
            view.ItemZones.Items.Add(new ItemInstanceState { ItemId = "r5.round1", DefinitionId = "entrepreneur.round.P.1", TypeId = "entrepreneur.round_card",
                PhysicalKind = ItemPhysicalKind.Card, ZoneId = "r5.active", OwnerSubject = ItemSubjectRef.Player(1), ControllerSubject = ItemSubjectRef.Player(1) });
            foreach (string ability in new[] { "entrepreneur.P09.departure", "entrepreneur.P09.place" })
                view.PlayerEffects.Add(new PlayerEffectFactView { FactId = "r5.fact." + ability, SourceItemId = "r5.ability", DefinitionId = source.DefinitionId,
                    AbilityId = ability, DetailPlayerId = 1, Activation = PlayerDetailActivation.Held, GrantingRuleFactIds = new List<string> { "r5.rule1" } });
            view.PlayerRules.Add(new PlayerRuleFactView { FactId = "r5.rule1", SourceItemId = "r5.round1", RuleOwnerItemId = "r5.ability", RuleRef = "entrepreneur.face.P01", DetailPlayerId = 1 });
            Production("UiTheme").GetMethod("Initialize", Flags).Invoke(null, new object[] { AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", Production("UiThemeCatalog")) });
            hudObject = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            var hud = hudObject.GetComponent(Production("GameplayInteractionHudView"));
            Call(hud, "ConfigureGameplayPresentation", pack.Presentation, new Dictionary<string, string>());
            var modules = Read(hud, "MainModules"); var frame = Read(hud, "Frame"); var expansion = Read(hud, "ExpansionUi");
            int localPlayer = 1, submitted = 0;
            Call(expansion, "Configure", new Func<GameStateView>(() => view), new Func<int>(() => localPlayer), null,
                new Dictionary<string, CharacterModeContract>(), new Action<GameCommand>(_ => submitted++));
            expansion.GetType().GetProperty("DetailDefinitions").SetValue(expansion, metadata);
            Save("实际输入与程序集", new { pack.ContentHash,
                scope = "P09正式元数据/文案＋显式公开事实隔离输入→真实ProjectDetails/Refresh→原HUD；未验证Host规则/正常菜单，不复制Content或图片",
                input = view, assemblies = new[] { GetType().Assembly, Production("GameplayExpansionUi").Assembly, typeof(GameStateView).Assembly }
                    .Select(a => new { a.Location, mvid = a.ManifestModule.ModuleVersionId, sha256 = Hash(a.Location) }),
                productionSourceSha = Hash("Assets/YC/Presentation/GameplayExpansionUi.cs") });
            var snapshots = new List<object>();
            PlayerDetailEntry[] Entries() => ((IEnumerable<PlayerDetailEntry>)Read(expansion, "DetailEntries")).Where(e => e.DetailPlayerId == 1).ToArray();
            IDictionary Rows() => (IDictionary)Read(expansion, "rows");
            RectTransform Host(int column) => (RectTransform)Call(modules, "GetLocalDetailHost", column);
            Component Row(PlayerDetailEntry entry) => (Component)Rows()[entry.EntryId];
            void Refresh()
            {
                Call(modules, "Render", null, view, localPlayer, Read(Read(hud, "DialogRegistry"), "CardVisualCatalog"));
                Call(expansion, "Refresh");
            }
            void Observe(string stage)
            {
                var entries = Entries();
                snapshots.Add(new { stage, view.StateRevision, localPlayer, rows = entries.Select(e => new { e.EntryId, e.AbilityId, e.DisplayName,
                    instance = Row(e).GetInstanceID(), parent = Row(e).transform.parent.GetInstanceID(), sibling = Row(e).transform.GetSiblingIndex(),
                    boundItemId = Read(Row(e), "ItemId"), active = Row(e).gameObject.activeInHierarchy }).ToArray() });
                Assert.That(Rows().Count, Is.EqualTo(entries.Length));
                foreach (var entry in entries) Assert.That(Read(Row(entry), "ItemId"), Is.EqualTo(entry.EntryId));
            }
            Refresh(); yield return null;
            var initial = Entries(); Assert.That(initial, Has.Length.EqualTo(2));
            var departure = initial.Single(e => e.AbilityId == "entrepreneur.P09.departure"); var place = initial.Single(e => e.AbilityId == "entrepreneur.P09.place");
            Assert.That(Row(departure).transform.parent, Is.SameAs(Host(0))); Assert.That(Row(place).transform.parent, Is.SameAs(Host(1)));
            Assert.That(departure.DisplayName, Is.EqualTo(metadata.Single(d => d.RequiredRuleRef == "entrepreneur.face.P01").DisplayName));
            var placeObject = Row(place); Observe("1A两条各一列");
            Click((Button)Read(modules, "localDetailToggle")); yield return null;
            Refresh(); Click((Button)Read(modules, "localDetailToggle")); yield return null;
            Click((Button)Read(modules, "localDetailToggle")); yield return null;
            Assert.That(Row(place), Is.SameAs(placeObject)); Observe("原展开收回再展开与重复刷新");
            view = GameStateCloneService.DeepClone(view); view.StateRevision++; view.PlayerRules.Clear(); Refresh(); yield return null;
            var remaining = Entries(); Assert.That(remaining, Has.Length.EqualTo(1)); Assert.That(Row(remaining[0]), Is.SameAs(placeObject));
            Assert.That(Row(remaining[0]).transform.parent, Is.SameAs(Host(0))); Assert.That(Rows().Contains(departure.EntryId), Is.False); Observe("授权显示段移除后原右行重挂左列");
            view = GameStateCloneService.DeepClone(view); view.StateRevision++;
            view.PlayerRules.Add(new PlayerRuleFactView { FactId = "r5.rule1", SourceItemId = "r5.round1", RuleOwnerItemId = "r5.ability", RuleRef = "entrepreneur.face.P02", DetailPlayerId = 1 });
            Refresh(); yield return null; initial = Entries(); Assert.That(initial, Has.Length.EqualTo(2));
            departure = initial.Single(e => e.AbilityId == "entrepreneur.P09.departure"); place = initial.Single(e => e.AbilityId == "entrepreneur.P09.place");
            Assert.That(Row(place), Is.SameAs(placeObject)); Assert.That(Row(place).transform.parent, Is.SameAs(Host(1)));
            Assert.That(Row(departure).transform.parent, Is.SameAs(Host(0))); Observe("1B两条恢复后同一行重挂右列");
            localPlayer = 2; view = GameStateCloneService.DeepClone(view); view.ViewerPlayerId = 2; Refresh(); yield return null;
            Assert.That(Rows().Values.Cast<Component>().All(r => r.transform.parent != Host(0) && r.transform.parent != Host(1)), Is.True); Observe("切到玩家2不残留本人列");
            localPlayer = 1; view = GameStateCloneService.DeepClone(view); view.ViewerPlayerId = 1; Refresh(); yield return null;
            initial = Entries(); Assert.That(initial, Has.Length.EqualTo(2));
            Assert.That(Row(initial.Single(e => e.AbilityId == "entrepreneur.P09.departure")).transform.parent, Is.SameAs(Host(0)));
            Assert.That(Row(initial.Single(e => e.AbilityId == "entrepreneur.P09.place")).transform.parent, Is.SameAs(Host(1))); Observe("切回玩家1无重复行");
            Assert.That(submitted, Is.Zero); Save("有限生产刷新观察", new { snapshots, submitted, threeOrFourRowsObserved = false });
        }
        [UnityTearDown] public IEnumerator Finish()
        { if (hudObject != null) UnityEngine.Object.DestroyImmediate(hudObject); if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
