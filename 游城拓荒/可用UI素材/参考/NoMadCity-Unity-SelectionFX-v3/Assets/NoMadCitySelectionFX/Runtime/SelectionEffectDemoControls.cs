using UnityEngine;

namespace NoMadCity.SelectionFX
{
    // Diagnostic demo only. Never add this to production UI: these buttons override visual state.
    public sealed class SelectionEffectDemoControls : MonoBehaviour
    {
        public SelectionEffectGraphic[] effects;
        private bool stopped;
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(Screen.width - 24, 820), 180), GUI.skin.box);
            GUILayout.Label("NoMadCity selection FX v2 | Top = Available, Bottom = Selected");
            GUILayout.Label("Left to right: Enterprise / Department / Effect row / City / Card");
            GUILayout.Label("Selected: strong double gold edge + TWO moving ivory segments (2.4 sec/orbit).");
            if (effects != null && effects.Length > 0 && effects[0] != null)
                GUILayout.Label("Uploaded clock: " + effects[0].LastUploadedAnimationTime.ToString("F2") +
                    " | state: " + effects[0].State + " | material: " + effects[0].HasEffectMaterial);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(stopped ? "Resume motion" : "Freeze motion (compare)"))
            {
                stopped = !stopped;
                foreach (var fx in effects) if (fx != null) fx.SetReducedMotion(stopped);
            }
            if (GUILayout.Button("Replay selection"))
                foreach (var fx in effects) if (fx != null) fx.ReplaySelection();
            GUILayout.EndHorizontal();
            GUILayout.Label("Clock movement only verifies CPU upload. Confirm moving segments in Game view.");
            GUILayout.EndArea();
        }
    }
}
