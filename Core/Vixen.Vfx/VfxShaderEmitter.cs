// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>One storage buffer the emitted shader expects the host to bind.</summary>
/// <param name="Name">The name it is declared under, which is what the reflection reports.</param>
/// <param name="Attribute">Which attribute it holds.</param>
/// <param name="Stride">How many bytes one particle occupies in it, under std430.</param>
/// <param name="IsWritten">
///     Whether a kernel stores into it. A buffer nothing writes is declared read-only, which is one
///     access decoration and lets a driver hoist a load out of a loop.
/// </param>
public readonly record struct VfxShaderBinding(string Name, VfxAttribute Attribute, int Stride, bool IsWritten);

/// <summary>A compiled graph as Raven source, plus what the host has to bind to run it.</summary>
/// <remarks>
///     Source rather than bytecode, because the thing that turns source into a module is the Raven
///     compiler — a tooling assembly with reflection and LINQ, which is not something the particle
///     runtime should link against. The runtime produces the text; whoever owns a compiler compiles
///     it.
/// </remarks>
public sealed class VfxShader {
    /// <summary>How many invocations one workgroup has.</summary>
    /// <remarks>
    ///     Sixty-four is one wavefront on AMD and two warps on NVIDIA, so it fills either without
    ///     leaving a partial one. The dispatch is rounded up to a whole workgroup and the tail
    ///     invocations return immediately, which is what the bounds test at the top of each kernel is
    ///     for.
    /// </remarks>
    public const int WorkgroupSize = 64;

    internal VfxShader(string name, string source, VfxShaderBinding[] bindings, bool hasInitialize, bool hasUpdate) {
        Name = name;
        Source = source;
        Bindings = bindings;
        HasInitialize = hasInitialize;
        HasUpdate = hasUpdate;
    }

    /// <summary>The base name the three shader declarations are derived from.</summary>
    public string Name { get; }

    /// <summary>The Raven source.</summary>
    public string Source { get; }

    /// <summary>The buffers the kernels touch, in declaration order.</summary>
    /// <remarks>
    ///     A subset of what the graph stores, not a mirror of it: an attribute only the renderer reads
    ///     is a descriptor these kernels would bind and never touch. Storage is what is used, here as
    ///     everywhere else in this module.
    /// </remarks>
    public IReadOnlyList<VfxShaderBinding> Bindings { get; }

    /// <summary>Whether there is an initializer kernel. There is not, if the graph has no initializers.</summary>
    public bool HasInitialize { get; }

    /// <summary>Whether there is an update kernel.</summary>
    public bool HasUpdate { get; }

    /// <summary>The shader declaration holding the bindings and the helpers both kernels use.</summary>
    public string CommonShader => Name + "Common";

    /// <summary>The shader declaration whose entry point applies the initializers.</summary>
    public string InitializeShader => Name + "Initialize";

    /// <summary>The shader declaration whose entry point advances every live particle.</summary>
    public string UpdateShader => Name + "Update";
}

/// <summary>
///     The GPU backend's front half: a compiled graph turned into a Raven compute shader.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a translation and not a second implementation.</b> The compiled graph was
///         given the shape it has — an array of fixed-size operations, no delegates, no pointers,
///         nothing to walk — precisely so that this file could be a <c>switch</c> that writes a line
///         of source per operation. Every decision that would otherwise have to be made twice was made
///         once, in <see cref="VfxCompiledGraph" />: which attributes exist, what each operation
///         reads and writes, and which salt each draws on. What is left here is spelling.
///     </para>
///     <para>
///         <b>The order is inverted, and that is the one real difference.</b>
///         <see cref="VfxSimulation" /> sweeps per operation across every particle, because on a CPU
///         that keeps the opcode dispatch out of the inner loop and walks each attribute array end to
///         end. A dispatch has no inner loop to keep anything out of: one invocation owns one particle
///         and runs the whole graph on it, so every intermediate stays in registers and the buffer is
///         touched once at each end. Sweeping per operation on the GPU would mean one dispatch per
///         operation and a round trip through memory between each — the same arithmetic at several
///         times the bandwidth. Both orders are correct because no operation reads another particle.
///     </para>
///     <para>
///         <b>The graph is unrolled, not interpreted.</b> The operation array could have been uploaded
///         and stepped through by a shader with a <c>switch</c> in it, which would need one shader for
///         every graph instead of one per graph. It would also put a branch on every instruction in
///         the hot path of the one processor that most dislikes them. The graph is known when the
///         effect is compiled, so it is spelled out.
///     </para>
///     <para>
///         <b>What agrees exactly and what agrees closely.</b> The hash is integer arithmetic
///         throughout and is exact on both sides — that is what <see cref="VfxRandom" /> is built for,
///         and it is the part that has to be exact, because a random value that differs by one bit
///         puts a particle somewhere else entirely. The arithmetic downstream of it is ordinary
///         floating point: a sine, a cube root or an exponential is accurate to a fraction of an ulp
///         and the two libraries need not choose the same fraction. So the agreement test is exact on
///         the hash and a tolerance on positions, which is the honest form of that claim.
///     </para>
/// </remarks>
public static class VfxShaderEmitter {
    /// <summary>Turns a compiled graph into Raven source.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="name">
    ///     The base name for the three shader declarations. Has to be a Raven identifier.
    /// </param>
    /// <returns>The source and the bindings it expects.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="name" /> is not an identifier, or an operation carries a parameter that
    ///     cannot be spelled — a cone about a zero axis, or a value that is not finite.
    /// </exception>
    public static VfxShader Emit(VfxCompiledGraph graph, string name = "Effect") {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!IsIdentifier(name)) {
            throw new ArgumentException($"`{name}` is not a Raven identifier, so it cannot name a shader.", nameof(name));
        }

        // The updater list may hold initializer opcodes, which the CPU's update sweep skips — see
        // VfxSimulation.Apply's default case. Dropping them here rather than at every use keeps the
        // two backends agreeing about what an update step is.
        var updaters = graph.Updaters.Where(operation => VfxOpcodes.IsUpdater(operation.Opcode)).ToArray();

        var hasInitialize = graph.Initializers.Length > 0 || Has(graph, VfxAttribute.Age);
        var hasUpdate = updaters.Length > 0 || Has(graph, VfxAttribute.Age);

        var bindings = Bindings(graph, updaters);
        var text = new StringBuilder();

        text.AppendLine("// Generated from a VfxCompiledGraph by Vixen.Vfx. The graph is the source; this is not.")
            .AppendLine()
            .AppendLine("package Vixen.Vfx.Generated")
            .AppendLine();

        Common(text, name, graph, bindings);

        if (hasInitialize) {
            text.AppendLine();
            Kernel(text, $"{name}Initialize", name, () => Initializer(text, graph));
        }

        if (hasUpdate) {
            text.AppendLine();
            Kernel(text, $"{name}Update", name, () => Updater(text, graph, updaters));
        }

        return new(name, text.ToString(), bindings, hasInitialize, hasUpdate);
    }

    // --- The shared declarations -------------------------------------------

    /// <summary>The shader holding the uniforms, the buffers and the helpers both kernels call.</summary>
    /// <remarks>
    ///     A base rather than a copy in each kernel, because <c>shader X : Base</c> already means
    ///     exactly this and two copies of a hash function is two things to keep the same.
    /// </remarks>
    static void Common(StringBuilder text, string name, VfxCompiledGraph graph, VfxShaderBinding[] bindings) {
        // No initializers on the uniforms. A SPIR-V uniform cannot carry one — the compiler says so
        // as an info — and every one of these is set by whoever dispatches, so a default would be a
        // value that never applies and a reader would have to work that out.
        text.AppendLine($"shader {name}Common {{")
            .AppendLine("    /// The step, in seconds. Zero for the initializer dispatch, which applies an")
            .AppendLine("    /// updater in the initializer list exactly as the CPU backend does.")
            .AppendLine("    var deltaTime: float")
            .AppendLine()
            .AppendLine("    /// The system instance's seed.")
            .AppendLine("    var seed: uint")
            .AppendLine()
            .AppendLine("    /// The first particle this dispatch touches. Zero for an update.")
            .AppendLine("    var first: int")
            .AppendLine()
            .AppendLine("    /// How many it touches.")
            .AppendLine("    var particleCount: int")
            .AppendLine()
            .AppendLine("    /// How long the system has been running. Only a drifting field reads it.")
            .AppendLine("    var time: float");

        foreach (var binding in bindings) {
            text.AppendLine()
                .AppendLine($"    var {binding.Name}: {(binding.IsWritten ? "RW" : "")}Buffer<{Element(binding.Attribute)}>");
        }

        var random = graph.Initializers.Any(operation => VfxOpcodes.IsRandom(operation.Opcode));
        var noise = Touches(graph, VfxOpcode.Turbulence);

        // The hash is what the two of them share, and either alone is a reason to emit it: a random
        // initializer hashes the particle's identifier, and a noise field hashes a lattice corner.
        // Emitting it from inside `Random` was a graph with turbulence and nothing random calling a
        // function that was not there.
        if (random || noise) {
            Hash(text);
        }

        if (random) {
            Draws(text);
        }

        if (graph.Initializers.Any(operation => operation.Opcode is VfxOpcode.PositionInSphere or VfxOpcode.VelocityRandomDirection)) {
            Direction(text);
        }

        if (graph.Initializers.Any(operation => operation.Opcode is VfxOpcode.VelocityInCone)) {
            Cone(text);
        }

        if (Touches(graph, VfxOpcode.SizeOverLife) || Touches(graph, VfxOpcode.ColourOverLife)) {
            Fraction(text);
        }

        if (Touches(graph, VfxOpcode.Attract) || Touches(graph, VfxOpcode.Vortex)) {
            Falloff(text);
        }

        if (Touches(graph, VfxOpcode.Turbulence)) {
            Noise(text);
        }

        text.AppendLine("}");
    }

    /// <summary>How much of a field's strength reaches a particle this far from it.</summary>
    static void Falloff(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// Squared, so the edge of the region eases rather than creases. A radius of")
            .AppendLine("    /// zero or less reaches everywhere and does not fall off at all.")
            .AppendLine("    func Falloff(distance: float, radius: float): float {")
            .AppendLine("        if (radius <= 0f) {")
            .AppendLine("            return 1f")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        val remaining = 1f - clamp(distance / radius, 0f, 1f)")
            .AppendLine()
            .AppendLine("        return remaining * remaining")
            .AppendLine("    }");
    }

    /// <summary>Value noise over a lattice, and the curl of three of them.</summary>
    /// <remarks>
    ///     <para>
    ///         Transcribed from <see cref="VfxNoise" /> the same way the RNG is transcribed from
    ///         <see cref="VfxRandom" />, and for the same reason: a shader cannot call into managed
    ///         code, so the function that has to produce the same field twice is the function that
    ///         exists twice. The lattice values come from the integer hash and agree exactly; the
    ///         interpolation and the differences are float arithmetic and agree to the last bit or
    ///         two.
    ///     </para>
    ///     <para>
    ///         Value noise rather than gradient noise is what makes this transcribable at all — a
    ///         Perlin gradient table would have to be uploaded, and an uploaded table is a way for the
    ///         two sides to differ.
    ///     </para>
    /// </remarks>
    static void Noise(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    func Corner(x: int, y: int, z: int, field: uint): float {")
            .AppendLine("        val mixed = Hash(Hash(Hash(uint(x)) ^ uint(y)) ^ uint(z))")
            .AppendLine()
            .AppendLine("        return float(Hash(Hash(mixed ^ field) ^ 0u) >> 8u) * (1f / 16777216f)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// 3t^2 - 2t^3. The raw fraction leaves a crease at every cell boundary,")
            .AppendLine("    /// which shows up in the motion long before it shows up in the noise.")
            .AppendLine("    func Smooth(t: float): float => t * t * (3f - 2f * t)")
            .AppendLine()
            .AppendLine("    func Noise(point: float3, field: uint): float {")
            .AppendLine("        val cell = int3(int(floor(point.x)), int(floor(point.y)), int(floor(point.z)))")
            .AppendLine("        val fx = Smooth(point.x - float(cell.x))")
            .AppendLine("        val fy = Smooth(point.y - float(cell.y))")
            .AppendLine("        val fz = Smooth(point.z - float(cell.z))")
            .AppendLine()
            .AppendLine("        val x0y0 = Mix(Corner(cell.x, cell.y, cell.z, field), Corner(cell.x, cell.y, cell.z + 1, field), fz)")
            .AppendLine("        val x0y1 = Mix(Corner(cell.x, cell.y + 1, cell.z, field), Corner(cell.x, cell.y + 1, cell.z + 1, field), fz)")
            .AppendLine("        val x1y0 = Mix(Corner(cell.x + 1, cell.y, cell.z, field), Corner(cell.x + 1, cell.y, cell.z + 1, field), fz)")
            .AppendLine("        val x1y1 = Mix(Corner(cell.x + 1, cell.y + 1, cell.z, field), Corner(cell.x + 1, cell.y + 1, cell.z + 1, field), fz)")
            .AppendLine()
            .AppendLine("        return Mix(Mix(x0y0, x0y1, fy), Mix(x1y0, x1y1, fy), fx)")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// a + (b - a) t, and not lerp: this is the arithmetic the CPU backend does.")
            .AppendLine("    func Mix(a: float, b: float, t: float): float => a + (b - a) * t")
            .AppendLine()
            .AppendLine($"    func Slope(point: float3, field: uint, step: float3): float => (Noise(point + step, field) - Noise(point - step, field)) / {Float(2f * VfxNoise.Epsilon)}")
            .AppendLine()
            .AppendLine("    /// The curl of three noise fields, which has zero divergence identically — the")
            .AppendLine("    /// property that makes it swirl rather than pile particles into its sinks.")
            .AppendLine("    func Curl(point: float3, field: uint): float3 {")
            .AppendLine($"        val ex = float3({Float(VfxNoise.Epsilon)}, 0f, 0f)")
            .AppendLine($"        val ey = float3(0f, {Float(VfxNoise.Epsilon)}, 0f)")
            .AppendLine($"        val ez = float3(0f, 0f, {Float(VfxNoise.Epsilon)})")
            .AppendLine()
            // One line, because a Raven statement ends where its line does — a wrapped argument list
            // is a call followed by orphan expressions.
            .AppendLine(
                "        return float3(Slope(point, field + 2u, ey) - Slope(point, field + 1u, ez), "
                + "Slope(point, field, ez) - Slope(point, field + 2u, ex), "
                + "Slope(point, field + 1u, ex) - Slope(point, field, ey))"
            )
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// Octaves are what hide the lattice: one is visibly axis-aligned, three are not.")
            .AppendLine("    func Turbulence(point: float3, field: uint, octaves: int): float3 {")
            .AppendLine("        var total = float3(0f, 0f, 0f)")
            .AppendLine("        var amplitude = 1f")
            .AppendLine("        var frequency = 1f")
            .AppendLine()
            .AppendLine("        for (octave in 0 .. 3) {")
            .AppendLine("            if (octave >= octaves) {")
            .AppendLine("                break")
            .AppendLine("            }")
            .AppendLine()
            .AppendLine("            total += Curl(point * frequency, field + uint(octave) * 3u) * amplitude")
            .AppendLine("            amplitude *= 0.5f")
            .AppendLine("            frequency *= 2f")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        return total")
            .AppendLine("    }");
    }

    /// <summary>The hash, and the two things every random operation asks it for.</summary>
    /// <remarks>
    ///     Transcribed from <see cref="VfxRandom" /> operation for operation. It is short enough to
    ///     transcribe and there is nowhere else for it to live: a shader cannot call into managed code,
    ///     so the one function that has to produce identical bits on both sides is the one function
    ///     that exists twice. Every step is a 32-bit multiply, xor or shift, all of which are defined
    ///     to the bit in SPIR-V, so "identical" here is a property rather than a hope.
    /// </remarks>
    static void Hash(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// lowbias32, offset first because the mixer has a fixed point at zero.")
            .AppendLine("    func Hash(value: uint): uint {")
            .AppendLine("        var mixed = value + 0x9e3779b9u")
            .AppendLine()
            .AppendLine("        mixed = mixed ^ (mixed >> 16u)")
            .AppendLine("        mixed = mixed * 0x7feb352du")
            .AppendLine("        mixed = mixed ^ (mixed >> 15u)")
            .AppendLine("        mixed = mixed * 0x846ca68bu")
            .AppendLine("        mixed = mixed ^ (mixed >> 16u)")
            .AppendLine()
            .AppendLine("        return mixed")
            .AppendLine("    }");
    }

    /// <summary>The two things every random operation asks the hash for.</summary>
    static void Draws(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// One particle's one use of randomness: hashed in turn, never added together.")
            .AppendLine("    func Draw(particle: uint, salt: uint): uint => Hash(Hash(Hash(particle) ^ seed) ^ salt)")
            .AppendLine()
            .AppendLine("    /// Twenty-four bits over 2^24: every float in [0, 1) that has an exact representation.")
            .AppendLine("    func Value(particle: uint, salt: uint): float => float(Draw(particle, salt) >> 8u) * (1f / 16777216f)")
            .AppendLine()
            // On one line, because a Raven statement ends where its line does: an expression body
            // wrapped onto the next line is a body followed by an orphan expression.
            .AppendLine("    func Range(particle: uint, salt: uint, minimum: float, maximum: float): float => minimum + (maximum - minimum) * Value(particle, salt)");
    }

    /// <summary>A direction uniform over the sphere, sampling z rather than the polar angle.</summary>
    static void Direction(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// Uniform over the sphere, not over the angles — the other way pinches at the poles.")
            .AppendLine("    func Direction(particle: uint, salt: uint): float3 {")
            .AppendLine("        val z = Range(particle, salt, -1f, 1f)")
            .AppendLine($"        val azimuth = Range(particle, salt + 1u, 0f, {Float(MathF.Tau)})")
            .AppendLine("        val radius = sqrt(max(0f, 1f - z * z))")
            .AppendLine()
            .AppendLine("        return float3(radius * cos(azimuth), radius * sin(azimuth), z)")
            .AppendLine("    }");
    }

    /// <summary>A direction inside a cone, uniform over the cap it subtends.</summary>
    static void Cone(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// Uniform in cos(theta) rather than in theta, or the particles crowd the axis.")
            .AppendLine("    func Cone(axis: float3, halfAngle: float, particle: uint, salt: uint): float3 {")
            .AppendLine("        val z = Range(particle, salt, cos(halfAngle), 1f)")
            .AppendLine($"        val azimuth = Range(particle, salt + 1u, 0f, {Float(MathF.Tau)})")
            .AppendLine("        val radius = sqrt(max(0f, 1f - z * z))")
            .AppendLine()
            .AppendLine("        // The reference only has to avoid being parallel to the axis.")
            .AppendLine("        var reference = float3(0f, 1f, 0f)")
            .AppendLine()
            .AppendLine("        if (abs(axis.y) >= 0.99f) {")
            .AppendLine("            reference = float3(1f, 0f, 0f)")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        val right = normalize(cross(reference, axis))")
            .AppendLine("        val up = cross(axis, right)")
            .AppendLine()
            .AppendLine("        return right * (radius * cos(azimuth)) + up * (radius * sin(azimuth)) + axis * z")
            .AppendLine("    }");
    }

    /// <summary>How far through its life a particle is.</summary>
    static void Fraction(StringBuilder text) {
        text.AppendLine()
            .AppendLine("    /// A lifetime of zero reads as already over, which is what a particle with none is.")
            .AppendLine("    func Fraction(age: float, lifetime: float): float {")
            .AppendLine("        if (lifetime > 0f) {")
            .AppendLine("            return clamp(age / lifetime, 0f, 1f)")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        return 1f")
            .AppendLine("    }");
    }

    // --- The kernels -------------------------------------------------------

    /// <summary>The wrapper every kernel shares: the bounds test and the slot it works out.</summary>
    /// <remarks>
    ///     The bounds test is not optional. A dispatch is rounded up to a whole workgroup, so the last
    ///     one has invocations with no particle, and an out-of-range access to a storage buffer is
    ///     undefined — a wrong value on one driver and a device loss on another.
    /// </remarks>
    static void Kernel(StringBuilder text, string shader, string name, Action body) {
        text.AppendLine($"shader {shader} : {name}Common {{")
            .AppendLine($"    [ComputeShader({VfxShader.WorkgroupSize})]")
            .AppendLine("    func Main([Semantic(\"SV_DispatchThreadID\")] id: uint3) {")
            .AppendLine("        val lane = int(id.x)")
            .AppendLine()
            .AppendLine("        if (lane >= particleCount) {")
            .AppendLine("            return")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        val slot = first + lane");

        body();

        text.AppendLine("    }")
            .AppendLine("}");
    }

    /// <summary>The initializer kernel's body: one run of newly spawned particles.</summary>
    static void Initializer(StringBuilder text, VfxCompiledGraph graph) {
        if (graph.Initializers.Any(operation => VfxOpcodes.IsRandom(operation.Opcode))) {
            text.AppendLine()
                .AppendLine("        // The identifier, never the slot: a slot is reused the moment its occupant dies,")
                .AppendLine("        // and hashing one would re-roll a particle's values partway through its life.")
                .AppendLine("        val particle = identifier[slot]");
        }

        for (var index = 0; index < graph.Initializers.Length; index++) {
            var operation = graph.Initializers[index];
            text.AppendLine();

            if (VfxOpcodes.IsUpdater(operation.Opcode)) {
                // An updater in the initializer list — "apply gravity once at birth" — which the CPU
                // backend runs with a step of zero rather than refusing.
                Apply(text, operation, "0f", index);
            } else {
                Initialize(text, operation);
            }
        }

        if (Has(graph, VfxAttribute.Age)) {
            text.AppendLine()
                .AppendLine("        age[slot] = 0f");
        }
    }

    /// <summary>The update kernel's body: every live particle, one step.</summary>
    static void Updater(StringBuilder text, VfxCompiledGraph graph, VfxOperation[] updaters) {
        // Ageing first and reaping last, so a particle is updated on the step it dies and not after
        // it. Reaping stays the CPU's for now: the GPU form is an atomic append, which Raven can
        // express since `atomicAdd` landed, but it needs the dispatch that does not exist yet.
        if (Has(graph, VfxAttribute.Age)) {
            text.AppendLine()
                .AppendLine("        age[slot] = age[slot] + deltaTime");
        }

        for (var index = 0; index < updaters.Length; index++) {
            text.AppendLine();
            Apply(text, updaters[index], "deltaTime", index);
        }
    }

    /// <summary>One initializer, as the statement that writes its attribute.</summary>
    static void Initialize(StringBuilder text, VfxOperation operation) {
        var salt = operation.Salt;

        switch (operation.Opcode) {
            case VfxOpcode.SetPosition: {
                text.AppendLine($"        position[slot] = {Padded(operation.A)}");

                break;
            }

            case VfxOpcode.PositionInSphere: {
                // The cube root is what makes it uniform by volume; a uniform radius piles two thirds
                // of the particles into the outer third and reads as a shell.
                text.AppendLine(
                    $"        position[slot] = float4({Vector(operation.A)} + Direction(particle, {salt}u) "
                    + $"* {Float(operation.A.W)} * pow(Value(particle, {salt + 2}u), 1f / 3f), 0f)"
                );

                break;
            }

            case VfxOpcode.PositionInBox: {
                text.AppendLine(
                    $"        position[slot] = float4({Between(operation.A.X, operation.B.X, salt)}, "
                    + $"{Between(operation.A.Y, operation.B.Y, salt + 1)}, "
                    + $"{Between(operation.A.Z, operation.B.Z, salt + 2)}, 0f)"
                );

                break;
            }

            case VfxOpcode.SetVelocity: {
                text.AppendLine($"        velocity[slot] = {Padded(operation.A)}");

                break;
            }

            case VfxOpcode.VelocityRandomDirection: {
                text.AppendLine(
                    $"        velocity[slot] = float4(Direction(particle, {salt}u) "
                    + $"* Range(particle, {salt + 2}u, {Float(operation.A.X)}, {Float(operation.A.Y)}), 0f)"
                );

                break;
            }

            case VfxOpcode.VelocityInCone: {
                var axis = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);

                if (axis.LengthSquared() <= 0f) {
                    throw new ArgumentException(
                        "A cone about a zero axis has no directions in it. VelocityInCone needs an axis to be about.",
                        nameof(operation)
                    );
                }

                // Normalized here rather than in the shader: the CPU backend normalizes with the same
                // library that folded this constant, so emitting the result is one fewer place the two
                // can round differently.
                text.AppendLine(
                    $"        velocity[slot] = float4(Cone({Vector(Vector3.Normalize(axis))}, {Float(operation.A.W)}, particle, {salt}u) "
                    + $"* Range(particle, {salt + 2}u, {Float(operation.B.X)}, {Float(operation.B.Y)}), 0f)"
                );

                break;
            }

            case VfxOpcode.SetLifetime: {
                text.AppendLine($"        lifetime[slot] = {Ranged(operation)}");

                break;
            }

            case VfxOpcode.SetSize: {
                text.AppendLine($"        size[slot] = {Ranged(operation)}");

                break;
            }

            case VfxOpcode.SetRotation: {
                text.AppendLine($"        rotation[slot] = {Ranged(operation)}");

                break;
            }

            case VfxOpcode.SetAngularVelocity: {
                text.AppendLine($"        angularVelocity[slot] = {Ranged(operation)}");

                break;
            }

            case VfxOpcode.SetColour: {
                text.AppendLine($"        colour[slot] = {Colour(operation.A)}");

                break;
            }

            default: {
                throw new ArgumentException($"`{operation.Opcode}` is neither an initializer nor an updater.", nameof(operation));
            }
        }
    }

    /// <summary>One updater, as the statement that advances its attribute by a step.</summary>
    /// <param name="text">Where the source is going.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="step">
    ///     The step to advance by — the uniform in the update kernel, and the literal zero in the
    ///     initializer one, which is the same distinction <see cref="VfxSimulation" /> draws by passing
    ///     a delta of zero.
    /// </param>
    /// <param name="index">
    ///     Where the operation sits in its list, which is what keeps two copies of one field from
    ///     declaring the same local twice. A field needs a distance before it can use one, and a
    ///     distance needs a name.
    /// </param>
    static void Apply(StringBuilder text, VfxOperation operation, string step, int index) {
        switch (operation.Opcode) {
            case VfxOpcode.Integrate: {
                text.AppendLine($"        position[slot] = position[slot] + float4(velocity[slot].xyz * {step}, 0f)");

                break;
            }

            case VfxOpcode.Gravity: {
                text.AppendLine($"        velocity[slot] = velocity[slot] + float4({Vector(operation.A)} * {step}, 0f)");

                break;
            }

            case VfxOpcode.Drag: {
                // Exponential, so a large step cannot reverse the particle the way `v *= 1 - k dt`
                // does once k dt passes one.
                text.AppendLine($"        velocity[slot] = velocity[slot] * exp({Float(-operation.A.X)} * {step})");

                break;
            }

            case VfxOpcode.Rotate: {
                text.AppendLine($"        rotation[slot] = rotation[slot] + angularVelocity[slot] * {step}");

                break;
            }

            case VfxOpcode.SizeOverLife: {
                text.AppendLine(
                    $"        size[slot] = lerp({Float(operation.A.X)}, {Float(operation.A.Y)}, "
                    + "Fraction(age[slot], lifetime[slot]))"
                );

                break;
            }

            case VfxOpcode.ColourOverLife: {
                text.AppendLine(
                    $"        colour[slot] = lerp({Colour(operation.A)}, {Colour(operation.B)}, "
                    + "Fraction(age[slot], lifetime[slot]))"
                );

                break;
            }

            case VfxOpcode.Attract: {
                // The zero-distance guard is not optional: normalizing the offset of a particle
                // sitting exactly on the centre is how an effect fills with NaNs, and one NaN in a
                // position is a quad the rasteriser drops and a bounding box that swallows the scene.
                text.AppendLine($"        val offset{index} = {Vector(operation.A)} - position[slot].xyz")
                    .AppendLine($"        val distance{index} = length(offset{index})")
                    .AppendLine()
                    .AppendLine($"        if (distance{index} > 0f) {{")
                    .AppendLine(
                        $"            velocity[slot] = velocity[slot] + float4(offset{index} / distance{index} * "
                        + $"{Float(operation.A.W)} * {step} * Falloff(distance{index}, {Float(operation.B.X)}), 0f)"
                    )
                    .AppendLine("        }");

                break;
            }

            case VfxOpcode.Vortex: {
                var axis = new Vector3(operation.B.X, operation.B.Y, operation.B.Z);

                if (axis.LengthSquared() <= 0f) {
                    throw new ArgumentException("A vortex about a zero axis has nothing to turn about.", nameof(operation));
                }

                // The component along the axis is taken out before the cross product, or the swirl
                // weakens with height above the centre for no reason anybody chose.
                text.AppendLine($"        val spin{index} = position[slot].xyz - {Vector(operation.A)}")
                    .AppendLine($"        val axis{index} = {Vector(Vector3.Normalize(axis))}")
                    .AppendLine($"        val radial{index} = spin{index} - axis{index} * dot(spin{index}, axis{index})")
                    .AppendLine($"        val around{index} = length(radial{index})")
                    .AppendLine()
                    .AppendLine($"        if (around{index} > 0f) {{")
                    .AppendLine(
                        $"            velocity[slot] = velocity[slot] + float4(cross(axis{index}, radial{index} / around{index}) * "
                        + $"{Float(operation.A.W)} * {step} * Falloff(around{index}, {Float(operation.B.W)}), 0f)"
                    )
                    .AppendLine("        }");

                break;
            }

            case VfxOpcode.Turbulence: {
                var octaves = Math.Clamp((int)operation.B.Y, 1, 4);
                var drift = $"time * {Float(operation.B.X)}";

                text.AppendLine(
                    $"        velocity[slot] = velocity[slot] + float4(Turbulence(position[slot].xyz * {Vector(operation.A)} "
                    + $"+ float3({drift}, {drift}, {drift}), {operation.Salt}u, {octaves}) * {Float(operation.A.W)} * {step}, 0f)"
                );

                break;
            }

            default: {
                throw new ArgumentException($"`{operation.Opcode}` is not an updater.", nameof(operation));
            }
        }
    }

    // --- Bindings ----------------------------------------------------------

    /// <summary>Which buffers the kernels touch, which is what the host has to bind.</summary>
    static VfxShaderBinding[] Bindings(VfxCompiledGraph graph, VfxOperation[] updaters) {
        var touched = VfxAttribute.None;
        var written = VfxAttribute.None;

        foreach (var operation in graph.Initializers.Concat(updaters)) {
            touched |= VfxOpcodes.Reads(operation.Opcode) | VfxOpcodes.Writes(operation.Opcode);
            written |= VfxOpcodes.Writes(operation.Opcode);

            if (VfxOpcodes.IsRandom(operation.Opcode)) {
                touched |= VfxAttribute.Identifier;
            }
        }

        // Age belongs to the runtime rather than to any operation: the initializer kernel zeroes it
        // and the update kernel advances it, both without anything in the graph saying so.
        if (Has(graph, VfxAttribute.Age)) {
            touched |= VfxAttribute.Age;
            written |= VfxAttribute.Age;
        }

        List<VfxShaderBinding> bindings = [];

        foreach (var attribute in VfxAttributes.All) {
            if ((touched & attribute) == 0) {
                continue;
            }

            bindings.Add(new(Name(attribute), attribute, Stride(attribute), (written & attribute) != 0));
        }

        return [.. bindings];
    }

    /// <summary>What one particle of an attribute occupies in a storage buffer, under std430.</summary>
    /// <remarks>
    ///     <b>Sixteen for a three-component vector, not twelve.</b> std430 aligns a <c>vec3</c> to
    ///     sixteen bytes, so an array of them has a stride of sixteen whatever it is declared as — and
    ///     a host that uploaded a packed <c>Vector3[]</c> would have every particle after the first
    ///     read from the wrong offset. The emitted buffer is therefore <c>float4</c>: it costs the
    ///     bytes the layout was going to spend anyway, and it spends them somewhere a later attribute
    ///     can use rather than in padding nothing can name.
    /// </remarks>
    static int Stride(VfxAttribute attribute) => VfxAttributes.TypeOf(attribute) switch {
        VfxAttributeType.Float3 or VfxAttributeType.Float4 => 16,
        _ => 4
    };

    /// <summary>What an attribute is declared as in the shader.</summary>
    static string Element(VfxAttribute attribute) => VfxAttributes.TypeOf(attribute) switch {
        VfxAttributeType.Float3 or VfxAttributeType.Float4 => "float4",
        VfxAttributeType.UInt => "uint",
        _ => "float"
    };

    /// <summary>What an attribute's buffer is called.</summary>
    static string Name(VfxAttribute attribute) => attribute switch {
        VfxAttribute.Position => "position",
        VfxAttribute.Velocity => "velocity",
        VfxAttribute.Size => "size",
        VfxAttribute.Colour => "colour",
        VfxAttribute.Lifetime => "lifetime",
        VfxAttribute.Age => "age",
        VfxAttribute.Rotation => "rotation",
        VfxAttribute.AngularVelocity => "angularVelocity",
        VfxAttribute.Identifier => "identifier",
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Not a single known attribute.")
    };

    // --- Spelling ----------------------------------------------------------

    /// <summary>A uniform draw between two ends, written the way the CPU backend writes it.</summary>
    /// <remarks>
    ///     The emitted <c>Range</c> is <c>a + (b - a) * t</c> and not <c>lerp(a, b, t)</c>, because that
    ///     is the arithmetic <see cref="VfxRandom.Range" /> does and the three forms of an interpolation
    ///     do not agree in the last bit. Where the CPU backend calls a library <c>Lerp</c> instead —
    ///     size and colour over life — this emits <c>lerp</c>, which is as close as it can get:
    ///     <c>float.Lerp</c> is a fused <c>a (1 - t) + b t</c>, and whether the target contracts its
    ///     <c>mix</c> the same way is the implementation's to decide.
    /// </remarks>
    static string Ranged(VfxOperation operation) =>
        $"Range(particle, {operation.Salt}u, {Float(operation.A.X)}, {Float(operation.A.Y)})";

    /// <summary>One component of a box, drawn between its two faces.</summary>
    static string Between(float minimum, float maximum, uint salt) =>
        $"Range(particle, {salt}u, {Float(minimum)}, {Float(maximum)})";

    /// <summary>The first three components, as a <c>float3</c>.</summary>
    static string Vector(Vector4 value) => Vector(new Vector3(value.X, value.Y, value.Z));

    /// <summary>A vector, as a <c>float3</c>.</summary>
    static string Vector(Vector3 value) => $"float3({Float(value.X)}, {Float(value.Y)}, {Float(value.Z)})";

    /// <summary>The first three components in a <c>float4</c> whose fourth lane is unused.</summary>
    static string Padded(Vector4 value) => $"float4({Float(value.X)}, {Float(value.Y)}, {Float(value.Z)}, 0f)";

    /// <summary>All four components.</summary>
    static string Colour(Vector4 value) =>
        $"float4({Float(value.X)}, {Float(value.Y)}, {Float(value.Z)}, {Float(value.W)})";

    /// <summary>A float as a Raven literal that reads back as the same float.</summary>
    /// <remarks>
    ///     Round-tripping is the whole job. A parameter written with fewer digits than it has would
    ///     make the GPU path run a subtly different effect from the CPU one, which is the failure this
    ///     module is least able to detect — it looks like the effect, only wrong.
    /// </remarks>
    static string Float(float value) {
        if (!float.IsFinite(value)) {
            throw new ArgumentException($"{value} cannot be written as a shader literal.", nameof(value));
        }

        // "R" round-trips exactly; the exponent it sometimes produces is a form the Raven lexer takes,
        // in lower case for the same reason everything else generated here is: so a golden test
        // compares one spelling.
        return value.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal) + "f";
    }

    // --- Small questions ---------------------------------------------------

    /// <summary>Whether the graph stores an attribute.</summary>
    static bool Has(VfxCompiledGraph graph, VfxAttribute attribute) => (graph.Attributes & attribute) != 0;

    /// <summary>Whether either list holds an opcode.</summary>
    static bool Touches(VfxCompiledGraph graph, VfxOpcode opcode) =>
        graph.Initializers.Any(operation => operation.Opcode == opcode)
        || graph.Updaters.Any(operation => operation.Opcode == opcode);

    /// <summary>Whether a name can be a Raven identifier.</summary>
    static bool IsIdentifier(string name) =>
        (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsLetterOrDigit(character) || character == '_');
}
