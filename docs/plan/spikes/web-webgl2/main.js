import { dotnet } from './_framework/dotnet.js'
const lines = [];
const paint = () => document.getElementById('result').innerText = lines.join("\n");
const { setModuleImports, getAssemblyExports, getConfig } = await dotnet.create();
setModuleImports('main.js', { log: (s) => { lines.push("• " + s); paint(); console.log("SPIKE " + s); } });
const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
try { lines.push("RESULT: " + exports.Program.Run()); }
catch (e) { lines.push("JS-CAUGHT: " + (e && (e.stack || e.message || e))); }
paint();
