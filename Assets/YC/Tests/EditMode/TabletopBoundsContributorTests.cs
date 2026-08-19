using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class TabletopBoundsContributorTests
    {
        [Test]
        public void AppendWorldCorners_CollectsEveryConfiguredRectAndSkipsNulls()
        {
            var contributorType = Type.GetType(
                "YC.Presentation.TabletopBoundsContributor, Assembly-CSharp",
                false);
            Assert.That(contributorType, Is.Not.Null);
            var owner = new GameObject("Tabletop Bounds Test", typeof(RectTransform));
            var second = new GameObject("Second Tabletop Rect", typeof(RectTransform));
            try
            {
                var ownerRect = owner.GetComponent<RectTransform>();
                ownerRect.sizeDelta = new Vector2(10f, 6f);
                ownerRect.position = new Vector3(2f, 3f, 0f);
                var secondRect = second.GetComponent<RectTransform>();
                secondRect.sizeDelta = new Vector2(4f, 8f);
                secondRect.position = new Vector3(-10f, 1f, 0f);

                var contributor = owner.AddComponent(contributorType);
                contributorType.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(contributor, new object[] { new[] { ownerRect, null, secondRect } });
                var corners = new List<Vector3>();

                contributorType.GetMethod("AppendWorldCorners", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(contributor, new object[] { corners });

                Assert.That(corners, Has.Count.EqualTo(8));
                Assert.That(corners, Does.Contain(new Vector3(-3f, 0f, 0f)));
                Assert.That(corners, Does.Contain(new Vector3(7f, 6f, 0f)));
                Assert.That(corners, Does.Contain(new Vector3(-12f, -3f, 0f)));
                Assert.That(corners, Does.Contain(new Vector3(-8f, 5f, 0f)));
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(owner);
            }
        }
    }
}
