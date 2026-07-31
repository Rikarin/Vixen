// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.DocGen;

/// <summary>What class of change a row is — docs/plan/25 § 6.2's table, in order of how much it costs a reader.</summary>
enum ChangeKind {
    Added,
    Removed,
    Deprecated,
    SignatureBreak,
    ShapeBreak,
    SemanticBreak,
    EngineBreak
}

/// <summary>One row of a release's table.</summary>
/// <param name="Kind">Which rule fired.</param>
/// <param name="Id">The documentation id, or a guide slug for a semantic break.</param>
/// <param name="Display">What the row calls it.</param>
/// <param name="Taxonomy">The node's kind slug, so the site can badge the row.</param>
/// <param name="Before">The previous form, when there was one.</param>
/// <param name="After">The current form.</param>
/// <param name="Note">Why it matters, in a sentence — the only prose in the table.</param>
sealed record Change(
    ChangeKind Kind,
    string Id,
    string Display,
    string Taxonomy,
    string? Before = null,
    string? After = null,
    string? Note = null
) {
    /// <summary>Whether the row is one a reader has to act on before upgrading.</summary>
    public bool IsBreaking => Kind is not (ChangeKind.Added or ChangeKind.Deprecated);
}

/// <summary>
///     The release diff — docs/plan/25 § 6.2.
/// </summary>
/// <remarks>
///     <para>
///         Computed from two graphs rather than from two baselines, because a graph carries what a
///         baseline cannot: a component's size, a system's phase, a shader's descriptor sets. The
///         last row of § 6.2's table is the one no generic tool would produce and the one an engine
///         user most needs — a component whose layout changed loads old scenes wrong, and its
///         signature is identical.
///     </para>
///     <para>
///         ⚠ <b>A removed type does not also remove its members.</b> Forty rows saying a method is
///         gone, under one row saying the type is, is a table nobody reads to the end; the members of
///         a type that appeared or vanished are folded into the type's own row.
///     </para>
/// </remarks>
static class ReleaseDiff {
    /// <summary>One comparable declaration — a type, or a member of one.</summary>
    sealed record Entry(
        string Id,
        string Display,
        string Taxonomy,
        string Signature,
        string? Obsolete,
        string? Owner,
        DocNode? Node
    );

    /// <summary>Every change from <paramref name="before" /> to <paramref name="after" />.</summary>
    /// <param name="semantic">
    ///     The hand-written half — `breaking:` entries from guide front matter. "This now defaults to
    ///     linear space" is in no signature, and a release note without it is a release note that
    ///     lies by omission.
    /// </param>
    public static IReadOnlyList<Change> Between(
        DocGraph before,
        DocGraph after,
        IEnumerable<(string Slug, string Text)>? semantic = null
    ) {
        var previous = Entries(before);
        var current = Entries(after);
        var changes = new List<Change>();

        var removedTypes = previous.Values
            .Where(entry => entry.Owner is null && !current.ContainsKey(entry.Id))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        var addedTypes = current.Values
            .Where(entry => entry.Owner is null && !previous.ContainsKey(entry.Id))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in current.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal)) {
            if (previous.TryGetValue(entry.Id, out var was)) {
                changes.AddRange(Compare(was, entry));

                continue;
            }

            if (entry.Owner is not null && addedTypes.Contains(entry.Owner)) {
                continue;
            }

            changes.Add(new Change(ChangeKind.Added, entry.Id, entry.Display, entry.Taxonomy, After: entry.Signature));
        }

        foreach (var entry in previous.Values
            .Where(entry => !current.ContainsKey(entry.Id))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)) {
            if (entry.Owner is not null && removedTypes.Contains(entry.Owner)) {
                continue;
            }

            changes.Add(new Change(
                ChangeKind.Removed,
                entry.Id,
                entry.Display,
                entry.Taxonomy,
                Before: entry.Signature,
                Note: entry.Owner is null ? "the type is gone" : "the member is gone"));
        }

        foreach (var (slug, text) in semantic ?? []) {
            changes.Add(new Change(ChangeKind.SemanticBreak, slug, text, "guide", Note: $"declared by `{slug}`"));
        }

        return [.. changes.OrderBy(change => change.Kind).ThenBy(change => change.Id, StringComparer.Ordinal)];
    }

    static IEnumerable<Change> Compare(Entry was, Entry now) {
        if (was.Obsolete is null && now.Obsolete is not null) {
            yield return new Change(
                ChangeKind.Deprecated,
                now.Id,
                now.Display,
                now.Taxonomy,
                After: now.Signature,
                Note: now.Obsolete);
        }

        var shape = now.Node is null || was.Node is null ? null : ShapeChange(was.Node, now.Node);

        if (shape is not null) {
            yield return new Change(
                ChangeKind.ShapeBreak,
                now.Id,
                now.Display,
                now.Taxonomy,
                was.Signature,
                now.Signature,
                shape);
        } else if (!string.Equals(was.Signature, now.Signature, StringComparison.Ordinal)) {
            yield return new Change(
                ChangeKind.SignatureBreak,
                now.Id,
                now.Display,
                now.Taxonomy,
                was.Signature,
                now.Signature,
                was.Owner is null ? "the declaration changed" : "the signature changed");
        }

        if (was.Node is not null && now.Node is not null) {
            var engine = EngineChange(was.Node, now.Node);

            if (engine is not null) {
                yield return new Change(
                    ChangeKind.EngineBreak,
                    now.Id,
                    now.Display,
                    now.Taxonomy,
                    Note: engine);
            }
        }
    }

    /// <summary>
    ///     § 6.2's <b>breaking — shape</b> row: the five ways a type stops being usable the way it was
    ///     without its signature saying anything a diff would notice on its own.
    /// </summary>
    static string? ShapeChange(DocNode was, DocNode now) {
        if (!Modifier(was, "sealed") && Modifier(now, "sealed")) {
            return "sealed — anything deriving from it stops compiling";
        }

        if (!Modifier(was, "abstract") && Modifier(now, "abstract")) {
            return "abstract — it can no longer be constructed";
        }

        if (!Modifier(was, "ref") && Modifier(now, "ref")) {
            return "a ref struct — it can no longer be boxed, stored in a field or captured";
        }

        if (now.Kind == DocKind.Enum && Underlying(was) is var wasUnderlying && Underlying(now) is var nowUnderlying
            && !string.Equals(wasUnderlying, nowUnderlying, StringComparison.Ordinal)) {
            return $"underlying type {wasUnderlying} → {nowUnderlying} — every serialised value changes width";
        }

        if (!string.Equals(was.BaseType, now.BaseType, StringComparison.Ordinal)) {
            return $"base type {was.BaseType ?? "none"} → {now.BaseType ?? "none"}";
        }

        var dropped = was.Interfaces.Except(now.Interfaces, StringComparer.Ordinal).ToList();

        return dropped.Count > 0
            ? $"no longer implements {string.Join(", ", dropped.Select(Short))}"
            : null;
    }

    /// <summary>
    ///     § 6.2's <b>engine-specific</b> row — the changes that are invisible in a signature and
    ///     break a scene, a frame or a pipeline.
    /// </summary>
    static string? EngineChange(DocNode was, DocNode now) {
        var before = was.Facets;
        var after = now.Facets;

        if (before is null || after is null) {
            return null;
        }

        if (before.SizeBytes != after.SizeBytes) {
            return $"size {before.SizeBytes} b → {after.SizeBytes} b — scenes saved with the old layout "
                + "load wrong";
        }

        if (!string.Equals(before.Phase, after.Phase, StringComparison.Ordinal)) {
            return $"phase {before.Phase} → {after.Phase} — it runs at a different point in the frame";
        }

        if (!Same(before.RunsBefore, after.RunsBefore) || !Same(before.RunsAfter, after.RunsAfter)) {
            return "its ordering constraints changed — systems around it run in a different order";
        }

        if (!Same(before.Reads, after.Reads) || !Same(before.Writes, after.Writes)) {
            return "its declared access changed — the scheduler parallelises it differently";
        }

        if (before.DescriptorSets != after.DescriptorSets || !Same(before.VertexInputs, after.VertexInputs)) {
            return "its descriptor layout changed — anything binding it has to be recompiled";
        }

        if (!string.Equals(before.Channel, after.Channel, StringComparison.Ordinal)) {
            return $"replication channel {before.Channel} → {after.Channel} — the wire format moved";
        }

        return string.Equals(before.Level, after.Level, StringComparison.Ordinal)
            ? null
            : $"log level {before.Level} → {after.Level} — anything filtering on it sees a different set";
    }

    /// <summary>An enum's underlying type, which is written after the colon or is <c>int</c>.</summary>
    static string Underlying(DocNode node) {
        var text = Text(node.Signature);
        var colon = text.IndexOf(':', StringComparison.Ordinal);

        return colon < 0 ? "int" : text[(colon + 1)..].Trim();
    }

    static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    static bool Modifier(DocNode node, string keyword) =>
        node.Signature.Any(span =>
            span.Kind == "keyword" && string.Equals(span.Text, keyword, StringComparison.Ordinal));

    static Dictionary<string, Entry> Entries(DocGraph graph) {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes) {
            entries[node.Id] = new Entry(
                node.Id,
                node.QualifiedName,
                Taxonomy.Slug(node.Kind),
                Text(node.Signature),
                node.Obsolete,
                null,
                node);

            foreach (var member in node.Members) {
                // Overloads share a name and not an id, which is exactly why the id is the key and
                // the name is only what the row is called.
                entries[member.Id] = new Entry(
                    member.Id,
                    $"{node.QualifiedName}.{member.Name}",
                    member.MemberKind,
                    Text(member.Signature),
                    member.Obsolete,
                    node.Id,
                    null);
            }
        }

        return entries;
    }

    static string Text(IReadOnlyList<DocSpan> signature) =>
        string.Concat(signature.Select(span => span.Text));

    static string Short(string id) {
        var name = id.StartsWith("T:", StringComparison.Ordinal) ? id[2..] : id;
        var dot = name.LastIndexOf('.');

        return dot < 0 ? name : name[(dot + 1)..];
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>How many rows a section prints before it starts counting instead.</summary>
    const int SectionLimit = 100;

    static readonly (ChangeKind Kind, string Heading)[] Sections = [
        (ChangeKind.Removed, "Removed"),
        (ChangeKind.ShapeBreak, "Breaking — shape"),
        (ChangeKind.SignatureBreak, "Breaking — signature"),
        (ChangeKind.EngineBreak, "Breaking — engine"),
        (ChangeKind.SemanticBreak, "Breaking — behaviour"),
        (ChangeKind.Deprecated, "Deprecated"),
        (ChangeKind.Added, "Added")
    ];

    /// <summary>The `CHANGELOG.md` section for a tag, which is the same table the site renders.</summary>
    public static string Markdown(
        string version,
        string? previousVersion,
        string date,
        IReadOnlyList<Change> changes
    ) {
        var builder = new StringBuilder();
        var breaking = changes.Count(change => change.IsBreaking);

        builder.Append("## ").Append(version).Append(" — ").Append(date).Append("\n\n");

        builder.Append(previousVersion is null
                ? "The first release. There is nothing before it to compare against, so what this "
                + "section records is that the surface begins here — everything in it is new by "
                + "definition, and the next release is the first one with a table.\n\n"
                : $"Compared with {previousVersion}: **{changes.Count(change => change.Kind == ChangeKind.Added)} "
                + $"added**, {changes.Count(change => change.Kind == ChangeKind.Removed)} removed, "
                + $"{changes.Count(change => change.Kind == ChangeKind.Deprecated)} deprecated, "
                + $"**{breaking} breaking**.\n\n");

        if (changes.Count == 0) {
            if (previousVersion is not null) {
                builder.Append("No public API changed.\n");
            }

            return builder.ToString();
        }

        foreach (var (kind, heading) in Sections) {
            var rows = changes.Where(change => change.Kind == kind).ToList();

            if (rows.Count == 0) {
                continue;
            }

            builder.Append("### ").Append(heading).Append(" (").Append(rows.Count).Append(")\n\n");
            builder.Append("| | Symbol | What changed |\n|---|---|---|\n");

            foreach (var row in rows.Take(SectionLimit)) {
                builder.Append("| `").Append(row.Taxonomy).Append("` | `").Append(row.Display).Append("` | ")
                    .Append(Cell(row)).Append(" |\n");
            }

            if (rows.Count > SectionLimit) {
                builder.Append("| | | …and ").Append(rows.Count - SectionLimit)
                    .Append(" more, in the release's JSON |\n");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    static string Cell(Change change) {
        var note = change.Note is null ? string.Empty : Escape(change.Note);

        return change.Kind switch {
            ChangeKind.Added => $"`{Escape(change.After ?? string.Empty)}`",
            ChangeKind.SignatureBreak or ChangeKind.ShapeBreak =>
                $"{note}<br>`{Escape(change.Before ?? string.Empty)}` → `{Escape(change.After ?? string.Empty)}`",
            _ => note
        };
    }

    /// <summary>A signature has pipes in it — `Func&lt;A, B&gt;` does not, but `a \| b` in a default does.</summary>
    static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
