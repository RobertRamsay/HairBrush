#!/usr/bin/env node
/** HairBrush MCP server. Node 18+ built-ins only; no npm install.
 *
 * Speaks MCP (JSON-RPC over stdio) to the host, and forwards each tool call to a running HairBrush
 * (Unity Editor in play mode, or a player started with -mcp) over a loopback TCP socket that is
 * authenticated with a shared token in ~/.hairbrush-mcp/token. See README.md.
 *
 * stdout carries protocol only; diagnostics go to stderr. Mutations are never retried
 * automatically - after a timeout the edit may still have happened, so inspect first.
 */
import net from 'node:net';
import { randomBytes } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { TOOLS } from './tools.mjs';

export const VERSION = '0.1.0';
const PROTOCOLS = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
const MAX_REPLY = 64 * 1024 * 1024;
const DEFAULT_TIMEOUT = 30000;
const log = msg => process.stderr.write(`[hairbrush-mcp] ${msg}\n`);
const isObject = v => v !== null && typeof v === 'object' && !Array.isArray(v);

export function loadConfig(env = process.env) {
  const port = Number(env.HAIRBRUSH_MCP_PORT ?? 5170);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('HAIRBRUSH_MCP_PORT must be 1024-65535.');
  const tokenFile = env.HAIRBRUSH_MCP_TOKEN_FILE || join(homedir(), '.hairbrush-mcp', 'token');
  let token;
  try { token = readFileSync(tokenFile, 'utf8').trim(); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    // Whichever of the app and the bridge starts first creates the secret; the other reads it.
    mkdirSync(dirname(tokenFile), { recursive: true, mode: 0o700 });
    try { writeFileSync(tokenFile, randomBytes(32).toString('hex') + '\n', { flag: 'wx', mode: 0o600 }); }
    catch (e) { if (e.code !== 'EEXIST') throw e; }
    token = readFileSync(tokenFile, 'utf8').trim();
  }
  if (!/^[0-9a-f]{64}$/i.test(token)) throw new Error(`Invalid token file: ${tokenFile}`);
  return { port, token };
}

/** One persistent, authenticated connection to the app, re-opened on demand. */
export class AppLink {
  constructor({ port, token, connectTimeoutMs = 3000 }) {
    this.port = port; this.token = token; this.connectTimeoutMs = connectTimeoutMs;
    this.socket = null; this.ready = null; this.seq = 0; this.pending = new Map(); this.app = null;
  }

  connect() {
    if (this.ready) return this.ready;
    this.ready = new Promise((resolve, reject) => {
      const socket = net.createConnection({ host: '127.0.0.1', port: this.port });
      let buffer = Buffer.alloc(0);
      let greeted = false;
      const fail = message => {
        socket.destroy();
        if (!greeted) reject(new Error(message));
      };
      const timer = setTimeout(() => fail('HairBrush did not answer the handshake.'), this.connectTimeoutMs);
      socket.setNoDelay(true);
      socket.on('connect', () => socket.write(JSON.stringify({ hello: this.token }) + '\n'));
      socket.on('data', chunk => {
        buffer = Buffer.concat([buffer, chunk]);
        if (buffer.length > MAX_REPLY) { fail('Reply too large.'); return; }
        let nl;
        while ((nl = buffer.indexOf(10)) >= 0) {
          const line = buffer.subarray(0, nl).toString('utf8');
          buffer = buffer.subarray(nl + 1);
          if (!line.trim()) continue;
          let msg;
          try { msg = JSON.parse(line); } catch { fail('Malformed reply from HairBrush.'); return; }
          if (!greeted) {
            if (msg.ready !== true) { fail('HairBrush rejected the handshake.'); return; }
            greeted = true; clearTimeout(timer); this.socket = socket; this.app = msg;
            resolve(msg);
            continue;
          }
          const p = this.pending.get(msg.id);
          if (!p) continue;
          this.pending.delete(msg.id); clearTimeout(p.timer);
          if (msg.ok) p.resolve(msg.result ?? {});
          else p.reject(new Error(typeof msg.error === 'string' ? msg.error : 'HairBrush reported an error.'));
        }
      });
      socket.on('error', error => {
        clearTimeout(timer);
        if (!greeted) reject(new Error(error.code === 'ECONNREFUSED'
          ? `HairBrush is not running or its MCP listener is off (127.0.0.1:${this.port}). Open the project in Unity and press Play, or start a HairBrush build with -mcp.`
          : `Could not reach HairBrush: ${error.code ?? error.message}`));
      });
      socket.on('close', () => {
        clearTimeout(timer);
        if (!greeted) reject(new Error('HairBrush closed the connection during the handshake (token mismatch?).'));
        this.socket = null; this.ready = null; this.app = null;
        for (const p of this.pending.values()) {
          clearTimeout(p.timer);
          p.reject(new Error('Connection to HairBrush was lost. The last edit may have completed; check with hb_status before retrying.'));
        }
        this.pending.clear();
      });
    });
    this.ready.catch(() => { this.ready = null; });
    return this.ready;
  }

  async request(method, args, timeoutMs = DEFAULT_TIMEOUT) {
    await this.connect();
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`HairBrush did not reply to ${method} within ${Math.round(timeoutMs / 1000)}s. It may still complete; check with hb_status before retrying.`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.socket.write(JSON.stringify({ id, method, args }) + '\n');
    });
  }

  close() { this.socket?.destroy(); }
}

/** Shape an app result into MCP tool content. Screenshots become image content. */
export function toContent(result) {
  if (isObject(result) && typeof result.png_base64 === 'string') {
    const { png_base64, ...rest } = result;
    return [
      { type: 'image', data: png_base64, mimeType: 'image/png' },
      { type: 'text', text: JSON.stringify(rest) },
    ];
  }
  return [{ type: 'text', text: JSON.stringify(result, null, 1) }];
}

export class McpServer {
  constructor(link, write) {
    this.link = link; this.write = write;
    this.byName = new Map(TOOLS.map(t => [t.name, t]));
  }
  reply(id, result) { this.write({ jsonrpc: '2.0', id, result }); }
  error(id, code, message) { this.write({ jsonrpc: '2.0', id, error: { code, message } }); }

  async handle(message) {
    if (!isObject(message) || message.jsonrpc !== '2.0' || typeof message.method !== 'string') {
      if (isObject(message) && 'id' in message) this.error(message.id ?? null, -32600, 'Invalid request');
      return;
    }
    const { id, method, params } = message;
    const isRequest = id !== undefined && id !== null;
    if (!isRequest) return; // notifications (initialized, cancelled) need no reply

    switch (method) {
      case 'initialize': {
        const asked = params?.protocolVersion;
        this.reply(id, {
          protocolVersion: PROTOCOLS.includes(asked) ? asked : PROTOCOLS[0],
          capabilities: { tools: { listChanged: false } },
          serverInfo: { name: 'hairbrush', version: VERSION },
          instructions: 'Controls a running HairBrush hair-card groom. Start with hb_status, then hb_head_info. '
            + 'Work in groups (fringe, crown, sides, back...). Place hair with hb_fill_region / hb_place_cards, shape it with hb_set_group_params, '
            + 'direct it with guides (hb_add_guide), and check results with hb_screenshot after each significant change. '
            + 'Every tool call is one undo step in HairBrush. Save with hb_save_project when the user is happy.',
        });
        return;
      }
      case 'ping': this.reply(id, {}); return;
      case 'tools/list':
        this.reply(id, { tools: TOOLS.map(({ timeout, ...t }) => t) });
        return;
      case 'tools/call': {
        const tool = this.byName.get(params?.name);
        if (!tool) { this.error(id, -32602, `Unknown tool: ${params?.name}`); return; }
        const args = params.arguments ?? {};
        if (!isObject(args)) { this.error(id, -32602, 'arguments must be an object'); return; }
        try {
          const result = await this.link.request(tool.name.slice(3), args, tool.timeout ?? DEFAULT_TIMEOUT);
          this.reply(id, { content: toContent(result), isError: false });
        } catch (e) {
          this.reply(id, { content: [{ type: 'text', text: e.message }], isError: true });
        }
        return;
      }
      default: this.error(id, -32601, `Method not found: ${method}`);
    }
  }
}

// node server.mjs --call hb_status '{}'   - one call from a terminal, for checking the link.
// A screenshot is written to the path given by --out instead of being printed.
async function cli(argv) {
  const [name, json = '{}'] = argv.slice(argv.indexOf('--call') + 1);
  const tool = TOOLS.find(t => t.name === name);
  if (!tool) throw new Error(`Unknown tool ${name}. Tools: ${TOOLS.map(t => t.name).join(', ')}`);
  const link = new AppLink(loadConfig());
  try {
    const result = await link.request(name.slice(3), JSON.parse(json), tool.timeout ?? DEFAULT_TIMEOUT);
    const outAt = argv.indexOf('--out');
    if (typeof result.png_base64 === 'string' && outAt >= 0) {
      writeFileSync(argv[outAt + 1], Buffer.from(result.png_base64, 'base64'));
      delete result.png_base64;
    }
    process.stdout.write(JSON.stringify(result, null, 2) + '\n');
  } finally { link.close(); }
}

function main() {
  if (process.argv.includes('--call')) {
    cli(process.argv).catch(e => { process.stderr.write(e.message + '\n'); process.exit(1); });
    return;
  }
  const config = loadConfig();
  const link = new AppLink(config);
  const server = new McpServer(link, msg => process.stdout.write(JSON.stringify(msg) + '\n'));
  let buffer = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', chunk => {
    buffer += chunk;
    let nl;
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl).trim();
      buffer = buffer.slice(nl + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); }
      catch { server.error(null, -32700, 'Parse error'); continue; }
      server.handle(msg).catch(e => log(`handler error: ${e.message}`));
    }
  });
  process.stdin.on('end', () => { link.close(); process.exit(0); });
  log(`v${VERSION} ready; HairBrush expected on 127.0.0.1:${config.port}`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) main();
