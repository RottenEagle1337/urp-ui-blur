using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RottenEagle.Editor
{
    internal static class UiBlurMenuItems
    {
        private const int UiLayer = 5;
        private const string PanelMaterialPath = "Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat";

        [MenuItem("GameObject/UI/Blur Panel", false, 2100)]
        private static void CreateBlurPanel(MenuCommand command)
        {
            GameObject panel = CreateUiObject("Blur Panel", command.context as GameObject, typeof(Image));
            ((RectTransform)panel.transform).sizeDelta = new Vector2(400.0f, 300.0f);

            var image = panel.GetComponent<Image>();
            image.material = AssetDatabase.LoadAssetAtPath<Material>(PanelMaterialPath);
            image.raycastTarget = true;

            Selection.activeGameObject = panel;
        }

        private static GameObject CreateUiObject(string name, GameObject parent, params System.Type[] components)
        {
            // The main menu passes no context: fall back to the selection, then to any canvas in the scene.
            if (parent == null)
            {
                parent = Selection.activeGameObject;
            }

            if (parent == null || parent.GetComponentInParent<Canvas>() == null)
            {
                Canvas sceneCanvas = FindSceneCanvas();
                parent = sceneCanvas != null ? sceneCanvas.gameObject : parent;
            }

            if (parent == null || parent.GetComponentInParent<Canvas>() == null)
            {
                Debug.LogWarning($"'{name}' was created without a parent canvas. Move it under a Screen Space Camera canvas.");
            }

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

        /// <summary>First root canvas in Screen Space Camera mode, otherwise any root canvas.</summary>
        private static Canvas FindSceneCanvas()
        {
            // Walks the active scene instead of FindObjectsByType, whose overloads differ between Unity 6 versions.
            Canvas fallback = null;
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (Canvas canvas in root.GetComponentsInChildren<Canvas>())
                {
                    if (!canvas.isRootCanvas)
                    {
                        continue;
                    }

                    if (canvas.renderMode == RenderMode.ScreenSpaceCamera)
                    {
                        return canvas;
                    }

                    fallback ??= canvas;
                }
            }

            return fallback;
        }
    }
}
