using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MonoRelay.Windows;

internal static class LauncherColors
{
    internal static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(8, 97, 214);
    internal static Color OnAccent => SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;
    internal static Color Surface => SystemColors.Window;
    internal static Color Track => SystemInformation.HighContrast ? SystemColors.Control
        : Surface.GetBrightness() < 0.5f ? Color.FromArgb(43, 49, 59) : Color.FromArgb(241, 244, 249);
    internal static Color Text => SystemColors.WindowText;
    internal static Color Secondary => SystemInformation.HighContrast ? Text
        : Surface.GetBrightness() < 0.5f ? Color.FromArgb(181, 191, 207) : Color.FromArgb(81, 92, 107);
    internal static Color Divider => SystemInformation.HighContrast ? Text
        : Surface.GetBrightness() < 0.5f ? Color.FromArgb(64, 73, 88) : Color.FromArgb(217, 224, 234);

    internal static void DrawButton(PaintEventArgs e, ButtonBase button, bool filled, bool focus)
    {
        var graphics = e.Graphics;
        graphics.Clear(button.BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = Rectangle.Inflate(button.ClientRectangle, -1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        int radius = Math.Min(button.LogicalToDeviceUnits(9), bounds.Height / 2);
        bool hovered = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
        var background = filled && button.Enabled ? Accent : Track;
        if (hovered && !SystemInformation.HighContrast) background = ControlPaint.Dark(background, 0.06f);
        using var brush = new SolidBrush(background);
        graphics.FillRoundedRectangle(brush, bounds, new Size(radius, radius));
        var text = !button.Enabled ? SystemColors.GrayText : filled ? OnAccent : Text;
        TextRenderer.DrawText(graphics, button.Text, button.Font, bounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (focus) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(bounds, -4, -4), text, background);
    }
}

internal sealed class RelayAction : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; set; }

    internal RelayAction()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
    }

    protected override void OnPaint(PaintEventArgs e) => LauncherColors.DrawButton(e, this, Primary, Focused && ShowFocusCues);
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

internal sealed class RelaySegment : RadioButton
{
    internal RelaySegment()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        AccessibleRole = AccessibleRole.RadioButton;
    }

    protected override void OnPaint(PaintEventArgs e) => LauncherColors.DrawButton(e, this, Checked, Focused && ShowFocusCues);
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

internal sealed class RelayTrack : TableLayoutPanel
{
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = new GraphicsPath();
        int radius = Math.Min(LogicalToDeviceUnits(12), Math.Min(ClientSize.Width, ClientSize.Height) / 2);
        if (radius <= 0) return;
        path.AddRoundedRectangle(ClientRectangle, new Size(radius, radius));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }
}
