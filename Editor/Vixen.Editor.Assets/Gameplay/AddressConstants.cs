// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Vixen.Editor.Assets.Gameplay;

/// <summary>What emitting a build's addresses produced.</summary>
/// <param name="Source">The C# file, or empty when there was nothing to write.</param>
/// <param name="Count">How many addresses reached it.</param>
/// <param name="Problems">Addresses that could not be named, in address order.</param>
public readonly record struct AddressConstantsResult(string Source, int Count, ImmutableArray<string> Problems);

/// <summary>Turns a build's addresses into constants a game can spell wrong at compile time.</summary>
/// <remarks>
///     <para>
///         <b>The last thing doc 28's G0 owed.</b> § Definitions makes an address the only identity —
///         <c>DefId.From("items/flamebrand")</c>, a hash, no registry — and the cost of that is a
///         magic string in every rule that names content. This is the other half: the content build
///         knows every address it shipped, so it can write them down once and a typo becomes a
///         compile error instead of a rule that silently never fires.
///     </para>
///     <para>
///         <b>Editor-time rather than a Roslyn generator, and that is forced.</b> A source generator
///         sees the compilation and nothing else; the address list is a property of the *content*
///         build, which runs beside the compiler rather than inside it. <c>Vixen.Sdk</c>'s import
///         target already runs <c>BeforeTargets="CoreCompile"</c> for exactly this — its comment says
///         <em>"generated C# … has to exist before CoreCompile reads the compile items. Nothing
///         generates C# yet; the ordering is what makes that a scheduling detail later rather than a
///         redesign."</em> This is the first thing to use it.
///     </para>
///     <para>
///         ⚠ <b>The id is computed from the string, never emitted as a literal.</b> Writing
///         <c>new DefId(0x9A3C1F04)</c> would be a second implementation of the hash, in generated
///         code nobody reads, that silently disagrees with <c>DefId.From</c> the day the hash changes
///         — and the failure is every id in the game resolving to nothing. One FNV pass per constant
///         at type-init is the price of that not being possible.
///     </para>
///     <para>
///         ⚠ <b>Every address gets a class of its own rather than a field on its parent.</b> The
///         obvious shape — a field named after the leaf — cannot express an address that is also a
///         prefix of another, and <c>maps/greenmarch</c> beside <c>maps/greenmarch/spawns</c> is
///         ordinary content. A class can hold both its own <c>Address</c> and its children, so the
///         shape is uniform and the case never arises.
///     </para>
/// </remarks>
public static class AddressConstants {
    /// <summary>What the file is called, wherever it is written.</summary>
    public const string FileName = "Addresses.g.cs";

    /// <summary>The member holding an address's own text.</summary>
    public const string AddressMember = "Address";

    /// <summary>The member holding an address's <c>DefId</c>.</summary>
    public const string IdMember = "Id";

    static readonly string[] Separators = ["-", "_", ".", " "];

    /// <summary>Writes the constants for a build's addresses.</summary>
    /// <param name="addresses">Every address the build shipped.</param>
    /// <param name="namespace">What namespace to put them in.</param>
    /// <param name="root">What the outermost class is called.</param>
    /// <param name="ids">
    ///     Whether to emit a <c>DefId</c> beside each address.
    ///     <para>
    ///         ⚠ Off by default, because the generated file would then reference
    ///         <c>Vixen.Gameplay</c> and a game that has declined the gameplay libraries would get a
    ///         file it cannot compile — from a build step it did not know it had turned on. A game
    ///         that uses definitions turns it on and gets the half doc 28 asked for.
    ///     </para>
    /// </param>
    /// <returns>The source, and anything that could not be named.</returns>
    public static AddressConstantsResult Emit(
        IEnumerable<string> addresses,
        string @namespace,
        string root = "Addresses",
        bool ids = false
    ) {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var problems = new List<string>();
        var tree = new Node(string.Empty);
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;

        // Address order, so a build that fails twice fails the same way and the file's bytes do not
        // depend on the order a dictionary enumerated in — doc 12 gates the content build on that.
        foreach (var address in addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            if (string.IsNullOrWhiteSpace(address)) {
                continue;
            }

            var segments = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var names = new List<string>(segments.Length);
            var named = true;

            foreach (var segment in segments) {
                var identifier = Identifier(segment);

                if (identifier.Length == 0) {
                    problems.Add(
                        $"'{address}' has a segment ('{segment}') with nothing in it that can be part of a C# "
                        + "name, so no constant is written for it."
                    );

                    named = false;

                    break;
                }

                names.Add(identifier);
            }

            if (!named) {
                continue;
            }

            var path = string.Join('.', names);

            // ⚠ Reported and neither is emitted. Emitting the first would make the second invisible,
            // and whoever wrote it would find out when their rule silently matched nothing — which is
            // the failure a compile-time constant exists to prevent.
            if (claimed.TryGetValue(path, out var already)) {
                problems.Add(
                    $"'{address}' and '{already}' both become '{root}.{path}', so neither is written. "
                    + "Rename one — two addresses a C# name cannot tell apart are two pieces of content "
                    + "somebody will confuse."
                );

                tree.Remove(names);

                continue;
            }

            claimed.Add(path, address);
            tree.Add(names, address);
            count++;
        }

        // A clash removes the address that claimed the name first, so the count has to come from what
        // actually survived rather than from what was accepted along the way.
        count = tree.Count();

        return new(count == 0 ? string.Empty : Write(tree, @namespace, root, ids), count, [.. problems.Order(StringComparer.Ordinal)]);
    }

    /// <summary>Turns one address segment into a C# identifier, or empty when nothing can be.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The identifier.</returns>
    public static string Identifier(string? segment) {
        if (string.IsNullOrWhiteSpace(segment)) {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);

        foreach (var word in segment.Split(Separators, StringSplitOptions.RemoveEmptyEntries)) {
            var start = builder.Length;

            foreach (var character in word) {
                if (char.IsLetterOrDigit(character)) {
                    builder.Append(character);
                }
            }

            if (builder.Length > start) {
                builder[start] = char.ToUpper(builder[start], CultureInfo.InvariantCulture);
            }
        }

        if (builder.Length == 0) {
            return string.Empty;
        }

        // A leading digit is not an identifier, and prefixing beats dropping: 'maps/2-crypt' would
        // otherwise become 'Crypt' and collide with 'maps/crypt' for a reason nobody could see.
        return char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    static string Write(Node tree, string @namespace, string root, bool ids) {
        var source = new StringBuilder();

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("// Written by the Vixen content build. Edits are lost on the next one.");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        if (ids) {
            source.AppendLine("using Vixen.Gameplay;");
            source.AppendLine();
        }

        source.AppendLine(CultureInfo.InvariantCulture, $"namespace {@namespace};");
        source.AppendLine();
        source.AppendLine("/// <summary>Every address this build shipped.</summary>");
        source.AppendLine(CultureInfo.InvariantCulture, $"public static partial class {root} {{");

        foreach (var child in tree.Children) {
            Write(source, child, 1, ids);
        }

        source.AppendLine("}");

        return source.ToString();
    }

    static void Write(StringBuilder source, Node node, int depth, bool ids) {
        // A subtree with no address left in it is a class with nothing in it, which is what a clash
        // leaves behind. Emitting it would put a name in a game's completion list that resolves to
        // nothing, which is a worse answer than the name being absent.
        if (node.Count() == 0) {
            return;
        }

        var indent = new string(' ', depth * 4);
        var inner = new string(' ', (depth + 1) * 4);

        source.AppendLine(CultureInfo.InvariantCulture, $"{indent}/// <summary><c>{node.Name}</c>.</summary>");
        source.AppendLine(CultureInfo.InvariantCulture, $"{indent}public static partial class {node.Name} {{");

        if (node.Address is { } address) {
            source.AppendLine(CultureInfo.InvariantCulture, $"{inner}/// <summary>The address: <c>{address}</c>.</summary>");
            source.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}public const string {AddressMember} = \"{address}\";"
            );

            if (ids) {
                source.AppendLine();
                source.AppendLine(CultureInfo.InvariantCulture, $"{inner}/// <summary>The id of <c>{address}</c>.</summary>");
                source.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}public static readonly DefId {IdMember} = DefId.From({AddressMember});"
                );
            }

            if (node.HasChildren) {
                source.AppendLine();
            }
        }

        foreach (var child in node.Children) {
            Write(source, child, depth + 1, ids);
        }

        source.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    // ⚠ There is deliberately no keyword-escaping table here, and writing one is how you find out it
    // is unreachable: every C# keyword is lowercase and Identifier upper-cases the first letter of
    // every segment, so 'spells/class' becomes 'Class' and never 'class'. A table of eighty keywords
    // that can never match is worse than none — it reads as a handled case and is a dead branch.

    sealed class Node(string name) {
        readonly Dictionary<string, Node> children = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public string? Address { get; private set; }

        public bool HasChildren => children.Count > 0;

        public IEnumerable<Node> Children => children.Values.OrderBy(child => child.Name, StringComparer.Ordinal);

        public void Add(IReadOnlyList<string> path, string address) {
            var node = this;

            foreach (var segment in path) {
                if (!node.children.TryGetValue(segment, out var child)) {
                    child = new(segment);
                    node.children.Add(segment, child);
                }

                node = child;
            }

            node.Address = address;
        }

        public void Remove(IReadOnlyList<string> path) {
            var node = this;

            foreach (var segment in path) {
                if (!node.children.TryGetValue(segment, out var child)) {
                    return;
                }

                node = child;
            }

            node.Address = null;
        }

        public int Count() {
            var total = Address is null ? 0 : 1;

            foreach (var child in children.Values) {
                total += child.Count();
            }

            return total;
        }
    }
}
