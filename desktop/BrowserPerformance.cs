namespace MishaWeb;

internal static class BrowserPerformance
{
    public const string NoMotionDocumentScript =
        """
        (() => {
            const styleId = '__misha_no_motion_style';
            const installStyle = () => {
                const root = document.documentElement;
                if (!root || document.getElementById(styleId)) return;

                const style = document.createElement('style');
                style.id = styleId;
                style.textContent = `
                    *, *::before, *::after {
                        animation-delay: 0s !important;
                        animation-duration: 0.001ms !important;
                        animation-iteration-count: 1 !important;
                        transition-delay: 0s !important;
                        transition-duration: 0.001ms !important;
                        scroll-behavior: auto !important;
                    }
                `;
                (document.head || root).appendChild(style);
            };

            installStyle();
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', installStyle, { once: true });
            }
        })();
        """;

    public const string RestoreMotionDocumentScript =
        "document.getElementById('__misha_no_motion_style')?.remove();";

    public const string SiteResponsivenessDocumentScript =
        """
        (() => {
            if (window.__mishaSiteResponsivenessInstalled) return;
            try {
                Object.defineProperty(window, '__mishaSiteResponsivenessInstalled', {
                    value: true,
                    writable: false,
                    configurable: false
                });
            } catch (_) {
                window.__mishaSiteResponsivenessInstalled = true;
            }

            // 1. Safe, Selective Passive Event Listeners for Scrolling
            // We ONLY mark default scroll events on Window/Document/Body as passive when
            // the caller did NOT explicitly pass { passive: false }.
            // We NEVER override explicit { passive: false }, and we NEVER touch interactive
            // surfaces (canvas, maps, sliders, games, contenteditable) that require preventDefault.
            try {
                const scrollTypes = new Set(['wheel', 'mousewheel', 'touchstart', 'touchmove']);
                const originalAddEventListener = EventTarget.prototype.addEventListener;
                EventTarget.prototype.addEventListener = function (type, listener, options) {
                    if (scrollTypes.has(type)) {
                        const isExplicitActive = typeof options === 'object' && options !== null && options.passive === false;
                        if (!isExplicitActive) {
                            const isGlobalTarget = this === window || this === document || this === document.body;
                            if (isGlobalTarget) {
                                if (typeof options === 'boolean') {
                                    options = { capture: options, passive: true };
                                } else if (typeof options === 'object' && options !== null) {
                                    if (options.passive === undefined) {
                                        try { options.passive = true; }
                                        catch (_) { options = Object.assign({}, options, { passive: true }); }
                                    }
                                } else {
                                    options = { passive: true };
                                }
                            }
                        }
                    }
                    return originalAddEventListener.call(this, type, listener, options);
                };
            } catch (_) {}

            // 2. Off-Thread Asynchronous Image Decoding
            // Applied non-destructively without mutating DOM attributes to avoid hydration issues
            try {
                if ('HTMLImageElement' in window) {
                    const originalSrcDescriptor = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src');
                    if (originalSrcDescriptor && originalSrcDescriptor.set) {
                        const originalSet = originalSrcDescriptor.set;
                        Object.defineProperty(HTMLImageElement.prototype, 'src', {
                            configurable: true,
                            enumerable: true,
                            get: originalSrcDescriptor.get,
                            set: function (val) {
                                if (this.decoding === 'auto' || !this.decoding) {
                                    try { this.decoding = 'async'; } catch (_) {}
                                }
                                return originalSet.call(this, val);
                            }
                        });
                    }
                }
            } catch (_) {}

            // 3. Font-Display Swap (Prevents Flash of Invisible Text)
            const installFontSwap = () => {
                try {
                    const root = document.head || document.documentElement;
                    if (!root || document.getElementById('__misha_font_swap')) return;
                    const style = document.createElement('style');
                    style.id = '__misha_font_swap';
                    style.textContent = '@media screen { @font-face { font-display: swap !important; } }';
                    root.appendChild(style);
                } catch (_) {}
            };
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', installFontSwap, { once: true });
            } else {
                installFontSwap();
            }

            // 4. Bounded, Leak-Free Hover Intent Link Preconnect / DNS-Prefetch
            try {
                const preconnectedOrigins = new Set();
                let activePreconnectLinks = [];
                const maxPreconnectCount = 10;
                let hoverTimer = null;

                const onPointer = (e) => {
                    const target = e.target;
                    const anchor = target && target.closest ? target.closest('a[href]') : null;
                    if (!anchor) {
                        if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
                        return;
                    }

                    if (hoverTimer) clearTimeout(hoverTimer);
                    hoverTimer = setTimeout(() => {
                        try {
                            const href = anchor.href;
                            if (!href || (!href.startsWith('http://') && !href.startsWith('https://'))) return;
                            const targetOrigin = new URL(href).origin;
                            if (targetOrigin === location.origin || preconnectedOrigins.has(targetOrigin)) return;

                            preconnectedOrigins.add(targetOrigin);
                            const head = document.head || document.documentElement;
                            if (!head) return;

                            // Prune oldest if at capacity to prevent memory leaks in SPAs
                            if (activePreconnectLinks.length >= maxPreconnectCount) {
                                const oldest = activePreconnectLinks.shift();
                                if (oldest) oldest.forEach(el => el.remove());
                            }

                            const dnsEl = document.createElement('link');
                            dnsEl.rel = 'dns-prefetch';
                            dnsEl.href = targetOrigin;
                            head.appendChild(dnsEl);

                            const preconnectEl = document.createElement('link');
                            preconnectEl.rel = 'preconnect';
                            preconnectEl.href = targetOrigin;
                            preconnectEl.crossOrigin = 'anonymous';
                            head.appendChild(preconnectEl);

                            activePreconnectLinks.push([dnsEl, preconnectEl]);
                        } catch (_) {}
                    }, 65);
                };

                document.addEventListener('pointerenter', onPointer, { passive: true, capture: true });
                document.addEventListener('pointerout', () => {
                    if (hoverTimer) { clearTimeout(hoverTimer); hoverTimer = null; }
                }, { passive: true, capture: true });
            } catch (_) {}
        })();
        """;
}
