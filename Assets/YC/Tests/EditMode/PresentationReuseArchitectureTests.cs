using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation.Maps;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class PresentationReuseArchitectureTests
    {
        [Test]
        public void EffectDialogs_ShareShellAndResourceAllocationModel()
        {
            var characterDialog = Type.GetType(
                "YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp",
                false);
            var facilityDialog = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            var shell = Type.GetType("YC.Presentation.EffectDialogShell, Assembly-CSharp", false);
            var allocation = Type.GetType(
                "YC.Presentation.ResourceAllocationSpec, Assembly-CSharp",
                false);

            Assert.That(characterDialog, Is.Not.Null);
            Assert.That(facilityDialog, Is.Not.Null);
            Assert.That(shell, Is.Not.Null);
            Assert.That(allocation, Is.Not.Null);
            Assert.That(
                characterDialog.GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(shell));
            Assert.That(
                facilityDialog.GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(shell));
            Assert.That(allocation.GetField("UnitPrices"), Is.Not.Null);
            Assert.That(allocation.GetField("FormatSummary"), Is.Not.Null);
        }

        [Test]
        public void FacilityOrBranches_KeepSharedCoordinatorHelperAndDialogShell()
        {
            var coordinator = Type.GetType(
                "YC.Presentation.FacilityEffectInteractionUiCoordinator, Assembly-CSharp",
                false);
            var dialog = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            var option = Type.GetType("YC.Presentation.EffectDialogOption, Assembly-CSharp", false);
            var shell = Type.GetType("YC.Presentation.EffectDialogShell, Assembly-CSharp", false);
            Assert.That(coordinator, Is.Not.Null);
            Assert.That(dialog, Is.Not.Null);
            Assert.That(option, Is.Not.Null);
            Assert.That(shell, Is.Not.Null);

            var sharedBranchPresenter = coordinator.GetMethod(
                "ShowOrBranchOptions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var showMercenary = coordinator.GetMethod(
                "ShowMercenary",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var showWarehouse = coordinator.GetMethod(
                "ShowWarehouse",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var showEffectOptions = dialog.GetMethod(
                "ShowEffectOptions",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(sharedBranchPresenter, Is.Not.Null,
                "带“或”的设施分支必须保留统一的 ShowOrBranchOptions 入口。");
            Assert.That(showMercenary, Is.Not.Null);
            Assert.That(showWarehouse, Is.Not.Null);
            Assert.That(showEffectOptions, Is.Not.Null);

            var parameters = sharedBranchPresenter.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(4));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PendingCardSessionState)));
            Assert.That(parameters[2].ParameterType.IsGenericType, Is.True);
            Assert.That(
                parameters[2].ParameterType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(IReadOnlyList<>)));
            Assert.That(parameters[2].ParameterType.GetGenericArguments()[0], Is.EqualTo(option));
            Assert.That(parameters[3].ParameterType, Is.EqualTo(typeof(bool)));

            Assert.That(CallsMethod(showMercenary, sharedBranchPresenter), Is.True,
                "佣兵指挥部必须通过统一“或”分支入口呈现一级选择。");
            Assert.That(CallsMethod(showWarehouse, sharedBranchPresenter), Is.True,
                "载具仓库必须通过统一“或”分支入口呈现一级选择。");
            Assert.That(CallsMethod(sharedBranchPresenter, showEffectOptions), Is.True,
                "统一“或”分支入口必须落到共用的可折叠选项弹窗。");
            Assert.That(
                dialog.GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(shell));
        }

        [Test]
        public void CityBoardSlotLayout_ProvidesSharedTwelveSlotGeometry()
        {
            var type = Type.GetType("YC.Presentation.CityBoardSlotLayout, Assembly-CSharp", false);
            var layout = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            Assert.That(type, Is.Not.Null);
            Assert.That(layout, Is.Not.Null);
            Assert.That(
                (int)type.GetField("SlotCount", BindingFlags.Static | BindingFlags.Public)
                    .GetRawConstantValue(),
                Is.EqualTo(12));

            var owner = new GameObject("Shared City Board Slot Layout Test", typeof(RectTransform));
            try
            {
                var rect = owner.GetComponent<RectTransform>();
                type.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)
                    .Invoke(null, new object[] { rect, layout, 0 });
                Assert.That(rect.anchorMin.x, Is.EqualTo(100f / 2059f).Within(0.0001f));
                Assert.That(rect.anchorMax.x, Is.EqualTo(700f / 2059f).Within(0.0001f));
                Assert.That(rect.anchorMin.y, Is.EqualTo(1f - 1006f / 3801f).Within(0.0001f));
                Assert.That(rect.anchorMax.y, Is.EqualTo(1f - 156f / 3801f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void CityBoardSlotLayout_UsesArtworkPixelsAfterParentTransformAndRejectsBorder()
        {
            var type = Type.GetType("YC.Presentation.CityBoardSlotLayout, Assembly-CSharp", true);
            var getSourceRect = type.GetMethod("GetSourceRect", BindingFlags.Static | BindingFlags.Public);
            var tryGetSlot = type.GetMethod("TryGetSlotIndex", BindingFlags.Static | BindingFlags.Public);
            Assert.That(getSourceRect, Is.Not.Null);
            Assert.That(tryGetSlot, Is.Not.Null);

            var owner = new GameObject("Artwork Geometry", typeof(RectTransform));
            try
            {
                var artwork = owner.GetComponent<RectTransform>();
                artwork.sizeDelta = new Vector2(270f, 500f);
                artwork.position = new Vector3(600f, 450f, 0f);
                artwork.localScale = Vector3.one * 0.72f;
                artwork.localRotation = Quaternion.Euler(0f, 0f, 11f);
                for (var i = 0; i < 12; i++)
                {
                    var source = (Rect)getSourceRect.Invoke(null, new object[] { i });
                    var point = ToScreen(artwork, source.center);
                    var arguments = new object[] { artwork, point, null, -1 };
                    Assert.That((bool)tryGetSlot.Invoke(null, arguments), Is.True, "slot=" + i);
                    Assert.That((int)arguments[3], Is.EqualTo(i));
                }

                var borderArguments = new object[]
                {
                    artwork, ToScreen(artwork, new Vector2(20f, 20f)), null, -1
                };
                Assert.That((bool)tryGetSlot.Invoke(null, borderArguments), Is.False);
                Assert.That((int)borderArguments[3], Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static Vector2 ToScreen(RectTransform artwork, Vector2 sourcePoint)
        {
            var rect = artwork.rect;
            var local = new Vector3(
                rect.xMin + sourcePoint.x * rect.width / 2059f,
                rect.yMax - sourcePoint.y * rect.height / 3801f);
            return RectTransformUtility.WorldToScreenPoint(null, artwork.TransformPoint(local));
        }

        [Test]
        public void RealConsumers_CallInjectedSpatialLayoutResolvers()
        {
            var slotLayout = Type.GetType(
                "YC.Presentation.CityBoardSlotLayout, Assembly-CSharp",
                true);
            var buildInfo = Type.GetType(
                "YC.Presentation.BuildInfoPanel, Assembly-CSharp",
                true);
            var previewView = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewView, Assembly-CSharp",
                true);
            var presenter = Type.GetType(
                "YC.Presentation.MapViewPresenter, Assembly-CSharp",
                true);

            Assert.That(
                CallsMethod(
                    buildInfo.GetMethod(
                        "BindFixedViews",
                        BindingFlags.Instance | BindingFlags.NonPublic),
                    slotLayout.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)),
                Is.True,
                "BuildInfoPanel 必须在运行时从显式注入的布局资产应用槽位几何。");
            Assert.That(
                CallsMethod(
                    previewView.GetMethod("ApplyCityBoardSlotLayout"),
                    slotLayout.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)),
                Is.True,
                "城市样式预览必须从自身持久布局引用应用槽位几何。");

            var refresh = presenter.GetMethod("RefreshScoreTrackDisplay");
            Assert.That(CallsMethod(
                    refresh,
                    typeof(MapDisplayLayout).GetMethod("GetScoreTrackNormalizedPosition")),
                Is.True,
                "MapViewPresenter 必须直接消费 View 已有 MapDisplayLayout 的计分轨迹。");
            Assert.That(CallsMethod(
                    refresh,
                    typeof(MapDisplayLayout).GetMethod("GetScoreMarkerOffset")),
                Is.True,
                "MapViewPresenter 必须直接消费 View 已有 MapDisplayLayout 的同分偏移。");
        }

        [Test]
        public void CityStyleMarkerRenderer_UsesConfiguredTracksAndAggregatesQuantities()
        {
            var type=Type.GetType("YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp",true);
            var resolve=type.GetMethod("ResolveAnchor");
            var layout=AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            foreach(var style in new[]{CityStyleDatabase.MilitaryIndustrialArea,CityStyleDatabase.SourceStoneIndustrialHub})
            {
                var areas=style==CityStyleDatabase.MilitaryIndustrialArea?new[]{"unused","used"}:new[]{"2","used_from_2","1","used_from_1","0"};
                float lastY=float.PositiveInfinity;
                foreach(var area in areas)
                {
                    var anchor=(Vector2)resolve.Invoke(null,new object[]{layout,style,area,0,0,0});
                    Assert.That(anchor.y,Is.LessThan(lastY));lastY=anchor.y;
                    Assert.That((Vector2)resolve.Invoke(null,new object[]{layout,style,area,99,0,99}),Is.EqualTo(anchor),"同玩家同状态的数量聚合在同一块。");
                }
            }
        }

        [Test]
        public void CityStyleMarkerRenderer_ResetsCanvasDepthRotationAndScale()
        {
            var type = Type.GetType("YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp", false);
            var layout = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            var configure = type == null ? null : type.GetMethod(
                "Configure",
                BindingFlags.Static | BindingFlags.Public);
            Assert.That(type, Is.Not.Null);
            Assert.That(layout, Is.Not.Null);
            Assert.That(configure, Is.Not.Null);

            var root = new GameObject("样式卡影响力标记", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            try
            {
                var marker = root.GetComponent<Image>();
                marker.rectTransform.anchoredPosition3D = new Vector3(5f, -3f, 40f);
                marker.rectTransform.localRotation = Quaternion.Euler(12f, 7f, 3f);
                marker.rectTransform.localScale = new Vector3(2f, 0.5f, 3f);
                var placementType = Type.GetType(
                    "YC.Presentation.CityStyleMarkerPlacement, Assembly-CSharp",
                    true);
                var placement = Activator.CreateInstance(
                    placementType,
                    CityStyleMarkerAreas.Declared,
                    0,
                    0,
                    0);
                configure.Invoke(null, new object[]
                {
                    layout,
                    marker,
                    "样式卡影响力标记",
                    Color.blue,
                    null,
                    (object)new Vector2(14f, 14f),
                    "style.test",
                    placement
                });

                Assert.That(marker.rectTransform.anchoredPosition3D, Is.EqualTo(Vector3.zero));
                Assert.That(marker.rectTransform.localRotation, Is.EqualTo(Quaternion.identity));
                Assert.That(marker.rectTransform.localScale, Is.EqualTo(Vector3.one));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CityStyleSpecialActionDropAreas_MatchPrintedCardZones()
        {
            var renderer = Type.GetType(
                "YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp",
                false);
            var dialog = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                false);
            Assert.That(renderer, Is.Not.Null);
            Assert.That(dialog, Is.Not.Null);

            var resolveBounds = renderer.GetMethod(
                "ResolveSpecialActionAreaBounds",
                BindingFlags.Static | BindingFlags.Public);
            var renderTargets = dialog.GetMethod(
                "RenderSpecialActionDropTargets",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(resolveBounds, Is.Not.Null);
            Assert.That(renderTargets, Is.Not.Null);
            var layout = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            Assert.That(layout, Is.Not.Null);
            Assert.That(CallsMethod(renderTargets, resolveBounds), Is.True,
                "特殊行动拖拽命中区必须复用卡面区域边界，不能退回固定尺寸的小矩形。");

            var levelOneUsed = (Rect)resolveBounds.Invoke(
                null,
                new object[]
                {
                    layout,
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Used
                });
            var cataloglevelOneUsed = layout.GetType().GetProperty("CityStyleVisuals").GetValue(layout);
            var configuredlevelOneUsed = (Rect)cataloglevelOneUsed.GetType().GetMethod("Bounds").Invoke(cataloglevelOneUsed, new object[] { CityStyleDatabase.MilitaryIndustrialArea, CityStyleMarkerAreas.Used });
            Assert.That(levelOneUsed, Is.EqualTo(configuredlevelOneUsed));
            Assert.That(levelOneUsed.width, Is.GreaterThan(0));

            var levelTwoFirstUsed = (Rect)resolveBounds.Invoke(
                null,
                new object[]
                {
                    layout,
                    CityStyleDatabase.SourceStoneIndustrialHub,
                    SpecialActionMarkerAreas.UsedFromTwo
                });
            var cataloglevelTwoFirstUsed = layout.GetType().GetProperty("CityStyleVisuals").GetValue(layout);
            var configuredlevelTwoFirstUsed = (Rect)cataloglevelTwoFirstUsed.GetType().GetMethod("Bounds").Invoke(cataloglevelTwoFirstUsed, new object[] { CityStyleDatabase.SourceStoneIndustrialHub, SpecialActionMarkerAreas.UsedFromTwo });
            Assert.That(levelTwoFirstUsed, Is.EqualTo(configuredlevelTwoFirstUsed));
            Assert.That(levelTwoFirstUsed.width, Is.GreaterThan(0));

            var levelTwoSecondUsed = (Rect)resolveBounds.Invoke(
                null,
                new object[]
                {
                    layout,
                    CityStyleDatabase.EfficientMobileManagementSystem,
                    SpecialActionMarkerAreas.UsedFromOne
                });
            var cataloglevelTwoSecondUsed = layout.GetType().GetProperty("CityStyleVisuals").GetValue(layout);
            var configuredlevelTwoSecondUsed = (Rect)cataloglevelTwoSecondUsed.GetType().GetMethod("Bounds").Invoke(cataloglevelTwoSecondUsed, new object[] { CityStyleDatabase.EfficientMobileManagementSystem, SpecialActionMarkerAreas.UsedFromOne });
            Assert.That(levelTwoSecondUsed, Is.EqualTo(configuredlevelTwoSecondUsed));
            Assert.That(levelTwoSecondUsed.width, Is.GreaterThan(0));
        }

        [Test]
        public void MilitaryIndustrialMarkers_KeepFourPlayersInsideTheConfiguredSafeArea()
        {
            var type=Type.GetType("YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp",true);
            var resolve=type.GetMethod("ResolveAnchor");var lane=type.GetMethod("ResolvePlayerLaneIndex");
            var layout=AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            var catalog=layout.GetType().GetProperty("CityStyleVisuals").GetValue(layout);
            var bounds=(Rect)catalog.GetType().GetMethod("Bounds").Invoke(catalog,new object[]{CityStyleDatabase.MilitaryIndustrialArea,"unused"});
            var anchors=new HashSet<Vector2>();
            for(int player=1;player<=4;player++)
            {
                int laneIndex=(int)lane.Invoke(null,new object[]{layout,player});
                var anchor=(Vector2)resolve.Invoke(null,new object[]{layout,CityStyleDatabase.MilitaryIndustrialArea,"unused",0,laneIndex,0});
                Assert.That(bounds.Contains(anchor),Is.True);Assert.That(anchors.Add(anchor),Is.True);
            }
        }

        [Test]
        public void CityStylePreviewGroupsMarkersPerPlayerAndArea()
        {
            var trackerType = Type.GetType(
                "YC.Presentation.CityStyleMarkerLayoutTracker, Assembly-CSharp",
                false);
            var buildPanelType = Type.GetType(
                "YC.Presentation.BuildInfoPanel, Assembly-CSharp",
                false);
            var previewDialogType = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                false);
            Assert.That(trackerType, Is.Not.Null);
            Assert.That(buildPanelType, Is.Not.Null);
            Assert.That(previewDialogType, Is.Not.Null);
            Assert.That(
                HasPrivateMethodParameter(
                    previewDialogType,
                    "AddCityStyleInfluenceMarker",
                    trackerType),
                Is.True);

            var layout = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "Assets/YC/Presentation/Content/CardBoardVisualLayout.asset");
            Assert.That(layout, Is.Not.Null);
            var tracker = Activator.CreateInstance(trackerType, new object[] { layout });
            var next = trackerType.GetMethod("Next", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(next, Is.Not.Null);
            var playerOneFirst = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Declared,
                    1
                });
            var playerOneSecond = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Unused,
                    1
                });
            var playerTwoFirst = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Declared,
                    2
                });

            Assert.That(
                GetPublicProperty<string>(playerOneFirst, "MarkerArea"),
                Is.EqualTo(CityStyleMarkerAreas.Declared));
            Assert.That(GetPublicProperty<int>(playerOneFirst, "PlayerLaneIndex"), Is.Zero);
            Assert.That(GetPublicProperty<int>(playerOneFirst, "PlayerMarkerIndex"), Is.Zero);
            Assert.That(GetPublicProperty<int>(playerOneSecond, "PlayerLaneIndex"), Is.Zero);
            Assert.That(
                GetPublicProperty<int>(playerOneSecond, "PlayerMarkerIndex"),
                Is.Zero,
                "额外宣告与使用轨道分别统计，不能合并。");
            Assert.That(GetPublicProperty<int>(playerTwoFirst, "PlayerLaneIndex"), Is.EqualTo(1));
            Assert.That(GetPublicProperty<int>(playerTwoFirst, "PlayerMarkerIndex"), Is.Zero);

            var otherStyleTracker = Activator.CreateInstance(trackerType, new object[] { layout });
            var otherStyleMarker = next.Invoke(
                otherStyleTracker,
                new object[] { "style.test", CityStyleMarkerAreas.Declared, 1 });
            Assert.That(
                GetPublicProperty<string>(otherStyleMarker, "MarkerArea"),
                Is.EqualTo(CityStyleMarkerAreas.Declared));
        }

        [Test]
        public void CityStyleOptionsViewModel_ContainsOnlyDisplaySnapshotsAndCallbacks()
        {
            Assert.That(typeof(CityStyleOptionsViewModel).GetProperty("State"), Is.Null);
            Assert.That(typeof(CityStyleOptionsViewModel).GetProperty("PlayerId"), Is.Null);
            foreach (var property in typeof(CityStyleOptionsViewModel).GetProperties())
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(GameState)), property.Name);
            }

            var slot = new CityBoardSlotViewModel(3, "facility.test", true);
            var marker = new CityStyleMarkerViewModel(
                "style.test",
                2,
                PlayerColor.Blue,
                CityStyleMarkerAreas.Used);
            Assert.That(slot.SlotIndex, Is.EqualTo(3));
            Assert.That(slot.FacilityId, Is.EqualTo("facility.test"));
            Assert.That(slot.Used, Is.True);
            Assert.That(marker.PlayerId, Is.EqualTo(2));
            Assert.That(marker.PlayerColor, Is.EqualTo(PlayerColor.Blue));
        }

        private static bool CallsMethod(MethodInfo caller, MethodInfo target)
        {
            var body = caller == null ? null : caller.GetMethodBody();
            var il = body == null ? null : body.GetILAsByteArray();
            if (il == null || target == null)
            {
                return false;
            }

            for (var index = 0; index <= il.Length - 5; index++)
            {
                if (il[index] != 0x28 && il[index] != 0x6f)
                {
                    continue;
                }

                MethodBase resolved;
                try
                {
                    resolved = caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1));
                }
                catch (Exception)
                {
                    continue;
                }

                if (resolved != null &&
                    resolved.Module == target.Module &&
                    resolved.MetadataToken == target.MetadataToken)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasPrivateMethodParameter(Type ownerType, string methodName, Type parameterType)
        {
            var methods = ownerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
            {
                var method = methods[methodIndex];
                if (method.Name != methodName)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameters[parameterIndex].ParameterType == parameterType)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            return (T)target.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                .GetValue(target, null);
        }
    }
}

