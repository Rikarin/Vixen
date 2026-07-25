namespace Vixen.Raven.CodeGen.Glsl;

/// <summary>Knobs for the GLSL backend.</summary>
public sealed class GlslOptions {
    /// <summary>
    ///     The <c>#version</c> to declare. 450 is the default because it is what
    ///     Vulkan-targeted GLSL uses and what <c>glslangValidator</c> assumes.
    /// </summary>
    public int Version { get; init; } = 450;
}
