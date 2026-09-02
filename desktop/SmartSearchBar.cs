using System.Drawing.Drawing2D;

namespace MishaWeb;

internal enum SmartSearchClassification
{
    Empty,
    Search,
    Address,
    Invalid
}

internal readonly record struct SmartSearchPresentation(
    SmartSearchClassification Classification,
    string Label,
    string ActionText,
    string Error)
{
    public bool CanSubmit => Classification is SmartSearchClassification.Search
        or SmartSearchClassification.Address;
}

/// <summary>
/// A renderer-free command bar used by the native start page. It deliberately
/// exposes only local input and keyboard events; suggestions remain owned by
/// the browser window so the same popup can be anchored to the omnibox.
/// </summary>
internal sealed class SmartSearchBar : UserControl
{
    private const int CompactWidthLogical = 500;
    private const int WideHintWidthLogical = 650;
    private const int HintColumnWidthLogical = 72;
    private const int ProviderColumnWidthLogical = 124;
    private const int ActionWidthLogical = 108;
    private const int CompactActionWidthLogical = 48;

    // Custom WinForms controls must paint opaque pixels on every frame. Alpha-filled
    // child buffers accumulate stale glyphs during interactive resizing on some GPUs.
    private static readonly Color SurfaceColor = BlendOpaque(NativeUiTheme.Surface, NativeUiTheme.Window, 148);
    private static readonly Color SurfaceColorSecondary = BlendOpaque(NativeUiTheme.Chrome, NativeUiTheme.Window, 148);
    private static readonly Color InputSurfaceColor = BlendOpaque(NativeUiTheme.Field, SurfaceColor, 196);
    private static readonly Color BorderColor = NativeUiTheme.Border;
    private static readonly Color FocusColor = NativeUiTheme.Focus;
    private static readonly Color PrimaryTextColor = NativeUiTheme.Text;
    private static readonly Color SecondaryTextColor = NativeUiTheme.SecondaryText;
    private static readonly Color MutedTextColor = NativeUiTheme.Muted;
    private static readonly Color AccentColor = NativeUiTheme.Accent;
    private static readonly Color AccentHoverColor = NativeUiTheme.AccentHover;
    private static readonly Color AccentPressedColor = NativeUiTheme.AccentPressed;
    private static readonly Color PillColor = BlendOpaque(NativeUiTheme.Chrome, SurfaceColor, 242);
    private static readonly Color PillHoverColor = BlendOpaque(NativeUiTheme.SurfaceRaised, SurfaceColor, 248);
    private static readonly Color PillPressedColor = BlendOpaque(NativeUiTheme.Selection, SurfaceColor, 252);
    private static readonly Font InputFont = new("Segoe UI", 11f);
    private static readonly Font LabelFont = new("Segoe UI Semibold", 8f);
    private static readonly Font ProviderFont = new("Segoe UI Semibold", 8.25f);
    private static readonly Font ActionFont = new("Segoe UI Semibold", 9f);
    private static readonly Font HintFont = new("Segoe UI", 7.6f);

    private readonly GlassSurface surface = new();
    private readonly TableLayoutPanel layout = new();
    private readonly SearchGlyph icon = new();
    private readonly TextBox input = new();
    private readonly Label modeLabel = new();
    private readonly Label hintLabel = new();
    private readonly ProviderPillButton providerButton = new();
    private readonly ArrowActionButton actionButton = new();
    private readonly ToolTip toolTip = new();
    private string providerId = BrowserPolicy.DefaultSearchProviderId;
    private SmartSearchPresentation presentation;

    public SmartSearchBar()
    {
        BuildControls();
    }

    private void BuildControls()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        MinimumSize = new Size(260, 48);
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Smart search command bar";
        AccessibleDescription = "Search the web or open a website address";

        surface.Dock = DockStyle.Fill;
        surface.Margin = Padding.Empty;
        surface.Padding = new Padding(2);
        surface.AccessibleRole = AccessibleRole.Grouping;
        surface.AccessibleName = "Smart search command bar surface";
        surface.FocusBorderColor = FocusColor;
        surface.Enter += (_, _) => UpdateFocusState();
        surface.Leave += (_, _) => UpdateFocusState();

        layout.Dock = DockStyle.Fill;
        layout.Margin = Padding.Empty;
        layout.Padding = new Padding(7, 5, 7, 5);
        TrySetTransparentBackColor(layout);
        layout.ColumnCount = 6;
        layout.RowCount = 2;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));

        icon.Dock = DockStyle.Fill;
        icon.Margin = Padding.Empty;
        icon.AccessibleName = "Search input";
        icon.AccessibleRole = AccessibleRole.Graphic;

        modeLabel.Dock = DockStyle.Fill;
        modeLabel.Margin = new Padding(6, 0, 0, 0);
        modeLabel.Font = LabelFont;
        modeLabel.ForeColor = MutedTextColor;
        modeLabel.TextAlign = ContentAlignment.BottomLeft;
        modeLabel.AutoEllipsis = true;
        modeLabel.TabStop = false;
        modeLabel.AccessibleRole = AccessibleRole.StaticText;

        hintLabel.AutoSize = true;
        hintLabel.Margin = new Padding(0, 0, 6, 0);
        hintLabel.Font = HintFont;
        hintLabel.ForeColor = MutedTextColor;
        hintLabel.Text = "CTRL + K";
        hintLabel.TextAlign = ContentAlignment.MiddleCenter;
        hintLabel.Padding = new Padding(4, 2, 4, 2);
        TrySetBackColor(hintLabel, Color.FromArgb(44, NativeUiTheme.Focus));
        hintLabel.AccessibleName = "Keyboard shortcut Ctrl plus K";
        hintLabel.AccessibleRole = AccessibleRole.StaticText;

        providerButton.Dock = DockStyle.Fill;
        providerButton.Margin = new Padding(0, 0, 6, 0);
        providerButton.MinimumSize = new Size(118, 0);
        providerButton.Font = ProviderFont;
        providerButton.ForeColor = SecondaryTextColor;
        providerButton.FillColor = PillColor;
        providerButton.HoverColor = PillHoverColor;
        providerButton.PressedColor = PillPressedColor;
        providerButton.BorderColor = BorderColor;
        ConfigureProviderButton();
        providerButton.Click += (_, _) => ProviderRequested?.Invoke(this, EventArgs.Empty);

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(6, 1, 8, 3);
        input.AutoSize = false;
        input.BorderStyle = BorderStyle.None;
        TrySetBackColor(input, InputSurfaceColor);
        input.ForeColor = PrimaryTextColor;
        input.Font = InputFont;
        input.PlaceholderText = "Search the web or enter an address";
        input.MaxLength = BrowserPolicy.MaximumUrlLength;
        input.TabIndex = 0;
        input.AccessibleName = "Search or open a website";
        input.AccessibleDescription = "Type search terms or a website address. Press Enter to continue.";
        input.TextChanged += (_, _) =>
        {
            UpdatePresentation();
            InputChanged?.Invoke(this, EventArgs.Empty);
        };
        input.Enter += (_, _) => UpdateFocusState();
        input.Leave += (_, _) => UpdateFocusState();
        input.KeyDown += OnInputKeyDown;

        actionButton.Dock = DockStyle.Fill;
        actionButton.Margin = new Padding(1, 1, 2, 1);
        actionButton.Font = ActionFont;
        actionButton.ForeColor = NativeUiTheme.AccentText;
        actionButton.FillColor = AccentColor;
        actionButton.HoverColor = AccentHoverColor;
        actionButton.PressedColor = AccentPressedColor;
        actionButton.AccessibleRole = AccessibleRole.PushButton;
        toolTip.SetToolTip(actionButton, "Run this search or address");
        actionButton.Click += (_, _) => SubmitRequested?.Invoke(this, EventArgs.Empty);
        actionButton.Enter += (_, _) => UpdateFocusState();
        actionButton.Leave += (_, _) => UpdateFocusState();

        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(modeLabel, 1, 0);
        layout.Controls.Add(hintLabel, 3, 0);
        layout.Controls.Add(providerButton, 4, 0);
        layout.Controls.Add(input, 1, 1);
        layout.SetColumnSpan(input, 4);
        layout.Controls.Add(actionButton, 5, 0);
        layout.SetRowSpan(actionButton, 2);
        surface.Controls.Add(layout);
        Controls.Add(surface);

        ApplyDpiMetrics();
        UpdatePresentation();
        ApplyResponsiveLayout();
        ApplySystemColorPalette();
    }

    public event EventHandler? InputChanged;
    public event EventHandler<KeyEventArgs>? SearchKeyDown;
    public event EventHandler? SubmitRequested;
    public event EventHandler? ProviderRequested;

    public Control SuggestionAnchor => this;

    public new string Text
    {
        get => input.Text;
        set
        {
            if (input.Text == (value ?? string.Empty)) return;
            input.Text = value ?? string.Empty;
            input.SelectionStart = input.TextLength;
            UpdatePresentation();
        }
    }

    public TextBox InputControl => input;

    public string ProviderId
    {
        get => providerId;
        set
        {
            var normalized = BrowserPolicy.NormalizeSearchProviderId(value);
            if (providerId == normalized) return;
            providerId = normalized;
            ConfigureProviderButton();
            UpdatePresentation();
        }
    }

    public SmartSearchClassification Classification => presentation.Classification;
    public string DynamicLabel => presentation.Label;
    public string DynamicAction => presentation.ActionText;
    public string InlineError => presentation.Error;
    public bool CanSubmit => presentation.CanSubmit;

    public void FocusInput(bool selectAll = true)
    {
        if (!input.CanFocus) return;
        input.Focus();
        if (selectAll) input.SelectAll();
    }

    public void ClearInput()
    {
        input.Clear();
        FocusInput(false);
    }

    internal bool SubmitForTesting()
    {
        if (!CanSubmit) return false;
        SubmitRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiMetrics();
        ApplyResponsiveLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyResponsiveLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
        ApplyResponsiveLayout();
        Invalidate(true);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplySystemColorPalette();
    }

    private void ApplySystemColorPalette()
    {
        input.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PrimaryTextColor;
        TrySetBackColor(input, SystemInformation.HighContrast ? SystemColors.Window : InputSurfaceColor, SystemColors.Window);
        modeLabel.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : MutedTextColor;
        hintLabel.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : MutedTextColor;
        TrySetBackColor(
            hintLabel,
            SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(44, NativeUiTheme.Focus),
            SystemColors.Window);
        surface.Invalidate();
        providerButton.Invalidate();
        actionButton.Invalidate();
        Invalidate(true);
    }

    private static void TrySetTransparentBackColor(Control control)
    {
        try
        {
            control.BackColor = Color.Transparent;
        }
        catch (ArgumentException)
        {
            // Control does not allow transparent backgrounds; keep the default color.
        }
    }

    private static void TrySetBackColor(Control control, Color color, Color? fallback = null)
    {
        try
        {
            control.BackColor = color;
            return;
        }
        catch (ArgumentException)
        {
            if (color.A < 255)
            {
                try
                {
                    control.BackColor = Color.FromArgb(255, color.R, color.G, color.B);
                    return;
                }
                catch (ArgumentException)
                {
                    // Fallback to the provided opaque default.
                }
            }
            else if (fallback is null)
            {
                return;
            }
        }

        if (fallback is null) return;

        try
        {
            control.BackColor = fallback.Value;
        }
        catch (ArgumentException)
        {
            // Give up silently; back color is best-effort for startup paths.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        SearchKeyDown?.Invoke(this, e);
        if (e.Handled || e.SuppressKeyPress) return;

        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (CanSubmit) SubmitRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.KeyCode == Keys.Escape && input.TextLength > 0)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ClearInput();
        }
    }

    private void ConfigureProviderButton()
    {
        providerButton.Text = BrowserPolicy.GetSearchProviderName(providerId);
        providerButton.AccessibleName = $"Search engine: {BrowserPolicy.GetSearchProviderName(providerId)}";
        providerButton.AccessibleDescription = "Choose the search engine used for search suggestions and searches";
        toolTip.SetToolTip(providerButton, "Choose search engine");
    }

    private void UpdatePresentation()
    {
        var value = input.Text.Trim();
        if (value.Length == 0)
        {
            presentation = new SmartSearchPresentation(
                SmartSearchClassification.Empty,
                "Search or open a site",
                "Go",
                string.Empty);
        }
        else
        {
            var resolution = BrowserPolicy.ResolveAddress(value);
            if (resolution.Error is not null)
            {
                presentation = new SmartSearchPresentation(
                    SmartSearchClassification.Invalid,
                    "Check this address",
                    "Go",
                    resolution.Error);
            }
            else if (BrowserPolicy.IsRecognizedAddressInput(value))
            {
                presentation = new SmartSearchPresentation(
                    SmartSearchClassification.Address,
                    "Open website",
                    "Open",
                    string.Empty);
            }
            else
            {
                presentation = new SmartSearchPresentation(
                    SmartSearchClassification.Search,
                    $"Search with {BrowserPolicy.GetSearchProviderName(providerId)}",
                    "Search",
                    string.Empty);
            }
        }

        modeLabel.Text = presentation.Label;
        modeLabel.AccessibleName = presentation.Label;
        input.AccessibleDescription = presentation.Error.Length == 0
            ? "Type search terms or a website address. Press Enter to continue."
            : $"{presentation.Error}. Correct the value and try again.";
        actionButton.Text = presentation.ActionText;
        actionButton.Enabled = presentation.CanSubmit;
        actionButton.AccessibleName = presentation.Classification switch
        {
            SmartSearchClassification.Search => "Search",
            SmartSearchClassification.Address => "Open website",
            SmartSearchClassification.Invalid => "Invalid address",
            _ => "Go"
        };
        actionButton.AccessibleDescription = presentation.Error.Length == 0
            ? $"{presentation.ActionText} the current command"
            : presentation.Error;
        toolTip.SetToolTip(this, presentation.Error.Length == 0 ? presentation.Label : presentation.Error);
        ApplyResponsiveVisibility();
        surface.IsFocused = input.Focused || actionButton.Focused || providerButton.Focused;
        Invalidate(true);
    }

    private void UpdateFocusState()
    {
        surface.IsFocused = input.Focused || actionButton.Focused || providerButton.Focused;
    }

    private void ApplyResponsiveLayout()
    {
        if (layout.ColumnStyles.Count < 6) return;
        ApplyResponsiveVisibility();
        layout.PerformLayout();
        Invalidate(true);
    }

    private void ApplyResponsiveVisibility()
    {
        if (layout.ColumnStyles.Count < 6) return;

        var compact = Width > 0 && Width < Scale(CompactWidthLogical);
        var showHint = !compact
            && Width >= Scale(WideHintWidthLogical)
            && input.TextLength == 0;

        modeLabel.Visible = !compact;
        hintLabel.Visible = showHint;
        providerButton.Visible = !compact;
        layout.ColumnStyles[3].SizeType = SizeType.Absolute;
        layout.ColumnStyles[3].Width = showHint ? Scale(HintColumnWidthLogical) : 0;
        layout.ColumnStyles[4].SizeType = SizeType.Absolute;
        layout.ColumnStyles[4].Width = compact ? 0 : Scale(ProviderColumnWidthLogical);
        layout.ColumnStyles[5].SizeType = SizeType.Absolute;
        layout.ColumnStyles[5].Width = Scale(compact ? CompactActionWidthLogical : ActionWidthLogical);
        actionButton.Compact = compact;
    }

    private void ApplyDpiMetrics()
    {
        MinimumSize = new Size(Scale(260), Scale(48));
        surface.Padding = new Padding(Scale(2));
        surface.CornerRadius = Scale(18);
        layout.Padding = new Padding(Scale(7), Scale(5), Scale(7), Scale(5));
        layout.RowStyles[0].Height = Scale(17);
        layout.ColumnStyles[0].SizeType = SizeType.Absolute;
        layout.ColumnStyles[0].Width = Scale(45);
        layout.ColumnStyles[2].SizeType = SizeType.Absolute;
        layout.ColumnStyles[2].Width = Scale(8);

        modeLabel.Margin = new Padding(Scale(6), 0, 0, 0);
        hintLabel.Margin = new Padding(0, 0, Scale(6), 0);
        hintLabel.Padding = new Padding(Scale(4), Scale(2), Scale(4), Scale(2));
        providerButton.Margin = new Padding(0, 0, Scale(6), 0);
        providerButton.MinimumSize = new Size(Scale(118), 0);
        providerButton.CornerRadius = Scale(10);
        input.Margin = new Padding(Scale(6), Scale(1), Scale(8), Scale(3));
        actionButton.Margin = new Padding(Scale(1), Scale(1), Scale(2), Scale(1));
        actionButton.CornerRadius = Scale(12);
    }

    private int Scale(int logicalValue)
    {
        return Math.Max(1, (logicalValue * DeviceDpi + 48) / 96);
    }

    private static Color FlattenColor(Color color, Color background)
    {
        return color.A == 255 ? color : BlendOpaque(color, background, color.A);
    }

    private static Color BlendOpaque(Color foreground, Color background, int foregroundAlpha)
    {
        var alpha = Math.Clamp(foregroundAlpha, 0, 255);
        var inverse = 255 - alpha;
        return Color.FromArgb(
            255,
            ((foreground.R * alpha) + (background.R * inverse) + 127) / 255,
            ((foreground.G * alpha) + (background.G * inverse) + 127) / 255,
            ((foreground.B * alpha) + (background.B * inverse) + 127) / 255);
    }

    private sealed class GlassSurface : Panel
    {
        private bool isFocused;

        public GlassSurface()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            CornerRadius = 18;
        }

        public int CornerRadius { get; set; }
        public Color FocusBorderColor { get; set; } = FocusColor;
        public bool IsFocused
        {
            get => isFocused;
            set
            {
                if (isFocused == value) return;
                isFocused = value;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = CreateRoundedPath(bounds, CornerRadius);
            if (SystemInformation.HighContrast)
            {
                using var fill = new SolidBrush(SystemColors.Window);
                using var border = new Pen(SystemColors.WindowText, isFocused ? 2f : 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            else
            {
                using var fill = new LinearGradientBrush(
                    ClientRectangle,
                    SurfaceColor,
                    SurfaceColorSecondary,
                    LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillPath(fill, path);

                using var border = new Pen(isFocused ? FocusBorderColor : BorderColor, isFocused ? 1.8f : 1f);
                using var highlight = new Pen(Color.FromArgb(54, NativeUiTheme.Focus));
                e.Graphics.DrawPath(border, path);
                e.Graphics.DrawPath(highlight, path);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 1 || Height <= 1) return;

            using var path = CreateRoundedPath(
                new RectangleF(0, 0, Width, Height),
                CornerRadius);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
            Invalidate(true);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class SearchGlyph : Control
    {
        public SearchGlyph()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            BackColor = FlattenColor(SurfaceColor, NativeUiTheme.Chrome);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var tile = new RectangleF(
                Math.Max(1f, (Width - (34f * scale)) / 2f),
                Math.Max(1f, (Height - (34f * scale)) / 2f),
                34f * scale,
                34f * scale);
            var halo = tile;
            halo.Inflate(4f * scale, 4f * scale);
            using var haloBrush = new SolidBrush(Color.FromArgb(32, NativeUiTheme.Accent));
            e.Graphics.FillEllipse(haloBrush, halo);
            using var tileBrush = new LinearGradientBrush(
                tile,
                SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.Accent,
                SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.Lavender,
                LinearGradientMode.ForwardDiagonal);
            e.Graphics.FillEllipse(tileBrush, tile);
            var diameter = 10f * scale;
            var left = tile.Left + (7f * scale);
            var top = tile.Top + (6f * scale);
            using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText, Math.Max(1.3f, 1.6f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(pen, left, top, diameter, diameter);
            e.Graphics.DrawLine(pen, left + diameter - scale, top + diameter - scale, left + diameter + (4f * scale), top + diameter + (4f * scale));
        }
    }

    private sealed class ProviderPillButton : Button
    {
        private bool hovered;
        private bool pressed;

        public ProviderPillButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public Color FillColor { get; set; } = PillColor;
        public Color HoverColor { get; set; } = PillHoverColor;
        public Color PressedColor { get; set; } = PillPressedColor;
        public Color BorderColor { get; set; } = NativeUiTheme.Border;
        public int CornerRadius { get; set; } = 10;

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = CreateRoundedPath(bounds, CornerRadius);
            var color = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : pressed ? PressedColor : hovered ? HoverColor : FillColor;
            var primaryColor = FlattenColor(color, SurfaceColor);
            var secondaryColor = Color.FromArgb(
                255,
                Math.Min(255, primaryColor.R + 20),
                Math.Min(255, primaryColor.G + 14),
                Math.Min(255, primaryColor.B + 20));
            if (!Enabled)
            {
                if (SystemInformation.HighContrast)
                {
                    primaryColor = SystemColors.Control;
                    secondaryColor = SystemColors.Control;
                }
                else
                {
                    primaryColor = BlendOpaque(primaryColor, SurfaceColor, 108);
                    secondaryColor = BlendOpaque(secondaryColor, SurfaceColor, 92);
                }
            }
            using var fill = new LinearGradientBrush(
                bounds,
                primaryColor,
                secondaryColor,
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(
                SystemInformation.HighContrast ? SystemColors.WindowText : Focused ? FocusColor : BorderColor,
                Focused ? 1.6f : 0.9f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var iconCenter = new PointF(14f * scale, Height / 2f);
            using var iconPen = new Pen(
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? SecondaryTextColor : MutedTextColor,
                Math.Max(1.1f, 1.35f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(
                iconPen,
                iconCenter.X - (4.2f * scale),
                iconCenter.Y - (5f * scale),
                9f * scale,
                9f * scale);
            e.Graphics.DrawLine(
                iconPen,
                iconCenter.X + (2.2f * scale),
                iconCenter.Y + (3f * scale),
                iconCenter.X + (5.6f * scale),
                iconCenter.Y + (6.2f * scale));

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                new Rectangle(
                    (int)(26f * scale),
                    0,
                    Math.Max(1, Width - (int)(46f * scale)),
                    Height),
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? ForeColor : MutedTextColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            var chevronX = Width - (13f * scale);
            var chevronY = Height / 2f;
            using var chevron = new Pen(
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? SecondaryTextColor : MutedTextColor,
                Math.Max(1.1f, 1.3f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(chevron,
            [
                new PointF(chevronX - (2.4f * scale), chevronY - (1.5f * scale)),
                new PointF(chevronX, chevronY + (1.6f * scale)),
                new PointF(chevronX + (2.4f * scale), chevronY - (1.5f * scale))
            ]);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ArrowActionButton : Button
    {
        private bool hovered;
        private bool pressed;

        public ArrowActionButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
        }

        public Color FillColor { get; set; } = AccentColor;
        public Color HoverColor { get; set; } = AccentHoverColor;
        public Color PressedColor { get; set; } = AccentPressedColor;
        public int CornerRadius { get; set; } = 12;
        public bool Compact { get; set; }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = CreateRoundedPath(bounds, CornerRadius);
            var color = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : pressed ? PressedColor : hovered ? HoverColor : FillColor;
            var primaryColor = FlattenColor(color, SurfaceColor);
            var secondaryColor = Color.FromArgb(
                255,
                Math.Min(255, primaryColor.R + 18),
                Math.Min(255, primaryColor.G + 12),
                Math.Min(255, primaryColor.B + 24));
            if (!Enabled)
            {
                if (SystemInformation.HighContrast)
                {
                    primaryColor = SystemColors.Control;
                    secondaryColor = SystemColors.Control;
                }
                else
                {
                    primaryColor = BlendOpaque(primaryColor, SurfaceColor, 116);
                    secondaryColor = BlendOpaque(secondaryColor, SurfaceColor, 98);
                }
            }
            using var fill = new LinearGradientBrush(
                bounds,
                primaryColor,
                secondaryColor,
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(
                SystemInformation.HighContrast ? SystemColors.WindowText : Focused ? FocusColor : color,
                Focused ? 1.8f : 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            using var sheen = new Pen(Color.FromArgb(68, NativeUiTheme.Focus), 1f);
            e.Graphics.DrawPath(sheen, path);
            if (!Compact)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    new Rectangle(4, 0, Math.Max(1, Width - Scale(20)), Height),
                    SystemInformation.HighContrast
                        ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                        : Enabled ? ForeColor : Color.FromArgb(160, ForeColor),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var centerX = Width - (10f * scale);
            var centerY = Height / 2f;
            using var arrow = new Pen(
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? ForeColor : Color.FromArgb(150, ForeColor),
                Math.Max(1.1f, 1.3f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(arrow,
            [
                new PointF(centerX - (2f * scale), centerY - (3f * scale)),
                new PointF(centerX + (1.5f * scale), centerY),
                new PointF(centerX - (2f * scale), centerY + (3f * scale))
            ]);
        }

        private int Scale(int logicalValue)
        {
            return Math.Max(1, (logicalValue * DeviceDpi + 48) / 96);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
