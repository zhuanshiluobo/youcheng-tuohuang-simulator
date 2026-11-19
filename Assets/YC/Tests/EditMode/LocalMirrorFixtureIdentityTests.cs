using System;
using System.Reflection;
using NUnit.Framework;

namespace YC.Tests.EditMode
{
    public sealed class LocalMirrorFixtureIdentityTests
    {
        private static object Read(params string[] arguments) => Type.GetType("YC.Infrastructure.Multiplayer.LocalMirrorRoomService, Assembly-CSharp", true)
            .GetMethod("ReadFixtureOperatorIdentity", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { arguments });

        [Test]
        public void FixtureIdentityRequiresExplicitLocalTestAndStableGuid()
        {
            const string id = "0123456789abcdef0123456789abcdef";
            Assert.That(Read("--yc-local-multiplayer-test"), Is.Null);
            Assert.That(Read("--yc-local-multiplayer-test", "--yc-test-operator-id=" + id), Is.EqualTo(id));
            var missingMode = Assert.Throws<TargetInvocationException>(() => Read("--yc-test-operator-id=" + id));
            Assert.That(missingMode.InnerException, Is.TypeOf<InvalidOperationException>());
            var invalid = Assert.Throws<TargetInvocationException>(() => Read("--yc-local-multiplayer-test", "--yc-test-operator-id=bad"));
            Assert.That(invalid.InnerException, Is.TypeOf<InvalidOperationException>());
            var duplicate = Assert.Throws<TargetInvocationException>(() => Read("--yc-local-multiplayer-test", "--yc-test-operator-id=" + id, "--yc-test-operator-id=" + id));
            Assert.That(duplicate.InnerException, Is.TypeOf<InvalidOperationException>());
        }
    }
}
