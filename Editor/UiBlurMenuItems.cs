using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RottenEagle.Editor
{
    internal static class UiBlurMenuItems
    {
        private const int UiLayer = 5;

        /// <summary>
        /// Child group drawn above the UI blur boundary: panels inside blur the scene and the UI below.
        /// </summary>
        [MenuItem("GameObject/UI/Blur Group", false, 2100)]
        private static void CreateBlurGroup(MenuCommand command)
        {
            int sortingLayerId = UiBlurEditorUtility.EnsureSortingLayer(UiBlurFeature.DefaultBlurSortingLayer);

            GameObject group = CreateUiObject("Blur Group", command.context as GameObject,
                typeof(Canvas), typeof(GraphicRaycaster), typeof(CanvasGroup));

            var rectTransform = (RectTransform)group.transform;
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var canvas = group.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingLayerID = sortingLayerId;

            Selection.activeGameObject = group;
        }

        [MenuItem("GameObject/UI/Blur Panel", false, 2101)]
        private static void CreateBlurPanel(MenuCommand command)
        {
            GameObject panel = CreateUiObject("Blur Panel", command.context as GameObject, typeof(Image));
            ((RectTransform)panel.transform).sizeDelta = new Vector2(400.0f, 300.0f);

            var image = panel.GetComponent<Image>();
            image.material = UiBlurEditorUtility.LoadPanelMaterial();
            image.raycastTarget = true;

            Selection.activeGameObject = panel;
        }

        private static GameObject CreateUiObject(string name, GameObject parent, params System.Type[] components)
        {
            var gameObject = new GameObject(name, typeof(RectTransform)) { layer = UiLayer };
            foreach (System.Type component in components)
            {
                gameObject.AddComponent(component);
            }

            if (parent != null)
            {
                GameObjectUtility.SetParentAndAlign(gameObject, parent);
            }

            Undo.RegisterCreatedObjectUndo(gameObject, "Create " + name);
            return gameObject;
        }
    }
}
