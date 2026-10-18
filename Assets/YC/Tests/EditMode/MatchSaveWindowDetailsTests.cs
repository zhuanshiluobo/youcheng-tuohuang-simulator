using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;

namespace YC.Tests.EditMode
{
    // 隔离展示回归：使用真实预制体和 v2 存档结构，不替代正常流程画面验收。
    public sealed class MatchSaveWindowDetailsTests
    {
        [TestCase("当前行动者", "席位名")]
        [TestCase("", "席位名")]
        [TestCase("", "")]
        public void DetailsReadV2ArchiveAndKeepCurrentPlayerSeparateFromLocalResources(string name, string seatName)
        {
            var sessions = DeterministicSimulationCoordinatorTests.CreateFormalSessions(out _, true);
            var data = MatchSaveResumeTests.FromSession(sessions[0], LaunchMode.Local, 1);
            var archive = JObject.Parse(data.ArchiveJson);
            var state = archive["Snapshot"]["State"];
            state["ActionRound"] = 2;
            state["CurrentPlayerId"] = 2;
            var players = (JArray)state["Players"];
            var current = players.Single(player => (int)player["PlayerId"] == 2);
            current["Name"] = name;
            data.Seats.Single(seat => seat.PlayerId == 2).PlayerName = seatName;
            var local = players.Single(player => (int)player["PlayerId"] == 1);
            local["Resources"] = new JObject { ["Originium"] = 11, ["OriginiumShard"] = 12,
                ["Iron"] = 13, ["PureOriginium"] = 14, ["GoldVoucher"] = 15 };
            current["Resources"] = new JObject { ["Originium"] = 91, ["OriginiumShard"] = 92,
                ["Iron"] = 93, ["PureOriginium"] = 94, ["GoldVoucher"] = 95 };
            data.ArchiveJson = archive.ToString(Formatting.None);
            data.Validate();
            string before = JsonConvert.SerializeObject(data);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Resources/MatchSaveWindow.prefab");
            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var type = Type.GetType("YC.Presentation.MatchSaveWindowView, Assembly-CSharp", true);
                var view = root.GetComponent(type);
                var refresh = type.GetMethod("RefreshRowDetails", BindingFlags.NonPublic | BindingFlags.Instance);
                var names = Field<Text[]>(view, "rowPlayerNames");
                var icons = Field<Image[]>(view, "rowPlayerIcons");
                var resources = Field<Text[]>(view, "rowResources");
                var rows = Field<Button[]>(view, "rows");
                Assert.That(names.Length, Is.EqualTo(rows.Length));
                Assert.That(icons.Length, Is.EqualTo(rows.Length));
                Assert.That(resources.Length, Is.EqualTo(rows.Length));
                string expectedName = name.Length > 0 ? name : seatName.Length > 0 ? seatName
                    : string.Format(Field<string>(view, "unnamedPlayerFormat"), 2);
                for (int index = 0; index < rows.Length; index++)
                {
                    Assert.That((int)refresh.Invoke(view, new object[] { index, data }), Is.EqualTo(2));
                    Assert.That(names[index].text, Is.EqualTo(expectedName));
                    Assert.That(icons[index].sprite, Is.SameAs(Field<Sprite[]>(view, "playerMarkerSprites")[(int)current["Color"]]));
                    Assert.That(icons[index].sprite, Is.Not.Null);
                    Assert.That(resources[index].text,
                        Is.EqualTo(string.Format(Field<string>(view, "resourceFormat"), 11, 12, 13, 14, 15)));
                    Assert.That(names[index].gameObject.activeSelf && icons[index].gameObject.activeSelf && resources[index].gameObject.activeSelf, Is.True);
                    refresh.Invoke(view, new object[] { index, null });
                    Assert.That(names[index].text, Is.Empty);
                    Assert.That(resources[index].text, Is.Empty);
                    Assert.That(icons[index].sprite, Is.Null);
                    Assert.That(names[index].gameObject.activeSelf || icons[index].gameObject.activeSelf || resources[index].gameObject.activeSelf, Is.False);
                }
                Assert.That(JsonConvert.SerializeObject(data), Is.EqualTo(before), "预览不得改变规则原包及联机确认边界");
                Assert.That(type.GetMethod("OpenSaveFailure", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static T Field<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    }
}
