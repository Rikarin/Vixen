// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation;

/// <summary>A polygon in a <see cref="NavMesh" />, named in a way that survives the tile going away.</summary>
/// <remarks>
///     <para>
///         Three fields packed into one 64-bit value: the salt of the tile slot, the slot itself, and
///         the polygon's index inside it. A path is a list of these, an agent holds one for the
///         polygon it is standing on, and both may outlive the tile they name — a streamed level
///         unloads tiles while agents are mid-path, and a rebaked tile reuses the slot.
///     </para>
///     <para>
///         The salt is what makes that safe. It increments every time a slot is reused, so a
///         reference into the previous occupant does not resolve: <see cref="NavMesh.TryGetPoly" />
///         returns <see langword="false" /> rather than silently answering about a different polygon
///         that happens to have the same index. A bare tile-and-index pair would have no way to tell
///         those two cases apart, and the failure mode is an agent walking a path through geometry
///         that no longer exists.
///     </para>
///     <para>
///         <see cref="Null" /> is the zero value, which is what a <c>default</c> struct and a cleared
///         array already hold. Salts therefore start at one.
///     </para>
/// </remarks>
public readonly struct NavPolyRef : IEquatable<NavPolyRef> {
    const int PolyBits = 20;
    const int TileBits = 20;
    const int SaltBits = 16;

    const ulong PolyMask = (1UL << PolyBits) - 1;
    const ulong TileMask = (1UL << TileBits) - 1;
    const ulong SaltMask = (1UL << SaltBits) - 1;

    readonly ulong value;

    NavPolyRef(ulong value) => this.value = value;

    /// <summary>The reference that names no polygon.</summary>
    public static NavPolyRef Null => default;

    /// <summary>Whether this names no polygon.</summary>
    public bool IsNull => value == 0;

    /// <summary>The tile slot.</summary>
    public int Tile => (int)((value >> PolyBits) & TileMask);

    /// <summary>The polygon's index within its tile.</summary>
    public int Poly => (int)(value & PolyMask);

    /// <summary>The salt the tile slot had when this reference was made.</summary>
    public uint Salt => (uint)((value >> (PolyBits + TileBits)) & SaltMask);

    /// <summary>The largest tile slot a reference can name, plus one.</summary>
    public static int MaxTiles => 1 << TileBits;

    /// <summary>The largest polygon count a tile can hold.</summary>
    public static int MaxPolysPerTile => 1 << PolyBits;

    /// <summary>Packs a reference.</summary>
    /// <param name="salt">The tile slot's salt. Wrapped into the salt field.</param>
    /// <param name="tile">The tile slot.</param>
    /// <param name="poly">The polygon index within the tile.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A field does not fit.</exception>
    public static NavPolyRef Encode(uint salt, int tile, int poly) {
        ArgumentOutOfRangeException.ThrowIfNegative(tile);
        ArgumentOutOfRangeException.ThrowIfNegative(poly);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tile, MaxTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(poly, MaxPolysPerTile);

        return new((((ulong)salt & SaltMask) << (PolyBits + TileBits)) | (((ulong)tile & TileMask) << PolyBits) | ((ulong)poly & PolyMask));
    }

    /// <summary>The packed value, for storing a reference somewhere that only holds numbers.</summary>
    /// <returns>The bits.</returns>
    public ulong ToUInt64() => value;

    /// <summary>Unpacks a value produced by <see cref="ToUInt64" />.</summary>
    /// <param name="value">The bits.</param>
    /// <returns>The reference.</returns>
    public static NavPolyRef FromUInt64(ulong value) => new(value);

    /// <inheritdoc />
    public bool Equals(NavPolyRef other) => value == other.value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NavPolyRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => value.GetHashCode();

    /// <summary>Whether two references name the same polygon of the same tile occupant.</summary>
    /// <param name="left">One reference.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true" /> if they are equal.</returns>
    public static bool operator ==(NavPolyRef left, NavPolyRef right) => left.value == right.value;

    /// <summary>Whether two references differ.</summary>
    /// <param name="left">One reference.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true" /> if they differ.</returns>
    public static bool operator !=(NavPolyRef left, NavPolyRef right) => left.value != right.value;

    /// <inheritdoc />
    public override string ToString() => IsNull ? "NavPolyRef.Null" : $"tile {Tile} poly {Poly} (salt {Salt})";
}
