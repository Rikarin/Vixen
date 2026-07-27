// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Core.Serialization.Storage;

namespace Vixen.Assets;

/// <summary>Where a bundle is fetched from, which decides what loading it costs.</summary>
public enum ContentProvider {
    /// <summary>Shipped inside the application. Always there, costs nothing to reach.</summary>
    Local,

    /// <summary>Downloaded and cached. May be absent, may cost the player data, may fail.</summary>
    Remote
}

/// <summary>One addressable thing.</summary>
/// <param name="Address">What a caller asks for — <c>ui/textures/hero</c>.</param>
/// <param name="Id">The chunk, by content.</param>
/// <param name="Bundle">Which bundle holds it, or empty for a loose chunk at edit time.</param>
/// <param name="Provider">Whether that bundle is here or has to be fetched.</param>
/// <param name="Dependencies">Other addresses that have to load first.</param>
/// <param name="Labels">Groupings a caller can load or download by.</param>
/// <param name="Size">How many bytes the chunk is, uncompressed.</param>
public readonly record struct CatalogEntry(
    string Address,
    ObjectId Id,
    string Bundle,
    ContentProvider Provider,
    ImmutableArray<string> Dependencies,
    ImmutableArray<string> Labels,
    long Size
) {
    /// <summary>Whether two entries describe the same thing.</summary>
    /// <param name="other">The other entry.</param>
    /// <returns>Whether every field matches, element by element.</returns>
    /// <remarks>
    ///     <b>Written out because the compiler's version would be wrong.</b> A record's generated
    ///     equality calls <see cref="object.Equals(object)" /> on each member, and
    ///     <see cref="ImmutableArray{T}" /> compares by the identity of its backing array rather than
    ///     by its contents. Two entries read from the same file twice would then be unequal, and the
    ///     first thing anyone asks a catalog — "did this update actually change anything?" — would
    ///     answer yes every time.
    /// </remarks>
    public bool Equals(CatalogEntry other) =>
        Address == other.Address
        && Id == other.Id
        && Bundle == other.Bundle
        && Provider == other.Provider
        && Size == other.Size
        && SameStrings(Dependencies, other.Dependencies)
        && SameStrings(Labels, other.Labels);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Address, Id, Bundle, Provider, Size, Dependencies.Length);

    internal static bool SameStrings(ImmutableArray<string> left, ImmutableArray<string> right) {
        if (left.IsDefaultOrEmpty || right.IsDefaultOrEmpty) {
            return left.IsDefaultOrEmpty && right.IsDefaultOrEmpty;
        }

        return left.AsSpan().SequenceEqual(right.AsSpan(), StringComparer.Ordinal);
    }
}

/// <summary>One bundle, and what it costs to get hold of it.</summary>
/// <param name="Name">What the catalog calls it.</param>
/// <param name="Url">Where to fetch it, for a remote one. Empty for local.</param>
/// <param name="Hash">The bundle's content hash, which is also its cache key.</param>
/// <param name="Size">Its size on the wire, compressed.</param>
/// <param name="Crc">A cheap integrity check that does not need the whole file hashed.</param>
/// <param name="Compression">How its chunks are packed.</param>
/// <param name="Dependencies">Other bundles that must be present for this one's chunks to resolve.</param>
public readonly record struct CatalogBundle(
    string Name,
    string Url,
    ObjectId Hash,
    long Size,
    uint Crc,
    CompressionMethod Compression,
    ImmutableArray<string> Dependencies
) {
    /// <summary>Whether two bundles describe the same thing.</summary>
    /// <param name="other">The other bundle.</param>
    /// <returns>Whether every field matches, element by element.</returns>
    /// <remarks>Written out for the reason given on <see cref="CatalogEntry.Equals(CatalogEntry)" />.</remarks>
    public bool Equals(CatalogBundle other) =>
        Name == other.Name
        && Url == other.Url
        && Hash == other.Hash
        && Size == other.Size
        && Crc == other.Crc
        && Compression == other.Compression
        && CatalogEntry.SameStrings(Dependencies, other.Dependencies);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Url, Hash, Size, Crc, Compression);
}

/// <summary>The index a build produces and the runtime reads: address in, chunk out.</summary>
/// <remarks>
///     <para>
///         Everything downstream of the content build is a lookup in here. A game asks for
///         <c>ui/textures/hero</c>; the catalog says which chunk that is, which bundle holds it,
///         whether that bundle is on the device or has to be downloaded first, and what else has to
///         be loaded alongside it. Without the catalog an address is just a string.
///     </para>
///     <para>
///         <b>It is immutable, and that is what makes the remote update story work.</b> A shipped
///         application carries a local catalog; a content update publishes a remote one; the runtime
///         merges the second over the first and gets a third. Nothing is mutated, so a half-applied
///         update cannot exist — either the merge produced a catalog or the old one is still in use.
///         See <see cref="MergedWith" />.
///     </para>
///     <para>
///         <b>Addresses are not paths.</b> They are slash-separated by convention because that is
///         what people type, and <see cref="Match" /> globs over them, but nothing here resolves
///         <c>..</c>, normalises case or touches a filesystem. An address is a name a build chose.
///     </para>
/// </remarks>
public sealed class ContentCatalog {
    readonly Dictionary<string, CatalogEntry> entries;
    readonly Dictionary<string, CatalogBundle> bundles;
    readonly Dictionary<string, ImmutableArray<string>> labels;

    /// <summary>The catalog format's version, so an old runtime refuses a new file rather than misreading it.</summary>
    public int Version { get; }

    /// <summary>What the build hashed to. Two catalogs with the same value describe the same content.</summary>
    public ObjectId BuildHash { get; }

    /// <summary>Which target this was built for — <c>Windows</c>, <c>Android/Vulkan</c>.</summary>
    public string Target { get; }

    /// <summary>Every entry, in address order.</summary>
    public IReadOnlyCollection<CatalogEntry> Entries => entries.Values;

    /// <summary>Every bundle.</summary>
    public IReadOnlyCollection<CatalogBundle> Bundles => bundles.Values;

    /// <summary>Every label that anything carries.</summary>
    public IReadOnlyCollection<string> Labels => labels.Keys;

    /// <summary>How many addresses there are.</summary>
    public int Count => entries.Count;

    /// <summary>Builds a catalog.</summary>
    /// <param name="version">The format version.</param>
    /// <param name="buildHash">What the build hashed to.</param>
    /// <param name="target">Which target it was built for.</param>
    /// <param name="entries">The entries.</param>
    /// <param name="bundles">The bundles.</param>
    /// <exception cref="ArgumentException">Two entries share an address, or two bundles share a name.</exception>
    public ContentCatalog(
        int version,
        ObjectId buildHash,
        string target,
        IEnumerable<CatalogEntry> entries,
        IEnumerable<CatalogBundle> bundles
    ) {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(bundles);

        Version = version;
        BuildHash = buildHash;
        Target = target ?? string.Empty;

        this.entries = [];
        this.bundles = [];

        foreach (var entry in entries) {
            if (!this.entries.TryAdd(entry.Address, entry)) {
                throw new ArgumentException(
                    $"Two entries are both addressed '{entry.Address}'. An address is how a caller names one "
                    + "thing, so the build has to have picked one of them.",
                    nameof(entries)
                );
            }
        }

        foreach (var bundle in bundles) {
            if (!this.bundles.TryAdd(bundle.Name, bundle)) {
                throw new ArgumentException($"Two bundles are both called '{bundle.Name}'.", nameof(bundles));
            }
        }

        // The label index is derived rather than stored, because a label list that disagreed with the
        // entries it indexes is a bug nothing would report.
        var building = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var entry in this.entries.Values) {
            foreach (var label in entry.Labels) {
                if (!building.TryGetValue(label, out var addresses)) {
                    addresses = [];
                    building[label] = addresses;
                }

                addresses.Add(entry.Address);
            }
        }

        labels = building.ToDictionary(
            pair => pair.Key,
            pair => {
                pair.Value.Sort(StringComparer.Ordinal);
                return pair.Value.ToImmutableArray();
            },
            StringComparer.Ordinal
        );
    }

    /// <summary>Finds one address.</summary>
    /// <param name="address">The address.</param>
    /// <param name="entry">Its entry.</param>
    /// <returns>Whether the catalog has it.</returns>
    public bool TryGet(string address, out CatalogEntry entry) => entries.TryGetValue(address, out entry);

    /// <summary>Finds the bundle an entry names.</summary>
    /// <param name="name">The bundle's name.</param>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Whether the catalog has it.</returns>
    public bool TryGetBundle(string name, out CatalogBundle bundle) => bundles.TryGetValue(name, out bundle);

    /// <summary>Every address carrying a label, in address order.</summary>
    /// <param name="label">The label.</param>
    /// <returns>The addresses, empty if nothing carries it.</returns>
    public ImmutableArray<string> ByLabel(string label) =>
        labels.TryGetValue(label, out var addresses) ? addresses : [];

    /// <summary>Every address matching a glob, in address order.</summary>
    /// <param name="pattern">The pattern. See <see cref="Matches" /> for what it means.</param>
    /// <returns>The addresses.</returns>
    public ImmutableArray<string> Match(string pattern) {
        ArgumentNullException.ThrowIfNull(pattern);

        var found = new List<string>();

        foreach (var address in entries.Keys) {
            if (Matches(address, pattern)) {
                found.Add(address);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return [.. found];
    }

    /// <summary>
    ///     Everything that has to be loaded for an address to be usable, the address itself included,
    ///     in dependency-first order.
    /// </summary>
    /// <param name="addresses">The roots.</param>
    /// <returns>The closure.</returns>
    /// <exception cref="InvalidOperationException">The dependency graph has a cycle.</exception>
    /// <remarks>
    ///     Dependency-first, so a caller loading the result in order never touches something before
    ///     the thing it points at exists. An address the catalog does not have is skipped rather than
    ///     throwing: a dependency on something the build dropped is a build problem, and failing the
    ///     whole load for it would turn one missing texture into a black screen.
    /// </remarks>
    public ImmutableArray<string> Closure(params ReadOnlySpan<string> addresses) {
        var order = new List<string>();
        var settled = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in addresses) {
            Visit(address);
        }

        return [.. order];

        void Visit(string address) {
            if (settled.Contains(address)) {
                return;
            }

            if (!visiting.Add(address)) {
                throw new InvalidOperationException(
                    $"'{address}' depends on itself, through the chain {string.Join(" → ", visiting)}. A content "
                    + "build cannot produce a load order for a cycle."
                );
            }

            if (entries.TryGetValue(address, out var entry)) {
                foreach (var dependency in entry.Dependencies) {
                    Visit(dependency);
                }

                order.Add(address);
                settled.Add(address);
            }

            visiting.Remove(address);
        }
    }

    /// <summary>
    ///     A catalog with another laid over it: the remote update applied to what shipped.
    /// </summary>
    /// <param name="update">The catalog to lay over this one.</param>
    /// <returns>A third catalog. Neither input is changed.</returns>
    /// <exception cref="ArgumentException">The two were built for different targets or formats.</exception>
    /// <remarks>
    ///     <para>
    ///         An address in both takes the update's version; an address only in this one survives.
    ///         That is what makes a content update able to replace an asset without shipping every
    ///         asset — and it is also why an update cannot <i>delete</i> an address: the shipped
    ///         application still has the bundle, and a runtime that forgot the address would fail to
    ///         load something that is sitting on the device.
    ///     </para>
    ///     <para>
    ///         The build hash and version come from the update, because the merged catalog describes
    ///         what the update decided the content is.
    ///     </para>
    /// </remarks>
    public ContentCatalog MergedWith(ContentCatalog update) {
        ArgumentNullException.ThrowIfNull(update);

        if (update.Target != Target) {
            throw new ArgumentException(
                $"This catalog was built for '{Target}' and the update for '{update.Target}'. Applying one to the "
                + "other would resolve addresses to chunks in a format this device cannot read.",
                nameof(update)
            );
        }

        if (update.Version != Version) {
            throw new ArgumentException(
                $"This catalog is format version {Version} and the update is {update.Version}. Merging across "
                + "versions would need a migration nobody has written.",
                nameof(update)
            );
        }

        var merged = new Dictionary<string, CatalogEntry>(entries, StringComparer.Ordinal);

        foreach (var entry in update.entries.Values) {
            merged[entry.Address] = entry;
        }

        var mergedBundles = new Dictionary<string, CatalogBundle>(bundles, StringComparer.Ordinal);

        foreach (var bundle in update.bundles.Values) {
            mergedBundles[bundle.Name] = bundle;
        }

        return new(update.Version, update.BuildHash, Target, merged.Values, mergedBundles.Values);
    }

    /// <summary>Whether an address matches a glob.</summary>
    /// <param name="address">The address.</param>
    /// <param name="pattern">The pattern.</param>
    /// <returns>Whether it matches.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>?</c> is one character other than a slash. <c>*</c> is any run of characters other
    ///         than a slash — so <c>level1/*</c> is the things directly under <c>level1</c> and not
    ///         everything beneath it. <c>**</c> is any run including slashes, which is how to say
    ///         "everything beneath".
    ///     </para>
    ///     <para>
    ///         That distinction is the one people expect from every shell and every build tool, and
    ///         collapsing it — making <c>*</c> cross slashes — is how a preload of one level ends up
    ///         downloading the game.
    ///     </para>
    /// </remarks>
    public static bool Matches(string address, string pattern) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(pattern);

        return Walk(address, 0, pattern, 0);

        static bool Walk(string address, int a, string pattern, int p) {
            while (p < pattern.Length) {
                var character = pattern[p];

                if (character == '*') {
                    var crossesSlashes = p + 1 < pattern.Length && pattern[p + 1] == '*';
                    var next = p + (crossesSlashes ? 2 : 1);

                    // Try every length this wildcard could consume, shortest first. Anchoring the
                    // rest of the pattern after each is what makes trailing literals work.
                    for (var taken = a; taken <= address.Length; taken++) {
                        if (Walk(address, taken, pattern, next)) {
                            return true;
                        }

                        if (taken < address.Length && !crossesSlashes && address[taken] == '/') {
                            break;
                        }
                    }

                    return false;
                }

                if (a >= address.Length) {
                    return false;
                }

                if (character == '?') {
                    if (address[a] == '/') {
                        return false;
                    }
                } else if (address[a] != character) {
                    return false;
                }

                a++;
                p++;
            }

            return a == address.Length;
        }
    }

    /// <summary>How many bytes would have to be downloaded to make an address loadable.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <param name="isCached">Whether a bundle is already on the device.</param>
    /// <returns>The size on the wire, counting each bundle once.</returns>
    /// <remarks>
    ///     Counted per bundle rather than per address, because two addresses in the same bundle cost
    ///     one download. Summing entry sizes instead is the mistake that tells a player a 4 MB pack
    ///     is 40 MB.
    /// </remarks>
    public long DownloadSize(IEnumerable<string> addresses, Func<CatalogBundle, bool>? isCached = null) {
        var total = 0L;

        foreach (var bundle in RemoteBundlesFor(addresses)) {
            if (isCached is null || !isCached(bundle)) {
                total += bundle.Size;
            }
        }

        return total;
    }

    /// <summary>Every remote bundle that has to be present before some addresses can load.</summary>
    /// <param name="addresses">The addresses.</param>
    /// <returns>The bundles, each once, in the order the closure reaches them.</returns>
    /// <remarks>
    ///     The closure, not the addresses themselves: a level that is local but whose textures are in
    ///     a downloadable pack still needs that pack. What a pre-download button downloads, what a
    ///     size estimate sums, and what clearing a cache deletes are all this same list.
    /// </remarks>
    public ImmutableArray<CatalogBundle> RemoteBundlesFor(IEnumerable<string> addresses) {
        ArgumentNullException.ThrowIfNull(addresses);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = ImmutableArray.CreateBuilder<CatalogBundle>();

        foreach (var address in Closure([.. addresses])) {
            if (entries.TryGetValue(address, out var entry)
                && entry.Provider == ContentProvider.Remote
                && bundles.TryGetValue(entry.Bundle, out var bundle)
                && seen.Add(bundle.Name)) {
                found.Add(bundle);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>Finds the entry for an address, or says which one is missing.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Its entry.</returns>
    /// <exception cref="AddressNotFoundException">The catalog does not have it.</exception>
    public CatalogEntry Get(string address) =>
        entries.TryGetValue(address, out var entry) ? entry : throw new AddressNotFoundException(address);
}

/// <summary>An address nothing in the catalog answers to.</summary>
/// <param name="address">The address.</param>
public sealed class AddressNotFoundException(string address)
    : Exception(
        $"Nothing in the catalog is addressed '{address}'. Either the build did not include it, or the "
        + "address is spelled differently than the group that produced it."
    ) {
    /// <summary>The address that was asked for.</summary>
    public string Address { get; } = address;
}

/// <summary>Extensions that read better than a <c>TryGet</c> at a call site.</summary>
public static class ContentCatalogExtensions {
    /// <summary>Whether the catalog has an address.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="address">The address.</param>
    /// <returns>Whether it does.</returns>
    public static bool Contains(this ContentCatalog catalog, string address) {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGet(address, out _);
    }

    /// <summary>The bundle an address lives in.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="address">The address.</param>
    /// <param name="bundle">The bundle.</param>
    /// <returns>Whether the address exists and names a bundle the catalog knows.</returns>
    public static bool TryGetBundleFor(
        this ContentCatalog catalog,
        string address,
        [NotNullWhen(true)] out CatalogBundle? bundle
    ) {
        ArgumentNullException.ThrowIfNull(catalog);
        bundle = null;

        if (!catalog.TryGet(address, out var entry) || !catalog.TryGetBundle(entry.Bundle, out var found)) {
            return false;
        }

        bundle = found;
        return true;
    }
}
