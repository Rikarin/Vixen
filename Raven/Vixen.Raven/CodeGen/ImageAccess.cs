// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen;

/// <summary>
///     Which storage images a stage reads and which it writes, over its reachable code.
/// </summary>
/// <remarks>
///     <para>
///         <b>What it buys.</b> An image a stage only stores into is <c>writeonly</c> in GLSL and
///         <c>NonReadable</c> in SPIR-V, and the reverse for one it only loads. A driver uses the
///         decoration to skip a read-after-write hazard on that binding and <c>spirv-opt</c> reads
///         it; GLSL ES needs it outright, since ES lets an image be both read and written only when
///         its format is one of <c>r32f</c>, <c>r32i</c>, <c>r32ui</c>. Raven's own GLSL is what the
///         GL backend compiles, so this is not something a downstream translator can supply for it.
///     </para>
///     <para>
///         ⚠ <b>Per (entry point, binding), not per binding</b>, which is why this is asked of the
///         stage rather than of the shader. A <c>compose</c>d implementation may store into an image
///         another stage only loads, and a binding is declared in every stage's module whether that
///         stage touches it or not — so the shader-wide answer would be "both" for every image any
///         stage writes, which is no answer.
///     </para>
///     <para>
///         ⚠ <b>What it is not: an ES fix for the three shaders that fail there.</b>
///         <c>IrradianceFill</c>, <c>IrradianceRepair</c> and <c>ImpostorFinish</c> genuinely load
///         <em>and</em> store the same image, so no decoration can be honest about them and they
///         stay on <c>CrossCompilationTests.OwedAtEs320</c>. #476 said each of them "only ever
///         <c>Store</c>s", and that is not what they do.
///     </para>
/// </remarks>
static class ImageAccess {
    /// <summary>What one stage does to the storage images it can reach.</summary>
    /// <param name="Read">Images some reachable <c>Load</c> reads.</param>
    /// <param name="Written">Images some reachable <c>Store</c> writes.</param>
    /// <param name="Traced">
    ///     Whether every image operation's receiver was resolved to a declaration.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>Traced</c> is the instrument's own check and it decides the whole result. An
    ///         image handed to a helper reaches the operation through the <em>parameter</em>, and a
    ///         decoration derived from an incomplete picture is worse than none: a stage that stores
    ///         into an image directly and passes the same image to a helper that loads it would look
    ///         write-only, and <c>NonReadable</c> on an image something reads is a module
    ///         <c>spirv-val</c> accepts and a driver may act on. So false means "decorate nothing",
    ///         for every image in the stage.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The trap this sits on, found by sabotaging it.</b> A parameter's load looks
    ///         exactly like a global's — same instruction, same storage-image type — so a walk that
    ///         asked only about the type recorded the parameter as if it were the binding, never set
    ///         <c>Traced</c> false, and was green under a sabotage that removed the flag entirely.
    ///         <c>IrVariableKind.Global</c> is what separates the two, and nothing else does. No
    ///         shader in <c>Raven/Library</c> passes a storage image to a function today, so this is
    ///         a floor rather than something the engine's shaders stand on.
    ///     </para>
    /// </remarks>
    public sealed record Access(
        IReadOnlySet<IrVariable> Read,
        IReadOnlySet<IrVariable> Written,
        bool Traced
    ) {
        /// <summary>Whether this stage may declare a binding as write-only.</summary>
        /// <param name="group">
        ///     Every declaration the binding plan collapsed into this one binding.
        /// </param>
        /// <remarks>
        ///     ⚠ The whole alias group, not one variable. A shared binding is one descriptor named
        ///     by several composed features, and each feature's body refers to <em>its own</em>
        ///     variable — so asking about only the one the emitter happens to be declaring would
        ///     call a binding write-only while another feature loads it.
        /// </remarks>
        public bool IsWriteOnly(IEnumerable<IrVariable> group) {
            ArgumentNullException.ThrowIfNull(group);
            var all = group.ToArray();

            return Traced && all.Any(Written.Contains) && !all.Any(Read.Contains);
        }

        /// <summary>Whether this stage may declare a binding as read-only.</summary>
        /// <param name="group">
        ///     Every declaration the binding plan collapsed into this one binding.
        /// </param>
        public bool IsReadOnly(IEnumerable<IrVariable> group) {
            ArgumentNullException.ThrowIfNull(group);
            var all = group.ToArray();

            return Traced && all.Any(Read.Contains) && !all.Any(Written.Contains);
        }
    }

    /// <summary>Walks a stage's reachable code and reports what it does to each storage image.</summary>
    public static Access Of(IrEntryPoint entryPoint) {
        ArgumentNullException.ThrowIfNull(entryPoint);

        HashSet<IrVariable> read = [];
        HashSet<IrVariable> written = [];
        var traced = true;

        Walk(entryPoint.Function.Body, [], []);

        return new(read, written, traced);

        void Note(IrValue receiver, HashSet<IrVariable> into, Dictionary<int, IrVariable> images) {
            if (images.TryGetValue(receiver.Id, out var image)) {
                into.Add(image);
            } else {
                traced = false;
            }
        }

        // ⚠ `images` is per function, not per walk. A value id is unique inside the function that
        // defines it and nowhere else — the SPIR-V emitter clears its own map at every
        // `EmitFunction` for the same reason — so one map across an expanded call graph would let a
        // helper's id collide with its caller's and name the wrong binding.
        void Walk(IrStatement statement, HashSet<IrFunction> active, Dictionary<int, IrVariable> images) {
            switch (statement) {
                case IrBlock block:
                    foreach (var nested in block.Statements) {
                        Walk(nested, active, images);
                    }

                    break;

                // ⚠ `Kind: Global` is load-bearing. A storage-image parameter is loaded by the same
                // instruction with the same type, so without it a helper's parameter is recorded as
                // if it were the binding — and the untraced case then never announces itself.
                case IrLoadInstruction {
                    Place: { Chain.Count: 0, Root: { Kind: IrVariableKind.Global, Type: IrStorageImageType } } place
                } load:
                    images[load.Result.Id] = place.Root;
                    break;

                case IrIntrinsicInstruction { Intrinsic: IrIntrinsic.LoadImage } intrinsic
                    when intrinsic.Arguments.Count > 0:
                    Note(intrinsic.Arguments[0], read, images);
                    break;

                case IrIntrinsicInstruction { Intrinsic: IrIntrinsic.StoreImage } intrinsic
                    when intrinsic.Arguments.Count > 0:
                    Note(intrinsic.Arguments[0], written, images);
                    break;

                // A size query is neither, and saying so is the point: an image nothing but
                // `GetDimensions` touches is legally `readonly writeonly`, which is exactly what
                // GLSL's two qualifiers together mean.
                case IrIntrinsicInstruction { Intrinsic: IrIntrinsic.ImageSize }:
                    break;

                // Expanded at the call site, for the reason `Lowerer.ResolveStreamDirections` gives:
                // a stage's code is what it can reach, and a composed implementation's functions
                // live in another shader entirely.
                case IrCallInstruction call when active.Add(call.Function):
                    try {
                        Walk(call.Function.Body, active, []);
                    } finally {
                        active.Remove(call.Function);
                    }

                    break;

                case IrIfStatement conditional:
                    Walk(conditional.Then, active, images);

                    if (conditional.Else is { } otherwise) {
                        Walk(otherwise, active, images);
                    }

                    break;

                case IrLoopStatement loop:
                    Walk(loop.Condition, active, images);
                    Walk(loop.Body, active, images);

                    if (loop.Continue is { } step) {
                        Walk(step, active, images);
                    }

                    break;
            }
        }
    }
}
