using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using YC.Presentation;
using YC.Presentation.Maps;
using Object = UnityEngine.Object;

namespace YC.Editor
{
    public static class MapFeedbackVisualBuildReadiness
    {
        public const string MapViewPrefabPath =
            "Assets/YC/Presentation/Prefabs/Map/MapView.prefab";
        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        public const int ExpectedPlacementFeedbackCount = 99;
        public const int ExpectedSharedMaterialRendererCount = 297;

        [MenuItem("YC/Build/Map Feedback Visuals/Validate Generated Assets")]
        public static void ValidateGeneratedAssetsMenu()
        {
            ValidateGeneratedAssets();
            Debug.Log(
                "[MapFeedbackVisualBuildReadiness] Material、Atlas、Clip、Controller 资产级合同验证通过。");
        }

        [MenuItem("YC/Build/Map Feedback Visuals/Validate Ready For Build")]
        public static void ValidateReadyForBuildMenu()
        {
            ValidateReadyForBuild();
            Debug.Log(
                "[MapFeedbackVisualBuildReadiness] 视觉资产、99 Animator、297 renderer 与空间布局合同验证通过。");
        }

        public static void ValidateGeneratedAssets()
        {
            ValidateAssetGuid(
                MapFeedbackVisualEditorAssetBuilder.ShaderPath,
                MapFeedbackVisualEditorAssetBuilder.ShaderGuid);
            ValidateAssetGuid(
                MapFeedbackVisualEditorAssetBuilder.MaterialPath,
                MapFeedbackVisualEditorAssetBuilder.MaterialGuid);
            ValidateAssetGuid(
                MapFeedbackVisualEditorAssetBuilder.AtlasPath,
                MapFeedbackVisualEditorAssetBuilder.AtlasGuid);
            ValidateAssetGuid(
                MapFeedbackVisualEditorAssetBuilder.ClipPath,
                MapFeedbackVisualEditorAssetBuilder.ClipGuid);
            ValidateAssetGuid(
                MapFeedbackVisualEditorAssetBuilder.ControllerPath,
                MapFeedbackVisualEditorAssetBuilder.ControllerGuid);

            var assets = MapFeedbackVisualEditorAssetBuilder.LoadRequiredAssets();
            ValidateShaderAndMaterial(assets.FeedbackMaterial);
            ValidateAtlas(assets.VisualAtlas);
            ValidateClip(assets.PlacementClip);
            ValidateController(assets.PlacementController as AnimatorController, assets.PlacementClip);
        }

        public static void ValidateReadyForBuild()
        {
            ValidateGeneratedAssets();
            ValidatePrefabBindings();
            ValidateSampleSceneInstanceOverrides();
            ValidateProductionSourceUsesAnimatorAssets();
            SpatialLayoutBuildReadiness.ValidateReadyForBuild();
        }

        private static void ValidateShaderAndMaterial(Material material)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                MapFeedbackVisualEditorAssetBuilder.ShaderPath);
            if (shader == null || shader.name != MapFeedbackVisualEditorAssetBuilder.ShaderName ||
                material == null || material.shader != shader || material.renderQueue != 3000 ||
                material.enableInstancing ||
                material.GetColor("_Color") != Color.white ||
                material.shaderKeywords.Length != 0 ||
                !Approximately(material.GetFloat("PixelSnap"), 0f) ||
                !Approximately(material.GetFloat("_EnableExternalAlpha"), 0f) ||
                material.GetTexture("_AlphaTex") != null ||
                material.GetTexture("_MainTex") != null)
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive Material/Shader 配置未精确匹配锁定合同。");
            }

            RequireEmptySerializedArray(material, "m_ValidKeywords");
            RequireEmptySerializedArray(material, "m_InvalidKeywords");
            RequireEmptySerializedArray(material, "m_DisabledShaderPasses");

            if (!string.Equals(
                    material.GetTag("CanUseSpriteAtlas", false, string.Empty),
                    "True",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive Shader 必须声明 CanUseSpriteAtlas=True。");
            }

            var source = File.ReadAllText(MapFeedbackVisualEditorAssetBuilder.ShaderPath);
            ValidatePremultipliedBlendDirectives(source);
            if (ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive 必须使用 SpriteFrag 的预乘输出配合 Blend One One，禁止 alpha 二次相乘。");
            }
        }

        internal static void ValidatePremultipliedBlendDirectives(string shaderSource)
        {
            ValidateShaderSourceContract(shaderSource);
#if false
            if (shaderSource == null)
            {
                throw new ArgumentNullException(nameof(shaderSource));
            }

            var uncommented = Regex.Replace(
                shaderSource,
                @"//[^\r\n]*|/\*[\s\S]*?\*/",
                string.Empty,
                RegexOptions.CultureInvariant);
            var directives = Regex.Matches(
                uncommented,
                @"^[ \t]*Blend\b[^\r\n]*",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (directives.Count != 1 ||
                !IsValidBlendDirectiveScope(uncommented, directives[0].Index))
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive 必须恰好包含一条位于 SubShader/Pass 渲染状态作用域的有效 Blend 指令。");
            }

            var tokens = Regex.Split(directives[0].Value.Trim(), @"\s+");
            if (tokens.Length != 3 ||
                !string.Equals(tokens[0], "Blend", StringComparison.Ordinal) ||
                !string.Equals(tokens[1], "One", StringComparison.Ordinal) ||
                !string.Equals(tokens[2], "One", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive 的唯一有效 Blend 指令必须精确为 Blend One One。");
            }
        }

#endif
        }

        private static bool IsValidBlendDirectiveScope(string source, int directiveIndex)
        {
            var scopes = new Stack<string>();
            var inQuotedString = false;
            var escaped = false;
            for (var index = 0; index < directiveIndex; index++)
            {
                var character = source[index];
                if (inQuotedString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inQuotedString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inQuotedString = true;
                }
                else if (character == '{')
                {
                    scopes.Push(ReadBlockKeyword(source, index));
                }
                else if (character == '}' && scopes.Count > 0)
                {
                    scopes.Pop();
                }
            }

            if (scopes.Count == 0 ||
                !scopes.Contains("SubShader") ||
                (scopes.Peek() != "SubShader" && scopes.Peek() != "Pass"))
            {
                return false;
            }

            var inProgram = false;
            var programMarkers = Regex.Matches(
                source.Substring(0, directiveIndex),
                @"^[ \t]*(CGPROGRAM|HLSLPROGRAM|ENDCG|ENDHLSL)[ \t]*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            foreach (Match marker in programMarkers)
            {
                var token = marker.Groups[1].Value;
                inProgram = token == "CGPROGRAM" || token == "HLSLPROGRAM";
            }
            return !inProgram;
        }

        private static string ReadBlockKeyword(string source, int openingBraceIndex)
        {
            var index = openingBraceIndex - 1;
            while (index >= 0 && char.IsWhiteSpace(source[index]))
            {
                index--;
            }

            var end = index;
            while (index >= 0 &&
                   (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
            {
                index--;
            }
            return end < index + 1 ? string.Empty : source.Substring(index + 1, end - index);
        }

        private static void ValidateShaderSourceContract(string source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var tokens = LexShader(source);
            var scopes = new Stack<string>();
            var subShaderCount = 0;
            var passCount = 0;
            var programCount = 0;
            var includeCount = 0;
            var fragmentCount = 0;
            var blendCount = 0;
            var inProgram = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind == ShaderTokenKind.QuotedString)
                {
                    continue;
                }

                if (token.Value == "CGPROGRAM" || token.Value == "HLSLPROGRAM")
                {
                    var line = GetShaderLineTokens(tokens, token.Line);
                    if (inProgram || scopes.Count == 0 || scopes.Peek() != "Pass" ||
                        line.Count != 1)
                    {
                        throw new InvalidOperationException("Shader program 必须唯一且直接位于唯一 Pass 中。");
                    }
                    inProgram = true;
                    programCount++;
                    continue;
                }
                if (token.Value == "ENDCG" || token.Value == "ENDHLSL")
                {
                    var line = GetShaderLineTokens(tokens, token.Line);
                    if (!inProgram || line.Count != 1)
                    {
                        throw new InvalidOperationException("Shader program 结束标记不匹配。");
                    }
                    inProgram = false;
                    continue;
                }

                if (inProgram)
                {
                    if (token.Value == "#include")
                    {
                        var line = GetShaderLineTokens(tokens, token.Line);
                        if (line.Count != 2 ||
                            line[1].Kind != ShaderTokenKind.QuotedString ||
                            line[1].Value != "UnitySprites.cginc")
                        {
                            throw new InvalidOperationException(
                                "Shader program 必须精确 include UnitySprites.cginc。");
                        }
                        includeCount++;
                    }
                    else if (token.Value == "#pragma")
                    {
                        var line = GetShaderLineTokens(tokens, token.Line);
                        if (line.Count >= 2 && line[1].Value == "fragment")
                        {
                            if (line.Count != 3 || line[2].Value != "SpriteFrag" ||
                                line[2].Kind != ShaderTokenKind.Identifier)
                            {
                                throw new InvalidOperationException(
                                    "Shader fragment pragma 必须精确指定 SpriteFrag。");
                            }
                            fragmentCount++;
                        }
                    }
                    continue;
                }

                if (token.Value == "{")
                {
                    var blockKeyword = PreviousShaderBlockKeyword(tokens, i);
                    if (blockKeyword == "SubShader")
                    {
                        subShaderCount++;
                    }
                    else if (blockKeyword == "Pass")
                    {
                        if (scopes.Count == 0 || scopes.Peek() != "SubShader")
                        {
                            throw new InvalidOperationException("唯一 Pass 必须直接位于唯一 SubShader 中。");
                        }
                        passCount++;
                    }
                    scopes.Push(blockKeyword);
                    continue;
                }
                if (token.Value == "}")
                {
                    if (scopes.Count == 0)
                    {
                        throw new InvalidOperationException("Shader 花括号不匹配。");
                    }
                    scopes.Pop();
                    continue;
                }
                if (token.Value == "Blend")
                {
                    var line = GetShaderLineTokens(tokens, token.Line);
                    if (scopes.Count == 0 ||
                        (scopes.Peek() != "SubShader" && scopes.Peek() != "Pass") ||
                        line.Count != 3 || line[0].Value != "Blend" ||
                        line[1].Value != "One" || line[2].Value != "One")
                    {
                        throw new InvalidOperationException(
                            "唯一有效 Blend 指令必须精确为 Blend One One，且位于渲染状态作用域。");
                    }
                    blendCount++;
                }
            }

            if (inProgram || scopes.Count != 0 || subShaderCount != 1 || passCount != 1 ||
                programCount != 1 || blendCount != 1 || includeCount != 1 || fragmentCount != 1)
            {
                throw new InvalidOperationException(
                    "Shader 必须恰好包含 1 SubShader/1 Pass/1 program/1 Blend、1 UnitySprites include 与 1 SpriteFrag pragma。");
            }
        }

        private static List<ShaderToken> LexShader(string source)
        {
            var result = new List<ShaderToken>();
            var line = 1;
            for (var index = 0; index < source.Length;)
            {
                var current = source[index];
                if (current == '\r' || current == '\n')
                {
                    if (current == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                    {
                        index++;
                    }
                    line++;
                    index++;
                    continue;
                }
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }
                if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] != '\r' && source[index] != '\n')
                    {
                        index++;
                    }
                    continue;
                }
                if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    index += 2;
                    var closed = false;
                    while (index < source.Length)
                    {
                        if (source[index] == '\r' || source[index] == '\n')
                        {
                            if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                            {
                                index++;
                            }
                            line++;
                            index++;
                            continue;
                        }
                        if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/')
                        {
                            index += 2;
                            closed = true;
                            break;
                        }
                        index++;
                    }
                    if (!closed)
                    {
                        throw new InvalidOperationException("Shader 含未闭合块注释。");
                    }
                    continue;
                }
                if (current == '"')
                {
                    var tokenLine = line;
                    var value = new StringBuilder();
                    var escaped = false;
                    var closed = false;
                    index++;
                    while (index < source.Length)
                    {
                        current = source[index++];
                        if (escaped)
                        {
                            value.Append(current);
                            escaped = false;
                        }
                        else if (current == '\\')
                        {
                            escaped = true;
                        }
                        else if (current == '"')
                        {
                            closed = true;
                            break;
                        }
                        else
                        {
                            if (current == '\r' || current == '\n')
                            {
                                line++;
                            }
                            value.Append(current);
                        }
                    }
                    if (!closed)
                    {
                        throw new InvalidOperationException("Shader 含未闭合字符串。");
                    }
                    result.Add(new ShaderToken(value.ToString(), tokenLine, ShaderTokenKind.QuotedString));
                    continue;
                }
                if (char.IsLetter(current) || current == '_' || current == '#')
                {
                    var start = index++;
                    while (index < source.Length &&
                           (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                    {
                        index++;
                    }
                    result.Add(new ShaderToken(
                        source.Substring(start, index - start),
                        line,
                        ShaderTokenKind.Identifier));
                    continue;
                }

                result.Add(new ShaderToken(current.ToString(), line, ShaderTokenKind.Symbol));
                index++;
            }
            return result;
        }

        private static List<ShaderToken> GetShaderLineTokens(
            IReadOnlyList<ShaderToken> tokens,
            int line)
        {
            return tokens.Where(item => item.Line == line).ToList();
        }

        private static string PreviousShaderBlockKeyword(
            IReadOnlyList<ShaderToken> tokens,
            int openingBraceIndex)
        {
            for (var index = openingBraceIndex - 1; index >= 0; index--)
            {
                if (tokens[index].Kind != ShaderTokenKind.QuotedString)
                {
                    return tokens[index].Value;
                }
            }
            return string.Empty;
        }

        private static void ValidateAtlas(SpriteAtlas atlas)
        {
            var atlasSerialized = atlas == null ? null : new SerializedObject(atlas);
            var includeInBuild = atlasSerialized == null
                ? null
                : atlasSerialized.FindProperty("m_EditorData.bindAsDefault");
            if (atlas == null || atlas.isVariant || includeInBuild == null ||
                !includeInBuild.boolValue)
            {
                throw new InvalidOperationException(
                    "MapVisuals SpriteAtlas 必须是 IncludeInBuild 的主 Atlas。");
            }

            var expected = MapFeedbackVisualEditorAssetBuilder.ResolveLockedAtlasSources();
            ValidateResourceIconImporters();
            var packables = SpriteAtlasExtensions.GetPackables(atlas);
            if (packables.Length != MapFeedbackVisualEditorAssetBuilder.ExpectedAtlasSpriteCount ||
                !HaveSamePersistentIdentities(packables, expected.Cast<Object>().ToArray()))
            {
                throw new InvalidOperationException(
                    "MapVisuals SpriteAtlas packable 必须精确等于锁定的 5 个资源 Sprite。");
            }

            var packing = SpriteAtlasExtensions.GetPackingSettings(atlas);
            var texture = SpriteAtlasExtensions.GetTextureSettings(atlas);
            var platform = SpriteAtlasExtensions.GetPlatformSettings(
                atlas,
                "DefaultTexturePlatform");
            if (packing.padding != 4 || packing.blockOffset != 1 ||
                packing.enableRotation || packing.enableTightPacking ||
                packing.enableAlphaDilation || texture.anisoLevel != 1 ||
                texture.filterMode != FilterMode.Bilinear || texture.generateMipMaps ||
                texture.readable || !texture.sRGB || platform.maxTextureSize != 2048 ||
                platform.resizeAlgorithm != TextureResizeAlgorithm.Mitchell ||
                platform.textureCompression != TextureImporterCompression.Uncompressed ||
                platform.crunchedCompression || platform.overridden)
            {
                throw new InvalidOperationException(
                    "MapVisuals SpriteAtlas 打包、纹理或默认平台设置已漂移。");
            }

            var actualPlatforms =
                MapFeedbackVisualEditorAssetBuilder.EnumerateSpriteAtlasPlatformNames(atlas);
            for (var platformIndex = 0; platformIndex < actualPlatforms.Count; platformIndex++)
            {
                var overridePlatform = SpriteAtlasExtensions.GetPlatformSettings(
                    atlas,
                    actualPlatforms[platformIndex]);
                if (!string.Equals(
                        actualPlatforms[platformIndex],
                        "DefaultTexturePlatform",
                        StringComparison.Ordinal) &&
                    overridePlatform.overridden)
                {
                    throw new InvalidOperationException(
                        "MapVisuals SpriteAtlas 禁止平台压缩 override：" + overridePlatform.name);
                }
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!atlas.CanBindTo(expected[i]))
                {
                    throw new InvalidOperationException(
                        "MapVisuals SpriteAtlas 尚未实际打包/绑定 Sprite：" + expected[i].name);
                }
            }

            ValidateSpriteLibraryAndResourceIcons(expected);
        }

        private static void ValidateResourceIconImporters()
        {
            var validatedCount = 0;
            var identities = MapFeedbackVisualEditorAssetBuilder.LockedAtlasSources;
            for (var i = 0; i < identities.Length; i++)
            {
                var identity = identities[i];
                if (identity.LocalId != 21300000L)
                {
                    continue;
                }

                validatedCount++;
                var path = AssetDatabase.GUIDToAssetPath(identity.Guid);
                var importer = string.IsNullOrEmpty(path)
                    ? null
                    : AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null ||
                    importer.textureCompression != TextureImporterCompression.Uncompressed ||
                    importer.crunchedCompression)
                {
                    throw new InvalidOperationException(
                        "Atlas 资源图 TextureImporter 必须为 Uncompressed 且禁用 Crunch：" +
                        identity.Guid);
                }

                var actualPlatforms =
                    MapFeedbackVisualEditorAssetBuilder.EnumerateTextureImporterPlatformNames(importer);
                for (var platformIndex = 0; platformIndex < actualPlatforms.Count; platformIndex++)
                {
                    var platform = actualPlatforms[platformIndex];
                    if (!string.Equals(
                            platform,
                            "DefaultTexturePlatform",
                            StringComparison.Ordinal) &&
                        importer.GetPlatformTextureSettings(platform).overridden)
                    {
                        throw new InvalidOperationException(
                            "Atlas 资源图禁止平台压缩 override：" + identity.Guid + " / " + platform);
                    }
                }
            }

            if (validatedCount != MapFeedbackVisualEditorAssetBuilder.ExpectedResourceIconSpriteCount)
            {
                throw new InvalidOperationException("Atlas 资源图 importer 合同必须恰好覆盖 5 个 Sprite。");
            }
        }

        private static void ValidateSpriteLibraryAndResourceIcons(IReadOnlyList<Sprite> expectedResourceIcons)
        {
            var library = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(
                MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath);
            if (library == null)
            {
                throw new InvalidOperationException("缺少 MapVisualSpriteLibrary 生产资产。");
            }
            if (!library.TryValidateConfiguration(out var reason) ||
                library.ResourceTokens.Count != 5)
            {
                throw new InvalidOperationException(
                    "MapVisualSpriteLibrary 无效或资源 Sprite 不恰好为 5 个：" + reason);
            }

            var generated = new[]
            {
                library.Hotspot,
                library.EmptyInfluenceSlot,
                library.OccupiedInfluenceSlot,
                library.MovableInfluenceBorder,
                library.PlacementFeedbackRing,
                library.MobileCity,
                library.ScoreMarker,
                library.ScoreMarkerBorder
            };
            if (generated.Length != MapFeedbackVisualEditorAssetBuilder.LockedGeneratedSprites.Length)
            {
                throw new InvalidOperationException("生成 Sprite 合同数量必须恰好为 8。");
            }
            var textures = new Texture2D[generated.Length];
            var persistentIdentities = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < generated.Length; i++)
            {
                var sprite = generated[i];
                var expectedName = MapFeedbackVisualEditorAssetBuilder.LockedGeneratedSprites[i];
                if (sprite == null || sprite.packed ||
                    sprite.name != expectedName.SpriteName ||
                    AssetDatabase.GetAssetPath(sprite) != MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath ||
                    sprite.texture == null ||
                    sprite.texture.name != expectedName.TextureName ||
                    AssetDatabase.GetAssetPath(sprite.texture) !=
                    MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath)
                {
                    throw new InvalidOperationException(
                        "MapVisualSpriteLibrary 生成 Sprite 必须保持为未打包的持久子资产，索引 " + i + "。");
                }
                textures[i] = sprite.texture;
                if (!persistentIdentities.Add(GetPersistentIdentity(sprite)) ||
                    !persistentIdentities.Add(GetPersistentIdentity(sprite.texture)))
                {
                    throw new InvalidOperationException(
                        "MapVisualSprites 的 8 Sprite/8 Texture 持久身份必须两两互异。");
                }

                var spriteAtlas = new SerializedObject(sprite).FindProperty("m_SpriteAtlas");
                if (spriteAtlas == null || spriteAtlas.objectReferenceValue != null)
                {
                    throw new InvalidOperationException(
                        "生成 Sprite 禁止持有 SpriteAtlas 引用：" + sprite.name);
                }
            }

            var librarySerialized = new SerializedObject(library);
            ValidateRetainedReferences(
                librarySerialized,
                "retainedSprites",
                generated.Cast<Object>().ToArray());
            ValidateRetainedReferences(
                librarySerialized,
                "retainedTextures",
                textures.Cast<Object>().ToArray());

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(
                MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath);
            var loadedSprites = allAssets.OfType<Sprite>().Cast<Object>().ToArray();
            var loadedTextures = allAssets.OfType<Texture2D>().Cast<Object>().ToArray();
            if (allAssets.Length != 1 + generated.Length + textures.Length ||
                allAssets.Count(item => item == library) != 1 ||
                allAssets.Any(item => item != library && !(item is Sprite) && !(item is Texture2D)) ||
                !HaveSamePersistentIdentities(loadedSprites, generated.Cast<Object>().ToArray()) ||
                !HaveSamePersistentIdentities(loadedTextures, textures.Cast<Object>().ToArray()))
            {
                throw new InvalidOperationException(
                    "MapVisualSprites.asset 必须恰好由主 Library、8 Sprite 与 8 Texture 构成。");
            }

            var actualResourceIcons = new List<Object>();
            for (var i = 0; i < library.ResourceTokens.Count; i++)
            {
                actualResourceIcons.Add(library.ResourceTokens[i].Sprite);
            }

            if (actualResourceIcons.Count != MapFeedbackVisualEditorAssetBuilder.ExpectedAtlasSpriteCount ||
                !HaveSamePersistentIdentities(
                    actualResourceIcons.ToArray(),
                    expectedResourceIcons.Cast<Object>().ToArray()))
            {
                throw new InvalidOperationException(
                    "MapVisualSpriteLibrary 的 5 个资源 Sprite 未精确覆盖 Atlas 来源。");
            }

            ValidateResourceTokenMappings(library);

            var source = File.ReadAllText(MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath);
            if (source.Contains("guid: 00000000000000000000000000000000") ||
                Regex.Matches(
                    source,
                    @"^[ \t]+m_SpriteAtlas:\s*\{fileID:\s*0\}\s*$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant).Count != generated.Length)
            {
                throw new InvalidOperationException(
                    "MapVisualSprites.asset 含有无效的全零 GUID，禁止以损坏 Atlas 引用通过门禁。");
            }
        }

        internal static void ValidateResourceTokenMappings(MapVisualSpriteLibrary library)
        {
            if (library == null ||
                library.ResourceTokens.Count != MapFeedbackVisualEditorAssetBuilder.LockedResourceTokens.Length)
            {
                throw new InvalidOperationException("资源 token 映射必须恰好包含 5 项。");
            }

            var expected = MapFeedbackVisualEditorAssetBuilder.LockedResourceTokens
                .ToDictionary(
                    item => ResourceTokenKey(item.ResourceType, item.Amount),
                    item => item,
                    StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < library.ResourceTokens.Count; i++)
            {
                var binding = library.ResourceTokens[i];
                if (binding == null)
                {
                    throw new InvalidOperationException("资源 token 映射含空项，索引 " + i + "。");
                }

                var key = ResourceTokenKey(binding.ResourceType, binding.Amount);
                if (!expected.TryGetValue(key, out var identity) || !seen.Add(key) ||
                    binding.Sprite == null ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        binding.Sprite,
                        out var guid,
                        out long localId) ||
                    !string.Equals(guid, identity.Guid, StringComparison.OrdinalIgnoreCase) ||
                    localId != identity.LocalId)
                {
                    throw new InvalidOperationException(
                        "资源 token 的 (ResourceType, amount)->GUID/localID 映射漂移：" + key);
                }
            }
            if (seen.Count != expected.Count || !seen.SetEquals(expected.Keys))
            {
                throw new InvalidOperationException("资源 token key 未精确覆盖锁定集合。");
            }
        }

        private static string ResourceTokenKey(YC.Domain.Rules.ResourceType type, int amount)
        {
            return type + ":" + amount;
        }

        private static void ValidateRetainedReferences(
            SerializedObject serialized,
            string propertyName,
            Object[] expected)
        {
            var property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray || property.arraySize != expected.Length)
            {
                throw new InvalidOperationException(propertyName + " 必须恰好保留 " + expected.Length + " 项。");
            }
            for (var i = 0; i < expected.Length; i++)
            {
                if (property.GetArrayElementAtIndex(i).objectReferenceValue != expected[i])
                {
                    throw new InvalidOperationException(
                        propertyName + " 与公开生成资产身份/顺序不一致，索引 " + i + "。");
                }
            }
        }

        private static void ValidateClip(AnimationClip clip)
        {
            if (clip == null || clip.legacy || clip.wrapMode != WrapMode.Once ||
                !Approximately(clip.frameRate, 60f) || !Approximately(clip.length, 0.15f))
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback Clip 的长度、采样率、Legacy 或 WrapMode 无效。");
            }

            var expected = new Dictionary<string, CurveExpectation>(StringComparer.Ordinal)
            {
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.FlashPath, typeof(Transform), "m_LocalScale.x"), new CurveExpectation(1.2f, 1f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.FlashPath, typeof(Transform), "m_LocalScale.y"), new CurveExpectation(1.2f, 1f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.FlashPath, typeof(Transform), "m_LocalScale.z"), new CurveExpectation(1.2f, 1f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.FlashPath, typeof(SpriteRenderer), "m_Color.a"), new CurveExpectation(1f, 0.35f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.RingPath, typeof(Transform), "m_LocalScale.x"), new CurveExpectation(1f, 1.8f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.RingPath, typeof(Transform), "m_LocalScale.y"), new CurveExpectation(1f, 1.8f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.RingPath, typeof(Transform), "m_LocalScale.z"), new CurveExpectation(1f, 1.8f) },
                { CurveKey(MapFeedbackVisualEditorAssetBuilder.RingPath, typeof(SpriteRenderer), "m_Color.a"), new CurveExpectation(1f, 0f) }
            };

            var bindings = AnimationUtility.GetCurveBindings(clip);
            if (bindings.Length != expected.Count ||
                AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0)
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback Clip 必须恰好包含 8 条浮点曲线且不含对象曲线。");
            }

            for (var i = 0; i < bindings.Length; i++)
            {
                var key = CurveKey(bindings[i].path, bindings[i].type, bindings[i].propertyName);
                if (!expected.TryGetValue(key, out var values))
                {
                    throw new InvalidOperationException("Clip 包含未锁定曲线：" + key);
                }

                var curve = AnimationUtility.GetEditorCurve(clip, bindings[i]);
                RequireLinearCurve(key, curve, values.Start, values.End);
            }

            var events = AnimationUtility.GetAnimationEvents(clip);
            if (events.Length != 1 || !Approximately(events[0].time, 0.15f) ||
                events[0].functionName != MapFeedbackVisualEditorAssetBuilder.CompletionEventName ||
                events[0].messageOptions != SendMessageOptions.RequireReceiver)
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback Clip 必须在 0.15 秒恰好发送一次完成事件。");
            }
        }

        private static void ValidateController(AnimatorController controller, AnimationClip clip)
        {
            if (controller == null || controller.parameters.Length != 0 ||
                controller.layers.Length != 1)
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback AnimatorController 必须恰好一层且无参数。");
            }

            var layer = controller.layers[0];
            var machine = layer.stateMachine;
            var serializedController = new SerializedObject(controller);
            var serializedLayers = serializedController.FindProperty("m_AnimatorLayers");
            var serializedSyncedLayer = serializedLayers == null || !serializedLayers.isArray ||
                                        serializedLayers.arraySize != 1
                ? null
                : serializedLayers.GetArrayElementAtIndex(0)
                    .FindPropertyRelative("m_SyncedLayerIndex");
            if (layer.name != "Base Layer" || !Approximately(layer.defaultWeight, 0f) ||
                layer.avatarMask != null || layer.blendingMode != AnimatorLayerBlendingMode.Override ||
                layer.iKPass || layer.syncedLayerIndex != -1 ||
                serializedSyncedLayer == null || serializedSyncedLayer.intValue != -1 ||
                machine == null || machine.behaviours.Length != 0 || machine.states.Length != 1 ||
                machine.stateMachines.Length != 0 || machine.anyStateTransitions.Length != 0 ||
                machine.entryTransitions.Length != 0)
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback Controller 层或状态机结构已漂移。");
            }
            var state = machine.states[0].state;
            if (state == null || state.name != MapFeedbackVisualEditorAssetBuilder.StateName ||
                state.motion != clip || !Approximately(state.speed, 1f) ||
                !Approximately(state.cycleOffset, 0f) || state.mirror || state.iKOnFeet ||
                state.behaviours.Length != 0 || state.speedParameterActive ||
                state.mirrorParameterActive || state.cycleOffsetParameterActive ||
                state.timeParameterActive || !string.IsNullOrEmpty(state.speedParameter) ||
                !string.IsNullOrEmpty(state.mirrorParameter) ||
                !string.IsNullOrEmpty(state.cycleOffsetParameter) ||
                !string.IsNullOrEmpty(state.timeParameter) ||
                !state.writeDefaultValues || state.transitions.Length != 0 ||
                machine.defaultState != state)
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback Controller 默认状态未唯一、直接引用锁定 Clip。");
            }
            ValidateControllerRawContract(
                serializedSyncedLayer.intValue,
                RequireSerializedArraySize(machine, "m_StateMachineBehaviours"),
                RequireSerializedArraySize(state, "m_StateMachineBehaviours"),
                state.speedParameterActive,
                state.mirrorParameterActive,
                state.cycleOffsetParameterActive,
                state.timeParameterActive,
                state.speedParameter,
                state.mirrorParameter,
                state.cycleOffsetParameter,
                state.timeParameter);
        }

        internal static void ValidateControllerRawContract(
            int syncedLayerIndex,
            int stateMachineBehaviourCount,
            int stateBehaviourCount,
            bool speedParameterActive,
            bool mirrorParameterActive,
            bool cycleOffsetParameterActive,
            bool timeParameterActive,
            string speedParameter,
            string mirrorParameter,
            string cycleOffsetParameter,
            string timeParameter)
        {
            if (syncedLayerIndex != -1 || stateMachineBehaviourCount != 0 ||
                stateBehaviourCount != 0 || speedParameterActive || mirrorParameterActive ||
                cycleOffsetParameterActive || timeParameterActive ||
                !string.IsNullOrEmpty(speedParameter) || !string.IsNullOrEmpty(mirrorParameter) ||
                !string.IsNullOrEmpty(cycleOffsetParameter) || !string.IsNullOrEmpty(timeParameter))
            {
                throw new InvalidOperationException(
                    "Controller raw contract 要求 unsynced、无 behaviours、参数开关关闭且参数名为空。");
            }
        }

        private static void ValidatePrefabBindings()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapViewPrefabPath);
            var assets = MapFeedbackVisualEditorAssetBuilder.LoadRequiredAssets();
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 MapView Prefab。");
            }

            var views = prefab.GetComponentsInChildren<MapView>(true);
            var canonicalSpriteLibrary = AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(
                MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath);
            if (views.Length != 1 || views[0].gameObject != prefab)
            {
                throw new InvalidOperationException("MapView Prefab 根必须恰好包含一个 MapView。");
            }
            ValidateCanonicalSpriteLibrary(views[0], canonicalSpriteLibrary);

            var feedbacks = prefab.GetComponentsInChildren<MapPlacementFeedback>(true);
            var pulses = prefab.GetComponentsInChildren<MapHighlightPulse>(true);
            var animators = prefab.GetComponentsInChildren<Animator>(true);
            if (feedbacks.Length != ExpectedPlacementFeedbackCount ||
                pulses.Length != ExpectedPlacementFeedbackCount ||
                animators.Length != ExpectedPlacementFeedbackCount)
            {
                throw new InvalidOperationException(
                    "MapView Prefab 必须恰好包含 99 个 PlacementFeedback、Pulse 与 Animator。");
            }

            ValidateFeedbackTopology(prefab);

            var materialRenderers = new HashSet<SpriteRenderer>();
            for (var i = 0; i < feedbacks.Length; i++)
            {
                var serialized = new SerializedObject(feedbacks[i]);
                var flash = RequireObject<SpriteRenderer>(serialized, "flashRenderer");
                var ring = RequireObject<SpriteRenderer>(serialized, "expandingRingRenderer");
                var animator = RequireObject<Animator>(serialized, "animator");
                ValidateFeedbackPresentationContract(
                    feedbacks[i],
                    canonicalSpriteLibrary,
                    assets);
                if (!feedbacks[i].enabled || flash.enabled || ring.enabled ||
                    flash.sprite != canonicalSpriteLibrary.PlacementFeedbackRing ||
                    ring.sprite != canonicalSpriteLibrary.PlacementFeedbackRing ||
                    !HasCanonicalFeedbackTransform(flash.transform) ||
                    !HasCanonicalFeedbackTransform(ring.transform) ||
                    animator.gameObject != feedbacks[i].gameObject || animator.enabled ||
                    animator.runtimeAnimatorController != assets.PlacementController ||
                    animator.updateMode != AnimatorUpdateMode.UnscaledTime ||
                    animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                    animator.applyRootMotion)
                {
                    throw new InvalidOperationException(
                        "PlacementFeedback Animator 必须在 owner 上关闭待机、UnscaledTime、AlwaysAnimate 并引用唯一 Controller。");
                }
                materialRenderers.Add(flash);
                materialRenderers.Add(ring);
            }

            for (var i = 0; i < pulses.Length; i++)
            {
                materialRenderers.Add(RequireObject<SpriteRenderer>(
                    new SerializedObject(pulses[i]),
                    "border"));
            }

            if (materialRenderers.Count != ExpectedSharedMaterialRendererCount ||
                materialRenderers.Any(renderer => renderer.sharedMaterial != assets.FeedbackMaterial))
            {
                throw new InvalidOperationException(
                    "地图反馈共享材质必须精确覆盖 22 Hotspot、77 Border 与 198 Placement renderer。");
            }

            var actualMaterialUsers = prefab.GetComponentsInChildren<SpriteRenderer>(true)
                .Where(renderer => renderer.sharedMaterial == assets.FeedbackMaterial)
                .ToArray();
            if (actualMaterialUsers.Length != ExpectedSharedMaterialRendererCount ||
                actualMaterialUsers.Any(renderer => !materialRenderers.Contains(renderer)))
            {
                throw new InvalidOperationException(
                    "MapFeedbackAdditive Material 在 MapView Prefab 中必须恰好有 297 个合法用户。");
            }

            var dependencies = new HashSet<string>(
                AssetDatabase.GetDependencies(MapViewPrefabPath, true),
                StringComparer.Ordinal);
            if (!dependencies.Contains(MapFeedbackVisualEditorAssetBuilder.MaterialPath) ||
                !dependencies.Contains(MapFeedbackVisualEditorAssetBuilder.ControllerPath) ||
                !dependencies.Contains(MapFeedbackVisualEditorAssetBuilder.ClipPath) ||
                !dependencies.Contains(MapFeedbackVisualEditorAssetBuilder.SpriteLibraryPath))
            {
                throw new InvalidOperationException(
                    "MapView Prefab 未形成到 Material/Controller/Clip/SpriteLibrary 的真实生产依赖链。");
            }
        }

        internal static void ValidateCanonicalSpriteLibrary(
            MapView view,
            MapVisualSpriteLibrary canonicalSpriteLibrary)
        {
            if (view == null || canonicalSpriteLibrary == null ||
                view.SpriteLibrary != canonicalSpriteLibrary)
            {
                throw new InvalidOperationException(
                    "MapView Prefab 必须显式引用 canonical MapVisualSprites.asset。");
            }
        }

        internal static void ValidateFeedbackPresentationContract(
            MapPlacementFeedback feedback,
            MapVisualSpriteLibrary canonicalSpriteLibrary,
            MapFeedbackVisualAssetSet assets)
        {
            if (feedback == null || canonicalSpriteLibrary == null || assets == null)
            {
                throw new ArgumentNullException("反馈表现合同输入不可为空。");
            }
            var serialized = new SerializedObject(feedback);
            var flash = RequireObject<SpriteRenderer>(serialized, "flashRenderer");
            var ring = RequireObject<SpriteRenderer>(serialized, "expandingRingRenderer");
            var animator = RequireObject<Animator>(serialized, "animator");
            if (!feedback.enabled || flash.enabled || ring.enabled ||
                flash.sprite != canonicalSpriteLibrary.PlacementFeedbackRing ||
                ring.sprite != canonicalSpriteLibrary.PlacementFeedbackRing ||
                flash.sharedMaterial != assets.FeedbackMaterial ||
                ring.sharedMaterial != assets.FeedbackMaterial ||
                !HasCanonicalFeedbackTransform(flash.transform) ||
                !HasCanonicalFeedbackTransform(ring.transform) ||
                animator.gameObject != feedback.gameObject || animator.enabled ||
                animator.runtimeAnimatorController != assets.PlacementController ||
                animator.updateMode != AnimatorUpdateMode.UnscaledTime ||
                animator.cullingMode != AnimatorCullingMode.AlwaysAnimate ||
                animator.applyRootMotion)
            {
                throw new InvalidOperationException(
                    "PlacementFeedback 必须锁定启用状态、初始隐藏 renderer、canonical ring/材质/Transform 与 Animator。");
            }
        }

        private static void ValidateSampleSceneInstanceOverrides()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapViewPrefabPath);
            if (prefab == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    prefab,
                    out _,
                    out long rootGameObjectLocalId) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    prefab.transform,
                    out _,
                    out long rootTransformLocalId))
            {
                throw new InvalidOperationException(
                    "无法解析 MapView Prefab 根对象/Transform 的持久 fileID。");
            }

            var sceneYaml = File.ReadAllText(SampleScenePath);
            ValidateSavedMapViewInstanceYaml(
                SampleScenePath,
                sceneYaml,
                AssetDatabase.AssetPathToGUID(MapViewPrefabPath),
                rootGameObjectLocalId,
                rootTransformLocalId);
        }

        internal static void ValidateSavedMapViewInstanceYaml(
            string scenePath,
            string yaml,
            string mapPrefabGuid,
            long rootGameObjectLocalId,
            long rootTransformLocalId)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(mapPrefabGuid))
            {
                throw new InvalidOperationException(scenePath + " 场景或 MapView Prefab GUID 为空。");
            }

            var mapRootTransformLocalId = ResolveUniqueMapRootTransformLocalId(scenePath, yaml);

            var sourceReference = new Regex(
                @"m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*" +
                Regex.Escape(mapPrefabGuid) + @",\s*type:\s*3\}",
                RegexOptions.CultureInvariant);
            var blocks = Regex.Matches(
                    yaml,
                    @"---\s*!u!1001\s*&[^\r\n]+[\s\S]*?(?=\r?\n---\s*!u!|\z)",
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Where(match => sourceReference.IsMatch(match.Value))
                .Select(match => match.Value)
                .ToArray();
            if (blocks.Length != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 必须恰好包含一个 MapView PrefabInstance YAML block。");
            }

            var block = blocks[0];
            var exactSourceReferences = Regex.Matches(
                block,
                @"^[ \t]+m_SourcePrefab:\s*\{fileID:\s*100100000,\s*guid:\s*" +
                Regex.Escape(mapPrefabGuid) + @",\s*type:\s*3\}\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (exactSourceReferences.Count != 1 ||
                Regex.Matches(
                    block,
                    @"^[ \t]+m_SourcePrefab\s*:",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant).Count != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 的 MapView PrefabInstance 必须恰好包含一个 canonical m_SourcePrefab。");
            }

            var transformParents = Regex.Matches(
                block,
                @"^[ \t]+m_TransformParent\s*:\s*\{fileID:\s*(?<id>-?\d+)\}\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (transformParents.Count != 1 ||
                !long.TryParse(transformParents[0].Groups["id"].Value, out var actualParentId) ||
                actualParentId != mapRootTransformLocalId ||
                Regex.Matches(
                    block,
                    @"^[ \t]+m_TransformParent\s*:",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant).Count != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 的 MapView PrefabInstance 必须直属唯一 MapRoot Transform。");
            }
            foreach (var section in new[]
                     {
                         "m_RemovedComponents", "m_RemovedGameObjects",
                         "m_AddedGameObjects", "m_AddedComponents"
                     })
            {
                var sectionOccurrences = Regex.Matches(
                    block,
                    @"^[ \t]+" + Regex.Escape(section) + @"\s*:",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
                var exactEmptySections = Regex.Matches(
                    block,
                    @"^[ \t]+" + Regex.Escape(section) + @":\s*\[\]\s*$",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
                if (sectionOccurrences.Count != 1 || exactEmptySections.Count != 1)
                {
                    throw new InvalidOperationException(
                        scenePath + " 的 MapView PrefabInstance 禁止 removed/added override：" + section);
                }
            }

            var modifications = Regex.Matches(
                block,
                @"^[ \t]+m_Modifications:\s*\r?\n(?<body>[\s\S]*?)(?=^[ \t]+m_RemovedComponents:)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (modifications.Count != 1 ||
                Regex.Matches(
                    block,
                    @"^[ \t]+m_Modifications\s*:",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant).Count != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 的 MapView PrefabInstance 缺少可验证的 m_Modifications。");
            }

            var body = modifications[0].Groups["body"].Value;
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { rootGameObjectLocalId + ":m_Name", "MapView" },
                { rootTransformLocalId + ":m_LocalPosition.x", "0" },
                { rootTransformLocalId + ":m_LocalPosition.y", "0" },
                { rootTransformLocalId + ":m_LocalPosition.z", "0" },
                { rootTransformLocalId + ":m_LocalRotation.w", "1" },
                { rootTransformLocalId + ":m_LocalRotation.x", "0" },
                { rootTransformLocalId + ":m_LocalRotation.y", "0" },
                { rootTransformLocalId + ":m_LocalRotation.z", "0" },
                { rootTransformLocalId + ":m_LocalEulerAnglesHint.x", "0" },
                { rootTransformLocalId + ":m_LocalEulerAnglesHint.y", "0" },
                { rootTransformLocalId + ":m_LocalEulerAnglesHint.z", "0" }
            };
            var entryStarts = Regex.Matches(
                body,
                @"^[ \t]*-\s*target:",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (entryStarts.Count != expected.Count ||
                (entryStarts.Count > 0 &&
                 !string.IsNullOrWhiteSpace(body.Substring(0, entryStarts[0].Index))))
            {
                throw new InvalidOperationException(
                    scenePath + " 的 MapView PrefabInstance 必须恰好包含 11 个完整 modification entry。");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var entryIndex = 0; entryIndex < entryStarts.Count; entryIndex++)
            {
                var start = entryStarts[entryIndex].Index;
                var end = entryIndex + 1 < entryStarts.Count
                    ? entryStarts[entryIndex + 1].Index
                    : body.Length;
                var entryText = body.Substring(start, end - start);
                var entry = Regex.Match(
                    entryText,
                    @"\A[ \t]*-\s*target:\s*\{fileID:\s*(?<id>-?\d+),\s*guid:\s*(?<guid>[0-9a-fA-F]{32}),\s*type:\s*3\}\s*\r?\n" +
                    @"[ \t]+propertyPath:\s*(?<path>[^\r\n]+)\r?\n" +
                    @"[ \t]+value:\s*(?<value>[^\r\n]*)\r?\n" +
                    @"[ \t]+objectReference:\s*(?<object>\{[^\r\n]+\})[ \t]*(?:\r?\n)?\z",
                    RegexOptions.CultureInvariant);
                if (!entry.Success)
                {
                    throw new InvalidOperationException(
                        scenePath + " 的 MapView PrefabInstance 含附加字段或无法解析的 modification entry。");
                }

                if (!string.Equals(
                        entry.Groups["guid"].Value,
                        mapPrefabGuid,
                        StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(entry.Groups["id"].Value, out var localId))
                {
                    throw new InvalidOperationException(
                        scenePath + " 的 MapView modification target 身份无效。");
                }

                var propertyPath = entry.Groups["path"].Value.Trim();
                var key = localId + ":" + propertyPath;
                if (!expected.TryGetValue(key, out var expectedValue) ||
                    !string.Equals(
                        entry.Groups["value"].Value.Trim(),
                        expectedValue,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        entry.Groups["object"].Value.Trim(),
                        "{fileID: 0}",
                        StringComparison.Ordinal) ||
                    !seen.Add(key))
                {
                    throw new InvalidOperationException(
                        scenePath + " 禁止 MapView 子对象/组件或重复场景 override：" + propertyPath);
                }
            }
            if (seen.Count != expected.Count || !seen.SetEquals(expected.Keys))
            {
                throw new InvalidOperationException(
                    scenePath + " 的 MapView 根覆盖必须精确锁定名称、位置与旋转共 11 项。");
            }
        }

        private static long ResolveUniqueMapRootTransformLocalId(string scenePath, string yaml)
        {
            var serializedBlocks = Regex.Matches(
                    yaml,
                    @"^---\s*!u!(?<type>\d+)\s*&(?<id>-?\d+)[^\r\n]*\r?\n(?<body>[\s\S]*?)(?=^---\s*!u!|\z)",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Cast<Match>()
                .ToArray();
            var mapRoots = serializedBlocks
                .Where(match => match.Groups["type"].Value == "1" &&
                                Regex.IsMatch(
                                    match.Groups["body"].Value,
                                    @"^[ \t]+m_Name:\s*MapRoot\s*$",
                                    RegexOptions.Multiline | RegexOptions.CultureInvariant) &&
                                Regex.IsMatch(
                                    match.Groups["body"].Value,
                                    @"^[ \t]+m_PrefabInstance:\s*\{fileID:\s*0\}\s*$",
                                    RegexOptions.Multiline | RegexOptions.CultureInvariant))
                .ToArray();
            if (mapRoots.Length != 1)
            {
                throw new InvalidOperationException(scenePath + " 必须恰好包含一个直接序列化的 MapRoot。");
            }

            if (!long.TryParse(mapRoots[0].Groups["id"].Value, out var mapRootGameObjectId))
            {
                throw new InvalidOperationException(scenePath + " 无法解析 MapRoot GameObject fileID。");
            }

            var componentIds = new HashSet<long>(
                Regex.Matches(
                        mapRoots[0].Groups["body"].Value,
                        @"^[ \t]*-\s*component:\s*\{fileID:\s*(?<id>-?\d+)\}\s*$",
                        RegexOptions.Multiline | RegexOptions.CultureInvariant)
                    .Cast<Match>()
                    .Select(match => long.Parse(match.Groups["id"].Value)));
            var transforms = serializedBlocks
                .Where(match => match.Groups["type"].Value == "4" &&
                                Regex.IsMatch(
                                    match.Groups["body"].Value,
                                    @"^[ \t]+m_GameObject:\s*\{fileID:\s*" +
                                    mapRootGameObjectId + @"\}\s*$",
                                    RegexOptions.Multiline | RegexOptions.CultureInvariant))
                .ToArray();
            if (transforms.Length != 1 ||
                !long.TryParse(transforms[0].Groups["id"].Value, out var transformLocalId) ||
                !componentIds.Contains(transformLocalId))
            {
                throw new InvalidOperationException(
                    scenePath + " 的唯一 MapRoot 必须恰好拥有一个可交叉验证的 Transform。");
            }
            return transformLocalId;
        }

        internal static void ValidateFeedbackTopology(GameObject root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            ValidateMapHotspotOwnerBindings(root);
            ValidateMapInfluenceSlotOwnerBindings(root);

            var feedbacks = root.GetComponentsInChildren<MapPlacementFeedback>(true);
            for (var i = 0; i < feedbacks.Length; i++)
            {
                var feedback = feedbacks[i];
                var serialized = new SerializedObject(feedback);
                var flash = RequireObject<SpriteRenderer>(serialized, "flashRenderer");
                var ring = RequireObject<SpriteRenderer>(serialized, "expandingRingRenderer");
                var animator = RequireObject<Animator>(serialized, "animator");
                var ownerAnimators = feedback.GetComponents<Animator>();
                if (flash == ring ||
                    flash.transform.parent != feedback.transform ||
                    flash.gameObject.name != MapFeedbackVisualEditorAssetBuilder.FlashPath ||
                    ring.transform.parent != feedback.transform ||
                    ring.gameObject.name != MapFeedbackVisualEditorAssetBuilder.RingPath)
                {
                    throw new InvalidOperationException(
                        "PlacementFeedback 的 flash/ring 必须互异、固定命名并直属各自 owner。");
                }
                if (ownerAnimators.Length != 1 || ownerAnimators[0] != animator)
                {
                    throw new InvalidOperationException(
                        "PlacementFeedback owner 必须恰好包含并引用一个 Animator。");
                }
            }

            var pulses = root.GetComponentsInChildren<MapHighlightPulse>(true);
            for (var i = 0; i < pulses.Length; i++)
            {
                var pulse = pulses[i];
                var border = RequireObject<SpriteRenderer>(new SerializedObject(pulse), "border");
                if (border.gameObject != pulse.gameObject)
                {
                    throw new InvalidOperationException(
                        "MapHighlightPulse 必须引用与自身位于同一 GameObject 的 border。");
                }
            }
        }

        private static void ValidateMapHotspotOwnerBindings(GameObject root)
        {
            var hotspots = root.GetComponentsInChildren<MapHotspot>(true);
            for (var i = 0; i < hotspots.Length; i++)
            {
                var hotspot = hotspots[i];
                var serialized = new SerializedObject(hotspot);
                var renderer = RequireObject<SpriteRenderer>(serialized, "spriteRenderer");
                var pulse = RequireObject<MapHighlightPulse>(serialized, "highlightPulse");
                var feedback = RequireObject<MapPlacementFeedback>(serialized, "placementFeedback");
                var pulseBorder = RequireObject<SpriteRenderer>(
                    new SerializedObject(pulse),
                    "border");
                if (renderer.gameObject != hotspot.gameObject ||
                    pulse.gameObject != hotspot.gameObject ||
                    feedback.gameObject != hotspot.gameObject ||
                    hotspot.GetComponent<SpriteRenderer>() != renderer ||
                    hotspot.GetComponent<MapHighlightPulse>() != pulse ||
                    hotspot.GetComponent<MapPlacementFeedback>() != feedback ||
                    pulseBorder != renderer)
                {
                    throw new InvalidOperationException(
                        "MapHotspot 的 renderer、pulse、feedback 与 pulse.border 必须精确绑定自身 owner。");
                }
            }

            var views = root.GetComponentsInChildren<MapView>(true);
            for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                var serialized = new SerializedObject(views[viewIndex]);
                var locations = serialized.FindProperty("locations");
                if (locations == null || !locations.isArray)
                {
                    throw new InvalidOperationException("MapView 缺少可验证的 locations 序列化列表。");
                }

                var owners = new HashSet<GameObject>();
                for (var index = 0; index < locations.arraySize; index++)
                {
                    var entry = locations.GetArrayElementAtIndex(index);
                    var id = entry.FindPropertyRelative("locationId");
                    var hotspot = RequireRelativeObject<MapHotspot>(entry, "hotspot", views[viewIndex]);
                    if (id == null || string.IsNullOrEmpty(id.stringValue) ||
                        hotspot.gameObject.name != "Hotspot " + id.stringValue ||
                        !hotspot.transform.IsChildOf(views[viewIndex].transform) ||
                        !owners.Add(hotspot.gameObject))
                    {
                        throw new InvalidOperationException(
                            "MapView location 必须按 locationId 唯一绑定自身 Hotspot owner。");
                    }
                }
            }
        }

        private static void ValidateMapInfluenceSlotOwnerBindings(GameObject root)
        {
            var views = root.GetComponentsInChildren<MapView>(true);
            for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                var view = views[viewIndex];
                var serialized = new SerializedObject(view);
                var slots = serialized.FindProperty("influenceSlots");
                if (slots == null || !slots.isArray)
                {
                    throw new InvalidOperationException("MapView 缺少可验证的 influenceSlots 序列化列表。");
                }

                var owners = new HashSet<GameObject>();
                for (var index = 0; index < slots.arraySize; index++)
                {
                    var entry = slots.GetArrayElementAtIndex(index);
                    var id = entry.FindPropertyRelative("slotId");
                    var renderer = RequireRelativeObject<SpriteRenderer>(entry, "renderer", view);
                    var collider = RequireRelativeObject<CircleCollider2D>(entry, "collider", view);
                    var clickTarget = RequireRelativeObject<InfluenceSlotClickTarget>(entry, "clickTarget", view);
                    var border = RequireRelativeObject<SpriteRenderer>(entry, "borderRenderer", view);
                    var pulse = RequireRelativeObject<MapHighlightPulse>(entry, "borderPulse", view);
                    var feedback = RequireRelativeObject<MapPlacementFeedback>(entry, "placementFeedback", view);
                    var owner = renderer.gameObject;
                    var pulseBorder = RequireObject<SpriteRenderer>(
                        new SerializedObject(pulse),
                        "border");
                    if (id == null || string.IsNullOrEmpty(id.stringValue) ||
                        owner.name != "InfluenceSlot " + id.stringValue ||
                        !owner.transform.IsChildOf(view.transform) ||
                        !owners.Add(owner) ||
                        owner.GetComponent<SpriteRenderer>() != renderer ||
                        owner.GetComponent<CircleCollider2D>() != collider ||
                        owner.GetComponent<InfluenceSlotClickTarget>() != clickTarget ||
                        owner.GetComponent<MapPlacementFeedback>() != feedback ||
                        border.transform.parent != owner.transform ||
                        border.gameObject.name != "MovableInfluenceBorder" ||
                        border.GetComponent<MapHighlightPulse>() != pulse ||
                        pulse.gameObject != border.gameObject ||
                        pulseBorder != border)
                    {
                        throw new InvalidOperationException(
                            "MapInfluenceSlot 的 renderer/collider/click/feedback 与 border/pulse 必须按 slotId 精确绑定自身 owner。");
                    }
                }
            }
        }

        private static void ValidateProductionSourceUsesAnimatorAssets()
        {
            const string sourcePath = "Assets/YC/Presentation/MapPlacementFeedback.cs";
            var source = File.ReadAllText(sourcePath);
            foreach (var forbidden in new[]
                     {
                         "StartCoroutine(", "StopCoroutine(", "Time.unscaledDeltaTime",
                         "new Material(", "new AnimationClip(", "AddComponent<Animator>"
                     })
            {
                if (source.Contains(forbidden))
                {
                    throw new InvalidOperationException(
                        sourcePath + " 仍在运行时创建/驱动视觉资产：" + forbidden);
                }
            }

            if (!source.Contains("AnimatorUpdateMode.UnscaledTime") &&
                !source.Contains("animator.updateMode"))
            {
                throw new InvalidOperationException(
                    "MapPlacementFeedback 未验证预制 Animator 的 UnscaledTime 合同。");
            }
        }

        private static T RequireObject<T>(SerializedObject serialized, string propertyName)
            where T : Object
        {
            var property = serialized.FindProperty(propertyName);
            var value = property == null ? null : property.objectReferenceValue as T;
            if (value == null)
            {
                throw new InvalidOperationException(
                    serialized.targetObject.name + " 缺少序列化引用 " + propertyName + "。");
            }
            return value;
        }

        private static bool HasCanonicalFeedbackTransform(Transform target)
        {
            return target != null && target.localPosition == Vector3.zero &&
                   target.localRotation == Quaternion.identity && target.localScale == Vector3.one;
        }

        private static void RequireEmptySerializedArray(Object target, string propertyName)
        {
            if (RequireSerializedArraySize(target, propertyName) != 0)
            {
                throw new InvalidOperationException(
                    target.name + " 的 " + propertyName + " 必须是可验证的空数组。");
            }
        }

        private static int RequireSerializedArraySize(Object target, string propertyName)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null && propertyName == "m_DisabledShaderPasses")
            {
                property = serialized.FindProperty("disabledShaderPasses");
            }
            if (property == null || !property.isArray)
            {
                throw new InvalidOperationException(
                    target.name + " 缺少可验证的序列化数组 " + propertyName + "。");
            }
            return property.arraySize;
        }

        private static T RequireRelativeObject<T>(
            SerializedProperty parent,
            string propertyName,
            Object context)
            where T : Object
        {
            var property = parent == null ? null : parent.FindPropertyRelative(propertyName);
            var value = property == null ? null : property.objectReferenceValue as T;
            if (value == null)
            {
                throw new InvalidOperationException(
                    (context == null ? "MapView" : context.name) +
                    " 缺少序列化引用 " + propertyName + "。");
            }
            return value;
        }

        private static bool HaveSamePersistentIdentities(Object[] left, Object[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            var leftIds = new HashSet<string>(left.Select(GetPersistentIdentity), StringComparer.Ordinal);
            var rightIds = new HashSet<string>(right.Select(GetPersistentIdentity), StringComparer.Ordinal);
            return leftIds.Count == left.Length && rightIds.Count == right.Length &&
                   leftIds.SetEquals(rightIds);
        }

        private static string GetPersistentIdentity(Object asset)
        {
            if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    asset,
                    out var guid,
                    out long localId) || string.IsNullOrEmpty(guid) || localId == 0)
            {
                throw new InvalidOperationException("视觉资产来源缺少持久化 GUID/localID。");
            }
            return guid + ":" + localId;
        }

        private static void RequireLinearCurve(
            string label,
            AnimationCurve curve,
            float start,
            float end)
        {
            if (curve == null || curve.length != 2 ||
                !Approximately(curve.keys[0].time, 0f) ||
                !Approximately(curve.keys[1].time, 0.15f) ||
                !Approximately(curve.keys[0].value, start) ||
                !Approximately(curve.keys[1].value, end))
            {
                throw new InvalidOperationException(label + " 关键帧未精确匹配锁定端点。");
            }

            var expectedSlope = (end - start) / 0.15f;
            var first = curve.keys[0];
            var second = curve.keys[1];
            if (!Approximately(first.outTangent, expectedSlope) ||
                !Approximately(second.inTangent, expectedSlope) ||
                !Approximately(curve.Evaluate(0.0375f), Mathf.Lerp(start, end, 0.25f)) ||
                !Approximately(curve.Evaluate(0.075f), Mathf.Lerp(start, end, 0.5f)) ||
                !Approximately(curve.Evaluate(0.1125f), Mathf.Lerp(start, end, 0.75f)))
            {
                throw new InvalidOperationException(label + " 必须保持线性插值，禁止缓入缓出。");
            }
        }

        private static string CurveKey(string path, Type type, string propertyName)
        {
            return path + "|" + type.FullName + "|" + propertyName;
        }

        private static void ValidateAssetGuid(string path, string expectedGuid)
        {
            if (!File.Exists(path) ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(path),
                    expectedGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "视觉资产缺失或 GUID 漂移：" + path + "，预期 " + expectedGuid + "。");
            }
        }

        private static bool Approximately(float left, float right)
        {
            return Mathf.Abs(left - right) <= 0.0001f;
        }

        private enum ShaderTokenKind
        {
            Identifier,
            QuotedString,
            Symbol
        }

        private readonly struct ShaderToken
        {
            public ShaderToken(string value, int line, ShaderTokenKind kind)
            {
                Value = value;
                Line = line;
                Kind = kind;
            }

            public string Value { get; }
            public int Line { get; }
            public ShaderTokenKind Kind { get; }
        }

        private readonly struct CurveExpectation
        {
            public CurveExpectation(float start, float end)
            {
                Start = start;
                End = end;
            }

            public float Start { get; }
            public float End { get; }
        }
    }
}
