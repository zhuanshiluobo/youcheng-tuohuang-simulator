using UnityEditor;
using UnityEngine;
using UnityEditor.UI;
using NoMadCity.SelectionFX;

[CustomEditor(typeof(SelectionEffectGraphic)), CanEditMultipleObjects]
public sealed class SelectionEffectGraphicEditor : GraphicEditor
{
    private RectTransform alignmentTarget;

    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        serializedObject.Update();
        foreach (string name in new[] { "state", "lineWidth", "cornerCut", "glowRadius", "reducedMotion", "selectedBadge" })
            EditorGUILayout.PropertyField(serializedObject.FindProperty(name));
        serializedObject.ApplyModifiedProperties();
        if (GUILayout.Button("Apply Strong Visible Preset (keeps State / Reduced Motion)"))
        {
            foreach (var item in targets)
            {
                Undo.RecordObject(item, "Apply strong selection preset");
                ((SelectionEffectGraphic)item).ApplyStrongVisiblePreset();
                EditorUtility.SetDirty(item);
                PrefabUtility.RecordPrefabInstancePropertyModifications(item);
            }
        }
        if (!Application.isPlaying && targets.Length == 1)
        {
            EditorGUILayout.Space();
            alignmentTarget=(RectTransform)EditorGUILayout.ObjectField("Alignment Target", alignmentTarget, typeof(RectTransform), true);
            var fx=(SelectionEffectGraphic)target;
            using (new EditorGUI.DisabledScope(!SelectionEffectAlignmentTools.CanAlign(fx,alignmentTarget)))
            {
                if (GUILayout.Button("Align under target frame (reparents this overlay)"))
                    SelectionEffectAlignmentTools.Align(fx,alignmentTarget);
            }
            EditorGUILayout.HelpBox("Optional setup: drag the EXISTING available visual frame, not the row hit area. Align moves this overlay under it and stretches to its rectangle; original Image/state is untouched. This changes hierarchy/mask inheritance. Target must stay active in every state. Matching rectangles cannot reproduce an art texture's irregular silhouette.",MessageType.Info);
        }
        if (Application.isPlaying && targets.Length == 1)
        {
            var fx = (SelectionEffectGraphic)target;
            EditorGUILayout.LabelField("State / reduced motion", fx.State + " / " + fx.ReducedMotion);
            EditorGUILayout.LabelField("Last mesh animation clock", fx.LastUploadedAnimationTime.ToString("F2"));
            EditorGUILayout.LabelField("Effect material loaded", fx.HasEffectMaterial.ToString());
            if (GUILayout.Button("DEBUG Force Selected + Replay")) fx.DebugForceSelected();
            if (GUILayout.Button("DEBUG Force Available")) fx.DebugForceAvailable();
            Repaint();
        }
        EditorGUILayout.HelpBox("v3: nonselected draws nothing; original game UI owns disabled/hover feedback. Test in Play Mode: Selected + Reduced Motion off. Moving clock proves mesh updates, not successful GPU rendering. Game code may overwrite debug state. Keep Raycast Target off.", MessageType.Info);
    }
}
