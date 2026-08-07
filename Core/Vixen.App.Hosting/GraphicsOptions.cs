// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.App;

/// <summary>What the host builds the frame out of.</summary>
/// <remarks>
///     <para>
///         Names and capacities, and nothing about <em>where the passes go</em> — that is the
///         compositor document's, which is the division the whole renderer is built on. What is here
///         is the handful of decisions that join a document to a host: which resource the window is,
///         which view the camera fills, which stage the world draws in, and how much geometry the
///         scene is allowed.
///     </para>
///     <para>
///         The three names default to the ones <see cref="AppGraphics.DefaultFrame" /> uses, so a
///         project that authors its own compositor either keeps those names or says here what it
///         called them instead. They are strings rather than an enum because a document is authored
///         data: the set of resources in a frame is open, and a host that could only bind names it
///         had compiled in could not run a frame somebody wrote after it shipped.
///     </para>
/// </remarks>
public sealed class GraphicsOptions {
    /// <summary>Whether the host opens a device and draws the world at all.</summary>
    /// <remarks>
    ///     On, for the same reason <see cref="AppConfig.UseEngine" /> is: <c>VixenApp.Run&lt;TGame&gt;</c>
    ///     takes a <see cref="Game" />, and a game that is handed a world and no way to see it is a
    ///     host that stops one step short. Off is the right line for a batch tool and for a headless
    ///     server that does no rendering of its own — and note that off is <em>not</em> how a server
    ///     gets built: a server keeps the frame and runs it against the Null backend, which is
    ///     [doc 17](../../docs/plan/17-app-heads-and-shipping.md)'s design and what keeps the two
    ///     builds one program.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Extra node kinds the frame may name, beyond the ones the builder knows itself.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Filled in <c>Game.OnConfigure</c>, because the compositor is built before
    ///         <c>OnInitialise</c> runs.</b> <c>CompositorBuilder</c> cannot switch on a type in a
    ///         package downstream of it — <c>Vixen.Rendering.PostFx</c> is one, and a case there would
    ///         be a reference cycle — so a document naming <c>!Bloom</c> or <c>!DistanceFieldAo</c>
    ///         needs a factory that knows them. Adding one here is the only moment early enough.
    ///     </para>
    ///     <para>
    ///         It also settles a second thing that looks unrelated and is not: constructing the
    ///         factory touches its assembly, which runs the <c>[ModuleInitializer]</c> registering
    ///         those aliases with the type registry. Without that the document does not even bind, and
    ///         the host reports a compositor it could not load rather than a node it could not build.
    ///     </para>
    /// </remarks>
    public IList<ISceneRendererFactory> Factories { get; } = [];

    /// <summary>The scalability tier for a frame that does not name its own.</summary>
    /// <remarks>
    ///     <para>
    ///         The platform's pick in doc 39's waterfall, and the bottom vote: a
    ///         <c>!StandardFrame</c> whose <c>quality:</c> is written out-votes it, and a
    ///         hand-authored document never reads it at all — its numbers are its own. What this
    ///         decides is the frame of a project that left the tier to whoever launched it, which is
    ///         exactly what a settings screen's quality dropdown or a per-device profile wants to
    ///         be: one assignment here, no document edits.
    ///     </para>
    ///     <para>
    ///         High rather than Epic, on the preset node's own reasoning: the default should be the
    ///         full chain at numbers a mid-range GPU sustains, not the numbers a capture is taken
    ///         at.
    ///     </para>
    /// </remarks>
    public QualityTier Quality { get; set; } = QualityTier.High;

    /// <summary>
    ///     The address of the compositor to load, or <see langword="null" /> for the built-in frame.
    /// </summary>
    /// <remarks>
    ///     A project's frame is content — authored in the editor, baked into a bundle, addressed like
    ///     anything else — and this is where a game says which one. Null takes
    ///     <see cref="AppGraphics.DefaultFrame" />, which is one lit pass into the window: enough to
    ///     see a scene, and deliberately not enough to be mistaken for a project's renderer.
    /// </remarks>
    public string? Compositor { get; set; }

    /// <summary>
    ///     The address of the project's look profile, or <see langword="null" /> for none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The <c>.vxlook</c> of doc 39: one asset of artistic opinions — exposure target, meter
    ///         clamps, grade, fog, vignette — folded under everything a scene's volumes say, so every
    ///         scene shares it unless a scene says otherwise. The host loads it and hands the payload
    ///         to <c>PostProcessVolumeSystem.Look</c>; a game holding the asset already may skip this
    ///         and assign <c>AppGraphics.Volumes.Look</c> itself.
    ///     </para>
    ///     <para>
    ///         ⚠ A frame document whose <c>!StandardFrame</c> writes a <c>look:</c> inline out-votes
    ///         this, the way a document's <c>quality:</c> out-votes <see cref="Quality" />: the
    ///         document decided, and the host's setting is for the project that left it out. A look
    ///         that fails to load logs and leaves the neutral frame, the same trade
    ///         <see cref="Compositor" /> makes.
    ///     </para>
    /// </remarks>
    public string? Look { get; set; }

    /// <summary>The name of the view the camera fills.</summary>
    /// <remarks>What a document's <c>view:</c> binds a node to. A name nothing in the document uses
    /// draws nothing, which the compositor reports as an unbound view rather than silently.</remarks>
    public string View { get; set; } = "Camera";

    /// <summary>The name of the stage the world's drawables are extracted into.</summary>
    public string Stage { get; set; } = "Opaque";

    /// <summary>
    ///     Stages an object is extracted into as well as <see cref="Stage" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A shadow caster is the same object in a second stage, and extraction is where that is
    ///     decided.</b> <see cref="Stage" /> is the camera's — it is what the frame's view draws — and
    ///     an object only in it is invisible to a shadow pass however carefully the document is
    ///     written. Naming the caster stage here is what puts the bit in the mask
    ///     <c>MeshExtractionSystem</c> stamps on every object.
    ///
    ///     The view's own mask stays <see cref="Stage" /> alone: a shadow node makes its own views,
    ///     one per cascade, and adding the caster stage to the camera would draw the level twice
    ///     into the frame the player sees.
    /// </remarks>
    public IList<string> CasterStages { get; } = [];

    /// <summary>
    ///     The name of the stage particle emitters are drawn in, or empty for a frame with none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <see cref="Stage" /> and never a caster stage.</b> Particles want a transparent
    ///         stage that tests depth and does not write it, which is not what a mesh is drawn in — and
    ///         a billboard is expanded once for the whole frame against one camera, so a shadow cascade
    ///         drawing the same quads would draw them edge-on to its own light. That is
    ///         <c>ParticleRenderFeature</c>'s constraint, and this is where a project honours it.
    ///     </para>
    ///     <para>
    ///         Empty by default rather than a conventional name, because a document that does not
    ///         declare one is the ordinary case and a host looking for a stage nobody wrote would log a
    ///         missing stage every run. A project whose scene carries a <c>!VfxEmitter</c> names the
    ///         stage its document declares; one that does not pays nothing.
    ///     </para>
    /// </remarks>
    public string ParticleStage { get; set; } = string.Empty;

    /// <summary>The name of the resource the window's image is lent to the frame as.</summary>
    /// <remarks>
    ///     ⚠ <b>The frame's last colour target has to be this one.</b> A render graph culls a pass
    ///     whose output nobody reads, and the only thing that makes the final pass matter is that its
    ///     target belongs to somebody outside the graph. A document whose last pass writes a resource
    ///     it declared itself draws a correct frame into memory that is then thrown away — a black
    ///     window, no error anywhere.
    /// </remarks>
    public string Output { get; set; } = "SceneColour";

    /// <summary>Which graphics APIs to try, most preferred first.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Empty means "whatever the head ships with", which is the right default.</b> A list
    ///         nobody wrote should not out-vote the app head that knows which backends it actually
    ///         references — <c>Vixen.App</c>'s order is Vulkan then Null, and a game that never
    ///         thinks about this gets exactly the behaviour it had before the setting existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A backend named here is <i>tried</i>, not guaranteed.</b> Each is asked in turn
    ///         and the first that opens wins; every refusal is reported, so a chain that fell all the
    ///         way through says what each candidate said rather than only the last. Naming one that
    ///         the head does not reference is a refusal with that as the reason, not a crash.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A list with nothing openable in it is a boot failure, and deliberately.</b> There
    ///         is no implicit fall-through to <see cref="GraphicsBackend.Null" />: an operator who
    ///         wrote <c>--vixen-backend vulkan</c> to find out whether Vulkan works is owed an answer,
    ///         and a silent downgrade to a device that draws nothing is the exact shape of the bug
    ///         that question was asked to find. Put <see cref="GraphicsBackend.Null" /> last to ask
    ///         for the fall-through.
    ///     </para>
    /// </remarks>
    public IList<GraphicsBackend> Backends { get; } = [];

    /// <summary>The format to ask the swapchain for.</summary>
    public PixelFormat Format { get; set; } = PixelFormat.Bgra8UNormSrgb;

    /// <summary>How the swapchain waits for the display.</summary>
    /// <remarks>
    ///     <see cref="PresentMode.Fifo" />, which is vsync and the only mode every backend is
    ///     required to have. It is also what makes <see cref="AppConfig.FrameRateLimit" /> mostly
    ///     redundant on a windowed head — the two together cap at whichever is lower, which is the
    ///     behaviour a player expects from both settings.
    /// </remarks>
    public PresentMode PresentMode { get; set; } = PresentMode.Fifo;

    /// <summary>The display gamut to ask the swapchain for.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>sRGB by default, and that default is a statement rather than caution.</b> Asking
    ///         for a wider gamut only changes what the swapchain is <i>able</i> to show; something
    ///         still has to map colours against <c>ISwapChain.Gamut</c> at presentation. An
    ///         application that raises this without doing that gets a picture that is over-saturated
    ///         on a P3 surface rather than a wider one, because the same numbers against wider
    ///         primaries are a different colour.
    ///     </para>
    ///     <para>
    ///         The request may not be met. A surface that offers no wide colour space paired with
    ///         ten-bit or half-float storage stays at <see cref="ColorGamut.Srgb" /> — eight bits
    ///         across a wider gamut bands where eight bits across sRGB does not — so read
    ///         <c>ISwapChain.Gamut</c> back rather than assuming this was honoured.
    ///     </para>
    /// </remarks>
    public ColorGamut Gamut { get; set; } = ColorGamut.Srgb;

    /// <summary>How many vertices the scene's shared geometry buffer holds.</summary>
    /// <remarks>
    ///     The project's geometry budget, and the caller's for the reason <c>WorldRenderer</c> gives:
    ///     <c>MeshExtractionSystem.Dropped</c> is what says a level asked for more than was reserved,
    ///     which otherwise looks like meshes that stop appearing past a certain number.
    /// </remarks>
    public int VertexCapacity { get; set; } = 1 << 20;

    /// <summary>How many indices it holds.</summary>
    public int IndexCapacity { get; set; } = 1 << 21;

    /// <summary>What size to draw at when there is no window to take a size from.</summary>
    /// <remarks>
    ///     A head with <see cref="AppConfig.Window" /> set to null still has a frame — a server
    ///     stepping a world, a test running <c>--vixen-frames</c> — and every resource the document
    ///     declares is sized as a fraction of this. Zero would give the graph a zero-sized texture,
    ///     which every backend refuses.
    /// </remarks>
    public Int2 WindowlessSize { get; set; } = new(1280, 720);

    /// <summary>Whether each of the frame's passes is timed on the GPU.</summary>
    /// <remarks>
    ///     <para>
    ///         Attaches a <see cref="GpuProfiler" /> to the render graph, which then brackets every
    ///         pass it runs with a timestamp pair named after the pass. <c>AppGraphics.GpuFrame</c>
    ///         is where the readings arrive, a few frames late — a GPU profiler that reported the
    ///         frame it was recording would be lying.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off, and it has to be.</b> A timestamp pair is a GPU write, and on tile-based
    ///         hardware — every Apple GPU, every mobile GPU — a query write can force a tile resolve,
    ///         so an always-on instrument changes the timings it reports. The number this yields is
    ///         a measurement of a profiled frame, which is near but not equal to the frame that
    ///         ships; leaving it on to "keep an eye on things" is how a renderer acquires a cost
    ///         nobody can find.
    ///     </para>
    ///     <para>
    ///         Ignored on a device that reports no timestamp queries, which the host logs rather
    ///         than failing over: a run that refused to start because it could not be profiled would
    ///         be worse than one that draws.
    ///     </para>
    /// </remarks>
    public bool GpuProfiling { get; set; }
}
