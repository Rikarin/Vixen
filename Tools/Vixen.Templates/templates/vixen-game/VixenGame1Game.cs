using Vixen.App;
using Vixen.Core;

namespace VixenGame1;

public sealed class VixenGame1Game : Game {
    protected override void OnConfigure(AppConfig config) {
        config.Name = "VixenGame1";
        config.Window = new() { Title = "VixenGame1", Size = new(1280, 720), IsVisible = true };
    }

    protected override void OnUpdate(GameTime time) {
    }

    protected override void OnRender(GameTime time) {
    }
}
