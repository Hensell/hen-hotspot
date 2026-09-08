namespace HenHotspot;

public static class RefreshPolicy
{
    public static TimeSpan Interval(bool minimized, bool filtering, bool hotspotOn, bool uncertain)
    {
        // Safety checks must never slow down because the window is hidden.
        if (filtering || uncertain) return TimeSpan.FromSeconds(2);
        if (minimized) return TimeSpan.FromSeconds(hotspotOn ? 10 : 30);
        return TimeSpan.FromSeconds(hotspotOn ? 2 : 10);
    }
}
