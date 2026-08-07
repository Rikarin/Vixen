// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using Vixen.Ui.Composition;

namespace Vixen.Ui.HotReload;

/// <summary>
///     The live components of one document, and the three ways an edit reaches them.
/// </summary>
/// <remarks>
///     <para>
///         <b>Styles</b> reload without rebuilding anything: the rule set is replaced and the
///         cascade runs again, so every element keeps its identity and therefore its focus, its
///         scroll offset and its animation state. This is the channel that is genuinely free, and it
///         is the one a designer uses all day.
///     </para>
///     <para>
///         <b>Markup</b> reloads by re-running <c>Build</c> on the same component objects. Their
///         fields survive because the objects do — which is most of what state preservation means —
///         and their elements do not, because two <c>Build</c> bodies are two different programs
///         with no shared identity beyond position. The focus is put back by path.
///     </para>
///     <para>
///         <b>A component</b> can also be replaced outright, for the edits .NET calls rude. Then the
///         new instance starts empty and <see cref="HotReloadStateAttribute" /> says what to carry.
///     </para>
///     <para>
///         ⚠ <b>What this does not do is deliver the new code.</b> A changed <c>.vxml</c> becomes a
///         different <c>Build</c> only after something has recompiled it, which is
///         <c>dotnet watch</c>'s job and the source generator's; until that generator exists, the
///         markup channel reloads whatever <c>Build</c> is currently in the assembly. That is not a
///         limitation of the reload — it is where the boundary between this and the build is.
///     </para>
/// </remarks>
public sealed class HotReloadHost {
    readonly List<Entry> entries = [];

    /// <summary>Creates a host over a document.</summary>
    /// <param name="document">The document.</param>
    public HotReloadHost(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>The document.</summary>
    public UiDocument Document { get; }

    /// <summary>The components being watched, in mount order.</summary>
    public IReadOnlyList<Component> Components => [.. entries.Select(entry => entry.Component)];

    /// <summary>Raised after every reload, successful or not.</summary>
    public event Action<ReloadReport>? Reloaded;

    /// <summary>Builds a component and keeps track of it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="parent">Where it hangs.</param>
    /// <returns>The component.</returns>
    public T Mount<T>(UiElement parent) where T : Component, new() {
        ArgumentNullException.ThrowIfNull(parent);

        var component = new T();
        var context = BuildContext.BuildInto(component, Document, parent);
        entries.Add(new(component, context, parent));
        return component;
    }

    // ================================================================== Styles

    /// <summary>Replaces a stylesheet.</summary>
    /// <param name="sheet">The index <see cref="UiDocument.Load" /> returned.</param>
    /// <param name="css">The new text.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A stylesheet that does not load puts the previous one back.</b> Half a stylesheet
    ///         is worse than the old one — a rule the author is midway through typing drops the
    ///         colour off everything it used to match — and the previous text is right there, so
    ///         restoring it costs one more load and turns a mangled screen into a message.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What rolls it back is a diagnostic this save <i>introduced</i>, not a diagnostic
    ///         the document happens to have.</b> A reload replays every sheet — that is what makes a
    ///         deleted rule stop applying — so the diagnostics afterwards are the whole document's,
    ///         and one unsupported selector anywhere in any sheet would roll back every save of every
    ///         other sheet for ever. Found in the editor, whose chrome at the time contained a
    ///         <c>:empty</c> the selector compiler did not yet implement: the style channel was
    ///         wired, the file was saved, the event arrived, and the reload silently undid itself
    ///         every time. The baseline is what the document was already complaining about, and only
    ///         what is new to it counts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That particular selector is supported now, and the rule is not one bit less
    ///         necessary.</b> The bug was never about <c>:empty</c> — it was about reading the
    ///         document's diagnostics as though they were this save's, and any selector the compiler
    ///         does not implement reproduces it exactly. Naming the one that happened to catch us is
    ///         history, not the reason.
    ///     </para>
    /// </remarks>
    public ReloadReport ReloadStyles(int sheet, string css) {
        ArgumentNullException.ThrowIfNull(css);

        var previous = Document.Styles.SheetText(sheet);
        var before = Diagnostics();

        Document.ReloadStyles(sheet, css);

        var errors = Introduced(before, Diagnostics());
        if (!errors.IsEmpty) {
            Document.ReloadStyles(sheet, previous);
        }

        return Report(new(ReloadChannel.Styles, 0, FocusRestored: true, errors));
    }

    /// <summary>What is in the second list that the first did not already account for.</summary>
    /// <remarks>
    ///     A multiset difference rather than a set one: two rules with the same mistake are two
    ///     diagnostics with the same text, and losing one of them to a set would let a save that
    ///     doubled a broken selector through unremarked.
    /// </remarks>
    static ImmutableArray<string> Introduced(ImmutableArray<string> before, ImmutableArray<string> after) {
        if (before.IsEmpty) {
            return after;
        }

        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var diagnostic in before) {
            remaining[diagnostic] = remaining.GetValueOrDefault(diagnostic) + 1;
        }

        var introduced = ImmutableArray.CreateBuilder<string>();

        foreach (var diagnostic in after) {
            if (remaining.TryGetValue(diagnostic, out var count) && count > 0) {
                remaining[diagnostic] = count - 1;
                continue;
            }

            introduced.Add(diagnostic);
        }

        return introduced.ToImmutable();
    }

    /// <summary>
    ///     Everything the last load could not use, from both places that report it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The loader and the selector compiler keep separate lists, and reading only the
    ///     loader's misses exactly the mistakes a person makes while typing a selector — which is
    ///     most of them. Found by a test that expected a rollback and did not get one.
    /// </remarks>
    ImmutableArray<string> Diagnostics() => [
        .. Document.Styles.Loader.Diagnostics.Select(diagnostic => diagnostic.ToString()),
        .. Document.Styles.Compiler.Diagnostics.Select(diagnostic => diagnostic.ToString())
    ];

    // ================================================================== Markup

    /// <summary>Re-runs <c>Build</c> for every tracked component.</summary>
    /// <returns>What happened.</returns>
    public ReloadReport ReloadComponents() {
        Prune();

        var focus = CaptureFocus();
        var errors = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in entries) {
            try {
                entry.Context.Rebuild(entry.Component);
            } catch (Exception exception) when (exception is not OutOfMemoryException) {
                // ⚠ Its subtree is already gone by the time Build throws — clear-then-build has no
                // snapshot to fall back to. Reported rather than swallowed, and the component is
                // left empty rather than half-built.
                errors.Add($"{entry.Component.GetType().Name}: {exception.Message}");
            }
        }

        return Report(
            new(ReloadChannel.Markup, entries.Count, RestoreFocus(focus), errors.ToImmutable())
        );
    }

    /// <summary>Replaces a component with a fresh instance, carrying its marked state over.</summary>
    /// <param name="component">The one to replace.</param>
    /// <param name="create">Makes the replacement.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentException">The component is not one of this host's.</exception>
    public ReloadReport Replace(Component component, Func<Component> create) {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(create);

        var index = entries.FindIndex(entry => ReferenceEquals(entry.Component, component));
        if (index < 0) {
            throw new ArgumentException("that component is not mounted here.", nameof(component));
        }

        var entry = entries[index];
        var focus = CaptureFocus();
        var carried = Capture(component);

        var replacement = create();
        Restore(replacement, carried);

        // The old host element goes with the old component; the new one is built in its place.
        var position = entry.Component.Root.IndexInParent;
        Document.Remove(entry.Component.Root);

        var context = BuildContext.BuildInto(replacement, Document, entry.Parent);
        Document.Move(replacement.Root, position);
        entries[index] = new(replacement, context, entry.Parent);

        return Report(new(ReloadChannel.Component, 1, RestoreFocus(focus), []));
    }

    // ================================================================== State

    /// <summary>Reads every <see cref="HotReloadStateAttribute" /> member of a component.</summary>
    internal static Dictionary<string, object?> Capture(Component component) {
        var captured = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var member in Members(component.GetType())) {
            switch (member) {
                case FieldInfo field:
                    captured[field.Name] = field.GetValue(component);
                    break;
                case PropertyInfo property when property.CanRead:
                    captured[property.Name] = property.GetValue(component);
                    break;
                default:
                    break;
            }
        }

        return captured;
    }

    internal static void Restore(Component component, Dictionary<string, object?> captured) {
        foreach (var member in Members(component.GetType())) {
            if (!captured.TryGetValue(member.Name, out var value)) {
                continue;
            }

            switch (member) {
                case FieldInfo field when Assignable(field.FieldType, value):
                    field.SetValue(component, value);
                    break;

                // A get-only property carrying state is the normal shape — `Signal<int> Count
                // { get; } = new(0)` — and there is a backing field behind it that can be written
                // even though the property cannot.
                case PropertyInfo property when property.SetMethod is not null
                                                && Assignable(property.PropertyType, value):
                    property.SetValue(component, value);
                    break;

                case PropertyInfo property when Backing(property) is { } backing
                                                && Assignable(backing.FieldType, value):
                    backing.SetValue(component, value);
                    break;

                default:
                    break;
            }
        }
    }

    static FieldInfo? Backing(PropertyInfo property) =>
        property.DeclaringType?.GetField(
            $"<{property.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

    /// <summary>
    ///     Whether a captured value still fits where it came from.
    /// </summary>
    /// <remarks>
    ///     ⚠ The point of a reload is that the type changed, so a member may have changed type with
    ///     it. Checking rather than assuming turns "your edit threw a cast exception somewhere in
    ///     the framework" into "that value did not come across".
    /// </remarks>
    static bool Assignable(Type target, object? value) =>
        value is null ? !target.IsValueType || Nullable.GetUnderlyingType(target) is not null
            : target.IsInstanceOfType(value);

    static IEnumerable<MemberInfo> Members(Type type) =>
        type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member.IsDefined(typeof(HotReloadStateAttribute), inherit: true));

    // ================================================================== Focus

    /// <summary>Where the focus is, as a path of child indices from a component's root.</summary>
    /// <remarks>
    ///     A path rather than the element, because the element will not exist afterwards. It is a
    ///     weak identity and it is the only one two different <c>Build</c> bodies share — which is
    ///     why the report says whether it worked instead of pretending it always does.
    /// </remarks>
    readonly record struct FocusPath(int Component, ImmutableArray<int> Path);

    FocusPath? CaptureFocus() {
        if (Document.Focused is not { } focused) {
            return null;
        }

        for (var i = 0; i < entries.Count; i++) {
            var path = PathTo(entries[i].Component.Root, focused);
            if (path is not null) {
                return new FocusPath(i, path.Value);
            }
        }

        return null;
    }

    static ImmutableArray<int>? PathTo(UiElement root, UiElement target) {
        var path = ImmutableArray.CreateBuilder<int>();

        for (var element = target; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, root)) {
                path.Reverse();
                return path.ToImmutable();
            }

            path.Add(element.IndexInParent);
        }

        return null;
    }

    bool RestoreFocus(FocusPath? captured) {
        if (captured is not { } path) {
            return true;
        }

        if (path.Component >= entries.Count) {
            return false;
        }

        var element = entries[path.Component].Component.Root;

        foreach (var index in path.Path) {
            if (index < 0 || index >= element.Children.Count) {
                return false;
            }

            element = element.Children[index];
        }

        return Document.Focus(element);
    }

    // ================================================================== Bookkeeping

    /// <summary>Forgets components whose elements someone else took out of the document.</summary>
    void Prune() => entries.RemoveAll(entry => entry.Component.Root.IsRemoved);

    ReloadReport Report(ReloadReport report) {
        Reloaded?.Invoke(report);
        return report;
    }

    readonly record struct Entry(Component Component, BuildContext Context, UiElement Parent);
}
