namespace BlindsDown.Helper;

/// <summary>
/// Is this process running with administrator rights right now? Asked of the process token (TokenElevation), not
/// of group membership: with UAC an administrator's normal programs run unelevated, and under Windows 11's
/// Administrator protection the elevated process even runs as a different, hidden account.
/// </summary>
internal static class ProcessElevation
{
    public static unsafe bool IsElevated()
    {
        nint token;
        if (HelperNative.OpenProcessToken(HelperNative.GetCurrentProcess(), HelperNative.TokenQuery, &token) == 0)
        {
            return false;
        }

        try
        {
            // TOKEN_ELEVATION is a single DWORD: TokenIsElevated.
            uint elevated = 0;
            uint length;
            return HelperNative.GetTokenInformation(token, HelperNative.TokenElevationClass, &elevated, sizeof(uint), &length) != 0
                && elevated != 0;
        }
        finally
        {
            _ = HelperNative.CloseHandle(token);
        }
    }
}
