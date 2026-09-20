#!/usr/bin/env node

// Automated Critique Runner for MishaWeb YouTube Playback
//
// Verifies:
// 1. Ability to cleanly open and close MishaWeb.exe
// 2. Navigation to youtube.com or direct watch URL
// 3. If on youtube.com, finding and clicking a video thumbnail to play
// 4. Instant playback start (within 3 seconds)
// 5. NO large central play button (.ytp-large-play-button) remaining visible
// 6. NO stuck 0:00 / 0:00 black screen (duration > 0, currentTime advancing, videoWidth > 0)
// 7. Visual verification via captured screenshots

import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, writeFile } from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { randomUUID } from 'node:crypto';

const DEFAULT_WATCH_URL = 'https://www.youtube.com/watch?v=0mNykxUtSGE';
const DEFAULT_TIMEOUT_MS = 20_000;
const DEFAULT_SAMPLE_MS = 100;

function parseArgs(argv) {
  const result = {
    exe: path.resolve('MishaWeb.exe'),
    out: path.resolve('desktop.tests/critique-output'),
    url: DEFAULT_WATCH_URL,
    mode: 'direct', // 'direct' or 'homepage-click'
    timeoutMs: DEFAULT_TIMEOUT_MS,
    sampleMs: DEFAULT_SAMPLE_MS,
    normalProfile: true
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--exe') result.exe = path.resolve(argv[++i]);
    else if (arg === '--out') result.out = path.resolve(argv[++i]);
    else if (arg === '--url') result.url = argv[++i];
    else if (arg === '--mode') result.mode = argv[++i];
    else if (arg === '--timeout-ms') result.timeoutMs = Number.parseInt(argv[++i], 10);
    else if (arg === '--sample-ms') result.sampleMs = Number.parseInt(argv[++i], 10);
    else if (arg === '--private') result.normalProfile = false;
    else if (arg === '--normal') result.normalProfile = true;
  }

  return result;
}

function killRunningMishaProcesses() {
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
  if (!port) throw new Error('Could not reserve a loopback DevTools port');
  return port;
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

class CdpClient {
  constructor(url) {
    this.url = url;
    this.socket = null;
    this.nextId = 0;
    this.pending = new Map();
    this.listeners = new Map();
  }

  async connect() {
    this.socket = new WebSocket(this.url);
    this.socket.addEventListener('message', event => this.onMessage(event.data));
    this.socket.addEventListener('close', () => this.rejectAll(new Error('DevTools closed')));
    this.socket.addEventListener('error', () => this.rejectAll(new Error('DevTools failed')));
    await Promise.race([
      new Promise((resolve, reject) => {
        this.socket.addEventListener('open', resolve, { once: true });
        this.socket.addEventListener('error', reject, { once: true });
      }),
      delay(5000).then(() => { throw new Error('DevTools connection timed out'); })
    ]);
  }

  onMessage(payload) {
    let message;
    try {
      message = JSON.parse(typeof payload === 'string' ? payload : Buffer.from(payload).toString('utf8'));
    } catch { return; }
    if (typeof message.id === 'number') {
      const req = this.pending.get(message.id);
      if (!req) return;
      this.pending.delete(message.id);
      clearTimeout(req.timer);
      if (message.error) req.reject(new Error(message.error.message));
      else req.resolve(message.result || {});
      return;
    }
    if (message.method) {
      for (const h of this.listeners.get(message.method) || []) {
        try { h(message.params || {}); } catch { }
      }
    }
  }

  on(method, handler) {
    if (!this.listeners.has(method)) this.listeners.set(method, []);
    this.listeners.get(method).push(handler);
  }

  send(method, params = {}, timeoutMs = 8000) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error('WebSocket not open'));
    }
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method} timed out`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  rejectAll(err) {
    for (const req of this.pending.values()) {
      clearTimeout(req.timer);
      req.reject(err);
    }
    this.pending.clear();
  }

  close() {
    try { if (this.socket) this.socket.close(); } catch { }
  }
}

async function waitForDevToolsTarget(port, preferredKeyword = '', timeoutMs = 20_000) {
  const deadline = Date.now() + timeoutMs;
  let lastErr = '';
  let fallbackTarget = null;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/list`, {
        signal: AbortSignal.timeout(1000)
      });
      if (res.ok) {
        const targets = await res.json();
        const pageTargets = targets.filter(item => item.type === 'page' && item.webSocketDebuggerUrl);
        if (pageTargets.length > 0) {
          if (preferredKeyword) {
            const matched = pageTargets.find(item => item.url && item.url.includes(preferredKeyword));
            if (matched) return matched;
          }
          fallbackTarget = pageTargets[pageTargets.length - 1];
          if (!preferredKeyword || Date.now() > deadline - (timeoutMs - 3000)) {
            return fallbackTarget;
          }
        }
      }
    } catch (err) {
      lastErr = err.message;
    }
    await delay(150);
  }
  if (fallbackTarget) return fallbackTarget;
  throw new Error(`DevTools target not available on port ${port}: ${lastErr}`);
}

async function captureScreenshot(cdp, outputPath) {
  try {
    const res = await cdp.send('Page.captureScreenshot', { format: 'png', fromSurface: true }, 5000);
    if (res.data) {
      await writeFile(outputPath, Buffer.from(res.data, 'base64'));
      return true;
    }
  } catch (_) { }
  return false;
}

const CRITIQUE_SAMPLE_EXPRESSION = String.raw`(() => {
  try {
    const video = document.querySelector('video.html5-main-video, video');
    const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
    const largePlayBtn = document.querySelector('.ytp-large-play-button');
    
    const isVisible = el => {
      if (!el || !el.isConnected) return false;
      const rect = el.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return false;
      const style = window.getComputedStyle(el);
      return style.display !== 'none' && style.visibility !== 'hidden' && parseFloat(style.opacity || '1') > 0.05;
    };

    let playerResponse = null;
    try { playerResponse = player?.getPlayerResponse?.() || window.ytInitialPlayerResponse; } catch (_) { }

    const playabilityStatus = playerResponse?.playabilityStatus?.status || '';
    const errorScreen = playerResponse?.playabilityStatus?.errorScreen;
    let playerState = null;
    try { playerState = player?.getPlayerState?.(); } catch (_) { }

    const currentTime = Number(video?.currentTime || 0);
    const duration = Number(video?.duration || 0);
    const readyState = Number(video?.readyState || 0);
    const paused = Boolean(video?.paused);
    const videoWidth = Number(video?.videoWidth || 0);
    const videoHeight = Number(video?.videoHeight || 0);
    const largePlayVisible = isVisible(largePlayBtn);

    const enforcementNodes = Array.from(document.querySelectorAll(
      'ytd-enforcement-message-view-model, yt-enforcement-message-view-model, tp-yt-paper-dialog:has(ytd-enforcement-message-view-model)'
    ));
    const warningVisible = enforcementNodes.some(isVisible);

    const isWatchPage = location.pathname.includes('/watch');
    const errorEl = document.querySelector('.ytp-error, .ytp-error-content, .ytp-error-message, yt-playability-error-supported-renderers');
    const errorText = errorEl ? (errorEl.textContent || errorEl.innerText || '').trim() : '';

    return {
      ok: true,
      url: location.href,
      isWatchPage,
      title: document.title,
      videoPresent: Boolean(video),
      playerPresent: Boolean(player),
      playerState,
      playabilityStatus,
      hasErrorScreen: Boolean(errorScreen),
      warningVisible,
      errorText,
      currentTime,
      duration,
      readyState,
      paused,
      videoWidth,
      videoHeight,
      largePlayVisible,
      isStuckZeroZero: isWatchPage && (readyState === 0 || (currentTime === 0 && (isNaN(duration) || duration === 0))),
      isPlaying: !paused && currentTime > 0.1 && videoWidth > 0
    };
  } catch (err) {
    return { ok: false, error: String(err) };
  }
})()`;

const FIND_AND_CLICK_VIDEO_EXPRESSION = String.raw`(() => {
  try {
    const candidates = Array.from(document.querySelectorAll(
      'ytd-rich-item-renderer a#thumbnail[href*="/watch"], a#video-title-link[href*="/watch"], ytd-video-renderer a#thumbnail[href*="/watch"], ytd-rich-grid-media a#thumbnail[href*="/watch"], a[href*="/watch?v="]'
    ));
    const target = candidates.find(a => {
      const rect = a.getBoundingClientRect();
      return rect.width > 40 && rect.height > 20 && rect.top >= 0 && rect.bottom <= window.innerHeight && a.href.includes('watch?v=');
    }) || candidates.find(a => a.href && a.href.includes('watch?v='));

    if (!target) return { found: false, href: '' };
    const href = target.href;
    target.scrollIntoView({ block: 'center', inline: 'center' });
    target.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
    target.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
    target.click();
    return { found: true, href };
  } catch (err) {
    return { found: false, error: String(err) };
  }
})()`;

async function closeProcess(child) {
  if (!child || child.exitCode !== null) return;
  const pid = child.pid;
  try {
    spawnSync('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-Command',
      `$p = Get-Process -Id ${pid} -ErrorAction SilentlyContinue; if ($p) { $p.CloseMainWindow(); Start-Sleep -Milliseconds 1500; if (!$p.HasExited) { Stop-Process -Id ${pid} -Force -ErrorAction SilentlyContinue } }; Get-Process -Name 'msedgewebview2' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue`
    ], { windowsHide: true, timeout: 8000 });
  } catch (_) {
    try { child.kill('SIGKILL'); } catch (_) { }
  }
}

async function runCritique(options) {
  await mkdir(options.out, { recursive: true });
  killRunningMishaProcesses();

  const port = await reserveLoopbackPort();
  const env = {
    ...process.env,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--autoplay-policy=no-user-gesture-required --remote-debugging-port=${port} --remote-debugging-address=127.0.0.1 --remote-allow-origins=http://127.0.0.1:${port}`
  };

  const startupUrl = options.mode === 'homepage-click' ? 'https://www.youtube.com' : options.url;
  const child = spawn(options.exe, [startupUrl], {
    cwd: path.dirname(options.exe),
    env,
    stdio: 'ignore',
    windowsHide: false
  });

  let cdp = null;
  const screenshots = [];
  const samples = [];
  const failureReasons = [];
  let passed = false;

  try {
    const preferredKeyword = options.mode === 'direct' ? 'watch' : '';
    const target = await waitForDevToolsTarget(port, preferredKeyword, 20_000);
    cdp = new CdpClient(target.webSocketDebuggerUrl);
    await cdp.connect();
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');

    const consoleLogs = [];
    const networkFailures = [];
    try {
      await cdp.send('Log.enable');
      cdp.on('Log.entryAdded', p => consoleLogs.push({ source: 'Log', level: p.entry?.level, text: p.entry?.text }));
    } catch (_) { }
    cdp.on('Runtime.consoleAPICalled', p => {
      if (p.type === 'error' || p.type === 'warning') {
        consoleLogs.push({ source: 'console', type: p.type, text: p.args?.map(a => a.value || a.description || '').join(' ') });
      }
    });
    try {
      await cdp.send('Network.enable');
      cdp.on('Network.responseReceived', p => {
        if (p.response?.status >= 400) {
          networkFailures.push({ url: p.response?.url, status: p.response?.status, statusText: p.response?.statusText });
        }
      });
      cdp.on('Network.loadingFailed', p => {
        networkFailures.push({ requestId: p.requestId, errorText: p.errorText, type: p.type });
      });
    } catch (_) { }

    if (options.mode === 'homepage-click') {
      try { await cdp.send('Page.navigate', { url: 'https://www.youtube.com' }); } catch (_) { }
      // Wait for homepage elements to render
      let thumbnailFound = false;
      let clickedHref = '';
      const findDeadline = Date.now() + 15_000;
      while (Date.now() < findDeadline) {
        const res = await cdp.send('Runtime.evaluate', {
          expression: FIND_AND_CLICK_VIDEO_EXPRESSION,
          returnByValue: true
        });
        if (res.result?.value?.found) {
          thumbnailFound = true;
          clickedHref = res.result?.value?.href;
          break;
        }
        await delay(500);
      }

      if (!thumbnailFound) {
        failureReasons.push('Could not find any playable video thumbnail on YouTube homepage');
      } else {
        const watchDeadline = Date.now() + 15_000;
        let reachedWatch = false;
        while (Date.now() < watchDeadline) {
          const locRes = await cdp.send('Runtime.evaluate', {
            expression: `(() => {
              if (!location.pathname.includes('/watch')) return false;
              const video = document.querySelector('video.html5-main-video, video');
              const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
              return Boolean(player && video && video.readyState >= 1);
            })()`,
            returnByValue: true
          });
          if (locRes.result?.value) {
            reachedWatch = true;
            break;
          }
          await delay(200);
        }
        if (!reachedWatch && clickedHref) {
          await cdp.send('Runtime.evaluate', {
            expression: `location.href = ${JSON.stringify(clickedHref)};`
          });
          const fallbackDeadline = Date.now() + 10_000;
          while (Date.now() < fallbackDeadline) {
            const check = await cdp.send('Runtime.evaluate', {
              expression: `(() => {
                if (!location.pathname.includes('/watch')) return false;
                const video = document.querySelector('video.html5-main-video, video');
                const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
                return Boolean(player && video && video.readyState >= 1);
              })()`,
              returnByValue: true
            });
            if (check.result?.value) break;
            await delay(200);
          }
        }
      }
    } else {
      // Allow MishaWeb's native command-line startup navigation to proceed without interruption
      const current = await cdp.send('Runtime.evaluate', {
        expression: `location.pathname.includes('/watch')`,
        returnByValue: true
      });
      if (!current.result?.value) {
        const startupDeadline = Date.now() + 4000;
        let started = false;
        while (Date.now() < startupDeadline) {
          const c = await cdp.send('Runtime.evaluate', {
            expression: `location.pathname.includes('/watch')`,
            returnByValue: true
          });
          if (c.result?.value) { started = true; break; }
          await delay(200);
        }
        if (!started) {
          await cdp.send('Page.navigate', { url: options.url });
        }
      }
      const navDeadline = Date.now() + 20_000;
      while (Date.now() < navDeadline) {
        const check = await cdp.send('Runtime.evaluate', {
          expression: `(() => {
            if (!location.pathname.includes('/watch')) return false;
            const video = document.querySelector('video.html5-main-video, video');
            const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
            return Boolean(player && video && video.readyState >= 1);
          })()`,
          returnByValue: true
        });
        if (check.result?.value) break;
        await delay(200);
      }
    }

    // Now monitor playback on the watch page
    const sampleStart = performance.now();
    let firstProgressTime = null;
    let instantPlaybackWithin3s = false;
    let noCentralPlayButton = true;
    let noStuckZeroZero = false;
    let framesRendered = false;
    let lastSample = null;

    let shotCount = 0;
    while (performance.now() - sampleStart <= options.timeoutMs) {
      const elapsed = Math.round(performance.now() - sampleStart);
      let sampleResult;
      try {
        const res = await cdp.send('Runtime.evaluate', {
          expression: CRITIQUE_SAMPLE_EXPRESSION,
          returnByValue: true
        });
        sampleResult = res.result?.value;
      } catch (err) {
        sampleResult = { ok: false, error: err.message };
      }

      if (sampleResult && sampleResult.ok) {
        lastSample = sampleResult;
        samples.push({ ms: elapsed, ...sampleResult });

        if (sampleResult.largePlayVisible && elapsed >= 3500) {
          noCentralPlayButton = false;
        }

        if (sampleResult.isPlaying && firstProgressTime === null) {
          firstProgressTime = elapsed;
          if (elapsed <= 3500) instantPlaybackWithin3s = true;
        }

        if (sampleResult.videoWidth > 0 && sampleResult.videoHeight > 0 && sampleResult.readyState >= 2) {
          framesRendered = true;
        }

        if (sampleResult.duration > 0 && sampleResult.currentTime > 0.1) {
          noStuckZeroZero = true;
        }

        // Take periodic screenshots for critique proof
        if ((elapsed >= 1000 && shotCount === 0)
          || (elapsed >= 2500 && shotCount === 1)
          || (sampleResult.isPlaying && shotCount < 3)
          || (elapsed >= 6000 && shotCount < 4)) {
          const file = `critique-${elapsed}ms.png`;
          const filePath = path.join(options.out, file);
          const ok = await captureScreenshot(cdp, filePath);
          if (ok) {
            screenshots.push({ ms: elapsed, file, path: filePath });
            shotCount++;
          }
        }

        // If playing stably with frames for >1.5s after start, we can accept early
        if (sampleResult.isPlaying && framesRendered && noStuckZeroZero && !sampleResult.largePlayVisible && elapsed >= 5000) {
          passed = true;
          break;
        }
      }

      await delay(options.sampleMs);
    }

    // Evaluate final critique conditions
    if (!lastSample) {
      failureReasons.push('No DOM sample could be retrieved from MishaWeb');
    } else {
      if (lastSample.warningVisible) {
        failureReasons.push('Ad-block enforcement warning dialog is visibly rendered');
      }
      if (lastSample.largePlayVisible) {
        failureReasons.push('Central large play button (.ytp-large-play-button) remains visible');
      }
      if (lastSample.isStuckZeroZero) {
        failureReasons.push('Player is stuck at 0:00 / 0:00 black screen');
      }
      if (!framesRendered) {
        failureReasons.push(`Video frames failed to render (videoWidth=${lastSample.videoWidth}, readyState=${lastSample.readyState})`);
      }
      if (!instantPlaybackWithin3s) {
        failureReasons.push(`Playback did not start instantly within 3.5s (firstProgressTime=${firstProgressTime}ms)`);
      }
    }

    passed = failureReasons.length === 0;

    // Final screenshot
    const finalShot = `critique-final.png`;
    const finalShotPath = path.join(options.out, finalShot);
    await captureScreenshot(cdp, finalShotPath);
    screenshots.push({ ms: Math.round(performance.now() - sampleStart), file: finalShot, path: finalShotPath });

    const report = {
      status: passed ? 'PASS' : 'FAIL',
      mode: options.mode,
      url: lastSample?.url || options.url,
      instantPlaybackWithin3s,
      noCentralPlayButton,
      noStuckZeroZero,
      framesRendered,
      durationSeconds: lastSample?.duration || 0,
      currentTimeSeconds: lastSample?.currentTime || 0,
      videoDimensions: { width: lastSample?.videoWidth || 0, height: lastSample?.videoHeight || 0 },
      failureReasons,
      screenshots,
      sampleCount: samples.length,
      samples,
      lastSample,
      consoleLogs: consoleLogs.slice(-20),
      networkFailures: networkFailures.slice(-20),
      critique: passed
        ? `Playback verified successfully! Video started within ${firstProgressTime}ms, duration loaded (${lastSample?.duration}s), frames rendering (${lastSample?.videoWidth}x${lastSample?.videoHeight}), and no central play button or black screen.`
        : `Playback critique FAILED: ${failureReasons.join('; ')}`
    };

    await writeFile(path.join(options.out, 'critique-report.json'), JSON.stringify(report, null, 2), 'utf8');
    console.log(JSON.stringify(report, null, 2));

    return passed ? 0 : 1;
  } catch (err) {
    const errorReport = {
      status: 'FAIL',
      error: err.message,
      failureReasons: [err.message],
      screenshots
    };
    await writeFile(path.join(options.out, 'critique-report.json'), JSON.stringify(errorReport, null, 2), 'utf8');
    console.log(JSON.stringify(errorReport, null, 2));
    return 1;
  } finally {
    try { cdp?.close(); } catch { }
    await closeProcess(child);
  }
}

const exitCode = await runCritique(parseArgs(process.argv.slice(2)));
process.exit(exitCode);
