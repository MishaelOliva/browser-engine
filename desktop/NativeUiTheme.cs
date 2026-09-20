namespace MishaWeb;

/// <summary>
/// Shared palette for small native utility windows. This intentionally styles
/// stock WinForms controls instead of replacing them with heavier custom views.
/// </summary>
internal static class NativeUiTheme
{
    // Shared v2 brand palette. Keeping these as process-wide Color values lets
    // every native surface share one identity without loading theme resources.
    internal static readonly Color BrandWine = Color.FromArgb(68, 7, 51);
    internal static readonly Color BrandCoral = Color.FromArgb(246, 107, 122);
    internal static readonly Color Window = Color.FromArgb(24, 11, 22);
    internal static readonly Color Chrome = Color.FromArgb(37, 15, 32);
    internal static readonly Color Toolbar = Color.FromArgb(43, 18, 38);
    internal static readonly Color Field = Color.FromArgb(23, 11, 22);
    internal static readonly Color Surface = Color.FromArgb(53, 25, 47);
    internal static readonly Color SurfaceRaised = Color.FromArgb(66, 32, 58);
    internal static readonly Color Hover = Color.FromArgb(79, 41, 68);
    internal static readonly Color Pressed = Color.FromArgb(93, 47, 77);
    internal static readonly Color Border = Color.FromArgb(150, 93, 126);
    internal static readonly Color Text = Color.FromArgb(255, 246, 249);
    internal static readonly Color SecondaryText = Color.FromArgb(237, 215, 225);
    internal static readonly Color Muted = Color.FromArgb(205, 168, 184);
    internal static readonly Color Selection = Color.FromArgb(91, 40, 74);
    internal static readonly Color Accent = Color.FromArgb(255, 120, 174);
    internal static readonly Color AccentHover = Color.FromArgb(255, 155, 195);
    internal static readonly Color AccentPressed = Color.FromArgb(223, 91, 148);
    internal static readonly Color AccentText = Color.FromArgb(43, 8, 24);
    internal static readonly Color Focus = Color.FromArgb(255, 179, 208);
    internal static readonly Color Lavender = Color.FromArgb(216, 135, 255);
    internal static readonly Color Positive = Color.FromArgb(132, 230, 191);
    internal static readonly Color Negative = Color.FromArgb(255, 157, 170);

    public static Color MutedText => SystemInformation.HighContrast
        ? SystemColors.GrayText
        : Muted;

    public static void Apply(Form form, params Button[] accentButtons)
    {
        ArgumentNullException.ThrowIfNull(form);
        accentButtons ??= [];
        ApplyPalette();
        form.SystemColorsChanged += (_, _) => ApplyPalette();

        void ApplyPalette()
        {
            ApplyTree(form);
            foreach (var button in accentButtons)
            {
                if (button is not null && !button.IsDisposed) ApplyAccentButton(button);
            }
        }
    }

    private static void ApplyTree(Control control)
    {
        var highContrast = SystemInformation.HighContrast;
        var window = highContrast ? SystemColors.Window : Window;
        var field = highContrast ? SystemColors.Window : Field;
        var foreground = highContrast ? SystemColors.WindowText : Text;
        control.ForeColor = foreground;
        try
        {
            control.BackColor = control switch
            {
                TextBoxBase or ListBox or ComboBox => field,
                Button => highContrast ? SystemColors.Control : Surface,
                Label => Color.Transparent,
                _ => window
            };
        }
        catch (ArgumentException)
        {
            control.BackColor = highContrast ? SystemColors.Window : window;
        }

        switch (control)
        {
            case Button button:
                ApplyButton(button, highContrast);
                break;
            case TextBoxBase textBox:
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListBox listBox:
                listBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox comboBox:
                comboBox.FlatStyle = highContrast ? FlatStyle.System : FlatStyle.Flat;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyTree(child);
        }
    }

    private static void ApplyButton(Button button, bool highContrast)
    {
        if (highContrast)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Pressed;
    }

    internal static void ApplyAccentButton(Button button)
    {
        if (SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Accent;
        button.ForeColor = AccentText;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Focus;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
    }
}
