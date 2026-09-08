// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>What a dump ends its lines with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A dump is compared against a raw string literal in a <c>.cs</c> file, and
///         <c>.gitattributes</c> pins <c>*.cs</c> to <c>eol=lf</c> on every checkout.</b> So a dump
///         whose terminator came from <see cref="System.Environment.NewLine" /> agrees with its
///         expectation on Linux and macOS and disagrees with it on <i>every line</i> on Windows.
///         That is not hypothetical: master's CI reported three <c>DisclosureMarkupTests</c> and one
///         <c>ComponentsViewDumpTests</c> red on the <c>test-windows-latest</c> leg with <c>\n</c>
///         expected and <c>\r\n</c> found, and green on the two other legs of the same run.
///     </para>
///     <para>
///         ⚠ <b>These assertions are vacuous on a platform whose newline is already <c>"\n"</c>, and
///         that is stated rather than hidden.</b> On Linux and macOS they cannot tell the fixed
///         producer from the broken one; the only way to watch them fail here is to sabotage
///         <c>UiTest.Newline</c> to <c>"\r\n"</c>, which is exactly the code Windows was running.
///         They are worth having because this is the one defect no Linux or macOS run can observe,
///         and a producer that goes back to spelling the machine's terminator is otherwise invisible
///         until a Windows leg re-reports it a batch later.
///     </para>
/// </remarks>
public class DumpLineEndingTests {
    static UiTest Fixture() {
        var ui = UiTest.Create(200f, 100f);

        ui.Load("""
            root { width: 200px; height: 100px; flex-direction: column; }
            .row { width: 200px; height: 20px; }
        """);

        var list = ui.Create("div", ui.Document.Root, "items", "row");

        // Hovered so that the flags dump has something to write: it prints nothing at all for an
        // element whose nine flags are all their default, and a dump of no lines would satisfy
        // "contains no carriage return" while proving nothing.
        ui.Create("div", list, null, "row").State = ElementState.Hover;
        ui.Create("div", list, null, "row").State = ElementState.Checked;

        ui.Frame();
        return ui;
    }

    [Fact]
    public void The_tree_dump_ends_every_line_with_a_bare_line_feed() {
        using var ui = Fixture();

        var tree = ui.Tree();

        Assert.DoesNotContain('\r', tree);
        Assert.Equal(4, tree.Split('\n').Length);
    }

    [Fact]
    public void The_flags_dump_ends_every_line_with_a_bare_line_feed() {
        using var ui = Fixture();

        var flags = ui.Flags(ui.Document.Root);

        Assert.DoesNotContain('\r', flags);
        Assert.Equal(2, flags.Split('\n').Length);
    }
}
