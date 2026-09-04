using System;
using System.Collections.Generic;
using RemoteFlattener.Models;
using Xunit;

namespace RemoteFlattener.Tests.Models;

public class MachineDesktopMapTests
{
    [Fact]
    public void FromWire_CanonicalizesKeys()
    {
        var map = MachineDesktopMap.FromWire(new Dictionary<string, int>
        {
            ["server.domain.test"] = 3,
        });

        Assert.True(map.TryGetValue(MachineName.From("SERVER"), out var desktop));
        Assert.Equal(3, desktop);
    }

    [Fact]
    public void ToWire_UsesCanonicalStringKeys()
    {
        var map = new MachineDesktopMap
        {
            { MachineName.From("server.domain.test"), 3 },
        };

        var wire = map.ToWire();

        Assert.Equal(3, wire["SERVER"]);
    }

    [Fact]
    public void FromWire_DuplicateCanonicalNamesThrows()
    {
        var entries = new[]
        {
            new KeyValuePair<string, int>("server.domain-one.test", 1),
            new KeyValuePair<string, int>("server.domain-two.test", 2),
        };

        Assert.Throws<ArgumentException>(() => MachineDesktopMap.FromWire(entries));
    }

    [Fact]
    public void ContentEquals_IgnoresObservedNameForm()
    {
        var first = new MachineDesktopMap
        {
            { "server.domain.test", 3 },
        };
        var second = new MachineDesktopMap
        {
            { "SERVER", 3 },
        };

        Assert.True(first.ContentEquals(second));
    }
}
