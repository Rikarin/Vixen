// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
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
    ///     The others are every at-rule anyone will ever mistype — <c>@suports</c>, <c>@meida</c> —
    ///     and all of them used to be dropped by <c>StyleSheetLoader.LoadUnknown</c> with a
    ///     well-worded diagnostic that reached nobody.
    ///     <para>
    ///         ⚠ <b>This list used to name <c>@container</c> and that was wrong, in the direction this
    ///         whole file exists to catch.</b> ExCSS 4.3.2 <i>does</i> know <c>@container</c> — it
    ///         hands back a <c>ContainerRule</c> with the name and condition already split out, not a
    ///         <c>RuleType.Unknown</c> — so it never went through <c>LoadUnknown</c>, never produced
    ///         the diagnostic this remark credited it with, and fell out of the loader's <c>switch</c>
    ///         through <c>default</c> in silence. It is implemented now
    ///         (<c>Vixen.Ui.Styling.Tests.ContainerQueryTests</c>); the point worth keeping is that a
    ///         remark naming a case no test covered was wrong for a release.
    ///     </para>
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

    /// <summary>A <c>::before</c> does not colour the element it was written against.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 43's F6, and the assertion is the colour rather than the diagnostic.</b>
    ///         <c>SelectorCompiler</c> used to intern <c>before</c> onto <c>Selector.PseudoElement</c>
    ///         and then compile the rest of the compound as though the <c>::before</c> were not
    ///         there. Nothing read the field, so <c>p::before { color: red }</c> was, observably and
    ///         only, <c>p { color: red }</c> — a rule that appeared to work and did something else.
    ///         A test asserting the compiler produced the right object is exactly the test that
    ///         passed all along; this one reads the paragraph.
    ///     </para>
    ///     <para>
    ///         <b>Both halves, because either alone is a state doc 43 refuses.</b> Silence with no
    ///         colour is a rule the author cannot debug; a colour with a warning is the bug plus a
    ///         message. The honest end state is neither: the declaration does not reach the element
    ///         and the log says why.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The green rule beside it is not decoration.</b> Refusing a selector happens after
    ///         parts of it may already have been written into the shared <c>SelectorTable</c>, and
    ///         every offset in that table is absolute. If a refusal renumbered anything, the rule
    ///         after it is what would break, so it is asserted here and not assumed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pseudo_element_rule_does_not_colour_the_element_it_was_written_against() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load("p::before { content: '>'; color: rgb(255, 0, 0) } p { color: rgb(0, 255, 0) }");

        var paragraph = document.Root.Add("p");
        document.Update();

        // The paragraph is green. Before F6 was closed it was red, and nothing said so. Channel
        // endpoints, because a colour is decoded to linear on the way in and 128 is not one there.
        Assert.Equal(new Color4(0f, 1f, 0f, 1f), document.ColorOf(paragraph.Style, document.PropertyId("color")));

        // `content` is not a Vixen property, and the refused rule is the only thing that set one.
        Assert.False(paragraph.Style.TryGet(document.PropertyId("content"), out _));

        var warning = Assert.Single(Warnings(sink));

        // 7006 rather than 7004: the fragment is `::before` and the rule is `p::before`, so the
        // refusal has an enclosing rule to name. See the two tests below.
        Assert.Equal(7006, warning.EventId.Id);
        Assert.Contains("The selector compiler", warning.Message, StringComparison.Ordinal);
        Assert.Contains("::before", warning.Message, StringComparison.Ordinal);
        Assert.Contains("no box without an element behind it", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>Two refusals of the same kind are told apart by the rule each names, and nothing
    ///     else.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the whole reason <c>SelectorDiagnostic</c> carries a rule.</b> A refusal
    ///         names the fragment the compiler stopped on, which for both rules below is the same
    ///         five characters — and the reason is the same sentence. Before the rule arrived, this
    ///         sheet produced two log lines that were <i>character-for-character identical</i>, on a
    ///         channel with no line numbers behind it, and a reader had no way to learn from either
    ///         one which rule to go and change. ExCSS does not carry source positions through to the
    ///         nodes the compiler walks, so the selector text is the only locator there is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted on the message, not on the diagnostic.</b> The compiler could build a
    ///         perfectly correct <c>SelectorDiagnostic</c> and the drain could go on logging four of
    ///         its five fields, which is the failure this file exists to catch in its other half. So
    ///         the subject here is <see cref="RingBufferSink" />'s record — what a person actually
    ///         reads in the Console panel — and the distinguishing assertion is that one message
    ///         names <c>.card</c> and the other does not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_refusals_differing_only_in_their_rule_each_name_the_rule_they_came_from() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(".card::before { color: red } .badge::before { color: blue }");

        var warnings = Warnings(sink);
        Assert.Equal(2, warnings.Count);

        var card = Assert.Single(warnings, record => record.Message.Contains(".card", StringComparison.Ordinal));
        var badge = Assert.Single(warnings, record => record.Message.Contains(".badge", StringComparison.Ordinal));

        // Each names its own rule and only its own — which is the fact that fails the moment the
        // rule stops being threaded and both lines collapse back into the same text.
        Assert.Contains(".card::before", card.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".badge", card.Message, StringComparison.Ordinal);
        Assert.Contains(".badge::before", badge.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".card", badge.Message, StringComparison.Ordinal);

        // And both still say what was refused and why: the rule is an addition, not a replacement.
        foreach (var warning in warnings) {
            Assert.Equal(7006, warning.EventId.Id);
            Assert.Contains("::before", warning.Message, StringComparison.Ordinal);
            Assert.Contains("no box without an element behind it", warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A refusal whose fragment already is the whole rule does not say so twice.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of the contract, and the reason the drain picks between two events
    ///         rather than always logging a <c>{Rule}</c>. <c>@nonsense pretend</c> is both the
    ///         fragment and the rule; a single message naming it once is the whole of what can be
    ///         said, and "refused 'X' in 'X'" on every at-rule in the language would be a worse
    ///         channel than the one before the rule was added.
    ///     </para>
    ///     <para>
    ///         ⚠ Written as an <c>EventId</c> assertion because that is the decision under test.
    ///         Asserting the message text would pass just as well with 7006 emitting a duplicated
    ///         name, which is the mistake this guards.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_refusal_whose_fragment_is_its_own_rule_stays_on_the_shorter_message() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load("@nonsense pretend { color: red }");

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("@nonsense", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(" in '", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A <c>:has()</c> argument is attributed to the rule it sits in, not to itself.</b>
    /// </summary>
    /// <remarks>
    ///     The compiler re-enters its own entry point to compile the argument of a <c>:is()</c> or a
    ///     <c>:has()</c>, and the naive threading — set the rule on every entry — would name
    ///     <c>:totally-invented</c> as the rule that contains <c>:totally-invented</c>. The argument
    ///     is not something a reader can go and find; the rule around it is.
    /// </remarks>
    [Fact]
    public void A_refusal_inside_a_nested_selector_names_the_outer_rule() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(".panel:has(:totally-invented) { color: red }");

        var warnings = Warnings(sink);

        Assert.NotEmpty(warnings);
        Assert.All(
            warnings,
            warning => Assert.Contains(".panel:has(:totally-invented)", warning.Message, StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ <b>A comma-separated selector is attributed per part, because the cascade splits it.</b>
    /// </summary>
    /// <remarks>
    ///     <c>a::before, b::before</c> is one rule to ExCSS and two to the cascade, and the one a
    ///     reader has to change is the part. Naming the whole list on both lines would put them back
    ///     to being indistinguishable, which is the defect this set of tests is about.
    /// </remarks>
    [Fact]
    public void Each_part_of_a_selector_list_is_attributed_to_itself() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(".card::before, .badge::before { color: red }");

        var warnings = Warnings(sink);
        Assert.Equal(2, warnings.Count);

        Assert.Single(warnings, record => !record.Message.Contains(".badge", StringComparison.Ordinal));
        Assert.Single(warnings, record => !record.Message.Contains(".card", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>The <i>loader</i> threads a rule too, and the four tests above only exercise the
    ///     compiler's.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SelectorDiagnostic.Rule</c> is supplied at twelve construction sites. Six are the
    ///         compiler's one <c>Refuse</c> helper, which the tests above cover between them; five
    ///         are at-rules whose fragment is their own rule, covered by
    ///         <see cref="A_refusal_whose_fragment_is_its_own_rule_stays_on_the_shorter_message" />.
    ///         The twelfth is <c>StyleSheetLoader.Collect</c>, which takes the rule as a
    ///         <i>parameter</i> from two callers, and neither leg had a test. Deleting either
    ///         argument left the whole suite green — which is the same "one site checked, a second
    ///         supplies the same value" trap that has made a sabotage prove nothing here before.
    ///     </para>
    ///     <para>
    ///         This is the ordinary-rule leg. A shorthand the expander cannot take apart is refused
    ///         with the declaration as its fragment, and <c>border: var(--x) solid</c> is the same
    ///         six words in every rule that carries it — so two rules with the same unexpandable
    ///         shorthand are the exact pair issue #520 is about, one level away from the selectors
    ///         above.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shorthand_the_loader_cannot_split_names_the_rule_it_was_written_in() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(".panel { border: var(--x) solid } .tray { border: var(--x) solid }");

        var warnings = Warnings(sink);
        Assert.Equal(2, warnings.Count);

        var panel = Assert.Single(warnings, record => record.Message.Contains(".panel", StringComparison.Ordinal));
        var tray = Assert.Single(warnings, record => record.Message.Contains(".tray", StringComparison.Ordinal));

        Assert.DoesNotContain(".tray", panel.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".panel", tray.Message, StringComparison.Ordinal);

        foreach (var warning in warnings) {
            Assert.Equal(7006, warning.EventId.Id);
            Assert.Contains("could not be taken apart", warning.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠ <b>Inside <c>@keyframes</c> the rule is the <i>offset</i>, not the animation.</b>
    /// </summary>
    /// <remarks>
    ///     The loader's second <c>Collect</c> caller, and the distinction is the whole reason it is
    ///     written the way it is: <c>@keyframes fade</c> may have six stops, and naming the animation
    ///     would leave a reader to find which of them carries the declaration. Two stops with the same
    ///     unexpandable shorthand is the arrangement that tells the two spellings apart — naming the
    ///     animation gives two identical messages, which is precisely the state before #520.
    /// </remarks>
    [Fact]
    public void A_shorthand_refused_inside_keyframes_names_the_offset_rather_than_the_animation() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(
            """
            @keyframes fade {
                0%   { border: var(--x) solid }
                100% { border: var(--x) solid }
            }
            """
        );

        var warnings = Warnings(sink);
        Assert.Equal(2, warnings.Count);

        Assert.Single(warnings, record => record.Message.Contains("{ 0% }", StringComparison.Ordinal));
        Assert.Single(warnings, record => record.Message.Contains("{ 100% }", StringComparison.Ordinal));

        foreach (var warning in warnings) {
            Assert.Equal(7006, warning.EventId.Id);
            Assert.Contains("@keyframes fade", warning.Message, StringComparison.Ordinal);
        }
    }

    // ── The two producers that are not the cascade ──────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A length in a unit that measures no distance is refused correctly and, until now,
    ///     invisibly.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>letter-spacing</c> is the worst of the five and the reason this is asserted on
    ///         the log rather than on a box.</b> The refusal leaves the tracking inherited, an
    ///         element under a root inherits zero, and zero tracking <i>is</i>
    ///         <c>letter-spacing: normal</c> — the initial value. There is no frame, no metric and no
    ///         computed style that differs between "this declaration was thrown away" and "there was
    ///         no declaration", so the log is the only place the difference can exist. Same for
    ///         <c>text-indent</c> and <c>word-spacing</c>; <c>line-height</c> at least stacks the
    ///         baselines, which reads as a layout bug rather than as a refusal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Drained at the end of <c>Update</c> and not at <c>Load</c>.</b> These are
    ///         produced per element in the style pass, so a sheet that is loaded and never updated
    ///         says nothing — which is correct: nothing has resolved the declaration yet.
    ///         `Rikarin/Vixen#521`.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("letter-spacing: 2deg")]
    [InlineData("word-spacing: 200ms")]
    [InlineData("text-indent: 3s")]
    [InlineData("line-height: 200ms")]
    public void A_text_length_in_a_unit_that_measures_nothing_reaches_the_log(string declaration) {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load($".card {{ {declaration} }}");
        document.Root.Add("div", classNames: "card");

        // Nothing yet: the value has not been resolved against an element, so nothing has refused it.
        Assert.Empty(Warnings(sink));

        document.Update();

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("The text resolver", warning.Message, StringComparison.Ordinal);
        Assert.Contains(declaration, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>A text property in a unit that <i>is</i> a distance says nothing at all.</summary>
    /// <remarks>
    ///     The other half, and without it the theory above would pass against a resolver that
    ///     reported every declaration it saw — which would fill the Console panel from any real
    ///     stylesheet and be worse than the silence it replaced.
    /// </remarks>
    [Theory]
    [InlineData("letter-spacing: 2px")]
    [InlineData("letter-spacing: normal")]
    [InlineData("word-spacing: 0.1em")]
    [InlineData("text-indent: 50%")]
    [InlineData("line-height: 1.5")]
    [InlineData("line-height: 150%")]
    public void A_text_length_this_engine_can_read_says_nothing(string declaration) {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load($".card {{ {declaration} }}");
        document.Root.Add("div", classNames: "card");
        document.Update();

        Assert.Empty(Warnings(sink));
    }

    /// <summary>
    ///     ⚠ <b>The draw list is a fourth producer with a drain point of its own, because it runs in
    ///     the draw pass.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>box-shadow: 90deg 2px #000000</c> is well-formed CSS in a unit that measures no
    ///         distance. Read through <c>LengthContext.PixelsPer</c> it was a shadow at no x-offset;
    ///         read through <c>ToLength</c> it is a refusal, which is right — and leaves a frame with
    ///         no shadow in it, which is what an element that never asked for one looks like.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is said until the frame is <i>drawn</i>.</b> A layout pass does not read
    ///         <c>box-shadow</c> at all, so <c>Update</c> alone is silent and the assertion below
    ///         checks that before it checks the warning. A drain in <c>Update</c> would have looked
    ///         like it worked in any test that happened to draw afterwards.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>The plain two-shadow list used to be a row here and is not one any more</b>: a list is
    ///     painted, a command each, since `Rikarin/Vixen#279`. What replaces it is the case that is
    ///     newly worth a warning — a list one of whose items cannot be read, where CSS refuses the
    ///     *whole* declaration and the perfectly good shadow beside it paints nothing either.
    /// </remarks>
    [Theory]
    [InlineData("box-shadow: 90deg 2px #000000")]
    [InlineData("box-shadow: 0px 4px 12px #000000, 0 0 0 calc(2px + 2px) #ff0000")]
    [InlineData("box-shadow: inset 0px 4px 12px #000000")]
    public void A_shadow_the_draw_list_cannot_paint_reaches_the_log(string declaration) {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load($"root {{ width: 200px; height: 200px }} .card {{ width: 50px; height: 20px; {declaration} }}");
        document.Root.Add("div", classNames: "card");
        document.Update();

        Assert.Empty(Warnings(sink));

        document.Draw();

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("The draw list", warning.Message, StringComparison.Ordinal);
        Assert.Contains("box-shadow", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A filter list holding one function this cannot execute drops the whole declaration, and
    ///     names it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>blur(200ms)</c> is the case that could not be seen at all.</b> A σ of zero
    ///     survives the finiteness test and composes by quadrature into no change, so the whole
    ///     <c>filter</c> was the identity — a declaration that parsed, cascaded, reached the draw
    ///     list and did nothing. The <c>invert(1)</c> beside it is what makes the refusal a refusal
    ///     of the <i>list</i>: a reader who saw the inversion applied would conclude the blur was
    ///     merely small.
    /// </remarks>
    [Theory]
    [InlineData("filter: blur(200ms)")]
    [InlineData("filter: blur(200ms) invert(1)")]
    [InlineData("filter: drop-shadow(90deg 2px #000000)")]
    [InlineData("backdrop-filter: blur(50%)")]
    public void A_filter_this_cannot_execute_reaches_the_log(string declaration) {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load($"root {{ width: 200px; height: 200px }} .card {{ width: 50px; height: 20px; {declaration} }}");
        document.Root.Add("div", classNames: "card");
        document.Update();
        document.Draw();

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7004, warning.EventId.Id);
        Assert.Contains("The draw list", warning.Message, StringComparison.Ordinal);
        Assert.Contains("none of it is applied", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A refused declaration is reported once, not once per frame.</b>
    /// </summary>
    /// <remarks>
    ///     The draw list is the only producer that runs every frame, so the watermark is doing work
    ///     here that it never has to do for the loader. Without the list's own deduplication by text
    ///     this would be a line per frame per element — a leak wearing a diagnostic's clothes, and
    ///     the reason a per-frame drain point was recorded as a design change rather than made
    ///     quietly.
    /// </remarks>
    [Fact]
    public void A_shadow_refused_every_frame_is_reported_on_the_first_one_only() {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load(
            "root { width: 200px; height: 200px } .card { width: 50px; height: 20px; box-shadow: 90deg 2px #000 }"
        );

        document.Root.Add("div", classNames: "card");
        document.Root.Add("div", classNames: "card");
        document.Update();

        for (var frame = 0; frame < 8; frame++) {
            document.Draw();
        }

        Assert.Single(Warnings(sink));
    }

    /// <summary>A shadow and a filter this <i>can</i> draw say nothing.</summary>
    [Theory]
    [InlineData("box-shadow: 0px 4px 12px #000000")]
    [InlineData("box-shadow: none")]
    [InlineData("filter: blur(4px)")]
    [InlineData("filter: blur(0)")]
    [InlineData("filter: invert(1) grayscale(0.5)")]
    public void A_shadow_or_filter_the_draw_list_can_paint_says_nothing(string declaration) {
        var (document, sink) = Watched();
        using var owned = document;

        document.Load($"root {{ width: 200px; height: 200px }} .card {{ width: 50px; height: 20px; {declaration} }}");
        document.Root.Add("div", classNames: "card");
        document.Update();
        document.Draw();

        Assert.Empty(Warnings(sink));
    }
}
