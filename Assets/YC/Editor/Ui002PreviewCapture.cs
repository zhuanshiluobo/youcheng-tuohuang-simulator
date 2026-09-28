using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    // A dedicated verification scene. No existing gameplay scene or prefab is saved here.
    public static class Ui002PreviewCapture
    {
        private const string Root = "Assets/YC/Presentation/Ui002";
        private const string ScenePath = Root + "/Verification/Ui002Preview.unity";
        private const string Evidence = "prompt/UI换新/执行记录/证据";

        public static void Capture()
        {
            Directory.CreateDirectory(Root + "/Verification");
            var created = !File.Exists(ScenePath);
            var scene = created
                ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single)
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (created) CreatePreview();
            PreparePreview();
            if (created && !EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save UI-002 verification scene.");

            var camera = GameObject.Find("UI002 Preview Camera").GetComponent<Camera>();
            var canvas = GameObject.Find("UI002 Preview Canvas").GetComponent<Canvas>();
            var frame = GameObject.Find("Preview SharedFrame").GetComponent<RectTransform>();
            var card = GameObject.Find("Preview CardSlot").GetComponent<UiCardSlotView>();
            var cardTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/YC/Presentation/Resources/CardImages/Facilities/trade_district.jpg");
            Sprite sampleCard = null;
            if (cardTexture != null)
            {
                sampleCard = Sprite.Create(cardTexture,
                    new Rect(0, 0, cardTexture.width, cardTexture.height), new Vector2(0.5f, 0.5f));
                card.SetArtwork(sampleCard);
            }
            foreach (var size in new[] { new Vector2Int(900, 600), new Vector2Int(1280, 720), new Vector2Int(1920, 1080) })
            {
                ((RectTransform)canvas.transform).sizeDelta = size;
                camera.aspect = (float)size.x / size.y;
                camera.orthographicSize = size.y / 200f;
                frame.sizeDelta = new Vector2(Mathf.Min(size.x * 0.76f, 1100f), size.y * 0.76f);
                var mixed = GameObject.Find("Preview MixedLine").GetComponent<RectTransform>();
                mixed.sizeDelta = new Vector2(size.x <= 900 ? 390f : frame.sizeDelta.x - 72f, 72f);
                mixed.anchoredPosition = new Vector2(0, frame.sizeDelta.y * 0.5f - 72f);
                Canvas.ForceUpdateCanvases();
                GameObject.Find("Preview ScrollView").GetComponent<ScrollRect>().verticalNormalizedPosition = 1f;
                Canvas.ForceUpdateCanvases();
                Render(camera, size);
            }
            card.SetArtwork(null);
            if (sampleCard != null) UnityEngine.Object.DestroyImmediate(sampleCard);
            CaptureStates(camera, canvas, frame);
            Debug.Log("UI002 PREVIEW captured 900x600, 1280x720, 1920x1080 and state grid from " + ScenePath);
        }

        private static void CaptureStates(Camera camera, Canvas canvas, RectTransform frame)
        {
            frame.gameObject.SetActive(false);
            var size = new Vector2Int(1280, 360);
            ((RectTransform)canvas.transform).sizeDelta = size;
            camera.aspect = (float)size.x / size.y;
            camera.orthographicSize = size.y / 200f;
            var names = new[] { "正常", "悬停", "聚焦", "按下", "选中", "禁用", "处理中" };
            var roles = AssetDatabase.LoadAssetAtPath<UiFontRoles>(Root + "/Content/UiFontRoles.asset");
            for (var i = 0; i < names.Length; i++)
            {
                var x = -495f + i * 165f;
                var button = Add("SharedButton", "State " + names[i], canvas.transform,
                    new Vector2(150, 55), new Vector2(x, -10));
                var visual = button.GetComponent<UiSharedButtonState>();
                switch (i)
                {
                    case 1: visual.OnPointerEnter(null); break;
                    case 2: visual.OnSelect(null); break;
                    case 3: visual.OnPointerDown(null); break;
                    case 4: visual.SetSelected(true); break;
                    case 5: button.GetComponent<Button>().interactable = false; visual.Refresh(); break;
                    case 6: visual.SetPending(true); break;
                }
                var caption = new GameObject("Caption " + names[i], typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(Text));
                caption.transform.SetParent(canvas.transform, false);
                var rect = (RectTransform)caption.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(155, 40);
                rect.anchoredPosition = new Vector2(x, 55);
                var text = caption.GetComponent<Text>();
                text.font = roles.Regular;
                text.fontStyle = FontStyle.Normal;
                text.fontSize = 22;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(0.96f, 0.9f, 0.78f);
                text.text = names[i];
                text.raycastTarget = false;
            }
            Canvas.ForceUpdateCanvases();
            Render(camera, size, "UI002-状态-1280x360.png");
        }

        private static void PreparePreview()
        {
            var scroll = GameObject.Find("Preview ScrollView").GetComponent<ScrollRect>();
            var content = scroll.content;
            if (content.childCount != 0) return;
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = new Vector2(0, 320);
            var names = new[] { "正常", "已选中", "不可用", "处理中", "长中文标点，换行检查" };
            for (var i = 0; i < names.Length; i++)
            {
                var row = Add("StatusRow", "Preview Row " + i, content, new Vector2(270, 64), new Vector2(0, -i * 64));
                var rect = row.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0, 1);
                rect.anchorMax = new Vector2(1, 1);
                rect.pivot = new Vector2(0.5f, 1);
                rect.sizeDelta = new Vector2(0, 64);
                row.transform.Find("Label").GetComponent<UiSemanticText>().SetValue(names[i]);
                row.GetComponent<UiSelectionState>().SetState((UiSelectionVisualState)Mathf.Min(i, 3));
            }
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
        }

        private static void CreatePreview()
        {
            var cameraGo = new GameObject("UI002 Preview Camera", typeof(Camera));
            var camera = cameraGo.GetComponent<Camera>();
            camera.orthographic = true;
            camera.transform.position = new Vector3(0, 0, -10);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.11f, 0.13f);
            var canvasGo = new GameObject("UI002 Preview Canvas", typeof(RectTransform), typeof(Canvas));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1280, 720);
            canvas.transform.localScale = Vector3.one * 0.01f;

            var frame = Add("SharedFrame", "Preview SharedFrame", canvas.transform,
                new Vector2(970, 547), Vector2.zero);
            var content = frame.transform.Find("Content");
            Add("MixedLine", "Preview MixedLine", content,
                new Vector2(900, 72), new Vector2(0, 200));
            Add("CardSlot", "Preview CardSlot", content,
                new Vector2(150, 214), new Vector2(-235, -20));
            var status = Add("StatusRow", "Preview StatusRow", content,
                new Vector2(300, 64), new Vector2(120, 88));
            status.GetComponent<UiSelectionState>().SetState(UiSelectionVisualState.Selected);
            Add("ScrollView", "Preview ScrollView", content,
                new Vector2(310, 155), new Vector2(120, -45));
            Add("SharedButton", "Preview SharedButton", content,
                new Vector2(180, 55), new Vector2(120, -180));
        }

        private static GameObject Add(string prefabName, string name, Transform parent, Vector2 size, Vector2 position)
        {
            var path = Root + "/Prefabs/" + prefabName + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("Missing preview prefab: " + path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = name;
            instance.transform.SetParent(parent, false);
            var rect = instance.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return instance;
        }

        private static void Render(Camera camera, Vector2Int size, string name = null)
        {
            var target = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32);
            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            var texture = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
                texture.Apply();
                Directory.CreateDirectory(Evidence);
                File.WriteAllBytes(Evidence + "/" + (name ?? "UI002-" + size.x + "x" + size.y + ".png"),
                    texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
