using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class PlayerJourneyBoundaryTests
    {
        [Test]
        public void PJ000_PlayerDriverDependsOnlyOnVisibleUiAndEventSystem()
        {
            var root = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/PlayerJourney");
            foreach (var path in Directory.GetFiles(root, "*.cs"))
            {
                var source = File.ReadAllText(path);
                foreach (var forbidden in new[] { "YC.Domain", "YC.Application", "YC.Infrastructure", "GameState", "PlayerState", "EffectRuntimeState",
                    "GameCommand", "GameSession", "RoundExecutionService", "EffectTreeExecutor", "System.Reflection", ".GetField(", ".GetMethod(",
                    "SendMessage(", "BroadcastMessage(", "onClick.Invoke(" })
                    Assert.That(source, Does.Not.Contain(forbidden), path + " 越过玩家可见边界：" + forbidden);
            }
            var assembly = File.ReadAllText(Path.Combine(root, "YC.PlayerJourney.asmdef"));
            Assert.That(assembly, Does.Not.Contain("YC.Domain"));
            Assert.That(assembly, Does.Not.Contain("YC.Application"));
            Assert.That(assembly, Does.Not.Contain("YC.Infrastructure"));
        }
    }
}
