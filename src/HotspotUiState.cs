namespace HenHotspot;

/// <summary>One source of truth for network-dependent controls, including unknown and transition states.</summary>
public record HotspotUiState(string Label, string Color, bool CanToggle, bool CanEditNetwork, bool CanUseClients, bool CanApplyRules)
{
    public static HotspotUiState From(string? state, string? capability, bool hasError, bool busy)
    {
        if (hasError || state is null or "Unknown")
            return new(L10n.T("Unconfirmed"), "#E8B85E", false, false, false, false);
        if (state == "InTransition")
            return new(L10n.T("Changing"), "#E8B85E", false, false, false, false);
        if (state == "On")
            return new(busy ? L10n.T("OnWorking") : L10n.T("On"), "#278B63", !busy, false, !busy, !busy);
        if (state == "Off")
            return new(busy ? L10n.T("OffWorking") : L10n.T("Off"), "#A4B3C4", !busy && capability == "Enabled", !busy && capability == "Enabled", false, false);
        return new(L10n.T("Unconfirmed"), "#E8B85E", false, false, false, false);
    }
}
