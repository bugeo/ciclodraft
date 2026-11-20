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
        _s = Math.Min(Bounds.Width / Photo.PixelSize.Width, Bounds.Height / Photo.PixelSize.Height);
        _ox = (Bounds.Width - Photo.PixelSize.Width * _s) / 2; _oy = (Bounds.Height - Photo.PixelSize.Height * _s) / 2;
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
        var img = Mode == PhotoMode.Insert && Result != null && !ShowOriginal ? Result : Photo;
        var dest = new Rect(_ox, _oy, Photo.PixelSize.Width * _s, Photo.PixelSize.Height * _s);
        ctx.DrawImage(img, new Rect(0, 0, img.PixelSize.Width, img.PixelSize.Height), dest);
        if (MaskBmp != null && (Brush != BrushMode.None || Mode == PhotoMode.Points)) ctx.DrawImage(MaskBmp, new Rect(0, 0, MaskBmp.PixelSize.Width, MaskBmp.PixelSize.Height), dest);

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
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_painting) { _painting = false; MaskChanged?.Invoke(); }
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
