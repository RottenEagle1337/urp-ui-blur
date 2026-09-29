using System.Collections.Generic;
using UnityEngine;

namespace RottenEagle
{
    /// <summary>
    /// Runtime control of blur layers. Values live in memory only, the renderer feature asset is never modified.
    /// </summary>
    public static class UiBlur
    {
        private static readonly Dictionary<int, float> layerStrengths = new Dictionary<int, float>(8);

        /// <summary>
        /// Sets blur strength of the blur layer that starts at the given sorting layer.
        /// 0 keeps only the soft half resolution copy, 1 is the full blur configured on the feature.
        /// </summary>
        public static void SetLayerStrength(int sortingLayerId, float strength)
        {
            layerStrengths[sortingLayerId] = Mathf.Clamp01(strength);
        }

        public static void SetLayerStrength(string sortingLayerName, float strength)
        {
            SetLayerStrength(SortingLayer.NameToID(sortingLayerName), strength);
        }

        public static float GetLayerStrength(int sortingLayerId)
        {
            return layerStrengths.TryGetValue(sortingLayerId, out float strength) ? strength : 1.0f;
        }

        public static float GetLayerStrength(string sortingLayerName)
        {
            return GetLayerStrength(SortingLayer.NameToID(sortingLayerName));
        }

        public static void ResetLayerStrengths()
        {
            layerStrengths.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            layerStrengths.Clear();
        }
    }
}
