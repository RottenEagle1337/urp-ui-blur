using System.Collections.Generic;

namespace RottenEagle
{
public static class UiBlurPanelRegistry
{
    private static readonly List<UiBlurPanel> activePanels = new List<UiBlurPanel>(8);

    public static IReadOnlyList<UiBlurPanel> ActivePanels => activePanels;

    public static void Register(UiBlurPanel panel)
    {
        if (panel == null || activePanels.Contains(panel))
        {
            return;
        }

        activePanels.Add(panel);
    }

    public static void Unregister(UiBlurPanel panel)
    {
        if (panel == null)
        {
            return;
        }

        activePanels.Remove(panel);
    }
}
}
