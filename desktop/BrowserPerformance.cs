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
}
