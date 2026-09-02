#!/usr/bin/env node

// Live first-paint probe for MishaWeb's isolated test launcher.
//
// Default mode requires MishaWeb.YouTubeProbe.exe because only that launcher
// explicitly constructs BrowserMode.Private. A disposable WebView2 user-data
// folder alone cannot isolate the production app's normal settings. The probe
// drives the private tab over CDP, records early samples, and asks only its exact
// PID to close. Existing matching processes make the probe skip safely.

import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { randomUUID } from 'node:crypto';
import {
  createProbeChildEnvironment,
  createProbeLaunchContract,
  PACKAGED_BROWSER_IMAGE_NAME
} from './ProbeLaunchContract.mjs';

const DEFAULT_URL = 'https://www.youtube.com/watch?v=E2T_jZC1QC8';
const DEFAULT_TIMEOUT_MS = 15_000;
const DEFAULT_SAMPLE_MS = 100;
const SKIP_EXIT_CODE = 2;

function printUsage() {
  console.log(`Usage:
  node desktop.tests/YouTubeFirstPaintProbe.mjs \\
    --exe <path-to-MishaWeb.YouTubeProbe.exe> \\
    --out <output-folder> \\
    [--url <youtube-watch-url>] [--timeout-ms <milliseconds>] \\
    [--sample-ms <milliseconds>] [--keep-profile] [--normal-profile] \
    [--no-screenshots] [--direct-startup] [--reload-after-ms <milliseconds>]

Modes:
  default          Requires MishaWeb.YouTubeProbe.exe; launches an actual
                   BrowserMode.Private window + disposable WebView2 profile
  --normal-profile Real normal MishaWeb profile/UDF; refuses to run while any
                   MishaWeb.exe or matching probe process is already open.
                   This explicit opt-in may update normal browser state.

Output contract:
  stdout: one compact summary JSON object with status PASS, FAIL, or SKIP
  <output-folder>/result.json: complete timing, event, and DOM samples
  <output-folder>/*.png: selected early-frame and diagnostic captures unless
                         --no-screenshots is set

Exit codes:
  0 = PASS
  1 = FAIL (warning became visible or playback did not start)
  2 = SKIP (unsafe to launch, DevTools unavailable, or probe inconclusive)`);
}

function parseArgs(argv) {
  const result = {
    exe: '',
    out: '',
    url: DEFAULT_URL,
    timeoutMs: DEFAULT_TIMEOUT_MS,
    sampleMs: DEFAULT_SAMPLE_MS,
    keepProfile: false,
    normalProfile: false,
    noScreenshots: false,
    directStartup: false,
    reloadAfterMs: null
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--keep-profile') {
      result.keepProfile = true;
      continue;
    }
    if (arg === '--normal-profile') {
      result.normalProfile = true;
      continue;
    }
    if (arg === '--no-screenshots') {
      result.noScreenshots = true;
      continue;
    }
    if (arg === '--direct-startup') {
      result.directStartup = true;
      continue;
    }
    if (arg === '--help' || arg === '-h') {
      printUsage();
      process.exit(0);
    }
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) {
      throw new Error(`Missing value for ${arg}`);
    }
    index += 1;
    if (arg === '--exe') result.exe = value;
    else if (arg === '--out') result.out = value;
    else if (arg === '--url') result.url = value;
    else if (arg === '--timeout-ms') result.timeoutMs = Number.parseInt(value, 10);
    else if (arg === '--sample-ms') result.sampleMs = Number.parseInt(value, 10);
    else if (arg === '--reload-after-ms') result.reloadAfterMs = Number.parseInt(value, 10);
    else throw new Error(`Unknown option: ${arg}`);
  }

  if (!result.exe) throw new Error('--exe is required');
  if (!result.out) throw new Error('--out is required');
  if (!Number.isInteger(result.timeoutMs) || result.timeoutMs < 2_000 || result.timeoutMs > 120_000) {
    throw new Error('--timeout-ms must be an integer from 2000 through 120000');
  }
  if (!Number.isInteger(result.sampleMs) || result.sampleMs < 50 || result.sampleMs > 1_000) {
    throw new Error('--sample-ms must be an integer from 50 through 1000');
  }
  if (result.reloadAfterMs !== null
      && (!Number.isInteger(result.reloadAfterMs)
        || result.reloadAfterMs < 2_000
        || result.reloadAfterMs > result.timeoutMs - 2_000)) {
    throw new Error('--reload-after-ms must leave at least 2000 ms before the timeout');
  }
  if (result.normalProfile && result.keepProfile) {
    throw new Error('--keep-profile is only valid for the default disposable-profile mode');
  }

  let parsedUrl;
  try {
    parsedUrl = new URL(result.url);
  } catch {
    throw new Error('--url must be an absolute HTTPS YouTube watch URL');
  }
  if (parsedUrl.protocol !== 'https:'
      || !['youtube.com', 'www.youtube.com'].includes(parsedUrl.hostname.toLowerCase())
      || parsedUrl.pathname !== '/watch') {
    throw new Error('--url must be an HTTPS youtube.com/watch URL');
  }

  result.exe = path.resolve(result.exe);
  result.out = path.resolve(result.out);
  return result;
}

function runningPidsForImage(imageName) {
  const query = spawnSync(
    'tasklist.exe',
    ['/FI', `IMAGENAME eq ${imageName}`, '/FO', 'CSV', '/NH'],
    { encoding: 'utf8', windowsHide: true });
  if (query.error || query.status !== 0) {
    return { error: query.error?.message || query.stderr?.trim() || `tasklist exited ${query.status}`, pids: [] };
  }

  const pids = [];
  for (const line of query.stdout.split(/\r?\n/)) {
    const match = /^"[^"]+","(\d+)"/.exec(line.trim());
    if (match) pids.push(Number.parseInt(match[1], 10));
  }
  return { error: '', pids };
}

async function reserveLoopbackPort() {
  const server = net.createServer();
  server.unref();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  if (!port) throw new Error('Could not reserve a loopback DevTools port');
  return port;
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function waitForExit(child, timeoutMs) {
  if (child.exitCode !== null || child.signalCode !== null) return true;
  return await Promise.race([
    new Promise(resolve => {
      child.once('exit', () => resolve(true));
      child.once('error', () => resolve(true));
    }),
    delay(timeoutMs).then(() => false)
  ]);
}

async function closeExactProcess(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) {
    return { requested: false, exited: true, method: 'already-exited' };
  }
  if (!Number.isInteger(child.pid)) {
    return { requested: false, exited: true, method: 'spawn-failed' };
  }

  const closeCommand = `$target = Get-Process -Id ${child.pid} -ErrorAction SilentlyContinue; `
    + 'if ($null -eq $target) { [Console]::Write("missing") } '
    + 'else { [Console]::Write($target.CloseMainWindow()) }';
  const close = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', closeCommand],
    { encoding: 'utf8', windowsHide: true, timeout: 10_000 });
  const requested = close.status === 0 && close.stdout.trim().toLowerCase() === 'true';
  const exited = await waitForExit(child, 15_000);
  return {
    requested,
    exited,
    method: 'CloseMainWindow',
    detail: close.error?.message || close.stderr?.trim() || close.stdout.trim()
  };
}

async function waitForDevToolsTarget(port, targetMatches, child, timeoutMs = 20_000) {
  const deadline = Date.now() + timeoutMs;
  let lastError = '';
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) {
      throw new Error(`MishaWeb exited before DevTools attached (exitCode=${child.exitCode}, signal=${child.signalCode})`);
    }
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`, {
        signal: AbortSignal.timeout(1_000)
      });
      if (response.ok) {
        const targets = await response.json();
        const target = targets.find(item => item.type === 'page'
          && typeof item.webSocketDebuggerUrl === 'string'
          && targetMatches(item));
        if (target) return target;
        lastError = `DevTools exposed ${targets.length} target(s), but no attachable page`;
      } else {
        lastError = `DevTools endpoint returned HTTP ${response.status}`;
      }
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
    await delay(100);
  }
  throw new Error(`DevTools target was not available within ${timeoutMs} ms: ${lastError}`);
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
    this.socket.addEventListener('close', () => this.rejectAll(new Error('DevTools WebSocket closed')));
    this.socket.addEventListener('error', () => this.rejectAll(new Error('DevTools WebSocket failed')));
    await Promise.race([
      new Promise((resolve, reject) => {
        this.socket.addEventListener('open', resolve, { once: true });
        this.socket.addEventListener('error', reject, { once: true });
      }),
      delay(5_000).then(() => { throw new Error('DevTools WebSocket connection timed out'); })
    ]);
  }

  on(method, handler) {
    const handlers = this.listeners.get(method) || [];
    handlers.push(handler);
    this.listeners.set(method, handlers);
  }

  onMessage(payload) {
    let message;
    try {
      message = JSON.parse(typeof payload === 'string' ? payload : Buffer.from(payload).toString('utf8'));
    } catch {
      return;
    }
    if (typeof message.id === 'number') {
      const request = this.pending.get(message.id);
      if (!request) return;
      this.pending.delete(message.id);
      clearTimeout(request.timer);
      if (message.error) request.reject(new Error(`${request.method}: ${message.error.message}`));
      else request.resolve(message.result || {});
      return;
    }
    if (message.method) {
      for (const handler of this.listeners.get(message.method) || []) {
        try { handler(message.params || {}); } catch { }
      }
    }
  }

  send(method, params = {}, timeoutMs = 5_000) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error('DevTools WebSocket is not open'));
    }
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method} timed out after ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  rejectAll(error) {
    for (const request of this.pending.values()) {
      clearTimeout(request.timer);
      request.reject(error);
    }
    this.pending.clear();
  }

  close() {
    if (this.socket && this.socket.readyState < WebSocket.CLOSING) this.socket.close();
  }
}

const DOM_SAMPLE_EXPRESSION = String.raw`(() => {
  try {
    const isVisible = element => {
      if (!(element instanceof Element) || !element.isConnected) return false;
      try {
        if (typeof element.checkVisibility === 'function'
            && !element.checkVisibility({
              contentVisibilityAuto: true,
              opacityProperty: true,
              visibilityProperty: true
            })) return false;
      } catch { }

      const viewportWidth = Math.max(
        0,
        document.documentElement?.clientWidth || 0,
        window.innerWidth || 0);
      const viewportHeight = Math.max(
        0,
        document.documentElement?.clientHeight || 0,
        window.innerHeight || 0);
      const elementBounds = element.getBoundingClientRect();
      let visibleLeft = Math.max(0, elementBounds.left);
      let visibleTop = Math.max(0, elementBounds.top);
      let visibleRight = Math.min(viewportWidth, elementBounds.right);
      let visibleBottom = Math.min(viewportHeight, elementBounds.bottom);
      if (elementBounds.width <= 0
          || elementBounds.height <= 0
          || visibleRight <= visibleLeft
          || visibleBottom <= visibleTop) return false;

      const renderedParent = current => {
        if (current.assignedSlot instanceof Element) return current.assignedSlot;
        if (current.parentElement instanceof Element) return current.parentElement;
        const root = current.getRootNode?.();
        return root?.host instanceof Element ? root.host : null;
      };
      for (let current = element; current instanceof Element; current = renderedParent(current)) {
        const style = getComputedStyle(current);
        const visibility = String(style.visibility || '').toLowerCase();
        const contentVisibility = String(style.contentVisibility || '').toLowerCase();
        const opacity = Number.parseFloat(style.opacity || '1');
        if (style.display === 'none'
            || visibility === 'hidden'
            || visibility === 'collapse'
            || contentVisibility === 'hidden'
            || (Number.isFinite(opacity) && opacity <= 0)) return false;

        const bounds = current.getBoundingClientRect();
        if (style.display !== 'contents' && (bounds.width <= 0 || bounds.height <= 0)) {
          return false;
        }

        const clipsX = /^(?:auto|clip|hidden|scroll)$/.test(style.overflowX || '');
        const clipsY = /^(?:auto|clip|hidden|scroll)$/.test(style.overflowY || '');
        if (clipsX) {
          visibleLeft = Math.max(visibleLeft, bounds.left);
          visibleRight = Math.min(visibleRight, bounds.right);
        }
        if (clipsY) {
          visibleTop = Math.max(visibleTop, bounds.top);
          visibleBottom = Math.min(visibleBottom, bounds.bottom);
        }
        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop) return false;
      }
      return true;
    };
    const enforcementSelector = [
      'ytd-enforcement-message-view-model',
      'yt-enforcement-message-view-model',
      'yt-playability-error-supported-renderers#error-screen',
      'tp-yt-paper-dialog'
    ].join(',');
    const warningPattern = /ad\s*blockers?.{0,40}(?:violate|terms of service|allowlist|allow youtube ads|disabled)/i;
    const enforcementNodes = Array.from(document.querySelectorAll(enforcementSelector));
    const visibleWarningNodes = enforcementNodes.filter(node =>
      isVisible(node) && warningPattern.test(node.innerText || node.textContent || ''));
    const genericErrorPattern = /(?:this content isn(?:'|\u2019)?t available|try again later|video unavailable|an error occurred|playback error)/i;
    const genericErrorSelector = [
      '.ytp-error',
      '.ytp-error-content-wrap',
      '.ytp-error-content',
      '#error-screen',
      'yt-playability-error-supported-renderers',
      'ytd-player-error-message-renderer',
      'yt-player-error-message-renderer'
    ].join(',');
    const genericErrorNodes = Array.from(document.querySelectorAll(genericErrorSelector));
    const visibleGenericErrorNodes = genericErrorNodes.filter(node =>
      isVisible(node) && genericErrorPattern.test(node.innerText || node.textContent || ''));
    const player = document.getElementById('movie_player');
    const video = document.querySelector('video.html5-main-video, video');
    let playerResponse = null;
    let playerResponseSource = '';
    try {
      playerResponse = player?.getPlayerResponse?.() || null;
      if (playerResponse) playerResponseSource = 'movie_player.getPlayerResponse';
    } catch { }
    if (!playerResponse && window.ytInitialPlayerResponse) {
      playerResponse = window.ytInitialPlayerResponse;
      playerResponseSource = 'ytInitialPlayerResponse';
    }
    const textValue = value => {
      if (typeof value === 'string') return value;
      if (typeof value?.simpleText === 'string') return value.simpleText;
      if (Array.isArray(value?.runs)) return value.runs.map(run => run?.text || '').join('');
      return '';
    };
    const playability = playerResponse?.playabilityStatus || null;
    const videoDetails = playerResponse?.videoDetails || null;
    const streamingData = playerResponse?.streamingData || null;
    const errorRenderer = playability?.errorScreen?.playerErrorMessageRenderer
      || playability?.errorScreen?.playabilityErrorSupportedRenderers
        ?.playabilityErrorSupportedRenderers?.[0]
      || null;
    let playerState = null;
    let loadedFraction = null;
    let playbackQuality = '';
    let statsForNerds = null;
    try { playerState = Number(player?.getPlayerState?.()); } catch { }
    try { loadedFraction = Number(player?.getVideoLoadedFraction?.()); } catch { }
    try { playbackQuality = String(player?.getPlaybackQuality?.() || ''); } catch { }
    try {
      const stats = player?.getStatsForNerds?.();
      if (stats && typeof stats === 'object') {
        statsForNerds = Object.fromEntries(Object.entries(stats).slice(0, 30));
      }
    } catch { }
    const buffered = [];
    try {
      for (let index = 0; index < Math.min(video?.buffered?.length || 0, 8); index += 1) {
        buffered.push({ start: video.buffered.start(index), end: video.buffered.end(index) });
      }
    } catch { }
    const currentTime = Number(video?.currentTime);
    const duration = Number(video?.duration);
    let bufferAheadSeconds = null;
    if (Number.isFinite(currentTime)) {
      const containingRange = buffered.find(range => range.start <= currentTime && range.end >= currentTime);
      if (containingRange) bufferAheadSeconds = Math.max(0, containingRange.end - currentTime);
    }
    let videoPlaybackQuality = null;
    try {
      const quality = video?.getVideoPlaybackQuality?.();
      if (quality) {
        videoPlaybackQuality = {
          creationTime: Number(quality.creationTime || 0),
          totalVideoFrames: Number(quality.totalVideoFrames || 0),
          droppedVideoFrames: Number(quality.droppedVideoFrames || 0),
          corruptedVideoFrames: Number(quality.corruptedVideoFrames || 0)
        };
      }
    } catch { }
    const playerStateNames = {
      '-1': 'unstarted',
      '0': 'ended',
      '1': 'playing',
      '2': 'paused',
      '3': 'buffering',
      '5': 'cued'
    };
    const normalizedPlayerState = Number.isFinite(playerState) ? playerState : null;
    const playabilityModel = {
      source: playerResponseSource,
      status: playability?.status || '',
      reason: textValue(playability?.reason || errorRenderer?.reason).slice(0, 500),
      subreason: textValue(playability?.subreason || errorRenderer?.subreason).slice(0, 500),
      errorCode: playability?.errorScreen?.playerErrorMessageRenderer?.errorCode
        || playability?.errorCode || null,
      videoId: typeof videoDetails?.videoId === 'string' ? videoDetails.videoId : '',
      hasStreamingData: Boolean(streamingData),
      adaptiveFormatCount: Array.isArray(streamingData?.adaptiveFormats)
        ? streamingData.adaptiveFormats.length : 0,
      muxedFormatCount: Array.isArray(streamingData?.formats) ? streamingData.formats.length : 0,
      isLiveContent: Boolean(videoDetails?.isLiveContent)
    };
    return {
      ok: true,
      href: location.href,
      documentReadyState: document.readyState,
      title: document.title,
      enforcementPresent: enforcementNodes.length > 0,
      warningVisible: visibleWarningNodes.length > 0,
      warningText: visibleWarningNodes[0]?.innerText?.slice(0, 300) || '',
      genericErrorVisible: visibleGenericErrorNodes.length > 0,
      genericErrorText: visibleGenericErrorNodes[0]?.innerText?.slice(0, 500) || '',
      playerTransparent: Boolean(player?.classList?.contains('ytp-transparent')),
      playerUnavailable: Boolean(document.querySelector('ytd-watch-flexy[player-unavailable]')),
      playabilityModel,
      playabilityStatus: playabilityModel.status,
      playabilityReason: playabilityModel.reason,
      playabilitySubreason: playabilityModel.subreason,
      playabilityErrorCode: playabilityModel.errorCode,
      playerState: normalizedPlayerState,
      playerStateModel: {
        code: normalizedPlayerState,
        name: normalizedPlayerState === null ? 'unavailable' : playerStateNames[String(normalizedPlayerState)] || 'unknown',
        buffering: normalizedPlayerState === 3
      },
      loadedFraction: Number.isFinite(loadedFraction) ? loadedFraction : null,
      playbackQuality,
      statsForNerds,
      networkMachineEnabled: window.ytcfg?.data_?.EXPERIMENT_FLAGS
        ?.all_web_enable_network_machine ?? null,
      networkMachineRawRequest: window.ytcfg?.data_?.EXPERIMENT_FLAGS
        ?.all_web_network_machine_raw_request ?? null,
      videoPresent: Boolean(video),
      videoReadyState: Number(video?.readyState || 0),
      videoPaused: typeof video?.paused === 'boolean' ? video.paused : null,
      videoEnded: typeof video?.ended === 'boolean' ? video.ended : null,
      videoSeeking: typeof video?.seeking === 'boolean' ? video.seeking : null,
      videoNetworkState: Number(video?.networkState || 0),
      videoPlaybackRate: Number(video?.playbackRate || 0),
      videoCurrentTime: Number.isFinite(currentTime) ? currentTime : null,
      videoDuration: Number.isFinite(duration) ? duration : null,
      videoWidth: Number(video?.videoWidth || 0),
      videoHeight: Number(video?.videoHeight || 0),
      renderedWidth: Number(video?.clientWidth || 0),
      renderedHeight: Number(video?.clientHeight || 0),
      buffered,
      bufferAheadSeconds,
      videoPlaybackQuality
    };
  } catch (error) {
    return { ok: false, href: location.href, error: String(error?.stack || error) };
  }
})()`;

async function evaluateSample(cdp) {
  const response = await cdp.send('Runtime.evaluate', {
    expression: DOM_SAMPLE_EXPRESSION,
    returnByValue: true,
    awaitPromise: false
  });
  if (response.exceptionDetails) {
    throw new Error(response.exceptionDetails.text || 'Runtime.evaluate failed');
  }
  return response.result?.value || null;
}

async function captureScreenshot(cdp, outputPath) {
  const response = await cdp.send('Page.captureScreenshot', {
    format: 'png',
    fromSurface: true,
    captureBeyondViewport: false
  }, 10_000);
  if (!response.data) throw new Error('Page.captureScreenshot returned no image data');
  await writeFile(outputPath, Buffer.from(response.data, 'base64'));
}

function safeScreenshotLabel(value) {
  return String(value).replace(/[^a-zA-Z0-9_-]+/g, '-').replace(/^-+|-+$/g, '');
}

async function writeResult(outputFolder, result) {
  await mkdir(outputFolder, { recursive: true });
  const resultPath = path.join(outputFolder, 'result.json');
  await writeFile(resultPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
  console.log(JSON.stringify({
    status: result.status,
    reason: result.reason,
    resultPath,
    probeId: result.probeId,
    processId: result.processId || null,
    firstYouTubeSampleMs: result.firstYouTubeSampleMs ?? null,
    firstReadyMs: result.firstReadyMs ?? null,
    firstPlayingMs: result.firstPlayingMs ?? null,
    firstProgressMs: result.firstProgressMs ?? null,
    warningVisibleMs: result.warningVisibleMs ?? null,
    genericErrorVisibleMs: result.genericErrorVisibleMs ?? null,
    playerUnavailableMs: result.playerUnavailableMs ?? null,
    playbackStarted: result.playbackStarted ?? false,
    immediatePlaybackWithin3s: result.immediatePlaybackWithin3s ?? false,
    screenshotsEnabled: result.screenshotsEnabled ?? null,
    profileMode: result.profileMode || null,
    browserMode: result.browserMode || null,
    executableKind: result.executableKind || null,
    close: result.close || null,
    profileRemoved: result.profileRemoved ?? false
  }));
}

function createPhaseTracker(name, startedMs) {
  return {
    name,
    startedMs,
    youtubeSampleCount: 0,
    firstYouTubeSampleMs: null,
    firstReadyMs: null,
    firstPlayingMs: null,
    firstProgressMs: null,
    warningVisibleMs: null,
    genericErrorVisibleMs: null,
    playerUnavailableMs: null,
    baselineCurrentTime: null,
    lastCurrentTime: null,
    maxCurrentTime: null,
    progressAdvanceSeconds: 0,
    lastPlayabilityStatus: '',
    lastPlayabilityReason: '',
    lastPlayabilityModel: null,
    encounteredPlayabilityStatuses: new Set(),
    lastWarningText: '',
    lastGenericErrorText: '',
    lastPlayerState: null,
    lastReadyState: 0,
    lastBuffered: [],
    maxBufferAheadSeconds: null,
    lastResolution: null,
    lastStatsForNerds: null,
    lastVideoPlaybackQuality: null
  };
}

function updatePhaseTracker(tracker, sample, elapsed) {
  const relativeMs = Math.max(0, elapsed - tracker.startedMs);
  tracker.youtubeSampleCount += 1;
  if (tracker.firstYouTubeSampleMs === null) tracker.firstYouTubeSampleMs = relativeMs;
  if (sample.warningVisible && tracker.warningVisibleMs === null) {
    tracker.warningVisibleMs = relativeMs;
  }
  if (sample.genericErrorVisible && tracker.genericErrorVisibleMs === null) {
    tracker.genericErrorVisibleMs = relativeMs;
  }
  if (sample.playerUnavailable && tracker.playerUnavailableMs === null) {
    tracker.playerUnavailableMs = relativeMs;
  }
  if ((sample.videoReadyState > 0 || sample.playabilityStatus === 'OK')
      && tracker.firstReadyMs === null) {
    tracker.firstReadyMs = relativeMs;
  }
  const activelyPlaying = sample.videoPresent
    && (sample.videoPaused === false || sample.playerState === 1);
  if (activelyPlaying && tracker.firstPlayingMs === null) {
    tracker.firstPlayingMs = relativeMs;
  }
  if (typeof sample.videoCurrentTime === 'number') {
    if (tracker.baselineCurrentTime === null) tracker.baselineCurrentTime = sample.videoCurrentTime;
    tracker.maxCurrentTime = tracker.maxCurrentTime === null
      ? sample.videoCurrentTime
      : Math.max(tracker.maxCurrentTime, sample.videoCurrentTime);
    if (typeof tracker.lastCurrentTime === 'number') {
      const incrementalAdvance = sample.videoCurrentTime - tracker.lastCurrentTime;
      if (activelyPlaying && incrementalAdvance > 0 && incrementalAdvance < 5) {
        tracker.progressAdvanceSeconds += incrementalAdvance;
        if (tracker.firstProgressMs === null && incrementalAdvance >= 0.05) {
          tracker.firstProgressMs = relativeMs;
        }
      }
    }
    tracker.lastCurrentTime = sample.videoCurrentTime;
  }
  tracker.lastPlayabilityStatus = sample.playabilityStatus || tracker.lastPlayabilityStatus;
  tracker.lastPlayabilityReason = sample.playabilityReason || tracker.lastPlayabilityReason;
  tracker.lastPlayabilityModel = sample.playabilityModel || tracker.lastPlayabilityModel;
  if (sample.playabilityStatus) tracker.encounteredPlayabilityStatuses.add(sample.playabilityStatus);
  tracker.lastWarningText = sample.warningText || tracker.lastWarningText;
  tracker.lastGenericErrorText = sample.genericErrorText || tracker.lastGenericErrorText;
  tracker.lastPlayerState = sample.playerState;
  tracker.lastReadyState = sample.videoReadyState || 0;
  tracker.lastBuffered = Array.isArray(sample.buffered) ? sample.buffered : [];
  if (typeof sample.bufferAheadSeconds === 'number') {
    tracker.maxBufferAheadSeconds = tracker.maxBufferAheadSeconds === null
      ? sample.bufferAheadSeconds
      : Math.max(tracker.maxBufferAheadSeconds, sample.bufferAheadSeconds);
  }
  tracker.lastResolution = sample.videoWidth > 0 || sample.videoHeight > 0
    ? {
        videoWidth: sample.videoWidth,
        videoHeight: sample.videoHeight,
        renderedWidth: sample.renderedWidth,
        renderedHeight: sample.renderedHeight,
        playbackQuality: sample.playbackQuality || ''
      }
    : tracker.lastResolution;
  tracker.lastStatsForNerds = sample.statsForNerds || tracker.lastStatsForNerds;
  tracker.lastVideoPlaybackQuality = sample.videoPlaybackQuality || tracker.lastVideoPlaybackQuality;
}

function phaseSummary(tracker) {
  const contentAvailable = tracker.warningVisibleMs === null
    && tracker.genericErrorVisibleMs === null
    && (tracker.firstReadyMs !== null || tracker.lastPlayabilityStatus === 'OK');
  const playbackStarted = tracker.firstProgressMs !== null;
  return {
    name: tracker.name,
    startedMs: tracker.startedMs,
    youtubeSampleCount: tracker.youtubeSampleCount,
    firstYouTubeSampleMs: tracker.firstYouTubeSampleMs,
    firstReadyMs: tracker.firstReadyMs,
    firstPlayingMs: tracker.firstPlayingMs,
    firstProgressMs: tracker.firstProgressMs,
    warningVisibleMs: tracker.warningVisibleMs,
    genericErrorVisibleMs: tracker.genericErrorVisibleMs,
    playerUnavailableMs: tracker.playerUnavailableMs,
    warningText: tracker.lastWarningText,
    playabilityStatus: tracker.lastPlayabilityStatus,
    playabilityReason: tracker.lastPlayabilityReason,
    playabilityModel: tracker.lastPlayabilityModel,
    encounteredPlayabilityStatuses: [...tracker.encounteredPlayabilityStatuses],
    genericErrorText: tracker.lastGenericErrorText,
    playerState: tracker.lastPlayerState,
    videoReadyState: tracker.lastReadyState,
    buffered: tracker.lastBuffered,
    maxBufferAheadSeconds: tracker.maxBufferAheadSeconds,
    resolution: tracker.lastResolution,
    statsForNerds: tracker.lastStatsForNerds,
    videoPlaybackQuality: tracker.lastVideoPlaybackQuality,
    playbackStarted,
    contentAvailable,
    accepted: contentAvailable && playbackStarted,
    currentTimeAdvance: tracker.youtubeSampleCount > 0 ? tracker.progressAdvanceSeconds : null
  };
}

function recordSampleTransitions(
  phaseName,
  elapsed,
  sample,
  transitionState,
  playerResponseTransitions,
  playerStateTransitions,
  genericErrorTransitions) {
  const playabilityModel = sample.playabilityModel || null;
  if (playabilityModel
      && (playabilityModel.source
        || playabilityModel.status
        || playabilityModel.reason
        || playabilityModel.videoId)) {
    const responseFingerprint = JSON.stringify([
      playabilityModel.source || '',
      playabilityModel.status || '',
      playabilityModel.reason || '',
      playabilityModel.subreason || '',
      playabilityModel.errorCode ?? null,
      playabilityModel.videoId || '',
      Boolean(playabilityModel.hasStreamingData),
      Number(playabilityModel.adaptiveFormatCount || 0),
      Number(playabilityModel.muxedFormatCount || 0)
    ]);
    if (responseFingerprint !== transitionState.playerResponseFingerprint) {
      transitionState.playerResponseFingerprint = responseFingerprint;
      playerResponseTransitions.push({
        ms: elapsed,
        phase: phaseName,
        model: playabilityModel
      });
    }
  }

  if (sample.playerState !== null
      && sample.playerState !== undefined
      && sample.playerState !== transitionState.playerState) {
    transitionState.playerState = sample.playerState;
    playerStateTransitions.push({
      ms: elapsed,
      phase: phaseName,
      ...(sample.playerStateModel || { code: sample.playerState })
    });
  }

  const errorFingerprint = JSON.stringify([
    Boolean(sample.genericErrorVisible),
    sample.genericErrorText || '',
    Boolean(sample.playerUnavailable)
  ]);
  if (errorFingerprint !== transitionState.genericErrorFingerprint) {
    transitionState.genericErrorFingerprint = errorFingerprint;
    genericErrorTransitions.push({
      ms: elapsed,
      phase: phaseName,
      visible: Boolean(sample.genericErrorVisible),
      text: sample.genericErrorText || '',
      playerUnavailable: Boolean(sample.playerUnavailable)
    });
  }
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    printUsage();
    console.error(error instanceof Error ? error.message : String(error));
    return 2;
  }

  const probeId = randomUUID();
  const profileFolder = path.resolve(os.tmpdir(), `MishaWeb-YouTube-Probe-${probeId}`);
  const bootstrapToken = `mishaweb-probe=${probeId}`;
  const bootstrapUrl = `https://example.com/?${bootstrapToken}`;
  const startupUrl = options.directStartup ? options.url : bootstrapUrl;
  let launchContract;
  try {
    launchContract = createProbeLaunchContract({
      executablePath: options.exe,
      startupUrl,
      normalProfile: options.normalProfile
    });
  } catch (error) {
    printUsage();
    console.error(error instanceof Error ? error.message : String(error));
    return SKIP_EXIT_CODE;
  }
  const imageName = launchContract.imageName;
  const launchMetadata = {
    profileMode: launchContract.profileMode,
    browserMode: launchContract.browserMode,
    executableKind: launchContract.executableKind
  };
  const startedAtUtc = new Date().toISOString();
  let child = null;
  let cdp = null;
  let closeResult = null;
  let result = null;
  let exitCode = SKIP_EXIT_CODE;

  await mkdir(options.out, { recursive: true });

  const guardedImageNames = new Set([imageName]);
  if (options.normalProfile) guardedImageNames.add(PACKAGED_BROWSER_IMAGE_NAME);
  const runningProcesses = [];
  for (const guardedImageName of guardedImageNames) {
    const running = runningPidsForImage(guardedImageName);
    if (running.error) {
      result = {
        status: 'SKIP',
        reason: `Could not safely check for existing ${guardedImageName} processes: ${running.error}`,
        probeId,
        ...launchMetadata,
        startedAtUtc
      };
      await writeResult(options.out, result);
      return SKIP_EXIT_CODE;
    }
    for (const pid of running.pids) runningProcesses.push({ imageName: guardedImageName, pid });
  }
  if (runningProcesses.length > 0) {
    result = {
      status: 'SKIP',
      reason: options.normalProfile
        ? 'The real normal profile is in use; close every MishaWeb window before this opt-in probe'
        : `Existing ${imageName} process(es) would make the isolated launch ambiguous`,
      existingProcesses: runningProcesses,
      probeId,
      ...launchMetadata,
      startedAtUtc
    };
    await writeResult(options.out, result);
    return SKIP_EXIT_CODE;
  }

  try {
    const port = await reserveLoopbackPort();
    const additionalBrowserArguments =
      `--remote-debugging-port=${port} --remote-debugging-address=127.0.0.1 `
      + `--remote-allow-origins=http://127.0.0.1:${port}`;
    const childEnvironment = createProbeChildEnvironment(
      process.env,
      launchContract,
      { profileFolder, additionalBrowserArguments });
    const processLaunchStart = performance.now();
    child = spawn(launchContract.executablePath, [...launchContract.childArguments], {
      cwd: path.dirname(launchContract.executablePath),
      env: childEnvironment,
      stdio: 'ignore',
      windowsHide: false
    });
    child.on('error', () => { });

    const directVideoId = new URL(options.url).searchParams.get('v') || '';
    const target = await waitForDevToolsTarget(
      port,
      item => options.directStartup
        ? directVideoId.length > 0
          && String(item.url || '').includes(directVideoId)
        : String(item.url || '').includes(bootstrapToken),
      child);
    cdp = new CdpClient(target.webSocketDebuggerUrl);
    await cdp.connect();
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');
    await cdp.send('Network.enable', { maxTotalBufferSize: 0, maxResourceBufferSize: 0 });
    await cdp.send('Page.setLifecycleEventsEnabled', { enabled: true });

    const navigationEvents = [];
    const networkFailures = [];
    const httpErrors = [];
    const requestUrls = new Map();
    let reloadTriggeredMs = null;
    const navigationStart = options.directStartup ? processLaunchStart : performance.now();
    const devToolsAttachedAfterProcessMs = Math.round(performance.now() - processLaunchStart);
    const eventTime = () => Math.round(performance.now() - navigationStart);
    cdp.on('Page.frameNavigated', event => {
      if (!event.frame?.parentId) {
        navigationEvents.push({ name: 'frameNavigated', ms: eventTime(), url: event.frame?.url || '' });
      }
    });
    cdp.on('Page.domContentEventFired', () => navigationEvents.push({ name: 'domContent', ms: eventTime() }));
    cdp.on('Page.loadEventFired', () => navigationEvents.push({ name: 'load', ms: eventTime() }));
    cdp.on('Page.lifecycleEvent', event => {
      if (['init', 'firstPaint', 'firstContentfulPaint', 'DOMContentLoaded', 'load'].includes(event.name)) {
        navigationEvents.push({ name: event.name, ms: eventTime(), frameId: event.frameId });
      }
    });
    cdp.on('Network.requestWillBeSent', event => {
      if (event.requestId && event.request?.url) requestUrls.set(event.requestId, event.request.url);
    });
    cdp.on('Network.loadingFailed', event => {
      const url = requestUrls.get(event.requestId) || '';
      requestUrls.delete(event.requestId);
      if (networkFailures.length >= 500) return;
      const ms = eventTime();
      networkFailures.push({
        ms,
        phase: reloadTriggeredMs !== null && ms >= reloadTriggeredMs ? 'reload' : 'initial',
        type: event.type || '',
        url,
        errorText: event.errorText || '',
        canceled: Boolean(event.canceled),
        blockedReason: event.blockedReason || ''
      });
    });
    cdp.on('Network.loadingFinished', event => requestUrls.delete(event.requestId));
    cdp.on('Network.responseReceived', event => {
      if (httpErrors.length >= 500 || Number(event.response?.status || 0) < 400) return;
      httpErrors.push({
        ms: eventTime(),
        phase: reloadTriggeredMs !== null && eventTime() >= reloadTriggeredMs ? 'reload' : 'initial',
        type: event.type || '',
        url: event.response?.url || '',
        status: event.response?.status || 0,
        statusText: event.response?.statusText || '',
        fromServiceWorker: Boolean(event.response?.fromServiceWorker)
      });
    });

    if (!options.directStartup) {
      await cdp.send('Page.navigate', { url: options.url }, 10_000);
    }

    const samples = [];
    const screenshots = [];
    const screenshotCheckpoints = [100, 250, 500, 1_000, 2_000, 5_000];
    const capturedCheckpoints = new Set();
    let warningCaptureTaken = false;
    let genericErrorCaptureTaken = false;
    let playingCaptureTaken = false;
    let firstYouTubeSampleMs = null;
    let firstReadyMs = null;
    let firstPlayingMs = null;
    let firstProgressMs = null;
    let baselineCurrentTime = null;
    let maxCurrentTime = null;
    let warningVisibleMs = null;
    let genericErrorVisibleMs = null;
    let playerUnavailableMs = null;
    const phaseTrackers = [createPhaseTracker('initial', 0)];
    let activePhaseTracker = phaseTrackers[0];
    const transitionStateByPhase = new Map([
      ['initial', {
        playerResponseFingerprint: '',
        playerState: null,
        genericErrorFingerprint: ''
      }]
    ]);
    const playerResponseTransitions = [];
    const playerStateTransitions = [];
    const genericErrorTransitions = [];
    let lastSampleDue = 0;

    while (performance.now() - navigationStart <= options.timeoutMs) {
      const elapsed = Math.round(performance.now() - navigationStart);
      if (elapsed < lastSampleDue) {
        await delay(Math.min(options.sampleMs, lastSampleDue - elapsed));
        continue;
      }
      lastSampleDue += options.sampleMs;

      if (options.reloadAfterMs !== null
          && reloadTriggeredMs === null
          && elapsed >= options.reloadAfterMs) {
        reloadTriggeredMs = elapsed;
        activePhaseTracker = createPhaseTracker('reload', elapsed);
        phaseTrackers.push(activePhaseTracker);
        transitionStateByPhase.set('reload', {
          playerResponseFingerprint: '',
          playerState: null,
          genericErrorFingerprint: ''
        });
        navigationEvents.push({ name: 'reloadRequested', ms: elapsed });
        await cdp.send('Page.reload', { ignoreCache: false }, 10_000);
        await delay(25);
        continue;
      }

      let sample;
      try {
        sample = await evaluateSample(cdp);
      } catch (error) {
        samples.push({ ms: elapsed, ok: false, error: error instanceof Error ? error.message : String(error) });
        await delay(25);
        continue;
      }

      const phaseName = reloadTriggeredMs !== null && elapsed >= reloadTriggeredMs
        ? 'reload'
        : 'initial';
      const timedSample = { ms: elapsed, phase: phaseName, ...sample };
      samples.push(timedSample);
      const onYouTubeWatch = typeof sample?.href === 'string'
        && /^https:\/\/(?:www\.)?youtube\.com\/watch(?:[?#]|$)/i.test(sample.href);
      if (!onYouTubeWatch) continue;
      updatePhaseTracker(activePhaseTracker, sample, elapsed);
      recordSampleTransitions(
        phaseName,
        elapsed,
        sample,
        transitionStateByPhase.get(phaseName),
        playerResponseTransitions,
        playerStateTransitions,
        genericErrorTransitions);
      if (firstYouTubeSampleMs === null) firstYouTubeSampleMs = elapsed;
      if (sample.warningVisible && warningVisibleMs === null) warningVisibleMs = elapsed;
      if (sample.genericErrorVisible && genericErrorVisibleMs === null) genericErrorVisibleMs = elapsed;
      if (sample.playerUnavailable && playerUnavailableMs === null) playerUnavailableMs = elapsed;
      if ((sample.videoReadyState > 0 || sample.playabilityStatus === 'OK') && firstReadyMs === null) {
        firstReadyMs = elapsed;
      }
      if (sample.videoPresent && sample.videoPaused === false && firstPlayingMs === null) {
        firstPlayingMs = elapsed;
      }
      if (typeof sample.videoCurrentTime === 'number') {
        if (baselineCurrentTime === null) baselineCurrentTime = sample.videoCurrentTime;
        maxCurrentTime = maxCurrentTime === null
          ? sample.videoCurrentTime
          : Math.max(maxCurrentTime, sample.videoCurrentTime);
        if (firstProgressMs === null && maxCurrentTime - baselineCurrentTime >= 0.15) {
          firstProgressMs = elapsed;
        }
      }

      if (!options.noScreenshots) {
        const dueCheckpoint = screenshotCheckpoints.find(checkpoint =>
          elapsed >= checkpoint && !capturedCheckpoints.has(checkpoint));
        let label = '';
        if (sample.warningVisible && !warningCaptureTaken) {
          warningCaptureTaken = true;
          label = `warning-${elapsed}ms`;
        } else if (sample.genericErrorVisible && !genericErrorCaptureTaken) {
          genericErrorCaptureTaken = true;
          label = `generic-error-${elapsed}ms`;
        } else if (activePhaseTracker.firstProgressMs !== null && !playingCaptureTaken) {
          playingCaptureTaken = true;
          label = `playing-${elapsed}ms`;
        } else if (dueCheckpoint !== undefined) {
          capturedCheckpoints.add(dueCheckpoint);
          label = `checkpoint-${dueCheckpoint}ms`;
        }
        if (label) {
          const fileName = `${safeScreenshotLabel(label)}.png`;
          try {
            await captureScreenshot(cdp, path.join(options.out, fileName));
            screenshots.push({ ms: elapsed, label, file: fileName });
          } catch (error) {
            screenshots.push({ ms: elapsed, label, error: error instanceof Error ? error.message : String(error) });
          }
        }
      }

      const reloadStillPending = options.reloadAfterMs !== null && reloadTriggeredMs === null;
      const activePhase = phaseSummary(activePhaseTracker);
      const activePhaseElapsed = elapsed - activePhaseTracker.startedMs;
      if (!reloadStillPending
          && activePhase.accepted
          && activePhase.genericErrorVisibleMs === null
          && activePhase.warningVisibleMs === null
          && warningVisibleMs === null
          && genericErrorVisibleMs === null
          && activePhaseElapsed >= Math.max((activePhase.firstProgressMs || 0) + 1_500, 5_500)) {
        break;
      }
    }

    const youtubeSamples = samples.filter(sample => typeof sample.href === 'string'
      && /^https:\/\/(?:www\.)?youtube\.com\/watch(?:[?#]|$)/i.test(sample.href));
    const phases = phaseTrackers.map(phaseSummary);
    const initialPhase = phases[0] || null;
    firstYouTubeSampleMs = initialPhase?.firstYouTubeSampleMs ?? null;
    firstReadyMs = initialPhase?.firstReadyMs ?? null;
    firstPlayingMs = initialPhase?.firstPlayingMs ?? null;
    firstProgressMs = initialPhase?.firstProgressMs ?? null;
    warningVisibleMs = initialPhase?.warningVisibleMs ?? null;
    genericErrorVisibleMs = initialPhase?.genericErrorVisibleMs ?? null;
    playerUnavailableMs = initialPhase?.playerUnavailableMs ?? null;
    const playbackStarted = phases.length > 0 && phases.every(phase => phase.playbackStarted);
    if (youtubeSamples.length === 0) {
      result = {
        status: 'SKIP',
        reason: 'No YouTube watch document became observable before the timeout',
        probeId,
        ...launchMetadata,
        processId: child.pid,
        url: options.url,
        startedAtUtc,
        screenshotsEnabled: !options.noScreenshots,
        devToolsAttachedAfterProcessMs,
        reloadRequestedAfterMs: options.reloadAfterMs,
        reloadTriggeredMs,
        phases,
        navigationEvents,
        networkFailures,
        httpErrors,
        playerResponseTransitions,
        playerStateTransitions,
        genericErrorTransitions,
        screenshots,
        samples
      };
      exitCode = SKIP_EXIT_CODE;
    } else {
      const passed = phases.length > 0 && phases.every(phase => phase.accepted);
      const genericErrorPhase = phases.find(phase => phase.genericErrorVisibleMs !== null);
      const unavailablePhase = phases.find(phase => phase.playerUnavailableMs !== null);
      const warningPhase = phases.find(phase => phase.warningVisibleMs !== null);
      const playabilityErrorPhase = phases.find(phase =>
        phase.encounteredPlayabilityStatuses.includes('ERROR'));
      const stalledPhase = phases.find(phase => !phase.playbackStarted);
      result = {
        status: passed ? 'PASS' : 'FAIL',
        reason: passed
          ? 'Every observed phase stayed visibly error-free and video playback advanced'
          : warningPhase
            ? `The YouTube ad-block enforcement UI became visibly rendered during ${warningPhase.name}`
            : genericErrorPhase
              ? `YouTube visibly rendered a playback error during ${genericErrorPhase.name}: ${genericErrorPhase.genericErrorText || 'unknown error'}`
              : stalledPhase && playabilityErrorPhase
                ? `Video playback did not advance after YouTube returned ERROR during ${playabilityErrorPhase.name}: ${playabilityErrorPhase.playabilityReason || 'no reason supplied'}`
                : stalledPhase && unavailablePhase
                  ? `Video playback did not advance while YouTube kept the player unavailable during ${unavailablePhase.name}`
                  : stalledPhase
                    ? `Video playback did not advance during ${stalledPhase.name} before the timeout`
                    : 'The YouTube phase did not meet the visible-error and playback acceptance checks',
        probeId,
        ...launchMetadata,
        processId: child.pid,
        url: options.url,
        startedAtUtc,
        timeoutMs: options.timeoutMs,
        sampleMs: options.sampleMs,
        screenshotsEnabled: !options.noScreenshots,
        devToolsAttachedAfterProcessMs,
        reloadRequestedAfterMs: options.reloadAfterMs,
        reloadTriggeredMs,
        firstYouTubeSampleMs,
        firstReadyMs,
        firstPlayingMs,
        firstProgressMs,
        warningVisibleMs,
        genericErrorVisibleMs,
        playerUnavailableMs,
        playbackStarted,
        immediatePlaybackWithin3s: firstProgressMs !== null
          && firstYouTubeSampleMs !== null
          && firstProgressMs - firstYouTubeSampleMs <= 3_000,
        currentTimeAdvance: initialPhase?.currentTimeAdvance ?? null,
        phases,
        navigationEvents,
        networkFailures,
        httpErrors,
        playerResponseTransitions,
        playerStateTransitions,
        genericErrorTransitions,
        screenshots,
        samples
      };
      exitCode = passed ? 0 : 1;
    }
  } catch (error) {
    result = {
      status: 'SKIP',
      reason: `Live probe could not attach or complete safely: ${error instanceof Error ? error.message : String(error)}`,
      probeId,
      ...launchMetadata,
      processId: child?.pid || null,
      url: options.url,
      screenshotsEnabled: !options.noScreenshots,
      startedAtUtc
    };
    exitCode = SKIP_EXIT_CODE;
  } finally {
    try { cdp?.close(); } catch { }
    closeResult = await closeExactProcess(child);
    if (!closeResult.exited && result) {
      result.status = 'SKIP';
      result.reason = `${result.reason}; the exact test PID did not close cleanly`;
      exitCode = SKIP_EXIT_CODE;
    }
    if (result) closeResult && (result.close = closeResult);

    if (!options.normalProfile && !options.keepProfile && closeResult?.exited) {
      const temporaryRoot = path.resolve(os.tmpdir());
      const relativeProfile = path.relative(temporaryRoot, profileFolder);
      if (relativeProfile
          && !relativeProfile.startsWith('..')
          && !path.isAbsolute(relativeProfile)
          && path.basename(profileFolder).startsWith('MishaWeb-YouTube-Probe-')) {
        try {
          await rm(profileFolder, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
          if (result) result.profileRemoved = true;
        } catch (error) {
          if (result) result.profileCleanupError = error instanceof Error ? error.message : String(error);
        }
      }
    } else if (!options.normalProfile && result) {
      result.profileFolder = profileFolder;
    }
  }

  await writeResult(options.out, result);
  return exitCode;
}

process.exitCode = await main();
