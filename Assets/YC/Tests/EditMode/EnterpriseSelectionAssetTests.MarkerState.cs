using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed partial class EnterpriseSelectionAssetTests
    {
        [Test]
        public void PublicEnterpriseProjectionUsesActualMissingMarkerAndConfiguredCopy_ComponentOnly()
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var rules = typeof(ExpansionRules.EnterpriseEffectTests);
            var state = (GameState)rules.GetMethod("State", flags).Invoke(null, new object[] { 3, 0, 1, null, 2 });
            const string definition = "test.enterprise.business";
            rules.GetMethod("RemoveCooperationMarkerForIsolation", flags).Invoke(null, new object[] { state, definition, 1 });
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            string enterprise = state.ItemZones.Items.Find(i => i.DefinitionId == definition).ItemId;
            string before = JsonConvert.SerializeObject(state);
            var root = new GameObject("实际公开轨道投影隔离验证");
            try
            {
                var adapter = root.AddComponent(T("GameplayExpansionUi"));
                var copy = Get<object>(adapter, "Copy");
                const string configured = "隔离动态缺标记配置";
                copy.GetType().GetField("MissingCooperationMarker").SetValue(copy, configured);
                var board = (EnterpriseBoardProjection)Call(adapter, "ProjectBoard", visible, enterprise, definition);
                Assert.That(board.PlayerLevels[Array.IndexOf(board.PlayerIds, 1)], Is.EqualTo(-1));
                Assert.That(board.PlayerLevels[Array.IndexOf(board.PlayerIds, 2)], Is.EqualTo(1));
                Assert.That(board.MissingMarkerLabel, Is.EqualTo(configured));
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void ExistingPrefabShowsConfiguredMissingMarkerStatusAndKeepsItsMarkerHidden_ComponentOnly()
        {
            var root = new GameObject("缺标记动态显示隔离验证", typeof(RectTransform), typeof(Canvas));
            try
            {
                var page = Load(false, root.transform);
                page.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
                var data = Data(EnterpriseSelectionMode.Company, 1);
                data.ViewerPlayerId = 1;
                var board = data.Boards[0];
                board.PlayerIds = new[] { 1, 2 }; board.PlayerColors = new[] { 0, 1 }; board.PlayerLevels = new[] { -1, 0 };
                board.MissingMarkerLabel = "隔离用可配置状态";
                Call(page, "Present", data, false); Call(page, "Choose", "t0");
                var row = (Component)((IDictionary)page.GetType().GetField("items", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page))["b0"];
                var colors = Field<int[]>(row, "playerColorOrder");
                var levels = Field<Text[]>(row, "playerLevels");
                var markers = (Array)row.GetType().GetField("playerLevelMarkers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(row);
                for (int i = 0; i < levels.Length; i++)
                {
                    int color = colors != null && i < colors.Length ? colors[i] : i;
                    int index = Array.IndexOf(board.PlayerColors, color);
                    Assert.That(levels[i].transform.parent.gameObject.activeSelf, Is.EqualTo(index >= 0));
                    if (index >= 0) Assert.That(levels[i].text, Is.EqualTo(index == 0 ? board.MissingMarkerLabel : "0"));
                }
                for (int i = 0; i < markers.Length; i++)
                {
                    int color = colors != null && i < colors.Length ? colors[i] : i;
                    Assert.That(((Component)markers.GetValue(i)).gameObject.activeSelf, Is.EqualTo(Array.IndexOf(board.PlayerColors, color) == 1));
                }
                // 当前预制体允许只用文字。另在内存实例上配置一枚缺标记图形，验证共同隐藏分支。
                var markerField = row.GetType().GetField("playerLevelMarkers", BindingFlags.Instance | BindingFlags.NonPublic);
                var isolatedMarker = new GameObject("隔离缺失标记图形"); isolatedMarker.transform.SetParent(row.transform, false);
                var isolatedMarkers = Array.CreateInstance(markerField.FieldType.GetElementType(), 1);
                isolatedMarkers.SetValue(isolatedMarker.AddComponent(markerField.FieldType.GetElementType()), 0);
                markerField.SetValue(row, isolatedMarkers);
                row.GetType().GetField("playerColorOrder", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(row, new[] { 0 });
                var catalog = page.GetType().GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                Call(row, "Bind", board, catalog, Get<EnterpriseSelectionDraft>(page, "Draft"), null, null);
                Assert.That(isolatedMarker.activeSelf, Is.False);
                var detail = Field<Text>(page, "cooperationLevel");
                Assert.That(detail.gameObject.activeSelf, Is.True);
                Assert.That(detail.text, Is.EqualTo(board.MissingMarkerLabel));
                Assert.That(Get<EnterpriseSelectionDraft>(page, "Draft").Projection.Boards[0].PlayerLevels, Is.EqualTo(new[] { -1, 0 }));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
