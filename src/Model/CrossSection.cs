using CicloDraft.Geometry;
using CicloDraft.IO;
using NetTopologySuite.Geometries;

namespace CicloDraft.Model;

/// <summary>
/// Sezione stradale: una linea A–B sulla mappa. Chi guarda la sezione sta su un lato della linea:
/// senza inversione guarda verso la sinistra di A→B (A a sinistra nel disegno, B a destra); con Flip guarda dall'altra parte.
/// </summary>
public class CrossSection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public V2 A { get; set; }
    public V2 B { get; set; }
    public bool Flip { get; set; }

    public double Length => V2.Dist(A, B);
    /// <summary>Direzione in cui guarda chi osserva la sezione (unitaria, in pianta).</summary>
    public V2 ViewDir { get { var d = (B - A).Norm(); var n = d.LeftNormal; return Flip ? n * -1 : n; } }
    /// <summary>Nel disegno: punto di partenza a sinistra e verso a destra.</summary>
    public (V2 left, V2 right) DrawingEnds => Flip ? (B, A) : (A, B);
}

/// <summary>Un tratto della sezione: un elemento del progetto o un edificio tagliato dalla linea.</summary>
public record SectionPiece(Element? Element, double From, double To, bool Building)
{
    public double Width => To - From;
    /// <summary>Elemento dentro cui sta questo tratto (es. un parcheggio disegnato dentro la carreggiata).</summary>
    public Element? Inside { get; init; }
}

public class SectionResult
{
    public double Length;
    public List<SectionPiece> Pieces = new();
    public List<SectionPiece> Buildings = new();
    public double ElementsWidth => Pieces.Sum(p => p.Width);
    /// <summary>Distanza tra le facciate più vicine agli elementi (null se manca un edificio da un lato).</summary>
    public double? BetweenBuildings;
}

public static class SectionCalc
{
    static readonly GeometryFactory Gf = new();
    const double Tol = 0.01;

    /// <summary>
    /// Elementi sovrapposti lungo la linea: un elemento dentro un altro (es. parcheggi disegnati dentro la carreggiata)
    /// sta sopra e divide quello che lo contiene; per le sovrapposizioni parziali vince l'elemento disegnato dopo.
    /// Restano i tratti visibili, senza doppi conteggi.
    /// </summary>
    static List<SectionPiece> Visible(List<(Element e, int order, double a, double b)> raw)
    {
        bool Contains((Element e, int order, double a, double b) o, (Element e, int order, double a, double b) i) =>
            o.e != i.e && o.a <= i.a + Tol && o.b >= i.b - Tol && (o.b - o.a) > (i.b - i.a) + Tol;
        // profondità: in quanti altri tratti è contenuto (prima si dipingono i contenitori, poi quello che c'è dentro)
        var items = raw.Select(r => (r, depth: raw.Count(o => Contains(o, r)),
            inside: raw.Where(o => Contains(o, r)).OrderBy(o => o.b - o.a).Select(o => o.e).FirstOrDefault())).ToList();
        var segs = new List<SectionPiece>();
        foreach (var it in items.OrderBy(x => x.depth).ThenBy(x => x.r.order))
        {
            double a = it.r.a, b = it.r.b;
            // ciò che c'era sotto viene tagliato dove arriva il nuovo tratto
            var next = new List<SectionPiece>();
            foreach (var s in segs)
            {
                if (s.To <= a + 1e-9 || s.From >= b - 1e-9) { next.Add(s); continue; }
                if (s.From < a - Tol) next.Add(s with { To = a });
                if (s.To > b + Tol) next.Add(s with { From = b });
            }
            next.Add(new SectionPiece(it.r.e, a, b, false) { Inside = it.depth > 0 ? it.inside : null });
            segs = next;
        }
        segs.Sort((x, y) => x.From.CompareTo(y.From));
        // tratti dello stesso elemento che si toccano si uniscono
        var merged = new List<SectionPiece>();
        foreach (var s in segs.Where(s => s.Width >= Tol))
        {
            if (merged.Count > 0 && merged[^1].Element == s.Element && s.From - merged[^1].To < 0.005) merged[^1] = merged[^1] with { To = s.To };
            else merged.Add(s);
        }
        return merged;
    }

    /// <summary>
    /// Taglia elementi ed edifici con la linea della sezione. Le progressive vanno da sinistra a destra nel disegno
    /// (cioè da A a B, o da B ad A se la sezione è invertita).
    /// </summary>
    public static SectionResult Compute(CrossSection s, Project p, GpkgData? data)
    {
        var (l, r) = s.DrawingEnds;
        var res = new SectionResult { Length = V2.Dist(l, r) };
        if (res.Length < 0.05) return res;
        var line = Gf.CreateLineString(new[] { new Coordinate(l.X, l.Y), new Coordinate(r.X, r.Y) });
        var dir = (r - l).Norm();
        double T(Coordinate c) => V2.Dot(new V2(c.X, c.Y) - l, dir);

        IEnumerable<(double a, double b)> Cut(NetTopologySuite.Geometries.Geometry? g)
        {
            if (g == null || g.IsEmpty) yield break;
            NetTopologySuite.Geometries.Geometry inter;
            try { inter = (g.IsValid ? g : g.Buffer(0)).Intersection(line); } catch { yield break; }
            for (int i = 0; i < inter.NumGeometries; i++)
                if (inter.GetGeometryN(i) is LineString ls && ls.NumPoints >= 2)
                {
                    double a = T(ls.GetCoordinateN(0)), b = T(ls.GetCoordinateN(ls.NumPoints - 1));
                    if (Math.Abs(b - a) > 0.005) yield return (Math.Min(a, b), Math.Max(a, b));
                }
        }

        var raw = new List<(Element e, int order, double a, double b)>();
        for (int k = 0; k < p.Elements.Count; k++)
        {
            var e = p.Elements[k];
            if (e.Error != null || e.Polygon.Count < 3 || !p.IsShown(e)) continue;
            foreach (var (a, b) in Cut(Project.ToNts(e.Polygon))) raw.Add((e, k, a, b));
        }
        res.Pieces = Visible(raw);

        if (data != null)
        {
            var box = BBox.Of(new List<V2> { l, r });
            foreach (var (_, _, ring) in data.RingsWithRole(LayerRole.Muri))
            {
                if (!ring.Closed || ring.Pts.Count < 4 || !ring.Box.Intersects(box)) continue;
                foreach (var (a, b) in Cut(Project.ToNts(ring.Pts))) res.Buildings.Add(new SectionPiece(null, a, b, true));
            }
            res.Buildings.Sort((x, y) => x.From.CompareTo(y.From));
            if (res.Pieces.Count > 0)
            {
                double f = res.Pieces.Min(x => x.From), t = res.Pieces.Max(x => x.To);
                var left = res.Buildings.Where(x => x.To <= f + 0.3).OrderByDescending(x => x.To).FirstOrDefault();
                var right = res.Buildings.Where(x => x.From >= t - 0.3).OrderBy(x => x.From).FirstOrDefault();
                if (left != null && right != null) res.BetweenBuildings = right.From - left.To;
            }
        }
        return res;
    }
}
