using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using NoMadCity.SelectionFX;

public static class SelectionEffectDemo
{
    [MenuItem("GameObject/UI/NoMadCity/Selection FX Comparison", false, 2050)]
    public static void Create()
    {
        var root = new GameObject("NoMadCity FX Comparison", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        Undo.RegisterCreatedObjectUndo(root, "Create selection FX comparison");
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080);
        string[] names = { "Enterprise Available", "Department Selected", "City Unavailable", "Card Selected" };
        Vector2[] sizes = { new Vector2(190,541), new Vector2(240,465), new Vector2(380,255), new Vector2(240,340) };
        float[] xs = { -660,-330,90,550 };
        for (int i=0;i<4;i++)
        {
            var board = new GameObject(names[i], typeof(RectTransform), typeof(Image));
            board.transform.SetParent(root.transform,false);
            var rt = (RectTransform)board.transform;
            rt.sizeDelta = sizes[i]; rt.anchoredPosition = new Vector2(xs[i],0);
            board.GetComponent<Image>().color = new Color(.12f,.105f,.08f,1);
            board.GetComponent<Image>().raycastTarget = false;
            var overlay = new GameObject("Procedural Border", typeof(RectTransform), typeof(SelectionEffectGraphic));
            overlay.transform.SetParent(board.transform,false);
            var rect = (RectTransform)overlay.transform;
            rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.offsetMin=rect.offsetMax=Vector2.zero;
            var fx = overlay.GetComponent<SelectionEffectGraphic>();
            fx.color = new Color(1,.69f,.15f,1);
            fx.SetState(i==0 ? SelectionEffectGraphic.SelectionState.Available : i==2 ? SelectionEffectGraphic.SelectionState.Unavailable : SelectionEffectGraphic.SelectionState.Selected);
        }
        Selection.activeGameObject = root;
    }
}
