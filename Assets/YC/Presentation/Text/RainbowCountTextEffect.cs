using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>仅对指定的彩色设施数量字形着色，不改变文字、字体或排版。</summary>
    [RequireComponent(typeof(Text))]
    public sealed class RainbowCountTextEffect : BaseMeshEffect
    {
        private int firstCharacter = -1, characterCount;
        private Color32 bottomLeft, topLeft, topRight, bottomRight;

        public void SetCountSpan(int first, int count, Color red, Color yellow, Color blue, Color violet)
        {
            firstCharacter = first;
            characterCount = count;
            bottomLeft = blue; topLeft = red; topRight = yellow; bottomRight = violet;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vertices)
        {
            if (!IsActive() || firstCharacter < 0 || characterCount <= 0 || vertices == null) return;
            Color32[] colors = { bottomLeft, topLeft, topRight, bottomRight };
            for (int index = 0; index < characterCount; index++)
            {
                int firstVertex = (firstCharacter + index) * 4;
                if (firstVertex < 0 || firstVertex + 3 >= vertices.currentVertCount) break;
                for (int corner = 0; corner < colors.Length; corner++)
                {
                    var vertex = new UIVertex();
                    vertices.PopulateUIVertex(ref vertex, firstVertex + corner);
                    var color = colors[corner]; color.a = vertex.color.a; vertex.color = color;
                    vertices.SetUIVertex(vertex, firstVertex + corner);
                }
            }
        }
    }
}
