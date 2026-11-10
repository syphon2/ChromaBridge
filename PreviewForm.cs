namespace ChromaBridge;

/// <summary>Live view of what the game sends to Razer (top) and what ChromaBridge sends to G HUB (bottom).</summary>
internal sealed class PreviewForm : Form
{
    private const int Cell = 30, Gap = 3, Pad = 14, LabelHeight = 22;
    private readonly Bridge _bridge;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    public PreviewForm(Bridge bridge)
    {
        _bridge = bridge;
        Text = "ChromaBridge - live preview";
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        ClientSize = new Size(Pad * 2 + KeyMap.RazerCols * (Cell + Gap),
                              Pad * 3 + (LabelHeight + KeyMap.RazerRows * (Cell + Gap)) * 2 + LabelHeight);
        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (razer, sent, app) = _bridge.Snapshot();
        var g = e.Graphics;
        using var text = new SolidBrush(ForeColor);

        var y = Pad;
        g.DrawString($"From the game via Razer Chroma ({(app.Length == 0 ? "no app" : app)}) - 22x6", Font, text, Pad, y);
        y += LabelHeight;
        for (var r = 0; r < KeyMap.RazerRows; r++)
            for (var c = 0; c < KeyMap.RazerCols; c++)
            {
                var col = razer[r * KeyMap.RazerCols + c];
                DrawKey(g, Pad + c * (Cell + Gap), y + r * (Cell + Gap), Color.FromArgb(col.R, col.G, col.B),
                        KeyMap.RazerToLogiPixel[r * KeyMap.RazerCols + c] >= 0);
            }

        y += KeyMap.RazerRows * (Cell + Gap) + Pad;
        g.DrawString("Sent to G HUB (Logitech bitmap 21x6, dashed = key not used)", Font, text, Pad, y);
        y += LabelHeight;
        for (var r = 0; r < LogitechLed.BitmapHeight; r++)
            for (var c = 0; c < LogitechLed.BitmapWidth; c++)
            {
                var o = (r * LogitechLed.BitmapWidth + c) * 4;
                DrawKey(g, Pad + c * (Cell + Gap), y + r * (Cell + Gap),
                        Color.FromArgb(sent[o + 2], sent[o + 1], sent[o]), sent[o + 3] != 0);
            }

        y += KeyMap.RazerRows * (Cell + Gap) + 4;
        g.DrawString(_bridge.Status, Font, text, Pad, y);
    }

    private static void DrawKey(Graphics g, int x, int y, Color color, bool used)
    {
        using var fill = new SolidBrush(color);
        g.FillRectangle(fill, x, y, Cell, Cell);
        using var border = new Pen(used ? Color.FromArgb(90, 90, 90) : Color.FromArgb(60, 60, 60))
        {
            DashStyle = used ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dash,
        };
        g.DrawRectangle(border, x, y, Cell, Cell);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
