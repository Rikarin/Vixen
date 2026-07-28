// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.ShaderGraph;

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

    internal RavenEmitter(StringBuilder body, Dictionary<string, string> uniforms, HashSet<ShaderStageInput> stage) {
        this.body = body;
        this.uniforms = uniforms;
        this.stage = stage;
    }

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
    }

    /// <summary>Declares a value the node computes, and names it.</summary>
    /// <param name="variable">The variable to write, which is an output port's name.</param>
    /// <param name="expression">What it is.</param>
    public void Assign(string variable, string expression) => Emit($"val {variable} = {expression}");

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

        return input switch {
            ShaderStageInput.Uv => "uv",
            ShaderStageInput.WorldPosition => "worldPosition",
            ShaderStageInput.WorldNormal => "worldNormal",
            _ => "vertexColour"
        };
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
    ///     as it likes and then name the last one.
    /// </remarks>
    protected internal abstract string Result { get; }
}
