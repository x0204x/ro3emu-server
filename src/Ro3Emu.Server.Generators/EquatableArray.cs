using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Ro3Emu.Server.Generators;

/// <summary>An array compared by its items, so the incremental pipeline can tell an unchanged model.</summary>
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly T[] _items = items;

    public int Count => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) => (_items ?? []).SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (var item in _items ?? []) hash = unchecked(hash * 31 + (item?.GetHashCode() ?? 0));
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
