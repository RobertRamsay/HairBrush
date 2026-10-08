// node --test tools/hairbrush-mcp/test.mjs
// Exercises the bridge against a fake HairBrush listener: handshake, forwarding, errors,
// screenshots as image content, and a refused token.
import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { AppLink, McpServer } from './server.mjs';
import { TOOLS } from './tools.mjs';

const TOKEN = 'ab'.repeat(32);

function fakeApp(handler) {
  return new Promise(resolve => {
    const server = net.createServer(socket => {
      let buf = '', authed = false;
      socket.on('data', d => {
        buf += d;
        let nl;
        while ((nl = buf.indexOf('\n')) >= 0) {
          const msg = JSON.parse(buf.slice(0, nl)); buf = buf.slice(nl + 1);
          if (!authed) {
            if (msg.hello !== TOKEN) { socket.destroy(); return; }
            authed = true; socket.write(JSON.stringify({ ready: true, app: 'HairBrush' }) + '\n'); continue;
          }
          const reply = handler(msg);
          socket.write(JSON.stringify({ id: msg.id, ...reply }) + '\n');
        }
      });
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

function harness(port, token = TOKEN) {
  const out = [];
  const link = new AppLink({ port, token });
  const mcp = new McpServer(link, m => out.push(m));
  return { out, link, mcp };
}

test('every tool has a valid schema and unique name', () => {
  const names = new Set();
  for (const t of TOOLS) {
    assert.match(t.name, /^hb_[a-z_]+$/);
    assert.ok(!names.has(t.name)); names.add(t.name);
    assert.equal(t.inputSchema.type, 'object');
    for (const r of t.inputSchema.required ?? []) assert.ok(r in t.inputSchema.properties, `${t.name}.${r}`);
  }
});

test('initialize and tools/list', async () => {
  const { out, mcp } = harness(1);
  await mcp.handle({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } });
  assert.equal(out[0].result.protocolVersion, '2025-06-18');
  await mcp.handle({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
  assert.equal(out[1].result.tools.length, TOOLS.length);
  assert.ok(out[1].result.tools.every(t => !('timeout' in t)));
});

test('forwards calls and maps results, errors and screenshots', async () => {
  const app = await fakeApp(msg => {
    if (msg.method === 'status') return { ok: true, result: { model_loaded: true } };
    if (msg.method === 'screenshot') return { ok: true, result: { png_base64: 'AAAA', width: 1, height: 1 } };
    return { ok: false, error: 'Group 9 does not exist.' };
  });
  const { out, mcp, link } = harness(app.address().port);
  await mcp.handle({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'hb_status', arguments: {} } });
  assert.equal(out[0].result.isError, false);
  assert.deepEqual(JSON.parse(out[0].result.content[0].text), { model_loaded: true });

  await mcp.handle({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'hb_screenshot', arguments: {} } });
  assert.equal(out[1].result.content[0].type, 'image');
  assert.equal(out[1].result.content[0].data, 'AAAA');

  await mcp.handle({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'hb_get_group', arguments: { group_id: 9 } } });
  assert.equal(out[2].result.isError, true);
  assert.match(out[2].result.content[0].text, /Group 9/);
  link.close(); app.close();
});

test('reports a missing app and a bad token as tool errors', async () => {
  const app = await fakeApp(() => ({ ok: true, result: {} }));
  const port = app.address().port;
  const bad = harness(port, 'cd'.repeat(32));
  await bad.mcp.handle({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'hb_status', arguments: {} } });
  assert.equal(bad.out[0].result.isError, true);
  app.close();

  await new Promise(r => app.on('close', r));
  const gone = harness(port);
  await gone.mcp.handle({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'hb_status', arguments: {} } });
  assert.equal(gone.out[0].result.isError, true);
  assert.match(gone.out[0].result.content[0].text, /not running|Could not reach|closed/);
});

test('images nested anywhere (batch, contact sheet) become image content', async () => {
  const { toContent } = await import('./server.mjs');
  const content = toContent({ results: [{ step: 0, result: { png_base64: 'AAA', width: 1 } }, { step: 1, result: { placed: 3 } }] });
  assert.equal(content[0].type, 'image');
  assert.equal(content[0].data, 'AAA');
  const text = JSON.parse(content.at(-1).text);
  assert.equal(text.results[0].result.image, 'image 1');
  assert.equal(text.results[1].result.placed, 3);
});

test('hb_grooming_guide is served locally without the app', async () => {
  const { out, mcp } = harness(1);
  await mcp.handle({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'hb_grooming_guide', arguments: {} } });
  assert.equal(out[0].result.isError, false);
  assert.match(out[0].result.content[0].text, /grooming guide/i);
});
