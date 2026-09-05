// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;

namespace Vixen.Editor.TextureGraph;

/// <summary>Every diagnostic id this assembly reports, and the one sentence each of them means.</summary>
/// <remarks>
///     <para>
///         <b>An id is what a host filters, suppresses and links help on</b>, so an id that means two
///         things is a filter that hides the wrong half of them. Before
///         <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a> the ids were string
///         literals at nine call sites and nothing listed them: <c>TG0017</c> and <c>TG0018</c> each
///         meant two different things — one of each pair a warning and the other an error — and
///         ⚠ <c>TG0012</c> was a third collision that predated the batch which renumbered the other
///         two by hand. Renumbering fixes an instance. This class is the cause.
///     </para>
///     <para>
///         <b>The declaration is here and the check reads it.</b> Two members holding one id is not a
///         compile error, so <see cref="Ids" /> is derived from the literals below by reflection and
///         <c>TextureDiagnosticIdTests</c> requires it to be distinct — the same move the kernel roll
///         call and the compound library made, which is to read the thing rather than keep a second
///         opinion about it. The other half of that gate walks this project's sources and refuses a
///         <c>"TG…"</c> literal anywhere but this file, which is what stops the tenth call site
///         inventing a twenty-second id in passing.
///     </para>
///     <para>
///         ⚠ <b><c>TG0003</c> and <c>TG0007</c> have never been used.</b> Measured with
///         <c>git log -S</c> over the whole history rather than assumed: the numbering has had those
///         two holes in it since the ids were first written, so neither is a retired meaning and
///         either may be taken by whatever needs one next. There is deliberately no
///         <c>TG0022</c>-shaped "next free id" member — a constant somebody has to remember to
///         increment is the defect one level up.
///     </para>
///     <para>
///         Internal, like every other type here. A host that wants to filter on one of these spells
///         the four characters, exactly as it would for an <c>RVN</c> rule, and the guide page is
///         where the sentences are read.
///     </para>
/// </remarks>
static class TextureDiagnostics {
    /// <summary>
    ///     A node type in this graph's library produced nothing: a published graph nothing inlined,
    ///     or an entry that is not a texture node at all.
    /// </summary>
    internal const string NothingToCompile = "TG0001";

    /// <summary>
    ///     A node needs an image and has none — an unwired input, or a source node whose asset
    ///     reference is empty. There is no literal image an author could type into a port instead.
    /// </summary>
    internal const string NoImage = "TG0002";

    /// <summary>
    ///     A colour arrived at an input that is measured rather than composited, and takes one
    ///     channel. There is no luminance a colour and a mask agree on.
    /// </summary>
    internal const string ColourWhereOneChannelIsWanted = "TG0004";

    /// <summary>
    ///     This graph has no <c>Output</c> node, so everything it computes is freed before anything
    ///     can read it.
    /// </summary>
    internal const string NoOutputNode = "TG0005";

    /// <summary>
    ///     Two <c>Output</c> nodes write one usage, and a bake writes one file per usage — so one of
    ///     them is the map and the graph does not say which.
    /// </summary>
    internal const string TwoOutputsOneUsage = "TG0006";

    /// <summary>
    ///     A port is wired and whatever feeds it produced no image: the node upstream failed, or its
    ///     output is not an image this compiler can carry.
    /// </summary>
    internal const string UpstreamProducedNoImage = "TG0008";

    /// <summary>
    ///     The compiler produced a plan that does not hold together. Every one of these is a compiler
    ///     bug rather than an author's mistake, said as a diagnostic rather than thrown at bake time.
    /// </summary>
    internal const string PlanDoesNotHoldTogether = "TG0009";

    /// <summary>
    ///     A setting on this node holds a value the node does not accept — a name outside the set it
    ///     takes, or a number outside the range it runs over.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The range half arrived here from <c>TG0012</c>, which is #804's third collision.</b>
    ///     <c>Analysis/Flood Fill</c>'s iteration-count refusal used <c>TG0012</c>, and so does an
    ///     expression this compiler will not take — two unrelated sentences under one id, both
    ///     errors, both reachable from one graph. The expression family <c>TG0012</c>–<c>TG0014</c>
    ///     is contiguous and stays whole; the refusal that moved is the one whose sentence was
    ///     already written here, with a set where it now also says a range.
    /// </remarks>
    internal const string SettingNotAccepted = "TG0010";

    /// <summary>
    ///     A kernel builder refused the numbers this node handed it, and its own message names them.
    ///     What the node adds is a diagnostic an author can select, rather than an exception three
    ///     frames away in a background bake.
    /// </summary>
    internal const string BuilderRefusedTheNumbers = "TG0011";

    /// <summary>An expression on a port is one this compiler refuses to put through Raven.</summary>
    internal const string ExpressionRefused = "TG0012";

    /// <summary>An expression on a port does not compile, and Raven's own complaint says why.</summary>
    internal const string ExpressionDoesNotCompile = "TG0013";

    /// <summary>
    ///     An expression on a port compiles and Raven cannot fold it to a number at compile time. A
    ///     plan's parameter is one float, so an expression is literals, parameters and arithmetic.
    /// </summary>
    internal const string ExpressionDoesNotFold = "TG0014";

    /// <summary>
    ///     A parameter override does not parse, or falls outside the declared range, and the
    ///     parameter kept its default.
    /// </summary>
    internal const string ParameterOverrideIgnored = "TG0015";

    /// <summary>
    ///     An expression is stored for a port that cannot carry one — a port the node has not got any
    ///     more, or an image input, which is wired rather than computed.
    /// </summary>
    internal const string ExpressionOnAPortThatTakesNone = "TG0016";

    /// <summary>
    ///     A node baked its own picture and handed over a different number of bytes than its width,
    ///     height and format need. An external image is uploaded exactly as it is written down.
    /// </summary>
    internal const string BakedPictureIsTheWrongSize = "TG0017";

    /// <summary>
    ///     A resample writes an image of its own size, which is a copy at the cost of a dispatch and
    ///     a texture. A warning: the plan it produces is sound.
    /// </summary>
    internal const string ResampleOntoItsOwnSize = "TG0018";

    /// <summary>
    ///     A setting the graph itself declares — a base extent, a seed — does not hold, and the
    ///     compiler's own value was used. A warning, and it names no node.
    /// </summary>
    internal const string GraphSettingIgnored = "TG0019";

    /// <summary>A Pixel Processor's expression is one this compiler refuses before Raven sees it.</summary>
    internal const string PixelProcessorExpressionRefused = "TG0020";

    /// <summary>
    ///     Raven's own complaint about the kernel a Pixel Processor's expression generated, mapped
    ///     back to the node and the span inside the expression.
    /// </summary>
    internal const string PixelProcessorDoesNotCompile = "TG0021";

    /// <summary>
    ///     An output was computed at a level below the graph's maps and the compiler resampled it
    ///     back. A warning, against the Output node's usage.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Neither of the two shapes #805 proposed, and the reason is what each of them cost.</b>
    ///     Rescaling in silence is what the issue objected to; refusing makes a legal-looking graph
    ///     illegal. A warning is neither: the bake draws a picture rather than throwing out of a
    ///     background task, and the author is told which map was resampled and where to say it in the
    ///     graph instead.
    /// </remarks>
    internal const string OutputResampledToTheGraphsMaps = "TG0022";

    /// <summary>Every id declared above, read off the declarations rather than listed again.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a collision findable at all.</b> Two members holding the same
    ///     string compile perfectly; a duplicate in this array does not survive
    ///     <c>TextureDiagnosticIdTests</c>. Reflection over <see cref="FieldInfo.IsLiteral" /> rather
    ///     than a second array, because a second array is the thing that would go stale — and the
    ///     roll call checks the reflection query itself found something, since an empty one is
    ///     trivially distinct.
    /// <para>
    ///     ⚠ <b><c>Ids</c> and emphatically not <c>All</c>, which is a real trap and was measured
    ///     rather than guessed.</b> The kernel roll call's "declaring surface" detector is a member
    ///     name: <c>TextureColourKernelTests.Declared</c> and <c>TextureNodeLibraryTests.Declared</c>
    ///     sweep <em>every</em> type in this assembly for a static <c>All</c> returning strings and
    ///     take what it holds to be kernel names. Calling this one <c>All</c> put nineteen diagnostic
    ///     ids into the kernel inventory and turned two roll calls red with a message about
    ///     <c>Tile</c>. A future non-kernel surface here should not be called <c>All</c> either —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>.
    /// </para>
    /// </remarks>
    internal static ImmutableArray<string> Ids { get; } = [
        .. typeof(TextureDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
    ];
}
