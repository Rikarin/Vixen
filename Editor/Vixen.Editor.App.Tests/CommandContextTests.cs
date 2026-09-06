// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What the editor's command context is today, one test per context.</summary>
/// <remarks>
///     <para>
///         <b>The regression net doc 45's step 2 asks for, landed before the step it protects.</b>
///         Changing how <c>CommandRegistry.FocusedContext</c> resolves cannot throw — its failure
///         mode is a chord quietly meaning something else — so the only way to see the change is to
///         write down what every context does first and watch this file go red.
///     </para>
///     <para>
///         ⚠ <b>It is also the measurement doc 45 § G2's amendment rests on.</b>
///         <see cref="Two_panels_in_seven_leave_a_focus_behind_for_a_route_to_read" /> presses in
///         each panel that claims a context and records where the focus ended up. Five of the seven
///         leave none at all, because the panel is not focusable and the press landed on nothing that
///         is — so a scope derived from <see cref="UiDocument.Focused" /> has nothing to read in five
///         of seven cases, and in none of the four <i>mode</i> contexts, which no press claims.
///     </para>
///     <para>
///         ⚠ <b>Five and not six, and what moved was the layout rather than the rule.</b> Nothing in
///         the editor became focusable: the CSS-initial <c>flex-shrink</c> fix (#628) let the
///         lighting panel's scroll viewport shrink, its middle rose by twenty-two pixels, and the
///         press that used to land on the bottom edge of an inspector row now lands on the slider
///         inside it. A count over "what is under the middle of each panel" is a measurement of a
///         layout, so it moves when a layout does — and the thing it is evidence for does not.
///     </para>
/// </remarks>
public class CommandContextTests {
    /// <summary>Every panel id the editor claims a context from, and the context it claims.</summary>
    /// <remarks>
    ///     The four mode contexts — <c>blockout</c>, <c>terrain</c>, <c>water</c>, <c>foliage</c> —
    ///     are deliberately absent: they belong to modules this assembly does not load, and they are
    ///     claimed by <c>Shell.Modes.Changed</c> rather than by a press in a panel at all. Their own
    ///     modules' tests pin them.
    /// </remarks>
    static readonly (string Panel, string Context)[] PanelContexts = [
        ("hierarchy", "scene"),
        ("scenes", "scene"),
        ("project", "project"),
        ("console", "console"),
        ("world-settings", "world"),
        ("lighting", "world"),
        ("navigation", "world")
    ];

    /// <summary>The same table, as the theory's cases.</summary>
    public static TheoryData<string, string> Panels {
        get {
            var data = new TheoryData<string, string>();

            foreach (var (panel, context) in PanelContexts) {
                data.Add(panel, context);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Panels))]
    public void Pressing_in_a_panel_claims_its_context(string panel, string context) {
        using var fixture = EditorSession.Start();

        fixture.Open(panel);
        fixture.Click(fixture.Panel(panel));

        Assert.Equal(context, fixture.Shell.Context);

        // ⚠ Through the delegate rather than the property, because the delegate is what step 2
        // rewires. When it stops answering the same thing, this line is the one that goes red.
        Assert.Equal(context, fixture.Shell.Commands.FocusedContext?.Invoke());
    }

    [Fact]
    public void Leaving_a_panel_for_another_hands_the_context_over() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");
        fixture.Click(fixture.Panel("hierarchy"));
        Assert.Equal("scene", fixture.Shell.Context);

        fixture.Open("project");
        fixture.Click(fixture.Panel("project"));
        Assert.Equal("project", fixture.Shell.Context);

        fixture.Click(fixture.Panel("hierarchy"));
        Assert.Equal("scene", fixture.Shell.Context);
    }

    [Fact]
    public void Every_context_a_command_declares_is_in_scope_exactly_when_the_shell_says_so() {
        using var fixture = EditorSession.Start();

        var commands = fixture.Shell.Commands;

        var declared = commands.Commands
            .Select(command => command.Context)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ⚠ Read off the registry rather than listed here, so a context added by a later module
        // joins this test without anybody remembering to — and asserted non-empty, because a loop
        // over nothing is a test that passes on the day the registry stops answering.
        Assert.NotEmpty(declared);

        foreach (var context in declared) {
            var scoped = commands.Commands
                .Where(command => string.Equals(command.Context, context, StringComparison.Ordinal))
                .ToList();

            fixture.Shell.Context = context;
            Assert.All(scoped, command => Assert.True(commands.IsInScope(command), $"{command.Id} in {context}"));

            fixture.Shell.Context = "no-such-context";
            Assert.All(scoped, command => Assert.False(commands.IsInScope(command), $"{command.Id} outside {context}"));
        }
    }

    [Fact]
    public void A_command_with_no_context_is_in_scope_wherever_the_user_is() {
        using var fixture = EditorSession.Start();

        var commands = fixture.Shell.Commands;
        var global = commands.Commands.First(command => command.Context is null);

        foreach (var (panel, _) in PanelContexts) {
            fixture.Open(panel);
            fixture.Click(fixture.Panel(panel));

            Assert.True(commands.IsInScope(global));
        }
    }

    [Fact]
    public void Two_panels_in_seven_leave_a_focus_behind_for_a_route_to_read() {
        using var fixture = EditorSession.Start();

        List<string> withAFocus = [];

        foreach (var (panel, context) in PanelContexts) {
            fixture.Open(panel);
            fixture.Click(fixture.Panel(panel));

            // The push is right, every time.
            Assert.Equal(context, fixture.Shell.Context);

            if (fixture.Document.Focused is { } focused) {
                Assert.True(Inside(focused, fixture.Panel(panel)), $"{panel}: the focus is somewhere else");
                withAFocus.Add(panel);
            }
        }

        // ⚠ **The number doc 45 § G2's amendment is built on.** `hierarchy` holds a `TreeView`, whose
        // rows are focusable, so a press there leaves the focus inside the panel that claimed the
        // context — a route *could* derive `scene` from that one. Five of the seven panels are not
        // focusable and neither is anything the press landed on, so `UiDocument.Focused` is null and
        // there is nothing to walk. Five of seven, plus four mode contexts that no press claims.
        //
        // ⚠ **`lighting` is the second one and it was `["hierarchy"]` alone until 2026-09-05**, when
        // the CSS-initial `flex-shrink` fix (#628) let its scroll viewport shrink to its content:
        // the viewport's middle moved from y≈445 to y≈423, which is inside the 415–433 of the
        // slider in the inspector row it lands on rather than below it. Nothing here became
        // focusable and no press means anything new — this test presses the *middle* of each panel,
        // so it reads a layout as well as a rule, and this is the layout half moving.
        //
        // It leaves the amendment's argument where it was and sharpens it: the focus a press leaves
        // behind is a control that happens to be under the pointer, and the scope a route could
        // derive from it is whatever panel it is *in*. `lighting` and `world-settings` and
        // `navigation` all declare `world`, so which of them holds the slider decides nothing — and
        // that is the point. If this goes red again, reread it rather than repair it: check whether
        // an editor panel has become focusable, or whether a layout has moved a control under the
        // middle of another panel.
        Assert.Equal(["hierarchy", "lighting"], withAFocus);
    }

    static bool Inside(UiElement element, UiElement ancestor) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }
}
