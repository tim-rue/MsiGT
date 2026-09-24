namespace MsiGT;

/// <summary>Shared control factories so the tabs look alike.</summary>
internal static class Ui
{
    /// <summary>A label that wraps inside a TableLayoutPanel column.</summary>
    public static Label Label(bool dim = false) => new()
    {
        AutoSize = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 0, 4),
        ForeColor = dim ? SystemColors.GrayText : SystemColors.ControlText,
    };

    public static Label Heading()
    {
        var label = Label();
        label.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 13f, FontStyle.Regular);
        label.ForeColor = Color.FromArgb(0x00, 0x33, 0x99); // TaskDialog's main-instruction blue
        label.Margin = new Padding(0, 0, 0, 8);
        return label;
    }

    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(88, 28),
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(0, 0, 8, 0),
        UseVisualStyleBackColor = true,
    };
}
