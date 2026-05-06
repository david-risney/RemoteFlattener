namespace RemoteFlattener.Network;

/// <summary>
/// String constants for all network message types.
/// Use these instead of inline string literals to get compile-time safety against typos.
/// </summary>
public static class MessageTypes
{
    // Handshake
    public const string Hello           = "HELLO";
    public const string HelloAck        = "HELLO_ACK";

    // State sharing
    public const string StateUpdate     = "STATE_UPDATE";

    // Desktop switching
    public const string SwitchLeft      = "SWITCH_DESKTOP_LEFT";
    public const string SwitchRight     = "SWITCH_DESKTOP_RIGHT";
    public const string SwitchToDesktop = "SWITCH_TO_DESKTOP_INDEX";

    // UI actions
    public const string TaskView        = "TASK_VIEW";
}
