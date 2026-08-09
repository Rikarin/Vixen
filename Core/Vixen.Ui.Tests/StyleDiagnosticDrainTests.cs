// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a stylesheet Vixen could not read says so somewhere a person will find it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The sabotage these are written against is deleting the drain, not breaking the
///         producer.</b> Every message asserted below already existed and was already correct —
///         <c>StyleSheetLoader</c> has said "Vixen does not understand this rule" since it was
///         written. What did not exist was a reader: <c>StyleSheetLoader.Diagnostics</c> is public
///         and, outside this repository's own tests, nothing had ever looked at it. Remove the
///         <c>DrainStyleDiagnostics</c> calls from <c>UiDocument</c> and every fact here goes red
///         while the whole rest of the suite stays green, which is exactly the shape of the gap.
///     </para>
///     <para>
///         <b>Asserted against a real <see cref="RingBufferSink" /> and not a stub.</b> That ring is
///         what the editor's Console panel reads live, what <c>LogOverlay</c> draws in a running
///         game, and what the crash reporter dumps — so "a developer will see it" is a claim about
///         this object, and a test that captured through a bespoke logger would have proved
///         something weaker while looking the same.
///     </para>
/// </remarks>
public class StyleDiagnosticDrainTests {
    /// <summary>A document logging into a ring, and the ring.</summary>
    static (UiDocument Document, RingBufferSink Log) Watched() {
        var sink = new RingBufferSink(64);
        return (new UiDocument(200f, 200f, logger: sink.CreateLogger("Vixen.Ui.Styling")), sink);
    }

    static IReadOnlyList<LogRecord> Warnings(RingBufferSink sink) =>
        [.. sink.Snapshot().Where(record => record.Level >= LogLevel.Warning)];

    /// <summary>The sabotage test: an at-rule nothing understands must not pass in silence.</summary>
    /// <remarks>
    ///     ⚠ <b><c>@apply</c> is one member of this class and the cheapest one to reason about.</b>
    ///     The others are every at-rule anyone will ever mistype — <c>@suports</c>, <c>@meida</c>,
    ///     a <c>@container</c> query Vixen has not implemented — and all of them used to be dropped
    ///     by <c>StyleSheetLoader.LoadUnknown</c> with a well-worded diagnostic that reached nobody.
    /// </remarks>
    [Fact]
    public void An_at_rule_vixen_does_not_understand_reaches_the_log() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load("@nonsense pretend { color: red }");

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("Vixen does not understand this rule", warning.Message, StringComparison.Ordinal);

        // And it names what was written, because "something was dropped" is not a diagnosis.
        Assert.Contains("@nonsense", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>A rule the loader keeps produces nothing, so the channel stays worth reading.</summary>
    /// <remarks>
    ///     The other half of the sabotage. A drain that logged on every load would pass the test
    ///     above with the diagnostics list never consulted at all, and would also make the editor's
    ///     console useless within a minute of opening it.
    /// </remarks>
    [Fact]
    public void A_stylesheet_vixen_understands_says_nothing() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load("@layer base { .card { color: red } } @media (min-width: 1px) { .card { color: blue } }");

        Assert.Empty(Warnings(sink));
    }

    /// <summary>A selector the compiler refused is drained through the same call.</summary>
    /// <remarks>
    ///     Two producers, one drain — <c>SelectorCompiler</c>'s list had the same problem and the
    ///     same absence of a reader, and <c>LayoutStyleBuilder</c>'s (issue #56) is the third of the
    ///     shape. The source is named in the message so that three lists arriving on one channel stay
    ///     tellable apart.
    /// </remarks>
    [Fact]
    public void A_selector_vixen_cannot_compile_reaches_the_log_too() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(":totally-invented { color: red }");

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("The selector compiler", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>The same refusal is reported once, not once per load of some other sheet.</summary>
    /// <remarks>
    ///     ⚠ <b>The watermark, checked.</b> Both lists accumulate for their producer's whole life, so
    ///     a drain that replayed them from the start would report the first sheet's problem again for
    ///     every sheet loaded afterwards — and a shell installs four.
    /// </remarks>
    [Fact]
    public void A_refusal_is_reported_once_however_many_sheets_follow_it() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load("@nonsense pretend { color: red }");
        document.Load(".a { color: red }");
        document.Load(".b { color: blue }");

        Assert.Single(Warnings(sink));
    }

    /// <summary>A reload starts the lists over, and the drain notices.</summary>
    /// <remarks>
    ///     ⚠ <b>Why the watermark is keyed on the producer and not on a count.</b>
    ///     <c>StyleEngine.Reload</c> throws the loader away and builds a new one, so its list
    ///     restarts at zero. A bare integer watermark would then be past the end of a fresh list and
    ///     every refusal a hot reload reproduced — or newly introduced, which is the case that
    ///     matters — would be skipped for the life of the document.
    /// </remarks>
    [Fact]
    public void A_hot_reload_that_introduces_a_bad_rule_reports_it() {
        var (document, sink) = Watched();
        using var owned = document;

        var sheet = document.Load(".card { color: red }");
        Assert.Empty(Warnings(sink));

        document.ReloadStyles(sheet, "@nonsense pretend { color: red }");

        var warning = Assert.Single(Warnings(sink));
        Assert.Contains("Vixen does not understand this rule", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>A document given no logger still loads, and still drops the rule.</summary>
    /// <remarks>
    ///     The behaviour has to be identical either way: the drain reports, it does not decide. Kept
    ///     because a logger that was accidentally required would break every existing caller, and
    ///     they are the majority.
    /// </remarks>
    [Fact]
    public void A_document_with_no_logger_behaves_exactly_as_before() {
        using var document = new UiDocument(200f, 200f);

        document.Load("@nonsense pretend { color: red } .card { color: red }");

        var diagnostic = Assert.Single(document.Styles.Loader.Diagnostics);
        Assert.Equal("Vixen does not understand this rule", diagnostic.Reason);

        // The good rule in the same sheet still applies, which is the recovery contract the whole
        // "report rather than throw" design rests on.
        var element = document.Root.Add("div", classNames: "card");
        document.Update();

        Assert.True(element.Style.TryGet(document.PropertyId("color"), out _));
    }

    /// <summary>An <c>@apply</c> that could not be expanded is reported on the same channel.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure that install-time expansion would otherwise have added to the pile.</b>
    ///     <c>ApplyExpander</c> records every utility it could not place and then clears the list on
    ///     its next call, so an expander reused across a document's sheets keeps only the last
    ///     sheet's refusals — which is a shorter road to silence than the one this file exists to
    ///     close. Drained per sheet, in <c>UiDocument.ExpandApply</c>.
    /// </remarks>
    [Fact]
    public void An_apply_that_names_no_utility_reaches_the_log() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(".card { @apply p-4 notautilityatall; }", StyleOrigin.UserAgent);

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7005, warning.EventId.Id);
        Assert.Contains("notautilityatall", warning.Message, StringComparison.Ordinal);

        // And the utility beside it still landed: one bad name is not a failed rule.
        var element = document.Root.Add("div", classNames: "card");
        document.Update();

        Assert.Equal(16f, document.LengthOf(element.Style, document.PropertyId("padding-left")));
    }
}
