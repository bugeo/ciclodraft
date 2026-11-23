using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CicloDraft.Model;

namespace CicloDraft.Controls;

/// <summary>
/// Disegno della sezione stradale: gli elementi tagliati dalla linea A–B come fasce colorate (marciapiedi e aiuole
/// rialzati), gli edifici tagliati ai lati, il numero di ogni elemento e la catena delle quote con le larghezze e il totale.
/// </summary>
public class SectionView : Control
{
    public SectionResult? Result;
    public CrossSection? Section;

    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#FF1E232A"));
    static readonly IBrush Ground = new SolidColorBrush(Color.Parse("#FF5A5048"));
    static readonly IBrush Building = new SolidColorBrush(Color.Parse("#FF8E949C"));
    static readonly IBrush BuildingHatch = new SolidColorBrush(Color.Parse("#FF6B7179"));
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#FFC9D1DA"));
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#FF8C95A1"));
    static readonly IBrush Badge = new SolidColorBrush(Color.Parse("#FF2FCB79"));

    /// <summary>Quota del piano dell'elemento sopra la strada (m).</summary>
    public static double Raise(Element e) => e.HasHeight ? e.Height : 0;

    static FormattedText T(string s, double size, IBrush b, bool bold = false) =>
        new(s, It, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, b);

    public override void Render(DrawingContext ctx)
    {
        var W = Bounds.Width; var H = Bounds.Height;
        ctx.FillRectangle(Bg, new Rect(Bounds.Size), 8);
        if (Result is not { } r || Section is not { } s || r.Length < 0.05) return;

        // area da mostrare: gli elementi tagliati, con un po' di margine e gli edifici più vicini
        double x0, x1;
        if (r.Pieces.Count > 0) { x0 = r.Pieces.Min(p => p.From); x1 = r.Pieces.Max(p => p.To); }
        else { x0 = 0; x1 = r.Length; }
        double pad = Math.Max(1.5, (x1 - x0) * 0.08);
        x0 = Math.Max(0, x0 - pad); x1 = Math.Min(r.Length, x1 + pad);
        if (x1 - x0 < 1) { x0 = 0; x1 = Math.Max(1, r.Length); }

        const double left = 26, right = 26;
        double sc = (W - left - right) / (x1 - x0);          // pixel per metro, in orizzontale
        double vsc = Math.Min(sc * 4, 90);                     // le quote in altezza si esagerano per leggerle
        double yRoad = H * 0.42;
        double X(double t) => left + (t - x0) * sc;

        using (ctx.PushClip(new Rect(Bounds.Size)))
        {
            // terreno
            ctx.FillRectangle(Ground, new Rect(X(x0), yRoad + 0.30 * vsc, X(x1) - X(x0), 4));

            // edifici tagliati: blocchi grigi con tratteggio, troncati in alto
            foreach (var bpc in r.Buildings)
            {
                double a = Math.Max(bpc.From, x0), b = Math.Min(bpc.To, x1);
                if (b - a < 0.01) continue;
                var rect = new Rect(X(a), yRoad - H * 0.32, X(b) - X(a), H * 0.32 + 0.30 * vsc);
                ctx.FillRectangle(Building, rect);
                using (ctx.PushClip(rect))
                    for (double k = rect.X - rect.Height; k < rect.Right; k += 9)
                        ctx.DrawLine(new Pen(BuildingHatch, 1), new Point(k, rect.Bottom), new Point(k + rect.Height, rect.Y));
            }

            // elementi: fasce colorate (spessore 30 cm, rialzate di 15 cm marciapiedi e aiuole)
            int n = 0;
            foreach (var p in r.Pieces)
            {
                n++;
                var (fill, stroke) = MapCanvas.Colors(p.Element!.Type, 255);
                double top = yRoad - Raise(p.Element) * vsc;
                var rect = new Rect(X(p.From), top, Math.Max(1, (p.To - p.From) * sc), yRoad + 0.30 * vsc - top);
                ctx.FillRectangle(fill, rect);
                ctx.DrawRectangle(new Pen(stroke, 1), rect);
                // numero dell'elemento sopra la fascia
                var c = new Point(rect.Center.X, top - 14);
                ctx.DrawEllipse(Badge, null, c, 9, 9);
                var ft = T(n.ToString(), 10, Brushes.Black, true);
                ctx.DrawText(ft, c - new Vector(ft.Width / 2, ft.Height / 2));
            }

            // quote: una per elemento, poi il totale degli elementi e la distanza tra gli edifici
            double yDim = yRoad + 0.30 * vsc + 22;
            var pen = new Pen(Dim, 1);
            void DimLine(double a, double b, double y, string text, bool bold)
            {
                var pa = new Point(X(a), y); var pb = new Point(X(b), y);
                ctx.DrawLine(pen, pa, pb);
                foreach (var q in new[] { pa, pb })
                {
                    ctx.DrawLine(pen, q + new Vector(0, -5), q + new Vector(0, 5));
                    ctx.DrawLine(pen, q + new Vector(-3, 3), q + new Vector(3, -3));
                }
                var ft = T(text, bold ? 11.5 : 10.5, bold ? Brushes.White : Dim, bold);
                if (ft.Width + 4 <= pb.X - pa.X || bold)
                    ctx.DrawText(ft, new Point((pa.X + pb.X) / 2 - ft.Width / 2, y - ft.Height - 2));
            }
            foreach (var p in r.Pieces) DimLine(p.From, p.To, yDim, p.Width.ToString("0.00", It), false);
            if (r.Pieces.Count > 0)
                DimLine(r.Pieces.Min(p => p.From), r.Pieces.Max(p => p.To), yDim + 30, $"Totale elementi {r.ElementsWidth.ToString("0.00", It)} m", true);
            if (r.BetweenBuildings is { } bb && r.Pieces.Count > 0)
            {
                double f = r.Pieces.Min(p => p.From), t = r.Pieces.Max(p => p.To);
                var lb = r.Buildings.Where(x => x.To <= f + 0.3).OrderByDescending(x => x.To).First().To;
                DimLine(lb, lb + bb, yDim + 58, $"Tra gli edifici {bb.ToString("0.00", It)} m", false);
            }

            // capi della sezione (lettere come sulla mappa)
            var (ln, rn) = s.Flip ? ("B", "A") : ("A", "B");
            ctx.DrawText(T(ln, 11, Muted, true), new Point(6, 6));
            var rft = T(rn, 11, Muted, true);
            ctx.DrawText(rft, new Point(W - rft.Width - 6, 6));
            if (r.Pieces.Count == 0)
            {
                var ft = T("La linea della sezione non attraversa nessun elemento disegnato.", 12, Muted);
                ctx.DrawText(ft, new Point(W / 2 - ft.Width / 2, H / 2 - ft.Height / 2));
            }
        }
    }
}
