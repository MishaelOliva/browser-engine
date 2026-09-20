import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';

function killAll() {
  try {
    spawnSync('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-Command',
      "Get-Process -Name '*misha*', 'msedgewebview2' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 1000"
    ], { windowsHide: true, timeout: 10000 });
  } catch (_) { }
}

async function reserveLoopbackPort() {
  const server = net.createServer();
  server.unref();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise((resolve, reject) => server.close(err => err ? reject(err) : resolve()));
  return port;
}

const delay = ms => new Promise(r => setTimeout(r, ms));

async function waitForDevToolsTarget(port) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/list`);
      if (res.ok) {
        const targets = await res.json();
        const target = targets.find(item => item.type === 'page' && item.webSocketDebuggerUrl);
        if (target) return target;
      }
    } catch (_) { }
    await delay(150);
  }
  throw new Error('DevTools target not found');
}

class CdpClient {
  constructor(url) {
    this.url = url;
    this.socket = null;
    this.nextId = 0;
    this.pending = new Map();
  }
  async connect() {
    this.socket = new WebSocket(this.url);
    this.socket.addEventListener('message', e => this.onMessage(e.data));
    await new Promise((resolve, reject) => {
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', reject, { once: true });
    });
  }
  onMessage(payload) {
    let msg;
    try { msg = JSON.parse(payload); } catch { return; }
    if (typeof msg.id === 'number') {
      const req = this.pending.get(msg.id);
      if (!req) return;
      this.pending.delete(msg.id);
      if (msg.error) req.reject(new Error(msg.error.message));
      else req.resolve(msg.result || {});
    }
  }
  send(method, params = {}) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
}

async function main() {
  killAll();
  const port = await reserveLoopbackPort();
  const env = {
    ...process.env,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port} --remote-debugging-address=127.0.0.1 --remote-allow-origins=http://127.0.0.1:${port}`
  };

  const url = 'https://www.youtube.com/watch?v=0mNykxUtSGE';
  const child = spawn(path.resolve('MishaWeb.exe'), [url], {
    cwd: path.resolve('.'),
    env,
    stdio: 'ignore'
  });

  try {
    const target = await waitForDevToolsTarget(port);
    const cdp = new CdpClient(target.webSocketDebuggerUrl);
    await cdp.connect();
    await cdp.send('Runtime.enable');
    await cdp.send('Page.enable');

    await cdp.send('Runtime.evaluate', {
      expression: `if (location.href !== ${JSON.stringify(url)}) { location.href = ${JSON.stringify(url)}; }`
    });

    // Wait until playabilityStatus is ERROR
    for (let i = 0; i < 10; i++) {
      await delay(1000);
      const res = await cdp.send('Runtime.evaluate', {
        expression: `(() => {
          const p = document.getElementById('movie_player');
          const resp = p?.getPlayerResponse?.() || window.ytInitialPlayerResponse;
          if (!resp) return { ready: false };
          return {
            ready: true,
            status: resp.playabilityStatus?.status,
            playabilityStatus: resp.playabilityStatus,
            hasStreamingData: Boolean(resp.streamingData),
            streamingDataKeys: resp.streamingData ? Object.keys(resp.streamingData) : [],
            videoDetails: resp.videoDetails ? {
              videoId: resp.videoDetails.videoId,
              title: resp.videoDetails.title
            } : null,
            playerConfig: resp.playerConfig ? Object.keys(resp.playerConfig) : []
          };
        })()`,
        returnByValue: true
      });
      if (res.result?.value?.status === 'ERROR') {
        console.log('FOUND ERROR RESPONSE:', JSON.stringify(res.result.value, null, 2));
        break;
      }
    }
  } finally {
    killAll();
  }
}

main().catch(console.error);
