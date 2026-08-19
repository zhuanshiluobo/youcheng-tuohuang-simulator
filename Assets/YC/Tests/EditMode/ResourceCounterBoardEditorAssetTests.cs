using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class ResourceCounterBoardEditorAssetTests
    {
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/ResourceCounterBoard.prefab";
        private const string LayoutPath =
            "Assets/YC/Presentation/Content/ResourceCounterBoardLayoutProfile.asset";
        private const string VisualsPath =
            "Assets/YC/Presentation/Sprites/ResourceCounterVisuals.asset";

        [Test]
        public void Prefab_HasFixedOrderPersistentVisualsTopLeftAnchorAndNoRaycastBlockers()
        {
            var boardType = GetRuntimeType("YC.Presentation.ResourceCounterBoard");
            var profileType = GetRuntimeType("YC.Presentation.ResourceCounterBoardLayoutProfile");
            var visualsType = GetRuntimeType("YC.Presentation.ResourceCounterVisualLibrary");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var profile = AssetDatabase.LoadAssetAtPath(LayoutPath, profileType);
            var visuals = AssetDatabase.LoadAssetAtPath(VisualsPath, visualsType);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(profile, Is.Not.Null);
            Assert.That(visuals, Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(profile), Is.True);
            Assert.That(EditorUtility.IsPersistent(visuals), Is.True);

            var board = prefab.GetComponent(boardType);
            Assert.That(board, Is.Not.Null);
            AssertTryValidate(board);
            Assert.That(GetProperty<Object>(board, "LayoutProfile"), Is.SameAs(profile));
            var view = GetProperty<Component>(board, "View");
            Assert.That(GetProperty<Object>(view, "VisualLibrary"), Is.SameAs(visuals));
            var gears = GetProperty<Array>(view, "GearCounters");
            var auxiliaries = GetProperty<Array>(view, "AuxiliaryCounters");
            Assert.That(gears.Length, Is.EqualTo(3));
            Assert.That(auxiliaries.Length, Is.EqualTo(2));
            Assert.That(GetProperty<ResourceType>(gears.GetValue(0), "ResourceType"), Is.EqualTo(ResourceType.Originium));
            Assert.That(GetProperty<ResourceType>(gears.GetValue(1), "ResourceType"), Is.EqualTo(ResourceType.OriginiumShard));
            Assert.That(GetProperty<ResourceType>(gears.GetValue(2), "ResourceType"), Is.EqualTo(ResourceType.Iron));
            Assert.That(GetProperty<ResourceType>(auxiliaries.GetValue(0), "ResourceType"), Is.EqualTo(ResourceType.PureOriginium));
            Assert.That(GetProperty<ResourceType>(auxiliaries.GetValue(1), "ResourceType"), Is.EqualTo(ResourceType.GoldVoucher));

            var rect = GetProperty<RectTransform>(view, "Root");
            Assert.That(rect.anchorMin, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(rect.anchorMax, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(rect.pivot, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(432f, 222f)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(18f, -18f)));
            Assert.That(prefab.transform.Find("Resource Board Title"), Is.Null);
            var rulebookBoard = prefab.transform.Find("Rulebook Resource Counter");
            Assert.That(rulebookBoard, Is.Not.Null);
            Assert.That(rulebookBoard.GetComponent<Outline>(), Is.Null);
            Assert.That(rulebookBoard.GetComponent<Shadow>(), Is.Null);
            foreach (var label in new[] { "源岩", "源石", "异铁" })
            {
                var column = rulebookBoard.Find(label + " Mechanical Counter");
                Assert.That(column, Is.Not.Null, label);
                Assert.That(column.Find("Current " + label + " Plate"), Is.Not.Null, label);
                Assert.That(column.Find("Reserve 1 " + label + " Plate"), Is.Null, label);
                Assert.That(column.Find("Reserve 2 " + label + " Plate"), Is.Null, label);
                Assert.That(column.Find("Reserve 3 " + label + " Plate"), Is.Null, label);
                var tensLabel = column.Find("Tens And Ones Dial Deck/Tens Label");
                var onesLabel = column.Find("Tens And Ones Dial Deck/Ones Label");
                Assert.That(tensLabel, Is.Not.Null, label);
                Assert.That(onesLabel, Is.Not.Null, label);
                Assert.That(tensLabel.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(46f, 14f)), label);
                Assert.That(onesLabel.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(46f, 14f)), label);
                var tensGear = column.Find("Tens Numbered Gear");
                var onesGear = column.Find("Ones Numbered Gear");
                var tensCover = column.Find("Tens Gear Cover");
                var onesCover = column.Find("Ones Gear Cover");
                var tensRing = column.Find("Tens Readout Ring");
                var onesRing = column.Find("Ones Readout Ring");
                Assert.That(tensGear, Is.Not.Null, label);
                Assert.That(onesGear, Is.Not.Null, label);
                Assert.That(tensCover, Is.Not.Null, label);
                Assert.That(onesCover, Is.Not.Null, label);
                Assert.That(tensRing.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(14f, 14f)), label);
                Assert.That(onesRing.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(14f, 14f)), label);
                Assert.That(tensGear.GetComponent<RectTransform>().pivot, Is.EqualTo(Vector2.one * 0.5f), label);
                Assert.That(onesGear.GetComponent<RectTransform>().pivot, Is.EqualTo(Vector2.one * 0.5f), label);
                Assert.That(tensGear.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(54f, 54f)), label);
                Assert.That(onesGear.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(54f, 54f)), label);
                Assert.That(tensGear.childCount, Is.Zero, label);
                Assert.That(onesGear.childCount, Is.Zero, label);
                Assert.That(tensGear.GetComponent<Image>().sprite,
                    Is.SameAs(GetProperty<Sprite>(visuals, "NumberedGear")), label);
                Assert.That(onesGear.GetComponent<Image>().sprite,
                    Is.SameAs(GetProperty<Sprite>(visuals, "NumberedGear")), label);
                Assert.That(tensCover.GetComponent<Image>().sprite,
                    Is.SameAs(GetProperty<Sprite>(visuals, "DialCover")), label);
                Assert.That(onesCover.GetComponent<Image>().sprite,
                    Is.SameAs(GetProperty<Sprite>(visuals, "DialCover")), label);
                Assert.That(column.Find("Tens Value State").GetComponent<Text>().color.a, Is.Zero, label);
                Assert.That(column.Find("Ones Value State").GetComponent<Text>().color.a, Is.Zero, label);
                Assert.That(column.Find("Exact Amount Overflow Window"), Is.Null, label);
            }
            Assert.That(prefab.GetComponent<CanvasGroup>().blocksRaycasts, Is.False);
            foreach (var graphic in prefab.GetComponentsInChildren<Graphic>(true))
                Assert.That(graphic.raycastTarget, Is.False, graphic.name);

            foreach (var property in new[]
                     { "LargeGear", "SmallGear", "NumberedGear", "DialCover", "ReadoutRing", "Rivet", "Banknote" })
            {
                var sprite = GetProperty<Sprite>(visuals, property);
                Assert.That(sprite, Is.Not.Null, property);
                Assert.That(EditorUtility.IsPersistent(sprite), Is.True, property);
                Assert.That(AssetDatabase.GetAssetPath(sprite), Is.EqualTo(VisualsPath));
            }

            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject), Is.Zero);
        }

        [Test]
        public void Render_InitialIsImmediateAndAnimatedChangesRemainExactAcrossDigitBoundariesAndRetargeting()
        {
            var boardType = GetRuntimeType("YC.Presentation.ResourceCounterBoard");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var instance = Object.Instantiate(prefab);
            try
            {
                var board = instance.GetComponent(boardType);
                Render(board, Resources(9, 99, 12, 3, 7), true);
                AssertDigits(board, 0, "0", "9");
                AssertDigits(board, 1, "9", "9");
                AssertDigits(board, 2, "1", "2");
                AssertAuxiliaryAmounts(board, "3", "7");

                var initialAngle = MainGear(board, 0).localEulerAngles.z;
                var initialPosition = MainGear(board, 0).anchoredPosition;
                Render(board, Resources(10, 100, 11, 4, 8), true);
                Advance(board, 0.18f);
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(initialAngle, MainGear(board, 0).localEulerAngles.z)),
                    Is.GreaterThan(0.001f));
                Assert.That(MainGear(board, 0).anchoredPosition, Is.EqualTo(initialPosition));
                Render(board, Resources(25, 101, 10, 5, 9), true);
                Advance(board, 1f);
                AssertDigits(board, 0, "2", "5");
                AssertDigits(board, 1, "0", "1");
                AssertDigits(board, 2, "1", "0");
                AssertAuxiliaryAmounts(board, "5", "9");

                var settledAngle = MainGear(board, 0).localEulerAngles.z;
                Render(board, Resources(25, 101, 10, 5, 9), true);
                Advance(board, 1f);
                Assert.That(MainGear(board, 0).localEulerAngles.z, Is.EqualTo(settledAngle).Within(0.001f));

                Render(board, Resources(9, 99, 9, 5, 9), false);
                Render(board, Resources(10, 100, 8, 5, 9), true);
                Advance(board, 1f);
                AssertAuxiliaryAmounts(board, "5", "9");
                AssertDigits(board, 0, "1", "0");
                AssertDigits(board, 1, "0", "0");
                AssertDigits(board, 2, "0", "8");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void NumberedGear_EachPrintedDigitRotatesIntoTheSameFixedWindow()
        {
            var visuals = AssetDatabase.LoadAssetAtPath(VisualsPath,
                GetRuntimeType("YC.Presentation.ResourceCounterVisualLibrary"));
            var sprite = GetProperty<Sprite>(visuals, "NumberedGear");
            var texture = sprite.texture;
            Assert.That(texture.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(GetProperty<Sprite>(visuals, "DialCover").texture.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(GetProperty<Sprite>(visuals, "ReadoutRing").texture.filterMode, Is.EqualTo(FilterMode.Point));
            var center = (texture.width - 1) * 0.5f;
            const float windowY = 84f;

            for (var y = 0; y < texture.height; y++)
            for (var x = 0; x < texture.width; x++)
            {
                var color = texture.GetPixel(x, y);
                if (color.a <= 0.5f) continue;
                Assert.That(Mathf.Max(color.r, Mathf.Max(color.g, color.b)), Is.GreaterThan(0.35f),
                    "编号齿轮不应包含黑色轴孔、凹槽或阴影装饰。");
            }

            for (var digit = 0; digit < 10; digit++)
            {
                var angle = -digit * 36f * Mathf.Deg2Rad;
                var cosine = Mathf.Cos(angle);
                var sine = Mathf.Sin(angle);
                var printedPixelsInWindow = 0;
                for (var y = 0; y < texture.height; y++)
                for (var x = 0; x < texture.width; x++)
                {
                    var color = texture.GetPixel(x, y);
                    if (color.r < 0.55f || color.r < color.g * 2f) continue;

                    var localX = x - center;
                    var localY = y - center;
                    var rotatedX = localX * cosine - localY * sine;
                    var rotatedY = localX * sine + localY * cosine;
                    var windowOffset = new Vector2(rotatedX, rotatedY - windowY);
                    if (windowOffset.sqrMagnitude <= 22f * 22f)
                    {
                        printedPixelsInWindow++;
                    }
                }

                Assert.That(printedPixelsInWindow, Is.GreaterThan(80),
                    "齿轮旋转到数字 " + digit + " 时，固定圆形开口没有露出该数字。");
            }
        }

        [Test]
        public void SinglePlayerCollection_UpdatesTensAndOnesReadoutsFromCollectedResources()
        {
            var map = new GameMapDefinition
            {
                MapId = "resource-counter-single-player",
                MinPlayers = 1,
                MaxPlayers = 4,
                Locations =
                {
                    new MapLocationDefinition
                    {
                        LocationId = "city-mine",
                        ResourceType = ResourceType.OriginiumShard,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    }
                }
            };
            var state = new GameState
            {
                Phase = GamePhase.ResourceCollection,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "city-mine"
                    }
                }
            };
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "city-mine",
                ResourceType = ResourceType.OriginiumShard,
                Amount = 27
            });
            var handler = new CollectResourceCommandHandler(
                new ResourceCollectionService(new MapQueryService(map), new ResourceTokenService()));
            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "city-mine"
            });
            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var instance = Object.Instantiate(prefab);
            try
            {
                var board = instance.GetComponent(GetRuntimeType("YC.Presentation.ResourceCounterBoard"));
                Render(board, state.FindPlayer(1).Resources, false);
                AssertDigits(board, 1, "2", "7");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private static void AssertDigits(Component board, int index, string tens, string ones)
        {
            var view = GetProperty<Component>(board, "View");
            var gears = GetProperty<Array>(view, "GearCounters");
            Assert.That(GetProperty<Text>(gears.GetValue(index), "TensDigitText").text, Is.EqualTo(tens));
            Assert.That(GetProperty<Text>(gears.GetValue(index), "OnesDigitText").text, Is.EqualTo(ones));
        }

        private static ResourceSet Resources(int originium, int shard, int iron, int pure, int voucher)
        {
            return new ResourceSet
            {
                Originium = originium,
                OriginiumShard = shard,
                Iron = iron,
                PureOriginium = pure,
                GoldVoucher = voucher
            };
        }

        private static void Render(Component board, ResourceSet resources, bool animate)
        {
            board.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(board, new object[] { resources, animate });
        }

        private static void Advance(Component board, float deltaTime)
        {
            board.GetType().GetMethod("AdvanceAnimations", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(board, new object[] { deltaTime });
        }

        private static RectTransform MainGear(Component board, int index)
        {
            var view = GetProperty<Component>(board, "View");
            var gears = GetProperty<Array>(view, "GearCounters");
            return GetProperty<RectTransform>(gears.GetValue(index), "MainGear");
        }

        private static void AssertAuxiliaryAmounts(
            Component board,
            string pure,
            string voucher)
        {
            var view = GetProperty<Component>(board, "View");
            var auxiliaries = GetProperty<Array>(view, "AuxiliaryCounters");
            Assert.That(GetProperty<Text>(auxiliaries.GetValue(0), "AmountText").text, Is.EqualTo(pure));
            Assert.That(GetProperty<Text>(auxiliaries.GetValue(1), "AmountText").text, Is.EqualTo(voucher));
        }

        private static Type GetRuntimeType(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, name);
            return type;
        }

        private static T GetProperty<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, target.GetType().Name + "." + name);
            return (T)property.GetValue(target, null);
        }

        private static void AssertTryValidate(object target)
        {
            var arguments = new object[] { string.Empty };
            var result = target.GetType().GetMethod("TryValidateConfiguration", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(target, arguments);
            Assert.That(result, Is.EqualTo(true), arguments[0] as string);
        }
    }
}
