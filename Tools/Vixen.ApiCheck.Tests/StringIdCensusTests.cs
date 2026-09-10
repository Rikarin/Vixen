// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1203">#1203</a>: the shape
///     <c>CheckStrings</c> could see neither half of, and the forty-nine object initialisers that
///     must stay invisible for the fix to be worth having.
/// </summary>
/// <remarks>
///     <para>
///         <b>The census is <c>build/StringIdCensus.cs</c> and this assembly compiles it</b> — the
///         arrangement <c>DocCommentRuleTests</c> and <c>PathCaseRuleTests</c> are in, and for the
///         same reason. Every answer the pattern set has ever given was a green <c>CheckStrings</c>,
///         and a green gate is what it prints on the day it reads nothing.
///     </para>
///     <para>
///         ⚠ <b>The false-positive half is the half that decides whether this shape survives.</b> A
///         naive <c>\w+ = new\(\s*"</c> matches forty-nine object initialisers in this repository
///         and not one of them builds a <c>StringId</c> — every one is a
///         <c>ProcessStartInfo</c>, an endpoint, a render stage or a world name. A pattern with that
///         hit rate would have been reverted the first time it went red on somebody else's commit,
///         so <see cref="Shapes_that_build_something_else_are_left_alone" /> carries them verbatim.
///     </para>
/// </remarks>
public class StringIdCensusTests {
    /// <summary>
    ///     <c>FoliageMode.cs</c> as it stood, so that a run can prove the third shape is seen.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Verbatim from <c>1c15589fa^</c>.</b> The declaration that makes <c>Unavailable</c> a
    ///     <c>StringId</c> is in <c>Vixen.Ui.Controls/EditorCommand.cs</c>, an assembly away from
    ///     the file that assigns it — which is the whole reason the member set is gathered over the
    ///     tree rather than over one file, and the reason it is included in this fixture.
    /// </remarks>
    const string FoliageFixture = """
        public sealed class EditorCommand {
            public StringId Unavailable { get; init; }
        }

        static class FoliageMode {
            static void Register(EditorShell shell) {
                shell.Commands.Add(
                    new EditorCommand(
                        RemoveTypeCommand,
                        new StringId("editor.command." + RemoveTypeCommand, "Remove Foliage Type"),
                        () => { }
                    ) {
                        Category = EditorStrings.CategoryFoliage,
                        Context = FoliageContext,
                        Unavailable = new("editor.command.foliage.type-remove.unavailable",
                            "Removing a type renumbers every instance above it; not yet built."),
                        Enablement = () => false
                    }
                );
            }
        }
        """;

    /// <summary>The object-initialiser shapes this repository actually holds, none of them a string.</summary>
    /// <remarks>
    ///     ⚠ <b>Taken off the tree rather than invented</b>, from <c>ShadowMapRendererTests</c>,
    ///     <c>PackagedGeneratorTests</c>, <c>RealmFixture</c>, <c>AiOverlayTests</c> and
    ///     <c>CompositorImageTests</c>. An invented negative fixture is a claim about what the rule
    ///     would have been wrong about; these are what it would have been wrong about.
    /// </remarks>
    const string OtherInitialisersFixture = """
        static class Elsewhere {
            static void Build() {
                var process = new Process {
                    StartInfo = new("dotnet") { RedirectStandardOutput = true }
                };

                var pass = new ShadowPass {
                    CasterStage = new("Caster"),
                    Camera = new("camera"),
                    Material = new("Mesh")
                };

                var realm = new RealmSpec {
                    Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
                    Endpoint = new("127.0.0.1", 7777)
                };

                World = new("overlay");
            }
        }
        """;

    /// <summary>The two shapes that were already seen, so the fixture above is not the only positive.</summary>
    const string AnchoredFixture = """
        static class Anchored {
            static readonly StringId CategoryWater = new("editor.category.water", "Water");

            static void Register() {
                Add(new StringId("editor.command.file.save", "Save"));
            }
        }
        """;

    /// <summary>An id assigned in an object initialiser is counted, and it was not before.</summary>
    /// <remarks>
    ///     Both halves: the id reaches <see cref="StringIdCensus.LiteralIds" />, which is what
    ///     <c>Undeclared</c> and <c>Repeated</c> read, and the construction reaches
    ///     <see cref="StringIdCensus.Constructions" />, which is what <c>Constructed</c> counts.
    ///     The gate saw neither for as long as <c>FoliageMode</c>'s line existed.
    /// </remarks>
    [Fact]
    public void An_id_built_in_an_object_initialiser_is_seen() {
        var members = StringIdCensus.Members([FoliageFixture]);

        Assert.Contains("Unavailable", members);

        var ids = StringIdCensus.LiteralIds(FoliageFixture, members).Select(found => found.Id).ToList();

        Assert.Contains("editor.command.foliage.type-remove.unavailable", ids);

        // And the construction census, which counts sites rather than ids: the concatenated one at
        // the top of the fixture and the initialiser below it.
        var constructions = StringIdCensus.Constructions(FoliageFixture, members).ToList();

        Assert.Equal(2, constructions.Count);
        Assert.Contains(constructions, construction => !construction.Literal);
        Assert.Contains(constructions, construction => construction.Literal);
    }

    /// <summary>
    ///     ⚠ The half that decides whether the shape is a gate or a nuisance: none of the object
    ///     initialisers this repository holds is counted.
    /// </summary>
    /// <remarks>
    ///     The member set is the tree's, so this is the real question rather than a fixture-local
    ///     one — <c>StartInfo</c>, <c>CasterStage</c>, <c>Key</c> and <c>Endpoint</c> stay invisible
    ///     because nothing anywhere declares them as a <c>StringId</c>.
    /// </remarks>
    [Fact]
    public void Shapes_that_build_something_else_are_left_alone() {
        var members = StringIdCensus.Members([FoliageFixture, OtherInitialisersFixture, AnchoredFixture]);

        Assert.Empty(StringIdCensus.LiteralIds(OtherInitialisersFixture, members));
        Assert.Empty(StringIdCensus.Constructions(OtherInitialisersFixture, members));
    }

    /// <summary>A member set that has stopped reading makes the census quieter, never louder.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the failure mode, written down as a test.</b> An empty member set does not
    ///     make <c>CheckStrings</c> fail — it makes every object-initialiser construction stop being
    ///     recognised, so both ceilings go down and the run reads as progress. <c>CheckStrings</c>
    ///     asserts the set is populated and contains <c>Unavailable</c> before trusting the count,
    ///     and this is what that assertion is defending against.
    /// </remarks>
    [Fact]
    public void An_empty_member_set_silently_unsees_the_third_shape() {
        Assert.Empty(StringIdCensus.LiteralIds(FoliageFixture, new HashSet<string>()));

        // The two anchored shapes are unaffected, which is why the loss is silent: the census still
        // reports a plausible number.
        Assert.Equal(2, StringIdCensus.LiteralIds(AnchoredFixture, new HashSet<string>()).Count());
    }

    /// <summary>A declaration that satisfies two shapes at once is one site, not two.</summary>
    /// <remarks>
    ///     <c>static readonly StringId CategoryWater = new("editor.category.water", "Water");</c>
    ///     matches the initialiser pattern anchored on the type name and the member-filtered one
    ///     together. Two shapes agreeing about one site would otherwise be two violations for one
    ///     line, and two counts against a ceiling.
    /// </remarks>
    [Fact]
    public void One_site_matching_two_shapes_is_counted_once() {
        var members = StringIdCensus.Members([AnchoredFixture]);

        Assert.Contains("CategoryWater", members);
        Assert.Equal(2, StringIdCensus.LiteralIds(AnchoredFixture, members).Count());
        Assert.Equal(2, StringIdCensus.Constructions(AnchoredFixture, members).Count());
    }

    /// <summary>
    ///     ⚠ A parameter is not a member, and relaxing that is three false positives out of four.
    /// </summary>
    /// <remarks>
    ///     <c>BackgroundTask</c> and <c>EditorDocument</c> both take a <c>string title</c> and write
    ///     <c>this.title = new(title)</c> into a <c>Signal&lt;string&gt;</c>. A member pattern that
    ///     accepted a lower-case identifier after <c>StringId</c> would pick <c>title</c> up from
    ///     some other file's parameter list and report both of them.
    /// </remarks>
    [Fact]
    public void A_parameter_name_is_not_a_member_name() {
        const string declaring = """
            static class Signals {
                static string Format(StringId title, StringId category) => title.Text + category.Text;
            }
            """;

        const string assigning = """
            sealed class BackgroundTask {
                readonly Signal<string> title = new(string.Empty);

                internal BackgroundTask(string title) => this.title = new(title);
            }
            """;

        var members = StringIdCensus.Members([declaring, assigning]);

        Assert.DoesNotContain("title", members);
        Assert.Empty(StringIdCensus.Constructions(assigning, members));
    }

    /// <summary>A <c>///</c> line is not a call site, in the shape whose every real hit is one.</summary>
    /// <remarks>
    ///     Every object-initialiser hit on this repository today is prose explaining the shape —
    ///     <c>TerrainStrings</c>' remarks and <c>Build.Strings.cs</c>' own. ⚠ A <c>//</c> line is
    ///     <em>not</em> excluded, and the comment describing this shape inside <c>CheckStrings</c>
    ///     failed the gate twice before it was written without the example in it.
    /// </remarks>
    [Fact]
    public void Prose_that_quotes_the_shape_is_not_a_call_site() {
        const string prose = """
            static class Documented {
                /// <summary>⚠ Written as <c>Unavailable = new("editor.command.x", "X")</c> once.</summary>
                public static StringId Unavailable { get; } = EditorStrings.Nothing;
            }
            """;

        var members = StringIdCensus.Members([prose]);
        var found = StringIdCensus.LiteralIds(prose, members).ToList();

        Assert.Single(found);
        Assert.True(StringIdCensus.InDocComment(prose, found[0].Index));
    }
}
