using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenWigiDash.ObsPlugin.Rendering;

/// <summary>Ícones do plugin. Desenhos próprios em traço (grade de 24x24), sem nada da Elgato nem do OBS.</summary>
public enum ObsIcon
{
    Record,
    Live,
    Microphone,
    Speaker,
    Replay,
    VirtualCam,
    Scene,
    Pause,
    Play,
    Check,
}

/// <summary>Desenha um <see cref="ObsIcon"/> dentro de uma caixa quadrada, no traço dos protótipos (1,8 de 24).</summary>
internal static class ObsGlyphs
{
    public static void Draw(Graphics g, ObsIcon icon, RectangleF box, Color color, bool slashed = false, Color? background = null)
    {
        var side = Math.Min(box.Width, box.Height);
        var s = side / 24f;
        var ox = box.X + (box.Width - side) / 2;
        var oy = box.Y + (box.Height - side) / 2;
        PointF P(float x, float y) => new(ox + x * s, oy + y * s);
        RectangleF R(float x, float y, float w, float h) => new(ox + x * s, oy + y * s, w * s, h * s);
        RectangleF Circle(float cx, float cy, float r) => R(cx - r, cy - r, r * 2, r * 2);

        using var pen = new Pen(color, Math.Max(1.4f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(color);
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;

        switch (icon)
        {
            case ObsIcon.Record:
                g.DrawEllipse(pen, Circle(12, 12, 8.5f));
                g.FillEllipse(fill, Circle(12, 12, 4.6f));
                break;
            case ObsIcon.Live:
                g.FillEllipse(fill, Circle(12, 12, 2.3f));
                g.DrawArc(pen, Circle(12, 12, 5.2f), 135, 90);
                g.DrawArc(pen, Circle(12, 12, 5.2f), -45, 90);
                g.DrawArc(pen, Circle(12, 12, 9.3f), 135, 90);
                g.DrawArc(pen, Circle(12, 12, 9.3f), -45, 90);
                break;
            case ObsIcon.Microphone:
                using (var capsule = Rounded(R(9, 3, 6, 11), 3 * s))
                    g.DrawPath(pen, capsule);
                g.DrawArc(pen, Circle(12, 11, 6.5f), 0, 180);
                g.DrawLine(pen, P(12, 17.5f), P(12, 21));
                g.DrawLine(pen, P(8.5f, 21), P(15.5f, 21));
                break;
            case ObsIcon.Speaker:
                g.DrawPolygon(pen, [P(4, 9.5f), P(7.5f, 9.5f), P(12, 5.5f), P(12, 18.5f), P(7.5f, 14.5f), P(4, 14.5f)]);
                g.DrawArc(pen, Circle(12, 12, 4f), -48, 96);
                g.DrawArc(pen, Circle(12, 12, 7.5f), -48, 96);
                break;
            case ObsIcon.Replay:
                g.DrawArc(pen, Circle(12, 12, 7.4f), 225, 315);
                g.DrawLines(pen, [P(4.6f, 4.4f), P(4.6f, 8.2f), P(8.4f, 8.2f)]);
                g.DrawLines(pen, [P(12, 8.6f), P(12, 12), P(14.6f, 13.8f)]);
                break;
            case ObsIcon.VirtualCam:
                using (var body = Rounded(R(3, 7, 12.5f, 10), 2 * s))
                    g.DrawPath(pen, body);
                g.DrawPolygon(pen, [P(15.5f, 10.5f), P(21, 7.5f), P(21, 16.5f), P(15.5f, 13.5f)]);
                break;
            case ObsIcon.Scene:
                using (var frame = Rounded(R(3, 5, 18, 13), 2 * s))
                    g.DrawPath(pen, frame);
                g.DrawLines(pen, [P(3, 14.5f), P(8, 10.5f), P(12, 13.5f), P(15, 11.5f), P(21, 15.5f)]);
                break;
            case ObsIcon.Pause:
                using (var thick = new Pen(color, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(thick, P(8.5f, 6), P(8.5f, 18));
                    g.DrawLine(thick, P(15.5f, 6), P(15.5f, 18));
                }
                break;
            case ObsIcon.Play:
                g.FillPolygon(fill, [P(8.5f, 5.8f), P(18, 12), P(8.5f, 18.2f)]);
                break;
            case ObsIcon.Check:
                g.DrawLines(pen, [P(5, 12.5f), P(9.5f, 17), P(19, 7.5f)]);
                break;
        }

        if (slashed)
        {
            // Risco com uma folga da cor do fundo, para não sumir sobre o traço do ícone.
            if (background is { } bg)
            {
                using var gap = new Pen(bg, 5.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(gap, P(4, 4), P(20, 20));
            }
            using var slash = new Pen(color, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(slash, P(4, 4), P(20, 20));
        }
        g.Restore(state);
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Clamp(radius * 2, 0, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
