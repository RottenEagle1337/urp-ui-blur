using UnityEngine;
using UnityEngine.UI;

namespace RottenEagle
{
    /// <summary>
    /// Marks a UI graphic that uses the blur panel material. The graphic is rendered by UGUI as usual,
    /// this component only reports its screen rect and sorting layer so the renderer feature can
    /// skip blur layers without visible panels and limit blur work to the panel area.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Graphic))]
    [ExecuteAlways]
    public class UiBlurPanel : MonoBehaviour
    {
        private const float MinVisibleAlpha = 0.001f;

        private readonly Vector3[] worldCorners = new Vector3[4];

        private Graphic graphic;
        private RectTransform rectTransform;
        private Canvas sortingCanvas;

        public Graphic Graphic => graphic;

        /// <summary>Canvas that defines the sorting layer of this panel (closest override sorting or root canvas).</summary>
        public Canvas SortingCanvas => sortingCanvas;

        public int SortingLayerId => sortingCanvas != null ? sortingCanvas.sortingLayerID : 0;

        internal int RegistryIndex { get; set; } = -1;

        /// <summary>Camera of the root canvas when it is a Screen Space Camera canvas, otherwise null.</summary>
        public Camera CanvasCamera
        {
            get
            {
                if (sortingCanvas == null)
                {
                    return null;
                }

                Canvas rootCanvas = sortingCanvas.rootCanvas;
                return rootCanvas.renderMode == RenderMode.ScreenSpaceCamera ? rootCanvas.worldCamera : null;
            }
        }

        public bool IsVisible
        {
            get
            {
                if (graphic == null || !graphic.enabled || sortingCanvas == null || !sortingCanvas.enabled)
                {
                    return false;
                }

                CanvasRenderer canvasRenderer = graphic.canvasRenderer;
                if (canvasRenderer.cull)
                {
                    return false;
                }

                return graphic.color.a * canvasRenderer.GetInheritedAlpha() > MinVisibleAlpha;
            }
        }

        private void OnEnable()
        {
            graphic = GetComponent<Graphic>();
            rectTransform = transform as RectTransform;
            RefreshSortingCanvas();
            UiBlurPanelRegistry.Register(this);
        }

        private void OnDisable()
        {
            UiBlurPanelRegistry.Unregister(this);
        }

        private void OnTransformParentChanged()
        {
            RefreshSortingCanvas();
        }

        private void OnCanvasHierarchyChanged()
        {
            RefreshSortingCanvas();
        }

        /// <summary>
        /// Axis aligned screen rect of the panel in pixels of the given camera (Camera.pixelRect space).
        /// </summary>
        public bool TryGetScreenRect(Camera camera, out Rect screenRect)
        {
            screenRect = default;

            if (rectTransform == null || camera == null)
            {
                return false;
            }

            rectTransform.GetWorldCorners(worldCorners);

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            for (int cornerIndex = 0; cornerIndex < worldCorners.Length; cornerIndex++)
            {
                Vector3 screenPoint = camera.WorldToScreenPoint(worldCorners[cornerIndex]);
                if (screenPoint.z < 0.0f)
                {
                    return false;
                }

                min = Vector2.Min(min, screenPoint);
                max = Vector2.Max(max, screenPoint);
            }

            if (max.x - min.x <= 0.0f || max.y - min.y <= 0.0f)
            {
                return false;
            }

            screenRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return true;
        }

        private void RefreshSortingCanvas()
        {
            sortingCanvas = null;

            if (graphic == null)
            {
                return;
            }

            Canvas canvas = graphic.canvas;
            while (canvas != null && !canvas.isRootCanvas && !canvas.overrideSorting)
            {
                Transform parent = canvas.transform.parent;
                canvas = parent != null ? parent.GetComponentInParent<Canvas>() : null;
            }

            sortingCanvas = canvas;
        }
    }
}
