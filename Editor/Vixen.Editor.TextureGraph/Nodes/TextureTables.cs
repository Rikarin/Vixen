// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>The one-row tables three nodes read: a colour ramp and a curve.</summary>
/// <remarks>
///     <para>
///         <b>Every one of them is an <em>external</em> image, and that is the whole of why
///         <c>Gradient</c>, <c>Curve</c> and <c>GradientMap</c> had no node until
///         <a href="https://github.com/Rikarin/Vixen/issues/732">#732</a>.</b> A table is not
///         computed by a kernel — it is baked on the CPU out of the evaluator the editor already has
///         for that shape, which is what stops a graph from acquiring a second opinion about what a
///         stop list or a tangent means. <see cref="TextureRamp" /> is that bake and this is where a
///         node asks for it.
///     </para>
///     <para>
///         <b>Two ways for a table to arrive, and the reason is what a pure compilation may do.</b>
///         An <em>authored</em> ramp or curve lives in an asset, and resolving one means an asset
///         database a compiler that runs on every edit must not touch — so the reference crosses on
///         <c>TextureGraphCompiler.Externals</c> and a host reads it. With no reference, the table is
///         baked here and now, through <see cref="TextureRamp" />, and the plan is complete: a
///         gradient with no ramp is black to white and a curve with none is the identity, both of
///         which are the answers those nodes should give.
///     </para>
///     <para>
///         ⚠ <b>An identity that did not go through <c>CurveEvaluation</c> would be worth nothing.</b>
///         <see cref="TextureRamp.Straight" />'s own remark says why — a channel left alone has to be
///         an identity <em>through the same evaluator</em>, or a curve node silently drops colour on
///         the three channels nobody touched.
///     </para>
/// </remarks>
static class TextureTables {
    /// <summary>The colour ramp one setting names, or a black-to-white strip when it names none.</summary>
    /// <param name="emitter">The node being compiled.</param>
    /// <param name="setting">The setting holding the asset reference.</param>
    /// <returns>The external image's index in the plan's table, or −1 when it could not be made.</returns>
    public static int Ramp(TextureEmitter emitter, string setting) {
        ArgumentNullException.ThrowIfNull(emitter);

        var asset = emitter.Text(setting).Trim();

        if (asset.Length > 0) {
            return emitter.External(TextureFormat.Rgba8, TextureChannels.Colour, asset);
        }

        // Black to white, baked through `FromRamp` exactly as an authored gradient would be. The
        // closed form `Gradient.rvn` names — a linear sweep over the identity strip is
        // `(x + 0.5) / width` — is a statement about *this* strip, so it is the one to default to.
        return emitter.External(
            TextureFormat.Rgba8,
            TextureChannels.Colour,
            TextureRamp.Entries,
            1,
            TextureRamp.FromRamp(position => new Color4(position, position, position, 1f))
        );
    }

    /// <summary>The curve table one setting names, or the identity when it names none.</summary>
    /// <param name="emitter">The node being compiled.</param>
    /// <param name="setting">The setting holding the asset reference.</param>
    /// <returns>The external image's index in the plan's table, or −1 when it could not be made.</returns>
    public static int Curve(TextureEmitter emitter, string setting) {
        ArgumentNullException.ThrowIfNull(emitter);

        var asset = emitter.Text(setting).Trim();

        if (asset.Length > 0) {
            return emitter.External(TextureFormat.Rgba8, TextureChannels.Colour, asset);
        }

        var straight = TextureRamp.Straight();

        return emitter.External(
            TextureFormat.Rgba8,
            TextureChannels.Colour,
            TextureRamp.Entries,
            1,
            TextureRamp.FromCurves(straight, straight, straight, straight)
        );
    }
}
