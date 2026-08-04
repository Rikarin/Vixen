using Vixen.App;
using Vixen.Core;
using Vixen.Rendering.PostFx;

namespace VixenGame1;

public sealed class VixenGame1Game : Game {
    protected override void OnConfigure(AppConfig config) {
        config.Name = "VixenGame1";
        config.Window = new() { Title = "VixenGame1", Size = new(1280, 720), IsVisible = true };

        // The project's frame: seven semantic knobs in Assets/Frame.vxcompositor that expand at
        // build time into the whole graph — shadows, GI, the post chain. Edit the file to turn
        // features on and off; run `vixen frame explode` on it the day the knobs stop being enough.
        // Guide: docs/guide/rendering/standard-frame and docs/guide/rendering/choosing-a-frame.
        config.Graphics.Compositor = "Assets/Frame.vxcompositor";

        // Extraction's half of the document's knobs — a frame cannot decide what an object is
        // extracted as. `shadows:` above Off needs every mesh drawn into the Shadow stage too, and
        // `antialiasing: Taa` needs the velocity pass fed the same way through Motion.
        config.Graphics.CasterStages.Add("Shadow");
        config.Graphics.CasterStages.Add("Motion");

        // ⚠ Here, not in OnInitialise: the compositor is built before OnInitialise runs, and the
        // builder only knows !StandardFrame and the post nodes through this factory — constructing
        // it is also what registers their document aliases, so without this line the file above
        // does not even bind. Assets/RenderQuality.vxpreset rides the same registration once it
        // says anything: `new PostEffectFactory { Preset = ... }`.
        config.Graphics.Factories.Add(new PostEffectFactory());
    }

    protected override void OnUpdate(GameTime time) {
    }

    protected override void OnRender(GameTime time) {
    }
}
