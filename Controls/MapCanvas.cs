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

/// <summary>Vista planimetrica: disegno e interazione col mouse.</summary>
public class MapCanvas : Control
{
    Editor? _ed;
    public Editor? Editor
    {
        get => _ed;
        set
        {
            if (_ed != null) _ed.Redraw -= OnRedraw;
            _ed = value;
            if (_ed != null) _ed.Redraw += OnRedraw;
            InvalidateBackground();
        }
    }

    // Vista: centro in metri locali, scala in pixel per metro
    double _cx, _cy, _s = 2;
    Point? _panLast;
    int _dragKey = -1; bool _keyMoved;
    (int i, int sign)? _dragHandle;
    bool _dragging;
    (int i, bool end)? _dragVar;
    int _dragSecEnd = -1;
    Point _lastScreen;

    public event Action<V2?>? PointerWorldMoved;
    /// <summary>Clic sull'icona di un fotoinserimento.</summary>
    public event Action<PhotoInsertRef>? PhotoClicked;
    public double Scale => _s;
    bool _showLabels = true;
    bool _showPhotoIcons = true;
    /// <summary>Icone dei fotoinserimenti sulla mappa (menu Visualizza).</summary>
    public bool ShowPhotoIcons { get => _showPhotoIcons; set { _showPhotoIcons = value; InvalidateVisual(); } }
    /// <summary>Etichette con il nome degli elementi sulla mappa (menu Visualizza).</summary>
    // ───────────── note: penna e testo ─────────────

    public enum NoteTool { None, Pen, Text, Eraser }
    NoteTool _tool;
    /// <summary>Strumento delle note attivo (penna, testo, gomma) o nessuno.</summary>
    public NoteTool Tool { get => _tool; set { _tool = value; _stroke = null; Cursor = value == NoteTool.None ? Cursor.Default : new Cursor(value == NoteTool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross); InvalidateVisual(); } }
    public string NoteColor = "#E53935";
    public double PenSize = 4;
    public double TextSize = 16;
    /// <summary>Disegno per la stampa (PDF): fondo bianco, niente selezioni, maniglie, note e scala.</summary>
    public bool Print;
    /// <summary>In stampa: sezioni e fotoinserimenti da mostrare (null = tutti).</summary>
    public HashSet<CrossSection>? PrintSections;
    public HashSet<PhotoInsertRef>? PrintPhotos;
    bool _showGroups = true;
    /// <summary>Bordi colorati degli elementi dei gruppi (pulsante «Gruppi» in alto). Nella modifica di un gruppo si vedono sempre.</summary>
    public bool ShowGroups { get => _showGroups; set { _showGroups = value; InvalidateVisual(); } }
    bool _showNotes = true;
    public bool ShowNotes { get => _showNotes; set { _showNotes = value; InvalidateVisual(); } }
    List<V2>? _stroke;
    /// <summary>Clic con lo strumento testo: dove scrivere (mondo, schermo) ed eventuale nota esistente da modificare.</summary>
    public event Action<V2, Point, Annotation?>? TextRequested;

    Annotation? HitNote(Point sp)
    {
        var ed = _ed; if (ed == null) return null;
        for (int k = ed.Project.Annotations.Count - 1; k >= 0; k--)
        {
            var a = ed.Project.Annotations[k];
            if (a.IsText)
            {
                if (a.Pts.Count == 0) continue;
                var ft = NoteText(a);
                var p = ToScreen(a.Pts[0]);
                if (new Rect(p.X - 4, p.Y - 4, ft.Width + 8, ft.Height + 8).Contains(sp)) return a;
            }
            else
                for (int i = 0; i + 1 < a.Pts.Count; i++)
                {
                    var p1 = ToScreen(a.Pts[i]); var p2 = ToScreen(a.Pts[i + 1]);
                    var d = p2 - p1; double L2 = d.X * d.X + d.Y * d.Y;
                    double t = L2 < 1e-9 ? 0 : Math.Clamp(((sp.X - p1.X) * d.X + (sp.Y - p1.Y) * d.Y) / L2, 0, 1);
                    var q = p1 + d * t;
                    if (Math.Sqrt((sp.X - q.X) * (sp.X - q.X) + (sp.Y - q.Y) * (sp.Y - q.Y)) < a.Size / 2 + 5) return a;
                }
        }
        return null;
    }

    static FormattedText NoteText(Annotation a) =>
        new(a.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), a.Size, new SolidColorBrush(Color.Parse(a.Color)));

    void DrawNotes(DrawingContext ctx)
    {
        var ed = _ed; if (ed == null || !_showNotes) return;
        void Stroke(List<V2> pts, string col, double size)
        {
            if (pts.Count == 0) return;
            var pen = new Pen(new SolidColorBrush(Color.Parse(col)), size, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            if (pts.Count == 1) { var p = ToScreen(pts[0]); ctx.DrawEllipse(pen.Brush, null, p, size / 2, size / 2); return; }
            ctx.DrawGeometry(null, pen, ScreenLine(pts));
        }
        var halo = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
        foreach (var a in ed.Project.Annotations)
        {
            if (!a.IsText) { Stroke(a.Pts, a.Color, a.Size); continue; }
            if (a.Pts.Count == 0 || string.IsNullOrEmpty(a.Text)) continue;
            var ft = NoteText(a); var p = ToScreen(a.Pts[0]);
            ctx.FillRectangle(halo, new Rect(p.X - 3, p.Y - 1, ft.Width + 6, ft.Height + 2), 3);
            ctx.DrawText(ft, p);
        }
        if (_stroke != null) Stroke(_stroke, NoteColor, PenSize);
    }

    /// <summary>Clic con uno strumento delle note; true se il clic è stato usato.</summary>
    bool NotePressed(PointerPressedEventArgs e, Point sp, V2 w)
    {
        var ed = _ed; if (ed == null || _tool == NoteTool.None || ed.Mode != Mode.Idle) return false;
        switch (_tool)
        {
            case NoteTool.Pen:
                _stroke = new List<V2> { w }; e.Pointer.Capture(this); InvalidateVisual(); return true;
            case NoteTool.Text:
                var hit = HitNote(sp);
                if (hit is { IsText: true }) TextRequested?.Invoke(hit.Pts[0], ToScreen(hit.Pts[0]), hit);
                else TextRequested?.Invoke(w, sp, null);
                return true;
            case NoteTool.Eraser:
                if (HitNote(sp) is { } er) { ed.Project.Annotations.Remove(er); ed.NotifyAll(); }
                return true;
        }
        return false;
    }

    public bool ShowLabels { get => _showLabels; set { _showLabels = value; InvalidateVisual(); } }

    const double TolPx = 9;

    static readonly CultureInfo Ci = CultureInfo.GetCultureInfo("it-IT");
    static IBrush B(string hex, byte a = 255) { var c = Color.Parse(hex); return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); }

    static readonly IBrush BgBrush = B("#F3F2EE");
    static readonly IBrush BuildingFill = B("#DADDE0");
    static readonly IBrush BuildingStroke = B("#959CA4");
    static readonly IBrush SidewalkFill = B("#E9E6DF");
    static readonly IBrush SidewalkStroke = B("#8E9BA6");
    static readonly IBrush OtherStroke = B("#BFC4C9");
    static readonly IBrush RoadEdge = B("#1E2833");
    static readonly IBrush Hover = B("#E7A500");
    static readonly IBrush Range = B("#FF7A00", 170);
    static readonly IBrush SelBrush = B("#1565C0");
    static readonly IBrush MultiFill = B("#1565C0", 64);
    static readonly IBrush AreaFill = B("#2FCB79", 85);
    static readonly IBrush AreaStroke = B("#1E9E5A");
    static readonly IBrush SnapBrush = B("#D81B60");
    static readonly IBrush TextBrush = B("#1E2833");
    static readonly IBrush LabelBg = B("#FFFFFF", 215);
    static readonly IBrush ErrBrush = B("#C62828");
    static readonly IBrush TreeBrush = B("#6C8E4B", 150);

    public static (IBrush fill, IBrush stroke) Colors(ElementType t, byte alpha = 215) => t switch
    {
        ElementType.Marciapiede => (B("#BBA781", alpha), B("#7D6C4C")),
        ElementType.Ciclabile => (B("#D6603F", alpha), B("#94381F")),
        ElementType.Carreggiata => (B("#6B7280", alpha), B("#3F4550")),
        ElementType.Corsia => (B("#93A3B8", alpha), B("#56667A")),
        ElementType.Parcheggio => (B("#6E9BC9", alpha), B("#3C6A98")),
        ElementType.Aiuola => (B("#86B068", alpha), B("#527B37")),
        ElementType.Ciclopedonale => (B("#C99A7C", alpha), B("#8A5E43")),
        ElementType.Separatore => (B("#F2C318", alpha), B("#A78300")),
        ElementType.CorsiaCiclabile => (B("#E8906F", alpha), B("#B0553A")),
        ElementType.CorsiaDoppia => (B("#7D8696", alpha), B("#4A5260")),
        ElementType.Indefinito => (B("#C9C2D6", alpha), B("#7A6F8C")),
        _ => (B("#999999", alpha), B("#555555"))
    };

    public static string Hex(ElementType t) => t switch
    {
        ElementType.Marciapiede => "#BBA781", ElementType.Ciclabile => "#D6603F", ElementType.Carreggiata => "#6B7280",
        ElementType.Corsia => "#93A3B8", ElementType.Parcheggio => "#6E9BC9", ElementType.Aiuola => "#86B068",
        ElementType.Separatore => "#F2C318", ElementType.CorsiaCiclabile => "#E8906F", ElementType.CorsiaDoppia => "#7D8696", ElementType.Indefinito => "#C9C2D6", ElementType.Ciclopedonale => "#C99A7C", _ => "#999999"
    };

    StreamGeometry? _gBuild, _gSide, _gOther;
    bool _bgDirty = true;

    public MapCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void InvalidateBackground() { _bgDirty = true; _bgBmpValid = false; InvalidateVisual(); }

    // ── sfondo (edifici, marciapiedi esistenti, livelli di sfondo, alberi) in un'immagine ──
    // Durante zoom e spostamenti si ridisegna l'immagine già pronta, scalata e spostata: è immediato anche con
    // migliaia di edifici. Quando la vista si ferma, l'immagine si rifà nitida alla nuova scala.
    static readonly bool NoBgCache = Environment.GetEnvironmentVariable("CICLODRAFT_NO_BG_CACHE") == "1";   // solo per le misure
    Avalonia.Media.Imaging.RenderTargetBitmap? _bgBmp;
    bool _bgBmpValid;
    double _bgS, _bgCx, _bgCy, _bgRot; Size _bgSize; Matrix _bgM = Matrix.Identity;
    Avalonia.Threading.DispatcherTimer? _bgTimer;

    void DrawBackground(DrawingContext ctx)
    {
        var ed = _ed;
        using (ctx.PushTransform(WorldToScreen))
        {
            if (_gOther != null) ctx.DrawGeometry(null, P(OtherStroke, 1), _gOther);
            if (_gSide != null) ctx.DrawGeometry(SidewalkFill, P(SidewalkStroke, 1, new DashStyle(new double[] { 4, 3 }, 0), PenLineCap.Flat), _gSide);
            if (_gBuild != null) ctx.DrawGeometry(BuildingFill, P(BuildingStroke, 1), _gBuild);
            if (ed?.Data is { } dd)
            {
                var treeBrush = TreeBrush;
                foreach (var l in dd.Layers)
                    if (l.Role != 0 && l.Visible && l.Points.Count > 0)
                        foreach (var tp in l.Points) ctx.DrawEllipse(treeBrush, null, new Point(tp.X, tp.Y), 1.6, 1.6);
            }
        }
    }

    void RebuildBackgroundBitmap()
    {
        if (Bounds.Width < 2 || Bounds.Height < 2) return;
        double scale = VisualRoot?.RenderScaling ?? 1;
        var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
        using (var c = bmp.CreateDrawingContext()) DrawBackground(c);
        _bgBmp?.Dispose();
        _bgBmp = bmp; _bgS = _s; _bgCx = _cx; _bgCy = _cy; _bgRot = RotDeg; _bgM = WorldToScreen; _bgSize = Bounds.Size; _bgBmpValid = true;
    }

    void ScheduleBackgroundRefresh()
    {
        _bgTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(140), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => { _bgTimer!.Stop(); RebuildBackgroundBitmap(); InvalidateVisual(); });
        _bgTimer.Stop(); _bgTimer.Start();
    }
    void OnRedraw() => InvalidateVisual();

    // ───────────── trasformazioni ─────────────

    // rotazione della vista (antioraria, in gradi): sta nel progetto, così si ritrova riaprendolo
    double RotDeg => _ed?.Project.ViewRotation ?? 0;
    double Cos => Math.Cos(RotDeg * Math.PI / 180);
    double Sin => Math.Sin(RotDeg * Math.PI / 180);
    Matrix WorldToScreen
    {
        get
        {
            double c = Cos * _s, s = Sin * _s;
            return new(c, -s, -s, -c, Bounds.Width / 2 - (c * _cx - s * _cy), Bounds.Height / 2 + (s * _cx + c * _cy));
        }
    }
    Point ToScreen(V2 w)
    {
        double dx = w.X - _cx, dy = w.Y - _cy, c = Cos, s = Sin;
        return new(Bounds.Width / 2 + (dx * c - dy * s) * _s, Bounds.Height / 2 - (dx * s + dy * c) * _s);
    }
    V2 ToWorld(Point p)
    {
        double rx = (p.X - Bounds.Width / 2) / _s, ry = (Bounds.Height / 2 - p.Y) / _s, c = Cos, s = Sin;
        return new(_cx + rx * c + ry * s, _cy - rx * s + ry * c);
    }
    /// <summary>Direzione nel mondo → direzione sullo schermo (unitaria se lo è v).</summary>
    Vector ScreenDir(V2 v) { double c = Cos, s = Sin; return new(v.X * c - v.Y * s, -(v.X * s + v.Y * c)); }

    /// <summary>Ruota la vista attorno al centro dello schermo (gradi, positivi = antiorario).</summary>
    public void Rotate(double deg)
    {
        if (_ed == null) return;
        double r = (RotDeg + deg) % 360; if (r > 180) r -= 360; if (r <= -180) r += 360;
        if (Math.Abs(r) < 1e-9) r = 0;
        _ed.Project.ViewRotation = r;
        _bgBmpValid = false;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }
    public double Rotation => RotDeg;
    public event Action? ViewChanged;
    double Tol => TolPx / _s;

    /// <summary>
    /// Inquadra tutti i punti al massimo ingrandimento possibile dentro l'area libera della mappa
    /// (free = margini occupati da pulsanti e riquadri sopra la mappa), tenendo conto della rotazione.
    /// </summary>
    public bool FitPoints(IEnumerable<V2> pts, Thickness free)
    {
        double W = Bounds.Width, H = Bounds.Height;
        if (W < 10 || H < 10) return false;
        double c = Cos, s = Sin;
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        foreach (var p in pts)
        {
            double rx = p.X * c - p.Y * s, ry = p.X * s + p.Y * c;
            if (rx < x0) x0 = rx; if (rx > x1) x1 = rx; if (ry < y0) y0 = ry; if (ry > y1) y1 = ry;
        }
        if (x0 > x1) return false;
        double fw = Math.Max(40, W - free.Left - free.Right), fh = Math.Max(40, H - free.Top - free.Bottom);
        _s = Math.Clamp(Math.Min(fw / Math.Max(x1 - x0, 0.5), fh / Math.Max(y1 - y0, 0.5)), 1e-4, 2000);
        double fx = free.Left + fw / 2, fy = free.Top + fh / 2;
        double rx0 = (x0 + x1) / 2 - (fx - W / 2) / _s, ry0 = (y0 + y1) / 2 + (fy - H / 2) / _s;
        _cx = rx0 * c + ry0 * s; _cy = -rx0 * s + ry0 * c;
        InvalidateVisual();
        ViewChanged?.Invoke();
        return true;
    }

    public (double cx, double cy, double s) View { get => (_cx, _cy, _s); set { (_cx, _cy, _s) = value; InvalidateVisual(); ViewChanged?.Invoke(); } }

    public void ZoomTo(BBox b)
    {
        if (b.IsEmpty || Bounds.Width < 10) { _cx = 0; _cy = 0; _s = 2; InvalidateVisual(); return; }
        _cx = (b.MinX + b.MaxX) / 2; _cy = (b.MinY + b.MaxY) / 2;
        double w0 = Math.Max(b.MaxX - b.MinX, 1), h0 = Math.Max(b.MaxY - b.MinY, 1);
        // ingombro del riquadro ruotato
        double w = Math.Abs(w0 * Cos) + Math.Abs(h0 * Sin), h = Math.Abs(w0 * Sin) + Math.Abs(h0 * Cos);
        _s = Math.Min(Bounds.Width / w, Bounds.Height / h) * 0.9;
        InvalidateVisual();
    }

    // ───────────── disegno ─────────────

    void BuildBackground()
    {
        _bgDirty = false;
        _gBuild = _gSide = _gOther = null;
        var d = _ed?.Data;
        if (d == null) return;
        _gBuild = Geo(d.RingsWithRole(LayerRole.Muri).Where(x => x.layer.Visible).Select(x => x.ring), true);
        _gSide = Geo(d.RingsWithRole(LayerRole.Marciapiedi).Where(x => x.layer.Visible).Select(x => x.ring), true);
        _gOther = Geo(d.RingsWithRole(LayerRole.Sfondo, LayerRole.Edifici3D).Where(x => x.layer.Visible).Select(x => x.ring), false);
    }

    static StreamGeometry Geo(IEnumerable<Ring> rings, bool fill)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.SetFillRule(FillRule.EvenOdd);
        foreach (var r in rings)
        {
            if (r.Pts.Count < 2) continue;
            c.BeginFigure(new Point(r.Pts[0].X, r.Pts[0].Y), fill && r.Closed);
            for (int i = 1; i < r.Pts.Count; i++) c.LineTo(new Point(r.Pts[i].X, r.Pts[i].Y));
            c.EndFigure(r.Closed);
        }
        return g;
    }

    static StreamGeometry Line(IReadOnlyList<V2> pts, bool closed = false, bool filled = false)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (pts.Count == 0) return g;
        c.BeginFigure(new Point(pts[0].X, pts[0].Y), filled);
        for (int i = 1; i < pts.Count; i++) c.LineTo(new Point(pts[i].X, pts[i].Y));
        c.EndFigure(closed);
        return g;
    }

    readonly Dictionary<string, IBrush> _groupBrushes = new();
    IBrush GroupBrush(string hex)
    {
        if (!_groupBrushes.TryGetValue(hex, out var b)) { try { b = B(hex); } catch { b = B("#E6194B"); } _groupBrushes[hex] = b; }
        return b;
    }

    Pen P(IBrush b, double px, IDashStyle? dash = null, PenLineCap cap = PenLineCap.Round) =>
        new(b, px / _s, dash, cap, PenLineJoin.Round);

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(Print ? Brushes.White : BgBrush, r);
        var ed = _ed;
        if (ed == null) return;
        if (_bgDirty) BuildBackground();

        // sfondo: immagine pronta se la vista è la stessa; altrimenti quella vecchia scalata e spostata, e se ne prepara una nuova
        if (NoBgCache || Print) { DrawBackground(ctx); goto elements; }
        bool sameView = _bgBmpValid && _bgBmp != null && _bgS == _s && _bgCx == _cx && _bgCy == _cy && _bgRot == RotDeg && _bgSize == Bounds.Size;
        if (!_bgBmpValid || _bgBmp == null || _bgSize != Bounds.Size) { RebuildBackgroundBitmap(); sameView = _bgBmp != null; }
        if (_bgBmp != null)
        {
            if (sameView) ctx.DrawImage(_bgBmp, new Rect(_bgSize));
            else
            {
                // dall'immagine (schermo di allora) al mondo, e dal mondo allo schermo di adesso
                if (_bgM.TryInvert(out var inv))
                    using (ctx.PushTransform(inv * WorldToScreen))
                        ctx.DrawImage(_bgBmp, new Rect(_bgSize));
                ScheduleBackgroundRefresh();
            }
        }

        elements:
        using (ctx.PushTransform(WorldToScreen))
        {
            // prima gli elementi più grandi, poi quelli più piccoli: un parcheggio disegnato dentro la carreggiata resta visibile
            foreach (var e in ed.Project.Shown.OrderByDescending(x => x.Metrics.Area))
            {
                if (e.Polygon.Count < 3) continue;
                var (f, s) = Colors(e.Type);
                ctx.DrawGeometry(f, P(s, 1), Line(e.Polygon, true, true));
                if (e.HasStalls)
                    foreach (var sl in e.StallMarkings())
                        if (sl.Count >= 2) ctx.DrawGeometry(null, P(Brushes.White, 1.2), Line(sl));
                foreach (var dl in e.DividerLines())
                {
                    if (dl.Count < 2) continue;
                    bool bk = e.Type is ElementType.Ciclabile or ElementType.CorsiaCiclabile;
                    // trattini in metri veri (i trattini di Avalonia sono multipli dello spessore della penna), almeno 3 px
                    double th = 0.9 / _s, dmin = 3 / 0.9;
                    var dash = e.DividerStyle == DividerStyle.Tratteggiata
                        ? new DashStyle(bk ? new[] { Math.Max(dmin, 1 / th), Math.Max(dmin, 1 / th) } : new[] { Math.Max(dmin, 4.5 / th), Math.Max(dmin, 7.5 / th) }, 0) : null;
                    if (e.DividerStyle == DividerStyle.DoppiaContinua)
                    {
                        ctx.DrawGeometry(null, P(Brushes.White, 0.8), Line(PL.OffsetProfile(dl, _ => 0.12, 1, Array.Empty<double>(), 1)));
                        ctx.DrawGeometry(null, P(Brushes.White, 0.8), Line(PL.OffsetProfile(dl, _ => 0.12, -1, Array.Empty<double>(), 1)));
                    }
                    else ctx.DrawGeometry(null, P(Brushes.White, 0.9, dash, PenLineCap.Flat), Line(dl));
                }
            }

            // bordo colorato degli elementi che stanno in un blocco (più spesso per il blocco a cui si stanno aggiungendo)
            if ((ShowGroups || ed.AssignGroup != 0) && !Print)
            foreach (var e in ed.Project.Shown)
                if (e.Group != 0 && e.Polygon.Count >= 3 && ed.FindGroup(e.Group) is { } grp)
                    ctx.DrawGeometry(null, P(GroupBrush(grp.Color), grp.Id == ed.AssignGroup ? 4 : 2.6), Line(e.Polygon, true));

            if (!Print && ed.Preview is { Error: null, Polygon.Count: >= 3 } pv)
            {
                var (f, s) = Colors(pv.Type, 150);
                ctx.DrawGeometry(f, P(s, 1.5, new DashStyle(new double[] { 3, 2 }, 0), PenLineCap.Flat), Line(pv.Polygon, true, true));
            }

            foreach (var e in ed.Project.Edges)
            {
                if (e.Points.Count < 2 || !ed.Project.IsShown(e)) continue;
                if (e.Owner == -1) ctx.DrawGeometry(null, P(RoadEdge, 2.6), Line(e.Points));          // bordo strada dei progetti precedenti
                else if (e.Owner == -2) ctx.DrawGeometry(null, P(SelBrush, 2.2), Line(e.Points));     // linea appena disegnata
                else if (ed.Project.Find(e.Owner) is { Centered: true })                                // interasse
                    ctx.DrawGeometry(null, P(RoadEdge, 1.1, new DashStyle(new double[] { 6, 4 }, 0), PenLineCap.Flat), Line(e.Points));
            }

            if (!Print) {
            // Bordo sotto il puntatore
            if (ed.Mode is Mode.PickBase or Mode.PickOther || (ed.Mode == Mode.PickRange && ed.RangeFixed))
                if (ed.HoverEdge is { } he && ed.Project.GetEdge(he) is { } hl)
                    ctx.DrawGeometry(null, P(Hover, 5), Line(hl));

            // Tratto evidenziato
            if (ed.Mode is Mode.PickRange or Mode.SetWidth or Mode.PickOther && ed.DraftBase is { } db && !IsDrawnLine(ed, db) && ed.Project.GetEdge(db) is { } bl)
            {
                ctx.DrawGeometry(null, P(Hover, 2), Line(bl));
                var sub = PL.Sub(bl, ed.DraftStart, ed.DraftEnd);
                ctx.DrawGeometry(null, P(Range, ed.Mode == Mode.PickRange ? 8 : 3, null, PenLineCap.Flat), Line(sub));
                if (ed.Mode == Mode.PickOther && ed.DraftOther is { } dor && ed.Project.GetEdge(dor) is { } ol)
                    ctx.DrawGeometry(null, P(Hover, 4), Line(ol));
            }

            foreach (var ms in ed.MultiSel.Concat(ed.GroupSel))
                if (ms.Polygon.Count >= 3) ctx.DrawGeometry(MultiFill, P(SelBrush, 2.4), Line(ms.Polygon, true, true));
            foreach (var part in ed.AreaParts)
                ctx.DrawGeometry(AreaFill, P(AreaStroke, 1.2, new DashStyle(new double[] { 4, 3 }, 0), PenLineCap.Flat), Line(part, true, true));
            if (ed.AreaParts.Count + ed.MultiSel.Count > 1)
                foreach (var pa in ed.PickedRings) ctx.DrawGeometry(null, P(AreaStroke, 2.6), Line(pa, true));

            if (ed.Selected is { } sel && sel.Polygon.Count >= 3)
            {
                ctx.DrawGeometry(null, P(SelBrush, 2), Line(sel.Polygon, true));
                if (sel.Mode == ElementMode.Offset) ctx.DrawGeometry(null, P(SelBrush, 3.5), Line(sel.Outer));
            }

            if (ed.Mode == Mode.Trace && ed.TracePts.Count > 0)
            {
                var pts = new List<V2>(ed.TracePts);
                if (ed.Mouse is { } m) pts.Add(ed.Snap?.P ?? m);
                ctx.DrawGeometry(null, P(SelBrush, 2.6), Line(ed.TracePts));
                ctx.DrawGeometry(null, P(SelBrush, 1.4, new DashStyle(new double[] { 3, 3 }, 0), PenLineCap.Flat), Line(pts.Skip(pts.Count - 2).ToList()));
            }
            }
        }

        if (!Print) DrawNotes(ctx);

        // ── sovrapposizioni in coordinate schermo ──
        foreach (var e in ed.Project.Edges)
            if (e.Points.Count >= 2 && e.Owner == -1) DrawEdgeDecor(ctx, e);

        foreach (var e in ed.Project.Shown)
            if (e.Error == null && e.Polygon.Count >= 3) { if (ShowLabels) DrawElementLabel(ctx, e); }
            else if (e.Error != null && ed.Project.GetEdge(e.Base) is { } bl2)
                Label(ctx, ToScreen(PL.PointAt(bl2, e.Start)), "⚠ " + e.Name + ": " + e.Error, ErrBrush);

        if (ShowPhotoIcons) DrawPhotoIcons(ctx, ed);
        DrawSections(ctx, ed);
        if (Print) return;

        // elemento tracciato selezionato: verso della linea (frecce, inizio/fine) e lati destro/sinistro,
        // per «Aggiungi a destra / a sinistra»
        if (ed.Mode == Mode.Idle && ed.CanAddAlongside && ed.Selected is { } se && se.Guide.Count >= 2)
            DrawDirection(ctx, se);

        if (ed.Mode == Mode.EditLine && ed.EditEdge is { Anchors: { } an } eed)
        {
            bool closedEdit = ed.EditIsPolygon;
            if (eed.Points.Count >= 2)
                ctx.DrawGeometry(null, new Pen(SelBrush, 2.4), ScreenLine(closedEdit ? eed.Points.Append(eed.Points[0]).ToList() : eed.Points));
            // maniglie dei punti in curva
            var hPen = new Pen(SelBrush, 1.2);
            foreach (var ca in an.Where(a => a.Curve))
            {
                var P = new V2(ca.X, ca.Y); var H = new V2(ca.Hx, ca.Hy);
                var h1 = ToScreen(P + H); var h2 = ToScreen(P - H);
                ctx.DrawLine(hPen, h2, h1);
                ctx.DrawEllipse(Brushes.White, new Pen(SelBrush, 1.6), h1, 4, 4);
                ctx.DrawEllipse(Brushes.White, new Pen(SelBrush, 1.6), h2, 4, 4);
            }
            if (ed.EditExtend != 0 && an.Count > 0 && ed.Mouse is { } mm)
            {
                var endA = ed.EditExtend > 0 ? an[^1] : an[0];
                ctx.DrawLine(new Pen(SelBrush, 1.4, new DashStyle(new double[] { 3, 3 }, 0)), ToScreen(new V2(endA.X, endA.Y)), ToScreen(ed.Snap?.P ?? mm));
            }
            for (int i = 0; i < an.Count; i++)
            {
                if (an[i].Auto) continue;
                var sp = ToScreen(new V2(an[i].X, an[i].Y));
                bool sel = i == ed.EditSel, end = !closedEdit && (i == 0 || i == an.Count - 1);
                IBrush fill = sel ? SelBrush : an[i].El >= 0 ? SnapBrush : Brushes.White;
                double rr = sel ? 6 : 4.5;
                if (end) ctx.DrawEllipse(fill, new Pen(SelBrush, 2), sp, rr + 1, rr + 1);
                else if (an[i].Curve) ctx.DrawEllipse(fill, new Pen(SelBrush, 1.6), sp, rr, rr);
                else ctx.DrawRectangle(fill, new Pen(SelBrush, 1.6), new Rect(sp.X - rr, sp.Y - rr, 2 * rr, 2 * rr));
            }
            if (an.Count > 0 && !closedEdit)
            {
                Label(ctx, ToScreen(new V2(an[0].X, an[0].Y)) + new Vector(10, -22), "inizio", TextBrush);
                Label(ctx, ToScreen(new V2(an[^1].X, an[^1].Y)) + new Vector(10, 6), "fine", TextBrush);
            }
            if (ed.Snap is { } sn2)
            {
                var sp = ToScreen(sn2.P);
                var pen = new Pen(SnapBrush, 2);
                if (sn2.Vertex) ctx.DrawRectangle(null, pen, new Rect(sp.X - 6, sp.Y - 6, 12, 12));
                else { ctx.DrawLine(pen, sp + new Vector(-5, -5), sp + new Vector(5, 5)); ctx.DrawLine(pen, sp + new Vector(-5, 5), sp + new Vector(5, -5)); }
            }
        }

        if (ed.Mode == Mode.Trace)
        {
            foreach (var p in ed.TracePts)
            {
                var sp = ToScreen(p);
                ctx.DrawRectangle(Brushes.White, new Pen(SelBrush, 1.5), new Rect(sp.X - 3, sp.Y - 3, 6, 6));
            }
            if (ed.Snap is { } sn)
            {
                var sp = ToScreen(sn.P);
                var pen = new Pen(SnapBrush, 2);
                if (sn.Vertex) ctx.DrawRectangle(null, pen, new Rect(sp.X - 6, sp.Y - 6, 12, 12));
                else { ctx.DrawLine(pen, sp + new Vector(-5, -5), sp + new Vector(5, 5)); ctx.DrawLine(pen, sp + new Vector(-5, 5), sp + new Vector(5, -5)); }
            }
        }

        if (ed.Mode is Mode.PickRange or Mode.SetWidth or Mode.PickOther && ed.DraftBase is { } db2 && !IsDrawnLine(ed, db2) && ed.Project.GetEdge(db2) is { } bl3)
        {
            var a = ToScreen(PL.PointAt(bl3, ed.DraftStart));
            var b = ToScreen(PL.PointAt(bl3, ed.DraftEnd));
            Tick(ctx, a); Tick(ctx, b);
            Label(ctx, a + new Vector(8, -24), $"{ed.DraftStart:0.00} m", TextBrush);
            Label(ctx, b + new Vector(8, 6), $"{ed.DraftEnd:0.00} m  ·  tratto {Math.Abs(ed.DraftEnd - ed.DraftStart):0.00} m", TextBrush);
        }

        if (ed.Selected is { Mode: ElementMode.Offset, Error: null } sv && sv.Guide.Count >= 2)
            DrawVariators(ctx, ed, sv);

        if (ed.Selected is { Mode: ElementMode.Offset, Error: null } s2)
        {
            for (int i = 0; i < s2.Profile.Count; i++)
            {
                var sp = ToScreen(ed.CpWorld(s2, s2.Profile[i]));
                bool act = i == ed.DragCp;
                ctx.DrawRectangle(act ? SelBrush : Brushes.White, new Pen(SelBrush, 2), new Rect(sp.X - 5, sp.Y - 5, 10, 10));
                if (act) Label(ctx, sp + new Vector(10, -10), $"{s2.Profile[i].W:0.00} m", SelBrush);
            }
        }

        DrawScaleBar(ctx);
    }

    StreamGeometry ScreenLine(IReadOnlyList<V2> pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(ToScreen(pts[0]), false);
        for (int i = 1; i < pts.Count; i++) c.LineTo(ToScreen(pts[i]));
        c.EndFigure(false);
        return g;
    }

    /// <summary>Linea appena disegnata per il nuovo elemento: si usa per intero, niente tratto da evidenziare.</summary>
    static bool IsDrawnLine(Editor ed, EdgeRef r) => r.Kind == EdgeKind.Road && ed.Project.FindEdge(r.Id) is { Owner: -2 };

    void Tick(DrawingContext ctx, Point p) => ctx.DrawEllipse(Brushes.White, new Pen(Range, 3), p, 5, 5);

    void DrawEdgeDecor(DrawingContext ctx, RoadEdge e)
    {
        double L = PL.Length(e.Points);
        double step = 140 / _s;
        var pen = new Pen(RoadEdge, 1.6);
        for (double s = step / 2; s < L; s += step)
        {
            var p = ToScreen(PL.PointAt(e.Points, s));
            var t = PL.TangentAt(e.Points, s);
            var d = ScreenDir(t); var n = new Vector(-d.Y, d.X);
            ctx.DrawLine(pen, p, p - d * 7 + n * 4);
            ctx.DrawLine(pen, p, p - d * 7 - n * 4);
        }
        Label(ctx, ToScreen(e.Points[0]) + new Vector(6, -22), e.Name, RoadEdge, bold: true);
    }

    void DrawElementLabel(DrawingContext ctx, Element e)
    {
        double size = e.Mode == ElementMode.Polygon ? Math.Sqrt(e.Metrics.Area) : e.Metrics.WMean;
        double sizePx = size * _s;
        if (sizePx < 14 || (e.Mode != ElementMode.Polygon && e.Metrics.Length * _s < 80)) return;
        if (e.LabelCache == null || !ReferenceEquals(e.LabelKey, e.Polygon)) { e.LabelCache = DxfWriter.LabelPoint(e); e.LabelKey = e.Polygon; }
        var p = ToScreen(e.LabelCache.Value);
        var ft = Text(e.Name, 11, TextBrush, true);
        var o = new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2);
        ctx.FillRectangle(LabelBg, new Rect(o.X - 4, o.Y - 1, ft.Width + 8, ft.Height + 2), 3);
        ctx.DrawText(ft, o);
    }

    static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    static readonly IBrush CamFill = new SolidColorBrush(Color.Parse("#FF262B33"));
    static readonly IBrush CamAccent = new SolidColorBrush(Color.Parse("#FF2FCB79"));
    static readonly IBrush CamCone = new SolidColorBrush(Color.Parse("#402FCB79"));
    const double CamR = 12;

    /// <summary>Fotoinserimenti: un occhio nel punto di ripresa e il cono di vista nella direzione della fotocamera.</summary>
    void DrawPhotoIcons(DrawingContext ctx, Editor ed)
    {
        foreach (var r in ed.Project.Photos)
        {
            if (r.Cam is not { } cw || PrintPhotos != null && !PrintPhotos.Contains(r)) continue;
            var c = ToScreen(cw);
            double yaw = r.Yaw * Math.PI / 180, half = Math.Clamp(r.HFov, 10, 150) * Math.PI / 360;
            Vector Dir(double a) => ScreenDir(new V2(Math.Cos(a), Math.Sin(a)));
            // cono di vista
            const double len = 56;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(c, true);
                for (int k = 0; k <= 12; k++) gc.LineTo(c + Dir(yaw - half + 2 * half * k / 12) * len);
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(CamCone, new Pen(CamAccent, 1), g);
            ctx.DrawLine(new Pen(CamAccent, 1.6, new DashStyle(new double[] { 3, 3 }, 0)), c, c + Dir(yaw) * (len + 10));
            // occhio, girato verso dove guarda la fotocamera
            ctx.DrawEllipse(CamFill, new Pen(CamAccent, 2), c, CamR, CamR);
            var d = Dir(yaw); var n = new Vector(-d.Y, d.X);
            var eye = new StreamGeometry();
            using (var ec = eye.Open())
            {
                ec.BeginFigure(c - d * 7.5, true);
                ec.QuadraticBezierTo(c + n * 7, c + d * 7.5);
                ec.QuadraticBezierTo(c - n * 7, c - d * 7.5);
                ec.EndFigure(true);
            }
            ctx.DrawGeometry(null, new Pen(Brushes.White, 1.5), eye);
            ctx.DrawEllipse(CamAccent, null, c + d * 1.2, 2.6, 2.6);
            if (ShowLabels) Label(ctx, c + new Vector(CamR + 4, CamR - 2), r.Name, TextBrush);
        }
    }

    static readonly IBrush SecInk = new SolidColorBrush(Color.Parse("#FF111111"));
    static readonly IBrush SecHalo = new SolidColorBrush(Color.Parse("#CCFFFFFF"));

    /// <summary>Sezioni: linea nera A–B con, ai due capi, una freccia verso cui si guarda la sezione.</summary>
    void DrawSections(DrawingContext ctx, Editor ed)
    {
        foreach (var s in ed.Project.Sections)
        {
            if (PrintSections != null && !PrintSections.Contains(s)) continue;
            bool sel = s == ed.SelectedSection;
            var a = ToScreen(s.A); var b = ToScreen(s.B);
            double w = sel ? 3.2 : 2.2;
            ctx.DrawLine(new Pen(SecHalo, w + 3, lineCap: PenLineCap.Round), a, b);
            ctx.DrawLine(new Pen(SecInk, w, lineCap: PenLineCap.Round), a, b);
            var v = ScreenDir(s.ViewDir);
            foreach (var e in new[] { a, b }) DrawSectionArrow(ctx, e, v, w);
            // nome vicino ad A, dalla parte opposta alla freccia
            var lab = a - v * 16 - new Vector(0, 8);
            Label(ctx, lab, s.Name, TextBrush, sel);
            if (sel)
                foreach (var (p, t) in new[] { (a, "A"), (b, "B") })
                {
                    ctx.DrawEllipse(Brushes.White, new Pen(SecInk, 2), p, 6.5, 6.5);
                    var ft = Text(t, 9.5, SecInk, true);
                    ctx.DrawText(ft, p - new Vector(ft.Width / 2, ft.Height / 2));
                }
        }
        if (ed.Mode == Mode.PickSection)
        {
            var m = ed.Snap?.P ?? ed.Mouse;
            if (ed.SectionA is { } a0)
            {
                var a = ToScreen(a0);
                if (m is { } mm) ctx.DrawLine(new Pen(SecInk, 2, new DashStyle(new double[] { 4, 3 }, 0)), a, ToScreen(mm));
                ctx.DrawEllipse(Brushes.White, new Pen(SecInk, 2), a, 6.5, 6.5);
                var ft = Text("A", 9.5, SecInk, true); ctx.DrawText(ft, a - new Vector(ft.Width / 2, ft.Height / 2));
            }
            if (ed.Snap is { } sn)
            {
                var sp = ToScreen(sn.P); var pen = new Pen(SnapBrush, 2);
                if (sn.Vertex) ctx.DrawRectangle(null, pen, new Rect(sp.X - 6, sp.Y - 6, 12, 12));
                else { ctx.DrawLine(pen, sp + new Vector(-5, -5), sp + new Vector(5, 5)); ctx.DrawLine(pen, sp + new Vector(-5, 5), sp + new Vector(5, -5)); }
            }
        }
    }

    static void DrawSectionArrow(DrawingContext ctx, Point e, Vector v, double w)
    {
        const double len = 22, head = 8;
        var tip = e + v * len; var n = new Vector(-v.Y, v.X);
        ctx.DrawLine(new Pen(SecHalo, w + 3, lineCap: PenLineCap.Round), e, tip);
        ctx.DrawLine(new Pen(SecInk, w, lineCap: PenLineCap.Round), e, tip - v * (head * 0.6));
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(tip, true); c.LineTo(tip - v * head + n * (head * 0.6)); c.LineTo(tip - v * head - n * (head * 0.6)); c.EndFigure(true); }
        ctx.DrawGeometry(SecInk, new Pen(SecHalo, 1), g);
    }

    PhotoInsertRef? HitPhoto(Point sp)
    {
        var ed = _ed; if (ed == null || !ShowPhotoIcons) return null;
        PhotoInsertRef? best = null; double bd = CamR + 3;
        foreach (var r in ed.Project.Photos)
        {
            if (r.Cam is not { } cw) continue;
            var c = ToScreen(cw); double d = Math.Sqrt((c.X - sp.X) * (c.X - sp.X) + (c.Y - sp.Y) * (c.Y - sp.Y));
            if (d < bd) { bd = d; best = r; }
        }
        return best;
    }

    static readonly IBrush VarBrush = new SolidColorBrush(Color.Parse("#FFFF8A00"));
    static readonly IBrush VarFill = new SolidColorBrush(Color.Parse("#40FF8A00"));

    /// <summary>Variatori di sezione dell'elemento selezionato (tratto evidenziato, linee di inizio e fine) e scelta di uno nuovo.</summary>
    void DrawVariators(DrawingContext ctx, Editor ed, Element e)
    {
        var pen = new Pen(VarBrush, 2.4);
        var dash = new Pen(VarBrush, 1.6, new DashStyle(new double[] { 3, 2 }, 0));
        void Cross(double t, Pen p) { var (a, b) = ed.CrossLine(e, t); ctx.DrawLine(p, ToScreen(a), ToScreen(b)); }
        void Band(double t1, double t2)
        {
            double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
            if (hi - lo < 0.01) return;
            var ring = new List<V2>();
            int n = Math.Max(2, (int)Math.Ceiling((hi - lo) / 0.5));
            var far = new List<V2>();
            for (int k = 0; k <= n; k++) { var (a, b) = ed.CrossLine(e, lo + (hi - lo) * k / n); ring.Add(a); far.Add(b); }
            far.Reverse(); ring.AddRange(far);
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(ToScreen(ring[0]), true); foreach (var q in ring.Skip(1)) c.LineTo(ToScreen(q)); c.EndFigure(true); }
            ctx.DrawGeometry(VarFill, null, g);
        }
        for (int i = 0; i < e.Variators.Count; i++)
        {
            var v = e.Variators[i];
            Band(v.T1, v.T2);
            Cross(v.T1, dash); Cross(v.T2, pen);
            var (_, b2) = ed.CrossLine(e, v.T2);
            var (wb, wa) = ed.VariatorWidths(e, i);
            Label(ctx, ToScreen(b2) + new Vector(8, -8), $"V{i + 1}  {wb:0.00} → {wa:0.00} m", VarBrush, i == ed.VarSel);
            if (ed.Mode == Mode.Idle)
                foreach (var end in new[] { false, true })
                {
                    var hp = ToScreen(ed.VarHandle(e, i, end));
                    bool on = i == ed.VarSel;
                    ctx.DrawEllipse(end ? VarBrush : Brushes.White, new Pen(VarBrush, 2), hp, on ? 6.5 : 5.5, on ? 6.5 : 5.5);
                }
        }
        if (ed.Mode == Mode.PickVar && ed.Mouse is { } m && ed.StationOnSelected(m) is { } tm)
        {
            if (ed.VarStart is { } t1)
            {
                Band(t1, tm); Cross(t1, dash);
                Label(ctx, ToScreen(ed.CrossLine(e, tm).b) + new Vector(8, -8), $"fine · tratto {Math.Abs(tm - t1):0.00} m", VarBrush);
            }
            else Label(ctx, ToScreen(ed.CrossLine(e, tm).b) + new Vector(8, -8), $"inizio · {tm:0.00} m", VarBrush);
            Cross(tm, pen);
        }
    }

    void DrawDirection(DrawingContext ctx, Element e)
    {
        var g = e.Guide; double L = PL.Length(g);
        if (L < 1e-6) return;
        var pen = new Pen(SelBrush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        // frecce sulla linea, circa ogni 140 px
        double step = Math.Max(140 / _s, L / 40);
        for (double t = Math.Min(step / 2, L / 2); t < L; t += step)
        {
            var tg = PL.TangentAt(g, t);
            var c = ToScreen(PL.PointAt(g, t));
            var d = ScreenDir(tg); var n = new Vector(-d.Y, d.X);
            ctx.DrawLine(pen, c - d * 7 + n * 5, c);
            ctx.DrawLine(pen, c - d * 7 - n * 5, c);
        }
        Label(ctx, ToScreen(g[0]) + new Vector(8, -22), "inizio", TextBrush);
        Label(ctx, ToScreen(g[^1]) + new Vector(8, 6), "fine", TextBrush);
        // «sinistra» e «destra» a metà, appena fuori dall'elemento
        var tm = PL.TangentAt(g, L / 2); var pm = PL.PointAt(g, L / 2);
        var nl = tm.LeftNormal;
        double wl = e.Centered ? e.WidthAt(L / 2) / 2 : (e.Side > 0 ? e.WidthAt(L / 2) : 0);
        double wr = e.Centered ? e.WidthAt(L / 2) / 2 : (e.Side < 0 ? e.WidthAt(L / 2) : 0);
        foreach (var (txt, side, w) in new[] { ("sinistra", 1.0, wl), ("destra", -1.0, wr) })
        {
            var sp = ToScreen(pm + nl * (side * w));
            var off = ScreenDir(nl) * (side * 26);
            var ft = Text(txt, 11.5, TextBrush, false);
            Label(ctx, sp + off - new Vector(ft.Width / 2, ft.Height / 2), txt, TextBrush);
        }
    }

    static void Label(DrawingContext ctx, Point at, string s, IBrush brush, bool bold = false)
    {
        var ft = Text(s, 11.5, brush, bold);
        ctx.FillRectangle(LabelBg, new Rect(at.X - 3, at.Y - 1, ft.Width + 6, ft.Height + 2), 3);
        ctx.DrawText(ft, at);
    }

    void DrawScaleBar(DrawingContext ctx)
    {
        double target = 120 / _s;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double len = new[] { 1, 2, 5, 10 }.Select(k => k * mag).Last(v => v <= target * 1.0001);
        double px = len * _s;
        var o = new Point(16, Bounds.Height - 22);
        var pen = new Pen(TextBrush, 2);
        ctx.DrawLine(pen, o, o + new Vector(px, 0));
        ctx.DrawLine(pen, o + new Vector(0, -5), o + new Vector(0, 3));
        ctx.DrawLine(pen, o + new Vector(px, -5), o + new Vector(px, 3));
        var ft = Text(len >= 1 ? $"{len:0} m" : $"{len:0.##} m", 11, TextBrush);
        ctx.DrawText(ft, o + new Vector(px + 6, -ft.Height / 2 - 1));
    }

    // ───────────── input ─────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var ed = _ed; if (ed == null) return;
        var pt = e.GetCurrentPoint(this);
        var sp = pt.Position; var w = ToWorld(sp);
        _lastScreen = sp;

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _panLast = sp; e.Pointer.Capture(this); return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (NotePressed(e, sp, w)) return;

        switch (ed.Mode)
        {
            case Mode.Trace:
                if (e.ClickCount >= 2) ed.TraceFinish();
                else ed.TraceClick(w, Tol);
                break;
            case Mode.PickBase:
            case Mode.PickRange:
            case Mode.PickOther:
                ed.ClickPick(w, Tol);
                if (ed.Mode == Mode.PickBase) { _panLast = sp; e.Pointer.Capture(this); }
                break;
            case Mode.SetWidth:
                _panLast = sp; e.Pointer.Capture(this);
                break;
            case Mode.PickVar:
                ed.ClickVar(w);
                break;
            case Mode.PickSection:
                ed.ClickSection(w, Tol);
                break;
            case Mode.EditLine:
            {
                if (ed.HitCurveHandle(w, Tol) is { } hh) { _dragHandle = hh; e.Pointer.Capture(this); return; }
                int kp = ed.HitKeyPoint(w, Tol);
                if (kp >= 0 && e.ClickCount >= 2) { ed.ToggleCurve(kp); return; }
                if (kp >= 0)
                {
                    ed.SelectKeyPoint(kp);
                    _dragKey = kp; e.Pointer.Capture(this);
                    return;
                }
                if (ed.EditExtend != 0) { ed.ExtendClick(w, Tol); return; }
                if (e.ClickCount >= 2 && ed.InsertKeyPoint(w, Tol)) { _dragKey = ed.EditSel; e.Pointer.Capture(this); return; }
                ed.SelectKeyPoint(-1);
                _panLast = sp; e.Pointer.Capture(this);
                break;
            }
            case Mode.Idle:
                if (ed.AssignGroup != 0) { ed.AssignAt(w); return; }   // aggiunta di elementi a un blocco
                if (HitPhoto(sp) is { } ph) { PhotoClicked?.Invoke(ph); return; }
                if (ed.HitSectionEnd(w, Tol) is var se and >= 0) { _dragSecEnd = se; e.Pointer.Capture(this); return; }
                if (ed.HitSection(w, Tol) is { } sec) { ed.SelectSection(sec); return; }
                if (ed.HitVarHandle(w, Tol) is { } vh) { _dragVar = vh; ed.VarSel = vh.i; e.Pointer.Capture(this); ed.NotifyRedraw(); return; }
                int cp = ed.HitCp(w, Tol);
                if (cp >= 0)
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ed.RemoveCp(cp); return; }
                    ed.DragCp = cp; _dragging = true; e.Pointer.Capture(this); ed.NotifyRedraw();
                    return;
                }
                if (e.ClickCount >= 2 && ed.TryAddCp(w, Tol)) { _dragging = true; e.Pointer.Capture(this); return; }
                // Maiusc o Ctrl (⌘) + clic: sugli elementi disegnati si scelgono per i gruppi («Crea gruppo», «Aggiungi a gruppo»),
                // sui poligoni della mappa (aree senza elementi) si scelgono le aree per «Crea elemento»
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                {
                    if (ed.ElementAt(w) != null) ed.ToggleGroupSel(w); else ed.PickAt(w, true);
                    return;
                }
                if (e.ClickCount >= 2)
                {
                    // doppio clic su un elemento: modifica della sua linea o del suo contorno; altrove: scelta di un'area
                    if (ed.ElementAt(w) is { } hit) { ed.Select(hit); if (ed.EditableLine() != null) ed.StartEditLine(); return; }
                    ed.PickAt(w, false); return;
                }
                var before = ed.Selected;
                ed.SelectAt(w);
                if (ed.Selected == null) { _panLast = sp; e.Pointer.Capture(this); }
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var ed = _ed; if (ed == null) return;
        var sp = e.GetPosition(this);
        var w = ToWorld(sp);
        ed.Mouse = w;
        PointerWorldMoved?.Invoke(w);

        if (_stroke != null)
        {
            if (V2.Dist(_stroke[^1], w) * _s > 2) { _stroke.Add(w); InvalidateVisual(); }
            return;
        }
        if (_panLast is { } last)
        {
            var d = sp - last;
            double rx = d.X / _s, ry = -d.Y / _s, c = Cos, sn = Sin;
            _cx -= rx * c + ry * sn; _cy -= -rx * sn + ry * c;
            _panLast = sp;
            InvalidateVisual();
            return;
        }
        if (_dragging) { ed.MoveCp(w); return; }
        if (_dragVar is { } dv) { ed.MoveVarHandle(dv.i, dv.end, w, false); InvalidateVisual(); return; }
        if (_dragSecEnd >= 0) { ed.MoveSectionEnd(_dragSecEnd, w, Tol, false); InvalidateVisual(); return; }
        if (_dragKey >= 0) { _keyMoved = true; ed.MoveKeyPoint(_dragKey, w, Tol, false); InvalidateVisual(); return; }
        if (_dragHandle is { } dh) { ed.MoveCurveHandle(dh.i, dh.sign, w, false); InvalidateVisual(); return; }

        switch (ed.Mode)
        {
            case Mode.Trace:
            case Mode.PickSection:
                ed.Snap = ed.FindSnap(w, Tol);
                break;
            case Mode.EditLine:
                ed.Snap = ed.EditExtend != 0 ? ed.FindSnap(w, Tol) : null;
                Cursor = ed.HitKeyPoint(w, Tol) >= 0 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
                break;
            case Mode.PickBase:
            case Mode.PickOther:
                ed.HoverEdge = ed.NearestEdge(w, Tol, ed.Mode == Mode.PickOther ? ed.DraftBase : null)?.r;
                break;
            case Mode.PickRange:
                if (!ed.RangeFixed) ed.HoverRange(w);
                else ed.HoverEdge = ed.NearestEdge(w, Tol)?.r;
                break;
            case Mode.Idle:
                Cursor = HitPhoto(sp) != null || ed.HitSection(w, Tol) != null ? new Cursor(StandardCursorType.Hand) : ed.HitSectionEnd(w, Tol) >= 0 || ed.HitVarHandle(w, Tol) != null || ed.HitCp(w, Tol) >= 0 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panLast = null;
        if (_stroke != null && _ed != null)
        {
            var st = _stroke; _stroke = null;
            _ed.Project.Annotations.Add(new Annotation { Pts = PL.Simplify(st, 0.5 / _s), Color = NoteColor, Size = PenSize });
            _ed.NotifyAll();
        }
        if (_dragging && _ed != null) { _ed.DragCp = -1; _ed.NotifyAll(); }
        _dragging = false;
        if (_dragVar is { } dv && _ed != null) _ed.MoveVarHandle(dv.i, dv.end, ToWorld(e.GetPosition(this)), true);
        _dragVar = null;
        if (_dragSecEnd >= 0 && _ed != null) _ed.MoveSectionEnd(_dragSecEnd, ToWorld(e.GetPosition(this)), Tol, true);
        _dragSecEnd = -1;
        if (_dragKey >= 0 && _ed != null && _keyMoved) _ed.MoveKeyPoint(_dragKey, ToWorld(e.GetPosition(this)), Tol, true);
        _dragKey = -1; _keyMoved = false;
        if (_dragHandle is { } dh && _ed != null) _ed.MoveCurveHandle(dh.i, dh.sign, ToWorld(e.GetPosition(this)), true);
        _dragHandle = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_ed != null) { _ed.Mouse = null; _ed.Snap = null; }
        PointerWorldMoved?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var sp = e.GetPosition(this);
        var before = ToWorld(sp);
        _s = Math.Clamp(_s * Math.Pow(1.2, e.Delta.Y), 0.01, 2000);
        var after = ToWorld(sp);
        _cx += before.X - after.X; _cy += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }
}
