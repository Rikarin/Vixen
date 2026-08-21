import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';

const root = process.argv[2];
const port = Number(process.argv[3] ?? 8099);

const types = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
    '.wasm': 'application/wasm', '.json': 'application/json', '.dat': 'application/octet-stream',
    '.dll': 'application/octet-stream', '.pdb': 'application/octet-stream',
    '.css': 'text/css', '.txt': 'text/plain'
};

createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    let path = decodeURIComponent(url.pathname);
    if (path.endsWith('/')) path += 'index.html';
    const file = join(root, normalize(path).replace(/^(\.\.[/\\])+/, ''));

    try {
        const body = await readFile(file);
        response.writeHead(200, {
            'Content-Type': types[extname(file)] ?? 'application/octet-stream',
            'Cross-Origin-Opener-Policy': 'same-origin',
            'Cross-Origin-Embedder-Policy': 'require-corp',
            'Cache-Control': 'no-store'
        });
        response.end(body);
    } catch {
        response.writeHead(404, { 'Content-Type': 'text/plain' });
        response.end('not found: ' + path);
    }
}).listen(port, () => console.log('serving ' + root + ' on ' + port));
