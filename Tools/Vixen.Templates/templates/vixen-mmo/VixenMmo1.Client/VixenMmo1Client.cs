using System.Net;
using System.Text;
using Vixen.App;
using Vixen.Core;
using Vixen.Live;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Udp;

namespace VixenMmo1.Client;

/// <summary>The player's half: one UDP session to a realm, and a ticket to get in with.</summary>
/// <remarks>
///     ⚠ The endpoint and the ticket are <b>learned, not configured</b>. A real client asks the gate
///     over HTTPS which shard to go to and is handed both; this one reads them off the command line
///     so that a freshly scaffolded pair of projects can talk to each other before there is a gate
///     to ask. That is a development shortcut and the only one in here.
/// </remarks>
public sealed class VixenMmo1Client : Game {
    NetworkSession? session;

    protected override void OnConfigure(AppConfig config) {
        config.Name = "VixenMmo1";
        config.Window = new() { Title = "VixenMmo1", Size = new(1280, 720), IsVisible = true };
    }

    protected override void OnInitialise() {
        var arguments = Services.Config.Arguments;

        if (!TryRead(arguments, "--realm", out var text) || !RealmEndpoint.TryParse(text, out var realm)) {
            // Nothing to connect to is not an error: a client with no arguments is what somebody
            // runs to see the window open.
            return;
        }

        TryRead(arguments, "--ticket", out var ticket);

        var transport = new UdpTransport(
            new UdpDatagramSocketFactory(),
            new UdpTransportOptions { RemoteEndPoint = new(IPAddress.Parse(realm.Host), realm.Port) }
        );

        session = new(
            transport,
            new SessionOptions {
                // The ticket goes in the handshake, which is the door the realm checks. It is opaque
                // to this process: the client is a courier and cannot read or forge one.
                AuthenticationPayload = Encoding.UTF8.GetBytes(ticket)
            },
            ownsTransport: true
        );

        session.StartClient();
    }

    protected override void OnUpdate(GameTime time) => session?.Update(time.UnscaledElapsed);

    protected override void OnShutdown() => session?.Dispose();

    static bool TryRead(IReadOnlyList<string> arguments, string name, out string value) {
        for (var index = 0; index < arguments.Count - 1; index++) {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal)) {
                value = arguments[index + 1];

                return true;
            }
        }

        value = string.Empty;

        return false;
    }
}
