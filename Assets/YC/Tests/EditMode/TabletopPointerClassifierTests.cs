using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class TabletopPointerClassifierTests
    {
        private readonly List<GameObject> owners = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (var i = owners.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(owners[i]);
            }
            owners.Clear();
        }

        [Test]
        public void TopUiHit_InTabletopCanvas_DoesNotBlockCameraInput()
        {
            var tabletop = CreateUiRaycastRoot("Tabletop", true);
            var card = new GameObject("Tabletop Card", typeof(RectTransform));
            owners.Add(card);
            card.transform.SetParent(tabletop.Root.transform, false);

            var blocked = Classify(new RaycastResult
            {
                gameObject = card,
                module = tabletop.Raycaster
            });

            Assert.That(blocked, Is.False);
        }

        [Test]
        public void TopUiHit_InFlatHud_BlocksCameraInput()
        {
            var flatHud = CreateUiRaycastRoot("Flat HUD", false);
            var panel = new GameObject("Action Panel", typeof(RectTransform));
            owners.Add(panel);
            panel.transform.SetParent(flatHud.Root.transform, false);

            var blocked = Classify(new RaycastResult
            {
                gameObject = panel,
                module = flatHud.Raycaster
            });

            Assert.That(blocked, Is.True);
        }

        [Test]
        public void NoUiHit_DoesNotBlockCameraInput()
        {
            Assert.That(Classify(), Is.False);
        }

        [Test]
        public void NonUiHitBeforeTabletopUi_IsIgnored()
        {
            var tabletop = CreateUiRaycastRoot("Tabletop", true);
            var card = new GameObject("Tabletop Card", typeof(RectTransform));
            owners.Add(card);
            card.transform.SetParent(tabletop.Root.transform, false);
            var physicsHit = new GameObject("Physics Hit");
            owners.Add(physicsHit);

            var blocked = Classify(
                new RaycastResult { gameObject = physicsHit },
                new RaycastResult { gameObject = card, module = tabletop.Raycaster });

            Assert.That(blocked, Is.False);
        }

        private (GameObject Root, GraphicRaycaster Raycaster) CreateUiRaycastRoot(
            string name,
            bool tabletop)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            owners.Add(root);
            if (tabletop)
            {
                root.AddComponent(GetRuntimeType("YC.Presentation.TabletopCanvasLayout"));
            }

            return (root, root.GetComponent<GraphicRaycaster>());
        }

        private static bool Classify(params RaycastResult[] hits)
        {
            var classifier = GetRuntimeType("YC.Presentation.TabletopPointerClassifier");
            var method = classifier.GetMethod(
                "IsBlockedByTopUiHit",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { hits });
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing runtime type " + fullName + ".");
            return type;
        }
    }
}
