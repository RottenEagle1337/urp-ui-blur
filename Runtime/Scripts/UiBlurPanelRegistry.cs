using System.Collections.Generic;

namespace RottenEagle
{
    public static class UiBlurPanelRegistry
    {
        private static readonly List<UiBlurPanel> activePanels = new List<UiBlurPanel>(16);

        public static IReadOnlyList<UiBlurPanel> ActivePanels => activePanels;

        public static void Register(UiBlurPanel panel)
        {
            if (panel == null || panel.RegistryIndex >= 0)
            {
                return;
            }

            panel.RegistryIndex = activePanels.Count;
            activePanels.Add(panel);
        }

        public static void Unregister(UiBlurPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            int index = panel.RegistryIndex;
            if (index < 0 || index >= activePanels.Count || activePanels[index] != panel)
            {
                panel.RegistryIndex = -1;
                return;
            }

            // Swap-remove keeps unregistering O(1) and allocation free.
            int lastIndex = activePanels.Count - 1;
            UiBlurPanel lastPanel = activePanels[lastIndex];
            activePanels[index] = lastPanel;
            lastPanel.RegistryIndex = index;
            activePanels.RemoveAt(lastIndex);
            panel.RegistryIndex = -1;
        }
    }
}
