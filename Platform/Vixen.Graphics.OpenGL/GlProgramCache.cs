// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.OpenGL;

/// <summary>Linked programs, keyed on the shaders and the layout that produced them.</summary>
/// <remarks>
///     <para>
///         <b>Why a cache and not one program per pipeline.</b> The RHI's pipelines are permutations:
///         the same vertex and fragment shaders appear in a dozen descriptions that differ only in
///         blend state, cull mode or depth comparison, because that is what a material system
///         produces. All of those are loose state in GL and none of them affect the program — so
///         compiling one per pipeline would compile the same GLSL a dozen times, and shader
///         compilation is the slowest thing a GL driver does.
///     </para>
///     <para>
///         The layout is part of the key and the shaders alone are not, because the binding indices
///         are baked into the translated source. Two pipelines sharing shaders but not layouts need
///         two programs, and a cache that keyed on shaders alone would hand the second one a program
///         whose samplers point at the first one's texture units.
///     </para>
/// </remarks>
sealed class GlProgramCache(IGlApi gl) : IDisposable {
    readonly Dictionary<Key, Entry> programs = [];

    /// <summary>How many distinct programs have been linked.</summary>
    public int Count => programs.Count;

    /// <summary>The program for a shader-and-layout combination, linking it the first time.</summary>
    /// <param name="key">What identifies the combination.</param>
    /// <param name="stages">The stage, source and name of each shader.</param>
    /// <param name="layout">The layout, for the binding plan.</param>
    /// <returns>The program and where its push constants land.</returns>
    public (uint Program, int PushConstantLocation) Get(
        Key key,
        IReadOnlyList<(ShaderStage Stage, string Source, string Name)> stages,
        GlPipelineLayout layout
    ) {
        if (programs.TryGetValue(key, out var existing)) {
            return (existing.Program, existing.PushConstantLocation);
        }

        var program = gl.CreateProgram();
        var compiled = new List<uint>(stages.Count);
        var named = new List<GlNamedBinding>();

        try {
            foreach (var (stage, source, name) in stages) {
                var translated = GlslTranslator.Translate(source, stage, gl.Profile, layout.Plan);
                named.AddRange(translated.Bindings);

                var shader = gl.CreateShader(GlEnums.Stage(stage));
                compiled.Add(shader);

                if (gl.CompileShader(shader, translated.Source) is { } log) {
                    throw new InvalidOperationException(
                        $"'{name}' ({stage}) failed to compile on {gl.Profile}:{Environment.NewLine}{log}"
                        + $"{Environment.NewLine}--- translated source ---{Environment.NewLine}"
                        + Numbered(translated.Source)
                    );
                }

                gl.AttachShader(program, shader);
            }

            if (gl.LinkProgram(program) is { } linkLog) {
                throw new InvalidOperationException(
                    $"A program failed to link on {gl.Profile}:{Environment.NewLine}{linkLog}"
                );
            }
        } catch {
            foreach (var shader in compiled) {
                gl.DeleteShader(shader);
            }

            gl.DeleteProgram(program);
            throw;
        }

        // Detached and deleted the moment the link succeeds. GL keeps a shader object alive until
        // every program has let go of it, and a backend that forgot this holds every shader's source
        // and IR in driver memory for the life of the process.
        foreach (var shader in compiled) {
            gl.DeleteShader(shader);
        }

        Bind(program, named);
        var push = gl.GetUniformLocation(program, GlslTranslator.PushConstantUniform);
        programs[key] = new(program, push);
        return (program, push);
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var entry in programs.Values) {
            gl.DeleteProgram(entry.Program);
        }

        programs.Clear();
    }

    /// <summary>Assigns the bindings the profile could not carry in the source.</summary>
    /// <remarks>
    ///     Only reached on GLES 3.0 and WebGL2. A sampler is a plain <c>int</c> uniform holding a
    ///     texture unit, which is why it needs the program to be in use and a uniform block does not
    ///     — the two look alike and are set by two entirely different mechanisms.
    /// </remarks>
    void Bind(uint program, List<GlNamedBinding> bindings) {
        if (bindings.Count == 0) {
            return;
        }

        var usesUniforms = bindings.Any(binding =>
            binding.Kind is DescriptorKind.SampledTexture or DescriptorKind.StorageTexture
        );

        if (usesUniforms) {
            gl.UseProgram(program);
        }

        foreach (var binding in bindings) {
            switch (binding.Kind) {
                case DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer: {
                    var index = gl.GetUniformBlockIndex(program, binding.Name);

                    if (index != uint.MaxValue) {
                        gl.UniformBlockBinding(program, index, binding.Index);
                    }

                    break;
                }

                case DescriptorKind.StorageBuffer or DescriptorKind.DynamicStorageBuffer: {
                    var index = gl.GetStorageBlockIndex(program, binding.Name);

                    if (index != uint.MaxValue) {
                        gl.StorageBlockBinding(program, index, binding.Index);
                    }

                    break;
                }

                case DescriptorKind.SampledTexture or DescriptorKind.StorageTexture: {
                    var location = gl.GetUniformLocation(program, binding.Name);

                    if (location >= 0) {
                        gl.Uniform(location, (int)binding.Index);
                    }

                    break;
                }

                // Unreachable in principle — GlBindingPlan.Build refuses a layout that declares
                // one — but reaching here silently would bind a program with a hole in it.
                case DescriptorKind.AccelerationStructure:
                    throw new NotSupportedException(
                        $"Binding '{binding.Name}' is an acceleration structure, and the OpenGL "
                        + "backend has no ray tracing. Ask Features.HasRayTracing and take the "
                        + "distance-field tracer."
                    );

                // A standalone sampler has no declaration of its own in GLSL — GL's samplers are
                // attached to texture units, not to shader objects — so there is nothing to bind
                // here. `GlDescriptorSet.DefaultSampler` is where it is resolved instead.
                case DescriptorKind.Sampler:
                default:
                    break;
            }
        }
    }

    static string Numbered(string source) {
        var lines = source.Split('\n');
        return string.Join(Environment.NewLine, lines.Select((line, index) => $"{index + 1,4}| {line.TrimEnd()}"));
    }

    /// <summary>What identifies a program.</summary>
    /// <param name="Vertex">The vertex or compute shader.</param>
    /// <param name="Fragment">The fragment shader, or the null handle.</param>
    /// <param name="Layout">The pipeline layout whose plan was baked into the source.</param>
    internal readonly record struct Key(
        ShaderHandle Vertex,
        ShaderHandle Fragment,
        PipelineLayoutHandle Layout
    );

    readonly record struct Entry(uint Program, int PushConstantLocation);
}
