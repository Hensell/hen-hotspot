using HenHotspot;

public static class RefreshPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        check(RefreshPolicy.Interval(false, false, false, false).TotalSeconds == 10,
            "An idle visible window reduces background polling");
        check(RefreshPolicy.Interval(true, false, false, false).TotalSeconds == 30,
            "A minimized idle window reduces background polling further");
        check(RefreshPolicy.Interval(false, false, true, false).TotalSeconds == 2 &&
            RefreshPolicy.Interval(true, false, true, false).TotalSeconds == 10,
            "An unfiltered active hotspot refreshes promptly and can idle when minimized");
        foreach (bool minimized in new[] { false, true })
            foreach (bool hotspotOn in new[] { false, true })
            {
                check(RefreshPolicy.Interval(minimized, true, hotspotOn, false).TotalSeconds == 2,
                    "Active or shutting-down filters retain two-second checks regardless of visibility");
                check(RefreshPolicy.Interval(minimized, false, hotspotOn, true).TotalSeconds == 2,
                    "Uncertain network states never use idle polling intervals");
            }
    }
}
