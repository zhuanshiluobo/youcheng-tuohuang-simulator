using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class SecondaryLayoutEditorAssetBuilder
    {
        public const string SourceJsonPath =
            "Assets/YC/Editor/Data/secondary_layout_manifest.json";
        public const string SourceJsonGuid = "84181727001c7bc48b3b310efcce7325";
        public const string ExpectedManifestSha256 =
            "6FB4251B920FF7680E688394065DB5DCE430C637F9BA15379C2FDCA7AAC05BCE";
        public const string ActionPanelAssetPath =
            "Assets/YC/Presentation/Content/ActionPanelLayoutProfile.asset";
        public const string ActionPanelAssetGuid = "fdfb2e039f0c842439596ccfcea7686b";
        public const string CardInteractionAssetPath =
            "Assets/YC/Presentation/Content/CardInteractionLayoutProfile.asset";
        public const string CardInteractionAssetGuid = "84c89edc1445da24cb6d911a4839e1f3";
        public const string ZoomableViewerAssetPath =
            "Assets/YC/Presentation/Content/ZoomableViewerLayoutProfile.asset";
        public const string ZoomableViewerAssetGuid = "6897c32684bc9b7448d53ba522fcdbc8";
        public const string CharacterHandAssetPath =
            "Assets/YC/Presentation/Content/CharacterHandLayoutProfile.asset";
        public const string CharacterHandAssetGuid = "49dc093f8a269884aad1c54e85becb8f";

        [MenuItem("YC/Build/Secondary Layout/Rebuild Assets")]
        public static void RebuildAssetsMenu()
        {
            RebuildAssets();
        }

        [MenuItem("YC/Build/Secondary Layout/Rebuild Assets And Configure Prefabs")]
        public static void RebuildAssetsAndConfigurePrefabs()
        {
            RebuildAssets();
            YC.EditorTools.CityStyleDeclarationPreviewEditorAssetBuilder.Rebuild();
            YC.EditorTools.ViewerEditorAssetBuilder.RebuildViewerPrefabs();
            YC.EditorTools.GameplayInteractionHudEditorAssetBuilder.Rebuild();
            SecondaryLayoutBuildReadiness.ValidateReadyForBuild();
            Debug.Log("[SecondaryLayoutEditorAssetBuilder] 已重建三份次级布局资产并显式接入 Prefab。");
        }

        public static void RebuildAssets()
        {
            var root = ParseSource(out var sha256);
            var action = RequireObject(root, "actionPanel");
            var card = RequireObject(root, "cardInteraction");
            var viewer = RequireObject(root, "zoomableViewer");
            var characterHand = RequireObject(root, "characterHand");

            var actionProfile = GetOrCreate<ActionPanelLayoutProfile>(ActionPanelAssetPath);
            actionProfile.ConfigureForEditor(
                sha256,
                ReadVector(action["cardImageOffsetMin"], "actionPanel.cardImageOffsetMin"),
                ReadVector(action["cardImageOffsetMax"], "actionPanel.cardImageOffsetMax"),
                ReadRect(RequireObject(action, "characterContainer")));
            ValidateProfile(actionProfile, "ActionPanelLayoutProfile");

            var drag = RequireObject(card, "dragGhost");
            var cardProfile = GetOrCreate<CardInteractionLayoutProfile>(CardInteractionAssetPath);
            cardProfile.ConfigureForEditor(
                sha256,
                ReadVector(card["pendingBuildGhostAnchorMin"], "cardInteraction.pendingBuildGhostAnchorMin"),
                ReadVector(card["pendingBuildGhostAnchorMax"], "cardInteraction.pendingBuildGhostAnchorMax"),
                new CardDragGhostLayout
                {
                    RootLayout = ReadAnchor(drag),
                    FallbackOffsetMin = ReadVector(drag["fallbackOffsetMin"], "dragGhost.fallbackOffsetMin"),
                    FallbackOffsetMax = ReadVector(drag["fallbackOffsetMax"], "dragGhost.fallbackOffsetMax"),
                    FallbackOutlineDistance = ReadVector(
                        drag["fallbackOutlineDistance"],
                        "dragGhost.fallbackOutlineDistance")
                },
                ReadAnchor(RequireObject(card, "externalCardAnchor")),
                ReadVector(card["facilitySelectableOutlineDistance"], "facilitySelectableOutlineDistance"),
                ReadVector(card["normalOutlineDistance"], "normalOutlineDistance"),
                ReadVector(card["legalCityBoardSlotOutlineDistance"], "legalCityBoardSlotOutlineDistance"),
                ReadVector(card["cityStyleBoardActiveOutlineDistance"], "cityStyleBoardActiveOutlineDistance"),
                ReadVector(card["cityStyleBoardInactiveOutlineDistance"], "cityStyleBoardInactiveOutlineDistance"),
                ReadVector(card["cityStyleSelectedSlotOutlineDistance"], "cityStyleSelectedSlotOutlineDistance"),
                ReadVector(card["cityStyleSelectableSlotOutlineDistance"], "cityStyleSelectableSlotOutlineDistance"),
                ReadVector(card["cityStyleEmphasizedButtonOutlineDistance"], "cityStyleEmphasizedButtonOutlineDistance"));
            ValidateProfile(cardProfile, "CardInteractionLayoutProfile");

            var viewerProfile = GetOrCreate<ZoomableViewerLayoutProfile>(ZoomableViewerAssetPath);
            viewerProfile.ConfigureForEditor(
                sha256,
                ReadRect(RequireObject(viewer, "collapsedToggle")),
                ReadRect(RequireObject(viewer, "expandedToggle")),
                ReadVector(viewer["fallbackViewportSize"], "zoomableViewer.fallbackViewportSize"));
            ValidateProfile(viewerProfile, "ZoomableViewerLayoutProfile");

            var characterHandProfile = GetOrCreate<CharacterHandLayoutProfile>(CharacterHandAssetPath);
            characterHandProfile.ConfigureForEditor(
                sha256,
                ReadVector(characterHand["cardSize"], "characterHand.cardSize"),
                ReadFloat(characterHand["fanSpacing"], "characterHand.fanSpacing"),
                ReadFloat(characterHand["fanMaxAngle"], "characterHand.fanMaxAngle"),
                ReadFloat(characterHand["passiveVisibleFraction"], "characterHand.passiveVisibleFraction"),
                ReadFloat(characterHand["passiveAlpha"], "characterHand.passiveAlpha"),
                ReadFloat(characterHand["hoverScale"], "characterHand.hoverScale"),
                ReadFloat(characterHand["hoverAlpha"], "characterHand.hoverAlpha"),
                ReadFloat(characterHand["hoverBottom"], "characterHand.hoverBottom"),
                ReadFloat(characterHand["expandedSpacing"], "characterHand.expandedSpacing"),
                ReadFloat(characterHand["expandedMaxAngle"], "characterHand.expandedMaxAngle"),
                ReadFloat(characterHand["expandedBottom"], "characterHand.expandedBottom"),
                ReadFloat(characterHand["fanCenterOffsetX"], "characterHand.fanCenterOffsetX"),
                ReadVector(characterHand["overlayCardSize"], "characterHand.overlayCardSize"),
                ReadVector(characterHand["overlaySpacing"], "characterHand.overlaySpacing"),
                ReadFloat(characterHand["discardAlpha"], "characterHand.discardAlpha"),
                ReadVector(characterHand["discardButtonSize"], "characterHand.discardButtonSize"),
                ReadVector(characterHand["discardButtonPosition"], "characterHand.discardButtonPosition"),
                ReadVector(characterHand["modalPanelSize"], "characterHand.modalPanelSize"),
                ReadVector(characterHand["modalPanelPosition"], "characterHand.modalPanelPosition"));
            ValidateProfile(characterHandProfile, "CharacterHandLayoutProfile");

            EditorUtility.SetDirty(actionProfile);
            EditorUtility.SetDirty(cardProfile);
            EditorUtility.SetDirty(viewerProfile);
            EditorUtility.SetDirty(characterHandProfile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ActionPanelAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(CardInteractionAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(ZoomableViewerAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(CharacterHandAssetPath, ImportAssetOptions.ForceUpdate);
        }

        public static ActionPanelLayoutProfile LoadRequiredActionProfile()
        {
            return LoadRequired<ActionPanelLayoutProfile>(ActionPanelAssetPath, ActionPanelAssetGuid);
        }

        public static CardInteractionLayoutProfile LoadRequiredCardProfile()
        {
            return LoadRequired<CardInteractionLayoutProfile>(CardInteractionAssetPath, CardInteractionAssetGuid);
        }

        public static ZoomableViewerLayoutProfile LoadRequiredZoomableViewerProfile()
        {
            return LoadRequired<ZoomableViewerLayoutProfile>(ZoomableViewerAssetPath, ZoomableViewerAssetGuid);
        }

        public static CharacterHandLayoutProfile LoadRequiredCharacterHandProfile()
        {
            return LoadRequired<CharacterHandLayoutProfile>(CharacterHandAssetPath, CharacterHandAssetGuid);
        }

        internal static string ComputeCurrentSourceSha256()
        {
            return ComputeSha256(SourceJsonPath);
        }

        private static JObject ParseSource(out string sha256)
        {
            if (!File.Exists(SourceJsonPath))
                throw new InvalidOperationException("次级布局 manifest 不存在：" + SourceJsonPath);
            if (!string.IsNullOrEmpty(SourceJsonGuid) &&
                AssetDatabase.AssetPathToGUID(SourceJsonPath) != SourceJsonGuid)
                throw new InvalidOperationException("次级布局 manifest GUID 已改变。");

            sha256 = ComputeSha256(SourceJsonPath);
            if (!string.IsNullOrEmpty(ExpectedManifestSha256) &&
                !string.Equals(sha256, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("次级布局 manifest SHA-256 已改变，必须先审核固定候选数据。");

            var root = JObject.Parse(File.ReadAllText(SourceJsonPath));
            if ((int?)root["schemaVersion"] != 1)
                throw new InvalidOperationException("次级布局 manifest schemaVersion 必须为 1。");
            return root;
        }

        private static T GetOrCreate<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null && MonoScript.FromScriptableObject(asset) == null)
            {
                if (!AssetDatabase.DeleteAsset(path))
                    throw new InvalidOperationException("无法替换脚本绑定无效的布局资产：" + path);
                asset = null;
            }
            if (asset != null) return asset;
            if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException("无法替换类型无效的布局资产：" + path);
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static T LoadRequired<T>(string path, string expectedGuid) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null || string.IsNullOrEmpty(expectedGuid) ||
                AssetDatabase.AssetPathToGUID(path) != expectedGuid)
                throw new InvalidOperationException(path + " 缺少固定 GUID 的持久化布局资产。");

            ValidateProfile(asset, typeof(T).Name);
            var sourceHash = GetSourceHash(asset);
            if (!string.Equals(sourceHash, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(path + " 未匹配锁定 manifest。");

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (allAssets.Length != 1 || allAssets[0] != asset || !AssetDatabase.Contains(asset) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long localId) ||
                guid != expectedGuid || localId == 0)
                throw new InvalidOperationException(path + " 必须是固定 GUID 的唯一持久化主资产。");
            return asset;
        }

        private static string GetSourceHash(ScriptableObject asset)
        {
            if (asset is ActionPanelLayoutProfile action) return action.SourceManifestSha256;
            if (asset is CardInteractionLayoutProfile card) return card.SourceManifestSha256;
            if (asset is ZoomableViewerLayoutProfile viewer) return viewer.SourceManifestSha256;
            if (asset is CharacterHandLayoutProfile characterHand) return characterHand.SourceManifestSha256;
            return string.Empty;
        }

        private static void ValidateProfile(ScriptableObject asset, string label)
        {
            var reason = string.Empty;
            var valid = asset is ActionPanelLayoutProfile action
                ? action.TryValidateConfiguration(out reason)
                : asset is CardInteractionLayoutProfile card
                    ? card.TryValidateConfiguration(out reason)
                    : asset is ZoomableViewerLayoutProfile viewer
                        ? viewer.TryValidateConfiguration(out reason)
                        : asset is CharacterHandLayoutProfile characterHand &&
                          characterHand.TryValidateConfiguration(out reason);
            if (!valid) throw new InvalidOperationException(label + " 数据无效：" + reason);
        }

        private static SecondaryAnchorLayout ReadAnchor(JObject source)
        {
            return new SecondaryAnchorLayout
            {
                AnchorMin = ReadVector(source["anchorMin"], "anchorMin"),
                AnchorMax = ReadVector(source["anchorMax"], "anchorMax"),
                Pivot = ReadVector(source["pivot"], "pivot")
            };
        }

        private static SecondaryRectLayout ReadRect(JObject source)
        {
            var anchor = ReadAnchor(source);
            return new SecondaryRectLayout
            {
                AnchorMin = anchor.AnchorMin,
                AnchorMax = anchor.AnchorMax,
                Pivot = anchor.Pivot,
                SizeDelta = ReadVector(source["sizeDelta"], "sizeDelta"),
                AnchoredPosition = ReadVector(source["anchoredPosition"], "anchoredPosition")
            };
        }

        private static Vector2 ReadVector(JToken token, string label)
        {
            if (!(token is JArray values) || values.Count != 2 ||
                values[0].Type != JTokenType.Float && values[0].Type != JTokenType.Integer ||
                values[1].Type != JTokenType.Float && values[1].Type != JTokenType.Integer)
                throw new InvalidOperationException(label + " 必须是两个有限数字组成的数组。");
            var result = new Vector2((float)values[0], (float)values[1]);
            if (float.IsNaN(result.x) || float.IsInfinity(result.x) ||
                float.IsNaN(result.y) || float.IsInfinity(result.y))
                throw new InvalidOperationException(label + " 包含非有限数值。");
            return result;
        }

        private static float ReadFloat(JToken token, string label)
        {
            if (token == null ||
                token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                throw new InvalidOperationException(label + " 必须是有限数字。");
            var result = (float)token;
            if (float.IsNaN(result) || float.IsInfinity(result))
                throw new InvalidOperationException(label + " 包含非有限数值。");
            return result;
        }

        private static JObject RequireObject(JObject source, string property)
        {
            if (!(source[property] is JObject value))
                throw new InvalidOperationException(property + " 必须是 JSON object。");
            return value;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }
    }
}
