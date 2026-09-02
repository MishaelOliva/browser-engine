using System.Drawing.Drawing2D;

namespace MishaWeb;

/// <summary>
/// Loads the generated bunny mark once for the whole process. The 256 px
/// runtime image keeps the decoded footprint small while remaining crisp at
/// every size used by the browser chrome and native start page.
/// </summary>
internal static class BrandArtwork
{
    internal const string LogoResourceName = "MishaWeb.Brand.BunnyLogo.png";

    private static readonly Lazy<Bitmap?> LogoImage = new(
        LoadLogoImage,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool TryDrawGeneratedLogo(Graphics graphics, Rectangle bounds)
    {
        if (SystemInformation.HighContrast || bounds.Width <= 1 || bounds.Height <= 1)
        {
            return false;
        }

        var image = LogoImage.Value;
        if (image is null) return false;

        var previousInterpolation = graphics.InterpolationMode;
        var previousPixelOffset = graphics.PixelOffsetMode;
        var previousCompositing = graphics.CompositingQuality;
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.DrawImage(
                image,
                bounds,
                new Rectangle(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            return true;
        }
        finally
        {
            graphics.InterpolationMode = previousInterpolation;
            graphics.PixelOffsetMode = previousPixelOffset;
            graphics.CompositingQuality = previousCompositing;
        }
    }

    private static Bitmap? LoadLogoImage()
    {
        try
        {
            using var stream = typeof(BrandArtwork).Assembly.GetManifestResourceStream(LogoResourceName);
            if (stream is null) return null;

            using var decoded = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            return new Bitmap(decoded);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class BrandLogoControl : Control
{
    private bool privateMode;

    public BrandLogoControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "MishaWeb";
    }

    public bool PrivateMode
    {
        get => privateMode;
        set
        {
            if (privateMode == value) return;
            privateMode = value;
            AccessibleName = value ? "Private MishaWeb" : "MishaWeb";
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var side = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height));
        var inset = Math.Max(1, side / 16);
        var logoBounds = new Rectangle(
            (ClientSize.Width - side) / 2 + inset,
            (ClientSize.Height - side) / 2 + inset,
            Math.Max(1, side - (inset * 2)),
            Math.Max(1, side - (inset * 2)));
        StartPageArtwork.DrawBrandMark(e.Graphics, logoBounds);

        if (!privateMode) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var badgeSize = Math.Max(8, side / 3);
        var badgeBounds = new Rectangle(
            logoBounds.Right - badgeSize,
            logoBounds.Bottom - badgeSize,
            badgeSize,
            badgeSize);
        using var fill = new SolidBrush(
            SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.Accent);
        using var border = new Pen(
            SystemInformation.HighContrast ? SystemColors.WindowText : NativeUiTheme.Focus,
            1f);
        e.Graphics.FillEllipse(fill, badgeBounds);
        e.Graphics.DrawEllipse(border, badgeBounds);
        TextRenderer.DrawText(
            e.Graphics,
            "P",
            Font,
            badgeBounds,
            SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine);
    }
}
