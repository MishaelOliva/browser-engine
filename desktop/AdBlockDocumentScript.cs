namespace MishaWeb;

/// <summary>
/// Audited, built-in page protections that must run before site scripts.
/// Remote filter lists are never allowed to inject JavaScript.
/// </summary>
internal static class AdBlockDocumentScript
{
    public const string Source =
        """
        (() => {
            const styleId = '__misha_adblock_style';
            const remoteStyleId = '__misha_adblock_remote_style';
            const controlChannel = '__MISHA_ADBLOCK_CONTROL_CHANNEL__';
            let enabledForDocument = window.__mishaAdBlockEnabled !== false;
            let stopCleanup = () => {};
            let stopRecovery = () => {};
            let stopPristineFrameHook = () => {};
            const bootstrapObservers = [];
            const stopBootstrapObservers = () => {
                for (const observer of bootstrapObservers) observer.disconnect();
                bootstrapObservers.length = 0;
            };
            const isEnabled = () => enabledForDocument;
            try {
                Object.defineProperty(window, '__mishaAdBlockEnabled', {
                    configurable: false,
                    enumerable: false,
                    get: () => enabledForDocument,
                    set: () => {}
                });
            } catch (_) { }
            const disableForDocument = () => {
                enabledForDocument = false;
                stopBootstrapObservers();
                stopCleanup();
                stopRecovery();
                stopPristineFrameHook();
                document.getElementById(styleId)?.remove();
                document.getElementById(remoteStyleId)?.remove();
            };
            window.addEventListener(controlChannel, disableForDocument);

            const isYouTubeHost = value => value === 'youtube.com'
                || value.endsWith('.youtube.com')
                || value === 'youtube-nocookie.com'
                || value.endsWith('.youtube-nocookie.com')
                || value === 'youtubekids.com'
                || value.endsWith('.youtubekids.com');
            const host = location.hostname.toLowerCase();
            const isYouTube = isYouTubeHost(host);
            const isYouTubeWatch = () => host === 'www.youtube.com'
                && location.pathname === '/watch';
            const recoveryMaskAttribute = 'data-misha-youtube-recovery';
            let recoveryMaskActive = false;
            let recoveryMaskVerified = false;
            let recoveryMaskTimer = 0;
            const syncRecoveryMask = () => {
                if (!document.documentElement) return;
                if (!recoveryMaskActive) {
                    document.documentElement.removeAttribute?.(recoveryMaskAttribute);
                    return;
                }
                document.documentElement.setAttribute?.(
                    recoveryMaskAttribute,
                    recoveryMaskVerified ? 'verified' : '');
            };
            const clearRecoveryMask = () => {
                recoveryMaskActive = false;
                recoveryMaskVerified = false;
                window.clearTimeout(recoveryMaskTimer);
                recoveryMaskTimer = 0;
                syncRecoveryMask();
            };
            const armRecoveryMask = (verified = false) => {
                if (!isEnabled() || !isYouTubeWatch()) return;
                const becameVerified = verified && !recoveryMaskVerified;
                recoveryMaskActive = true;
                recoveryMaskVerified ||= verified;
                syncRecoveryMask();
                if (becameVerified) {
                    window.clearTimeout(recoveryMaskTimer);
                    recoveryMaskTimer = 0;
                }
                if (recoveryMaskTimer) return;
                // Fail open if YouTube never delivers a follow-up player response.
                recoveryMaskTimer = window.setTimeout(clearRecoveryMask, 15_000);
            };

            // Curated list rules and the audited selectors below cover YouTube.
            // Keep these broad fallback heuristics off its application shell so
            // ordinary watch-page controls cannot be mistaken for ad chrome.
            const selectors = isYouTube ? [] : [
                '[id^="ad-"]', '[id*="-ad-"]', '[id$="-ad"]', '[id="ad"]',
                '[class^="ad-"]', '[class*=" ad-"]', '[class$="-ad"]',
                '[id*="advert"]', '[class*="advert"]', '[id*="sponsor"]',
                '[class*="sponsor"]', '[data-ad]', '[data-ad-slot]',
                '[data-ad-client]', '[data-google-query-id]',
                '[aria-label="Advertisement"]', '[aria-label*="advertisement"]',
                '.adsbygoogle', '.advertisement', '.advert', '.ad-banner',
                '.ad-container', '.ad-wrapper', '.ad-unit', '.popunder',
                '.popup-ad', '.ad-overlay', '.sponsor-ad',
                'iframe[src*="doubleclick"]', 'iframe[src*="googlesyndication"]',
                'iframe[src*="adservice"]', 'iframe[src*="adsrvr"]'
            ];

            if (isYouTube) {
                selectors.push(
                    '#masthead-ad', '#panels ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]',
                    '.video-ads', '.ytp-ad-module', '.ytp-ad-overlay-container', '.ytp-ad-progress-list',
                    'ytd-ad-slot-renderer', 'ytd-display-ad-renderer', 'ytd-promoted-sparkles-web-renderer',
                    'ytd-in-feed-ad-layout-renderer', 'ytd-search-pyv-renderer',
                    'ytd-banner-promo-renderer', 'ytd-statement-banner-renderer',
                    'ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer)',
                    '#shorts-inner-container > .ytd-shorts:has(ytd-ad-slot-renderer)',
                    'ytm-companion-ad-renderer', 'ytm-companion-slot', 'ytm-promoted-sparkles-web-renderer',
                    'ytm-rich-item-renderer:has(ad-slot-renderer)', 'ad-slot-renderer'
                );
            }

            const installStyle = () => {
                if (!isEnabled()) {
                    stopBootstrapObservers();
                    return;
                }
                if (!document.documentElement || document.getElementById(styleId)) return;
                const style = document.createElement('style');
                style.id = styleId;
                style.textContent = selectors.join(',')
                    + '{display:none !important; visibility:hidden !important; min-height:0 !important;}'
                    + `html[${recoveryMaskAttribute}] ytd-enforcement-message-view-model,`
                    + `html[${recoveryMaskAttribute}] yt-enforcement-message-view-model,`
                    + `html[${recoveryMaskAttribute}] tp-yt-paper-dialog:has(ytd-enforcement-message-view-model),`
                    + `html[${recoveryMaskAttribute}] tp-yt-paper-dialog:has(yt-enforcement-message-view-model)`
                    + '{display:none !important; visibility:hidden !important; pointer-events:none !important;}'
                    // A generic player error is hidden only after the strict
                    // transport-stall predicate verifies that a bounded retry is
                    // underway. The document-start pre-arm cannot hide real errors.
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error,`
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error-content-wrap,`
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error-content,`
                    + `html[${recoveryMaskAttribute}="verified"] `
                    + 'yt-playability-error-supported-renderers#error-screen'
                    + '{display:none !important; visibility:hidden !important; pointer-events:none !important;}';
                document.documentElement.appendChild(style);
                if (isYouTubeWatch() && !recoveryMaskActive) {
                    // Pre-arm before the parser can paint a player error. The first
                    // classified non-enforcement error removes this mask immediately.
                    armRecoveryMask();
                }
                if (recoveryMaskActive) {
                    syncRecoveryMask();
                }
            };
            installStyle();
            if (!document.documentElement) {
                const styleBootstrapObserver = new MutationObserver(() => {
                    installStyle();
                    if (document.documentElement || !isEnabled()) {
                        styleBootstrapObserver.disconnect();
                    }
                });
                bootstrapObservers.push(styleBootstrapObserver);
                styleBootstrapObserver.observe(document, { childList: true });
            }
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', installStyle, { once: true });
            }

            if (!isYouTube || !isEnabled()) return;

            // Current uBO quick fixes disable YouTube's delayed network-machine
            // detector before it can replace an already-playable response with an
            // enforcement error. Trap only the two audited flags; do not patch
            // Promise/Map prototypes or unrelated experiment configuration.
            const patchedYtcfgObjects = new WeakSet();
            const patchedYtcfgData = new WeakSet();
            const forceFalseFlag = (flags, name) => {
                if (!flags || typeof flags !== 'object') return;
                try {
                    const descriptor = Object.getOwnPropertyDescriptor(flags, name);
                    if (descriptor && !descriptor.configurable) {
                        flags[name] = false;
                        return;
                    }
                    Object.defineProperty(flags, name, {
                        configurable: true,
                        enumerable: descriptor?.enumerable ?? true,
                        get: () => false,
                        set: () => {}
                    });
                } catch (_) {
                    try { flags[name] = false; } catch (_) { }
                }
            };
            const patchExperimentFlags = flags => {
                forceFalseFlag(flags, 'all_web_enable_network_machine');
                forceFalseFlag(flags, 'all_web_network_machine_raw_request');
            };
            const patchYtcfgData = data => {
                if (!data || typeof data !== 'object' || patchedYtcfgData.has(data)) return;
                patchedYtcfgData.add(data);
                try {
                    let storedFlags = data.EXPERIMENT_FLAGS;
                    const descriptor = Object.getOwnPropertyDescriptor(data, 'EXPERIMENT_FLAGS');
                    if (!descriptor || descriptor.configurable) {
                        Object.defineProperty(data, 'EXPERIMENT_FLAGS', {
                            configurable: true,
                            enumerable: descriptor?.enumerable ?? true,
                            get: () => storedFlags,
                            set: value => {
                                storedFlags = value;
                                patchExperimentFlags(value);
                            }
                        });
                    }
                    patchExperimentFlags(storedFlags);
                } catch (_) { patchExperimentFlags(data.EXPERIMENT_FLAGS); }
            };
            const patchYtcfg = config => {
                if (!config || typeof config !== 'object' || patchedYtcfgObjects.has(config)) return;
                patchedYtcfgObjects.add(config);
                try {
                    let storedData = config.data_;
                    const descriptor = Object.getOwnPropertyDescriptor(config, 'data_');
                    if (!descriptor || descriptor.configurable) {
                        Object.defineProperty(config, 'data_', {
                            configurable: true,
                            enumerable: descriptor?.enumerable ?? true,
                            get: () => storedData,
                            set: value => {
                                storedData = value;
                                patchYtcfgData(value);
                            }
                        });
                    }
                    patchYtcfgData(storedData);
                } catch (_) { patchYtcfgData(config.data_); }
            };
            try {
                let storedYtcfg = window.ytcfg;
                const descriptor = Object.getOwnPropertyDescriptor(window, 'ytcfg');
                if (!descriptor || descriptor.configurable) {
                    Object.defineProperty(window, 'ytcfg', {
                        configurable: true,
                        enumerable: descriptor?.enumerable ?? true,
                        get: () => storedYtcfg,
                        set: value => {
                            storedYtcfg = value;
                            patchYtcfg(value);
                        }
                    });
                }
                patchYtcfg(storedYtcfg);
            } catch (_) { patchYtcfg(window.ytcfg); }

            // Avoid wrapping and traversing ordinary browse, search, comments,
            // and account responses. Player-bearing endpoints are the only
            // fetch/XHR payloads that need the deeper metadata sanitizer.
            const playerPayloadPath = /\/(?:youtubei\/v1\/(?:player|get_watch)|youtubei\/v1\/reel\/(?:reel_watch_sequence|reel_item_watch)|get_video_info|playlist|watch)(?:[/?]|$)/;
            const requestUrl = request => {
                try {
                    if (typeof request === 'string') return request;
                    if (request && typeof request.url === 'string') return request.url;
                    return request instanceof URL ? request.href : '';
                } catch (_) { return ''; }
            };
            const shouldSanitizePlayerPayload = request => {
                const value = requestUrl(request);
                if (!value || !playerPayloadPath.test(value)) return false;
                try {
                    const target = new URL(value, location.href);
                    return isYouTubeHost(target.hostname.toLowerCase())
                        && playerPayloadPath.test(target.pathname);
                } catch (_) { return false; }
            };

            const adKeys = new Set([
                'adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams'
            ]);
            const isAdEntry = value => !!value && typeof value === 'object' && (
                value.adSlotRenderer
                || value.richItemRenderer?.content?.adSlotRenderer
                || value.command?.reelWatchEndpoint?.adClientParams?.isAd
            );
            const sanitizedObjects = new WeakSet();

            const sanitize = value => {
                if (!isEnabled()
                    || !value
                    || typeof value !== 'object'
                    || sanitizedObjects.has(value)) return value;
                const pending = [value];
                const seen = new WeakSet();
                let visited = 0;
                while (pending.length && visited++ < 25000) {
                    const current = pending.pop();
                    if (!current
                        || typeof current !== 'object'
                        || seen.has(current)
                        || sanitizedObjects.has(current)) continue;
                    seen.add(current);

                    if (Array.isArray(current)) {
                        for (let index = current.length - 1; index >= 0; index--) {
                            const item = current[index];
                            if (isAdEntry(item)) {
                                current.splice(index, 1);
                            } else if (item && typeof item === 'object') {
                                pending.push(item);
                            }
                        }
                        continue;
                    }

                    for (const key of Object.keys(current)) {
                        if (adKeys.has(key)) {
                            try { delete current[key]; }
                            catch (_) { try { current[key] = undefined; } catch (_) { } }
                            continue;
                        }
                        const child = current[key];
                        if (child && typeof child === 'object') pending.push(child);
                    }
                }
                if (pending.length === 0) sanitizedObjects.add(value);
                return value;
            };

            const sanitizePayload = value => {
                if (!isEnabled() || typeof value !== 'string'
                    || !/"(?:adPlacements|playerAds|adSlots|adSlotRenderer|adBreakHeartbeatParams)"/.test(value)) {
                    return sanitize(value);
                }
                try { return JSON.stringify(sanitize(JSON.parse(value))); }
                catch (_) { return value; }
            };

            const getRecoveryContract = playerResponse => {
                const videoId = playerResponse?.videoDetails?.videoId
                    || new URLSearchParams(location.search).get('v')
                    || '';
                const status = playerResponse?.playabilityStatus;
                const errorScreen = status?.errorScreen;
                const captcha = errorScreen?.playerErrorMessageRenderer?.playerCaptchaViewModel;
                const description = JSON.stringify(
                    errorScreen?.playerErrorMessageRenderer?.subreason?.runs
                    || errorScreen?.playerInterstitialRenderer?.content
                        ?.interstitialViewModel?.description?.commandRuns
                    || []);
                const exactContractError = status?.status === 'UNPLAYABLE'
                    && !captcha
                    && description.includes('WEB_PAGE_TYPE_UNKNOWN')
                    && description.includes('https://support.google.com/youtube/answer/3037019');
                const enforcement = errorScreen?.enforcementMessageViewModel;
                const enforcementCommands = enforcement?.primaryButton?.onTap
                    ?.parallelCommand?.commands;
                const exactAdBlockEnforcement = status?.status === 'ERROR'
                    && enforcement
                    && enforcement.isVisible !== false
                    && (enforcementCommands?.some?.(command =>
                        command?.innertubeCommand?.openAdAllowlistInstructionCommand)
                        || /ad[ -]?block/i.test(enforcement.title?.content || ''));
                return {
                    videoId,
                    status,
                    exactContractError,
                    exactAdBlockEnforcement: !!exactAdBlockEnforcement
                };
            };
            let pendingRecoverySignal = false;
            let serverRecoveryExhausted = false;
            let queuePlayerRecovery = () => { pendingRecoverySignal = true; };
            const inspectPlayerResponse = playerResponse => {
                const contract = getRecoveryContract(playerResponse);
                if (!contract.videoId) return;
                if (contract.exactAdBlockEnforcement) {
                    if (serverRecoveryExhausted) return;
                    // Verify the exact model before hiding any YouTube error surface.
                    armRecoveryMask(true);
                }
                if (contract.exactAdBlockEnforcement || recoveryMaskActive) {
                    queuePlayerRecovery();
                }
            };

            const trapGlobal = name => {
                try {
                    let stored = sanitize(window[name]);
                    Object.defineProperty(window, name, {
                        configurable: true,
                        enumerable: true,
                        get: () => stored,
                        set: value => {
                            stored = sanitize(value);
                            inspectPlayerResponse(stored);
                        }
                    });
                    inspectPlayerResponse(stored);
                } catch (_) { }
            };
            trapGlobal('ytInitialPlayerResponse');
            trapGlobal('ytInitialData');
            trapGlobal('playerResponse');

            const recoveryMarkers = ['channel', 'lactmilli'];
            let recoveryVideoId = '';
            let recoveryAttempt = 0;
            let seenRecoveryResponses = new WeakSet();
            let originalClientUserAgent = null;
            let recoveryClient = null;
            let recoveryClientHadScreen = false;
            let originalClientScreen;
            let activeRecoveryMarker = '';
            let activeRecoveryMarkerOwner = '';
            let transportRecoveryVideoId = '';
            let transportRecoveryAttempt = 0;
            let transportRecoveryResponse = null;
            let transportRecoveryAttemptAt = 0;
            let transportRecoveryStartSeconds = 0;
            let transportRecoveryBaselineTime = Number.NaN;
            let transportRecoveryBaselineBufferedEnd = 0;
            let transportRecoveryHasPlayed = false;
            let transportRecoveryTerminal = false;
            let transportRecoveryErrorNode = null;
            let transportRecoveryFastPollUntil = isYouTubeWatch()
                ? performance.now() + 12_000
                : 0;
            const isPremium = () => window.ytInitialData?.topbar?.desktopTopbarRenderer
                ?.logo?.topbarLogoRenderer?.iconImage?.iconType === 'YOUTUBE_PREMIUM_LOGO';
            const parseStartSeconds = value => {
                const normalized = String(value || '').trim().toLowerCase();
                if (!normalized) return 0;
                if (/^\d+(?:\.\d+)?s?$/.test(normalized)) {
                    const numeric = Number.parseFloat(normalized);
                    return Number.isFinite(numeric) && numeric >= 0 ? numeric : 0;
                }
                const match = /^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/.exec(normalized);
                if (!match || !match.slice(1).some(Boolean)) return 0;
                const seconds = Number(match[1] || 0) * 3600
                    + Number(match[2] || 0) * 60
                    + Number(match[3] || 0);
                return Number.isFinite(seconds) && seconds >= 0 ? seconds : 0;
            };
            const getRequestedStartSeconds = playerResponse => {
                const query = new URLSearchParams(location.search);
                const requested = query.get('t') ?? query.get('start');
                if (requested !== null) return parseStartSeconds(requested);
                const configured = Number(
                    playerResponse?.playerConfig?.playbackStartConfig?.startSeconds);
                if (Number.isFinite(configured) && configured > 0) return configured;
                return 0;
            };
            const resetTransportRecovery = (videoId = '', playerResponse = null) => {
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                if (transportRecoveryAttempt > 0 && recoveryMaskVerified) clearRecoveryMask();
                transportRecoveryVideoId = videoId;
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = 0;
                transportRecoveryStartSeconds = getRequestedStartSeconds(playerResponse);
                transportRecoveryBaselineTime = Number.NaN;
                transportRecoveryBaselineBufferedEnd = 0;
                transportRecoveryHasPlayed = false;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = isYouTubeWatch()
                    ? performance.now() + 12_000
                    : 0;
            };
            const restoreRecoveryClient = () => {
                if (!recoveryClient) return;
                try {
                    if (originalClientUserAgent !== null) {
                        recoveryClient.userAgent = originalClientUserAgent;
                    }
                    if (recoveryClientHadScreen) {
                        recoveryClient.clientScreen = originalClientScreen;
                    } else {
                        delete recoveryClient.clientScreen;
                    }
                } catch (_) { }
            };
            const setRecoveryMarker = (marker, owner = 'server') => {
                if (!marker) {
                    restoreRecoveryClient();
                    recoveryClient = null;
                    originalClientUserAgent = null;
                    recoveryClientHadScreen = false;
                    originalClientScreen = undefined;
                    activeRecoveryMarker = '';
                    activeRecoveryMarkerOwner = '';
                    return true;
                }
                const client = window.ytcfg?.data_?.INNERTUBE_CONTEXT?.client;
                if (!client) return false;
                if (recoveryClient && recoveryClient !== client) {
                    restoreRecoveryClient();
                    recoveryClient = null;
                    originalClientUserAgent = null;
                }
                if (!recoveryClient) {
                    recoveryClient = client;
                    originalClientUserAgent = String(client.userAgent || '');
                    recoveryClientHadScreen = Object.prototype.hasOwnProperty.call(
                        client,
                        'clientScreen');
                    originalClientScreen = client.clientScreen;
                }
                try {
                    if (recoveryClientHadScreen) client.clientScreen = originalClientScreen;
                    else delete client.clientScreen;
                    const marked = originalClientUserAgent.replace(
                        /(Mozilla\/5\.0 \([^)]+)/,
                        `$1; ${marker}`);
                    client.userAgent = marked === originalClientUserAgent
                        ? `${originalClientUserAgent} ${marker}`.trim()
                        : marked;
                    if (marker === 'channel') client.clientScreen = 'CHANNEL';
                    activeRecoveryMarker = marker;
                    activeRecoveryMarkerOwner = owner;
                    return client.userAgent.includes(marker)
                        && (marker !== 'channel' || client.clientScreen === 'CHANNEL');
                } catch (_) {
                    setRecoveryMarker('');
                    return false;
                }
            };
            stopRecovery = () => {
                clearRecoveryMask();
                setRecoveryMarker('');
                recoveryAttempt = recoveryMarkers.length;
                serverRecoveryExhausted = true;
                resetTransportRecovery();
                transportRecoveryAttempt = recoveryMarkers.length;
                transportRecoveryTerminal = true;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                pendingRecoverySignal = false;
            };
            const rewritePlayerRequest = (url, bodyText) => {
                if (!isEnabled()
                    || !activeRecoveryMarker
                    || typeof bodyText !== 'string'
                    || !/\/youtubei\/v1\/player(?:\?|$)/.test(url || '')) return bodyText;
                try {
                    const body = JSON.parse(bodyText);
                    const client = body?.context?.client;
                    if (!body || !client) return bodyText;
                    if (activeRecoveryMarker === 'channel') client.clientScreen = 'CHANNEL';
                    if (activeRecoveryMarker === 'lactmilli') {
                        body.params = '8AUB';
                        const playback = body.playbackContext?.contentPlaybackContext;
                        if (playback) playback.lactMilliseconds = String(Date.now());
                    }
                    if (typeof client.referer === 'string'
                        && !client.referer.includes('#reloadxhr')) {
                        client.referer += '#reloadxhr';
                    }
                    return JSON.stringify(body);
                } catch (_) { return bodyText; }
            };

            const patchResponse = (response, request) => {
                if (!isEnabled()
                    || !response
                    || response.__mishaAdBlockPatched
                    || (!shouldSanitizePlayerPayload(request)
                        && !shouldSanitizePlayerPayload(response.url))) return response;
                try {
                    const nativeJson = response.json.bind(response);
                    Object.defineProperty(response, 'json', {
                        configurable: true,
                        value: (...args) => nativeJson(...args).then(sanitize)
                    });
                    const nativeText = response.text.bind(response);
                    Object.defineProperty(response, 'text', {
                        configurable: true,
                        value: (...args) => nativeText(...args).then(sanitizePayload)
                    });
                    const nativeClone = response.clone.bind(response);
                    Object.defineProperty(response, 'clone', {
                        configurable: true,
                        value: (...args) => patchResponse(nativeClone(...args), request)
                    });
                    Object.defineProperty(response, '__mishaAdBlockPatched', { value: true });
                } catch (_) { }
                return response;
            };

            try {
                const nativeFetch = window.fetch;
                window.fetch = new Proxy(nativeFetch, {
                    apply(target, thisArg, args) {
                        try {
                            const url = requestUrl(args[0]);
                            const body = args[1]?.body;
                            const rewrittenBody = rewritePlayerRequest(url, body);
                            if (rewrittenBody !== body) {
                                args[1] = { ...(args[1] || {}), body: rewrittenBody };
                            }
                        } catch (_) { }
                        const pending = Reflect.apply(target, thisArg, args);
                        return shouldSanitizePlayerPayload(args[0])
                            ? pending.then(response => patchResponse(response, args[0]))
                            : pending;
                    }
                });
            } catch (_) { }

            try {
                const requestUrls = new WeakMap();
                const nativeOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = new Proxy(nativeOpen, {
                    apply(target, thisArg, args) {
                        try { requestUrls.set(thisArg, new URL(args[1], location.href).href); }
                        catch (_) { }
                        return Reflect.apply(target, thisArg, args);
                    }
                });
                const responseDescriptor = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'response');
                if (responseDescriptor?.get && responseDescriptor.configurable) {
                    Object.defineProperty(XMLHttpRequest.prototype, 'response', {
                        configurable: true,
                        enumerable: responseDescriptor.enumerable,
                        get() {
                            const value = responseDescriptor.get.call(this);
                            return shouldSanitizePlayerPayload(requestUrls.get(this))
                                ? sanitizePayload(value)
                                : value;
                        }
                    });
                }
                const responseTextDescriptor = Object.getOwnPropertyDescriptor(
                    XMLHttpRequest.prototype,
                    'responseText');
                if (responseTextDescriptor?.get && responseTextDescriptor.configurable) {
                    Object.defineProperty(XMLHttpRequest.prototype, 'responseText', {
                        configurable: true,
                        enumerable: responseTextDescriptor.enumerable,
                        get() {
                            const value = responseTextDescriptor.get.call(this);
                            return shouldSanitizePlayerPayload(requestUrls.get(this))
                                ? sanitizePayload(value)
                                : value;
                        }
                    });
                }
                const nativeSend = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.send = new Proxy(nativeSend, {
                    apply(target, thisArg, args) {
                        try {
                            const url = requestUrls.get(thisArg);
                            args[0] = rewritePlayerRequest(url, args[0]);
                            if (shouldSanitizePlayerPayload(url)) {
                                thisArg.addEventListener(
                                    'load',
                                    () => sanitize(thisArg.response),
                                    { once: true });
                            }
                        } catch (_) { }
                        return Reflect.apply(target, thisArg, args);
                    }
                });
            } catch (_) { }

            // YouTube sometimes reads request functions from a sandboxed
            // about:blank frame. Intercept that iframe-specific read instead of
            // proxying Node.appendChild for every DOM insertion on the page.
            try {
                const contentWindowDescriptor = Object.getOwnPropertyDescriptor(
                    HTMLIFrameElement.prototype,
                    'contentWindow');
                if (contentWindowDescriptor?.get && contentWindowDescriptor.configurable) {
                    const patchedFrameWindows = new WeakSet();
                    const patchFrameWindow = frameWindow => {
                        if (!isEnabled()
                            || !frameWindow
                            || patchedFrameWindows.has(frameWindow)) return frameWindow;
                        try {
                            frameWindow.fetch = window.fetch;
                            frameWindow.Request = window.Request;
                            frameWindow.XMLHttpRequest = window.XMLHttpRequest;
                            patchedFrameWindows.add(frameWindow);
                        } catch (_) { }
                        return frameWindow;
                    };
                    const contentWindowGetter = function() {
                        return patchFrameWindow(contentWindowDescriptor.get.call(this));
                    };
                    Object.defineProperty(HTMLIFrameElement.prototype, 'contentWindow', {
                        ...contentWindowDescriptor,
                        get: contentWindowGetter
                    });
                    const patchLoadedFrame = event => {
                        const frame = event.target;
                        if (frame instanceof HTMLIFrameElement) patchFrameWindow(frame.contentWindow);
                    };
                    document.addEventListener('load', patchLoadedFrame, true);
                    stopPristineFrameHook = () => {
                        document.removeEventListener('load', patchLoadedFrame, true);
                        const currentDescriptor = Object.getOwnPropertyDescriptor(
                            HTMLIFrameElement.prototype,
                            'contentWindow');
                        if (currentDescriptor?.get === contentWindowGetter) {
                            Object.defineProperty(
                                HTMLIFrameElement.prototype,
                                'contentWindow',
                                contentWindowDescriptor);
                        }
                    };
                }
            } catch (_) { }

            const skipSelectors = [
                '.ytp-ad-skip-button', '.ytp-ad-skip-button-modern',
                '.ytp-skip-ad-button', '.ytp-skip-ad-button-modern',
                '.ytp-ad-overlay-close-button'
            ];

            const maybeRecoverServerContract = (player, playerResponse) => {
                if (host !== 'www.youtube.com'
                    || location.pathname !== '/watch'
                    || isPremium()
                    || !playerResponse) return false;

                const contract = getRecoveryContract(playerResponse);
                const { status, exactContractError, exactAdBlockEnforcement } = contract;
                const videoId = getMatchingWatchVideoId(playerResponse);
                if ((exactContractError || exactAdBlockEnforcement) && !videoId) {
                    if (activeRecoveryMarkerOwner === 'server') setRecoveryMarker('');
                    clearRecoveryMask();
                    return false;
                }
                if (videoId && recoveryVideoId !== videoId) {
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    if (recoveryMaskVerified) clearRecoveryMask();
                    recoveryVideoId = videoId;
                    recoveryAttempt = 0;
                    serverRecoveryExhausted = false;
                    seenRecoveryResponses = new WeakSet();
                }

                if (status?.status === 'OK') {
                    // A transport-stall retry starts from an apparently OK player
                    // response. Keep its request marker until real media progress
                    // proves that the replacement request completed successfully.
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    recoveryAttempt = 0;
                    serverRecoveryExhausted = false;
                    seenRecoveryResponses = new WeakSet();
                    return false;
                }
                if (!exactContractError && !exactAdBlockEnforcement) {
                    if (!recoveryMaskVerified) clearRecoveryMask();
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    return false;
                }
                if (!videoId) return false;
                if (recoveryAttempt >= recoveryMarkers.length) {
                    serverRecoveryExhausted = true;
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    return false;
                }
                if (typeof player?.loadVideoById !== 'function') {
                    clearRecoveryMask();
                    return false;
                }

                const responseCanBeTracked = (typeof playerResponse === 'object'
                    && playerResponse !== null) || typeof playerResponse === 'function';
                if (responseCanBeTracked && seenRecoveryResponses.has(playerResponse)) return false;

                // The account-side enforcement variant succeeds with the current
                // lact-millisecond contract; keep CHANNEL as its bounded fallback.
                const markerIndex = exactContractError || recoveryAttempt === 0 ? 1 : 0;
                const marker = recoveryMarkers[markerIndex];
                if (!setRecoveryMarker(marker)) {
                    clearRecoveryMask();
                    return false;
                }
                armRecoveryMask(true);
                const startSeconds = playerResponse.playerConfig
                    ?.playbackStartConfig?.startSeconds ?? 0;
                try {
                    if (responseCanBeTracked) seenRecoveryResponses.add(playerResponse);
                    recoveryAttempt = exactContractError
                        ? recoveryMarkers.length
                        : recoveryAttempt + 1;
                    serverRecoveryExhausted = recoveryAttempt >= recoveryMarkers.length;
                    player.loadVideoById(videoId, startSeconds);
                    return true;
                } catch (_) {
                    if (responseCanBeTracked) seenRecoveryResponses.delete(playerResponse);
                    recoveryAttempt = exactContractError ? 0 : Math.max(0, recoveryAttempt - 1);
                    serverRecoveryExhausted = false;
                    setRecoveryMarker('');
                    return false;
                }
            };

            const transportRetryPattern = /this content isn['’]?t available[,.]?\s*try again later/i;
            const textFromRuns = runs => Array.isArray(runs)
                ? runs.map(run => run?.text || '').join(' ')
                : '';
            const getTransportResponseText = playerResponse => {
                const status = playerResponse?.playabilityStatus;
                const renderer = status?.errorScreen?.playerErrorMessageRenderer;
                return [
                    status?.reason,
                    ...(Array.isArray(status?.messages) ? status.messages : []),
                    renderer?.reason?.simpleText,
                    textFromRuns(renderer?.reason?.runs),
                    renderer?.subreason?.simpleText,
                    textFromRuns(renderer?.subreason?.runs)
                ].filter(Boolean).join(' ');
            };
            const hasMatchingTransportError = playerResponse => {
                if (transportRecoveryErrorNode?.isConnected !== false
                    && transportRetryPattern.test(
                        transportRecoveryErrorNode?.textContent || '')) {
                    return true;
                }
                const candidates = document.querySelectorAll(
                    '.ytp-error, .ytp-error-content-wrap, .ytp-error-content, '
                    + 'yt-playability-error-supported-renderers#error-screen');
                for (const candidate of candidates) {
                    try {
                        const text = candidate.textContent || candidate.innerText || '';
                        if (!transportRetryPattern.test(text)) continue;
                        const style = getComputedStyle(candidate);
                        const bounds = candidate.getBoundingClientRect();
                        const visible = style.display !== 'none'
                            && style.visibility !== 'hidden'
                            && style.opacity !== '0'
                            && bounds.width > 0
                            && bounds.height > 0;
                        if (visible) {
                            transportRecoveryErrorNode = candidate;
                            return true;
                        }
                    } catch (_) { }
                }
                return !!document.querySelector('ytd-watch-flexy[player-unavailable]')
                    && transportRetryPattern.test(getTransportResponseText(playerResponse));
            };

            const getBufferedEnd = video => {
                try {
                    const ranges = video?.buffered;
                    if (!ranges?.length) return 0;
                    const end = Number(ranges.end(ranges.length - 1));
                    return Number.isFinite(end) && end >= 0 ? end : 0;
                } catch (_) {
                    return 0;
                }
            };

            const isValidYouTubeVideoId = value => /^[A-Za-z0-9_-]{11}$/.test(value || '');
            const getMatchingWatchVideoId = playerResponse => {
                const requested = new URLSearchParams(location.search).get('v') || '';
                const response = playerResponse?.videoDetails?.videoId || '';
                return isValidYouTubeVideoId(requested)
                    && isValidYouTubeVideoId(response)
                    && requested === response
                    ? response
                    : '';
            };

            const hasActualMediaProgress = (player, playerResponse, progress, video) => {
                const videoId = getMatchingWatchVideoId(playerResponse);
                if (!videoId) return false;
                if (transportRecoveryVideoId !== videoId) {
                    resetTransportRecovery(videoId, playerResponse);
                }
                if (!(video instanceof HTMLVideoElement) || video.readyState < 2) {
                    return false;
                }
                const current = Number(video.currentTime || 0);
                const bufferedEnd = getBufferedEnd(video);
                if (!Number.isFinite(transportRecoveryBaselineTime)) {
                    transportRecoveryBaselineTime = current;
                    transportRecoveryBaselineBufferedEnd = bufferedEnd;
                    return false;
                }
                const currentAdvanced = transportRecoveryAttempt > 0
                    && current > transportRecoveryBaselineTime + 0.1;
                const bufferAdvanced = transportRecoveryAttempt > 0
                    && bufferedEnd > transportRecoveryBaselineBufferedEnd + 0.25;
                let state = null;
                let stats = null;
                try { state = player?.getPlayerStateObject?.(); } catch (_) { }
                try { stats = player?.getStatsForNerds?.(); } catch (_) { }
                const resolution = String(stats?.resolution || '');
                const bufferHealth = String(stats?.buffer_health_seconds || '');
                const strictPlayable = transportRecoveryAttempt > 0
                    && playerResponse?.playabilityStatus?.status === 'OK'
                    && state?.isBuffering === false
                    && resolution.length > 0
                    && resolution !== '0x0'
                    && bufferHealth.length > 0
                    && bufferHealth !== '0.00 s'
                    && !hasMatchingTransportError(playerResponse)
                    && video.readyState >= 3
                    && (video.paused === false || bufferedEnd > current + 0.05);
                const ready = transportRecoveryAttempt > 0
                    ? currentAdvanced || bufferAdvanced || strictPlayable
                    : current > transportRecoveryBaselineTime + 0.1;
                if (!ready) return false;
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = 0;
                transportRecoveryBaselineTime = current;
                transportRecoveryBaselineBufferedEnd = bufferedEnd;
                transportRecoveryHasPlayed = true;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                clearRecoveryMask();
                return true;
            };

            const finalizeTransportRecovery = () => {
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                clearRecoveryMask();
                transportRecoveryAttempt = recoveryMarkers.length;
                transportRecoveryAttemptAt = 0;
                transportRecoveryTerminal = true;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
            };
            const finalizeExpiredTransportRecovery = () => {
                if (transportRecoveryTerminal || transportRecoveryAttempt === 0) return false;
                const elapsed = performance.now() - transportRecoveryAttemptAt;
                if ((transportRecoveryAttempt >= recoveryMarkers.length && elapsed >= 2_000)
                    || elapsed >= 6_000) {
                    finalizeTransportRecovery();
                    return true;
                }
                return false;
            };

            const maybeRecoverTransportStall = (player, playerResponse, progress, video) => {
                if (host !== 'www.youtube.com'
                    || location.pathname !== '/watch'
                    || isPremium()
                    || !playerResponse
                    || typeof player?.loadVideoById !== 'function') return false;

                const videoId = getMatchingWatchVideoId(playerResponse);
                if (!videoId) return false;
                if (transportRecoveryVideoId !== videoId) {
                    resetTransportRecovery(videoId, playerResponse);
                }
                if (transportRecoveryTerminal
                    || playerResponse?.videoDetails?.isLive
                    || playerResponse?.videoDetails?.isLiveContent) return false;
                const captcha = playerResponse?.playabilityStatus?.errorScreen
                    ?.playerErrorMessageRenderer?.playerCaptchaViewModel;
                if (captcha) return false;

                const duration = Number(progress?.duration || 0);
                const loaded = Number(progress?.loaded || 0);
                const current = Number(progress?.current || 0);
                const unfinished = duration > 0
                    && (loaded < duration || duration - current > 1);
                let state = null;
                let stats = null;
                try { state = player.getPlayerStateObject?.(); } catch (_) { }
                try { stats = player.getStatsForNerds?.(); } catch (_) { }
                const stalled = unfinished
                    && state?.isBuffering === true
                    && String(stats?.buffer_health_seconds || '') === '0.00 s'
                    && String(stats?.resolution || '') === '0x0'
                    && hasMatchingTransportError(playerResponse);
                if (!stalled) return false;

                const now = performance.now();
                // Give the request started by the previous attempt a chance to
                // replace the response object. YouTube occasionally mutates the
                // object in place, so permit the alternate marker after 2 seconds.
                if (transportRecoveryAttempt > 0
                    && transportRecoveryResponse === playerResponse
                    && now - transportRecoveryAttemptAt < 2_000) return false;
                if (transportRecoveryAttempt >= recoveryMarkers.length) {
                    return false;
                }

                const marker = recoveryMarkers[transportRecoveryAttempt];
                if (!setRecoveryMarker(marker, 'transport')) return false;
                armRecoveryMask(true);
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = now;
                transportRecoveryFastPollUntil = now + 6_000;
                const mediaTime = Number(video?.currentTime);
                const progressTime = Number(progress?.current);
                const observedTime = Number.isFinite(mediaTime) && mediaTime >= 0
                    ? mediaTime
                    : Number.isFinite(progressTime) && progressTime >= 0
                        ? progressTime
                        : 0;
                transportRecoveryStartSeconds = transportRecoveryHasPlayed
                    && Number.isFinite(observedTime)
                    && observedTime >= 0
                    ? observedTime
                    : getRequestedStartSeconds(playerResponse);
                transportRecoveryBaselineTime = observedTime;
                transportRecoveryBaselineBufferedEnd = getBufferedEnd(video);
                try {
                    transportRecoveryAttempt++;
                    player.loadVideoById(videoId, transportRecoveryStartSeconds);
                    return true;
                } catch (_) {
                    if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                    clearRecoveryMask();
                    return false;
                }
            };

            const cleanPlayerAds = () => {
                if (!isEnabled()) return false;
                let adActivity = false;
                try {
                    for (const selector of skipSelectors) {
                        const button = document.querySelector(selector);
                        if (button instanceof HTMLElement) {
                            button.click();
                            adActivity = true;
                        }
                    }

                    const player = document.getElementById('movie_player');
                    const playerResponse = player?.getPlayerResponse?.()
                        || window.ytInitialPlayerResponse;
                    if (maybeRecoverServerContract(player, playerResponse)) return true;

                    // Apply the current uBO SSAP gate only to non-Premium watch pages.
                    const progress = player?.getProgressState?.();
                    const recoveredVideo = document.querySelector('video.html5-main-video, video');
                    const mediaProgressing = hasActualMediaProgress(
                        player,
                        playerResponse,
                        progress,
                        recoveredVideo);
                    finalizeExpiredTransportRecovery();
                    if (!mediaProgressing && maybeRecoverTransportStall(
                        player,
                        playerResponse,
                        progress,
                        recoveredVideo)) return true;
                    const serverContract = player?.getStatsForNerds?.()?.debug_info;
                    const unfinished = progress?.duration > 0
                        && (progress.loaded < progress.duration
                            || progress.duration - progress.current > 1);
                    if (host === 'www.youtube.com'
                        && location.pathname === '/watch'
                        && !isPremium()
                        && serverContract?.startsWith?.('SSAP, AD')
                        && unfinished) {
                        player.seekTo?.(progress.duration, true);
                        adActivity = true;
                    }

                    const isClientAd = player?.classList?.contains('ad-showing')
                        || player?.classList?.contains('ad-interrupting');
                    if (isClientAd) {
                        adActivity = true;
                        const video = document.querySelector('video.html5-main-video, video');
                        if (video instanceof HTMLVideoElement
                            && Number.isFinite(video.duration)
                            && video.duration > 0) {
                            const wasMuted = video.muted;
                            video.muted = true;
                            video.currentTime = video.duration;
                            queueMicrotask(() => { video.muted = wasMuted; });
                        }
                    }

                    const enforcement = document.querySelector(
                        'ytd-enforcement-message-view-model, yt-enforcement-message-view-model');
                    const playbackReady = playerResponse?.playabilityStatus?.status === 'OK'
                        && recoveredVideo instanceof HTMLVideoElement
                        && recoveredVideo.readyState > 0;
                    if (playbackReady) {
                        if (enforcement) {
                            adActivity = true;
                            const owner = enforcement.closest?.(
                                'tp-yt-paper-dialog, yt-playability-error-supported-renderers#error-screen');
                            (owner || enforcement).remove?.();
                            document.querySelector('tp-yt-iron-overlay-backdrop')?.remove();
                            player?.classList?.remove?.('ytp-transparent');
                            document.querySelector('ytd-watch-flexy[player-unavailable]')
                                ?.removeAttribute('player-unavailable');
                            player?.playVideo?.();
                        }
                        if (!recoveryMaskVerified || mediaProgressing) {
                            clearRecoveryMask();
                        }
                    }
                    const popupConfig = window.yt?.config_?.openPopupConfig?.supportedPopups;
                    if (popupConfig && popupConfig.adBlockMessageViewModel !== false) {
                        popupConfig.adBlockMessageViewModel = false;
                        adActivity = true;
                    }
                } catch (_) { }
                return adActivity;
            };

            let cleanupQueued = false;
            let urgentCleanupQueued = false;
            let cleanupStarted = false;
            let fallbackTimer = 0;
            let cleanupSignalTimer = 0;
            let observedPlayer = null;
            let playerObserver = null;
            let playerBootstrapObserver = null;
            let playerBootstrapTimer = 0;
            const cleanupEvents = ['yt-page-data-updated', 'yt-player-updated'];
            const scheduleFallback = adActivity => {
                window.clearTimeout(fallbackTimer);
                if (!isEnabled()) return;
                const fastRecoveryPoll = isYouTubeWatch()
                    && performance.now() < transportRecoveryFastPollUntil;
                const delay = document.hidden
                    ? 30_000
                    : (fastRecoveryPoll ? 250 : (adActivity ? 500 : 5_000));
                fallbackTimer = window.setTimeout(() => {
                    const activity = cleanPlayerAds();
                    observePlayer();
                    scheduleFallback(activity);
                }, delay);
            };
            const queueCleanup = () => {
                if (cleanupQueued || !isEnabled()) return;
                cleanupQueued = true;
                cleanupSignalTimer = window.setTimeout(() => {
                    cleanupQueued = false;
                    const activity = cleanPlayerAds();
                    observePlayer();
                    if (activity) scheduleFallback(true);
                }, document.hidden ? 5_000 : 150);
            };
            const queueUrgentCleanup = () => {
                if (urgentCleanupQueued || !isEnabled()) return;
                urgentCleanupQueued = true;
                queueMicrotask(() => {
                    urgentCleanupQueued = false;
                    if (!isEnabled()) return;
                    const activity = cleanPlayerAds();
                    observePlayer();
                    if (activity) scheduleFallback(true);
                });
            };
            queuePlayerRecovery = () => {
                pendingRecoverySignal = false;
                queueUrgentCleanup();
            };
            if (pendingRecoverySignal) queuePlayerRecovery();
            const stopPlayerBootstrapObservation = () => {
                playerBootstrapObserver?.disconnect();
                playerBootstrapObserver = null;
                window.clearTimeout(playerBootstrapTimer);
                playerBootstrapTimer = 0;
            };
            const observePlayer = () => {
                if (!isEnabled()) {
                    playerObserver?.disconnect();
                    observedPlayer = null;
                    stopPlayerBootstrapObservation();
                    return;
                }
                const player = document.getElementById('movie_player');
                if (player === observedPlayer) return;
                playerObserver?.disconnect();
                observedPlayer = player;
                if (player) {
                    stopPlayerBootstrapObservation();
                    playerObserver?.observe(player, {
                        attributes: true,
                        attributeFilter: ['class']
                    });
                }
            };
            const startPlayerBootstrapObservation = () => {
                if (!isEnabled() || observedPlayer || playerBootstrapObserver
                    || !document.documentElement) return;
                playerBootstrapObserver = new MutationObserver(() => {
                    observePlayer();
                    if (observedPlayer) queueCleanup();
                });
                playerBootstrapObserver.observe(document.documentElement, {
                    childList: true,
                    subtree: true
                });
                playerBootstrapTimer = window.setTimeout(
                    stopPlayerBootstrapObservation,
                    10_000);
            };
            const onVisibilityChange = () => {
                if (!document.hidden) queueCleanup();
                scheduleFallback(false);
            };
            const onNavigationStart = () => {
                clearRecoveryMask();
                if (activeRecoveryMarker) setRecoveryMarker('');
                recoveryVideoId = '';
                recoveryAttempt = 0;
                serverRecoveryExhausted = false;
                seenRecoveryResponses = new WeakSet();
                transportRecoveryVideoId = '';
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = null;
                transportRecoveryAttemptAt = 0;
                transportRecoveryStartSeconds = 0;
                transportRecoveryBaselineTime = Number.NaN;
                transportRecoveryBaselineBufferedEnd = 0;
                transportRecoveryHasPlayed = false;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                pendingRecoverySignal = false;
                if (isYouTubeWatch()) armRecoveryMask();
            };
            const onNavigationFinish = () => {
                if (isYouTubeWatch()) {
                    resetTransportRecovery();
                    armRecoveryMask();
                } else {
                    clearRecoveryMask();
                }
                observePlayer();
                startPlayerBootstrapObservation();
                queueCleanup();
            };
            const startCleanup = () => {
                if (!isEnabled() || cleanupStarted || !document.documentElement) return;
                cleanupStarted = true;
                const activity = cleanPlayerAds();
                playerObserver = new MutationObserver(queueCleanup);
                observePlayer();
                startPlayerBootstrapObservation();
                for (const eventName of cleanupEvents) {
                    document.addEventListener(eventName, queueCleanup, { passive: true });
                }
                document.addEventListener('yt-navigate-start', onNavigationStart, { passive: true });
                document.addEventListener('yt-navigate-finish', onNavigationFinish, { passive: true });
                document.addEventListener('visibilitychange', onVisibilityChange, { passive: true });
                scheduleFallback(activity);
                stopCleanup = () => {
                    playerObserver?.disconnect();
                    stopPlayerBootstrapObservation();
                    window.clearTimeout(fallbackTimer);
                    window.clearTimeout(cleanupSignalTimer);
                    cleanupQueued = false;
                    urgentCleanupQueued = false;
                    for (const eventName of cleanupEvents) {
                        document.removeEventListener(eventName, queueCleanup);
                    }
                    document.removeEventListener('yt-navigate-start', onNavigationStart);
                    document.removeEventListener('yt-navigate-finish', onNavigationFinish);
                    document.removeEventListener('visibilitychange', onVisibilityChange);
                };
            };
            startCleanup();
            if (!cleanupStarted) {
                const cleanupBootstrapObserver = new MutationObserver(() => {
                    startCleanup();
                    if (cleanupStarted || !isEnabled()) {
                        cleanupBootstrapObserver.disconnect();
                    }
                });
                bootstrapObservers.push(cleanupBootstrapObserver);
                cleanupBootstrapObserver.observe(document, { childList: true });
            }
            if (!cleanupStarted && document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', startCleanup, { once: true });
            }

            // Keep the remotely compiled cosmetic style independently removable.
            void remoteStyleId;
        })();
        """;
}
