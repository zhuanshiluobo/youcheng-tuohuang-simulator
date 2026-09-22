using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace YC.Editor
{
    // 只检查已保存场景中的实例与覆盖，不加载、重建或保存任何 Unity 资产。
    internal static class SavedSceneBootstrapYaml
    {
        internal static void Validate(
            string scenePath,
            string yaml,
            string prefabGuid,
            long rootLocalId,
            long bootstrapLocalId,
            string catalogGuid,
            long catalogLocalId,
            string bootstrapScriptGuid,
            string bootstrapLabel,
            string catalogProperty,
            bool requireRemovalSection = true)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                throw new InvalidOperationException(scenePath + " 的场景 YAML 为空。");
            }

            var blocks = Regex.Matches(
                yaml,
                @"^--- !u!1001 &.*?(?=^--- !u!|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            var sourceToken =
                "m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid + ", type: 3}";
            var matchingBlocks = new List<string>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Value.Contains(sourceToken))
                {
                    matchingBlocks.Add(blocks[i].Value);
                }
            }

            if (matchingBlocks.Count != 1)
            {
                throw new InvalidOperationException(
                    scenePath + " 必须恰好连接一个 GameSettings Prefab 实例，实际 " +
                    matchingBlocks.Count + " 个。");
            }

            var directScriptPattern =
                @"m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*" +
                Regex.Escape(bootstrapScriptGuid) + @",\s*type:\s*3\}";
            if (Regex.IsMatch(yaml, directScriptPattern, RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException(
                    scenePath + " 包含额外的本地 " + bootstrapLabel + " 组件。");
            }

            var block = matchingBlocks[0];
            // 内容目录要求完整移除区段；UiTheme 保留原有的可省略区段行为。
            var removedStart = block.IndexOf("m_RemovedComponents:", StringComparison.Ordinal);
            var removedEnd = block.IndexOf("m_RemovedGameObjects:", StringComparison.Ordinal);
            if (requireRemovalSection && (removedStart < 0 || removedEnd <= removedStart))
            {
                throw new InvalidOperationException(
                    scenePath + " 的 GameSettings Prefab override 结构无效。");
            }

            var removedSection = requireRemovalSection
                ? block.Substring(removedStart, removedEnd - removedStart)
                : Regex.Match(block,
                    @"m_RemovedComponents:\s*(?<items>.*?)(?=\s*m_RemovedGameObjects:|\z)",
                    RegexOptions.Singleline).Groups["items"].Value;
            var componentRemoved = requireRemovalSection
                ? removedSection.Contains("fileID: " + bootstrapLocalId) &&
                  removedSection.Contains("guid: " + prefabGuid)
                : Regex.IsMatch(removedSection,
                    @"fileID:\s*" + bootstrapLocalId + @",\s*guid:\s*" + Regex.Escape(prefabGuid),
                    RegexOptions.IgnoreCase);
            if (componentRemoved)
            {
                throw new InvalidOperationException(scenePath + " 删除了" + bootstrapLabel + " 组件。");
            }

            RejectDisabledOverride(
                scenePath,
                block,
                prefabGuid,
                rootLocalId,
                "m_IsActive",
                "GameSettings Prefab 根对象");
            RejectDisabledOverride(
                scenePath,
                block,
                prefabGuid,
                bootstrapLocalId,
                "m_Enabled",
                bootstrapLabel);

            var catalogOverrides = FindPropertyOverrides(
                block,
                prefabGuid,
                bootstrapLocalId,
                catalogProperty);
            for (var i = 0; i < catalogOverrides.Count; i++)
            {
                var referenceId = long.Parse(catalogOverrides[i].Groups["referenceId"].Value);
                var referenceGuid = catalogOverrides[i].Groups["referenceGuid"].Value;
                if (referenceId != catalogLocalId ||
                    !string.Equals(referenceGuid, catalogGuid, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        scenePath + " 覆盖了" + bootstrapLabel + " 的 " + catalogProperty + " 引用。");
                }
            }
        }

        private static void RejectDisabledOverride(
            string scenePath,
            string block,
            string prefabGuid,
            long targetLocalId,
            string propertyPath,
            string label)
        {
            var overrides = FindPropertyOverrides(
                block,
                prefabGuid,
                targetLocalId,
                propertyPath);
            for (var i = 0; i < overrides.Count; i++)
            {
                if (overrides[i].Groups["value"].Value.Trim() == "0")
                {
                    throw new InvalidOperationException(
                        scenePath + " 通过 override 禁用了" + label + "。");
                }
            }
        }

        private static MatchCollection FindPropertyOverrides(
            string block,
            string prefabGuid,
            long targetLocalId,
            string propertyPath)
        {
            var pattern =
                @"(?m)^\s*-\s+target:\s+\{fileID:\s*" + targetLocalId +
                @",\s*guid:\s*" + Regex.Escape(prefabGuid) +
                @",\s*type:\s*\d+\}\r?\n" +
                @"^\s*propertyPath:\s*" + Regex.Escape(propertyPath) + @"\s*\r?\n" +
                @"^\s*value:[ \t]*(?<value>[^\r\n]*)\r?\n" +
                @"^\s*objectReference:\s*\{fileID:\s*(?<referenceId>-?\d+)" +
                @"(?:,\s*guid:\s*(?<referenceGuid>[0-9a-f]+),\s*type:\s*\d+)?\}";
            return Regex.Matches(block, pattern, RegexOptions.IgnoreCase);
        }
    }
}
