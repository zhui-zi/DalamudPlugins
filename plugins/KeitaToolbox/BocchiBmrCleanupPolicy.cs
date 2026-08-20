namespace KeitaToolbox;

internal sealed class BocchiBmrCleanupPolicy
{
    private bool wasInCrescent;
    private bool? lastBmrEnabled;
    private bool bmrOwnedByBocchi;
    private bool cleanupPending;

    internal bool Update(
        bool featureEnabled,
        bool inCrescent,
        bool bmrEnabled,
        bool bocchiEnabled,
        bool bocchiControlsBmr,
        string? bocchiState)
    {
        if (!featureEnabled)
        {
            Reset();
            return false;
        }

        if (inCrescent)
        {
            if (!wasInCrescent)
            {
                wasInCrescent = true;
                lastBmrEnabled = bmrEnabled;
                bmrOwnedByBocchi = false;
                cleanupPending = false;
                return false;
            }

            if (lastBmrEnabled == false &&
                bmrEnabled &&
                bocchiEnabled &&
                bocchiControlsBmr &&
                IsBocchiAiState(bocchiState))
            {
                bmrOwnedByBocchi = true;
            }
            else if (!bmrEnabled)
            {
                bmrOwnedByBocchi = false;
            }

            lastBmrEnabled = bmrEnabled;
            return false;
        }

        if (wasInCrescent)
            cleanupPending = bmrOwnedByBocchi;

        wasInCrescent = false;
        lastBmrEnabled = bmrEnabled;
        if (!cleanupPending)
            return false;

        if (bmrEnabled)
            return true;

        cleanupPending = false;
        bmrOwnedByBocchi = false;
        return false;
    }

    private static bool IsBocchiAiState(string? state) => state is
        "Participating" or
        "InFate" or
        "InCriticalEncounter" or
        "WaitingForCriticalEncounter" or
        "WaitingToStartCriticalEncounter";

    private void Reset()
    {
        wasInCrescent = false;
        lastBmrEnabled = null;
        bmrOwnedByBocchi = false;
        cleanupPending = false;
    }
}
