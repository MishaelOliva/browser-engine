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

  const url = 'https://www.youtube.com';
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

    await delay(3000);
    // Find thumbnail and click
    const clickRes = await cdp.send('Runtime.evaluate', {
      expression: `(() => {
        const link = document.querySelector('ytd-rich-grid-media a#thumbnail, ytd-video-renderer a#thumbnail');
        if (link) {
          link.click();
          return { clicked: link.href };
        }
        return { clicked: null };
      })()`,
      returnByValue: true
    });
    console.log('Thumbnail click:', JSON.stringify(clickRes.result?.value));

    for (let i = 0; i < 10; i++) {
      await delay(1000);
      const res = await cdp.send('Runtime.evaluate', {
        expression: `(() => {
          const v = document.querySelector('video');
          const p = document.getElementById('movie_player');
          return {
            sec: ${i + 1},
            url: location.href,
            paused: v?.paused,
            currentTime: v?.currentTime,
            readyState: v?.readyState,
            playerState: p?.getPlayerState?.()
          };
        })()`,
        returnByValue: true
      });
      console.log(JSON.stringify(res.result?.value));

      if (i === 3) {
        console.log('--- Calling video.play() and player.playVideo() ---');
        const playRes = await cdp.send('Runtime.evaluate', {
          expression: `(async () => {
            const v = document.querySelector('video');
            const p = document.getElementById('movie_player');
            let vErr = null;
            try {
              if (v) await v.play();
            } catch (e) {
              vErr = e.name + ': ' + e.message;
            }
            let pErr = null;
            try {
              if (p) p.playVideo();
            } catch (e) {
              pErr = e.name + ': ' + e.message;
            }
            return { vErr, pErr, nowPaused: v?.paused };
          })()`,
          awaitPromise: true,
          returnByValue: true
        });
        console.log('Play attempt result:', JSON.stringify(playRes.result?.value));
      }
    }
  } finally {
    killAll();
  }
}

main().catch(console.error);
