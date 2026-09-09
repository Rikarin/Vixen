// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a binding that throws leaves behind, which until now was nothing at all.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1109">#1109</a>, and its symptom was a
///         panel rather than a crash.</b> An <c>Effect</c> answers an unhandled exception by
///         suspending itself — it keeps its dependencies, keeps its place in the graph and never runs
///         again — which is the right answer for a UI framework and is also completely silent. The
///         assignment that threw had already written whatever it wrote, so the first frame is
///         correct and every frame after it is the same frame: a panel that renders once and then
///         freezes, indistinguishable from a model that stopped changing.
///     </para>
///     <para>
///         ⚠ <b>The two halves are separate defects and both are asserted here.</b> One is that
///         nothing counted the failure; the other is that the mechanism which was supposed to
///         <em>name</em> it had one caller passing the default —
///         <c>BuildContext.Bind</c> constructs every effect in the framework, so
///         <c>Effect.Origin</c>'s <c>[CallerFilePath]</c> resolved to <c>BuildContext.cs</c> and one
///         line number, for every binding in every panel. A count with no origin says an interface is
///         broken and not which line to open.
///     </para>
///     <para>
///         ⚠ <b>What this instrument says on the day nothing is wrong is asserted first.</b> A
///         counter that only ever appears when it fires is a counter nobody can tell apart from one
///         that is not running, which is the failure this repository keeps meeting; so
///         <see cref="A_document_whose_bindings_all_run_reports_none_broken" /> is the pair to every
///         other test in this file rather than a formality.
///     </para>
/// </remarks>
public class BrokenBindingTests {
    /// <summary>The instrument's zero, taken on a document whose bindings all work.</summary>
    /// <remarks>
    ///     Both members, because they fail differently: a count that started at one would be caught
    ///     by the first, and a <c>LastBrokenBinding</c> left over from another document's failure —
    ///     the shape a static field would have given — only by the second.
    /// </remarks>
    [Fact]
    public void A_document_whose_bindings_all_run_reports_none_broken() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Healthy>(document, document.Root);

        component.Name.Value = "second";
        document.Effects.Flush();

        Assert.Equal("second", component.Root.Children[0].Text);
        Assert.Equal(0, document.Diagnostics.BrokenBindings);
        Assert.Null(document.Diagnostics.LastBrokenBinding);
    }

    /// <summary>⚠ A row that binds its own <c>Text</c> renders once, freezes, and now says so.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The freeze is asserted before the diagnostic, because the diagnostic is only worth
    ///         anything if the freeze is real.</b> The element keeps the string it was given — the
    ///         <c>[UiProperty]</c> setter writes the backing field before the change hook throws — so
    ///         a test that only read the text would find the first value and call it correct.
    ///         Writing the signal again and flushing is what separates "following" from "frozen".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The element has a child, which is the whole of why it throws.</b> An element with
    ///         text measures itself and a node cannot both measure itself and have children. Every
    ///         row shape in a list is a container, so this is the natural spelling rather than an
    ///         exotic one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_binding_that_writes_text_to_a_container_freezes_the_row_and_is_counted() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Frozen>(document, document.Root);

        document.Effects.Flush();

        // ⚠ `Component.Root` is the component's own host element; the row is its child.
        var row = component.Root.Children[0];

        Assert.Equal("row", row.Tag);
        Assert.Equal("first", row.Text);
        Assert.Equal(1, component.Runs);

        component.Name.Value = "second";
        document.Effects.Flush();

        // Frozen: the effect is suspended, so the body did not run and the text is last frame's.
        Assert.Equal(1, component.Runs);
        Assert.Equal("first", row.Text);

        // ⚠ And this is the line that did not exist. One failure, counted once, on the document the
        // panel belongs to — a second flush does not re-run a suspended effect, so it does not
        // inflate the count either.
        Assert.Equal(1, document.Diagnostics.BrokenBindings);
    }

    /// <summary>⚠ The record names the file the binding was written in, not the framework's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the assertion the fix is really about.</b> <c>Effect</c> has carried a
    ///         <c>[CallerFilePath]</c> since it was written, with a remark saying it exists so that a
    ///         message "points at the effect rather than at this file" — and
    ///         <c>BuildContext.Bind</c>, the single call site that constructs effects for every panel
    ///         in the engine, let it default. So it pointed at <c>BuildContext.cs</c>, always.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The file name is asserted rather than merely "not BuildContext".</b> A record
    ///         that named some other framework file would satisfy a negative and be just as useless;
    ///         what a person needs is the file they can open, and the only file that can be is the
    ///         one the <c>Bind</c> call is written in — this one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_broken_binding_records_the_file_and_line_it_was_written_at() {
        using var document = new UiDocument(200f, 200f);

        BuildContext.Build<Frozen>(document, document.Root);
        document.Effects.Flush();

        var record = document.Diagnostics.LastBrokenBinding;

        Assert.NotNull(record);
        Assert.StartsWith("BrokenBindingTests.cs:", record, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildContext.cs", record, StringComparison.Ordinal);

        // The message the layout invariant would have thrown anonymously, carried out to a reader:
        // it names the tag, says the element has children, and says what to do instead.
        Assert.Contains("<row>", record, StringComparison.Ordinal);
        Assert.Contains("label", record, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The helpers that bind on the author's behalf name the author's file too.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The shape the first fix missed, and it is the commonest one in the tree.</b>
    ///         <c>BuildContext.Bind</c> was given a <c>[CallerFilePath]</c> — but
    ///         <c>Text(parent, () =&gt; …)</c>, <c>Use</c> and <c>Help</c> call it themselves, so
    ///         those three let it default and every binding made through them went on reporting
    ///         <c>BuildContext.cs</c>. ⚠ A markup interpolation compiles to exactly the first of
    ///         them — <c>ComponentEmitter</c> emits <c>ctx.Text(parent, () =&gt; expr)</c> under a
    ///         <c>#line</c> directive naming the <c>.vxml</c> — so the one binding shape an author is
    ///         most likely to break was the one shape the origin could not name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A case per helper and not one</b>, because forwarding is per-method: a fix that
    ///         threaded the arguments through <c>Text</c> alone would leave the other two exactly as
    ///         they were, and a single case would be green for it. <c>Help</c>'s case lives in
    ///         <c>Vixen.Ui.Controls.Tests</c> rather than here — a description needs an
    ///         implementation, and this assembly references only <c>Vixen.Ui</c>, so the call throws
    ///         before it ever reaches a binding.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("text")]
    [InlineData("use")]
    public void A_binding_made_through_a_helper_records_the_callers_file(string helper) {
        using var document = new UiDocument(200f, 200f);

        BuildContext.BuildInto(new Helped(helper), document, document.Root);
        document.Effects.Flush();

        var record = document.Diagnostics.LastBrokenBinding;

        Assert.NotNull(record);
        Assert.StartsWith("BrokenBindingTests.cs:", record, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildContext.cs", record, StringComparison.Ordinal);
    }

    /// <summary>⚠ Text on an element that has children is refused before the layout tree sees it.</summary>
    /// <remarks>
    ///     The refusal outside a binding was always there and was good; what it could not do is name
    ///     the element, because <c>LayoutTree</c> knows only that node 1 has children. A panel author
    ///     holding a stack trace has no way to turn a node index into a tag.
    /// </remarks>
    [Fact]
    public void Text_on_an_element_with_children_names_the_element_and_the_way_out() {
        using var document = new UiDocument(200f, 200f);
        var row = document.Root.Add("row");

        row.Add("slider");

        var thrown = Assert.Throws<InvalidOperationException>(() => row.Text = "1.0");

        Assert.Contains("<row>", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1 element children", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("label", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ And the other order — a child added to an element that already has text.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Half of this invariant was enforced and half was not, and the unenforced half was
    ///         the worse one.</b> <c>UiElement.Text</c>'s own remarks say "setting either on an
    ///         element that has the other throws"; only one of the two did. Adding a child to a
    ///         self-measuring node was accepted in silence, and <c>LayoutTree.LayoutNode</c> returns
    ///         as soon as it sees a measure function — so the child was never laid out at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An exception, not a wrong box, because the wrong box is invisible.</b> A child
    ///         that is never measured keeps whatever geometry it last had, which for a freshly
    ///         created element is none, and draws as nothing at the origin.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_child_added_to_an_element_that_has_text_is_refused_rather_than_never_laid_out() {
        using var document = new UiDocument(200f, 200f);
        var label = document.Root.Add("label");

        label.Text = "words";

        Assert.Throws<InvalidOperationException>(() => label.Add("box"));
    }

    /// <summary>A panel whose binding works, for the reading taken when nothing is wrong.</summary>
    sealed class Healthy : Component {
        public Signal<string> Name { get; } = new("first");

        protected override void Build(BuildContext ctx) {
            var name = ctx.Element(null, "name");

            ctx.Bind(() => name.Text = Name.Value);
        }
    }

    /// <summary>The natural spelling: a container that binds its own text, with a run counter.</summary>
    /// <remarks>
    ///     ⚠ The counter is what makes "frozen" measurable. Without it, an element holding the right
    ///     first value and the wrong second one is the same picture as an element whose model has
    ///     not moved.
    /// </remarks>
    /// <summary>A component that breaks one binding, made through whichever helper is named.</summary>
    /// <param name="helper">Which of the three to use.</param>
    /// <remarks>
    ///     ⚠ The failures are deliberately identical, so the only thing that differs between the
    ///     cases is which method built the effect.
    /// </remarks>
    sealed class Helped(string helper) : Component {
        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(null, "row");

            switch (helper) {
                case "text":
                    ctx.Text(row, Boom);

                    break;

                default:
                    ctx.Use(row, _ => Boom());

                    break;
            }
        }

        static object? Boom() => throw new InvalidOperationException("this binding is meant to throw.");
    }

    sealed class Frozen : Component {
        public Signal<string> Name { get; } = new("first");

        public int Runs { get; private set; }

        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(null, "row");

            ctx.Element(row, "slider");

            ctx.Bind(
                () => {
                    Runs++;
                    row.Text = Name.Value;
                }
            );
        }
    }
}
