// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;
using Vixen.Shaders;

namespace Vixen.Engine.Renderer;

/// <summary>
///     The material a scene's references name, loaded, compiled and painted.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join that made <c>MeshRenderable.Material</c> mean something.</b> Every piece either
///         side of it was finished — the importer compiles a <c>.vxmat</c> into a
///         <see cref="MaterialContent" />, the manager loads and shares the chunk,
///         <see cref="MaterialCompiler" /> turns a descriptor into a <see cref="Material" />, and
///         <c>MaterialRenderFeature</c> resolves that to an effect — and nothing put them in a line, so
///         every object in a scene was drawn with the one material a host had assigned by hand.
///     </para>
///     <para>
///         <b>One material per reference, kept and shared.</b> Two entities naming one material get one
///         object, and therefore one descriptor set, one uniform block and one variant — which is the
///         whole economy <c>MaterialRenderFeature</c> is built on. Compiling per entity would be
///         correct and would multiply the frame's per-material work by the instance count.
///     </para>
///     <para>
///         <b>Textures do not hold the material up.</b> A material is answered as soon as it compiles,
///         with its texture parameters unset; <see cref="Update" /> fills them in as they land, and
///         <c>MaterialRenderFeature.Index</c> notices — it compares the view it holds against the one
///         the material carries, so a texture that appears later costs one re-index and nothing else.
///         Until then the index stays zero, which is the table's fallback and a defined thing to
///         sample. Waiting instead would hold a whole level's geometry off screen for its slowest
///         texture.
///     </para>
///     <para>
///         ⚠ <b>And they are re-asserted every frame rather than written once.</b> A streamed
///         texture's view is not a constant: <see cref="AssetTextureSource" /> uploads each new
///         resident tail into a new image and destroys the old pair, so a material written once goes
///         on naming an image that no longer exists. See <see cref="paints" />.
///     </para>
/// </remarks>
public sealed class AssetMaterialSource : IMaterialSource, IDisposable {
    readonly AssetManager assets;
    readonly Dictionary<AssetReference, Entry> entries = [];

    /// <summary>Every (material, parameter, texture) triple a compiled material declared.</summary>
    /// <remarks>
    ///     ⚠ <b>A standing list rather than a queue that empties, and streaming is why.</b> This was
    ///     a work list: a texture sat on it until its view landed, was written into the material once
    ///     and removed. That is correct for a view that never moves, and
    ///     <see cref="AssetTextureSource" /> moves them —
    ///     <c>Swap</c> uploads each new resident tail into a <em>new</em> image and destroys the old
    ///     pair, which is the one thing its own remarks warn a caller about: the view a caller cached
    ///     is a destroyed one. So the material kept naming an image that no longer existed, the
    ///     bindless slot kept pointing at it because the handle it was keyed by had not changed, and
    ///     the shader sampled a dead descriptor — black, on every surface whose wanted resolution
    ///     moved after the first paint, which is the large ones.
    /// </remarks>
    readonly List<Paint> paints = [];

    /// <summary>Which textures each compiled material samples.</summary>
    /// <remarks>
    ///     Asking "which textures does this material use" is a question about the material and is
    ///     asked every frame — see <see cref="TextureDemand" />. Keyed by the compiled
    ///     <see cref="Material" /> rather than by the reference, because that is what a render object
    ///     points at.
    /// </remarks>
    readonly Dictionary<Material, AssetReference[]> sampled = [];

    bool disposed;

    /// <summary>Builds a source over a content manager.</summary>
    /// <param name="assets">Where the materials come from.</param>
    /// <param name="textures">Where their textures come from, or null for a project with none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assets" /> is null.</exception>
    public AssetMaterialSource(AssetManager assets, AssetTextureSource? textures = null) {
        ArgumentNullException.ThrowIfNull(assets);

        this.assets = assets;
        Textures = textures;
    }

    /// <summary>Where the textures a material assigns come from.</summary>
    public AssetTextureSource? Textures { get; }

    /// <summary>
    ///     Slot fillers the project decides rather than the material, by their qualified names.
    /// </summary>
    /// <remarks>
    ///     <see cref="MaterialCompiler.ForwardIrradianceSlot" /> is the one that exists, and it is a
    ///     project's decision for the reason the compiler's own parameter says: whether the scene has an
    ///     irradiance field is true of every material in it at once, and two materials disagreeing about
    ///     it is two effects where the project meant one.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Slots { get; set; }

    /// <summary>
    ///     Permutation values the project decides rather than the material, applied to every one it
    ///     compiles.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Exactly <see cref="Slots" />'s argument, one mechanism along.</b> Whether the frame
    ///         renders a shadow atlas, whether there is an environment cube to reflect, whether the
    ///         probe field is filled — each of those is true of every material in the scene at once,
    ///         because they are facts about the <em>frame</em>. A material file that claimed one would
    ///         be a claim about a document it has never seen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Without it a project's own materials take each permutation's declared default —
    ///         which is not the same as off, and this paragraph said it was.</b> It claimed a null
    ///         collection compiled "with every permutation off", and therefore that a level rendering
    ///         four cascades drew with a shader that had no shadow term in it. That never happened
    ///         and cannot: <c>EffectKey.From</c> falls through to <see cref="ParameterKey" />'s
    ///         <c>DefaultValue</c> for a key the collection does not carry, and those defaults are the
    ///         <c>.rvn</c>'s — <c>ClusteredShading.rvn</c> has declared <c>UseShadows: bool = true</c>
    ///         and <c>CascadeCount: int = 4</c> since the file was written. A project that sets
    ///         nothing here is shadowed, in four cascades.
    ///         <c>AssetMaterialSourceTests.AProjectThatSetsNoPermutationsStillCompilesTheShadowTerm</c>
    ///         is the guard, and it is the one worth having: the claim becomes true the day somebody
    ///         flips that default, and nothing else would notice.
    ///     </para>
    ///     <para>
    ///         Measured rather than argued, because the claim was about a picture. Sample 13 at 512
    ///         headless frames with this property set, against the same build with the one line that
    ///         sets it removed: mean channel 104.009 against 104.004, mean absolute difference
    ///         0.014/255, and 704 of 1 440 000 pixels moving by more than 2. Every cast sun shadow in
    ///         the frame is in both, and a 20× amplified difference is speckle with no shadow edge in
    ///         it. A level losing its shadow term across nine materials is not a change of five
    ///         thousandths of a channel.
    ///     </para>
    ///     <para>
    ///         What a null collection <em>does</em> cost is every permutation whose declared default is
    ///         off — <c>UseReflectionProbe</c>, <c>UseIrradianceField</c>,
    ///         <c>UseDistanceFieldOcclusion</c> — so a project that built a probe or a field gets
    ///         materials compiled not to read it. Real, and a missing specular reflection rather than
    ///         a scene with no shadows. The frame's own permutations are a separate layer and are not
    ///         affected either way: <c>MaterialRenderFeature.Permutations</c> is applied last and wins,
    ///         which is how <c>CompositorBuilder</c> already keeps <c>CascadeCount</c> agreeing with
    ///         the <c>!ShadowMap</c> node whatever a material or a project says — see
    ///         <c>ShadowMapRenderer.CascadeCountKey</c>.
    ///     </para>
    ///     <para>
    ///         Applied at compile time rather than copied afterwards, because a permutation is part of
    ///         the effect key: setting one on a material that has already resolved its variant changes
    ///         nothing until something asks again.
    ///     </para>
    /// </remarks>
    public ParameterCollection? Permutations { get; set; }

    /// <summary>How many distinct materials have been asked for.</summary>
    public int Requested => entries.Count;

    /// <summary>How many have compiled.</summary>
    public int Loaded {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Material is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many will not arrive.</summary>
    /// <remarks>
    ///     A reference nothing shipped, a chunk that is not a material, or a document the compiler
    ///     rejected. The last of those should have been caught at import — <c>MaterialImporter</c>
    ///     compiles every material to find out whether it is one — so a number here that is not zero is
    ///     content built by an older engine.
    /// </remarks>
    public int Failed {
        get {
            var count = 0;

            foreach (var entry in entries.Values) {
                if (entry.Failed) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many texture assignments are still waiting for their pixels.</summary>
    /// <remarks>
    ///     Falls to zero a few frames after a level starts. A number that stays up is a texture
    ///     reference nothing can resolve, which otherwise looks exactly like a material an artist
    ///     forgot to assign a map to.
    ///     <para>
    ///         Counted rather than read off the length of a queue, because <see cref="paints" /> no
    ///         longer drains — see the field. A paint is owed until its parameter has been written
    ///         once, which is the same moment the list used to drop it.
    ///     </para>
    /// </remarks>
    public int Unpainted {
        get {
            var count = 0;

            foreach (var paint in paints) {
                if (!paint.Material.Parameters.Has(paint.Key)) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Which textures a compiled material samples.</summary>
    /// <param name="material">One of the materials this compiled.</param>
    /// <returns>
    ///     Its texture references, or null for a material this did not compile and for one that
    ///     samples nothing.
    /// </returns>
    /// <remarks>
    ///     The material side of the texture-to-bounds join: extraction knows which material a
    ///     drawable is painted in and this says which files that material reads, which is what lets
    ///     <see cref="TextureDemand" /> turn a drawable's screen size into
    ///     <see cref="AssetTextureSource.Want" />. Null rather than an empty list for a material this
    ///     never saw, because a project supplying its own <see cref="IMaterialSource" /> — the
    ///     samples do — has materials this has no opinion about at all.
    /// </remarks>
    public IReadOnlyList<AssetReference>? TexturesOf(Material material) =>
        material is not null && sampled.TryGetValue(material, out var textures) ? textures : null;

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out Material material) {
        ObjectDisposedException.ThrowIf(disposed, this);

        material = null!;

        if (!entries.TryGetValue(reference, out var entry)) {
            entries[reference] = entry = Begin(reference);
        }

        if (entry.Material is { } compiled) {
            material = compiled;
            return true;
        }

        if (entry.Failed || entry.Handle is not { Status: AssetStatus.Loaded }) {
            if (entry.Handle is { Status: AssetStatus.Failed }) {
                entry.Failed = true;
            }

            return false;
        }

        if (!Compile(entry)) {
            return false;
        }

        material = entry.Material!;
        return true;
    }

    /// <summary>Puts the frame's texture work on a list, and paints what has landed.</summary>
    /// <param name="commands">The frame's list.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Called once a frame by whoever owns the frame</b> — <see cref="WorldRenderer.Draw" />
    ///     does it — and a host that forgets leaves every textured material sampling the fallback for
    ///     ever. That is a picture rather than a failure, which is exactly why it is worth saying here:
    ///     the symptom is "all my materials are the same flat colour" and the cause is one missing call.
    /// </remarks>
    public void Update(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Textures is not { } textures) {
            return;
        }

        // The copies first, so a texture whose bytes were recorded on a previous frame is viewable by
        // the time this asks for it.
        textures.Update(commands);

        // ⚠ Every frame, and every paint — not once each. A streamed texture's view is replaced
        // whenever its resident resolution changes, and the old one is destroyed with it; a material
        // written once holds that dead handle for the rest of the run. Re-asserting costs nothing
        // for a settled texture, because ParameterCollection.Set does not bump the version when the
        // value is unchanged, so MaterialRenderFeature re-indexes only what actually moved.
        foreach (var paint in paints) {
            if (textures.TryGet(paint.Texture, out var view)) {
                paint.Material.Parameters.Set(paint.Key, view);
            }
        }
    }

    /// <summary>Starts a load, and answers with the entry that will hold it.</summary>
    Entry Begin(AssetReference reference) {
        var entry = new Entry();

        try {
            entry.Handle = assets.LoadAsync<MaterialContent>(reference);
        } catch (ReferenceNotFoundException) {
            // Recorded rather than rethrown, for the reason AssetMeshSource gives: a reference the
            // catalog does not know is one entity in a level, and a frame that threw would take the
            // level with it.
            entry.Failed = true;
        }

        return entry;
    }

    /// <summary>Compiles a loaded document, and says whether it produced a material.</summary>
    bool Compile(Entry entry) {
        var content = entry.Handle!.Result;

        // A model this build does not have is the default rather than a refusal, because the import
        // already refused it — content that reaches here naming one was built by an older engine, and
        // shading it with the standard model is a picture somebody can recognise as wrong.
        MaterialShading.TryResolve(content.Shading, out var shading);

        var compilation = MaterialCompiler.Compile(content.ToDescriptor(shading), Slots);

        if (compilation.Material is not { } material) {
            entry.Failed = true;
            return false;
        }

        // Before anything asks the material for a variant, which is what makes these permutations
        // rather than values — see Permutations.
        if (Permutations is { } project) {
            material.Parameters.Apply(project);
        }

        entry.Material = material;

        var textures = new List<AssetReference>(content.Textures.Length);

        foreach (var texture in content.Textures) {
            if (texture.Parameter.Length > 0 && !texture.Texture.IsNull) {
                paints.Add(new(material, ParameterKeys.New<TextureViewHandle>(texture.Parameter), texture.Texture));
                textures.Add(texture.Texture);
            }
        }

        if (textures.Count > 0) {
            sampled[material] = textures.ToArray();
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        entries.Clear();
        paints.Clear();
        sampled.Clear();
    }

    /// <summary>One material, somewhere between named and drawn.</summary>
    sealed class Entry {
        public AssetHandle<MaterialContent>? Handle;
        public Material? Material;
        public bool Failed;
    }

    /// <summary>A texture a material samples, and the parameter it reaches the shader through.</summary>
    /// <param name="Material">The material to set it on.</param>
    /// <param name="Key">The parameter the material's shader reads the view from.</param>
    /// <param name="Texture">Which texture.</param>
    readonly record struct Paint(Material Material, ParameterKey<TextureViewHandle> Key, AssetReference Texture);
}
