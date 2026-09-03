using System;

namespace RemoteFlattener.Models;

/// <summary>
/// A machine name as observed from configuration, DNS, RDP, or the network protocol.
/// Equality intentionally preserves the application's current behavior: names compare
/// by their uppercased first DNS label.
/// </summary>
public readonly struct MachineName : IEquatable<MachineName>
{
    private readonly string? _value;
    private readonly string? _canonical;

    private MachineName(string value)
    {
        _value = value;
        _canonical = Canonicalize(value);
    }

    /// <summary>The exact name supplied by the originating boundary.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The current canonical identity used for lookups and routing.</summary>
    public string Canonical => _canonical ?? string.Empty;

    /// <summary>
    /// The current display representation. This intentionally remains the observed
    /// value so introducing this type does not alter existing UI text.
    /// </summary>
    public string DisplayName => Value;

    public static MachineName From(string? value) => new(value ?? string.Empty);

    public bool Matches(string? other) => Equals(From(other));

    /// <summary>
    /// Compares the observed values without canonicalizing them. This preserves call
    /// sites whose current behavior intentionally or historically requires exact names.
    /// </summary>
    public bool HasSameObservedValue(string? other) =>
        string.Equals(Value, From(other).Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares this name's canonical form to the other value exactly. This captures
    /// existing asymmetric comparisons so they are explicit and easy to audit.
    /// </summary>
    public bool CanonicalEqualsObservedValue(string? other) =>
        string.Equals(Canonical, From(other).Value, StringComparison.OrdinalIgnoreCase);

    public bool Equals(MachineName other) =>
        string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MachineName other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical);

    public override string ToString() => Value;

    public static bool operator ==(MachineName left, MachineName right) => left.Equals(right);

    public static bool operator !=(MachineName left, MachineName right) => !left.Equals(right);

    private static string Canonicalize(string name)
    {
        if (name.Length == 0) return string.Empty;
        var dot = name.IndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToUpperInvariant();
    }
}
