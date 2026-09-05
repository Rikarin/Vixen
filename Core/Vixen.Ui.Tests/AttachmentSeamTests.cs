// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What <c>help=</c> and <c>context-menu=</c> say when nothing has filled their seam.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This assembly is the state under test rather than a stand-in for it.</b>
///         <c>Vixen.Ui.Tests</c> references <c>Vixen.Ui</c> and nothing above it, so
///         <c>ControlMarkup.Register</c> has not run here and cannot — which is exactly the shape a
///         real panel reaches when its markup names no capitalised tag: the project references the
///         control library, nothing in the assembly has been touched, and the module initializer
///         that fills these two delegates has therefore never run.
///     </para>
///     <para>
///         ⚠ <b>The old message named only the other cause.</b> It said the control library
///         "registers one when it is loaded", which reads as a claim about the project file — and
///         the project file is usually right. What is asserted here is that the message now names
///         the load-order half and what ends it, because the difference between "you did not
///         reference the controls" and "nothing has touched them yet" is the difference between an
///         hour and a line.
///     </para>
///     <para>
///         The bodies are written by hand because that is what a compiled <c>.vxml</c> is: the
///         markup compiler emits <c>ctx.Help</c> and <c>BuildContext.Menu</c> calls, and this
///         assembly has no markup compiler loaded. Nothing about the failure depends on which of
///         the two wrote the call.
///     </para>
/// </remarks>
public class AttachmentSeamTests {
    /// <summary>A <c>help</c> with no registration says the library may simply be untouched.</summary>
    [Fact]
    public void A_help_with_nothing_registered_blames_the_load_order_and_not_only_the_reference() {
        using var document = new UiDocument(200f, 200f);

        var error = Assert.Throws<InvalidOperationException>(
            () => BuildContext.Build<Helpful>(document, document.Root)
        );

        // The directive, so the reader knows which attribute in their markup is being talked about.
        Assert.Contains("'help'", error.Message, StringComparison.Ordinal);

        // ⚠ The mechanism, which is the half that was missing: a reference is not a touch.
        Assert.Contains("module initializer", error.Message, StringComparison.Ordinal);
        Assert.Contains("first touched", error.Message, StringComparison.Ordinal);

        // And the line that ends it, named rather than described.
        Assert.Contains("ControlTheme.Install", error.Message, StringComparison.Ordinal);

        // The other cause is still there — it just is no longer the only one offered.
        Assert.Contains("references only Vixen.Ui", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And <c>context-menu</c> says the same thing, because it is the same seam.</summary>
    /// <remarks>
    ///     ⚠ <b>Both, and not one as a sample.</b> The two directives landed together on
    ///     <c>BuildContext</c>'s registration seam and each wrote its own sentence; a message
    ///     corrected on one of them is the arrangement that produced the disagreement in the first
    ///     place.
    /// </remarks>
    [Fact]
    public void A_context_menu_with_nothing_registered_says_the_same_thing() {
        using var document = new UiDocument(200f, 200f);
        var target = document.Root.Add<UiElement>();
        var menu = document.Root.Add<UiElement>();

        var error = Assert.Throws<InvalidOperationException>(() => BuildContext.Menu(target, menu));

        Assert.Contains("'context-menu'", error.Message, StringComparison.Ordinal);
        Assert.Contains("module initializer", error.Message, StringComparison.Ordinal);
        Assert.Contains("ControlTheme.Install", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>And <c>on:</c> on the same markup does not fail</b>, which is why the assumption
    ///     written on <c>ControlMarkup.Register</c> held for it and not for these two: the event
    ///     names a plain tag can use are <c>Vixen.Ui</c>'s own, and the control library's entries
    ///     sharpen them rather than supplying them.
    /// </summary>
    [Fact]
    public void An_on_click_on_a_plain_tag_still_binds_with_the_control_library_untouched() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Tappable>(document, document.Root);

        Assert.Equal(["div"], component.Root.Children.Select(child => child.Tag));
    }

    sealed class Helpful : Component {
        protected override void Build(BuildContext ctx) {
            var div = ctx.Element(null, "div");
            ctx.Help(div, "Writes the scene to disk");
        }
    }

    sealed class Tappable : Component {
        protected override void Build(BuildContext ctx) {
            var div = ctx.Element(null, "div");
            ctx.On(div, "click", () => { });
        }
    }
}
