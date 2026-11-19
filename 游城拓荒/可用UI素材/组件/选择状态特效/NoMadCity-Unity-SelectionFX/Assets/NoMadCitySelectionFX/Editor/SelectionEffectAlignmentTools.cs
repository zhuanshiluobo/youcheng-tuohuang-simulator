using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using NoMadCity.SelectionFX;

/// Explicit editor setup only: no guessed coordinates or automatic changes to original UI.
public static class SelectionEffectAlignmentTools
{
    public static bool CanAlign(SelectionEffectGraphic effect, RectTransform target)
    {
        return effect != null && target != null && effect.transform != target &&
            !target.IsChildOf(effect.transform);
    }

    public static void Align(SelectionEffectGraphic effect, RectTransform target)
    {
        if (!CanAlign(effect, target)) return;
        var rt = (RectTransform)effect.transform;
        Undo.SetTransformParent(rt, target, "Align selection overlay to available frame");
        Undo.RecordObject(rt, "Stretch selection overlay to available frame");
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(.5f, .5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;
        var position = rt.localPosition; position.z = 0; rt.localPosition = position;
        rt.SetAsLastSibling();
        PrefabUtility.RecordPrefabInstancePropertyModifications(rt);
        effect.SetVerticesDirty();
        EditorUtility.SetDirty(rt);
    }

    [MenuItem("GameObject/UI/NoMadCity/Create Overlay Aligned To This Frame", false, 2051)]
    public static void CreateAligned()
    {
        var target = Selection.activeTransform as RectTransform;
        if (target == null)
        {
            EditorUtility.DisplayDialog("Select the available frame", "First select the actual available-frame RectTransform, not its whole-row hit area or enterprise board.", "OK");
            return;
        }
        // Do not silently stack a second effect.
        for (int i=0; i<target.childCount; i++)
        {
            var existing=target.GetChild(i).GetComponent<SelectionEffectGraphic>();
            if (existing != null)
            {
                Selection.activeGameObject=existing.gameObject;
                EditorUtility.DisplayDialog("Overlay already exists", "Selected the existing child overlay. Use its Alignment Target control to realign if needed.", "OK");
                return;
            }
        }
        var child=new GameObject("Selected FX (aligned frame)", typeof(RectTransform), typeof(SelectionEffectGraphic));
        Undo.RegisterCreatedObjectUndo(child, "Create aligned selection overlay");
        var effect=child.GetComponent<SelectionEffectGraphic>();
        Align(effect,target);
        effect.ApplyStrongVisiblePreset();
        effect.SetState(SelectionEffectGraphic.SelectionState.Available);
        Selection.activeGameObject=child;
    }
}
