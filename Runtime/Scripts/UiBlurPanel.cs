using UnityEngine;
using UnityEngine.UI;

namespace RottenEagle
{
[DisallowMultipleComponent]
[RequireComponent(typeof(Image))]
[RequireComponent(typeof(CanvasRenderer))]
[ExecuteAlways]
public class UiBlurPanel : MonoBehaviour
{
    [SerializeField] private bool hideFromCapture = true;

    private Image image;
    private CanvasRenderer canvasRenderer;
    private RectTransform rectTransform;
    private Canvas rootCanvas;

    public Image Image => image;

    public Color Color => image != null ? image.color : Color.white;

    public bool HideFromCapture => hideFromCapture;

    private void Awake()
    {
        image = GetComponent<Image>();
        canvasRenderer = GetComponent<CanvasRenderer>();
        rectTransform = transform as RectTransform;
        rootCanvas = GetComponentInParent<Canvas>();
    }

    private void OnEnable()
    {
        UiBlurPanelRegistry.Register(this);
    }

    private void OnDisable()
    {
        UiBlurPanelRegistry.Unregister(this);

        if (canvasRenderer != null)
        {
            canvasRenderer.cull = false;
        }
    }

    private void LateUpdate()
    {
        if (canvasRenderer == null)
        {
            return;
        }

        canvasRenderer.cull = hideFromCapture;
    }

    public bool TryGetScreenRect(Camera renderingCamera, out Rect pixelRect)
    {
        pixelRect = default;

        if (rectTransform == null || renderingCamera == null)
        {
            return false;
        }

        if (rootCanvas == null)
        {
            rootCanvas = GetComponentInParent<Canvas>();
        }

        if (rootCanvas == null)
        {
            return false;
        }

        Vector3[] corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);

        Vector2 min = RectTransformUtility.WorldToScreenPoint(renderingCamera, corners[0]);
        Vector2 max = min;

        for (int cornerIndex = 1; cornerIndex < corners.Length; cornerIndex++)
        {
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(renderingCamera, corners[cornerIndex]);
            min = Vector2.Min(min, screenPoint);
            max = Vector2.Max(max, screenPoint);
        }

        float width = max.x - min.x;
        float height = max.y - min.y;

        if (width <= 0.0f || height <= 0.0f)
        {
            return false;
        }

        pixelRect = new Rect(min.x, min.y, width, height);
        return true;
    }
}
}
