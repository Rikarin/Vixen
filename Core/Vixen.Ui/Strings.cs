// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Ui;

/// <summary>A string an interface shows, named by an id and carrying the source text it says.</summary>
/// <param name="Id">What a catalog calls it: <c>editor.command.file.save</c>.</param>
/// <param name="Source">What it says as it was written here, and what it says when nothing translates it.</param>
/// <remarks>
///     <para>
///         <b>The pair is the whole idea, and it is why localisation is not a retrofit.</b>
///         <c>item.Label = EditorStrings.Save.Text</c> is no more work at the call site than
///         <c>item.Label = "Save"</c>, so there is never a reason to write the literal — which is
///         the failure Stride's <c>Stride.Core.Translation</c> exists to repair, and repairing it
///         means finding every literal in an editor after the fact.
///     </para>
///     <para>
///         ⚠ <b>The source text lives at the declaration rather than in an <c>en</c> catalog.</b> An
///         application whose fallback is a file is one that shows <c>editor.command.file.save</c>
///         to anybody whose install is missing it, and the missing file is exactly what a
///         localisation bug looks like. Here the worst case is English.
///     </para>
///     <para>
///         What a <c>Strings.Resource</c> generator would add — doc 11 asks for one — is emitting
///         these declarations from a catalog rather than the other way round, so that an id used
///         nowhere and an id declared nowhere are both build errors. The shape it would emit is
///         a static class of these, member for member, plus an <c>All</c> list; nothing at a call
///         site changes when it lands.
///     </para>
/// </remarks>
public readonly record struct StringId(string Id, string Source) {
    /// <summary>What it says in the current language.</summary>
    public string Text => Strings.Get(this);

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Which language an application is showing, and what each string says in it.</summary>
/// <remarks>
///     <para>
///         <b>Static, which is the one place in this assembly that is.</b> Every other service is an
///         instance a shell owns, because a document may have two of them; a language is a property
///         of the person using the application rather than of a window, and threading a localiser
///         through every control that shows a word is the design that makes people write the literal
///         instead.
///     </para>
///     <para>
///         ⚠ <b>The catalog in use is a <see cref="Signal{T}" />, and that is what makes a language
///         change re-label a running interface.</b> Every <c>@expr</c> in a <c>.vxml</c> is a
///         region-scoped <c>Effect</c>, so an expression that reads a string is a consumer of this
///         signal without saying so: <see cref="Use" /> marks it dirty, the document's next flush
///         re-runs it, and the label changes. No application writes a line of code for that, nothing
///         subscribes, and nothing is asked to restart.
///     </para>
///     <para>
///         ⚠ <b>A label assigned once in C# is not an expression.</b> A control whose constructor
///         writes <c>Button.Label = ControlStrings.Close.Text</c> reads the signal outside any
///         effect, so it shows whatever language was in use when it was built — which is the
///         standard control set's behaviour today. <see cref="Changed" /> is what such a surface
///         subscribes to in order to rebuild itself, and it is a plain event because a rebuild is a
///         side effect rather than a value.
///     </para>
/// </remarks>
public static class Strings {
    static readonly SortedSet<string> MissingIds = new(StringComparer.Ordinal);

    /// <summary>The catalog in use, for the whole process.</summary>
    /// <remarks>
    ///     A field and not a signal, because a field is what every thread can read safely. What makes
    ///     a language change re-label a running interface is <see cref="announced" />.
    /// </remarks>
    static volatile StringCatalog inUse = StringCatalog.Source;

    /// <summary>
    ///     ⚠ <b>A <see cref="Signal{T}" /> per thread, and it was one for the process until
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1413">#1413</a>.</b> The language is a
    ///     property of the person and stays process-wide in <see cref="inUse" />; this is only the
    ///     graph node that <em>announces</em> a change, and a graph node belongs to one graph.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One static node was the one thing two independent graphs still shared.</b>
    ///         <c>ReactiveGraph.OwningThread</c> is off by default so that a test host — or an editor
    ///         with more than one graph — can run single-threaded graphs on several threads, and
    ///         #1030 made the epoch safe for that. But every <c>@expr</c> that shows a word is an
    ///         effect over this node, so every UI test class, each on its own xunit thread, added and
    ///         removed live consumers on one node's arrays at the same time — and a removal writes the
    ///         moved twin's index into the <em>consumer's</em> producer array, which is another
    ///         thread's graph. Eight threads making and dropping one effect each over a shared node
    ///         threw <c>IndexOutOfRangeException</c> in 159 825 of 160 000 iterations; one node per
    ///         thread threw none. Short of throwing, a torn edge array is how a live computed in a
    ///         private graph misses the push that should dirty it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So a <see cref="Use" /> re-labels what its own thread's graph shows</b>, which in
    ///         the editor and in <c>UiApplication</c> is the only graph there is — both set
    ///         <c>OwningThread</c>, and a read of any signal off that thread throws anyway. A graph on
    ///         another thread reads the new language the next time it evaluates and is not told to.
    ///     </para>
    ///     <para>
    ///         The comparer is the type's own, which for a class is reference equality — so
    ///         <c>Use(null)</c> twice writes <see cref="StringCatalog.Source" /> twice and propagates
    ///         once. A catalog mutated in place after it is in use is invisible here, deliberately:
    ///         <see cref="Use" /> is the seam, and a translation that changes under a running frame is
    ///         not a case worth a comparer that reports nothing equal.
    ///     </para>
    /// </remarks>
    [ThreadStatic] static Signal<StringCatalog>? announced;

    /// <summary>This thread's node, made the first time this thread asks.</summary>
    static Signal<StringCatalog> Announced => announced ??= new(inUse);

    /// <summary>The catalog in use.</summary>
    /// <remarks>
    ///     Reading this inside an effect or a computed records a dependency on the language, which
    ///     is what re-labels a bound expression when <see cref="Use" /> is called.
    /// </remarks>
    public static StringCatalog Catalog => Read();

    /// <summary>The process's catalog, read through this thread's node so the read is a dependency.</summary>
    /// <remarks>
    ///     ⚠ <b>The node's value is not what is returned.</b> A thread whose node has not been told
    ///     about a <see cref="Use" /> made on another one still says the language the process is in.
    /// </remarks>
    static StringCatalog Read() {
        _ = Announced.Value;

        return inUse;
    }

    /// <summary>Raised after <see cref="Use" /> changes it.</summary>
    /// <remarks>
    ///     For the surfaces that are not expressions — a menu bar built in C# and rebuilt whole.
    ///     ⚠ It is static, so a subscriber outlives the document it was built for unless it
    ///     unsubscribes; <c>MenuPresenter</c> is <c>IDisposable</c> for exactly that.
    /// </remarks>
    public static event Action<StringCatalog>? Changed;

    /// <summary>
    ///     Ids asked for that the current catalog does not have, in id order.
    /// </summary>
    /// <remarks>
    ///     The list a translator works from, and the thing a test asserts is empty for a language
    ///     that claims to be complete. It is gathered rather than reported because an application
    ///     that logged a warning per missing string would log one per menu rebuild.
    /// </remarks>
    public static IReadOnlyCollection<string> Missing => MissingIds;

    /// <summary>Shows the interface in a language.</summary>
    /// <param name="catalog">The catalog, or <c>null</c> for the source strings.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="Changed" /> is raised after the write rather than from inside it.</b> A
    ///     handler that rebuilds a menu reads strings, and a read while a write is still notifying
    ///     its dependents is refused by the graph — half of it is marked dirty and half is not, so
    ///     what it would read is arbitrary.
    /// </remarks>
    public static void Use(StringCatalog? catalog) {
        var chosen = catalog ?? StringCatalog.Source;
        var previous = inUse;
        var node = Announced;

        inUse = chosen;

        if (!ReferenceEquals(node.Peek(), chosen)) {
            node.Value = chosen;
        } else if (!ReferenceEquals(previous, chosen)) {
            // ⚠ This thread's node already holds it — a `Use` elsewhere moved the process away and
            // this one moves it back — so the write would compare equal and tell nobody, while
            // everything this thread drew in between says the other language.
            node.Invalidate();
        }

        MissingIds.Clear();

        Changed?.Invoke(chosen);
    }

    /// <summary>What a string says in the current language.</summary>
    /// <param name="id">The string.</param>
    /// <returns>The translation, or its source text if there is none.</returns>
    public static string Get(StringId id) {
        if (id.Id is null) {
            return id.Source ?? string.Empty;
        }

        // ⚠ Through `Read`, which is what records the dependency. Reading `inUse` directly here
        // would be one line shorter and would silently unbind every expression in the tree.
        var catalog = Read();

        if (catalog.Find(id.Id) is { } translated) {
            return translated;
        }

        // ⚠ Only recorded against a catalog that is not the source one. Against the source catalog
        // every id is "missing" by construction, and a list of every string in the application is
        // not a list of anything.
        if (!ReferenceEquals(catalog, StringCatalog.Source)) {
            MissingIds.Add(id.Id);
        }

        return id.Source;
    }

    /// <summary>A catalog holding every string a set of declarations names, for a translator to start from.</summary>
    /// <param name="language">What to call the new catalog.</param>
    /// <param name="ids">The declarations to export — an <c>All</c> list, or several concatenated.</param>
    /// <returns>The catalog, filled with the source text.</returns>
    /// <remarks>
    ///     <para>
    ///         The template a <c>Strings.Resource</c> generator would emit at build time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It takes the declarations rather than finding them.</b> Nothing here can walk
    ///         "every <see cref="StringId" /> in the process": they are static properties on
    ///         whichever classes an application chose to declare, and a list gathered by reflecting
    ///         over those at run time is a list the application's trimming settings are entitled to
    ///         shorten. Passing the <c>All</c> lists in is what makes the answer a fact about the
    ///         source rather than about the trimmer.
    ///     </para>
    /// </remarks>
    public static StringCatalog Template(string language, IEnumerable<StringId> ids) {
        ArgumentNullException.ThrowIfNull(ids);

        var catalog = new StringCatalog(language);

        foreach (var id in ids) {
            catalog.Set(id.Id, id.Source);
        }

        return catalog;
    }
}
