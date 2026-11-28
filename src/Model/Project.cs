using CicloDraft.Geometry;
using NetTopologySuite.Geometries;

namespace CicloDraft.Model;

/// <summary>Indefinito: poligono appena disegnato, il tipo si sceglie dopo (sempre ultimo nell'elenco).</summary>
public enum ElementType { Marciapiede, Ciclabile, Carreggiata, Corsia, Parcheggio, Aiuola, Separatore, CorsiaCiclabile, CorsiaDoppia, Ciclopedonale, Indefinito }

/// <summary>Offset = larghezza impostata; Fill = riempie lo spazio fino a un secondo bordo.</summary>
/// <summary>Offset/Fill = elementi «a tracciato» (bordo o interasse / riempimento); Polygon = poligono chiuso disegnato a mano.</summary>
public enum ElementMode { Offset, Fill, Polygon }

/// <summary>Road = bordo strada tracciato; Outer/Inner = bordi di un elemento.</summary>
public enum EdgeKind { Road, Outer, Inner }

public readonly record struct EdgeRef(EdgeKind Kind, int Id);

/// <summary>Tipo delle strisce di mezzeria.</summary>
public enum DividerStyle { Tratteggiata, Continua, DoppiaContinua }

public class RoadEdge
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<V2> Points { get; set; } = new();
    /// <summary>
    /// Punti cliccati, con l'eventuale aggancio al bordo esterno di un elemento: se l'elemento cambia (larghezza, tratto),
    /// il bordo si ricalcola e lo segue. Null per i bordi tracciati solo su muri e punti liberi.
    /// </summary>
    public List<EdgeAnchor>? Anchors { get; set; }
    /// <summary>
    /// Elemento a cui appartiene la linea (il bordo o l'interasse disegnato per quell'elemento); -1 = bordo strada
    /// dei progetti precedenti; -2 = linea appena disegnata, elemento non ancora confermato.
    /// </summary>
    public int Owner { get; set; } = -1;
    public bool IsLinked => Anchors?.Any(a => a.El >= 0) == true;
}

/// <summary>Punto di un bordo: libero (X, Y) o agganciato al bordo esterno dell'elemento El alla progressiva S della sua linea guida.</summary>
public class EdgeAnchor
{
    public double X { get; set; }
    public double Y { get; set; }
    public int El { get; set; } = -1;
    public double S { get; set; }
    /// <summary>Fino al punto successivo il bordo segue il contorno dell'elemento.</summary>
    public bool Follow { get; set; }
    /// <summary>Su quale contorno dell'elemento sta il punto: 1 = bordo esterno (Outer), -1 = l'altro lato (Inner).</summary>
    public int Sd { get; set; } = 1;
    /// <summary>Vertice aggiunto da «segui il contorno» (lungo un muro): non è un punto chiave, si ricalcola.</summary>
    public bool Auto { get; set; }
    /// <summary>Punto in curva (Bézier): i lati che vi arrivano si curvano secondo la maniglia (Hx, Hy), simmetrica.</summary>
    public bool Curve { get; set; }
    /// <summary>Maniglia della curva: verso il punto successivo è P + H, verso il precedente P − H (metri).</summary>
    public double Hx { get; set; }
    public double Hy { get; set; }
}

public class WidthPoint
{
    public double T { get; set; }   // progressiva locale dall'inizio dell'elemento (m)
    public double W { get; set; }   // larghezza in quel punto (m)
}

/// <summary>
/// Variatore di sezione: tra le progressive T1 e T2 (sulla linea guida) la larghezza passa gradualmente a quella di prima
/// più Delta, e resta così oltre T2. Se T2 &lt; T1 il cambio vale verso l'inizio della linea.
/// </summary>
public class SectionVariator
{
    public double T1 { get; set; }
    public double T2 { get; set; }
    public double Delta { get; set; }
    /// <summary>Quanto del cambio è applicato in t (0 prima del tratto, 1 dopo).</summary>
    public double Ramp(double t)
    {
        double d = T2 - T1;
        if (Math.Abs(d) < 1e-9) return (T2 >= T1 ? t >= T2 : t <= T2) ? 1 : 0;
        return Math.Clamp((t - T1) / d, 0, 1);
    }
}

/// <summary>Fotoinserimento del progetto: la foto (punti, fotocamera e maschera stanno nei file accanto alla foto).</summary>
public class PhotoInsertRef
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    // dalla fotocamera calcolata (coordinate locali), per l'icona sulla mappa
    public V2? Cam;
    /// <summary>Direzione di vista in pianta (gradi, 0 = Est, 90 = Nord).</summary>
    public double Yaw;
    public double HFov = 60;
    public int Pairs;
    public bool Missing;
}

public class Metrics
{
    public double Length, Area, WMin, WMax, WMean;
}

/// <summary>Blocco di elementi: per dividere un progetto grande o tenere più versioni di una parte (si mostrano o si nascondono).</summary>
public class ElementGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    /// <summary>Colore del blocco: il bordo dei suoi elementi sulla mappa.</summary>
    public string Color { get; set; } = "#E6194B";

    public static readonly string[] Palette =
        { "#E6194B", "#3C8DFF", "#F58231", "#2BB673", "#B04FD8", "#00B8C4", "#F032B4", "#9BC53D", "#8C5A3C", "#FFC20E" };
}

public class Element
{
    public int Id { get; set; }
    /// <summary>Blocco dell'elemento (0 = nessun blocco).</summary>
    public int Group { get; set; }
    public string Name { get; set; } = "";
    public ElementType Type { get; set; }
    public ElementMode Mode { get; set; }
    public EdgeRef Base { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    /// <summary>Il tratto arriva fino alla fine del bordo di aggancio, anche se questo si allunga.</summary>
    public bool ToEnd { get; set; }
    public int Side { get; set; } = 1;
    /// <summary>La linea disegnata è l'interasse: l'elemento si estende per metà larghezza da ciascun lato.</summary>
    public bool Centered { get; set; }
    /// <summary>Numero di strisce di mezzeria (dividono l'elemento in parti uguali); -1 = automatico secondo tipo e larghezza.</summary>
    public int Dividers { get; set; } = -1;
    /// <summary>Parcheggio: linee bianche degli stalli di sosta.</summary>
    public bool StallLines { get; set; } = true;
    /// <summary>Parcheggio: lunghezza di ogni stallo lungo la linea (m).</summary>
    public double StallLength { get; set; } = 5.0;
    /// <summary>Marciapiede e aiuola: altezza sopra la strada (m).</summary>
    public double Height { get; set; } = 0.15;
    public bool HasHeight => Type is ElementType.Marciapiede or ElementType.Aiuola or ElementType.Ciclopedonale;
    /// <summary>Ciclopedonale: bici e pedoni separati da una striscia continua (false = percorso misto).</summary>
    public bool Separated { get; set; }

    public bool HasStalls => Type == ElementType.Parcheggio && Mode == ElementMode.Offset && StallLines && Error == null && Guide.Count >= 2;

    /// <summary>Linee bianche del parcheggio: le linee trasversali tra gli stalli e la linea lungo il lato verso la strada.</summary>
    public List<List<V2>> StallMarkings()
    {
        var res = new List<List<V2>>();
        if (!HasStalls) return res;
        double L = PL.Length(Guide), step = Math.Max(0.5, StallLength);
        for (double s = step; s < L - 0.5; s += step)
        {
            var P = PL.PointAt(Guide, s); var n = PL.TangentAt(Guide, s).LeftNormal; double w = WidthAt(s);
            res.Add(Centered ? new List<V2> { P + n * (w / 2), P - n * (w / 2) } : new List<V2> { P + n * (Side * 0.05), P + n * (Side * w) });
        }
        // linea continua lungo il lato esterno (verso la carreggiata), appena dentro al bordo
        if (!Centered) res.Add(PL.OffsetProfile(Guide, t => Math.Max(0.1, WidthAt(t) - 0.06), Side, WidthBreaks, 5));
        return res;
    }
    public DividerStyle DividerStyle { get; set; } = DividerStyle.Tratteggiata;

    /// <summary>Tipi che possono avere strisce di mezzeria.</summary>
    public static bool HasDividers(ElementType t) => t is ElementType.Carreggiata or ElementType.Corsia or ElementType.CorsiaDoppia
        or ElementType.Ciclabile or ElementType.CorsiaCiclabile;

    public bool SupportsDividers => HasDividers(Type) && Mode != ElementMode.Polygon;

    /// <summary>Strisce automatiche: mezzeria della carreggiata larga, della corsia doppia, della pista bidirezionale.</summary>
    public int AutoDividers => Type switch
    {
        ElementType.Carreggiata => Metrics.WMean >= 5.5 ? 1 : 0,
        ElementType.CorsiaDoppia => 1,
        ElementType.Ciclabile => Metrics.WMean >= 2.45 ? 1 : 0,
        _ => 0
    };

    public int EffectiveDividers => Type == ElementType.Ciclopedonale ? (Separated && Mode != ElementMode.Polygon ? 1 : 0)
        : !SupportsDividers ? 0 : Dividers >= 0 ? Dividers : AutoDividers;

    /// <summary>Le strisce di mezzeria: linee a k/(n+1) della larghezza, tra i due lati dell'elemento.</summary>
    // le strisce si ricalcolano solo se cambia la geometria (nuove liste Inner/Outer) o il numero
    List<List<V2>>? _divCache; int _divN = -1; object? _divIn, _divOut;
    /// <summary>Punto per l'etichetta sulla mappa, ricalcolato solo quando cambia il poligono.</summary>
    public V2? LabelCache; public object? LabelKey;

    public List<List<V2>> DividerLines()
    {
        int n = EffectiveDividers;
        if (_divCache != null && _divN == n && ReferenceEquals(_divIn, Inner) && ReferenceEquals(_divOut, Outer)) return _divCache;
        var res = new List<List<V2>>();
        _divCache = res; _divN = n; _divIn = Inner; _divOut = Outer;
        if (n <= 0 || Error != null || Inner.Count < 2 || Outer.Count < 2) return res;
        double L = PL.Length(Inner);
        var samples = new List<(V2 a, V2 b)>();
        for (double s = 0; ; s += 1.0)
        {
            bool last = s >= L; if (last) s = L;
            var a = PL.PointAt(Inner, s);
            samples.Add((a, PL.Project(Outer, a).Point));
            if (last) break;
        }
        for (int k = 1; k <= n; k++)
        {
            double f = k / (double)(n + 1);
            res.Add(PL.Simplify(PL.Dedup(samples.Select(x => x.a + (x.b - x.a) * f).ToList()), 0.02));
        }
        return res;
    }
    public double Width { get; set; } = 1.5;
    /// <summary>Rientranze e sporgenze del bordo di aggancio più piccole di questo valore (m) non vengono riportate sul bordo esterno.</summary>
    public double Smooth { get; set; } = 1.0;
    public List<WidthPoint> Profile { get; set; } = new();
    public EdgeRef? Other { get; set; }

    // Geometria calcolata
    public List<V2> Inner = new();
    /// <summary>Linea guida regolarizzata su cui si calcola l'offset (le progressive del profilo si misurano qui).</summary>
    public List<V2> Guide = new();
    public List<V2> Outer = new();
    public List<V2> Polygon = new();
    public Metrics Metrics = new();
    public string? Error;

    /// <summary>Variatori di sezione lungo la linea (larghezza che cambia gradualmente tra due punti).</summary>
    public List<SectionVariator> Variators { get; set; } = new();

    /// <summary>Progressive in cui la larghezza cambia andamento (punti del profilo e capi dei variatori).</summary>
    public IEnumerable<double> WidthBreaks => Profile.Select(x => x.T).Concat(Variators.SelectMany(v => new[] { v.T1, v.T2 }));

    public double WidthAt(double t)
    {
        double w = BaseWidthAt(t);
        foreach (var v in Variators) w += v.Delta * v.Ramp(t);
        return Math.Max(0.05, w);
    }

    double BaseWidthAt(double t)
    {
        if (Profile.Count == 0) return Width;
        if (t <= Profile[0].T) return Profile[0].W;
        for (int i = 1; i < Profile.Count; i++)
        {
            var a = Profile[i - 1]; var b = Profile[i];
            if (t <= b.T)
            {
                double d = b.T - a.T;
                return d < 1e-9 ? b.W : a.W + (b.W - a.W) * (t - a.T) / d;
            }
        }
        return Profile[^1].W;
    }

    /// <summary>Linea di mezzo dell'elemento (per la striscia tra le due corsie della corsia doppia).</summary>
    public List<V2> MidLine()
    {
        if (Mode == ElementMode.Fill)
        {
            if (Inner.Count < 2 || Outer.Count < 2) return new();
            double L = PL.Length(Inner);
            var res = new List<V2>();
            for (double s = 0; ; s += 1.0)
            {
                bool last = s >= L; if (last) s = L;
                var p = PL.PointAt(Inner, s);
                res.Add((p + PL.Project(Outer, p).Point) * 0.5);
                if (last) break;
            }
            return PL.Simplify(PL.Dedup(res), 0.03);
        }
        if (Mode == ElementMode.Polygon || Guide.Count < 2) return new();
        if (Centered) return new List<V2>(Guide);
        return PL.OffsetProfile(Guide, t => WidthAt(t) / 2, Side, WidthBreaks, 5);
    }

    public double MaxProfileWidth => (Profile.Count == 0 ? Width : Math.Max(Width, Profile.Max(p => p.W))) + Variators.Sum(v => Math.Max(0, v.Delta));

    /// <summary>Tipi che si possono scegliere, in ordine alfabetico (la corsia doppia non c'è più: è una carreggiata con una mezzeria).</summary>
    public static readonly ElementType[] Choosable = Enum.GetValues<ElementType>()
        .Where(t => t is not (ElementType.CorsiaDoppia or ElementType.Indefinito))
        .OrderBy(TypeLabel, StringComparer.Create(new System.Globalization.CultureInfo("it-IT"), true)).ToArray();

    public static string TypeLabel(ElementType t) => t switch
    {
        ElementType.Marciapiede => "Marciapiede",
        ElementType.Ciclabile => "Pista ciclabile",
        ElementType.Carreggiata => "Carreggiata",
        ElementType.Corsia => "Corsia",
        ElementType.Parcheggio => "Parcheggio",
        ElementType.Aiuola => "Aiuola / verde",
        ElementType.Ciclopedonale => "Ciclopedonale",
        ElementType.Separatore => "Striscia gialla",
        ElementType.CorsiaCiclabile => "Corsia ciclabile",
        ElementType.CorsiaDoppia => "Corsia doppia",
        ElementType.Indefinito => "Indefinito",
        _ => t.ToString()
    };

    public static double DefaultWidth(ElementType t) => t switch
    {
        ElementType.Marciapiede => 1.50,
        ElementType.Ciclabile => 2.50,
        ElementType.Carreggiata => 6.00,
        ElementType.Corsia => 3.00,
        ElementType.Parcheggio => 2.00,
        ElementType.Aiuola => 1.00,
        ElementType.Ciclopedonale => 3.00,
        ElementType.Separatore => 0.50,
        ElementType.CorsiaCiclabile => 1.50,
        ElementType.CorsiaDoppia => 6.00,
        _ => 1.5
    };
}

/// <summary>Nota sulla mappa: un tratto a mano libera (penna) o un testo. Spessore e dimensione del testo in pixel sullo schermo.</summary>
public class Annotation
{
    public bool IsText { get; set; }
    public List<V2> Pts { get; set; } = new();
    public string Color { get; set; } = "#E53935";
    public double Size { get; set; } = 3;
    public string Text { get; set; } = "";
}

public class Project
{
    /// <summary>Disegni a mano libera e note di testo sulla mappa.</summary>
    public List<Annotation> Annotations { get; } = new();
    /// <summary>Linee di riferimento: quelle disegnate per ogni elemento (bordo o interasse) e i bordi strada dei progetti precedenti.</summary>
    public List<RoadEdge> Edges { get; } = new();

    /// <summary>I due bordi strada del vecchio flusso (prove automatiche e progetti precedenti).</summary>
    public void EnsureLegacyEdges()
    {
        if (FindEdge(0) == null) Edges.Add(new RoadEdge { Id = 0, Name = "Bordo sinistro" });
        if (FindEdge(1) == null) Edges.Add(new RoadEdge { Id = 1, Name = "Bordo destro" });
    }
    public List<Element> Elements { get; } = new();
    public int NextId { get; set; } = 1;
    /// <summary>Rotazione della vista sulla mappa (gradi, antioraria): si salva nel progetto.</summary>
    public double ViewRotation { get; set; }
    /// <summary>Fotoinserimenti del progetto.</summary>
    public List<PhotoInsertRef> Photos { get; } = new();
    /// <summary>Sezioni stradali disegnate sulla mappa.</summary>
    public List<CrossSection> Sections { get; } = new();
    /// <summary>Blocchi di elementi.</summary>
    public List<ElementGroup> Groups { get; } = new();

    /// <summary>L'elemento è visibile (fuori da ogni blocco o in un blocco visibile). Gli elementi nascosti restano nel
    /// progetto (gli altri possono appoggiarsi a loro) ma non si vedono, non si selezionano e non vanno nelle esportazioni.</summary>
    /// <summary>Solo per le esportazioni (PDF): si vede soltanto questo gruppo (0 = solo gli elementi senza gruppo).</summary>
    public int? OnlyGroup;
    /// <summary>Con OnlyGroup: si vedono anche gli elementi senza gruppo (la base comune, per i fotoinserimenti).</summary>
    public bool OnlyGroupWithBase;
    public bool IsShown(Element e) => OnlyGroup is { } og ? e.Group == og || OnlyGroupWithBase && e.Group == 0 : e.Group == 0 || Groups.FirstOrDefault(g => g.Id == e.Group) is not { Visible: false };
    public IEnumerable<Element> Shown => Elements.Where(IsShown);
    /// <summary>La linea è visibile (non appartiene a un elemento nascosto).</summary>
    public bool IsShown(RoadEdge ed) => ed.Owner < 0 || Find(ed.Owner) is not { } o || IsShown(o);
    public string NewGroupName()
    {
        int n = Groups.Count + 1;
        while (Groups.Any(g => g.Name == $"Gruppo {n}")) n++;
        return $"Gruppo {n}";
    }

    static readonly GeometryFactory Gf = new();

    public Element? Find(int id) => Elements.FirstOrDefault(e => e.Id == id);

    public RoadEdge? FindEdge(int id) => Edges.FirstOrDefault(e => e.Id == id);

    /// <summary>Nuovo bordo strada (oltre a sinistro e destro: spartitraffico, isole, rientranze, altra via…).</summary>
    public RoadEdge AddEdge(string? name = null)
    {
        int id = Edges.Count == 0 ? 0 : Edges.Max(e => e.Id) + 1;
        int n = Edges.Count + 1;
        while (Edges.Any(e => e.Name == $"Bordo {n}")) n++;
        var e = new RoadEdge { Id = id, Name = string.IsNullOrWhiteSpace(name) ? $"Bordo {n}" : name.Trim() };
        Edges.Add(e);
        return e;
    }

    /// <summary>Elementi agganciati direttamente a un bordo strada.</summary>
    public List<Element> UsersOfEdge(int id) => Elements.Where(e =>
        (e.Base.Kind == EdgeKind.Road && e.Base.Id == id) || (e.Other is { Kind: EdgeKind.Road } o && o.Id == id)).ToList();

    public List<V2>? GetEdge(EdgeRef r)
    {
        switch (r.Kind)
        {
            case EdgeKind.Road:
                var e = Edges.FirstOrDefault(x => x.Id == r.Id);
                return e is { Points.Count: >= 2 } ? e.Points : null;
            case EdgeKind.Outer:
                var o = Find(r.Id);
                return o is { Error: null, Outer.Count: >= 2 } ? o.Outer : null;
            case EdgeKind.Inner:
                var i = Find(r.Id);
                return i is { Error: null, Inner.Count: >= 2 } ? i.Inner : null;
        }
        return null;
    }

    public string EdgeName(EdgeRef r) => r.Kind switch
    {
        EdgeKind.Road => Edges.FirstOrDefault(x => x.Id == r.Id)?.Name ?? "?",
        EdgeKind.Outer => "bordo esterno di " + (Find(r.Id)?.Name ?? "?"),
        EdgeKind.Inner => "bordo interno di " + (Find(r.Id)?.Name ?? "?"),
        _ => "?"
    };

    public bool DependsOn(Element e, int id) =>
        (e.Base.Kind != EdgeKind.Road && e.Base.Id == id) ||
        (e.Other is { } o && o.Kind != EdgeKind.Road && o.Id == id);

    public string NewName(ElementType t)
    {
        int n = Elements.Count(e => e.Type == t) + 1;
        string name;
        do { name = $"{Element.TypeLabel(t)} {n++}"; } while (Elements.Any(e => e.Name == name));
        return name;
    }

    /// <summary>Ricalcola tutti gli elementi nell'ordine di creazione (gli agganci puntano sempre a elementi precedenti).</summary>
    public void Regenerate()
    {
        // i bordi agganciati agli elementi seguono le loro modifiche, e gli elementi agganciati a quei bordi si ricalcolano
        for (int pass = 0; pass < 4; pass++)
        {
            foreach (var e in Elements) Build(e);
            if (!UpdateLinkedEdges()) break;
        }
    }

    /// <summary>L'elemento dipende (direttamente o tramite altri elementi) dal bordo strada edgeId?</summary>
    public bool ElementDependsOnEdge(Element e, int edgeId, int depth = 0)
    {
        if (depth > 50) return true;
        foreach (var r in new[] { (EdgeRef?)e.Base, e.Other })
        {
            if (r is not { } rr) continue;
            if (rr.Kind == EdgeKind.Road)
            {
                if (rr.Id == edgeId) return true;
                if (FindEdge(rr.Id) is { Anchors: { } an } && an.Any(a => a.El >= 0 && Find(a.El) is { } src && ElementDependsOnEdge(src, edgeId, depth + 1))) return true;
            }
            else if (Find(rr.Id) is { } src2 && ElementDependsOnEdge(src2, edgeId, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Posizione attuale di un punto agganciato: sul bordo esterno, alla progressiva S della linea guida.</summary>
    /// <summary>Contorno dell'elemento su cui stanno i punti agganciati: 1 = Outer, -1 = Inner.</summary>
    public static List<V2> SideLine(Element e, int sd) => sd < 0 ? e.Inner : e.Outer;

    /// <summary>Distanza (con segno, verso la normale sinistra della guida) del contorno sd dalla guida.</summary>
    static double SideOffset(Element e, int sd, double s) =>
        e.Centered ? (sd > 0 ? -1 : 1) * e.WidthAt(s) / 2 : (sd > 0 ? e.Side * e.WidthAt(s) : 0);

    public static V2? AnchorPoint(Element e, double s, int sd = 1)
    {
        var line = SideLine(e, sd);
        if (e.Error != null || e.Mode != ElementMode.Offset || e.Guide.Count < 2 || line.Count < 2) return null;
        var g = e.Guide;
        s = Math.Clamp(s, 0, PL.Length(g));
        double w = SideOffset(e, sd, s);
        // su uno spigolo della linea guida il punto agganciato è lo spigolo del bordo esterno (lungo la bisettrice);
        // se la linea è chiusa anche il punto di chiusura è uno spigolo
        bool closed = g.Count >= 4 && V2.Dist(g[0], g[^1]) < 1e-6;
        double L = PL.Length(g);
        if (closed && (s < 0.02 || s > L - 0.02)) s = 0;
        double acc = 0;
        for (int i = closed ? 0 : 1; i + 1 < g.Count; i++)
        {
            if (i > 0) acc += V2.Dist(g[i - 1], g[i]);
            if (Math.Abs(acc - s) > 0.02) continue;
            var prevA = i > 0 ? g[i - 1] : g[^2];
            var n1 = (g[i] - prevA).Norm().LeftNormal; var n2 = (g[i + 1] - g[i]).Norm().LeftNormal;
            var bis = n1 + n2; double bl = Math.Sqrt(V2.Dot(bis, bis));
            if (bl < 1e-6) break;
            bis = bis * (1 / bl);
            double c = Math.Max(0.2, V2.Dot(bis, n1));
            return PL.Project(line, g[i] + bis * (w / c)).Point;
        }
        var target = PL.PointAt(g, s) + PL.TangentAt(g, s).LeftNormal * w;
        return PL.Project(line, target).Point;
    }

    /// <summary>Tratto del bordo esterno tra due punti (il più breve, se il bordo è chiuso), senza gli estremi.</summary>
    static List<V2> OuterPath(Element e, V2 a, V2 b, int sd = 1)
    {
        var o = SideLine(e, sd); double L = PL.Length(o);
        double sa = PL.Project(o, a).Station, sb = PL.Project(o, b).Station;
        bool closed = V2.Dist(o[0], o[^1]) < 0.05;
        List<V2> path;
        if (!closed || Math.Abs(sb - sa) <= L / 2) path = PL.Sub(o, Math.Min(sa, sb), Math.Max(sa, sb));
        else
        {
            // passa per il punto di chiusura
            double lo = Math.Min(sa, sb), hi = Math.Max(sa, sb);
            var p1 = PL.Sub(o, hi, L); var p2 = PL.Sub(o, 0, lo);
            path = p1.Concat(p2.Skip(1)).ToList();
            if (sa < sb) path.Reverse();
            return path.Count > 2 ? path.Skip(1).Take(path.Count - 2).ToList() : new();
        }
        if (sa > sb) path.Reverse();
        return path.Count > 2 ? path.Skip(1).Take(path.Count - 2).ToList() : new();
    }

    /// <summary>
    /// Punti del bordo esterno di e da aggiungere dopo p perché il tratto dritto p→q non attraversi l'elemento:
    /// si avanza lungo il contorno (nel verso più corto) finché da lì q si vede senza passare dentro.
    /// </summary>
    List<V2> WrapAround(Element? e, V2 p, V2 q, int sd = 1)
    {
        var res = new List<V2>();
        if (e is not { Error: null, Mode: ElementMode.Offset } || SideLine(e, sd).Count < 2) return res;
        var poly = ToNts(e.Polygon);
        if (poly == null || !poly.IsValid) return res;
        var inside = poly.Buffer(-0.02);
        bool Cuts(V2 a, V2 b) => V2.Dist(a, b) > 0.05 && inside.Intersects(Gf.CreateLineString(new[] { new Coordinate(a.X, a.Y), new Coordinate(b.X, b.Y) }));
        if (!Cuts(p, q)) return res;
        var o = SideLine(e, sd); bool closed = V2.Dist(o[0], o[^1]) < 0.05;
        var pr = PL.Project(o, p);
        var cum = PL.Cumulative(o); double L = cum[^1];
        foreach (int dir in new[] { 1, -1 })
        {
            var path = new List<V2>();
            double s = pr.Station;
            // vertici del contorno in quel verso, al massimo mezzo giro
            var order = Enumerable.Range(0, o.Count - (closed ? 1 : 0))
                .Select(i => (i, d: dir > 0 ? (cum[i] - s + (closed ? L : 0)) % (closed ? L : double.MaxValue) : (s - cum[i] + (closed ? L : 0)) % (closed ? L : double.MaxValue)))
                .Where(x => x.d > 1e-6 && x.d < (closed ? L / 2 : L)).OrderBy(x => x.d).Select(x => o[x.i]);
            foreach (var v in order)
            {
                path.Add(v);
                if (!Cuts(v, q)) return path;
                if (path.Count > 12) break;
            }
        }
        return res;
    }

    /// <summary>Tratto di Bézier cubica da a a b (senza il punto di partenza; con quello d'arrivo se addEnd).</summary>
    static IEnumerable<V2> BezierSeg(EdgeAnchor a, EdgeAnchor b, bool addEnd)
    {
        var p0 = new V2(a.X, a.Y); var p3 = new V2(b.X, b.Y);
        var p1 = a.Curve ? p0 + new V2(a.Hx, a.Hy) : p0;
        var p2 = b.Curve ? p3 - new V2(b.Hx, b.Hy) : p3;
        const int K = 24;
        for (int k = 1; k <= (addEnd ? K : K - 1); k++)
        {
            double t = k / (double)K, u = 1 - t;
            yield return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }
    }

    /// <summary>Ricalcola i bordi agganciati; true se almeno uno è cambiato.</summary>
    public bool UpdateLinkedEdges()
    {
        bool changed = false;
        foreach (var edge in Edges)
        {
            if (edge.Anchors is not { Count: >= 2 } an) continue;
            var pts = new List<V2>();
            V2? prev = null;
            for (int i = 0; i < an.Count; i++)
            {
                var a = an[i];
                var el = a.El >= 0 ? Find(a.El) : null;
                var p = el != null && AnchorPoint(el, a.S, a.Sd) is { } q ? q : new V2(a.X, a.Y);
                a.X = p.X; a.Y = p.Y;
                if (i > 0 && (an[i - 1].Curve || a.Curve) && !an[i - 1].Follow)
                {
                    // tratto curvo (Bézier) verso un punto in curva o da un punto in curva
                    pts.AddRange(BezierSeg(an[i - 1], a, false));
                }
                else if (i > 0 && an[i - 1] is { Follow: true } pa && pa.El == a.El && el is { Error: null } && prev is { } pp)
                    pts.AddRange(OuterPath(el, pp, p, a.Sd));
                else if (i > 0 && prev is { } pp2)
                {
                    // tratto dritto verso il punto successivo: se allargando l'elemento lo spigolo è cresciuto e il
                    // tratto lo taglierebbe, il bordo gira attorno allo spigolo seguendo il contorno
                    var from = an[i - 1].El >= 0 ? Find(an[i - 1].El) : null;
                    var wrapA = WrapAround(from, pp2, p, an[i - 1].Sd);
                    var startB = wrapA.Count > 0 ? wrapA[^1] : pp2;
                    var wrapB = WrapAround(el, p, startB, a.Sd);
                    wrapB.Reverse();
                    pts.AddRange(wrapA); pts.AddRange(wrapB);
                }
                pts.Add(p); prev = p;
            }
            // poligono: tratto di chiusura curvo
            if (an.Count >= 3 && (an[^1].Curve || an[0].Curve) && !an[^1].Follow && edge.Owner >= 0 && Find(edge.Owner) is { Mode: ElementMode.Polygon })
                pts.AddRange(BezierSeg(an[^1], an[0], false));
            pts = PL.Dedup(pts);
            if (pts.Count >= 2 && (pts.Count != edge.Points.Count || pts.Zip(edge.Points).Any(z => V2.Dist(z.First, z.Second) > 1e-4)))
            {
                edge.Points = pts; changed = true;
            }
        }
        return changed;
    }

    public void Build(Element e)
    {
        e.Error = null; e.Inner = new(); e.Guide = new(); e.Outer = new(); e.Polygon = new(); e.Metrics = new();
        var baseLine = GetEdge(e.Base);
        if (baseLine == null) { e.Error = e.Mode == ElementMode.Polygon ? "Il contorno del poligono non esiste più" : "Il bordo di aggancio non esiste più"; return; }
        if (e.Mode == ElementMode.Polygon)
        {
            // poligono: la linea disegnata è il contorno, chiuso sempre
            var ring = PL.Dedup(baseLine);
            if (ring.Count >= 2 && V2.Dist(ring[0], ring[^1]) < 1e-6) ring.RemoveAt(ring.Count - 1);
            if (ring.Count < 3) { e.Error = "Il poligono ha bisogno di almeno tre punti"; return; }
            var closedRing = new List<V2>(ring) { ring[0] };
            e.Inner = closedRing; e.Outer = closedRing; e.Guide = closedRing;
            e.Polygon = ring;
            var pm = new Metrics { Length = PL.Length(closedRing) };
            var pp = ToNts(ring);
            if (pp != null) { try { pm.Area = pp.IsValid ? pp.Area : pp.Buffer(0).Area; } catch { pm.Area = Math.Abs(PL.RingArea(ring)); } }
            e.Metrics = pm;
            return;
        }
        double L = PL.Length(baseLine);
        double s0 = Math.Clamp(Math.Min(e.Start, e.End), 0, L);
        double s1 = e.ToEnd ? L : Math.Clamp(Math.Max(e.Start, e.End), 0, L);
        if (s1 - s0 < 0.05) { e.Error = "Il tratto selezionato è troppo corto"; return; }
        var b = PL.Dedup(PL.Sub(baseLine, s0, s1));

        if (e.Mode == ElementMode.Offset && e.Centered)
        {
            // interasse: metà larghezza da ciascun lato; Inner = lato sinistro, Outer = lato destro
            var guide = PL.Simplify(b, e.Smooth);
            double maxLoop = Math.Max(5, 2 * e.MaxProfileWidth);
            var left = PL.OffsetProfile(guide, t => e.WidthAt(t) / 2, 1, e.WidthBreaks, maxLoop);
            var right = PL.OffsetProfile(guide, t => e.WidthAt(t) / 2, -1, e.WidthBreaks, maxLoop);
            e.Inner = left; e.Guide = guide; e.Outer = right;
        }
        else if (e.Mode == ElementMode.Offset)
        {
            var guide = PL.Simplify(b, e.Smooth);
            var outer = PL.OffsetProfile(guide, e.WidthAt, e.Side, e.WidthBreaks,
                Math.Max(5, 4 * e.MaxProfileWidth));
            e.Inner = b; e.Guide = guide; e.Outer = outer;
        }
        else
        {
            var other = e.Other is { } oref ? GetEdge(oref) : null;
            if (other == null) { e.Error = "Il secondo bordo non esiste più"; return; }
            double pa0 = PL.Project(other, b[0]).Station;
            double pa1 = PL.Project(other, b[^1]).Station;
            var bsub = PL.Dedup(PL.Sub(other, pa0, pa1));
            if (bsub.Count < 2 || PL.Length(bsub) < 0.05) { e.Error = "I due bordi non si affacciano nel tratto scelto"; return; }
            e.Inner = b; e.Guide = b; e.Outer = bsub;
        }

        var poly = new List<V2>(e.Inner);
        for (int i = e.Outer.Count - 1; i >= 0; i--) poly.Add(e.Outer[i]);
        e.Polygon = PL.Dedup(poly);
        ComputeMetrics(e);
    }

    public static Polygon? ToNts(List<V2> ring)
    {
        if (ring.Count < 3) return null;
        // elemento tutto attorno a un edificio: il contorno è «anello interno chiuso + anello esterno chiuso»;
        // diventa un poligono con il buco (l'edificio), così le operazioni geometriche sono corrette
        for (int k = 3; k < ring.Count - 3; k++)
        {
            if (V2.Dist(ring[k], ring[0]) > 1e-3) continue;
            var a = ring.Take(k + 1).ToList(); var b = ring.Skip(k + 1).ToList();
            if (b.Count < 4 || V2.Dist(b[0], b[^1]) > 1e-3) break;
            LinearRing R(List<V2> l)
            {
                var cs = l.Select(p => new Coordinate(p.X, p.Y)).ToList();
                cs[^1] = cs[0].Copy();   // chiusura esatta
                return Gf.CreateLinearRing(cs.ToArray());
            }
            try
            {
                var ra = R(a); var rb = R(b);
                double aa = Math.Abs(PL.RingArea(a)), ab = Math.Abs(PL.RingArea(b));
                var poly = aa >= ab ? Gf.CreatePolygon(ra, new[] { rb }) : Gf.CreatePolygon(rb, new[] { ra });
                if (poly.IsValid) return poly;
                if (Environment.GetEnvironmentVariable("CICLODRAFT_DEBUG_NTS") == "1")
                    Console.WriteLine("      foro non valido: " + new NetTopologySuite.Operation.Valid.IsValidOp(poly).ValidationError);
            }
            catch { }
            break;
        }
        var c = ring.Select(p => new Coordinate(p.X, p.Y)).ToList();
        c.Add(c[0]);
        try { return Gf.CreatePolygon(c.ToArray()); } catch { return null; }
    }

    static void ComputeMetrics(Element e)
    {
        var m = new Metrics();
        double lin = PL.Length(e.Inner), lout = PL.Length(e.Outer);
        m.Length = e.Mode == ElementMode.Offset ? (e.Centered ? PL.Length(e.Guide) : lin) : (lin + lout) / 2;

        var poly = ToNts(e.Polygon);
        if (poly != null)
        {
            try { m.Area = poly.IsValid ? poly.Area : poly.Buffer(0).Area; }
            catch { m.Area = Math.Abs(PL.RingArea(e.Polygon)); }
        }

        double step = Math.Max(0.1, lin / 4000.0);
        double min = double.MaxValue, max = 0, sum = 0; int n = 0;
        for (double s = 0; ; s += step)
        {
            bool last = s >= lin;
            if (last) s = lin;
            var P = PL.PointAt(e.Inner, s);
            var pr = PL.Project(e.Outer, P);
            bool atEnd = pr.Station < 1e-6 || pr.Station > lout - 1e-6;
            bool atOwnEnd = s < 1e-6 || last;
            if (!(e.Mode == ElementMode.Fill && atEnd && !atOwnEnd))
            {
                min = Math.Min(min, pr.Distance); max = Math.Max(max, pr.Distance);
                sum += pr.Distance; n++;
            }
            if (last) break;
        }
        if (n > 0) { m.WMin = min; m.WMax = max; m.WMean = sum / n; }
        e.Metrics = m;
    }
}
