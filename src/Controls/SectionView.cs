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
    /// <summary>Colori chiari (sfondo bianco) per l'immagine esportata.</summary>
    public bool Light;
    static IBrush C(string s) => new SolidColorBrush(Color.Parse(s));
    IBrush Bg => Light ? Brushes.White : C("#FF1E232A");
    IBrush Ground => Light ? C("#FF9A8F84") : C("#FF5A5048");
    IBrush Building => Light ? C("#FFD5D8DC") : C("#FF8E949C");
    IBrush BuildingHatch => Light ? C("#FFB4B9C0") : C("#FF6B7179");
    IBrush Dim => Light ? C("#FF2A2F36") : C("#FFC9D1DA");
    IBrush Muted => Light ? C("#FF6B7380") : C("#FF8C95A1");
    IBrush TextStrong => Light ? Brushes.Black : Brushes.White;
    static readonly IBrush Badge = new SolidColorBrush(Color.Parse("#FF2FCB79"));
    IBrush Figure => Light ? C("#E0363B44") : C("#E6D7DDE4");
    IBrush FigureDark => Light ? C("#FF1E2228") : C("#FF5C6470");

    /// <summary>Quota del piano dell'elemento sopra la strada (m).</summary>
    public static double Raise(Element e) => e.HasHeight ? e.Height : 0;

    /// <summary>Ingrandimento di testi, numeri e quote (per l'immagine esportata, più grande dello schermo).</summary>
    public double TextScale = 1;

    FormattedText T(string s, double size, IBrush b, bool bold = false) =>
        new(s, It, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size * TextScale, b);

    enum User { None, Pedestrian, Cyclist, Car, Both, Green }

    static User UserOf(ElementType t) => t switch
    {
        ElementType.Marciapiede => User.Pedestrian,
        ElementType.Ciclabile or ElementType.CorsiaCiclabile => User.Cyclist,
        ElementType.Ciclopedonale => User.Both,
        ElementType.Aiuola => User.Green,
        ElementType.Carreggiata or ElementType.Corsia or ElementType.CorsiaDoppia or ElementType.Parcheggio => User.Car,
        _ => User.None
    };

    /// <summary>Disegna le figure sopra il tratto (una per corsia); restituisce la loro altezza in pixel.</summary>
    double DrawUsers(DrawingContext ctx, SectionPiece p, double x0, double x1, double top, double s)
    {
        var e = p.Element!; var u = UserOf(e.Type);
        if (u == User.None || s <= 0) return 0;
        if (u == User.Green) return Planting(ctx, p.Width, x0, x1, top, s);
        if (u == User.Both)
        {
            // ciclopedonale: un pedone e un ciclista affiancati (se non c'è spazio, solo il ciclista)
            double wb = x1 - x0;
            if (wb < 0.62 * s * 0.6) return 0;
            if (wb >= 1.25 * s) { Pedestrian(ctx, x0 + wb * 0.27, top, s); Cyclist(ctx, x0 + wb * 0.73, top, s); }
            else Cyclist(ctx, (x0 + x1) / 2, top, s);
            return 1.78 * s;
        }
        double figW = u switch { User.Car => 1.8, User.Cyclist => 0.62, _ => 0.5 };
        double figH = u switch { User.Car => 1.45, _ => 1.78 };
        int lanes = e.EffectiveDividers + 1;
        double laneW = e.Metrics.WMean > 0 ? e.Metrics.WMean / lanes : p.Width;
        int k = Math.Clamp((int)Math.Round(p.Width / Math.Max(0.1, laneW)), 1, lanes);
        double w = x1 - x0;
        if (w < figW * s * 0.6) return 0;                  // troppo stretto: niente figure
        k = Math.Max(1, Math.Min(k, (int)Math.Floor(w / (figW * s * 1.05))));   // le figure non si sovrappongono
        for (int i = 0; i < k; i++)
        {
            double cx = x0 + w * (i + 0.5) / k;
            switch (u)
            {
                case User.Pedestrian: Pedestrian(ctx, cx, top, s); break;
                case User.Cyclist: Cyclist(ctx, cx, top, s); break;
                case User.Car: Car(ctx, cx, top, s); break;
            }
        }
        return figH * s;
    }

    // figure viste di fronte: (cx, base) = centro in basso, s = pixel per metro
    // colori delle figure (uguali su fondo scuro e chiaro)
    static readonly IBrush Skin = C("#FFE3B48F"), Hair = C("#FF3B2A20"), Shirt = C("#FF4F7CB8"), Trousers = C("#FF2F3642"),
        Shoes = C("#FF1A1D22"), Jersey = C("#FFF0A53A"), Helmet = C("#FFE2533F"), Tyre = C("#FF22262C"), Metal = C("#FF9AA3AE");

    /// <summary>Tracciato in metri attorno a (cx, b): x verso destra, y verso l'alto.</summary>
    static StreamGeometry Shape(double cx, double b, double s, params (double x, double y)[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx + pts[0].x * s, b - pts[0].y * s), true);
            foreach (var q in pts.Skip(1)) c.LineTo(new Point(cx + q.x * s, b - q.y * s));
            c.EndFigure(true);
        }
        return g;
    }

    static void Limb(DrawingContext ctx, IBrush br, double w, double cx, double b, double s, params (double x, double y)[] pts)
    {
        var pen = new Pen(br, Math.Max(1, w * s), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (int i = 0; i + 1 < pts.Length; i++)
            ctx.DrawLine(pen, new Point(cx + pts[i].x * s, b - pts[i].y * s), new Point(cx + pts[i + 1].x * s, b - pts[i + 1].y * s));
    }

    static void Head(DrawingContext ctx, double cx, double b, double s, double y, bool hair)
    {
        ctx.DrawRectangle(Skin, null, new Rect(cx - 0.035 * s, b - (y - 0.06) * s, 0.07 * s, 0.07 * s));          // collo
        if (hair) ctx.DrawEllipse(Hair, null, new Point(cx, b - (y + 0.035) * s), 0.1 * s, 0.085 * s);
        ctx.DrawEllipse(Skin, null, new Point(cx, b - y * s), 0.088 * s, 0.105 * s);
    }

    void Pedestrian(DrawingContext ctx, double cx, double b, double s)
    {
        // scarpe e gambe (pantaloni)
        ctx.DrawEllipse(Shoes, null, new Point(cx - 0.09 * s, b - 0.03 * s), 0.07 * s, 0.035 * s);
        ctx.DrawEllipse(Shoes, null, new Point(cx + 0.09 * s, b - 0.03 * s), 0.07 * s, 0.035 * s);
        ctx.DrawGeometry(Trousers, null, Shape(cx, b, s, (-0.17, 0.96), (-0.15, 0.05), (-0.03, 0.05), (0, 0.78), (0.03, 0.05), (0.15, 0.05), (0.17, 0.96)));
        // braccia (maniche e mani) dietro al busto
        Limb(ctx, Shirt, 0.085, cx, b, s, (-0.2, 1.38), (-0.24, 1.0));
        Limb(ctx, Shirt, 0.085, cx, b, s, (0.2, 1.38), (0.24, 1.0));
        ctx.DrawEllipse(Skin, null, new Point(cx - 0.245 * s, b - 0.95 * s), 0.042 * s, 0.05 * s);
        ctx.DrawEllipse(Skin, null, new Point(cx + 0.245 * s, b - 0.95 * s), 0.042 * s, 0.05 * s);
        // busto con le spalle arrotondate
        ctx.DrawGeometry(Shirt, null, Shape(cx, b, s, (-0.17, 0.92), (-0.2, 1.36), (-0.17, 1.43), (-0.1, 1.47), (0.1, 1.47), (0.17, 1.43), (0.2, 1.36), (0.17, 0.92)));
        Head(ctx, cx, b, s, 1.6, true);
    }

    void Cyclist(DrawingContext ctx, double cx, double b, double s)
    {
        // ruota vista di fronte (copertone sottile), forcella e manubrio
        ctx.FillRectangle(Tyre, new Rect(cx - 0.028 * s, b - 0.68 * s, 0.056 * s, 0.68 * s), (float)(0.028 * s));
        Limb(ctx, Metal, 0.018, cx, b, s, (-0.045, 0.34), (-0.03, 0.86), (0, 0.96));
        Limb(ctx, Metal, 0.018, cx, b, s, (0.045, 0.34), (0.03, 0.86), (0, 0.96));
        Limb(ctx, Metal, 0.03, cx, b, s, (-0.29, 1.0), (0.29, 1.0));
        Limb(ctx, Tyre, 0.04, cx, b, s, (-0.31, 1.0), (-0.25, 1.0));
        Limb(ctx, Tyre, 0.04, cx, b, s, (0.25, 1.0), (0.31, 1.0));
        // gambe sui pedali (una su, una giù) e scarpe
        Limb(ctx, Trousers, 0.1, cx, b, s, (-0.1, 0.98), (-0.17, 0.62), (-0.14, 0.3));
        Limb(ctx, Trousers, 0.1, cx, b, s, (0.1, 0.98), (0.18, 0.74), (0.14, 0.47));
        ctx.DrawEllipse(Shoes, null, new Point(cx - 0.14 * s, b - 0.26 * s), 0.06 * s, 0.03 * s);
        ctx.DrawEllipse(Shoes, null, new Point(cx + 0.14 * s, b - 0.43 * s), 0.06 * s, 0.03 * s);
        // braccia tese verso il manubrio
        Limb(ctx, Jersey, 0.08, cx, b, s, (-0.17, 1.36), (-0.26, 1.06));
        Limb(ctx, Jersey, 0.08, cx, b, s, (0.17, 1.36), (0.26, 1.06));
        ctx.DrawEllipse(Skin, null, new Point(cx - 0.27 * s, b - 1.02 * s), 0.04 * s, 0.04 * s);
        ctx.DrawEllipse(Skin, null, new Point(cx + 0.27 * s, b - 1.02 * s), 0.04 * s, 0.04 * s);
        // busto (maglia) e testa con il casco
        ctx.DrawGeometry(Jersey, null, Shape(cx, b, s, (-0.15, 0.97), (-0.19, 1.34), (-0.15, 1.41), (-0.08, 1.44), (0.08, 1.44), (0.15, 1.41), (0.19, 1.34), (0.15, 0.97)));
        Head(ctx, cx, b, s, 1.56, false);
        var helmet = new StreamGeometry();
        using (var g = helmet.Open())
        {
            g.BeginFigure(new Point(cx - 0.115 * s, b - 1.6 * s), true);
            g.ArcTo(new Point(cx + 0.115 * s, b - 1.6 * s), new Size(0.115 * s, 0.1 * s), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Helmet, null, helmet);
        ctx.FillRectangle(Tyre, new Rect(cx - 0.07 * s, b - 1.585 * s, 0.14 * s, 0.03 * s), (float)(0.012 * s));   // occhiali
    }

    static readonly IBrush Leaf = C("#FF5E9E45"), LeafDark = C("#FF3F7A33"), LeafLight = C("#FF86BE5F"), Bark = C("#FF6B4A33");

    /// <summary>Verde dell'aiuola secondo la larghezza: tappezzante fino a 40 cm, arbusto fino a 1,40 m, albero oltre.</summary>
    double Planting(DrawingContext ctx, double width, double x0, double x1, double b, double s)
    {
        double w = x1 - x0, cx = (x0 + x1) / 2;
        if (w < 3) return 0;
        if (width <= 0.40)
        {
            // tappezzante: cuscinetti bassi lungo tutta la larghezza
            int n = Math.Max(1, (int)Math.Round(width / 0.1));
            double r = w / n / 2;
            for (int i = 0; i < n; i++)
                ctx.DrawEllipse(i % 2 == 0 ? Leaf : LeafLight, null, new Point(x0 + r * (2 * i + 1), b - Math.Min(0.06 * s, r * 0.7)), r * 1.15, Math.Min(0.07 * s, r * 0.9));
            return 0.12 * s;
        }
        if (width <= 1.40)
        {
            // arbusto: chioma tonda bassa (circa 1 m)
            double bw = Math.Min(width * 0.9, 1.2) * s / 2, h = 0.95 * s;
            ctx.DrawEllipse(LeafDark, null, new Point(cx - bw * 0.45, b - h * 0.42), bw * 0.62, h * 0.42);
            ctx.DrawEllipse(LeafDark, null, new Point(cx + bw * 0.45, b - h * 0.42), bw * 0.62, h * 0.42);
            ctx.DrawEllipse(Leaf, null, new Point(cx, b - h * 0.6), bw * 0.7, h * 0.4);
            ctx.DrawEllipse(LeafLight, null, new Point(cx - bw * 0.2, b - h * 0.72), bw * 0.32, h * 0.2);
            return h;
        }
        // albero: tronco e chioma (alto fino a 6 m, ridotto se non c'è spazio sopra)
        double H = Math.Min(6.0, Math.Max(2.5, (b - 40 * TextScale) / s));
        double cr = Math.Min(1.8, H * 0.32) * s, trunkTop = H * s - cr * 1.75;
        ctx.FillRectangle(Bark, new Rect(cx - 0.12 * s, b - trunkTop - cr * 0.4, 0.24 * s, trunkTop + cr * 0.4));
        ctx.DrawEllipse(LeafDark, null, new Point(cx - cr * 0.5, b - trunkTop - cr * 0.55), cr * 0.75, cr * 0.65);
        ctx.DrawEllipse(LeafDark, null, new Point(cx + cr * 0.5, b - trunkTop - cr * 0.55), cr * 0.75, cr * 0.65);
        ctx.DrawEllipse(Leaf, null, new Point(cx, b - trunkTop - cr * 0.95), cr * 0.9, cr * 0.75);
        ctx.DrawEllipse(LeafLight, null, new Point(cx - cr * 0.3, b - trunkTop - cr * 1.2), cr * 0.38, cr * 0.3);
        return H * s;
    }

    void Car(DrawingContext ctx, double cx, double b, double s)
    {
        Rect R(double xa, double y0, double xb, double y1) => new(cx + xa * s, b - y1 * s, (xb - xa) * s, (y1 - y0) * s);
        // ruote, carrozzeria, abitacolo con parabrezza, fari
        ctx.FillRectangle(FigureDark, R(-0.88, 0, -0.62, 0.36), (float)(0.04 * s));
        ctx.FillRectangle(FigureDark, R(0.62, 0, 0.88, 0.36), (float)(0.04 * s));
        ctx.FillRectangle(Figure, R(-0.9, 0.22, 0.9, 0.92), (float)(0.12 * s));
        var cab = new StreamGeometry();
        using (var g = cab.Open())
        {
            g.BeginFigure(new Point(cx - 0.78 * s, b - 0.9 * s), true);
            g.LineTo(new Point(cx - 0.6 * s, b - 1.45 * s));
            g.LineTo(new Point(cx + 0.6 * s, b - 1.45 * s));
            g.LineTo(new Point(cx + 0.78 * s, b - 0.9 * s));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Figure, null, cab);
        var glass = new StreamGeometry();
        using (var g = glass.Open())
        {
            g.BeginFigure(new Point(cx - 0.66 * s, b - 0.96 * s), true);
            g.LineTo(new Point(cx - 0.53 * s, b - 1.37 * s));
            g.LineTo(new Point(cx + 0.53 * s, b - 1.37 * s));
            g.LineTo(new Point(cx + 0.66 * s, b - 0.96 * s));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(FigureDark, null, glass);
        var lamp = Light ? C("#FFF2C94C") : C("#FFFFE08A");
        ctx.DrawEllipse(lamp, null, new Point(cx - 0.6 * s, b - 0.7 * s), 0.13 * s, 0.07 * s);
        ctx.DrawEllipse(lamp, null, new Point(cx + 0.6 * s, b - 0.7 * s), 0.13 * s, 0.07 * s);
        ctx.FillRectangle(FigureDark, R(-0.35, 0.42, 0.35, 0.56), (float)(0.03 * s));
    }

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
        double yRoad = H * (Light ? 0.5 : 0.42);
        double X(double t) => left + (t - x0) * sc;

        // figure (pedoni, ciclisti, auto): stessa scala in orizzontale e in verticale, così le proporzioni sono vere
        double fs = Math.Min(sc, (yRoad - 34 * TextScale) / 1.9);

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
                // chi usa l'elemento: pedoni sul marciapiede, ciclisti sulla ciclabile, auto sulle corsie (una per corsia)
                double figH = DrawUsers(ctx, p, X(p.From), X(p.To), top, fs);
                // numero dell'elemento sopra la fascia (e sopra le figure)
                var c = new Point(rect.Center.X, Math.Max(12 * TextScale, top - 14 * TextScale - figH));
                ctx.DrawEllipse(Badge, null, c, 9 * TextScale, 9 * TextScale);
                var ft = T(n.ToString(), 10, Brushes.Black, true);
                ctx.DrawText(ft, c - new Vector(ft.Width / 2, ft.Height / 2));
            }

            // quote: una per elemento, poi il totale degli elementi e la distanza tra gli edifici
            double ks = TextScale;
            double yDim = yRoad + 0.30 * vsc + 22 * ks;
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
                var ft = T(text, bold ? 11.5 : 10.5, bold ? TextStrong : Dim, bold);
                if (ft.Width + 4 <= pb.X - pa.X || bold)
                    ctx.DrawText(ft, new Point((pa.X + pb.X) / 2 - ft.Width / 2, y - ft.Height - 2));
            }
            foreach (var p in r.Pieces) DimLine(p.From, p.To, yDim, p.Width.ToString("0.00", It), false);
            if (r.Pieces.Count > 0)
                DimLine(r.Pieces.Min(p => p.From), r.Pieces.Max(p => p.To), yDim + 30 * ks, $"Totale elementi {r.ElementsWidth.ToString("0.00", It)} m", true);
            if (r.BetweenBuildings is { } bb && r.Pieces.Count > 0)
            {
                double f = r.Pieces.Min(p => p.From), t = r.Pieces.Max(p => p.To);
                var lb = r.Buildings.Where(x => x.To <= f + 0.3).OrderByDescending(x => x.To).First().To;
                DimLine(lb, lb + bb, yDim + 58 * ks, $"Tra gli edifici {bb.ToString("0.00", It)} m", false);
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
