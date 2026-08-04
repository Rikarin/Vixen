using Vixen.Live.Realms;

namespace VixenMmo1.Realm;

public static class Program {
    // A realm is launched by a placement backend with one argument — `--realm-spec shard=…;map=…;
    // port=…` — and everything it needs is in it. A process handed no spec says so on standard error
    // and exits 2, which a launcher can tell from a crash and should not retry.
    public static int Main(string[] args) => RealmApp.Run<VixenMmo1Realm>(args);
}
