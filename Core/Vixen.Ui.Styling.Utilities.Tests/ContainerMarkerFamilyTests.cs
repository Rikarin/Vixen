// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>The <c>@container</c> marker family: the class that makes a box a query container.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The variants that ask the question landed a batch before the class that makes a box
///         answerable, and until this every query container in the tree was declared in hand-written
///         CSS.</b> #273 recorded why: a pure variant emits no new property and the consumption gate
///         never sees it, where a marker family emits <c>container-type</c> and faces the gate — and
///         <c>container-type</c> moves none of the four channels unless the scene holds a query
///         reacting to it. The <c>queried</c> probe scene is that scene, and this file is the mapping
///         the ledger cannot see: it measures that <em>something</em> reads the property and never
///         which value each class emits.
///     </para>
///     <para>
///         ⚠ <b>And the scanner was the other half of the blocker.</b> <c>@container</c> has no colon,
///         so the rule that keeps <c>@apply</c> and <c>@media</c> out of the candidate set kept the
///         class out with them; <c>CandidateScanner</c> admits exactly this name now, and
///         <c>ThemeAndScannerTests</c> holds that it admits no other.
///     </para>
/// </remarks>
public class ContainerMarkerFamilyTests {
    [Theory]
    [InlineData("@container", "inline-size")]
    [InlineData("@container-normal", "normal")]
    [InlineData("@container-size", "size")]
    [InlineData("@container-[inline-size]", "inline-size")]
    public void Each_marker_class_computes_to_its_own_container_type(string utility, string keyword) =>
        Assert.Equal(keyword, new UtilityFixture().Computed([utility], "container-type"));

    /// <summary>A name after the slash is the container's name, beside the type the head chose.</summary>
    /// <remarks>
    ///     ⚠ <b>Both declarations, and the type is not defaulted by the name.</b> v4's
    ///     <c>@container/main</c> is <c>inline-size</c> named <c>main</c>; a family that emitted the
    ///     name alone would register a name nothing can ask for, because <c>Containers.KindOf</c>
    ///     makes a name with no type a non-container — the exact reason the probe scene's host is
    ///     typed before it is named.
    /// </remarks>
    [Theory]
    [InlineData("@container/main", "inline-size", "main")]
    [InlineData("@container-size/sidebar", "size", "sidebar")]
    public void A_slash_names_the_container(string utility, string type, string name) {
        var fixture = new UtilityFixture();

        Assert.Equal(type, fixture.Computed([utility], "container-type"));
        Assert.Equal(name, fixture.Computed([utility], "container-name"));
    }

    /// <summary>What is not a class: a keyword CSS has no such value for, and a name that is not an identifier.</summary>
    /// <remarks>
    ///     The second is the one that matters. <c>container-name: 24rem</c> is a declaration ExCSS
    ///     drops whole, so a family that passed the suffix through would emit a rule whose type
    ///     landed and whose name did not — a container that answers the unnamed query and not the
    ///     one the author wrote it for.
    /// </remarks>
    [Theory]
    [InlineData("@container-nonsense")]
    [InlineData("@container/24rem")]
    [InlineData("@container/--main")]
    [InlineData("@container/")]
    [InlineData("@container-[inline-size]/24rem")]
    public void A_value_the_family_does_not_have_computes_to_nothing(string utility) {
        var fixture = new UtilityFixture();

        Assert.Null(fixture.Computed([utility], "container-type"));
        Assert.Null(fixture.Computed([utility], "container-name"));
    }

    /// <summary>The escape hatch carries a name, rather than resolving and dropping it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The arbitrary branch runs before the one that reads the slash, so until this row
    ///         existed <c>@container-[inline-size]/main</c> emitted the type and silently discarded
    ///         the name.</b> That is the failure the keyword branch two entries down spends a
    ///         paragraph refusing — <c>text-center/50</c> is not a translucent alignment — arriving
    ///         through the one door that door does not cover, and its symptom is the worse half of
    ///         the pair: a class that <em>works</em>, registering a container the author's
    ///         <c>@container main (…)</c> query can never name, where a refusal would at least have
    ///         been reported as an unrecognised class.
    ///     </para>
    ///     <para>
    ///         The name is held to the same identifier shape the keyword form holds it to, which is
    ///         the refusal row above: <c>container-name: 24rem</c> is a declaration ExCSS drops
    ///         whole, so passing it through would emit a rule whose type landed and whose name did
    ///         not — the asymmetry all over again with an extra step.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_arbitrary_container_type_keeps_the_name_beside_it() {
        var fixture = new UtilityFixture();

        Assert.Equal("inline-size", fixture.Computed(["@container-[inline-size]/main"], "container-type"));
        Assert.Equal("main", fixture.Computed(["@container-[inline-size]/main"], "container-name"));
    }

    /// <summary>The class makes a box a container that a child's query is answered by.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through a document and its layout rather than the cascade alone</b>, because a
    ///         query container is a box with a measured size and nothing in <c>StyleEngine</c> knows
    ///         one: <c>Containers</c> reads <c>container-type</c> off each element after layout and
    ///         registers the scope the cascade then asks. The computed-value theories above prove the
    ///         declaration; this proves the declaration reaches the thing that reads it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three boxes and both signs.</b> A child under an unmarked parent gets nothing, or
    ///         the rule applied unconditionally; a child under <c>@container</c> gets it; and a child
    ///         under <c>@container/main</c> answers the <em>named</em> query where the unnamed
    ///         container does not — which is the row that says the name landed on the scope and not
    ///         only in the computed style.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_marked_box_answers_a_childs_query_and_a_named_one_answers_by_name() {
        var fixture = new UtilityFixture();

        using var document = new UiDocument(300f, 100f);
        document.Load(
            fixture.Generate("@container", "@container/main") + """
            .box { display: flex; width: 120px; height: 20px; }
            .kid { width: 10px; height: 10px; }
            @container (min-width: 100px) { .kid { width: 40px; } }
            @container main (min-width: 100px) { .kid { height: 30px; } }
            """,
            StyleOrigin.Author
        );

        var plain = document.Create("div", document.Root, null, "box");
        var plainKid = document.Create("div", plain, null, "kid");

        var marked = document.Create("div", document.Root, null, "box", "@container");
        var markedKid = document.Create("div", marked, null, "kid");

        var named = document.Create("div", document.Root, null, "box", "@container/main");
        var namedKid = document.Create("div", named, null, "kid");

        document.Update();

        // ⚠ A second pass, because a container's scope is registered off the first layout and the
        // query is answered on the cascade after it — `Settled` is the document's own word for it.
        document.Update();

        Assert.Equal((10f, 10f), (plainKid.Width, plainKid.Height));
        Assert.Equal((40f, 10f), (markedKid.Width, markedKid.Height));
        Assert.Equal((40f, 30f), (namedKid.Width, namedKid.Height));
    }
}
