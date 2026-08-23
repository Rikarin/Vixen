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

        return (T) Track(new T(), parent, static () => new T());
    }

    /// <summary>Builds a component whose type is only known at run time, and keeps track of it.</summary>
    /// <param name="type">The component type, which needs a public parameterless constructor.</param>
    /// <param name="parent">Where it hangs.</param>
    /// <returns>The component.</returns>
    /// <exception cref="ArgumentException">
    ///     The type is not a <see cref="Component" />, or cannot be constructed without arguments.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>For a caller that cannot name the type, which is not the same as not knowing it.</b>
    ///     A component compiled from a <c>.vxml</c> is <c>internal</c> to the assembly the markup
    ///     lives in, so the application that owns the reload host frequently cannot write
    ///     <c>Mount&lt;T&gt;</c> for a panel in a library it references — and the library, which can,
    ///     is the one that must not reference a development-only assembly to say so. This is the seam
    ///     between the two: the library asks for its own type and the application supplies the
    ///     tracking. See <c>EditorShell.RemountTaskCenter</c>, which is the case it was added for.
    /// </remarks>
    public Component Mount(Type type, UiElement parent) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(parent);

        if (!typeof(Component).IsAssignableFrom(type)) {
            throw new ArgumentException($"'{type}' is not a {nameof(Component)}.", nameof(type));
        }

        if (Activator.CreateInstance(type) is not Component component) {
            throw new ArgumentException($"'{type}' cannot be created without arguments.", nameof(type));
        }

        return Track(component, parent, () => (Component) Activator.CreateInstance(type)!);
    }

    /// <summary>Tracks a component somebody else constructed, and how to construct another.</summary>
    /// <param name="create">
    ///     Makes this component. Called now to nothing — the instance is supplied — and again by a
    ///     reload that has to replace it.
    /// </param>
    /// <param name="parent">Where it hangs.</param>
    /// <returns>The component.</returns>
    /// <remarks>
    ///     ⚠ <b>The overload a host in front of an application framework needs, and the reason it
    ///     takes a factory rather than an instance alone.</b> <c>UiApplicationOptions.Content</c> is
    ///     already a <c>Func&lt;Component&gt;</c> — an application writes
    ///     <c>() =&gt; new Shell { Model = model }</c> — so the factory that built the first instance
    ///     is exactly the one that should build its replacement. Handed only the instance, a recreate
    ///     would fall back to the parameterless constructor and drop `Model` on the floor, and the
    ///     application would have to notice and re-apply it from <c>Reloaded</c>. This is what makes
    ///     that unnecessary.
    /// </remarks>
    public Component Mount(Func<Component> create, UiElement parent) {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(parent);

        return Track(create(), parent, create);
    }

    Component Track(Component component, UiElement parent, Func<Component>? create) {
        var context = BuildContext.BuildInto(component, Document, parent);
        entries.Add(new(component, context, parent, create));
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
    /// <remarks>
    ///     Rebuild only. A component whose <c>Build</c> throws is left empty and reported — see the
    ///     overload for the one case where throwing away the instance is the right answer.
    /// </remarks>
    public ReloadReport ReloadComponents() => ReloadComponents(null);

    /// <summary>
    ///     Re-runs <c>Build</c> for every tracked component, replacing the instances the runtime has
    ///     just moved out from under.
    /// </summary>
    /// <param name="updated">
    ///     The types the runtime says it changed, as <c>MetadataUpdateHandler</c> was given them, or
    ///     <see langword="null" /> when it does not know — in which case nothing is replaced.
    /// </param>
    /// <returns>What happened, including how many instances had to be replaced.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Rebuild first, always.</b> Re-running <c>Build</c> on the same object is what keeps
    ///         a panel's signals, and it is the answer for almost every edit: a changed method body
    ///         does not change what an instance holds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this adds is the case where rebuilding <i>cannot</i> work, and it is a real
    ///         one rather than a hypothetical.</b> The runtime can add an instance field to a live
    ///         type, and the field initialisers that would have filled it do not run on an object
    ///         that already exists — so a component holding a <c>Signal&lt;T&gt;</c> field the edit
    ///         introduced holds <see langword="null" /> there, and the new <c>Build</c> dereferences
    ///         it. Rebuilding again next time fails the same way for ever. A fresh instance runs its
    ///         own initialisers, and <see cref="HotReloadStateAttribute" /> is how anything worth
    ///         keeping crosses over. This is what that attribute has always been for and it is where
    ///         <see cref="Replace" /> is reached from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only for a type the runtime named, and only after a throw.</b> Both halves guard
    ///         the same thing: state preservation is the whole point of this channel, and replacing
    ///         an instance discards everything not marked. A <c>Build</c> that throws for any other
    ///         reason — a null the developer just introduced, a bad index — is a component whose
    ///         type nobody edited, and it keeps its fields and its error. A <c>.vxml</c> with a typo
    ///         in it does not compile, so no update arrives at all; a <c>Build</c> that throws
    ///         <i>after</i> a successful compile of its own type is exactly the stale-instance shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a replacement that also throws is reported as the original failure.</b> The
    ///         first exception is the one that describes the edit; "and then the new instance did it
    ///         too" is the same news twice.
    ///     </para>
    /// </remarks>
    public ReloadReport ReloadComponents(IReadOnlyCollection<Type>? updated) {
        Prune();

        var focus = CaptureFocus();
        var errors = ImmutableArray.CreateBuilder<string>();
        var replaced = 0;

        // ⚠ By index rather than by enumerator, because a replacement writes `entries[i]` and the
        // list is walked while that happens.
        for (var i = 0; i < entries.Count; i++) {
            var component = entries[i].Component;

            if (Rebuild(i) is not { } failure) {
                continue;
            }

            // A type the runtime did not name is a component nobody edited, so its instance is not
            // the thing that is wrong with it.
            if (updated is null || !updated.Contains(component.GetType())) {
                errors.Add(failure);
                continue;
            }

            if (Recreate(i)) {
                replaced++;
            } else {
                errors.Add(failure);
            }
        }

        return Report(
            new(ReloadChannel.Markup, entries.Count, RestoreFocus(focus), errors.ToImmutable()) {
                Replaced = replaced
            }
        );
    }

    /// <summary>Re-runs one entry's <c>Build</c>.</summary>
    /// <returns>What went wrong, or null.</returns>
    string? Rebuild(int index) {
        var entry = entries[index];

        try {
            entry.Context.Rebuild(entry.Component);
            return null;
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            // ⚠ Its subtree is already gone by the time Build throws — clear-then-build has no
            // snapshot to fall back to. Reported rather than swallowed, and the component is
            // left empty rather than half-built.
            return $"{entry.Component.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    ///     Swaps an entry's component for a fresh instance of the same type, carrying its marked
    ///     state.
    /// </summary>
    /// <returns>Whether a fresh instance was made and built.</returns>
    /// <remarks>
    ///     ⚠ A type with no public parameterless constructor cannot be recreated from here *unless the
    ///     caller supplied a factory* — see <see cref="Mount(Func{Component}, UiElement)" />, which is
    ///     how an application hands over the one that built the first instance. Without it this has
    ///     nothing to supply the new one's arguments from. Answered <see langword="false" /> so the original failure is what gets reported,
    ///     rather than a second exception about reflection that says nothing about the edit. The same
    ///     goes for a replacement whose own <c>Build</c> throws: the first exception is the one that
    ///     describes the edit.
    /// </remarks>
    bool Recreate(int index) {
        var entry = entries[index];
        var type = entry.Component.GetType();

        try {
            // ⚠ The factory the caller supplied, when there is one, and the type only when there is
            // not. A component with constructor arguments or with parameters its caller assigned has
            // no other way back — see `Entry.Create`, and `HotReloadHost.Mount(Func<Component>, …)`,
            // which is the overload that carries it.
            if ((entry.Create is { } create ? create() : Activator.CreateInstance(type)) is not Component replacement) {
                return false;
            }

            ReplaceAt(index, replacement);
            return true;
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            return false;
        }
    }

    /// <summary>Replaces a component with a fresh instance, carrying its marked state over.</summary>
    /// <param name="component">The one to replace.</param>
    /// <param name="create">Makes the replacement.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="ArgumentException">The component is not one of this host's.</exception>
    /// <remarks>
    ///     ⚠ <b>Reached from <see cref="ReloadComponents(IReadOnlyCollection{Type})" /> as well as
    ///     by hand.</b> The runtime hands the metadata-update handler the types it changed, and an
    ///     instance of one of them that can no longer run its own <c>Build</c> is exactly what this
    ///     is for — see that overload for why rebuilding cannot cover that case. The explicit form
    ///     stays because the factory is the half a caller may need: a component that is not
    ///     default-constructible has no other way through.
    /// </remarks>
    public ReloadReport Replace(Component component, Func<Component> create) {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(create);

        var index = entries.FindIndex(entry => ReferenceEquals(entry.Component, component));
        if (index < 0) {
            throw new ArgumentException("that component is not mounted here.", nameof(component));
        }

        var focus = CaptureFocus();

        ReplaceAt(index, create());

        return Report(new(ReloadChannel.Component, 1, RestoreFocus(focus), []) { Replaced = 1 });
    }

    /// <summary>Swaps one entry's component for an already-created replacement, and builds it.</summary>
    /// <remarks>
    ///     The state is carried before the build rather than after it, because a <c>Build</c> reads
    ///     the signals it is about to bind to and a value arriving afterwards would be a value the
    ///     first frame did not have.
    /// </remarks>
    void ReplaceAt(int index, Component replacement) {
        var entry = entries[index];

        Restore(replacement, Capture(entry.Component));

        // The old host element goes with the old component; the new one is built in its place.
        var position = entry.Component.Root.IndexInParent;
        Document.Remove(entry.Component.Root);

        var context = BuildContext.BuildInto(replacement, Document, entry.Parent);
        Document.Move(replacement.Root, position);
        // ⚠ The factory carries over. A component replaced once can be replaced again, and the
        // second replacement has to know what the first one did — otherwise the parameters survive
        // exactly one incompatible edit.
        entries[index] = new(replacement, context, entry.Parent, entry.Create);
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

    /// <param name="Component">The tracked component.</param>
    /// <param name="Context">The build region it owns, which is what a rebuild disposes and remakes.</param>
    /// <param name="Parent">Where it hangs.</param>
    /// <param name="Create">
    ///     How to make a replacement, or <see langword="null" /> to construct one from the type.
    ///     ⚠ <b>This is what lets a component with constructor arguments — or with parameters its
    ///     caller assigned — survive being re-created.</b> Without it <see cref="Recreate" /> can only
    ///     call <c>Activator.CreateInstance</c>, so the new instance comes up with every parameter at
    ///     its default and the panel is bound to a model nothing else holds. Nothing reports that: the
    ///     reload succeeds and the interface is simply wrong.
    /// </param>
    readonly record struct Entry(Component Component, BuildContext Context, UiElement Parent, Func<Component>? Create);
}
