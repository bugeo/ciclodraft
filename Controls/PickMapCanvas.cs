using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;

namespace CicloDraft.Controls;

/// <summary>Mappa per scegliere i punti di controllo del fotoinserimento (aggancio a vertici e linee).</summary>
public class PickMapCanvas : Control
{
    public GpkgData? Data;
    public Project? Project;
    /// <summary>Punti di controllo da disegnare: numero, punto o linea (coordinate locali), selezionato.</summary>
    public List<(int n, List<V2> pts, bool line, bool selected)> Marks = new();
    public PhotoCamera? Camera;   // coordinate locali
    public bool AwaitingPick;
    /// <summary>Mostra (e aggancia) gli elementi del progetto e i bordi strada.</summary>
    public bool ShowProject = true;
    /// <summary>Rotazione della vista (radianti, antioraria).</summary>
    double _rot;
    public double RotationDeg => _rot * 180 / Math.PI;

    public void RotateBy(double deg) => SetRotation(RotationDeg + deg);

    /// <summary>Ruota la vista attorno al centro dello schermo.</summary>
    public void SetRotation(double deg)
    {
        var c = Wd(new Point(Bounds.Width / 2, Bounds.Height / 2));
        _rot = ((deg % 360) + 360) % 360 * Math.PI / 180;
        _cx = c.X; _cy = c.Y;
        InvalidateVisual();
    }

    /// <summary>Clic sulla mappa: punto agganciato o linea (Maiusc), coordinate locali.</summary>
    public event Action<List<V2>, bool>? Picked;

    double _cx, _cy, _s = 2;
    Point? _panLast;
    (List<V2> pts, bool line)? _hover;
    const double TolPx = 10;

    static readonly CultureInfo Ci = CultureInfo.GetCultureInfo("it-IT");
    static IBrush B(string hex, byte a = 255) { var c = Color.Parse(hex); return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); }
    static readonly IBrush Bg = B("#F3F2EE"), Bld = B("#DADDE0"), BldS = B("#959CA4"), Oth = B("#B9BEC4"), Side = B("#C9DCEA"),
        EdgeB = B("#1E2833"), Mark = B("#D81B60"), MarkSel = B("#1565C0"), Hov = B("#E7A500"), CamB = B("#D6603F"), LabelBg = B("#FFFFFF", 220);

    public PickMapCanvas() { ClipToBounds = true; Focusable = true; }

    Point S(V2 w)
    {
        double dx = w.X - _cx, dy = w.Y - _cy, c = Math.Cos(_rot), s = Math.Sin(_rot);
        double rx = dx * c - dy * s, ry = dx * s + dy * c;
        return new(rx * _s + Bounds.Width / 2, Bounds.Height / 2 - ry * _s);
    }
    V2 Wd(Point p)
    {
        double rx = (p.X - Bounds.Width / 2) / _s, ry = (Bounds.Height / 2 - p.Y) / _s, c = Math.Cos(_rot), s = Math.Sin(_rot);
        return new(rx * c + ry * s + _cx, -rx * s + ry * c + _cy);
    }

    public void ZoomTo(BBox b)
    {
        if (b.IsEmpty) return;
        _cx = (b.MinX + b.MaxX) / 2; _cy = (b.MinY + b.MaxY) / 2;
        double bw = Bounds.Width > 10 ? Bounds.Width : 380, bh = Bounds.Height > 10 ? Bounds.Height : 380;
        _s = Math.Min(bw / Math.Max(10, b.MaxX - b.MinX), bh / Math.Max(10, b.MaxY - b.MinY)) * 0.9;
        InvalidateVisual();
    }

    IEnumerable<List<V2>> Lines()
    {
        if (Data != null)
            foreach (var l in Data.Layers.Where(l => l.Role != 0))
                foreach (var r in l.Rings)
                {
                    if (!r.Closed) { yield return r.Pts; continue; }
                    var c = new List<V2>(r.Pts) { r.Pts[0] };
                    yield return c;
                }
        if (Project != null && ShowProject)
        {
            foreach (var e in Project.Edges) if (e.Points.Count >= 2) yield return e.Points;
            foreach (var el in Project.Elements)
                if (el.Polygon.Count >= 3) { var c = new List<V2>(el.Polygon) { el.Polygon[0] }; yield return c; }
        }
    }

    /// <summary>Vertice più vicino o, con linea = true, il tratto più vicino (con i due tratti adiacenti).</summary>
    (List<V2> pts, bool line)? Snap(V2 w, bool line)
    {
        double tol = TolPx / _s;
        if (!line)
        {
            V2? best = null; double bd = tol;
            foreach (var pl in Lines())
                foreach (var p in pl)
                {
                    double d = V2.Dist(p, w);
                    if (d < bd) { bd = d; best = p; }
                }
            return best is { } b ? (new List<V2> { b }, false) : (new List<V2> { w }, false);
        }
        List<V2>? bl = null; int bi = -1; double bs = tol * 1.5;
        foreach (var pl in Lines())
            for (int i = 0; i + 1 < pl.Count; i++)
            {
                var pr = PL.Project(new[] { pl[i], pl[i + 1] }, w);
                if (pr.Distance < bs) { bs = pr.Distance; bl = pl; bi = i; }
            }
        if (bl == null) return null;
        var seg = new List<V2>();
        if (bi > 0) seg.Add(bl[bi - 1]);
        seg.Add(bl[bi]); seg.Add(bl[bi + 1]);
        if (bi + 2 < bl.Count) seg.Add(bl[bi + 2]);
        return (seg, true);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        if (Data != null)
            foreach (var l in Data.Layers.Where(l => l.Role != 0))
            {
                var role = (LayerRole)l.Role;
                foreach (var r in l.Rings)
                {
                    if (r.Pts.Count < 2) continue;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(S(r.Pts[0]), r.Closed && role is LayerRole.Muri or LayerRole.Marciapiedi);
                        foreach (var p in r.Pts.Skip(1)) c.LineTo(S(p));
                        c.EndFigure(r.Closed);
                    }
                    ctx.DrawGeometry(role == LayerRole.Muri ? Bld : role == LayerRole.Marciapiedi ? Side : null,
                        new Pen(role == LayerRole.Muri ? BldS : Oth, 1), g);
                }
            }
        if (Project != null && ShowProject)
        {
            foreach (var e in Project.Elements.Where(e => e.Polygon.Count >= 3))
            {
                var (f, s) = MapCanvas.Colors(e.Type, 170);
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(S(e.Polygon[0]), true); foreach (var p in e.Polygon.Skip(1)) c.LineTo(S(p)); c.EndFigure(true); }
                ctx.DrawGeometry(f, new Pen(s, 1), g);
            }
            foreach (var e in Project.Edges.Where(e => e.Points.Count >= 2))
                for (int i = 0; i + 1 < e.Points.Count; i++) ctx.DrawLine(new Pen(EdgeB, 2), S(e.Points[i]), S(e.Points[i + 1]));
        }

        if (Camera is { } cam)
        {
            var p = S(new V2(cam.X, cam.Y));
            double half = Math.Atan(cam.W / 2.0 / cam.F);
            double yr = cam.Yaw * Math.PI / 180, L = 60 / _s;
            var a = new V2(cam.X + Math.Cos(yr + half) * L * 3, cam.Y + Math.Sin(yr + half) * L * 3);
            var b = new V2(cam.X + Math.Cos(yr - half) * L * 3, cam.Y + Math.Sin(yr - half) * L * 3);
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(p, true); c.LineTo(S(a)); c.LineTo(S(b)); c.EndFigure(true); }
            ctx.DrawGeometry(B("#D6603F", 40), new Pen(CamB, 1.5), g);
            ctx.DrawEllipse(CamB, new Pen(Brushes.White, 2), p, 6, 6);
        }

        foreach (var (n, pts, line, sel) in Marks)
        {
            var br = sel ? MarkSel : Mark;
            if (line && pts.Count >= 2) for (int i = 0; i + 1 < pts.Count; i++) ctx.DrawLine(new Pen(br, 3.5), S(pts[i]), S(pts[i + 1]));
            var at = line ? S((pts[pts.Count / 2 - 1 + pts.Count % 2] + pts[pts.Count / 2]) * 0.5) : S(pts[0]);
            ctx.DrawEllipse(br, new Pen(Brushes.White, 1.5), at, 9, 9);
            var ft = new FormattedText(n.ToString(), Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10, Brushes.White);
            ctx.DrawText(ft, at - new Vector(ft.Width / 2, ft.Height / 2));
        }

        // freccia del nord
        {
            var o = new Point(Bounds.Width - 24, Bounds.Height - 30);
            double c = Math.Cos(_rot), sn = Math.Sin(_rot);
            var tip = o + new Vector(-sn * 14, -c * 14);
            var l = o + new Vector(c * 6, -sn * 6); var r = o + new Vector(-c * 6, sn * 6);
            var g = new StreamGeometry();
            using (var gc = g.Open()) { gc.BeginFigure(tip, true); gc.LineTo(l); gc.LineTo(r); gc.EndFigure(true); }
            ctx.DrawGeometry(EdgeB, null, g);
            var ft = new FormattedText("N", Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10, EdgeB);
            ctx.DrawText(ft, o + new Vector(-sn * 24 - ft.Width / 2, -c * 24 - ft.Height / 2));
        }

        if (AwaitingPick && _hover is { } h)
        {
            if (h.line) for (int i = 0; i + 1 < h.pts.Count; i++) ctx.DrawLine(new Pen(Hov, 4), S(h.pts[i]), S(h.pts[i + 1]));
            else ctx.DrawRectangle(null, new Pen(Hov, 2), new Rect(S(h.pts[0]) - new Vector(6, 6), new Size(12, 12)));
        }
        if (AwaitingPick)
        {
            var ft = new FormattedText("Clic: punto (si aggancia ai vertici) · Maiusc+clic: linea · Ctrl+rotella: ruota", Ci, FlowDirection.LeftToRight, Typeface.Default, 11.5, EdgeB);
            ctx.FillRectangle(LabelBg, new Rect(8, 8, ft.Width + 12, ft.Height + 6), 4);
            ctx.DrawText(ft, new Point(14, 11));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed || !AwaitingPick)
        {
            _panLast = pt.Position; e.Pointer.Capture(this); return;
        }
        if (pt.Properties.IsLeftButtonPressed)
        {
            var s = Snap(Wd(pt.Position), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (s is { } r) Picked?.Invoke(r.pts, r.line);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panLast is { } last)
        {
            var w0 = Wd(last); var w1 = Wd(p); _cx -= w1.X - w0.X; _cy -= w1.Y - w0.Y; _panLast = p;
        }
        else if (AwaitingPick) _hover = Snap(Wd(p), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panLast = null; e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var sp = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            RotateBy(e.Delta.Y > 0 ? 5 : -5); e.Handled = true; return;
        }
        var before = Wd(sp);
        _s = Math.Clamp(_s * Math.Pow(1.2, e.Delta.Y), 0.05, 400);
        var after = Wd(sp);
        _cx += before.X - after.X; _cy += before.Y - after.Y;
        InvalidateVisual(); e.Handled = true;
    }
}
