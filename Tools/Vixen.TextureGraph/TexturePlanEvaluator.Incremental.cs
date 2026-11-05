// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.TextureGraph;

public sealed partial class TexturePlanEvaluator {
    /// <summary>The library every kernel compiles with, as a digest.</summary>
    static readonly UInt128 PreludeIdentity = DigestPrelude();

    /// <summary>The results this evaluator has kept, which only the incremental path reads.</summary>
    /// <remarks>
    ///     On the evaluator rather than beside it, so that it has the evaluator's lifetime: a host
    ///     lends one evaluator per device, and a result is a texture on that device.
    /// </remarks>
    public TextureResultCache Results { get; }

    /// <summary>Evaluates what changed since the results it can reuse were made.</summary>
    /// <param name="plan">What to run.</param>
    /// <param name="externals">
    ///     The caller's textures, as for
    ///     <see cref="Evaluate(TexturePlan,IReadOnlyDictionary{int,TextureExternal})" />. One that
    ///     declares a <see cref="TextureExternal.Content" /> can be cached through; one that does not
    ///     makes everything downstream of it run.
    /// </param>
    /// <returns>
    ///     A bake that reads like any other, and whose textures belong to <see cref="Results" />: they
    ///     are held until it is disposed and are not the caller's to destroy.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan" /> is null.</exception>
    /// <exception cref="ArgumentException">The plan is unsound, or an external image was not supplied.</exception>
    /// <exception cref="ObjectDisposedException">The evaluator has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device already has a frame open.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The same evaluator, not a second one.</b> Doc 48 § D3 allows one, and this is
    ///         <see cref="Run" /> with a schedule that gives a slot only to the ops that have to run.
    ///         A result that already exists goes in beside the caller's own images, which is what it
    ///         is for the length of this evaluation: an input somebody else wrote, already readable.
    ///     </para>
    ///     <para>
    ///         <b>Demand runs backwards from <see cref="TexturePlan.Outputs" />.</b> An image is wanted
    ///         because the plan names it or because an op that has to run reads it, and a result in
    ///         the cache ends the walk: nothing above a hit is even looked at. Editing one node
    ///         therefore runs that node and what depends on it, and an op whose key did not change
    ///         does not run wherever it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A key is computed for every op on every evaluation, so a kernel the plan names is
    ///         compiled even if it is answered from the cache.</b> The key reads the uniform block as
    ///         the kernel declares it, which is reflection, and that is what lets it hash what the GPU
    ///         would actually receive: a parameter the kernel ignores cannot cost a miss, an
    ///         <c>int</c> uniform that truncates to the same number cannot either, and a kernel with
    ///         no <c>seed</c> member does not re-run when the seed moves. A key that hashed
    ///         <see cref="TextureOp.Parameters" /> directly would be cheaper once and wrong about all
    ///         three.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An op with no reader that matters does not run here and does on the whole-plan
    ///         path</b>, so <see cref="TextureBake.Dispatches" /> is not comparable between the two.
    ///         Every image either can return is byte-identical on one device, which is what
    ///         <c>TextureIncrementalPropertyTests</c> holds it to.
    ///     </para>
    /// </remarks>
    public TextureBake EvaluateIncremental(
        TexturePlan plan,
        IReadOnlyDictionary<int, TextureExternal> externals
    ) {
        var problems = Admit(plan, externals);
        var keys = KeysOf(plan, externals);
        var decision = Decide(plan, keys);
        var cautions = Cautions(problems, plan, externals);

        foreach (var entry in decision.Reads.Values) {
            TextureResultCache.Pin(entry);
        }

        var kept = Keep(plan, decision);
        var schedule = TexturePoolSchedule.For(plan, decision.Runs, kept);
        var rented = new TextureResultCache.Rented?[schedule.Allocations];
        List<TextureViewHandle> owned = [];
        List<TextureResultCache.Entry> pinned = [.. decision.Reads.Values];

        void Release() {
            foreach (var entry in pinned) {
                Results.Unpin(entry);
            }

            foreach (var left in rented) {
                if (left is { } texture) {
                    Results.Return(texture);
                }
            }

            pinned.Clear();
            Array.Clear(rented);
            Results.Trim();
        }

        var bake = new TextureBake(
            device,
            device.ComputeQueue,
            plan,
            schedule,
            new TextureHandle[schedule.Allocations],
            owned,
            Release
        ) {
            Warnings = cautions,
            Keys = [.. keys],
            Reused = decision.Reused
        };

        try {
            // ⚠ Reclaim, rent, trim, in that order: an idle texture of the right shape is free, so the
            // oldest results are offered to this run before anything is created or destroyed.
            Results.Reclaim(schedule.Bytes);

            for (var slot = 0; slot < rented.Length; slot++) {
                var name = string.Create(CultureInfo.InvariantCulture, $"texture graph result {slot}");

                rented[slot] = Results.Rent(schedule.Slots[slot], name);
                bake.Fill(slot, rented[slot]!.Value.Texture);
            }

            Results.Trim(schedule.Bytes);

            var views = ExternalViews(plan, externals, owned);
            var supplied = new Dictionary<int, TextureExternal>(externals);

            foreach (var (image, entry) in decision.Reads) {
                views[image] = entry.View;
                supplied[image] = new(entry.Texture, PooledUsage, plan.SizeOf(image));
            }

            if (rented.Length > 0) {
                var slotViews = new TextureViewHandle[rented.Length];

                for (var slot = 0; slot < rented.Length; slot++) {
                    slotViews[slot] = rented[slot]!.Value.View;
                }

                Run(plan, schedule, bake, slotViews, views, supplied);
            }

            bake.Ran = [.. Enumerable.Range(0, plan.Ops.Length).Where(index => decision.Runs[index])];

            // ⚠ After the run and not before it. A result that is filed is a result the next
            // evaluation will trust, and a run that threw has filed nothing.
            bake.Settle(File(plan, schedule, keys, kept, decision, rented, pinned));
            Results.Tally(decision.Reused, bake.Ran.Length);
            Results.Trim();
        } catch {
            bake.Dispose();

            throw;
        }

        return bake;
    }

    /// <summary>Which images of a run stay in the cache, as a mask over the image table.</summary>
    /// <remarks>
    ///     ⚠ <b>Outputs always, and the rest while they fit.</b> A forty-op bake at 4K has no business
    ///     keeping forty 128 MB intermediates because it was asked for one map, and the pool exists to
    ///     not do that. So what the budget cannot hold is pooled as the whole-plan path would pool
    ///     it, and the next edit re-runs it.
    /// </remarks>
    bool[] Keep(TexturePlan plan, Decision decision) {
        var kept = new bool[plan.Images.Length];
        var room = Results.Budget;

        foreach (var entry in decision.Reads.Values) {
            room -= TextureResultCache.BytesOf(entry.Shape);
        }

        foreach (var output in plan.Outputs) {
            if (plan.Images[output].External) {
                continue;
            }

            kept[output] = true;

            if (!decision.Reads.ContainsKey(output)) {
                room -= TextureResultCache.BytesOf(ShapeOf(plan, output));
            }
        }

        for (var index = 0; index < plan.Ops.Length; index++) {
            var image = plan.Ops[index].Output;

            if (!decision.Runs[index] || kept[image]) {
                continue;
            }

            var bytes = TextureResultCache.BytesOf(ShapeOf(plan, image));

            if (bytes <= room) {
                kept[image] = true;
                room -= bytes;
            }
        }

        return kept;
    }

    /// <summary>Hands a finished run's textures to the cache, and says what the bake may read.</summary>
    Dictionary<int, TextureHandle> File(
        TexturePlan plan,
        TexturePoolSchedule schedule,
        TextureResultKey[] keys,
        bool[] kept,
        Decision decision,
        TextureResultCache.Rented?[] rented,
        List<TextureResultCache.Entry> pinned
    ) {
        Dictionary<int, TextureHandle> readable = [];

        foreach (var (image, entry) in decision.Reads) {
            readable[image] = entry.Texture;
        }

        for (var index = 0; index < plan.Ops.Length; index++) {
            var image = plan.Ops[index].Output;

            if (!decision.Runs[index] || !kept[image]) {
                continue;
            }

            // ⚠ A kept image is never freed, so the slot it ended in is its alone from there on.
            var slot = schedule.SlotOf[image];
            var entry = Results.Add(keys[image], rented[slot]!.Value);

            rented[slot] = null;
            pinned.Add(entry);
            readable[image] = entry.Texture;
        }

        return readable;
    }

    /// <summary>Which ops have to run, and which results they and the caller will read.</summary>
    Decision Decide(TexturePlan plan, TextureResultKey[] keys) {
        var wanted = new bool[plan.Images.Length];
        var runs = new bool[plan.Ops.Length];
        var found = new TextureResultCache.Entry?[plan.Ops.Length];
        Dictionary<int, TextureResultCache.Entry> reads = [];
        var reused = 0;

        foreach (var output in plan.Outputs) {
            wanted[output] = true;
        }

        for (var index = plan.Ops.Length - 1; index >= 0; index--) {
            var op = plan.Ops[index];
            var entry = Results.Find(keys[op.Output], ShapeOf(plan, op.Output));

            found[index] = entry;

            if (entry is not null) {
                reused++;
            }

            if (!wanted[op.Output]) {
                continue;
            }

            if (entry is not null) {
                reads[op.Output] = entry;

                continue;
            }

            runs[index] = true;

            foreach (var input in op.Inputs) {
                wanted[input] = true;
            }
        }

        // In plan order rather than the walk's, so that an image and its readers age together and
        // the oldest thing in the current plan is the first to go if it all has to.
        foreach (var entry in found) {
            if (entry is not null) {
                Results.Touch(entry);
            }
        }

        return new(runs, reads, reused);
    }

    /// <summary>The key of every image in a plan.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every length is hashed before what it counts, and the order is fixed.</b> The
    ///         ingredients are concatenated, so a list that could slide into its neighbour would be
    ///         two different ops with one key.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not cached across evaluations, by anything.</b> It is a hash per op, and what it
    ///         would save is small against what a stale key costs.
    ///     </para>
    /// </remarks>
    TextureResultKey[] KeysOf(TexturePlan plan, IReadOnlyDictionary<int, TextureExternal> externals) {
        var keys = new TextureResultKey[plan.Images.Length];
        var parts = Results.Parts;

        for (var image = 0; image < keys.Length; image++) {
            if (plan.Images[image].External) {
                keys[image] = ExternalKey(plan, image, externals, parts);
            }
        }

        for (var index = 0; index < plan.Ops.Length; index++) {
            var op = plan.Ops[index];
            var hash = new XxHash128();
            var shape = ShapeOf(plan, op.Output);

            if (parts.HasFlag(TextureKeyParts.Format)) {
                Append(hash, (int)shape.Format);
            }

            if (parts.HasFlag(TextureKeyParts.Resolution)) {
                Append(hash, shape.Width);
                Append(hash, shape.Height);
            }

            var cacheable = op.Cpu is null
                ? DispatchIngredients(plan, index, hash, parts)
                : CpuIngredients(plan, index, hash, parts);

            if (parts.HasFlag(TextureKeyParts.Inputs)) {
                Append(hash, op.Inputs.Length);

                foreach (var input in op.Inputs) {
                    Append(hash, keys[input].Value);
                }
            }

            keys[op.Output] = cacheable ? new(hash.GetCurrentHashAsUInt128()) : Results.Nonce();
        }

        return keys;
    }

    /// <summary>What a key says about one image the caller supplied.</summary>
    TextureResultKey ExternalKey(
        TexturePlan plan,
        int image,
        IReadOnlyDictionary<int, TextureExternal> externals,
        TextureKeyParts parts
    ) {
        if (!externals.TryGetValue(image, out var supplied)) {
            return Results.Nonce();
        }

        if (!parts.HasFlag(TextureKeyParts.Content)) {
            return new(UInt128.One);
        }

        if (!supplied.Content.IsValid) {
            return Results.Nonce();
        }

        var hash = new XxHash128();

        Append(hash, supplied.Content.Value);
        Append(hash, (int)plan.Images[image].Format);
        Append(hash, supplied.Size.X);
        Append(hash, supplied.Size.Y);

        return new(hash.GetCurrentHashAsUInt128());
    }

    /// <summary>What a key says about a dispatch: its source, and its uniform block as the GPU reads it.</summary>
    /// <returns>Always true: a kernel is a function of what is hashed here.</returns>
    bool DispatchIngredients(TexturePlan plan, int index, XxHash128 hash, TextureKeyParts parts) {
        var op = plan.Ops[index];
        var variant = VariantFor(plan, op.Kernel, plan.Images[op.Output].Format);

        if (parts.HasFlag(TextureKeyParts.Kernel)) {
            Append(hash, variant.Identity.Value);
        }

        foreach (var binding in variant.Effect.Bindings) {
            if (binding.Set != DescriptorSetSlot.PerMaterial
                || binding.Kind is not (DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer)) {
                continue;
            }

            var block = Uniforms(plan, index, variant, binding.Size);

            foreach (var member in variant.Data.Parameters) {
                if (member.Set != DescriptorSetSlot.PerMaterial) {
                    continue;
                }

                // The same test `Uniforms` makes: an op that carries a parameter called seed has
                // authored a number, and only a seed the evaluator supplied is the plan's.
                var name = Unqualified(member.Name, variant.Data.ShaderName);
                var isSeed = op.Find(name) is null && string.Equals(name, "seed", StringComparison.Ordinal);

                if (parts.HasFlag(isSeed ? TextureKeyParts.Seed : TextureKeyParts.Parameters)) {
                    hash.Append(block.AsSpan(member.Offset, member.Size));
                }
            }
        }

        return true;
    }

    /// <summary>What a key says about a CPU op, which is only what the op has promised it may.</summary>
    /// <returns>Whether the operation declares an identity at all.</returns>
    static bool CpuIngredients(TexturePlan plan, int index, XxHash128 hash, TextureKeyParts parts) {
        var op = plan.Ops[index];

        if (op.Cpu!.CacheIdentity is not { } identity) {
            return false;
        }

        if (parts.HasFlag(TextureKeyParts.Kernel)) {
            hash.Append(Encoding.UTF8.GetBytes(identity));
        }

        if (parts.HasFlag(TextureKeyParts.Parameters)) {
            Append(hash, op.Parameters.Length);

            foreach (var parameter in op.Parameters) {
                hash.Append(Encoding.UTF8.GetBytes(parameter.Name));
                Append(hash, plan.Resolve(index, parameter));
            }
        }

        if (parts.HasFlag(TextureKeyParts.Seed)) {
            Append(hash, plan.SeedFor(index));
        }

        return true;
    }

    static TexturePoolSlot ShapeOf(TexturePlan plan, int image) {
        var size = plan.SizeOf(image);

        return new(plan.Images[image].Format, size.X, size.Y);
    }

    /// <summary>What a kernel is, as a number.</summary>
    /// <remarks>
    ///     ⚠ <b>The source rather than a version somebody bumps</b>, so there is nothing to forget, and
    ///     the library with it: <c>Random.Hash</c> changing changes every <c>Noise</c>. ⚠ <b>The text
    ///     before <see cref="TextureKernels.Variant(string,string,TextureFormat)" /> rewrites the
    ///     format into it</b>, because hashing the rewritten text would put the format in here twice
    ///     and leave <see cref="TextureKeyParts.Format" /> with nothing to prove.
    /// </remarks>
    static TextureResultKey SourceIdentity(string source) {
        var hash = new XxHash128();

        Append(hash, PreludeIdentity);
        hash.Append(Encoding.UTF8.GetBytes(source));

        return new(hash.GetCurrentHashAsUInt128());
    }

    static UInt128 DigestPrelude() {
        var hash = new XxHash128();

        foreach (var (name, text) in TextureKernelPrelude.Sources) {
            hash.Append(Encoding.UTF8.GetBytes(name));
            hash.Append(Encoding.UTF8.GetBytes(text));
        }

        return hash.GetCurrentHashAsUInt128();
    }

    static void Append<T>(XxHash128 hash, T value) where T : unmanaged =>
        hash.Append(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>The outcome of looking a plan's keys up.</summary>
    /// <param name="Runs">Which ops have to run, by index.</param>
    /// <param name="Reads">The results somebody will read, by image.</param>
    /// <param name="Reused">How many of the plan's ops have a result held.</param>
    readonly record struct Decision(
        bool[] Runs,
        Dictionary<int, TextureResultCache.Entry> Reads,
        int Reused
    );
}
