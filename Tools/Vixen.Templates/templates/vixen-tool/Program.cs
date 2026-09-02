using VixenTool1;
using Vixen.App;

// The two calls VixenApp.Run<T> makes, written out, because a batch head wants to answer with the
// exit code its own step decided rather than the host's. Everything in the boot path is a public
// call you can inline and edit — see docs/plan/17.
var tool = new VixenTool1Tool();

using var application = VixenApp.Create(args).Build(tool);

var host = application.Run();

return host != 0 ? host : tool.ExitCode;
