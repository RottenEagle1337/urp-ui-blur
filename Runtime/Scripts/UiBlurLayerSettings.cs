using System;
using UnityEngine;

namespace RottenEagle
{
    /// <summary>
    /// Shows an int field as a sorting layer popup in the inspector.
    /// </summary>
    public class UiBlurSortingLayerAttribute : PropertyAttribute
    {
    }

    /// <summary>
    /// One blur layer. The layer starts at <see cref="sortingLayerId"/> and lasts until the next blur layer.
    /// UI of this range samples the blur of everything rendered before it (scene and lower UI layers).
    /// </summary>
    [Serializable]
    public class UiBlurLayerSettings
    {
        public const int MaxLevels = 6;

        [Tooltip("First sorting layer of this blur layer. Panels on this and higher sorting layers (up to the next blur layer) blur everything below.")]
        [UiBlurSortingLayer]
        public int sortingLayerId;

        [Tooltip("Dual Kawase downsample steps at the reference resolution. Each step doubles the blur radius and adds two passes.")]
        [Range(1, MaxLevels)]
        public int levels = 3;

        [Tooltip("Sample spread in texels of each step. Values above 2 start to show sampling patterns.")]
        [Range(0.0f, 3.0f)]
        public float offset = 1.0f;
    }
}
