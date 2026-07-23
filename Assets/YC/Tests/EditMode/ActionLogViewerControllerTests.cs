using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ActionLogViewerControllerTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void BuildDisplayEntries_IncludesAllPlayersAndSortsOldestFirst()
        {
            var state = CreateState();
            state.Logs.Add(new GameLogEntry { Sequence = 2, PlayerId = 2, Message = "second" });
            state.Logs.Add(new GameLogEntry { Sequence = 1, PlayerId = 1, Message = "first" });

            var entries = BuildDisplayEntries(state);

            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(GetProperty(entries[0], "Sequence"), Is.EqualTo(1));
            Assert.That(GetProperty(entries[0], "PlayerLabel"), Is.EqualTo("Alice"));
            Assert.That(GetProperty(entries[1], "PlayerLabel"), Is.EqualTo("Bob"));
        }

        [Test]
        public void Open_EmptyStateShowsEmptyMessage_AndCloseHidesViewer()
        {
            var session = new GameSession(CreateState());
            var viewer = CreateViewer(session);

            Invoke(viewer, "Open");

            Assert.That(GetProperty(viewer, "IsOpen"), Is.True);
            var emptyText = GameObject.Find("Action Log Row Text").GetComponent<UnityEngine.UI.Text>();
            Assert.That(emptyText.text, Is.EqualTo("暂无成功行动记录"));

            Invoke(viewer, "Close");
            Assert.That(GetProperty(viewer, "IsOpen"), Is.False);
        }

        [Test]
        public void Open_AfterClientSnapshotReplacementReadsSynchronizedLogs()
        {
            var session = new GameSession(CreateState());
            var viewer = CreateViewer(session);
            var synchronizedState = CreateState();
            synchronizedState.Logs.Add(new GameLogEntry { Sequence = 7, PlayerId = 2, Message = "host confirmed" });

            session.ReplaceState(synchronizedState);
            Invoke(viewer, "Open");

            var entries = BuildDisplayEntries(session.State);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(GetProperty(entries[0], "PlayerLabel"), Is.EqualTo("Bob"));
            Assert.That(GetProperty(entries[0], "Message"), Is.EqualTo("host confirmed"));
        }

        [Test]
        public void BuildDisplayEntries_HidesVagueLegacyEntriesAndLocalizesPublicActions()
        {
            var state = CreateState();
            state.Logs.Add(new GameLogEntry
            {
                Sequence = 1,
                PlayerId = 1,
                Message = "Player 1 ended their action."
            });
            state.Logs.Add(new GameLogEntry
            {
                Sequence = 2,
                PlayerId = 1,
                Message = "\u73a9\u5bb6 1 \u5df2\u76d6\u653e\u89d2\u8272\u724c\u3002"
            });
            state.Logs.Add(new GameLogEntry
            {
                Sequence = 3,
                PlayerId = 2,
                Message = "Player 2 built \u62a4\u822a\u8c03\u5ea6\u4e2d\u5fc3."
            });

            var entries = BuildDisplayEntries(state);

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(GetProperty(entries[0], "PlayerLabel"), Is.EqualTo("Bob"));
            Assert.That(GetProperty(entries[0], "Message"), Is.EqualTo("\u5efa\u9020\u4e86\u5efa\u7b51\u201c\u62a4\u822a\u8c03\u5ea6\u4e2d\u5fc3\u201d\u3002"));
        }

        [Test]
        public void Open_UsesWideSingleLineRowsAndFastDraggableScrollbar()
        {
            var state = CreateState();
            state.Logs.Add(new GameLogEntry
            {
                Sequence = 1,
                PlayerId = 1,
                Message = "\u5efa\u9020\u4e86\u5efa\u7b51\u201c\u62a4\u822a\u8c03\u5ea6\u4e2d\u5fc3\u201d\u3002"
            });
            var viewer = CreateViewer(new GameSession(state));

            Invoke(viewer, "Open");

            var panel = GameObject.Find("Action Log Panel").GetComponent<RectTransform>();
            var scroll = GameObject.Find("Action Log Scroll View").GetComponent<ScrollRect>();
            var rowText = GameObject.Find("Action Log Row Text").GetComponent<Text>();
            Assert.That(panel.sizeDelta.x, Is.EqualTo(864f).Within(0.01f));
            Assert.That(scroll.scrollSensitivity, Is.EqualTo(60f).Within(0.01f));
            Assert.That(scroll.verticalScrollbar, Is.Not.Null);
            Assert.That(scroll.verticalScrollbar.direction, Is.EqualTo(Scrollbar.Direction.BottomToTop));
            Assert.That(rowText.text, Is.EqualTo("Alice \u5efa\u9020\u4e86\u5efa\u7b51\u201c\u62a4\u822a\u8c03\u5ea6\u4e2d\u5fc3\u201d\u3002"));
            Assert.That(rowText.text, Does.Not.Contain("\n"));
        }

        private object CreateViewer(GameSession session)
        {
            owner = new GameObject("Action Log Viewer Tests");
            var viewer = owner.AddComponent(GetControllerType());
            Invoke(viewer, "Configure", new Func<GameState>(() => session.State));
            return viewer;
        }

        private static IList BuildDisplayEntries(GameState state)
        {
            var method = GetControllerType().GetMethod("BuildDisplayEntries", BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return (IList)method.Invoke(null, new object[] { state });
        }

        private static Type GetControllerType()
        {
            var type = Type.GetType("YC.Presentation.ActionLogViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);
            return type;
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(target, args);
        }

        private static object GetProperty(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
            {
                return property.GetValue(target, null);
            }

            var field = target.GetType().GetField(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(target);
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, Name = "Alice", Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 2, Name = "Bob", Color = PlayerColor.Red }
                }
            };
        }
    }
}
