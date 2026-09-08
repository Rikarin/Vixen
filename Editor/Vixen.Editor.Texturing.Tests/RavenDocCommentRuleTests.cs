// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Build;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/1076">#1076</a>: the same defect
///     <see cref="DocCommentRuleTests" /> covers, in the half of this repository's source no compiler
///     here parses.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>CheckDocComments</c> parses C#.</b> Raven has the same <c>///</c> doc comments and
///         176 committed <c>.rvn</c> files carry 2 349 of them, so a block stapled above the wrong
///         <c>func</c> in a shader was outside every gate in this repository. Not theoretically: the
///         commit that quoted that blind spot as its own warning then inserted <c>Lift</c> between
///         <c>Blend.Combine</c>'s block and <c>Combine</c>.
///     </para>
///     <para>
///         ⚠ <b>The rule #1076 asked for was measured and refused, which is the finding worth
///         carrying.</b> "A block whose prose names a different <c>func</c> in the same file" reports
///         49 findings on this tree and every one sampled is a deliberate cross-reference — including
///         <c>ComputeColor.SoftLight</c>'s own correction, which names <c>HardLight</c> on purpose.
///         It also misses the defect it was proposed for, since <c>Combine</c>'s old summary named no
///         function at all. <see cref="A_deliberate_cross_reference_is_not_a_staple" /> is that
///         refutation pinned, so a later batch cannot re-propose it by reasoning.
///     </para>
///     <para>
///         <b>What replaced it is where the block ends.</b> Prose has no second <c>&lt;summary&gt;</c>
///         to count, but two blocks spliced into one leave a line that finishes a sentence well short
///         of the wrap column with a new sentence on the very next line and no <c>///</c> separator.
///         Every real paragraph break in this tree is written with that separator.
///     </para>
/// </remarks>
public class RavenDocCommentRuleTests {
    /// <summary>Where this file was compiled from.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The repository tree this assembly was compiled from.</summary>
    /// <remarks>
    ///     Anchored at the compiled path rather than climbing for a <c>.git</c>, for
    ///     <see cref="DocCommentRuleTests" />'s reason: <c>.claude/worktrees</c> holds a whole
    ///     checkout per agent and a climb from there reads somebody else's copy of these shaders.
    /// </remarks>
    static string Repository() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));

    /// <summary>
    ///     ⚠ Every doc comment in every committed shader describes the declaration it is attached to,
    ///     and every file on the exemption list still needs to be on it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Everything before the last two assertions is the instrument</b>, and this rule has
    ///         three ways to read nothing rather than the C# rule's two. A glob that stopped matching
    ///         finds no files; a run splitter that stopped splitting finds files with no blocks in
    ///         them; a rule that lost its checks finds blocks and reports nothing. All three print
    ///         "clean tree", so all three are refused by name first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The exemption list was empty the day the rule landed, so it can never be the
    ///         instrument.</b> Eight blocks were wrong when this was written and all eight were fixed
    ///         in the same commit — the mistake <c>docs/DocCommentExempt.txt</c> records is asserting
    ///         "the list is not empty, therefore this run flagged something", which goes vacuous on
    ///         exactly the day it is needed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_repository_holds_no_spliced_doc_comment_outside_the_exemption_list() {
        var root = Repository();
        var sources = RavenDocCommentRule.Sources(root);

        Assert.True(
            sources.Count > 120,
            $"Only {sources.Count} .rvn files were found under {root}. This walk is anchored at this file's "
            + "compiled path; a run whose sources are not on the machine reads nothing and would otherwise "
            + "report a clean tree."
        );

        var texts = sources.ToDictionary(file => file, File.ReadAllText, StringComparer.Ordinal);
        var blocks = texts.Values.Sum(RavenDocCommentRule.Blocks);

        Assert.True(
            blocks > 1500,
            $"Only {blocks} doc comment blocks were read out of {sources.Count} shaders. The run splitter is wrong, "
            + "and a rule that reads no blocks reports no findings."
        );

        // The third instrument, and the only one that survives a clean tree and a clean walk: the
        // rule firing, in this process, on the text that prompted the issue.
        Assert.NotEmpty(RavenDocCommentRule.Check("fixture.rvn", SplicedOntoLift));

        var findings = texts
            .SelectMany(entry => RavenDocCommentRule.Check(entry.Key[(root.Length + 1)..], entry.Value))
            .ToList();

        var exempt = RavenDocCommentRule.Exemptions(root);
        var (unexpected, stale) = DocCommentRule.Review(findings, exempt);

        // One message rather than a collection diff, for DocCommentRuleTests' reason: a doc comment
        // is fixed by reading it, so the file, the line and the sentence have to survive the failure.
        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} .rvn file(s) hold a doc comment block that describes a declaration other than the "
            + "one it is attached to:\n"
            + string.Join('\n', findings.Where(finding => unexpected.Contains(finding.File)))
        );

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} file(s) in {RavenDocCommentRule.ExemptionsPath} no longer hold one. Delete their lines — "
            + "the list may only shrink: " + string.Join(", ", stale)
        );
    }

    /// <summary>⚠ The staple that prompted #1076, as it stood on master.</summary>
    /// <remarks>
    ///     Verbatim from <c>ef0ee04f^</c> rather than reduced, because a reduction is a claim about
    ///     what the defect looked like. If this went quiet the gate would be decoration.
    /// </remarks>
    [Fact]
    public void The_block_Lift_was_inserted_into_is_red() {
        var findings = RavenDocCommentRule.Check("Blend.rvn", SplicedOntoLift);

        Assert.Contains(findings, finding => finding.Message.Contains("spliced", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Message.Contains("TextureBlendDeviceTests", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A live staple this rule found on the day it was written, which nothing had ever reported.
    /// </summary>
    /// <remarks>
    ///     <c>IIrradianceSource</c>'s whole block sat above <c>struct IrradianceSample</c>, twenty-two
    ///     lines from the protocol it describes — so the protocol was undocumented and the struct was
    ///     described twice. That is <c>KeyChord.MacFormat</c>'s defect (#879) in a shader, and it had
    ///     been in <c>Raven/Library</c> long enough that a reader had already quoted the wrong half of
    ///     it. Fixed in the commit that landed this rule; kept here because a rule proved only against
    ///     the defect it was designed from is a rule proved against itself.
    /// </remarks>
    [Fact]
    public void The_protocol_block_that_sat_on_a_struct_is_red() {
        var findings = RavenDocCommentRule.Check("IrradianceField.rvn", StapledOntoIrradianceSample);

        Assert.Contains(findings, finding => finding.Message.Contains("spliced", StringComparison.Ordinal));
    }

    /// <summary>A block that heads no declaration is red.</summary>
    /// <remarks>
    ///     The shape a deleted or moved declaration leaves behind, and the one check here that
    ///     <see cref="DocCommentRule" /> deliberately does not make — Roslyn attaches a trailing block
    ///     to whatever token follows, and Raven's line-oriented answer is simply that nothing follows.
    /// </remarks>
    [Fact]
    public void A_block_followed_by_no_declaration_is_red() {
        var findings = RavenDocCommentRule.Check(
            "Orphan.rvn",
            """
            shader Orphan {
                /// The distance the march gave up at.

                func Trace(origin: float3): float => 0f
            }
            """
        );

        Assert.Contains(findings, finding => finding.Message.Contains("documents nothing", StringComparison.Ordinal));
    }

    /// <summary>An XML <c>&lt;param&gt;</c> naming a parameter the function below does not have is red.</summary>
    /// <remarks>
    ///     The one check this rule and <see cref="DocCommentRule" /> share, and it fires nowhere in
    ///     the tree today. One library shader does use the tag — <c>SpecularModels.GgxAnisotropic</c>
    ///     — which is what makes it worth carrying rather than a check for a syntax nobody writes.
    /// </remarks>
    [Fact]
    public void A_param_tag_naming_a_parameter_the_function_lacks_is_red() {
        var findings = RavenDocCommentRule.Check(
            "SpecularModels.rvn",
            """
            struct SpecularModels {
                /// The anisotropic lobe, for brushed metal and hair.
                /// <param name="beta">Tangent-direction alpha in x, bitangent in y.</param>
                static func GgxAnisotropic(f0: float3, alpha: float2): float3 => f0
            }
            """
        );

        Assert.Contains(findings, finding => finding.Message.Contains("`beta`", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Message.Contains("`GgxAnisotropic`", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ The refutation of #1076's own proposal, pinned so it cannot be re-proposed by reasoning.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The issue asked for "a <c>///</c> run above a <c>func</c> whose prose names a different
    ///         <c>func</c> that exists in the same file", and said it would catch all three of the
    ///         historical defects. Measured over this tree it reports <b>49</b> findings, and the very
    ///         comment written to correct the first of those defects is one of them: this is
    ///         <c>ComputeColor.SoftLight</c> as it stands, and it names <c>HardLight</c> and
    ///         <c>Overlay</c> because the whole point of the sentence is what it is <em>not</em>.
    ///     </para>
    ///     <para>
    ///         ⚠ It would also have missed <c>Combine</c>'s "alpha left alone", which named no
    ///         function — so the proposal catches one of three at a 49-finding cost. That is
    ///         <see cref="DocCommentRule" />'s rejected regular-expression draft again, and this test
    ///         is what stops it being drafted a third time.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_deliberate_cross_reference_is_not_a_staple() =>
        Assert.Empty(
            RavenDocCommentRule.Check(
                "ComputeColor.rvn",
                """
                struct ComputeColor {
                    /// Photoshop's soft light: the darkening half is a quadratic, the lightening half reaches for
                    /// `sqrt(under)` rather than the W3C's cubic, and the two differ visibly on a dark backdrop.
                    ///
                    /// ⚠ This summary read "overlay with the operands swapped" for the whole of this file's history,
                    /// which is what `HardLight` below *is* — one line down, spelled `Overlay(over, under)`. The
                    /// body here has never been that, so the sentence described the next function. Nothing in this
                    /// repository can see a doc comment stapled to the wrong member in a `.rvn`: `CheckDocComments`
                    /// parses C#.
                    static func SoftLight(under: float3, over: float3): float3 => under

                    static func HardLight(under: float3, over: float3): float3 => Overlay(over, under)

                    static func Overlay(under: float3, over: float3): float3 => under
                }
                """
            )
        );

    /// <summary>The shapes an over-eager rule reports and this one has to leave alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that decides whether this gate survives.</b> A paragraph whose line ends a
    ///     sentence <em>at</em> the wrap column ran out of room and continues; a declaration wearing
    ///     attributes on its own line and on its own is still a declaration; and a <c>&lt;param&gt;</c>
    ///     that names a real parameter is documentation. A rule that reports any of these is a list
    ///     somebody switches off within a week.
    /// </remarks>
    [Fact]
    public void Shapes_that_are_not_defects_are_left_alone() =>
        Assert.Empty(
            RavenDocCommentRule.Check(
                "Clean.rvn",
                """
                shader Clean {
                    /// Sixty-four bytes, which is the guaranteed minimum push-constant size — a matrix and nothing
                    /// else, so this fits everywhere without asking the device what it allows. That is the whole of
                    /// it. The rest of this paragraph runs on across the wrap column and keeps the same subject
                    /// throughout, which is what a wrapped paragraph looks like and is not a splice at all.
                    [PushConstant] var viewProjection: mat4

                    /// The direction the light travels in xyz, and the ambient term in w.
                    stream var light: float4

                    /// One tap of the kernel.
                    /// <param name="offset">Where in the neighbourhood, in texels.</param>
                    [VertexShader]
                    [Semantic("SV_Position")]
                    func Tap(offset: float2): float4 => float4(offset, 0f, 1f)
                }
                """
            )
        );

    /// <summary>⚠ The <c>Blend.rvn</c> staple, verbatim from <c>ef0ee04f^</c>.</summary>
    /// <remarks>
    ///     Only the bodies are reduced. The comment is the one that was on master: <c>Combine</c>'s
    ///     block ends mid-run and <c>Lift</c>'s begins on the very next line, so the block describing
    ///     sixteen blend modes and their neutrals described a two-line widening helper.
    /// </remarks>
    const string SplicedOntoLift = """
        shader Blend {
            /// The mode applied to two colours, alpha left alone.
            ///
            /// **Every mode has a neutral foreground, and that is what the suite reads them off.** Copy has
            /// none by construction; multiply and divide are neutral at white, screen, add, subtract,
            /// difference, exclusion and colour dodge at black, darken at white, lighten at black, colour
            /// burn at white, overlay, hard light, soft light and signed add at mid-grey. ⚠ A mode
            /// implemented with an operand swapped, a factor dropped or a `1 −` missing generally *keeps*
            /// its distinguishing value at some point and loses its neutral, or the reverse — which is why
            /// `TextureBlendDeviceTests` asserts both and not either.
            /// A three-channel result back in the four lanes `Combine` answers in.
            ///
            /// ⚠ **The fourth lane of `Combine` is dead and this is where that is written down.** Both
            /// `target.Store` calls in `Main` build their alpha out of `a.w`, `b.w` and `opacity` — never
            /// out of `Combine`'s — and read the colour as `blended.xyz`.
            func Lift(colour: float3, over: float4): float4 => float4(colour.x, colour.y, colour.z, over.w)
        }
        """;

    /// <summary>⚠ <c>IIrradianceSource</c>'s block where it sat, on <c>struct IrradianceSample</c>.</summary>
    /// <remarks>
    ///     Verbatim from <c>Raven/Library/IrradianceFields/IrradianceField.rvn</c> before the commit
    ///     that landed this rule. The protocol it describes is twenty-two lines further down and had
    ///     no documentation of its own.
    /// </remarks>
    const string StapledOntoIrradianceSample = """
        /// What a pass needs from an irradiance field, whatever is behind it.
        ///
        /// A `protocol` rather than a shader a pass hard-codes, for the reason `IDistanceFieldSource` is one: the
        /// slot is resolved at compile time with no dispatch, so a pass that reads indirect light is written once
        /// and a project with no field fills the slot with something that answers "none" — no permutation in the
        /// pass, no branch, and no bindings it does not use.
        /// What a surface receives from a field, as the three separate numbers it actually is.
        ///
        /// Separate because a consumer multiplies each into a different term: the irradiance into the ambient
        /// one, the sun into the direct one, and the coverage into whichever fallback it has.
        struct IrradianceSample {
            /// The indirect diffuse a surface receives, divided by π — what to multiply by albedo.
            var irradiance: float3
        }

        protocol IIrradianceSource {
            /// Everything a field says about where a surface stands, in one lookup.
            func Sample(position: float3, normal: float3, view: float3): IrradianceSample
        }
        """;
}
