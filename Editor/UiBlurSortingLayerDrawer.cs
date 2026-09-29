using UnityEditor;
using UnityEngine;

namespace RottenEagle.Editor
{
    [CustomPropertyDrawer(typeof(UiBlurSortingLayerAttribute))]
    internal sealed class UiBlurSortingLayerDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.LabelField(position, label.text, "Use [UiBlurSortingLayer] with int.");
                return;
            }

            SortingLayer[] layers = SortingLayer.layers;
            var names = new GUIContent[layers.Length];
            int selectedIndex = -1;

            for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                names[layerIndex] = new GUIContent(layers[layerIndex].name);
                if (layers[layerIndex].id == property.intValue)
                {
                    selectedIndex = layerIndex;
                }
            }

            using (new EditorGUI.PropertyScope(position, label, property))
            {
                if (selectedIndex < 0)
                {
                    label = new GUIContent(label.text + " (missing)", label.tooltip);
                }

                EditorGUI.BeginChangeCheck();
                int newIndex = EditorGUI.Popup(position, label, selectedIndex, names);
                if (EditorGUI.EndChangeCheck() && newIndex >= 0)
                {
                    property.intValue = layers[newIndex].id;
                }
            }
        }
    }
}
