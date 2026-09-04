using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RemoteFlattener.Models;

/// <summary>
/// Maps canonical machine identities to their 1-based virtual desktop indexes.
/// String conversion is confined to external boundaries such as JSON and Windows APIs.
/// </summary>
public sealed class MachineDesktopMap : IReadOnlyCollection<KeyValuePair<MachineName, int>>
{
    private readonly Dictionary<MachineName, int> _entries;

    public MachineDesktopMap()
    {
        _entries = new Dictionary<MachineName, int>();
    }

    public MachineDesktopMap(MachineDesktopMap source)
    {
        _entries = new Dictionary<MachineName, int>(source._entries);
    }

    public int Count => _entries.Count;

    public IEnumerable<MachineName> Keys => _entries.Keys;

    public int this[MachineName machine]
    {
        get => _entries[CanonicalKey(machine)];
        set => _entries[CanonicalKey(machine)] = value;
    }

    public int this[string machine]
    {
        get => this[MachineName.From(machine)];
        set => this[MachineName.From(machine)] = value;
    }

    public void Add(MachineName machine, int desktopIndex) =>
        _entries.Add(CanonicalKey(machine), desktopIndex);

    public void Add(string machine, int desktopIndex) =>
        Add(MachineName.From(machine), desktopIndex);

    public bool ContainsKey(MachineName machine) =>
        _entries.ContainsKey(CanonicalKey(machine));

    public bool ContainsKey(string machine) => ContainsKey(MachineName.From(machine));

    public bool TryGetValue(MachineName machine, out int desktopIndex) =>
        _entries.TryGetValue(CanonicalKey(machine), out desktopIndex);

    public bool TryGetValue(string machine, out int desktopIndex) =>
        TryGetValue(MachineName.From(machine), out desktopIndex);

    public Dictionary<string, int> ToWire() =>
        _entries.ToDictionary(
            entry => entry.Key.Canonical,
            entry => entry.Value,
            StringComparer.OrdinalIgnoreCase);

    public static MachineDesktopMap FromWire(IEnumerable<KeyValuePair<string, int>>? entries)
    {
        var result = new MachineDesktopMap();
        if (entries == null) return result;

        foreach (var entry in entries)
            result.Add(entry.Key, entry.Value);

        return result;
    }

    public bool ContentEquals(MachineDesktopMap? other)
    {
        if (other == null) return false;
        if (Count != other.Count) return false;
        foreach (var entry in _entries)
            if (!other.TryGetValue(entry.Key, out var value) || value != entry.Value)
                return false;
        return true;
    }

    public IEnumerator<KeyValuePair<MachineName, int>> GetEnumerator() =>
        _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static MachineName CanonicalKey(MachineName machine) =>
        MachineName.From(machine.Canonical);
}
