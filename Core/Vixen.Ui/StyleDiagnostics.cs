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
///         <b>One drain, and <c>LayoutStyleBuilder.Diagnostics</c> is the second producer it is
///         shaped for.</b> That list is the same <c>IReadOnlyList&lt;SelectorDiagnostic&gt;</c> for the
///         same reason — see the remark on it — and is equally unread; wiring it is issue #56 and is
///         deliberately not done here, because it is produced inside the per-element style pass
///         rather than at load and so needs a drain point in <see cref="UiDocument.Update()" />
///         rather than in <see cref="UiDocument.Load" />. The line that closes it is one call:
///         <c>Drain("The layout bridge", Builder, Builder.Diagnostics, ref drainedBuilder, ref
///         drainedBuilderCount)</c> at the end of the pass. Nothing else has to change — the builder
///         already deduplicates by text, so a bad declaration matched by five hundred elements is one
///         entry and therefore one log line.
///     </para>
/// </remarks>
public sealed partial class UiDocument {
    /// <summary>Where a refusal goes. <c>NullLogger</c> when the host wired none.</summary>
    readonly ILogger logger;

    object? drainedLoader;
    int drainedLoaderCount;

    object? drainedCompiler;
    int drainedCompilerCount;

    /// <summary>Logs every refusal the cascade has recorded since the last time this ran.</summary>
    /// <remarks>
    ///     Called after anything that can add to either list: a <see cref="Load" />, a
    ///     <see cref="ReloadStyles" />, and the reload a resize can trigger. Not called per frame,
    ///     because neither producer runs per frame — the one that does is the builder, and that is
    ///     #56.
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
            StyleLog.Refused(logger, source, diagnostic.Text, diagnostic.Reason);
        }
    }
}
