// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     The ripple field on the device: one dispatch a step over a ping-pong pair.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry),
///         the device half.</b> <see cref="WaterRipples" /> is the CPU reference and the thing a test
///         measures against; this is what a frame actually runs, and
///         <c>WaterRippleSeamTests</c> is what holds the two together.
///     </para>
///     <para>
///         ⚠ <b>The seam's tolerance is stated in metres and is <em>not</em> exact-to-the-float, and
///         that asymmetry is the design rather than a concession.</b> The closed-form wave sum in
///         <c>Surface.rvn</c> is held exactly, because a rollback re-asks it at a time it never
///         simulated — it is an expression, and two evaluations of one expression agree. A height
///         field's state <em>is</em> its history: there is no expression to compare, only a
///         trajectory, and two trajectories that begin together diverge at whatever rate the
///         arithmetic differs. Which is why the ripple contribution is a separate argument to
///         <see cref="WaterEvaluator" /> and the network path passes none at all.
///     </para>
///     <para>
///         ⚠ <b>Height and rate in one texture's <c>rg</c>, not two pairs.</b> Two ping-pongs is four
///         textures and two chances for one to be advanced without the other — which is a simulation
///         reading this step's height with last step's rate, and it is <em>stable</em>, so it does not
///         announce itself.
///     </para>
///     <para>
///         ⚠ <b>§ B5: <c>glBindImageTexture</c> is ⬜ in the GL backend</b>, so the shader carries a
///         <c>Compute</c> permutation and its off variant is the same arithmetic writing a colour
///         target. <see cref="Compute" /> is what a host sets from the backend it opened.
///     </para>
/// </remarks>
public sealed class WaterRippleSimulation : IDisposable {
    /// <summary>How many disturbances one step applies, which is the shader's array length.</summary>
    /// <remarks>
    ///     ⚠ <b>The cap is the budget, and § D12 says why there is one at all.</b> An unbounded number
    ///     of sources is how this feature becomes a frame-time cliff, and thirty-two distance tests
    ///     per texel is what a step costs when every one is used. What does not fit is counted into
    ///     <see cref="Overflowed" /> rather than dropped in silence.
    /// </remarks>
    public const int MaxInjections = 32;

    /// <summary>How many texels a compute group covers along each axis — the shader's own.</summary>
    public const int GroupSize = 8;

    /// <summary>Which descriptor set the shader's own bindings live in.</summary>
    /// <remarks>
    ///     Per-material, which is where a Raven shader's own declarations land — see the reflection's
    ///     <c>Set: 2</c>. A dispatch has no material; the slot is the layout's, not the concept's.
    /// </remarks>
    const int SetIndex = (int)DescriptorSetSlot.PerMaterial;

    readonly IGraphicsDevice device;
    readonly PingPongTextures textures;
    readonly Vector4[] injections = new Vector4[MaxInjections];

    DescriptorSetHandle[] sets = [];
    BufferHandle[] blocks = [];
    PipelineHandle pipeline;
    int ring;
    int stepsPerFrame = 4;
    int builtFor;
    bool built;
    bool disposed;

    /// <summary>Creates the pair and its state.</summary>
    /// <param name="device">The device it lives on.</param>
    /// <param name="settings">The simulation's window, rate and budget.</param>
    /// <param name="origin">Where the window's low corner is, on the ground plane.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <exception cref="ArgumentException">The settings cannot be simulated.</exception>
    /// <param name="readable">Whether the field can be copied back to the host.</param>
    public WaterRippleSimulation(
        IGraphicsDevice device,
        in WaterRippleSettings settings,
        Vector2 origin = default,
        bool readable = false
    ) {
        ArgumentNullException.ThrowIfNull(device);

        if (settings.Resolution < 2) {
            throw new ArgumentException(
                $"A ripple field of {settings.Resolution} texels has no neighbours to take a Laplacian "
                + "over. The CPU reference refuses the same case.",
                nameof(settings)
            );
        }

        this.device = device;

        Settings = settings;
        Origin = origin;

        textures = new(
            device,
            new(
                // ⚠ Half precision, and the quantum is what the seam tolerance is mostly made of. A
                // ripple is centimetres over a window of tens of metres, so the exponent does the
                // work and the absolute error is small where the displacement is; full precision
                // would double a per-zone allocation for a field nothing reads at range.
                //
                // ⚠ Four channels and not two, which is portability rather than waste. Vulkan's
                // *required* storage-image formats are a short list and Rg16Float is not on it — a
                // device without StorageImageExtendedFormats refuses the module outright, which is
                // what the seam fixture found on the first machine it ran on. Rgba16Float is required
                // everywhere, and `ba` are somewhere a future consumer can put the surface normal
                // without another texture.
                PixelFormat.Rgba16Float,
                settings.Resolution,
                settings.Resolution,

                // ColourTarget as well as Storage, because clearing is a render pass — see
                // PingPongTextures.Clear, which refuses a storage-only pair by name — and because
                // § B5's GL variant writes a colour target rather than a storage image.
                TextureUsage.Storage
                | TextureUsage.Sampled
                | TextureUsage.ColourTarget
                | (readable ? TextureUsage.CopySource : TextureUsage.None),
                Name: "water ripples"
            )
        );
    }

    /// <summary>The window, the rate and the budget.</summary>
    public WaterRippleSettings Settings { get; }

    /// <summary>Where the window's low corner is, on the ground plane.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>How many steps one submit may record, which is how long the descriptor ring is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not one, and the reason is what a fixed step <em>is</em>.</b> A set names the half
    ///         of the pair this step reads, and the halves swap — so a set rewritten while a submitted
    ///         command list still references it is the race the ping-pong exists to avoid, arriving
    ///         through the descriptor rather than through the texture. Sizing the ring by frames in
    ///         flight alone assumes one step a frame, and an accumulator catching up after a hitch
    ///         takes several: <c>FixedStepAccumulator.Advance</c> returns a count, and it is not
    ///         always one.
    ///     </para>
    ///     <para>
    ///         Four is a frame that lost sixty milliseconds and is catching up at 60 Hz, which is what
    ///         alt-tabbing looks like. The validation layer says this out loud when it is too small,
    ///         which is how the seam fixture found it.
    ///     </para>
    ///     <para>
    ///         ⚠ Changing it after the first step rebuilds every set — see <c>Resolve</c>.
    ///     </para>
    /// </remarks>
    public int StepsPerFrame {
        get => stepsPerFrame;
        set => stepsPerFrame = Math.Max(value, 1);
    }

    /// <summary>Whether this backend can write a storage image at all — § B5.</summary>
    /// <remarks>
    ///     ⚠ <b>A host's answer and not a guess.</b> Left true on a backend without
    ///     <c>glBindImageTexture</c>, the dispatch is recorded, validates, and writes nothing — a
    ///     simulation that runs at full cost and stays perfectly flat.
    /// </remarks>
    public bool Compute { get; set; } = true;

    /// <summary>How many disturbances this step has taken.</summary>
    public int Injections { get; private set; }

    /// <summary>And how many it had to refuse. ⚠ Non-zero is a wake some hulls do not make.</summary>
    public int Overflowed { get; private set; }

    /// <summary>How many steps have been dispatched.</summary>
    public long StepCount => textures.StepCount;

    /// <summary>The texture a consumer samples — the one the last step wrote.</summary>
    public TextureHandle Field => textures.ReadTexture;

    /// <summary>Whether anything has been written yet.</summary>
    /// <remarks>
    ///     ⚠ <b>False means the field holds uninitialised memory and nothing will say so</b> — see
    ///     <see cref="PingPongTextures.HasHistory" />. On most drivers that is zeroes, which is
    ///     exactly what a settled field looks like, so the bug survives to the first machine whose
    ///     driver hands back something else. <see cref="Clear" /> is the one decision it leaves.
    /// </remarks>
    public bool HasHistory => textures.HasHistory;

    /// <summary>Queues a disturbance for the next step.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="radius">How wide the bump is, in metres.</param>
    /// <param name="amount">How hard, in metres a second. Negative pushes down.</param>
    /// <returns>Whether it fitted inside the budget.</returns>
    /// <remarks>
    ///     ⚠ <b>Into the velocity, which <see cref="WaterRipples.Inject" /> says is the whole
    ///     difference between a wake and a hole</b>: a boat sitting still that pushed the height down
    ///     would carve a permanent dent in the lake, where one that pushes the rate down makes a
    ///     depression that springs back.
    /// </remarks>
    public bool Inject(Vector2 position, float radius, float amount) {
        if (Injections >= MaxInjections) {
            Overflowed++;

            return false;
        }

        var step = Settings.MetresPerTexel;
        var local = (position - Origin) / step;

        injections[Injections++] = new(local.X, local.Y, MathF.Max(radius, step), amount);

        return true;
    }

    /// <summary>Injects every disturbance a step produced.</summary>
    /// <param name="queue">The step's disturbances.</param>
    /// <returns>How many fitted inside this simulation's own budget.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="queue" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>It does not clear the queue</b> — see <see cref="WaterRipples.Apply" />, which is the
    ///     same method one implementation over and says why there are two consumers.
    /// </remarks>
    public int Apply(WaterDisturbances queue) {
        ArgumentNullException.ThrowIfNull(queue);

        var taken = 0;

        foreach (var disturbance in queue.Queued) {
            if (Inject(disturbance.Position, disturbance.Radius, disturbance.Strength)) {
                taken++;
            }
        }

        return taken;
    }

    /// <summary>Moves the window's low corner. ⚠ The contents do not move with it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unlike <see cref="WaterRipples.Follow" />, which shifts its contents by whole
    ///         texels.</b> Shifting a texture means a copy pass with a source and destination that
    ///         overlap, which is undefined — so a device window that moves is a device window that has
    ///         to be re-cleared, and what that costs is every wake in the scene.
    ///     </para>
    ///     <para>
    ///         Which is why this exists as a separate call and does not clear: the decision of when a
    ///         window has moved far enough to be worth losing its history is the host's, exactly as
    ///         the zone's scroll threshold is. See <see cref="Clear" />.
    ///     </para>
    /// </remarks>
    /// <param name="origin">The new low corner.</param>
    public void MoveTo(Vector2 origin) => Origin = origin;

    /// <summary>Declares a pass that clears the field, so the first read is defined.</summary>
    /// <param name="graph">The graph.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    public void Clear(RenderGraph graph) => textures.Clear(graph);

    /// <summary>Declares this step's dispatch.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="effects">Where the variant is resolved.</param>
    /// <param name="pipelines">Where the resolved variant becomes a compute pipeline.</param>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <returns>Whether a pass was declared.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    /// <exception cref="ArgumentException">The settings are not stable at that step.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The Courant condition is checked here and refused with a sentence</b>, exactly as
    ///         <see cref="WaterRipples.Step" /> refuses it. Past the limit an explicit wave equation
    ///         does not look wrong — it grows without bound in a few dozen steps and everything
    ///         downstream reads a NaN, which on a device is a whole frame of them with no stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves of the pair are imported whether or not the step touches them</b> —
    ///         see <see cref="PingPongTextures.Import" />. A graph told about only the one it writes
    ///         cannot place the barrier between this step's write and the next step's read of the same
    ///         texture, and an unsynchronised read produces a field that is one step stale in some
    ///         tiles and current in others, which reads as noise in the simulation rather than as a
    ///         race.
    ///     </para>
    /// </remarks>
    public bool Record(RenderGraph graph, EffectSystem effects, ComputePipelineCache pipelines, float deltaTime) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(pipelines);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Settings.Validate(deltaTime) is { } why) {
            throw new ArgumentException(why, nameof(deltaTime));
        }

        if (!Resolve(effects, pipelines)) {
            return false;
        }

        var pair = textures.Import(graph);
        var groups = (Settings.Resolution + GroupSize - 1) / GroupSize;

        // ⚠ The block is filled and the ring slot taken *here*, at declaration, not inside the pass
        // body. A graph executes long after it is declared, and the queue this step's injections live
        // in is cleared at the bottom of this method — so a body that described itself would describe
        // an empty queue every time. Which is a device field that is perfectly flat, perfectly
        // stable, and diverges from the reference by exactly the reference's own amplitude: the seam
        // test's first real finding, and it would have been invisible in a picture.
        var slot = ring = (ring + 1) % Math.Max(sets.Length, 1);

        Span<byte> bytes = stackalloc byte[RipplesConstants.Size];

        Describe(deltaTime).Write(bytes);
        device.Write(blocks[slot], 0, bytes);

        graph.AddPass(
            "WaterRipples",
            pass => {
                pass.Reads(pair.Read, ResourceState.ShaderRead);
                pass.Writes(pair.Write, ResourceState.ShaderWrite);

                // Nothing in this frame reads what the step wrote — the next one does — so culling
                // would remove the pass and the simulation would never advance.
                pass.SideEffect();

                pass.Kind = PassKind.Compute;

                pass.Execute(context => Dispatch(context, pair, slot, groups));
            }
        );

        Injections = 0;
        Overflowed = 0;

        return true;
    }

    /// <summary>Copies the field back to the host, for a fixture that measures the seam.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <param name="into">A host-readable buffer of at least <see cref="ReadbackBytes" /> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The pair was not created readable.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It exists for <c>WaterRippleSeamTests</c> and it is a real capability rather than a
    ///         test hole.</b> § D12 makes the CPU reference and the device step two implementations of
    ///         one simulation, and the only way to hold two implementations together is to read what
    ///         both produced. A fixture that reached into the textures instead would be a fixture that
    ///         breaks the first time the storage changes — which it already did once, when
    ///         <c>rg16f</c> turned out not to be a required storage format.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off by default, because the usage flag is not free.</b> A texture declared
    ///         <c>CopySource</c> is one a driver may place differently, and a production field is
    ///         never read back — the consumer of a ripple is a shader.
    ///     </para>
    /// </remarks>
    public void RecordReadback(ICommandList commands, BufferHandle into) {
        ArgumentNullException.ThrowIfNull(commands);

        if ((Description.Usage & TextureUsage.CopySource) == 0) {
            throw new InvalidOperationException(
                "This ripple field cannot be read back, because it was not created readable. Pass "
                + "readable: true — and see RecordReadback's remarks for why that is not the default."
            );
        }

        var field = Field;

        commands.Barrier(new([], [new(field, ResourceState.ShaderRead, ResourceState.CopySource)]));

        commands.CopyTextureToBuffer(
            new(field),
            new(Settings.Resolution, Settings.Resolution, 1),
            into,
            0
        );

        // Back to where the graph's own tracking believes it is, or the next step's import declares a
        // transition from a layout the image is no longer in — which the validation layer says out
        // loud and a release build does not.
        commands.Barrier(new([], [new(field, ResourceState.CopySource, ResourceState.ShaderRead)]));
    }

    /// <summary>How many bytes a readback of the whole field is.</summary>
    /// <remarks>Four half-floats a texel — see the format's own remarks for why four and not two.</remarks>
    public int ReadbackBytes => Settings.Resolution * Settings.Resolution * 8;

    /// <summary>What each of the two textures is.</summary>
    public TextureDescription Description => textures.Description;

    /// <summary>Swaps the pair, so this step's result becomes the next one's input.</summary>
    /// <remarks>
    ///     ⚠ <b>After the graph has been executed, not after it has been declared</b> — see
    ///     <see cref="PingPongTextures.Advance" />, whose remarks say what swapping mid-declaration
    ///     costs.
    /// </remarks>
    public void Advance() => textures.Advance();

    /// <summary>The uniform block one step needs, filled from the settings and this step's queue.</summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <returns>The block, ready to be written into a uniform buffer.</returns>
    /// <remarks>
    ///     <para>
    ///         Public so a device test can run the shader's own arithmetic against the CPU
    ///         reference's, which is what <c>WaterRippleSeamTests</c> is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole injection array travels, every step, not only its live prefix.</b> A
    ///         uniform block is written wholly or holds whatever the last write left, so a shader that
    ///         read <c>injectionCount</c> correctly over a stale array would replay a splash from two
    ///         steps ago — which reads as the simulation ringing rather than as a buffer nobody
    ///         cleared.
    ///     </para>
    /// </remarks>
    public RipplesConstants Describe(float deltaTime) {
        for (var index = Injections; index < MaxInjections; index++) {
            injections[index] = Vector4.Zero;
        }

        return new() {
            Resolution = Settings.Resolution,
            MetresPerTexel = Settings.MetresPerTexel,
            Speed = Settings.Speed,
            Damping = Settings.Damping,
            DeltaTime = deltaTime,
            EdgeFade = Settings.EdgeFade,
            Injections = injections,
            InjectionCount = Injections
        };
    }

    /// <summary>Binds the pipeline, the pair and this step's block, and dispatches.</summary>
    /// <remarks>
    ///     ⚠ <b>One descriptor set per frame in flight, and the ring is what makes that necessary.</b>
    ///     The set names the half of the pair this step reads, and the halves swap — so a set
    ///     rewritten while a submitted frame still references it is the race a ping-pong exists to
    ///     avoid, arriving through the descriptor rather than through the texture.
    /// </remarks>
    void Dispatch(RenderGraphContext context, PingPongPair pair, int slot, int groups) {
        var commands = context.CommandList;
        var set = sets[slot];
        var block = blocks[slot];

        device.UpdateDescriptorSet(
            set,
            [
                DescriptorWrite.Uniform(RipplesKeys.ConstantBufferBinding, block, 0, RipplesConstants.Size),
                DescriptorWrite.Texture(RipplesKeys.StateBinding, context.View(pair.Read)),
                new(RipplesKeys.TargetBinding, DescriptorKind.StorageTexture, TextureView: context.View(pair.Write))
            ]
        );

        commands.BindPipeline(pipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
        commands.Dispatch(groups, groups, 1);
    }

    /// <summary>Resolves the variant and builds what one dispatch needs, once.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebuilt when <see cref="Compute" /> changes, because that is a different pipeline.</b>
    ///     A host that discovered its backend had no storage images after the first frame would
    ///     otherwise keep dispatching the variant that cannot write.
    /// </remarks>
    bool Resolve(EffectSystem effects, ComputePipelineCache pipelines) {
        if (pipeline.IsValid && built == Compute && builtFor == StepsPerFrame) {
            return true;
        }

        var key = EffectKey.Of(
            RipplesKeys.ShaderName,
            [KeyValuePair.Create("Compute", Compute ? "true" : "false")]
        );

        if (effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var made = pipelines.GetOrCreate(effect);

        if (!made.IsValid || (int)SetIndex >= effect.SetLayouts.Length || !effect.SetLayouts[(int)SetIndex].IsValid) {
            return false;
        }

        Release();

        var slots = Math.Max(device.FramesInFlight, 1) * StepsPerFrame;

        sets = new DescriptorSetHandle[slots];
        blocks = new BufferHandle[slots];

        for (var index = 0; index < slots; index++) {
            sets[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)SetIndex], "water ripples");

            blocks[index] = device.CreateBuffer(
                new(RipplesConstants.Size, BufferUsage.Uniform, MemoryAccess.HostUpload, $"water ripples {index}")
            );

            if (!sets[index].IsValid || !blocks[index].IsValid) {
                return false;
            }
        }

        pipeline = made;
        built = Compute;
        builtFor = StepsPerFrame;
        ring = 0;

        return true;
    }

    void Release() {
        foreach (var set in sets) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        foreach (var block in blocks) {
            if (block.IsValid) {
                device.Destroy(block);
            }
        }

        sets = [];
        blocks = [];
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Release();
        textures.Dispose();
    }
}
