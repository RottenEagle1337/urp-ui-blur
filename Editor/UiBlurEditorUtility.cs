using UnityEditor;
using UnityEngine;

namespace RottenEagle.Editor
{
    internal static class UiBlurEditorUtility
    {
        public const string PanelMaterialPath = "Packages/com.rotteneagle.urp-ui-blur/Runtime/Materials/UiBlurPanel.mat";

        /// <summary>Returns the id of the sorting layer, appending it to the project sorting layers when missing.</summary>
        public static int EnsureSortingLayer(string layerName)
        {
            int existingId = SortingLayer.NameToID(layerName);
            if (existingId != 0 && SortingLayer.IsValid(existingId))
            {
                return existingId;
            }

            Object tagManagerAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            var tagManager = new SerializedObject(tagManagerAsset);
            SerializedProperty layers = tagManager.FindProperty("m_SortingLayers");

            layers.InsertArrayElementAtIndex(layers.arraySize);
            SerializedProperty layer = layers.GetArrayElementAtIndex(layers.arraySize - 1);
            layer.FindPropertyRelative("name").stringValue = layerName;
            layer.FindPropertyRelative("uniqueID").uintValue = StableId(layerName);
            layer.FindPropertyRelative("locked").intValue = 0;
            tagManager.ApplyModifiedProperties();

            return SortingLayer.NameToID(layerName);
        }

        public static Material LoadPanelMaterial()
        {
            return AssetDatabase.LoadAssetAtPath<Material>(PanelMaterialPath);
        }

        private static uint StableId(string value)
        {
            // FNV-1a, kept positive and non zero.
            uint hash = 2166136261;
            foreach (char character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }

            return (hash & 0x7fffffff) | 1;
        }
    }
}
