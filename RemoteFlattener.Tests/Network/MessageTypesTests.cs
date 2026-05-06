using System.Collections.Generic;
using System.Linq;
using RemoteFlattener.Network;
using Xunit;

namespace RemoteFlattener.Tests.Network;

public class MessageTypesTests
{
    private static IEnumerable<string> AllTypes() =>
    [
        MessageTypes.Hello,
        MessageTypes.HelloAck,
        MessageTypes.StateUpdate,
        MessageTypes.SwitchLeft,
        MessageTypes.SwitchRight,
        MessageTypes.SwitchToDesktop,
        MessageTypes.TaskView,
    ];

    [Fact]
    public void AllMessageTypes_AreNonEmpty()
    {
        foreach (var t in AllTypes())
            Assert.False(string.IsNullOrWhiteSpace(t), $"A MessageType constant is null or whitespace.");
    }

    [Fact]
    public void AllMessageTypes_AreDistinct()
    {
        var types    = AllTypes().ToList();
        var distinct = types.Distinct().ToList();
        Assert.Equal(types.Count, distinct.Count);
    }
}
