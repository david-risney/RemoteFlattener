using System.Runtime.InteropServices;

namespace RemoteFlattener.RDP;

public static class RdpRoleDetector
{
    private const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Returns true when the process is running inside a Remote Desktop session (i.e. acting as an RDP server).</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SM_REMOTESESSION) != 0;
}
