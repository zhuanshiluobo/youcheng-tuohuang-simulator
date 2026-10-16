using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 隔离测试的原生组件壳。会话与确定性模拟由调用方装配，不创建旧网络通道。
    internal sealed class SimulationUiTestShell
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<GameObject> objects = new List<GameObject>();
        private Scenario current;
        private static Type Production(string name) => Assembly.Load("Assembly-CSharp").GetType(name, true);
        private static Type MirrorType(string name) => Assembly.Load("Mirror").GetType("Mirror." + name, true);
        internal static GameState ReadState(GameState simulation, GameStateView view, int viewer) =>
            (GameState)Production("YC.Presentation.PlayerUiReadStateFactory").GetMethod("Create", Statics)
                .Invoke(null, new object[] { simulation, view, viewer });
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType().GetField(name, Members).GetValue(target);
        private object Component(Type type)
        {
            var gameObject = new GameObject("确定性模拟组件检查");
            gameObject.SetActive(false); objects.Add(gameObject);
            return gameObject.AddComponent(type);
        }
        private sealed class Scenario
        {
            public object Transport, Mobile, Controller, ServerConnection;
            public object PreviousConnection, PreviousConnectState, PreviousActiveTransport;
            public bool ConnectionCaptured, ActiveTransportCaptured;
        }
        private Scenario Create()
        {
            var scenario = current = new Scenario();
            scenario.Transport = Component(Production("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
            scenario.Mobile = Component(Production("YC.Presentation.MobileCityInteractionController"));
            var hud = Component(Production("YC.Presentation.GameplayInteractionHudView"));
            var expansion = Component(Production("YC.Presentation.GameplayExpansionUi"));
            Set(hud, "expansionUi", expansion);
            Set(scenario.Mobile, "gameplayInteractionHud", hud);
            Set(scenario.Mobile, "pendingEffectCommandId", string.Empty);
            Set(scenario.Mobile, "coverIntent", new InteractionCommandIntent());
            var frame = Component(Production("YC.Presentation.GameplayHudFrame"));
            var panel = Component(Production("YC.Presentation.ActionPanelView"));
            var buttonType = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.Button", true);
            Set(frame, "endActionButton", Component(buttonType)); Set(panel, "endRoundButton", Component(buttonType));
            Set(hud, "frame", frame); Set(hud, "actionPanelView", panel);
            var activeTransport = MirrorType("Transport").GetField("active", Statics);
            scenario.PreviousActiveTransport = activeTransport.GetValue(null);
            scenario.ActiveTransportCaptured = true;
            activeTransport.SetValue(null, Component(Assembly.Load("Mirror.Transports").GetType("Mirror.TelepathyTransport", true)));
            scenario.Controller = Activator.CreateInstance(Production("YC.Presentation.CommandSubmissionController"), new object[] {
                new GameSession(new GameState()), null, 1, null, new Action(() => { }), new Action<string>(_ => { }),
                new Action<string>(_ => { }), new Action<string, bool>((_, __) => { }),
                new Action<string, string, bool, CommandErrorCode>((_, __, ___, ____) => { }) });
            return scenario;
        }
        private static void BindRealLocalConnection(Scenario scenario)
        {
            var client = MirrorType("NetworkClient");
            Assert.That(client.GetProperty("isConnected", Statics).GetValue(null), Is.EqualTo(false), "检查不接管已有连接。");
            Assert.That(MirrorType("NetworkServer").GetProperty("active", Statics).GetValue(null), Is.EqualTo(false));
            scenario.PreviousConnection = client.GetProperty("connection", Statics).GetValue(null);
            scenario.PreviousConnectState = client.GetField("connectState", Statics).GetValue(null);
            scenario.ConnectionCaptured = true;
            var connection = Activator.CreateInstance(MirrorType("LocalConnectionToServer"));
            scenario.ServerConnection = Activator.CreateInstance(MirrorType("LocalConnectionToClient"));
            Set(connection, "connectionToClient", scenario.ServerConnection); Set(scenario.ServerConnection, "connectionToServer", connection);
            client.GetProperty("connection", Statics).SetValue(null, connection);
            client.GetField("connectState", Statics).SetValue(null, Enum.Parse(MirrorType("ConnectState"), "Connected"));
        }
        private static Func<T> ControllerFactory<T>(object controller) => () => (T)controller;
        public void Clear()
        {
            (current?.Controller as IDisposable)?.Dispose();
            if (current?.ConnectionCaptured == true)
            {
                var client = MirrorType("NetworkClient");
                client.GetProperty("connection", Statics).SetValue(null, current.PreviousConnection);
                client.GetField("connectState", Statics).SetValue(null, current.PreviousConnectState);
                var queue = Get(current.ServerConnection, "queue");
                while ((int)queue.GetType().GetProperty("Count").GetValue(queue) > 0)
                    ((IDisposable)queue.GetType().GetMethod("Dequeue").Invoke(queue, null)).Dispose();
            }
            if (current?.ActiveTransportCaptured == true)
                MirrorType("Transport").GetField("active", Statics).SetValue(null, current.PreviousActiveTransport);
            foreach (var gameObject in objects) if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            objects.Clear(); current = null;
        }
    }
}
