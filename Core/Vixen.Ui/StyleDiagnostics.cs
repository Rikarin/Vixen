// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>The one place a refused stylesheet rule stops being a list nobody reads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The messages were always there; the reader was not.</b>
///         <see cref="StyleSheetLoader" /> has answered an unrecognised at-rule with
///         "Vixen does not understand this rule" since the loader was written, and
///         <see cref="SelectorCompiler" /> has named every selector it could not compile. Both go
///         into a <c>List&lt;SelectorDiagnostic&gt;</c> behind a public property, and outside this
///         assembly's tests the only thing that had ever read either was
///         <c>HotReloadHost</c> — which compares the lists before and after a <i>save</i> and is
///         therefore blind to everything that was already wrong when the document was built. So any
///         CSS Vixen did not understand vanished without a word, and <c>@apply</c> was only the most
///         expensive instance of a class that includes every at-rule anyone will ever mistype.
///     </para>
///     <para>
///         ⚠ <b>The log, because the log is what a developer already has open.</b>
///         <c>Vixen.Core.Diagnostics</c>' <c>RingBufferSink</c> is on in every build; the editor's
///         Console panel reads it live, <c>LogOverlay</c> draws it in a running game, and the crash
///         reporter dumps it. A second channel — a diagnostics collection somebody has to remember to
///         query — would be the same list with a longer name. ADR-008's <c>[LoggerMessage]</c> shape
///         means a document whose host wired no logger pays a level test and no allocation.
///     </para>
///     <para>
///         ⚠ <b>Watermarked per producer <i>instance</i>, not per count.</b>
///         <see cref="StyleEngine.Reload" /> throws the loader and the compiler away and builds new
///         ones, so their lists restart at zero — a watermark held as a bare integer would then skip
///         every diagnostic a reload reproduced, and a hot reload that fixed one rule and broke
///         another would report nothing at all. Comparing the object identity makes "a new list"
///         and "a longer list" different events, which is what they are.
///     </para>
///     <para>
///         <b>One drain, and <c>LayoutStyleBuilder.Diagnostics</c> is the second producer it takes.</b>
///         That list is the same <c>IReadOnlyList&lt;SelectorDiagnostic&gt;</c> for the same reason —
///         see the remark on it — and was equally unread. It is drained by
///         <see cref="DrainBuilderDiagnostics" /> rather than by <see cref="DrainStyleDiagnostics" />,
///         because it is produced inside the per-element style pass rather than at load: its drain
///         point is the end of <see cref="Update" /> and not <see cref="Load" />. Nothing else had to
///         change — the builder already deduplicates by text, so a bad declaration matched by five
///         hundred elements is one entry and therefore one log line.
///     </para>
///     <para>
///         ⚠ <b>It matters more since grid landed than it would have before.</b> The bridge's other
///         refusals are mostly a keyword it does not know, which leaves a property at its initial
///         value; a track list is a grammar. A <c>grid-template-rows</c> the parser stops halfway
///         through is a one-row grid, which reads as a layout bug in a panel rather than as a
///         declaration the engine refused — and outside this assembly's tests, nothing could tell a
///         track list that was accepted from one that was not.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    /// <summary>Where a refusal goes. <c>NullLogger</c> when the host wired none.</summary>
    readonly ILogger logger;

    object? drainedLoader;
    int drainedLoaderCount;

    object? drainedCompiler;
    int drainedCompilerCount;

    object? drainedBuilder;
    int drainedBuilderCount;

    object? drainedText;
    int drainedTextCount;

    object? drainedDrawing;
    int drainedDrawingCount;

    /// <summary>What <see cref="ResolveText" /> could not read, and why.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A fourth producer, and it is here rather than on
    ///         <see cref="LayoutStyleBuilder" /> because this pass is not the bridge.</b>
    ///         <c>line-height</c>, <c>letter-spacing</c>, <c>word-spacing</c> and <c>text-indent</c>
    ///         are resolved by <see cref="ResolveText" /> against <i>this element's own</i> font size
    ///         and inherited as answers rather than as declarations, which is exactly why the bridge
    ///         cannot do it. Logging them as the bridge's would send a reader to a file that never
    ///         saw the value.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusals these record are invisible by construction, which is what makes the
    ///         list worth its bytes.</b> A <c>letter-spacing</c> in a unit that measures no distance
    ///         leaves the tracking inherited, and inherited-from-a-root is zero, and zero tracking
    ///         <i>is</i> <c>letter-spacing: normal</c> — the initial value. There is no frame in
    ///         which that declaration looks any different from never having written it. Same for
    ///         <c>text-indent</c>; <c>line-height</c> at least stacks the baselines, which reads as a
    ///         layout bug rather than as a stylesheet the engine refused. See `Rikarin/Vixen#521`.
    ///     </para>
    /// </remarks>
    readonly List<SelectorDiagnostic> textDiagnostics = [];

    /// <summary>The one reason three of the four text properties are refused for.</summary>
    /// <remarks>
    ///     ⚠ Shared text rather than three near-identical strings, because the failure really is one
    ///     failure: <c>LengthContext.ToLength</c> answers a length only for a unit that measures a
    ///     distance, and <c>letter-spacing: 2deg</c>, <c>word-spacing: 200ms</c> and
    ///     <c>text-indent: 3s</c> are the same mistake spelt three ways. The declaration is dropped,
    ///     as CSS drops it, and the property keeps what it inherited.
    /// </remarks>
    const string NotADistance =
        "this property takes a distance, and the declared unit measures none — so the declaration "
        + "is dropped and the inherited value stands";

    /// <summary>Records a text declaration that resolved to nothing, once per distinct declaration.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Deduplicated by text for <see cref="LayoutStyleBuilder" />'s reason and not a weaker
    ///         one: this runs once per element per restyle, so one bad declaration in a theme sheet is
    ///         a line per element per frame if nothing collapses it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>Rule</c>, and it is the same unavailability the bridge documents rather
    ///         than a lapse from `Rikarin/Vixen#520`.</b> A rule can be named only while the sheet is
    ///         being read; by the time a declaration is resolved against an element the cascade has
    ///         picked a winner per property and thrown the provenance away. <c>Text</c> — the
    ///         declaration as the author wrote it — is the locator this side of that line has.
    ///     </para>
    /// </remarks>
    /// <param name="property">The interned property name.</param>
    /// <param name="value">The interned value, as the author wrote it.</param>
    /// <param name="reason">Why it could not be used.</param>
    void RefuseText(int property, int value, string reason) {
        var text = $"{Styles.Properties.NameOf(property)}: {Styles.Values.NameOf(value)}";

        foreach (var existing in textDiagnostics) {
            if (existing.Text == text) {
                return;
            }
        }

        textDiagnostics.Add(new SelectorDiagnostic(text, reason));
    }

    /// <summary>Everything the document's five diagnostic producers are holding, as text.</summary>
    /// <returns>One entry per distinct refusal, in producer order.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Five, and a caller that reads two of them is a caller that reports a broken
    ///         document as a working one.</b> <c>HotReloadHost</c> built its before-and-after
    ///         snapshot out of <see cref="StyleSheetLoader" /> and <c>SelectorCompiler</c> alone, so
    ///         a saved sheet declaring <c>grid-template-rows: 4furlongs</c> or
    ///         <c>letter-spacing: 2deg</c> parsed, compiled, introduced nothing, and was reported as
    ///         a successful reload while the panel it styled laid out as one row. See #583.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two of the five are empty until a pass has run, which is the whole reason this
    ///         is not a one-line addition to that caller.</b> The loader's and the compiler's lists
    ///         are filled at load; the bridge's and the text resolver's are filled per element
    ///         during <see cref="Update" /> and the draw list's per frame during <see cref="Draw()" />.
    ///         A snapshot taken at the moment a sheet is replaced reads three empty lists whatever
    ///         the sheet says — so a caller judging a reload by this has to drive a frame between
    ///         the two readings, and <see cref="ForgetPassRefusals" /> is how it levels them.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> Refusals() => [
        .. Styles.Loader.Diagnostics.Select(diagnostic => diagnostic.ToString()),
        .. Styles.Compiler.Diagnostics.Select(diagnostic => diagnostic.ToString()),
        .. Builder.Diagnostics.Select(diagnostic => diagnostic.ToString()),
        .. textDiagnostics.Select(diagnostic => diagnostic.ToString()),
        .. drawings.Diagnostics.Select(diagnostic => diagnostic.ToString())
    ];

    /// <summary>
    ///     Forgets what the per-pass and per-frame producers have refused, and arranges for the next
    ///     pass to refuse it all again.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>For a caller that is about to run a pass and wants the refusals of <i>that</i>
    ///         pass.</b> <c>LayoutStyleBuilder.ClearDiagnostics</c> and
    ///         <c>DrawListBuilder.ClearDiagnostics</c> were written for exactly this and had no
    ///         caller at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Clearing the lists is half of it, and the half on its own is worse than
    ///         nothing.</b> <see cref="Invalidate" /> re-cascades but does not rebuild a layout
    ///         style whose interned <c>ComputedStyle</c> came back identical — which is the whole
    ///         point of that interning — so a caller that cleared and then invalidated would run a
    ///         full restyle, reach the bridge for nothing, and read an empty list as "this document
    ///         refuses nothing". <see cref="Forget()" /> is what makes the next pass genuinely
    ///         reproduce every refusal, and it is why this is one call rather than a caller's
    ///         recipe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The log watermarks are reset with the lists and that is not tidiness.</b>
    ///         <see cref="Drain" /> remembers how many entries of a producer it has already logged;
    ///         a list cleared behind its back leaves a watermark past the end, and the next several
    ///         refusals — the very ones the caller cleared in order to see — would be dropped
    ///         silently rather than logged.
    ///     </para>
    ///     <para>
    ///         The loader's and the compiler's lists are deliberately left alone: they are rebuilt
    ///         by the next load rather than by the next pass, and a caller comparing two loads needs
    ///         the baseline they carry.
    ///     </para>
    /// </remarks>
    public void ForgetPassRefusals() {
        Builder.ClearDiagnostics();
        textDiagnostics.Clear();
        drawings.ClearDiagnostics();

        drainedBuilderCount = 0;
        drainedTextCount = 0;
        drainedDrawingCount = 0;

        Forget();
    }

    /// <summary>Logs every refusal the cascade has recorded since the last time this ran.</summary>
    /// <remarks>
    ///     Called after anything that can add to either list: a <see cref="Load" />, a
    ///     <see cref="ReloadStyles" />, and the reload a resize can trigger. Not called per frame,
    ///     because neither producer runs per frame — the one that does is the builder, and it has
    ///     <see cref="DrainBuilderDiagnostics" /> of its own.
    /// </remarks>
    void DrainStyleDiagnostics() {
        Drain(
            "The stylesheet loader",
            Styles.Loader,
            Styles.Loader.Diagnostics,
            ref drainedLoader,
            ref drainedLoaderCount
        );

        Drain(
            "The selector compiler",
            Styles.Compiler,
            Styles.Compiler.Diagnostics,
            ref drainedCompiler,
            ref drainedCompilerCount
        );
    }

    /// <summary>Logs every declaration the layout bridge refused during the pass that just ran.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>After the pass rather than inside it, and once rather than per element.</b> The
    ///         builder is the one producer that runs per frame: it is called from <c>Apply</c> for
    ///         every element the updater restyled, so a drain woven into
    ///         that loop would test the list's length a thousand times a frame to log nothing. The
    ///         list deduplicates by text, so the whole of a pass's news is its tail and reading it
    ///         once at the end is the same news.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not in the <c>finally</c> that clears <see cref="updating" />.</b> A pass that
    ///         threw is a pass whose element walk did not finish, and the refusals it did record are
    ///         reported by the next one — the watermark is a position in a list that nothing truncates,
    ///         not a per-pass buffer. Draining from the <c>finally</c> would put a log write on the
    ///         way out of an exception, where the exception is the news.
    ///     </para>
    /// </remarks>
    void DrainBuilderDiagnostics() {
        Drain(
            "The layout bridge",
            Builder,
            Builder.Diagnostics,
            ref drainedBuilder,
            ref drainedBuilderCount
        );

        // The same drain point, because `ResolveText` runs in the same per-element pass the bridge
        // does — a second producer rather than a second place to read one.
        Drain(
            "The text resolver",
            textDiagnostics,
            textDiagnostics,
            ref drainedText,
            ref drainedTextCount
        );
    }

    /// <summary>Logs every declaration the draw list refused while building the frame just drawn.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A per-frame drain, and it costs one integer comparison per frame.</b>
    ///         <see cref="DrawListBuilder" /> is the only producer that runs in the draw pass rather
    ///         than the style pass, so it needs a drain point of its own and the only honest one is
    ///         after the build. What makes that affordable is that its list deduplicates by text: a
    ///         <c>box-shadow</c> refused on frame one is refused on every frame after it and adds
    ///         nothing, so from the second frame on this is <c>drained &lt; entries.Count</c> and
    ///         returns.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per surface rather than per <see cref="Draw()" />, because a host may draw one
    ///         window and skip another.</b> The watermark is on the builder, which is shared by every
    ///         surface, so a refusal is logged by whichever surface's build recorded it and not once
    ///         per window.
    ///     </para>
    /// </remarks>
    void DrainDrawingDiagnostics() =>
        Drain(
            "The draw list",
            drawings,
            drawings.Diagnostics,
            ref drainedDrawing,
            ref drainedDrawingCount
        );

    /// <summary>Logs the tail of one producer's list and remembers how far it got.</summary>
    /// <param name="source">What to call the producer in the message.</param>
    /// <param name="producer">The producer itself, which is the identity the watermark is keyed on.</param>
    /// <param name="entries">Its diagnostics.</param>
    /// <param name="drainedFrom">The producer the watermark belongs to.</param>
    /// <param name="drained">How many of its entries have been logged.</param>
    void Drain(
        string source,
        object producer,
        IReadOnlyList<SelectorDiagnostic> entries,
        ref object? drainedFrom,
        ref int drained
    ) {
        if (!ReferenceEquals(drainedFrom, producer)) {
            drainedFrom = producer;
            drained = 0;
        }

        for (; drained < entries.Count; drained++) {
            var diagnostic = entries[drained];

            // ⚠ The enclosing rule when there is one, because the fragment alone does not say which
            // rule to go and fix — see `SelectorDiagnostic`. Two events rather than one with an
            // empty slot, so that a refusal whose fragment *is* its rule does not read "in 'X'"
            // after having already said 'X'.
            if (diagnostic.NamesAnEnclosingRule) {
                StyleLog.RefusedIn(logger, source, diagnostic.Text, diagnostic.Where, diagnostic.Reason);
            } else {
                StyleLog.Refused(logger, source, diagnostic.Text, diagnostic.Reason);
            }
        }
    }
}
