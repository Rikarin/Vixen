using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui.Controls;

namespace VixenPlugin1;

/// <summary>
///     The one type the editor looks for: a public class with a parameterless constructor that
///     implements <see cref="IEditorPlugin" />.
/// </summary>
/// <remarks>
///     <para>
///         The editor finds this assembly through the <c>plugin.yaml</c> beside it, loads it into a
///         collectible load context of its own, and calls <see cref="Activate" /> once. Everything
///         registered on the <see cref="PluginContext" /> is recorded, so unloading the plugin is
///         undoing that record — which is what makes "build, Reload Plugins, see the change" work
///         without closing the project.
///     </para>
///     <para>
///         There is no <c>Deactivate</c> below, because there is nothing here the context does not
///         already know how to undo. Add one when the plugin owns something it did not register
///         through the context: a file watcher, a socket, a thread. Anything still holding a
///         reference into this assembly keeps it loaded, and the runtime says nothing when that
///         happens.
///     </para>
///     <para>
///         The constructor is deliberately absent. It runs before the editor is ready to be asked
///         anything, so the work belongs in <see cref="Activate" />.
///     </para>
/// </remarks>
public sealed class VixenPlugin1Plugin : IEditorPlugin {
    /// <summary>What the editor knows this plugin by — the same id <c>plugin.yaml</c> declares.</summary>
    /// <remarks>
    ///     Also what a built-in feature is activated under: the editor's own Terrain and Blockout
    ///     tools are plugins too, registered with <c>host.Activate(PluginId, PluginName, new
    ///     VixenPlugin1Plugin())</c> instead of being discovered on disk. Same interface, same
    ///     context, same rules — which is the point.
    /// </remarks>
    public const string PluginId = "com.example.plugin";

    /// <summary>What the plugin list calls it.</summary>
    public const string PluginName = "VixenPlugin1";

    /// <summary>
    ///     The verb. Prefixed with the plugin's id, because a registry refuses a second command
    ///     under a name somebody already owns — and finding that out from a user's bug report is
    ///     worse than typing eighteen characters.
    /// </summary>
    const string GreetCommand = PluginId + ".greet";

    /// <summary>The panel, prefixed for the same reason, and remembered in the saved layout.</summary>
    const string GreetingPanel = PluginId + ".greeting";

    int greetings;
    TextBlock? line;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        // A command is one registration and four affordances: it is in the command palette, it can
        // be bound to a key, it can be put in a menu, and it can be put on a toolbar. Registering
        // the *verb* rather than the menu entry is what makes all four true at once.
        context.AddCommand(
            GreetCommand,
            new StringId(PluginId + ".command.greet", "Say Hello"),
            Greet
        );

        // A panel and the command that shows it, as the shell always makes them. The builder runs
        // when the panel is first opened, not now.
        context.AddPanel(
            GreetingPanel,
            new StringId(PluginId + ".panel.greeting", "Greeting"),
            panel => {
                line = panel.Add<TextBlock>();
                Show();
            }
        );

        // Window ▸ Say Hello. `Shell.View` is the menu panels are listed under; `context.AddMenu`
        // adds a top-level one of your own, and `context.FindMenu("editor.menu.scene")` finds one
        // the editor or another plugin already made.
        context.AddMenuItem(context.Shell.View, GreetCommand);
    }

    void Greet() {
        greetings++;
        Show();
    }

    void Show() {
        if (line is not null) {
            line.Text = greetings == 0
                ? $"Hello from {PluginName}. Run Window ▸ Say Hello."
                : $"Hello from {PluginName}, {greetings} time(s).";
        }
    }
}
