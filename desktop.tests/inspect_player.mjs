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

async function waitForDevToolsTarget(port, timeoutMs = 20000) {
  const deadline = Date.now() + timeoutMs;
  let lastErr = '';
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/list`);
      if (res.ok) {
        const targets = await res.json();
        const target = targets.find(item => item.type === 'page' && item.webSocketDebuggerUrl);
        if (target) return target;
      }
    } catch (err) {
      lastErr = err.message;
    }
    await delay(150);
  }
  throw new Error(`DevTools target not available: ${lastErr}`);
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
  close() {
    try { this.socket?.close(); } catch (_) { }
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

    for (let i = 0; i < 15; i++) {
      await delay(1000);
      const res = await cdp.send('Runtime.evaluate', {
        expression: `(() => {
          const v = document.querySelector('video');
          const p = document.getElementById('movie_player');
          const lp = document.querySelector('.ytp-large-play-button');
          return {
            time: Math.round(performance.now()),
            url: location.href,
            video: v ? {
              src: v.src ? v.src.slice(0, 60) : '',
              currentSrc: v.currentSrc ? v.currentSrc.slice(0, 60) : '',
              paused: v.paused,
              currentTime: v.currentTime,
              duration: v.duration,
              readyState: v.readyState,
              networkState: v.networkState,
              error: v.error ? { code: v.error.code, message: v.error.message } : null,
              width: v.videoWidth,
              height: v.videoHeight
            } : null,
            player: p ? {
              state: typeof p.getPlayerState === 'function' ? p.getPlayerState() : null,
              playabilityStatus: p.getPlayerResponse?.()?.playabilityStatus?.status || null,
              reason: p.getPlayerResponse?.()?.playabilityStatus?.reason || null,
              stats: p.getStatsForNerds?.()?.resolution || null,
              largePlayVisible: lp ? (getComputedStyle(lp).display !== 'none' && lp.offsetWidth > 0) : null
            } : null,
            ytInitialPlayability: window.ytInitialPlayerResponse?.playabilityStatus?.status || null
          };
        })()`,
        returnByValue: true
      });
      console.log(`[Sample ${i + 1}]`, JSON.stringify(res.result?.value));
    }
  } finally {
    killAll();
  }
}

main().catch(console.error);
