// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>How far a <c>bind:</c> expression actually reaches, written from a real <c>.vxml</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#663 measures <c>bind:</c> at eight attributes in one file and lists what a target
///         must be; one of the three claims in that list is wrong and the two it says already work
///         were asserted nowhere.</b> A target must be an lvalue — <c>bind:Value="@(x*2)"</c> does
///         not compile, because the emitter writes <c>__v =&gt; EXPR = __v</c> — and it must be a
///         registered <c>[UiProperty]</c> of the <i>exact</i> type, which <c>TwoWayTypeTests</c>
///         holds.
///     </para>
///     <para>
///         ⚠ <b>"<c>string?</c> not <c>string</c>" is not a restriction at all, and listing it makes
///         the feature sound narrower than it is.</b> Nullable annotations on a reference type are
///         erased: <c>typeof(string?)</c> <i>is</i> <c>typeof(string)</c>, so the exact-type check
///         never sees the difference. <c>int?</c> against <c>int</c> genuinely is two types, and
///         that half is real. The last test below binds a non-nullable <c>string</c> model to
///         <c>TextField.Value</c>, which is declared <c>string?</c>.
///     </para>
///     <para>
///         ⚠ <b>And the real narrowness is one the issue does not name: a model that is not
///         reactive gets one forward write and never another.</b> Every <c>bind:</c> attribute in
///         the repository binds <c>Something.Value</c> on a <c>Signal&lt;T&gt;</c>, so this had
///         never been exercised. The forward leg is an <c>Effect</c> — it re-runs when a signal it
///         read changes, and a plain property is not one — while the write-back is a
///         <c>PropertyChanged</c> subscription and works either way. So a path or an indexer over an
///         ordinary POCO is a <i>half</i>-live binding, which is the shape of model an author
///         porting a hand-written panel already has.
///         <see cref="A_plain_model_gets_one_forward_write_and_the_control_stops_following_it" /> is
///         that stated rather than discovered.
///     </para>
///     <para>
///         ⚠ <b>Which is why every binding in this sheet now writes a warning to the document's
///         log.</b> <c>TwoWay</c> asks the expression whether it subscribed to anything and says so
///         when it did not — <c>Vixen.Ui.Tests.InertBindingTests</c> is that, and this fixture is
///         the shape it reports. Nothing here asserts on the log, deliberately: what these tests
///         are about is the <i>reach</i>, and a half-live binding still reaches.
///     </para>
/// </remarks>
public class BindReachTests {
    /// <summary>A two-hop path is an lvalue, so it binds.</summary>
    /// <remarks>
    ///     Both directions, because they are separate code paths: the forward leg is the effect and
    ///     the write-back is the subscription, and an assertion in one direction lets the other fail
    ///     in silence — which is the failure <c>TwoWayTypeTests</c> exists for.
    /// </remarks>
    [Fact]
    public void A_nested_property_path_binds_both_ways() {
        using var ui = Sheet(out var sheet);

        // Forward: the seeded model reached the control on the first flush.
        Assert.Equal(0.25f, sheet.Nested.Value);

        // Back: through `PropertyChanged`, synchronously with the assignment.
        sheet.Nested.Value = 0.75f;
        Assert.Equal(0.75f, sheet.Model.Levels.Gain);
    }

    /// <summary>And a settable indexer, which is the other lvalue nothing in the tree writes.</summary>
    [Fact]
    public void A_settable_indexer_binds_both_ways() {
        using var ui = Sheet(out var sheet);

        Assert.Equal(0.4f, sheet.Indexed.Value);

        sheet.Indexed.Value = 0.9f;
        Assert.Equal(0.9f, sheet.Model[0]);
    }

    /// <summary>
    ///     ⚠ A <c>string</c> model against a <c>string?</c> property is one type, not two.
    /// </summary>
    /// <remarks>
    ///     Composing at all is half the claim: a genuine mismatch throws out of <c>TwoWay</c> during
    ///     <c>Build</c>, so <see cref="Sheet" /> returning is already the statement that these two
    ///     agree.
    /// </remarks>
    [Fact]
    public void Reference_nullability_is_erased_so_it_is_not_a_type_mismatch() {
        using var ui = Sheet(out var sheet);

        Assert.Equal("Kick", sheet.Named.Value);

        sheet.Named.Value = "Snare";
        Assert.Equal("Snare", sheet.Model.Name);
    }

    /// <summary>
    ///     ⚠ <b>The forward leg follows a signal, not a property, and this is what that costs.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Written as a <c>Fact</c> rather than left as a known limitation because it is the
    ///         answer to the question #663 asks — why eight uses — and because it is the half that
    ///         cannot be seen from the markup. <c>bind:Value="@Model.Levels.Gain"</c> and
    ///         <c>bind:Value="@Model.Gain.Value"</c> are one character apart in the file and one of
    ///         them stops working the moment anything but the control writes the model.
    ///     </para>
    ///     <para>
    ///         ⚠ And it is <i>not</i> the silent-mismatch defect <c>TwoWayTypeTests</c> covers: this
    ///         binding is correctly typed, composes, and works in the direction an author usually
    ///         tests first. Nothing is thrown and nothing is logged, because nothing went wrong —
    ///         the effect simply has no dependency to be woken by.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_plain_model_gets_one_forward_write_and_the_control_stops_following_it() {
        using var ui = Sheet(out var sheet);

        sheet.Model.Levels.Gain = 0.9f;
        ui.Frame();

        Assert.Equal(0.25f, sheet.Nested.Value);
    }

    static UiTest Sheet(out BindReachSheet sheet) {
        var ui = ControlHarness.Open(400f, 300f);

        sheet = new();

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
