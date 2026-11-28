using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CicloDraft.Geometry;
using CicloDraft.IO;

namespace CicloDraft.Controls;

public enum AreaTool { Pan, Rect, Poly }

/// <summary>Mappa di anteprima per scegliere l'area da ritagliare.</summary>
public class ClipCanvas : Control
{
    public IReadOnlyList<SourceLayer> Layers { get; set; } = Array.Empty<SourceLayer>();
    public V2 Origin;
    public AreaTool Tool = AreaTool.Pan;
    /// <summary>Area in coordinate locali.</summary>
    public List<V2> Area = new();
    public bool AreaClosed;
    public double Margin;

    public event Action? AreaChanged;
    public event Action<V2?>? PointerWorldMoved;

    double _cx, _cy, _s = 0.2;
    Point? _panLast;
    V2? _rectStart;
    V2? _mouse;

    static readonly CultureInfo Ci = CultureInfo.GetCultureInfo("it-IT");
    static IBrush B(string hex, byte a = 255) { var c = Color.Parse(hex); return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); }

    public static readonly string[] Palette = { "#6B7280", "#B5532F", "#2F6FB5", "#3E8E5E", "#8A5BB5", "#B58A2F", "#2F9AA0", "#B52F6B" };
    public static IBrush LayerBrush(int i, byte a = 255) => B(Palette[i % Palette.Length], a);

    static readonly IBrush Bg = B("#F3F2EE");
    static readonly IBrush AreaFill = B("#D6603F", 28);
    static readonly IBrush AreaStroke = B("#D6603F");
    static readonly IBrush MarginStroke = B("#D6603F", 140);
    static readonly IBrush Text = B("#1E2833");
    static readonly IBrush LabelBg = B("#FFFFFF", 220);
    static readonly IBrush ExtStroke = B("#5B6470", 110);

    readonly Dictionary<SourceLayer, StreamGeometry> _cache = new();

    public ClipCanvas() { Focusable = true; ClipToBounds = true; }

    public void InvalidateLayer(SourceLayer l) { _cache.Remove(l); InvalidateVisual(); }

    Matrix WorldToScreen => new(_s, 0, 0, -_s, Bounds.Width / 2 - _cx * _s, Bounds.Height / 2 + _cy * _s);
    Point ToScreen(V2 w) => new((w.X - _cx) * _s + Bounds.Width / 2, Bounds.Height / 2 - (w.Y - _cy) * _s);
    V2 ToWorld(Point p) => new((p.X - Bounds.Width / 2) / _s + _cx, (Bounds.Height / 2 - p.Y) / _s + _cy);
    public double Scale => _s;

    public void ZoomTo(BBox b)
    {
        if (b.IsEmpty) return;
        _cx = (b.MinX + b.MaxX) / 2; _cy = (b.MinY + b.MaxY) / 2;
        double w = Math.Max(b.MaxX - b.MinX, 10), h = Math.Max(b.MaxY - b.MinY, 10);
        double bw = Bounds.Width > 10 ? Bounds.Width : 900, bh = Bounds.Height > 10 ? Bounds.Height : 700;
        _s = Math.Min(bw / w, bh / h) * 0.9;
        InvalidateVisual();
    }

    StreamGeometry Geo(SourceLayer l)
    {
        if (_cache.TryGetValue(l, out var g)) return g;
        g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.SetFillRule(FillRule.EvenOdd);
            foreach (var r in l.Preview!)
            {
                if (r.Pts.Count < 2) continue;
                c.BeginFigure(new Point(r.Pts[0].X, r.Pts[0].Y), r.Closed);
                for (int i = 1; i < r.Pts.Count; i++) c.LineTo(new Point(r.Pts[i].X, r.Pts[i].Y));
                c.EndFigure(r.Closed);
            }
        }
        _cache[l] = g;
        return g;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        using (ctx.PushTransform(WorldToScreen))
        {
            foreach (var l in Layers)
            {
                if (!l.Show || l.Preview == null) continue;
                var pen = new Pen(LayerBrush(l.ColorIndex), 1 / _s);
                if (l.IsPoint)
                {
                    var pb = LayerBrush(l.ColorIndex, 200);
                    double r = 1.6 / _s;
                    if (l.Preview.Count < 60000)
                        foreach (var p in l.Preview) ctx.DrawRectangle(pb, null, new Rect(p.Pts[0].X - r, p.Pts[0].Y - r, 2 * r, 2 * r));
                    continue;
                }
                bool poly = l.GeomType.ToUpperInvariant().Contains("POLYGON") || l.Kind == SourceKind.Shp && l.GeomType.Contains("POLYGON");
                ctx.DrawGeometry(poly ? LayerBrush(l.ColorIndex, 40) : null, pen, Geo(l));
            }
        }

        // estensione dei livelli inclusi ma non mostrati
        foreach (var l in Layers.Where(l => l.Include && (!l.Show || l.Preview == null) && !l.Extent.IsEmpty))
        {
            var a = ToScreen(new V2(l.Extent.MinX, l.Extent.MinY) - Origin);
            var b = ToScreen(new V2(l.Extent.MaxX, l.Extent.MaxY) - Origin);
            ctx.DrawRectangle(null, new Pen(ExtStroke, 1, new DashStyle(new double[] { 4, 4 }, 0)), new Rect(a, b).Normalize());
        }

        // area
        var pts = new List<V2>(Area);
        if (!AreaClosed && Tool == AreaTool.Poly && _mouse is { } m && pts.Count > 0) pts.Add(m);
        if (_rectStart is { } rs && _mouse is { } m2) pts = RectPts(rs, m2);
        if (pts.Count >= 2)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(ToScreen(pts[0]), true);
                foreach (var p in pts.Skip(1)) c.LineTo(ToScreen(p));
                c.EndFigure(AreaClosed || _rectStart != null || pts.Count > 2);
            }
            ctx.DrawGeometry(AreaFill, new Pen(AreaStroke, 2), g);
            if (Margin > 0 && AreaClosed && Area.Count >= 3)
            {
                var b = BBox.Of(Area);
                var a = ToScreen(new V2(b.MinX - Margin, b.MinY - Margin)); var c2 = ToScreen(new V2(b.MaxX + Margin, b.MaxY + Margin));
                if (Area.Count == 4 && IsRect(Area))
                    ctx.DrawRectangle(null, new Pen(MarginStroke, 1, new DashStyle(new double[] { 5, 3 }, 0)), new Rect(a, c2).Normalize());
            }
            foreach (var p in Area)
            {
                var sp = ToScreen(p);
                ctx.DrawRectangle(Brushes.White, new Pen(AreaStroke, 1.5), new Rect(sp.X - 3, sp.Y - 3, 6, 6));
            }
            var bb = BBox.Of(pts);
            Label(ctx, ToScreen(new V2(bb.MinX, bb.MaxY)) + new Vector(4, -22),
                $"{(bb.MaxX - bb.MinX).ToString("0", Ci)} × {(bb.MaxY - bb.MinY).ToString("0", Ci)} m");
        }

        DrawScaleBar(ctx);
    }

    static bool IsRect(List<V2> a) => a.Count == 4 && Math.Abs(a[0].Y - a[1].Y) < 1e-6 && Math.Abs(a[1].X - a[2].X) < 1e-6;

    static List<V2> RectPts(V2 a, V2 b) => new()
    {
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new(Math.Max(a.X, b.X), Math.Min(a.Y, b.Y)),
        new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), new(Math.Min(a.X, b.X), Math.Max(a.Y, b.Y))
    };

    static void Label(DrawingContext ctx, Point at, string s)
    {
        var ft = new FormattedText(s, Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12, Text);
        ctx.FillRectangle(LabelBg, new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4), 3);
        ctx.DrawText(ft, at);
    }

    void DrawScaleBar(DrawingContext ctx)
    {
        double target = 120 / _s;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double len = new[] { 1, 2, 5, 10 }.Select(k => k * mag).Last(v => v <= target * 1.0001);
        double px = len * _s;
        var o = new Point(16, Bounds.Height - 22);
        var pen = new Pen(Text, 2);
        ctx.DrawLine(pen, o, o + new Vector(px, 0));
        ctx.DrawLine(pen, o + new Vector(0, -5), o + new Vector(0, 3));
        ctx.DrawLine(pen, o + new Vector(px, -5), o + new Vector(px, 3));
        var ft = new FormattedText(len >= 1000 ? $"{len / 1000:0.#} km" : $"{len:0} m", Ci, FlowDirection.LeftToRight, Typeface.Default, 11, Text);
        ctx.DrawText(ft, o + new Vector(px + 6, -ft.Height / 2 - 1));
    }

    // ───────────── input ─────────────

    public void SetRect(V2 a, V2 b)
    {
        Area = RectPts(a, b); AreaClosed = true;
        AreaChanged?.Invoke(); InvalidateVisual();
    }

    public void ClearArea() { Area = new(); AreaClosed = false; _rectStart = null; AreaChanged?.Invoke(); InvalidateVisual(); }

    public void ClosePolygon()
    {
        if (Tool == AreaTool.Poly && !AreaClosed && Area.Count >= 3) { AreaClosed = true; AreaChanged?.Invoke(); InvalidateVisual(); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        var sp = pt.Position; var w = ToWorld(sp);
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed || (pt.Properties.IsLeftButtonPressed && Tool == AreaTool.Pan))
        {
            _panLast = sp; e.Pointer.Capture(this); return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (Tool == AreaTool.Rect)
        {
            _rectStart = w; _mouse = w; e.Pointer.Capture(this);
        }
        else if (Tool == AreaTool.Poly)
        {
            if (AreaClosed) { Area = new(); AreaClosed = false; }
            if (e.ClickCount >= 2) { ClosePolygon(); return; }
            Area.Add(w);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var sp = e.GetPosition(this);
        var w = ToWorld(sp);
        _mouse = w;
        PointerWorldMoved?.Invoke(w + Origin);
        if (_panLast is { } last)
        {
            var d = sp - last;
            _cx -= d.X / _s; _cy += d.Y / _s; _panLast = sp;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_rectStart is { } a && _mouse is { } b)
        {
            _rectStart = null;
            if (Math.Abs(a.X - b.X) * _s > 4 && Math.Abs(a.Y - b.Y) * _s > 4) SetRect(a, b);
        }
        _panLast = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerWorldMoved?.Invoke(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var sp = e.GetPosition(this);
        var before = ToWorld(sp);
        _s = Math.Clamp(_s * Math.Pow(1.2, e.Delta.Y), 0.0005, 500);
        var after = ToWorld(sp);
        _cx += before.X - after.X; _cy += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter) { ClosePolygon(); e.Handled = true; }
        else if (e.Key == Key.Back && Tool == AreaTool.Poly && !AreaClosed && Area.Count > 0) { Area.RemoveAt(Area.Count - 1); InvalidateVisual(); e.Handled = true; }
    }
}
