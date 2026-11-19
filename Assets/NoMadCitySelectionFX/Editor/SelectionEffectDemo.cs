using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using NoMadCity.SelectionFX;

public static class SelectionEffectDemo
{
    [MenuItem("GameObject/UI/NoMadCity/Selection FX Comparison", false, 2050)]
    public static void Create()
    {
        var root = new GameObject("NoMadCity FX Comparison v6", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(SelectionEffectDemoControls));
        Undo.RegisterCreatedObjectUndo(root, "Create selection FX comparison");
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        root.GetComponent<Canvas>().sortingOrder = 100;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080);
        string[] names = { "Enterprise", "Department", "Effect row", "City", "Card" };
        Vector2[] sizes = { new Vector2(105,300), new Vector2(150,291), new Vector2(285,132), new Vector2(270,181), new Vector2(210,300) };
        float[] xs = { -700,-380,-30,330,690 };
        var selected = new SelectionEffectGraphic[5];
        for (int row=0;row<2;row++) for (int i=0;i<5;i++)
        {
            var board = new GameObject(names[i] + (row==0 ? " Available" : " Selected"), typeof(RectTransform), typeof(Image));
            board.transform.SetParent(root.transform,false);
            var rt = (RectTransform)board.transform;
            rt.sizeDelta = sizes[i]; rt.anchoredPosition = new Vector2(xs[i],row==0 ? 100 : -275);
            board.GetComponent<Image>().color = new Color(.12f,.105f,.08f,1);
            board.GetComponent<Image>().raycastTarget = false;
            var overlay = new GameObject("Procedural Border", typeof(RectTransform), typeof(SelectionEffectGraphic));
            overlay.transform.SetParent(board.transform,false);
            var rect = (RectTransform)overlay.transform;
            rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.offsetMin=rect.offsetMax=Vector2.zero;
            var fx = overlay.GetComponent<SelectionEffectGraphic>();
            fx.ApplyStrongVisiblePreset();
            fx.SetState(row==0 ? SelectionEffectGraphic.SelectionState.Available : SelectionEffectGraphic.SelectionState.Selected);
            if (row==1) selected[i]=fx;
        }
        root.GetComponent<SelectionEffectDemoControls>().effects=selected;
        Selection.activeGameObject = root;
    }
}
