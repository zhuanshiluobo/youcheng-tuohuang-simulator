using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class TabletopBoundsContributor : MonoBehaviour
    {
        [SerializeField] private RectTransform[] rectTransforms = new RectTransform[0];

        private readonly Vector3[] worldCorners = new Vector3[4];

        public void Configure(params RectTransform[] contributors)
        {
            rectTransforms = contributors ?? new RectTransform[0];
        }

        public void AppendWorldCorners(List<Vector3> destination)
        {
            if (destination == null || rectTransforms == null)
            {
                return;
            }

            for (var i = 0; i < rectTransforms.Length; i++)
            {
                var rectTransform = rectTransforms[i];
                if (rectTransform == null)
                {
                    continue;
                }

                rectTransform.GetWorldCorners(worldCorners);
                destination.Add(worldCorners[0]);
                destination.Add(worldCorners[1]);
                destination.Add(worldCorners[2]);
                destination.Add(worldCorners[3]);
            }
        }
    }
}
