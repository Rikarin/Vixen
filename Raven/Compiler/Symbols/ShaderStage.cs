namespace Vixen.Raven.Symbols;

/// <summary>
/// The pipeline stage a method is an entry point for, taken from a stage
/// attribute (<c>[VertexShader]</c>, <c>[PixelShader]</c>, …).
/// </summary>
public enum ShaderStage {
    None,
    Vertex,
    Pixel,
    Geometry,
    Compute
}

/// <summary>How a shader field maps onto a GPU binding.</summary>
public enum ResourceKind {
    /// <summary>Not a resource — a plain field or local.</summary>
    None,
    /// <summary>A scalar/vector/matrix shader field: a uniform / constant-buffer entry.</summary>
    Uniform,
    Texture,
    Sampler
}
