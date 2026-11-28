using CicloDraft.Geometry;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using CicloDraft.IO;

namespace CicloDraft.Controls;

public enum PhotoMode { Points, Verify, Insert }
public enum BrushMode { None, Paint, Erase }

/// <summary>Vista della foto: punti di controllo, verifica con il disegno della mappa, risultato, pennello della maschera.</summary>
public class PhotoCanvas : Control
{
    public WriteableBitmap? Photo, Result, MaskBmp;
    // trasformazione libera (passo 3): foto di base, progetto da solo e ciò che sta davanti (la foto dove c'è la maschera)
    public WriteableBitmap? BaseBmp, LayerBmp, FrontBmp;
    // Deformazione: una parte già fissata (WarpBase) e il poligono in corso: vertici posati (PolySrc) e spostati (PolyDst)
    public double[]? WarpBase, PolySrc, PolyDst;
    /// <summary>Modalità «Deforma» attiva: si vedono e si spostano i vertici del poligono.</summary>
    public bool DeformMode;
    /// <summary>Si stanno posando i vertici di un nuovo poligono.</summary>
    public bool Placing;
    public readonly List<Point> Placed = new();
    public event Action? WarpChanged, PolygonCreated, PlacingProgress;
    int _dragVertex = -1; bool _dragPoly; Point _dragStart; double[]? _polyStart;
    bool FreeTransform => Mode == PhotoMode.Insert && LayerBmp != null && BaseBmp != null && !ShowOriginal;
    bool Editing => FreeTransform && DeformMode;

    /// <summary>Omografia complessiva: pixel del progetto → pixel della foto.</summary>
    public double[] WarpMatrix
    {
        get
        {
            var m = WarpBase ?? Homography.IdentityMatrix();
            if (PolySrc != null && PolyDst != null && Homography.QuadToQuad(PolySrc, PolyDst) is { } hp) m = Homography.Mul(hp, m);
            return m;
        }
    }
    public bool HasWarp
    {
        get
        {
            var m = WarpMatrix; var id = Homography.IdentityMatrix();
            return m.Zip(id).Any(p => Math.Abs(p.First - p.Second) > 1e-6);
        }
    }

    /// <summary>Inizia un nuovo poligono: la deformazione fatta fin qui resta fissata.</summary>
    public void StartPlacing()
    {
        WarpBase = HasWarp ? WarpMatrix : null;
        PolySrc = PolyDst = null;
        Placed.Clear(); Placing = true;
        InvalidateVisual();
    }

    public void CancelPlacing() { Placing = false; Placed.Clear(); InvalidateVisual(); }

    public void ClearWarp() { WarpBase = PolySrc = PolyDst = null; Placing = false; Placed.Clear(); InvalidateVisual(); }
    public Rgba? Mask;
    public PhotoMode Mode = PhotoMode.Points;
    /// <summary>Mostra la foto originale al posto del risultato (confronto prima/dopo).</summary>
    public bool ShowOriginal;
    public BrushMode Brush = BrushMode.None;
    public double BrushRadius = 25;
    /// <summary>Punti: numero, posizione sulla foto, posizione prevista dalla fotocamera (se calcolata), selezionato.</summary>
    public List<(int n, Point p, Point? predicted, bool selected)> Marks = new();
    public Point? Pending;
    /// <summary>Segmenti di verifica (in pixel della foto) con il loro colore.</summary>
    public List<(Point a, Point b, IBrush color, double width)> Wire = new();

    public event Action<Point>? PhotoClicked;
    public event Action? MaskChanged;

    double _s = 1, _ox, _oy;   // scala e spostamento (pixel schermo per pixel foto)
    Point? _panLast;
    bool _painting;
    Point _mouse;
    bool _fitted;

    static readonly CultureInfo Ci = CultureInfo.GetCultureInfo("it-IT");
    static IBrush B(string hex, byte a = 255) { var c = Color.Parse(hex); return new ImmutableSolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); }
    static readonly IBrush Bg = B("#2A2F36"), Mk = B("#D81B60"), MkSel = B("#1565C0"), Pred = B("#FFD54F"), LabelBg = B("#1E2833", 200);

    public PhotoCanvas() { ClipToBounds = true; Focusable = true; }

    public static WriteableBitmap ToBitmap(Rgba img)
    {
        var wb = new WriteableBitmap(new PixelSize(img.W, img.H), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        for (int y = 0; y < img.H; y++)
            System.Runtime.InteropServices.Marshal.Copy(img.Px, y * img.W * 4, fb.Address + y * fb.RowBytes, img.W * 4);
        return wb;
    }

    public void RefreshMaskBitmap()
    {
        if (Mask == null) { MaskBmp = null; return; }
        var tint = new Rgba(Mask.W, Mask.H);
        for (int i = 0; i < Mask.W * Mask.H; i++)
        {
            byte a = Mask.Px[4 * i + 3];
            tint.Px[4 * i] = 216; tint.Px[4 * i + 1] = 27; tint.Px[4 * i + 2] = 96; tint.Px[4 * i + 3] = (byte)(a * 0.45);
        }
        MaskBmp = ToBitmap(tint);
    }

    public void Fit()
    {
        if (Photo == null || Bounds.Width < 10) return;
        // la foto occupa tutta la larghezza disponibile; se è più alta dello spazio, si centra e si sposta con il tasto destro
        _s = Bounds.Width / Photo.PixelSize.Width;
        _ox = 0; _oy = (Bounds.Height - Photo.PixelSize.Height * _s) / 2;
        _fitted = true;
        InvalidateVisual();
    }

    Point ToScreen(Point p) => new(p.X * _s + _ox, p.Y * _s + _oy);
    Point ToPhoto(Point s) => new((s.X - _ox) / _s, (s.Y - _oy) / _s);

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); if (!_fitted) Fit(); }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        if (Photo == null)
        {
            var ft = new FormattedText("Apri una foto della via (scattata a altezza d'uomo, senza grandangoli estremi).", Ci, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.White);
            ctx.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, Bounds.Height / 2));
            return;
        }
        var dest = new Rect(_ox, _oy, Photo.PixelSize.Width * _s, Photo.PixelSize.Height * _s);
        if (FreeTransform)
        {
            // foto, progetto deformato con l'omografia degli angoli, poi ciò che sta davanti (auto, persone)
            int W = Photo.PixelSize.Width, H = Photo.PixelSize.Height;
            ctx.DrawImage(BaseBmp!, new Rect(0, 0, W, H), dest);
            var hm = WarpMatrix;
            {
                var hM = new Matrix(hm[0], hm[3], hm[6], hm[1], hm[4], hm[7], hm[2], hm[5], hm[8]);
                var toScreen = new Matrix(_s, 0, 0, _s, _ox, _oy);
                using (ctx.PushClip(dest))
                using (ctx.PushTransform(hM * toScreen))
                    ctx.DrawImage(LayerBmp!, new Rect(0, 0, W, H), new Rect(0, 0, W, H));
            }
            if (FrontBmp != null) ctx.DrawImage(FrontBmp, new Rect(0, 0, W, H), dest);
            if (DeformMode) DrawQuadHandles(ctx);
        }
        else
        {
            var img = Mode == PhotoMode.Insert && Result != null && !ShowOriginal ? Result : Photo;
            ctx.DrawImage(img, new Rect(0, 0, img.PixelSize.Width, img.PixelSize.Height), dest);
        }
        if (MaskBmp != null && Brush != BrushMode.None) ctx.DrawImage(MaskBmp, new Rect(0, 0, MaskBmp.PixelSize.Width, MaskBmp.PixelSize.Height), dest);

        if (Mode == PhotoMode.Verify)
            foreach (var (a, b, col, w) in Wire) ctx.DrawLine(new Pen(col, w), ToScreen(a), ToScreen(b));

        if (Mode != PhotoMode.Insert)
            foreach (var (n, p, pred, sel) in Marks)
            {
                var sp = ToScreen(p);
                if (pred is { } q)
                {
                    var sq = ToScreen(q);
                    ctx.DrawLine(new Pen(Pred, 2), sp, sq);
                    ctx.DrawEllipse(null, new Pen(Pred, 2), sq, 4, 4);
                }
                var br = sel ? MkSel : Mk;
                ctx.DrawLine(new Pen(Brushes.White, 3), sp - new Vector(9, 0), sp + new Vector(9, 0));
                ctx.DrawLine(new Pen(Brushes.White, 3), sp - new Vector(0, 9), sp + new Vector(0, 9));
                ctx.DrawLine(new Pen(br, 1.5), sp - new Vector(8, 0), sp + new Vector(8, 0));
                ctx.DrawLine(new Pen(br, 1.5), sp - new Vector(0, 8), sp + new Vector(0, 8));
                var ft = new FormattedText(n.ToString(), Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 11, Brushes.White);
                ctx.DrawEllipse(br, new Pen(Brushes.White, 1.2), sp + new Vector(13, -13), 9, 9);
                ctx.DrawText(ft, sp + new Vector(13 - ft.Width / 2, -13 - ft.Height / 2));
            }
        if (Pending is { } pd)
        {
            var sp = ToScreen(pd);
            ctx.DrawEllipse(null, new Pen(Brushes.White, 3), sp, 10, 10);
            ctx.DrawEllipse(null, new Pen(B("#E7A500"), 2), sp, 10, 10);
        }
        if (Brush != BrushMode.None)
        {
            ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), _mouse, BrushRadius * _s, BrushRadius * _s);
            ctx.DrawEllipse(null, new Pen(Brushes.Black, 0.8), _mouse, BrushRadius * _s + 1.5, BrushRadius * _s + 1.5);
        }
    }

    static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#FF2FCB79"));

    static readonly IBrush PolyLine = new SolidColorBrush(Color.Parse("#FF2FCB79"));

    void DrawQuadHandles(DrawingContext ctx)
    {
        Point S(double x, double y) => ToScreen(new Point(x, y));
        var pen = new Pen(PolyLine, 2);
        if (Placing)
        {
            // vertici posati finora, la linea verso il mouse e il numero del prossimo
            for (int i = 0; i < Placed.Count; i++)
            {
                var a = ToScreen(Placed[i]);
                if (i > 0) ctx.DrawLine(pen, ToScreen(Placed[i - 1]), a);
                DrawVertex(ctx, a, i + 1, false);
            }
            if (Placed.Count > 0) ctx.DrawLine(new Pen(PolyLine, 1.5, new DashStyle(new double[] { 4, 3 }, 0)), ToScreen(Placed[^1]), _mouse);
            if (Placed.Count == 3) ctx.DrawLine(new Pen(PolyLine, 1.2, new DashStyle(new double[] { 2, 3 }, 0)), _mouse, ToScreen(Placed[0]));
            return;
        }
        if (PolyDst is not { } q) return;
        var pts = Enumerable.Range(0, 4).Select(i => S(q[2 * i], q[2 * i + 1])).ToArray();
        for (int i = 0; i < 4; i++) ctx.DrawLine(pen, pts[i], pts[(i + 1) % 4]);
        for (int i = 0; i < 4; i++) DrawVertex(ctx, pts[i], i + 1, i == _dragVertex);
    }

    static void DrawVertex(DrawingContext ctx, Point p, int n, bool active)
    {
        double r = active ? 8.5 : 7;
        ctx.DrawEllipse(active ? Brushes.White : HandleFill, new Pen(Brushes.White, 2), p, r, r);
        var ft = new FormattedText(n.ToString(), Ci, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 10, active ? Brushes.Black : Brushes.White);
        ctx.DrawText(ft, p - new Vector(ft.Width / 2, ft.Height / 2));
    }

    int HitVertex(Point sp)
    {
        if (PolyDst is not { } q) return -1;
        int best = -1; double bd = 14;
        for (int i = 0; i < 4; i++)
        {
            var c = ToScreen(new Point(q[2 * i], q[2 * i + 1]));
            double d = Math.Sqrt((c.X - sp.X) * (c.X - sp.X) + (c.Y - sp.Y) * (c.Y - sp.Y));
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    bool InsidePoly(Point photoPt)
    {
        if (PolyDst is not { } q) return false;
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            double xi = q[2 * i], yi = q[2 * i + 1], xj = q[2 * j], yj = q[2 * j + 1];
            if ((yi > photoPt.Y) != (yj > photoPt.Y) && photoPt.X < (xj - xi) * (photoPt.Y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    void PaintAt(Point screen)
    {
        if (Mask == null) return;
        var p = ToPhoto(screen);
        int r = (int)Math.Ceiling(BrushRadius);
        byte val = Brush == BrushMode.Paint ? (byte)255 : (byte)0;
        if (MaskBmp == null) RefreshMaskBitmap();
        using var fb = MaskBmp!.Lock();
        var tint = new byte[] { 216, 27, 96, (byte)(val * 0.45) };
        for (int y = (int)p.Y - r; y <= (int)p.Y + r; y++)
            for (int x = (int)p.X - r; x <= (int)p.X + r; x++)
            {
                if (x < 0 || y < 0 || x >= Mask.W || y >= Mask.H) continue;
                double d = Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
                if (d > BrushRadius) continue;
                Mask.Px[(y * Mask.W + x) * 4 + 3] = val;
                System.Runtime.InteropServices.Marshal.Copy(tint, 0, fb.Address + y * fb.RowBytes + x * 4, 4);
            }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (Photo == null) return;
        if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            _panLast = pt.Position; e.Pointer.Capture(this); return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (Brush != BrushMode.None)
        {
            _painting = true; e.Pointer.Capture(this);
            PaintAt(pt.Position); InvalidateVisual();
            return;
        }
        if (Editing)
        {
            // clic fuori dal poligono: se ne inizia uno nuovo (la deformazione fatta resta)
            if (!Placing && HitVertex(pt.Position) < 0 && !InsidePoly(ToPhoto(pt.Position))) StartPlacing();
            if (Placing)
            {
                Placed.Add(ToPhoto(pt.Position));
                if (Placed.Count == 4)
                {
                    var q = Homography.SortAround(Placed.SelectMany(p => new[] { p.X, p.Y }).ToArray());
                    Placing = false; Placed.Clear();
                    if (Homography.IsConvex(q)) { PolySrc = q; PolyDst = (double[])q.Clone(); PolygonCreated?.Invoke(); }
                    else PlacingProgress?.Invoke();
                }
                else PlacingProgress?.Invoke();
                InvalidateVisual();
                return;
            }
            _dragVertex = HitVertex(pt.Position);
            _dragPoly = _dragVertex < 0 && InsidePoly(ToPhoto(pt.Position));
            if (_dragVertex >= 0 || _dragPoly)
            {
                _dragStart = ToPhoto(pt.Position); _polyStart = (double[])PolyDst!.Clone();
                e.Pointer.Capture(this);
            }
            InvalidateVisual();
            return;
        }
        if (FreeTransform) return;   // passo 3 fuori dalla modalità «Deforma»: il clic sinistro non fa nulla
        if (Mode is PhotoMode.Points or PhotoMode.Verify)
        {
            var p = ToPhoto(pt.Position);
            if (p.X >= 0 && p.Y >= 0 && p.X < Photo.PixelSize.Width && p.Y < Photo.PixelSize.Height) PhotoClicked?.Invoke(p);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);
        if (_panLast is { } last)
        {
            var d = _mouse - last; _ox += d.X; _oy += d.Y; _panLast = _mouse;
        }
        else if (_painting) PaintAt(_mouse);
        else if (_polyStart != null && (_dragVertex >= 0 || _dragPoly))
        {
            var p = ToPhoto(_mouse);
            double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
            var nq = (double[])_polyStart.Clone();
            if (_dragPoly) for (int i = 0; i < 4; i++) { nq[2 * i] += dx; nq[2 * i + 1] += dy; }
            else { nq[2 * _dragVertex] += dx; nq[2 * _dragVertex + 1] += dy; }
            if (Homography.IsConvex(nq)) PolyDst = nq;   // un poligono intrecciato non è valido: il vertice si ferma
        }
        else if (Editing)
            Cursor = Placing ? new Cursor(StandardCursorType.Cross) : HitVertex(_mouse) >= 0 ? new Cursor(StandardCursorType.SizeAll) : InsidePoly(ToPhoto(_mouse)) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_painting) { _painting = false; MaskChanged?.Invoke(); }
        if (_polyStart != null && (_dragVertex >= 0 || _dragPoly))
        {
            _polyStart = null; _dragVertex = -1; _dragPoly = false;
            WarpChanged?.Invoke();
            InvalidateVisual();
        }
        _panLast = null; e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var sp = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Brush != BrushMode.None)
        {
            BrushRadius = Math.Clamp(BrushRadius * Math.Pow(1.15, e.Delta.Y), 2, 400);
        }
        else
        {
            var before = ToPhoto(sp);
            _s = Math.Clamp(_s * Math.Pow(1.2, e.Delta.Y), 0.05, 40);
            _ox = sp.X - before.X * _s; _oy = sp.Y - before.Y * _s;
        }
        InvalidateVisual(); e.Handled = true;
    }
}
