// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;

namespace Vixen.Editor.Ai;

/// <summary>Which of a node's three lists an attachment is in.</summary>
public enum BehaviorAttachmentSlot : byte {
    /// <summary>A condition that gates the node.</summary>
    Decorator,

    /// <summary>Something that runs while the node's branch is active.</summary>
    Service
}

/// <summary>An editable behaviour tree: the document, plus everything a gesture needs to ask it.</summary>
/// <remarks>
///     <para>
///         The document <i>is</i> <see cref="BehaviorTreeContent" /> — the same shape the file holds
///         and the same shape the compiler takes — and this is the operations over it. There is no
///         second model to keep in step, which is the bargain <c>Vixen.Editor.AnimationGraph</c>
///         makes and for the same reason: two models is two places for a field to be added and one
///         place for it to be forgotten.
///     </para>
///     <para>
///         ⚠ <b>Undo is a snapshot, not a per-field inverse.</b> The node graphs use fine-grained
///         commands because a shader graph is thousands of nodes and a snapshot per keystroke would
///         be megabytes; a behaviour tree is tens of nodes, and a snapshot is a few kilobytes of
///         strings. What that buys is that <i>every</i> gesture is undoable by construction — a
///         reparent, a reorder, a key rename that rewrote forty references — with no chance of an
///         inverse that restores four of the five things it changed. <c>BehaviorTreeDocument</c> is
///         where the snapshots become undo entries.
///     </para>
///     <para>
///         ⚠ <b>A node's identity is the object.</b> Nothing carries a GUID: the content tree is
///         reachable only through this model, a snapshot restore replaces the whole tree at once, and
///         a selection is re-resolved from the path after one. An id would be a second thing the file
///         has to carry and a person editing the YAML has to keep unique.
///     </para>
/// </remarks>
public sealed class BehaviorTreeModel {
    /// <summary>Creates a model over a document.</summary>
    /// <param name="content">The tree. Taken by reference and edited in place.</param>
    /// <param name="schema">The node library its type names are looked up in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public BehaviorTreeModel(BehaviorTreeContent content, BehaviorNodeSchema? schema = null) {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        Schema = schema ?? BehaviorNodeSchema.Default;
    }

    /// <summary>The tree.</summary>
    public BehaviorTreeContent Content { get; private set; }

    /// <summary>The node library.</summary>
    public BehaviorNodeSchema Schema { get; }

    /// <summary>Raised after anything changes.</summary>
    public event Action<BehaviorTreeModel>? Changed;

    /// <summary>Every node, in pre-order — which is the priority order the badges show.</summary>
    /// <remarks>
    ///     ⚠ <b>The editor's pre-order, not the compiled template's.</b> The compiler splices a
    ///     static subtree in place of its node, so a tree with one in it has more compiled nodes than
    ///     authored ones and the two numberings part company below the splice. The badge shows what
    ///     the author is looking at; the compiled index is the debugger's.
    /// </remarks>
    public IEnumerable<BehaviorNodeContent> Walk() {
        if (Content.Root is null) {
            yield break;
        }

        var stack = new Stack<BehaviorNodeContent>();

        stack.Push(Content.Root);

        while (stack.Count > 0) {
            var node = stack.Pop();

            yield return node;

            for (var index = node.Children.Count - 1; index >= 0; index--) {
                stack.Push(node.Children[index]);
            }
        }
    }

    /// <summary>How many nodes there are.</summary>
    public int Count => Walk().Count();

    /// <summary>A node's position in the pre-order walk, or <c>-1</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its index.</returns>
    public int IndexOf(BehaviorNodeContent node) {
        var index = 0;

        foreach (var walked in Walk()) {
            if (ReferenceEquals(walked, node)) {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>The node at a pre-order index, or null.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The node.</returns>
    public BehaviorNodeContent? At(int index) => Walk().ElementAtOrDefault(index);

    /// <summary>A node's parent, or null for the root and for anything not in this tree.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Its parent.</returns>
    public BehaviorNodeContent? Parent(BehaviorNodeContent node) {
        foreach (var candidate in Walk()) {
            foreach (var child in candidate.Children) {
                if (ReferenceEquals(child, node)) {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Whether one node is inside another's subtree, itself included.</summary>
    /// <param name="ancestor">The subtree's root.</param>
    /// <param name="node">The node to test.</param>
    /// <returns>Whether it is inside.</returns>
    public static bool Contains(BehaviorNodeContent ancestor, BehaviorNodeContent node) {
        ArgumentNullException.ThrowIfNull(ancestor);

        if (ReferenceEquals(ancestor, node)) {
            return true;
        }

        foreach (var child in ancestor.Children) {
            if (Contains(child, node)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a node's type declares, or null when the file names something unknown.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The declaration.</returns>
    public BehaviorNodeType? TypeOf(BehaviorNodeContent node) {
        ArgumentNullException.ThrowIfNull(node);

        return Schema.TryGet(node.Type, out var type) ? type : null;
    }

    /// <summary>What an attachment's type declares.</summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>The declaration.</returns>
    public BehaviorNodeType? TypeOf(BehaviorAttachmentContent attachment) {
        ArgumentNullException.ThrowIfNull(attachment);

        return Schema.TryGet(attachment.Type, out var type) ? type : null;
    }

    // ── Structure ────────────────────────────────────────────────────────────

    /// <summary>Replaces the whole document. What an undo does.</summary>
    /// <param name="content">The tree to show instead.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public void Replace(BehaviorTreeContent content) {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        Raise();
    }

    /// <summary>A deep copy of the document, for an undo entry.</summary>
    /// <returns>The copy.</returns>
    public BehaviorTreeContent Snapshot() => Clone(Content);

    /// <summary>A deep copy of any tree.</summary>
    /// <param name="content">The tree.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public static BehaviorTreeContent Copy(BehaviorTreeContent content) {
        ArgumentNullException.ThrowIfNull(content);

        return Clone(content);
    }

    /// <summary>Makes a node of a type, with its declared defaults filled in.</summary>
    /// <param name="type">Which type.</param>
    /// <param name="name">What to call it, or null for the type's label.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is null.</exception>
    public static BehaviorNodeContent Make(BehaviorNodeType type, string? name = null) {
        ArgumentNullException.ThrowIfNull(type);

        var node = new BehaviorNodeContent { Name = name ?? type.Label, Type = type.Type };

        Fill(type, node.Fields);

        return node;
    }

    /// <summary>Makes an attachment of a type, with its declared defaults filled in.</summary>
    /// <param name="type">Which type.</param>
    /// <returns>The attachment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is null.</exception>
    public static BehaviorAttachmentContent MakeAttachment(BehaviorNodeType type) {
        ArgumentNullException.ThrowIfNull(type);

        var attachment = new BehaviorAttachmentContent { Type = type.Type };

        Fill(type, attachment.Fields);

        return attachment;
    }

    /// <summary>Puts a node under a parent.</summary>
    /// <param name="parent">Where it goes, or null to make it the root.</param>
    /// <param name="node">The node.</param>
    /// <param name="at">Which position among the children, or <c>-1</c> for last.</param>
    /// <exception cref="ArgumentNullException"><paramref name="node" /> is null.</exception>
    public void Insert(BehaviorNodeContent? parent, BehaviorNodeContent node, int at = -1) {
        ArgumentNullException.ThrowIfNull(node);

        if (parent is null) {
            Content.Root = node;
            Raise();

            return;
        }

        parent.Children.Insert(at < 0 || at > parent.Children.Count ? parent.Children.Count : at, node);
        Raise();
    }

    /// <summary>Takes a node and its subtree out.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it was in the tree.</returns>
    public bool Remove(BehaviorNodeContent node) {
        if (ReferenceEquals(Content.Root, node)) {
            Content.Root = null;
            Raise();

            return true;
        }

        if (Parent(node!) is not { } parent) {
            return false;
        }

        parent.Children.Remove(node!);
        Raise();

        return true;
    }

    /// <summary>Moves a node to a new parent and position.</summary>
    /// <param name="node">The node.</param>
    /// <param name="parent">Its new parent.</param>
    /// <param name="at">Which position, or <c>-1</c> for last.</param>
    /// <returns>Whether the move was legal and happened.</returns>
    /// <remarks>
    ///     ⚠ <b>A node cannot be moved inside its own subtree</b>, and refusing is not a nicety: the
    ///     gesture is one drag away from the ordinary case, and a tree that allowed it would lose the
    ///     subtree out of the document the moment it was let go.
    /// </remarks>
    public bool Reparent(BehaviorNodeContent node, BehaviorNodeContent parent, int at = -1) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(parent);

        if (Contains(node, parent) || ReferenceEquals(node, Content.Root)) {
            return false;
        }

        var from = Parent(node);

        if (from is null) {
            return false;
        }

        var was = from.Children.IndexOf(node);

        from.Children.RemoveAt(was);

        // ⚠ Adjusted after the removal, and only when the node came out of the same list ahead of
        // where it is going: without this, dragging a child two places to the right lands it one
        // place short, every time, which reads as the editor being wrong about what was grabbed.
        var target = at < 0 ? parent.Children.Count : at;

        if (ReferenceEquals(from, parent) && was < target) {
            target--;
        }

        parent.Children.Insert(Math.Clamp(target, 0, parent.Children.Count), node);
        Raise();

        return true;
    }

    /// <summary>Moves a child up or down among its siblings.</summary>
    /// <param name="node">The node.</param>
    /// <param name="delta">How far, negative for earlier.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     The keyboard half of the reorder gesture. Doc 37 § D5 asks for both: a drag onto the gap
    ///     between two siblings, and ↑/↓ on the selection — because the whole priority ordering of
    ///     the tree is this list, and a gesture nobody can do precisely is one they will avoid.
    /// </remarks>
    public bool Reorder(BehaviorNodeContent node, int delta) {
        if (Parent(node!) is not { } parent) {
            return false;
        }

        var was = parent.Children.IndexOf(node!);
        var target = was + delta;

        if (target < 0 || target >= parent.Children.Count || delta == 0) {
            return false;
        }

        parent.Children.RemoveAt(was);
        parent.Children.Insert(target, node!);
        Raise();

        return true;
    }

    // ── Attachments ──────────────────────────────────────────────────────────

    /// <summary>One of a node's two attachment lists.</summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">Which list.</param>
    /// <returns>The list.</returns>
    public static List<BehaviorAttachmentContent> Attachments(BehaviorNodeContent node, BehaviorAttachmentSlot slot) {
        ArgumentNullException.ThrowIfNull(node);

        return slot == BehaviorAttachmentSlot.Decorator ? node.Decorators : node.Services;
    }

    /// <summary>Attaches something to a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">Which list.</param>
    /// <param name="attachment">The attachment.</param>
    /// <param name="at">Which position, or <c>-1</c> for last.</param>
    public void Attach(
        BehaviorNodeContent node,
        BehaviorAttachmentSlot slot,
        BehaviorAttachmentContent attachment,
        int at = -1
    ) {
        ArgumentNullException.ThrowIfNull(attachment);

        var list = Attachments(node, slot);

        list.Insert(at < 0 || at > list.Count ? list.Count : at, attachment);
        Raise();
    }

    /// <summary>Takes an attachment off.</summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">Which list.</param>
    /// <param name="attachment">The attachment.</param>
    /// <returns>Whether it was there.</returns>
    public bool Detach(BehaviorNodeContent node, BehaviorAttachmentSlot slot, BehaviorAttachmentContent attachment) {
        if (!Attachments(node, slot).Remove(attachment!)) {
            return false;
        }

        Raise();

        return true;
    }

    /// <summary>Moves an attachment up or down its list.</summary>
    /// <param name="node">The node.</param>
    /// <param name="slot">Which list.</param>
    /// <param name="attachment">The attachment.</param>
    /// <param name="delta">How far, negative for earlier.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Decorator order is significant and this is how it is authored.</b> They evaluate top
    ///     to bottom and the first failure stops the rest, so putting the cheap test above the trace
    ///     is a decision the editor has to let somebody make — doc 37 § D4.
    /// </remarks>
    public bool MoveAttachment(
        BehaviorNodeContent node,
        BehaviorAttachmentSlot slot,
        BehaviorAttachmentContent attachment,
        int delta
    ) {
        var list = Attachments(node, slot);
        var was = list.IndexOf(attachment!);
        var target = was + delta;

        if (was < 0 || target < 0 || target >= list.Count || delta == 0) {
            return false;
        }

        list.RemoveAt(was);
        list.Insert(target, attachment!);
        Raise();

        return true;
    }

    // ── Fields ───────────────────────────────────────────────────────────────

    /// <summary>Writes one field on a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="field">Which field.</param>
    /// <param name="value">Its new value.</param>
    public void SetField(BehaviorNodeContent node, string field, string value) {
        ArgumentNullException.ThrowIfNull(node);

        node.Fields[field] = value;
        Raise();
    }

    /// <summary>Writes one field on an attachment.</summary>
    /// <param name="attachment">The attachment.</param>
    /// <param name="field">Which field.</param>
    /// <param name="value">Its new value.</param>
    public void SetField(BehaviorAttachmentContent attachment, string field, string value) {
        ArgumentNullException.ThrowIfNull(attachment);

        attachment.Fields[field] = value;
        Raise();
    }

    /// <summary>Renames a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="name">Its new name.</param>
    public void Rename(BehaviorNodeContent node, string name) {
        ArgumentNullException.ThrowIfNull(node);

        node.Name = name;
        Raise();
    }

    /// <summary>Moves a node's box.</summary>
    /// <param name="node">The node.</param>
    /// <param name="x">Where to.</param>
    /// <param name="y">Ditto.</param>
    public void Move(BehaviorNodeContent node, float x, float y) {
        ArgumentNullException.ThrowIfNull(node);

        node.X = x;
        node.Y = y;
        Raise();
    }

    // ── The blackboard ───────────────────────────────────────────────────────

    /// <summary>Adds a key.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="type">What it holds.</param>
    /// <returns>The key, or null if the name is taken.</returns>
    public BehaviorKeyContent? AddKey(string name, BlackboardValueType type) {
        if (string.IsNullOrWhiteSpace(name) || Content.Keys.Any(key => string.Equals(key.Name, name, StringComparison.Ordinal))) {
            return null;
        }

        var added = new BehaviorKeyContent { Name = name, Type = type };

        Content.Keys.Add(added);
        Raise();

        return added;
    }

    /// <summary>Renames a key, and every reference to it in this document.</summary>
    /// <param name="key">The key.</param>
    /// <param name="name">Its new name.</param>
    /// <returns>How many references were rewritten, or <c>-1</c> if the name was refused.</returns>
    /// <remarks>
    ///     ⚠ <b>The rewrite is the whole point of the operation.</b> A file references a key by name
    ///     and the compiled form by index, precisely so that a rename is a thing the editor can do —
    ///     but a rename that only changed the declaration would leave every decorator pointing at a
    ///     key that no longer exists, and the tree would compile to a list of complaints about a
    ///     rename that looked like it worked.
    /// </remarks>
    public int RenameKey(BehaviorKeyContent key, string name) {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrWhiteSpace(name)
            || Content.Keys.Any(other => !ReferenceEquals(other, key)
                && string.Equals(other.Name, name, StringComparison.Ordinal))) {
            return -1;
        }

        var was = key.Name;
        var rewritten = 0;

        key.Name = name;

        foreach (var (fields, type) in EveryFieldBag()) {
            foreach (var field in type?.Fields ?? []) {
                if (field.Kind == BehaviorFieldKind.Key
                    && fields.TryGetValue(field.Name, out var value)
                    && string.Equals(value, was, StringComparison.Ordinal)) {
                    fields[field.Name] = name;
                    rewritten++;
                }
            }
        }

        Raise();

        return rewritten;
    }

    /// <summary>Changes what a key holds.</summary>
    /// <param name="key">The key.</param>
    /// <param name="type">What it should hold instead.</param>
    public void RetypeKey(BehaviorKeyContent key, BlackboardValueType type) {
        ArgumentNullException.ThrowIfNull(key);

        key.Type = type;
        Raise();
    }

    /// <summary>Deletes a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>How many references are now dangling, or <c>-1</c> if it was not a key of this tree.</returns>
    /// <remarks>
    ///     ⚠ <b>The references are left dangling on purpose, and counted.</b> Silently clearing them
    ///     would throw away which key forty decorators used to read, which is exactly what somebody
    ///     undoing a mistaken delete wants back; the compiler reports each one by name, so the
    ///     damage is visible rather than done.
    /// </remarks>
    public int RemoveKey(BehaviorKeyContent key) {
        if (!Content.Keys.Remove(key!)) {
            return -1;
        }

        var dangling = 0;

        foreach (var (fields, type) in EveryFieldBag()) {
            foreach (var field in type?.Fields ?? []) {
                if (field.Kind == BehaviorFieldKind.Key
                    && fields.TryGetValue(field.Name, out var value)
                    && string.Equals(value, key!.Name, StringComparison.Ordinal)) {
                    dangling++;
                }
            }
        }

        Raise();

        return dangling;
    }

    // ── The abort scope ──────────────────────────────────────────────────────

    /// <summary>What a decorator with an observer can interrupt.</summary>
    /// <param name="node">The node it is attached to.</param>
    /// <param name="attachment">The decorator.</param>
    /// <returns>Every node inside the region, or nothing when it observes nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>The payoff for taking Unity's scope rule over Unreal's.</b> An observer affects the
    ///     siblings under its own parent composite and no further, which means the region it can
    ///     interrupt is a subtree — and a subtree is a thing the canvas can shade. Unreal's abort
    ///     reaches further up the tree, which is more powerful and cannot be drawn, and is the
    ///     subject of most of the confusion in its forums. A rule you can draw is a rule an author
    ///     can predict.
    /// </remarks>
    public IReadOnlyList<BehaviorNodeContent> AbortScope(
        BehaviorNodeContent node,
        BehaviorAttachmentContent attachment
    ) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(attachment);

        if (TypeOf(attachment) is not { } type
            || BehaviorNodeSchema.Choice<ObserverAborts>(type, attachment.Fields, "Aborts") == ObserverAborts.None) {
            return [];
        }

        var scope = Parent(node) ?? node;
        var inside = new List<BehaviorNodeContent>();

        Collect(scope, inside);

        return inside;

        static void Collect(BehaviorNodeContent from, List<BehaviorNodeContent> into) {
            into.Add(from);

            foreach (var child in from.Children) {
                Collect(child, into);
            }
        }
    }

    /// <summary>A one-line summary of an attachment's settings, for the row on the node.</summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>The text, or empty.</returns>
    public string Summarise(BehaviorAttachmentContent attachment) {
        if (TypeOf(attachment) is not { } type) {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();

        foreach (var field in type.Fields) {
            var value = BehaviorNodeSchema.Read(type, attachment.Fields, field.Name);

            // The defaults are what most rows are made of, and a summary that repeated them would be
            // a wall of "Aborts None" nobody reads past.
            if (value.Length == 0
                || string.Equals(value, field.Default, StringComparison.Ordinal)
                || string.Equals(field.Name, "Aborts", StringComparison.Ordinal)) {
                continue;
            }

            if (text.Length > 0) {
                text.Append(", ");
            }

            text.Append(CultureInfo.InvariantCulture, $"{field.Label.ToLowerInvariant()} {value}");
        }

        // ⚠ And the nested rows, because a `Composite` condition whose summary said only "logic Or"
        // would be a box on the canvas that does not say what it is joining — which is the one thing
        // an author needs to read from it.
        if (attachment.Children.Count == 0) {
            return text.ToString();
        }

        if (text.Length > 0) {
            text.Append(": ");
        }

        text.Append(
            string.Join(", ", attachment.Children.Select(child => TypeOf(child)?.Label ?? child.Type))
        );

        return text.ToString();
    }

    void Raise() => Changed?.Invoke(this);

    /// <summary>Every field bag in the document, with the declaration that gives it meaning.</summary>
    IEnumerable<(Dictionary<string, string> Fields, BehaviorNodeType? Type)> EveryFieldBag() {
        foreach (var node in Walk()) {
            yield return (node.Fields, TypeOf(node));

            foreach (var decorator in node.Decorators) {
                yield return (decorator.Fields, TypeOf(decorator));

                // The nested operands of a `Composite` or a `ConditionalLoop` hold keys too, so a
                // rename that skipped them would rewrite half the references and leave the rest.
                foreach (var operand in decorator.Children) {
                    yield return (operand.Fields, TypeOf(operand));
                }
            }

            foreach (var service in node.Services) {
                yield return (service.Fields, TypeOf(service));
            }
        }
    }

    static void Fill(BehaviorNodeType type, Dictionary<string, string> fields) {
        foreach (var field in type.Fields) {
            if (field.Default.Length > 0) {
                fields[field.Name] = field.Default;
            }
        }
    }

    static BehaviorTreeContent Clone(BehaviorTreeContent content) {
        var copy = new BehaviorTreeContent {
            Version = content.Version,
            Name = content.Name,
            Root = content.Root is null ? null : Clone(content.Root)
        };

        foreach (var key in content.Keys) {
            copy.Keys.Add(new() { Name = key.Name, Type = key.Type });
        }

        return copy;
    }

    static BehaviorNodeContent Clone(BehaviorNodeContent node) {
        var copy = new BehaviorNodeContent {
            Name = node.Name,
            Type = node.Type,
            X = node.X,
            Y = node.Y,
            Fields = new(node.Fields, StringComparer.Ordinal)
        };

        foreach (var child in node.Children) {
            copy.Children.Add(Clone(child));
        }

        foreach (var decorator in node.Decorators) {
            copy.Decorators.Add(Clone(decorator));
        }

        foreach (var service in node.Services) {
            copy.Services.Add(Clone(service));
        }

        return copy;
    }

    static BehaviorAttachmentContent Clone(BehaviorAttachmentContent attachment) => new() {
        Type = attachment.Type,
        Interval = attachment.Interval,
        RandomDeviation = attachment.RandomDeviation,
        Fields = new(attachment.Fields, StringComparer.Ordinal),

        // ⚠ The nested rows too, or an undo of an edit inside a Composite condition would put back
        // the operands the snapshot before it happened to still be sharing.
        Children = [.. attachment.Children.Select(Clone)]
    };
}
