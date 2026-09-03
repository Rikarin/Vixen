// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

/// <summary>What shape of Raven a graph compiles to.</summary>
/// <remarks>
///     <para>
///         <b>The one structural decision a node makes, and until doc 08's material compiler there
///         was only one of them.</b> A standalone shader is a whole program — a vertex stage, a
///         fragment stage and a <c>return</c> — which is what an author can read, hand to
///         <c>raven compile</c> and draw a preview thumbnail with, and which nothing in the engine
///         can put on a mesh: a draw binds transforms, lights, shadows and a bindless table by
///         names that shader does not declare.
///     </para>
///     <para>
///         ⚠ <b>A material feature is what actually draws</b>, and it is not a smaller shader — it
///         is a different one. <c>IMaterialSurface</c> has no stages, no entry point and no
///         <c>return</c>; it reads and writes the <c>MaterialData</c> a pass already interpolated,
///         and <c>MaterialCompiler</c> composes it into <c>CompositeSurface</c> beside the
///         hand-written features. That is why a graph that draws needs no new render feature, no new
///         pass and no new binding convention: the whole of the engine's material path is already
///         written for exactly this shape.
///     </para>
/// </remarks>
public enum ShaderGraphKind {
    /// <summary>A whole shader with its own stages. Readable, previewable, and not drawable.</summary>
    Standalone,

    /// <summary>
    ///     A material feature: <c>shader N : IMaterialSurface</c>, composed into a pass by
    ///     <c>MaterialCompiler</c>.
    /// </summary>
    Surface
}

/// <summary>Which stage a value comes from.</summary>
public enum ShaderStageInput {
    /// <summary>The interpolated texture coordinate.</summary>
    Uv,

    /// <summary>The world-space position.</summary>
    WorldPosition,

    /// <summary>The interpolated world-space normal.</summary>
    WorldNormal,

    /// <summary>The interpolated vertex colour.</summary>
    VertexColour
}

/// <summary>
///     Where a node writes its Raven, and what it may ask the shader for.
/// </summary>
/// <remarks>
///     <para>
///         <b>A node writes statements, not a shader.</b> It has no idea what stage it is in, what the
///         entry point is called or what the master node did; it emits lines that assign to its own
///         output variables and it stops there. Everything structural is
///         <see cref="ShaderGraphCompiler" />'s, which is what lets a node be twelve lines and a
///         plugin's node be twelve lines too.
///     </para>
///     <para>
///         <b>Declarations are requests, not text.</b> A node that needs a uniform or an interpolated
///         value says so and gets the name back; the compiler decides where the declaration goes and
///         emits it once however many nodes asked. Two texture nodes sampling the same property
///         declare one binding, and a graph with no normal node interpolates no normal.
///     </para>
/// </remarks>
public sealed class RavenEmitter {
    readonly StringBuilder body;
    readonly Dictionary<string, string> uniforms;
    readonly HashSet<ShaderStageInput> stage;
    readonly Dictionary<string, string> maps;

    readonly SortedSet<string> imports;

    internal RavenEmitter(
        StringBuilder body,
        Dictionary<string, string> uniforms,
        HashSet<ShaderStageInput> stage,
        Dictionary<string, string> maps,
        SortedSet<string> imports,
        ShaderGraphKind kind
    ) {
        this.body = body;
        this.uniforms = uniforms;
        this.stage = stage;
        this.maps = maps;
        this.imports = imports;
        Kind = kind;
    }

    /// <summary>What shape the compiler is emitting, which decides what a node may reach.</summary>
    /// <remarks>
    ///     <b>Read by the emitter, not by a node</b> — that is the whole point of it being here. A
    ///     node says "sample this property at this coordinate" and "give me the surface normal", and
    ///     what those become differs entirely between the two shapes; a node that branched on this
    ///     would be a node every plugin author had to write twice.
    /// </remarks>
    public ShaderGraphKind Kind { get; }

    /// <summary>How many lines have been written into the body so far.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted here rather than measured afterwards, and that is what makes the span map
    ///     honest.</b> <see cref="ShaderGraphCompiler" /> reads this either side of a node's
    ///     <see cref="ShaderNode.Emit" /> to learn which lines that node wrote; recovering the same
    ///     thing by scanning the emitted text would mean matching variable names, which is a guess
    ///     about a naming convention rather than a record of what happened.
    /// </remarks>
    internal int Lines { get; private set; }

    /// <summary>Writes one statement into the pixel stage's body.</summary>
    /// <param name="statement">The Raven. One statement, and one line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="statement" /> is null.</exception>
    /// <remarks>
    ///     <b>One line, because a Raven statement ends where its line does.</b> A wrapped expression
    ///     is a statement followed by orphan expressions, and the message that comes back names a
    ///     token rather than the node that emitted it. Nothing here wraps.
    /// </remarks>
    public void Emit(string statement) {
        ArgumentNullException.ThrowIfNull(statement);
        body.Append("        ").AppendLine(statement);
        Lines++;
    }

    /// <summary>Declares a value the node computes, and names it.</summary>
    /// <param name="variable">The variable to write, which is an output port's name.</param>
    /// <param name="expression">What it is.</param>
    public void Assign(string variable, string expression) => Emit($"val {variable} = {expression}");

    /// <summary>Asks for a Raven package to be in scope, for a node that calls a library function.</summary>
    /// <param name="package">The package, as it would be written after <c>import</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="package" /> is null or blank.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Asked for rather than always emitted, because the two shapes had different
    ///         answers and neither was right for the other.</b> A surface graph imports the four
    ///         <c>Vixen.Shaders.*</c> packages unconditionally — it is composed into a pass that has
    ///         them — while a standalone graph imported <em>nothing</em>, which is what a preview
    ///         wants and what a node calling <c>ComputeColor.ValueNoise</c> cannot have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An import a graph does not use is not free in a preview.</b>
    ///         <see cref="ShaderGraphPreviewRenderer" /> binds one uniform block and no resources at
    ///         all, and refuses any variant whose reflection asks for more — so a preamble that
    ///         imported the shading library into every graph would be paid for by every node that
    ///         never called into it. Set-valued and sorted, so the same graph emits the same source.
    ///     </para>
    /// </remarks>
    public void Import(string package) {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        imports.Add(package);
    }

    /// <summary>Asks for a material uniform, and gets the name to read it by.</summary>
    /// <param name="name">What the author called the property.</param>
    /// <param name="type">Its Raven type — <c>float4</c>, <c>Texture2D</c>, and so on.</param>
    /// <returns>The name the shader declares it under.</returns>
    /// <exception cref="ArgumentException">
    ///     The same name has already been asked for as a different type, which would be one
    ///     declaration that cannot satisfy both.
    /// </exception>
    public string Uniform(string name, string type) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (uniforms.TryGetValue(name, out var existing)) {
            if (existing != type) {
                throw new ArgumentException(
                    $"'{name}' is already declared as {existing} and this node wants a {type}. Two properties "
                    + "cannot share a name.",
                    nameof(type)
                );
            }

            return name;
        }

        uniforms.Add(name, type);

        return name;
    }

    /// <summary>Asks for an interpolated value from the vertex stage, and gets its name.</summary>
    /// <param name="input">Which one.</param>
    /// <returns>The stream variable's name.</returns>
    /// <remarks>
    ///     Asked for rather than always present, so a graph that never reads a normal does not
    ///     interpolate one — which on a dense mesh is a real cost and on every mesh is a varying slot.
    /// </remarks>
    public string Stage(ShaderStageInput input) {
        stage.Add(input);

        if (Kind == ShaderGraphKind.Standalone) {
            return input switch {
                ShaderStageInput.Uv => "uv",
                ShaderStageInput.WorldPosition => "worldPosition",
                ShaderStageInput.WorldNormal => "worldNormal",
                _ => "vertexColour"
            };
        }

        // ⚠ A feature cannot read the pass's streams and must not try. It is composed into a shader
        // it has never seen — `MaterialSurface.rvn` says so at length — so everything it may know
        // about the point being shaded arrives on `MaterialData`. Two of the four are there; the
        // other two are not, and `ShaderGraphCompiler` refuses the graph rather than substituting
        // something plausible. What is returned for those is only what keeps the rest of the walk
        // type-correct so that one refusal is reported instead of a cascade.
        return input switch {
            ShaderStageInput.Uv => "d.uv",
            ShaderStageInput.WorldNormal => "d.tangentFrame.normal",
            ShaderStageInput.WorldPosition => "float3(0f, 0f, 0f)",
            _ => "float4(1f, 1f, 1f, 1f)"
        };
    }

    /// <summary>Reads a material's texture at a coordinate, however this shape reaches one.</summary>
    /// <param name="name">What the author called the property.</param>
    /// <param name="coordinate">The Raven expression to sample at.</param>
    /// <returns>The expression that reads it, as a <c>float4</c>.</returns>
    /// <exception cref="ArgumentException">The name is empty, or is already a different type.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two shapes bind a texture in ways that have nothing in common, and neither is
    ///         a simplification of the other.</b> A standalone shader owns its bindings, so it
    ///         declares the texture and a sampler beside it. A material feature owns none — which
    ///         binding index a texture of its own would get is the composed shader's decision, and
    ///         doc 06 records that as the reason a feature could not sample at all until there was a
    ///         table. So it declares a <c>uint</c> slot instead and reads
    ///         <c>MaterialTextures</c>'s shared array, which is what every hand-written textured
    ///         feature in <c>Raven/Library/Material</c> does.
    ///     </para>
    ///     <para>
    ///         <b>The array is indexed directly rather than through <c>SampleSurface</c>, and that is
    ///         deliberate.</b> That helper samples at <c>d.uv</c> unconditionally, so a graph with a
    ///         <c>Tiling and Offset</c> node feeding a texture's coordinate would silently sample
    ///         somewhere the author did not ask for — a wrong image with nothing to blame, which is
    ///         the exact defect class a generated shader is worst at surfacing.
    ///     </para>
    /// </remarks>
    public string Sample(string name, string coordinate) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinate);

        if (Kind == ShaderGraphKind.Standalone) {
            var texture = Uniform(name, "Texture2D");
            var sampler = Uniform(name + "Sampler", "Sampler");

            return $"{texture}.Sample({sampler}, {coordinate})";
        }

        // The name a host pairs with the material's texture of the same name — the join
        // `MaterialRenderFeature.TextureIndices` makes, and the convention
        // `TexturedMetalRoughnessSurface.baseColorIndex` already keeps.
        var slot = Uniform(name + "Index", "uint");

        maps[name] = slot;

        return $"materialTextures[int({slot})].Sample(materialSampler, {coordinate})";
    }
}

/// <summary>
///     A node of a shader graph: something that writes a line of Raven.
/// </summary>
/// <remarks>
///     Derives from <see cref="Node" /> and adds exactly one thing — what the node <i>does</i>. The
///     port machinery, the binding and the metadata are all the framework's, so a new node is a class
///     with some marked fields and one method.
/// </remarks>
public abstract class ShaderNode : Node {
    /// <summary>Writes whatever this node contributes.</summary>
    /// <param name="emitter">Where to write it.</param>
    /// <remarks>
    ///     Called once per instance per compilation, with every port field already filled: an input
    ///     holds the expression that reads whatever feeds it, and an output holds the name to write.
    /// </remarks>
    protected internal abstract void Emit(RavenEmitter emitter);
}

/// <summary>
///     The node a graph ends at: what the shader actually outputs.
/// </summary>
/// <remarks>
///     A graph needs exactly one, and <see cref="ShaderGraphCompiler" /> says so — a graph with none
///     produces nothing, and one with two would produce two shaders under one name. Which master it
///     is decides the shape of the emitted stage, which is the one structural decision a node makes.
/// </remarks>
public abstract class ShaderMasterNode : ShaderNode {
    /// <summary>The expression the pixel stage returns.</summary>
    /// <remarks>
    ///     Read after <see cref="ShaderNode.Emit" /> has run, so a master may emit as many statements
    ///     as it likes and then name the last one. Empty for a <see cref="ShaderGraphKind.Surface" />
    ///     master, which returns nothing — it has written into the surface it was handed.
    /// </remarks>
    protected internal virtual string Result => string.Empty;

    /// <summary>What shape of shader this master makes the graph.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what the class summary above used to claim and the code did not do.</b>
    ///     <c>ShaderGraphCompiler.Finish</c> hard-coded one preamble, one vertex stage and one
    ///     <c>float4</c> fragment whatever master the graph held, so "which master it is decides the
    ///     shape of the emitted stage" was true of the design and false of the build. It is true now,
    ///     and this property is the whole of the mechanism.
    /// </remarks>
    protected internal virtual ShaderGraphKind Kind => ShaderGraphKind.Standalone;
}
