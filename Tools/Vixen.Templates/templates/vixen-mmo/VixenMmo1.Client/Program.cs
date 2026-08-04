using Vixen.App;

namespace VixenMmo1.Client;

public static class Program {
    // The same one-liner a single-player game uses. A client that plays online is a client with a
    // session in it, not a different kind of application.
    public static int Main(string[] args) => VixenApp.Run<VixenMmo1Client>(args);
}
