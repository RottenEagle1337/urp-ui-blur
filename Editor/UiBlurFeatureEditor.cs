using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace RottenEagle.Editor
{
    [CustomEditor(typeof(UiBlurFeature))]
    internal sealed class UiBlurFeatureEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var feature = (UiBlurFeature)target;
            UniversalRendererData rendererData = FindOwnerRenderer(feature);

            if (rendererData != null && (rendererData.transparentLayerMask & feature.UiLayerMask) != 0)
            {
                EditorGUILayout.HelpBox(
                    "UI layers are still in the renderer Transparent Layer Mask. UI will be drawn twice: " +
                    "once before post processing and once by this feature.",
                    MessageType.Warning);

                if (GUILayout.Button("Remove UI layers from Transparent Layer Mask"))
                {
                    Undo.RecordObject(rendererData, "Remove UI layers from Transparent Layer Mask");
                    rendererData.transparentLayerMask &= ~feature.UiLayerMask;
                    EditorUtility.SetDirty(rendererData);
                }
            }

            if (feature.BlurSortingLayers.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No blur sorting layers: blur panels blur only the scene. Put the UI that should blur the UI " +
                    $"below it on a sorting layer from the list (for example '{UiBlurFeature.DefaultBlurSortingLayer}').",
                    MessageType.Info);

                if (GUILayout.Button($"Use '{UiBlurFeature.DefaultBlurSortingLayer}' sorting layer"))
                {
                    int layerId = UiBlurEditorUtility.EnsureSortingLayer(UiBlurFeature.DefaultBlurSortingLayer);
                    serializedObject.Update();
                    SerializedProperty layers = serializedObject.FindProperty("blurSortingLayers");
                    layers.InsertArrayElementAtIndex(layers.arraySize);
                    layers.GetArrayElementAtIndex(layers.arraySize - 1).intValue = layerId;
                    serializedObject.ApplyModifiedProperties();
                }
            }

            DrawDefaultInspector();
        }

        private static UniversalRendererData FindOwnerRenderer(Object feature)
        {
            string assetPath = AssetDatabase.GetAssetPath(feature);
            return string.IsNullOrEmpty(assetPath)
                ? null
                : AssetDatabase.LoadMainAssetAtPath(assetPath) as UniversalRendererData;
        }
    }
}
