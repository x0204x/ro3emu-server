namespace Ro3Emu.Server.Data.Config;

/// <summary>How a config field is read, chosen by the type of its view property.</summary>
public enum ConfigKind
{
    /// <summary><c>long</c>: an integer.</summary>
    Integral,
    /// <summary><c>int</c>: an integer within the range of an <c>int</c>.</summary>
    Integral32,
    /// <summary><c>double</c>: a number, also one the Lua table stored as a string ("2000").</summary>
    Number,
    /// <summary><c>bool</c>: true/false, or a number other than 0.</summary>
    Bool,
    /// <summary><c>string</c>: a text value.</summary>
    Text,
    /// <summary><c>long[]</c>: a list of integers.</summary>
    Ints,
    /// <summary><c>double[]</c>: a list of numbers.</summary>
    Numbers,
    /// <summary><c>long[][]</c>: a list of integer lists ([[id, chance, ms], ...]).</summary>
    IntRows,
    /// <summary><c>double[][]</c>: a list of number lists.</summary>
    NumberRows,
    /// <summary><c>string[]</c>: a list of texts.</summary>
    Texts,
    /// <summary><c>(long Id, double Value)[]</c>: [[id, value, ...], ...] read as (id, first value) pairs.</summary>
    Pairs,
    /// <summary><c>JsonElement?</c>: the value as exported, for shapes no other kind describes.</summary>
    Raw,
}

/// <summary>A declared field of a config table view.</summary>
public sealed record ConfigFieldInfo(string Name, ConfigKind Kind, bool Optional);

/// <summary>A declared config table view and its fields (generated, listed in <c>ConfigTables.All</c>).</summary>
public sealed record ConfigTableInfo(string Name, bool Optional, bool Grouped, IReadOnlyList<ConfigFieldInfo> Fields);
