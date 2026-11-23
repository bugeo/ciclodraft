using System.Text;
using System.Text.Json.Nodes;
using CicloDraft.Geometry;
using CicloDraft.Model;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace CicloDraft.IO;

public class Export3DOptions
{
    public bool Buildings { get; set; } = true;
    /// <summary>Altezza usata per gli edifici senza altezza nei dati (m).</summary>
    public double DefaultBuildingHeight { get; set; } = 10;
    /// <summary>Marciapiedi esistenti (livelli con ruolo Marciapiedi) fuori dal progetto.</summary>
    public bool ExistingSidewalks { get; set; } = true;
    public bool Markings { get; set; } = true;
    public bool Ground { get; set; } = true;
    /// <summary>Altezza dei marciapiedi e delle aiuole sul piano stradale (m).</summary>
    public double CurbHeight { get; set; } = 0.15;
    /// <summary>Larghezza del cordolo (m).</summary>
    public double CurbWidth { get; set; } = 0.15;
    /// <summary>Pista ciclabile rialzata alla quota del marciapiede (altrimenti a quota strada).</summary>
    public bool RaisedBikeLane { get; set; }

    /// <summary>Texture realistiche (asfalto, pietra, facciate con finestre…). Senza: colori uniformi.</summary>
    public bool Textures { get; set; } = true;
    /// <summary>Alberi dai livelli di punti con nome «albero»/«tree».</summary>
    public bool Trees { get; set; } = true;
    /// <summary>Lampioni lungo i marciapiedi del progetto.</summary>
    public bool StreetLights { get; set; } = true;
    public double StreetLightSpacing { get; set; } = 25;
    /// <summary>Simboli della bicicletta dipinti sulle piste ciclabili.</summary>
    public bool BikeSymbols { get; set; } = true;
    /// <summary>Luce del sole (KHR_lights_punctual).</summary>
    public bool Sun { get; set; } = true;
}

public record Export3DResult(string Path, int Triangles, int Elements, int Buildings, int Trees, long Bytes);

/// <summary>
/// Esporta il progetto in glTF 2.0 binario (.glb): metri, asse Y = quota, X = Est, -Z = Nord.
/// L'origine della scena è l'origine locale del progetto (coordinate reali in asset.extras).
/// </summary>
public static class Export3D
{
    // ───────────── materiali ─────────────

    /// <summary>Materiale: colore (moltiplica la texture), rugosità, texture generata e sua dimensione reale in metri.</summary>
    public record Mat(string Name, string Hex, double Rough, string? TexKey = null, double TexW = 1, double TexH = 1,
                      double Metal = 0, bool AlphaMask = false, bool DoubleSided = false);

    public static readonly Mat Asfalto = new("Asfalto", "#3B3E43", 0.92, "asfalto", 4, 4);
    public static readonly Mat Marciapiede = new("Marciapiede (lastre di pietra)", "#BDB5A6", 0.85, "pietra", 2.4, 2.4);
    public static readonly Mat MarciapiedeEsistente = new("Marciapiede esistente", "#A8A398", 0.88, "pietra_esistente", 2.4, 2.4);
    public static readonly Mat Cordolo = new("Cordolo (granito)", "#DAD7D0", 0.7, "granito", 1, 1);
    public static readonly Mat Ciclabile = new("Ciclabile (resina rossa)", "#B2442F", 0.82, "resina", 2, 2);
    public static readonly Mat Segnaletica = new("Segnaletica", "#F1F1EC", 0.6, "vernice", 1, 1);
    public static readonly Mat SegnaleticaGialla = new("Striscia gialla", "#EDBE1A", 0.6, "vernice_gialla", 1, 1);
    public static readonly Mat SimboloBici = new("Simbolo bici", "#F1F1EC", 0.6, "bici", 1, 1, AlphaMask: true);
    public static readonly Mat Verde = new("Verde", "#6C8E4B", 0.95, "erba", 2, 2);
    public static readonly Mat Parcheggio = new("Parcheggio", "#45484E", 0.92, "asfalto", 4, 4);
    public static readonly Mat Copertura = new("Copertura (coppi)", "#9C5A4B", 0.85, "coppi", 2, 2);
    public static readonly Mat Cornice = new("Cornice di gronda", "#E6E0D4", 0.85);
    public static readonly Mat Tronco = new("Tronco", "#5A4636", 0.95);
    public static readonly Mat[] Chiome =
    {
        new("Chioma 1", "#4E6B34", 0.95), new("Chioma 2", "#5F7A3A", 0.95), new("Chioma 3", "#46602F", 0.95)
    };
    public static readonly Mat Metallo = new("Lampione (metallo)", "#34383C", 0.45, Metal: 0.7);
    public static readonly Mat VetroLampada = new("Lampione (vetro)", "#EEE8D6", 0.2);

    const int FacadeVariants = 6;
    const double BayW = 3.6, FloorH = 3.2, GroundH = 3.5;
    static readonly Mat[] FacciataPiani = Enumerable.Range(0, FacadeVariants)
        .Select(i => new Mat($"Facciata {i + 1} (piani)", Pal(i).Plaster, 0.9, $"piano_{i}", BayW, FloorH)).ToArray();
    static readonly Mat[] FacciataTerra = Enumerable.Range(0, FacadeVariants)
        .Select(i => new Mat($"Facciata {i + 1} (piano terra)", Pal(i).Plaster, 0.9, $"terra_{i}", 2 * BayW, GroundH)).ToArray();
    static readonly Mat[] Intonaco = Enumerable.Range(0, FacadeVariants)
        .Select(i => new Mat($"Intonaco {i + 1}", Pal(i).Plaster, 0.92, $"intonaco_{i}", 3, 3)).ToArray();
    static TextureFactory.Palette Pal(int i) => TextureFactory.Palettes[i % TextureFactory.Palettes.Length];

    public static Tex MakeTexture(string key)
    {
        if (key.StartsWith("piano_")) { int i = int.Parse(key[6..]); return TextureFactory.FacadeFloor(Pal(i), i); }
        if (key.StartsWith("terra_")) { int i = int.Parse(key[6..]); return TextureFactory.FacadeGround(Pal(i), i); }
        if (key.StartsWith("intonaco_")) { int i = int.Parse(key[9..]); return TextureFactory.PlainPlaster(Pal(i), i); }
        return key switch
        {
            "asfalto" => TextureFactory.Asphalt(),
            "pietra" => TextureFactory.StoneSlabs("#C2BBAD", 3),
            "pietra_esistente" => TextureFactory.StoneSlabs("#AFA99D", 8),
            "granito" => TextureFactory.Granite(),
            "resina" => TextureFactory.Resin("#B5462F", 4),
            "vernice" => TextureFactory.Paint(),
            "vernice_gialla" => TextureFactory.Paint("#EDBE1A", "#A99A62"),
            "erba" => TextureFactory.Grass(),
            "coppi" => TextureFactory.RoofTiles(),
            "bici" => TextureFactory.BikeSymbol(),
            _ => new Tex(4, 4)
        };
    }

    static readonly GeometryFactory Gf = new();

    /// <summary>Punto 3D in coordinate glTF.</summary>
    public readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public V3 Norm() { var l = Math.Sqrt(X * X + Y * Y + Z * Z); return l < 1e-15 ? new V3(0, 1, 0) : new V3(X / l, Y / l, Z / l); }
    }

    public readonly record struct UV(double U, double V);

    static V3 P(double x, double y, double h) => new(x, h, -y);
    static readonly V3 Up = new(0, 1, 0);
    static readonly V3 Down = new(0, -1, 0);

    /// <summary>Geometria di un materiale: vertici indicizzati (posizione, normale, UV).</summary>
    public class MeshPart
    {
        public readonly List<float> Pos = new(), Nrm = new(), Tex = new();
        public readonly List<uint> Idx = new();
        readonly Dictionary<(float, float, float, float, float, float, float, float), uint> _map = new();
        public int Tris => Idx.Count / 3;

        uint Vertex(V3 p, V3 n, UV t)
        {
            var key = ((float)p.X, (float)p.Y, (float)p.Z, (float)n.X, (float)n.Y, (float)n.Z, (float)t.U, (float)t.V);
            if (_map.TryGetValue(key, out var i)) return i;
            i = (uint)(Pos.Count / 3);
            Pos.Add(key.Item1); Pos.Add(key.Item2); Pos.Add(key.Item3);
            Nrm.Add(key.Item4); Nrm.Add(key.Item5); Nrm.Add(key.Item6);
            Tex.Add(key.Item7); Tex.Add(key.Item8);
            _map[key] = i;
            return i;
        }

        /// <summary>Triangolo con normale piatta orientata come hint.</summary>
        public void Tri(V3 a, V3 b, V3 c, V3 hint, UV ta, UV tb, UV tc)
        {
            var n = V3.Cross(b - a, c - a);
            if (V3.Dot(n, n) < 1e-20) return;
            if (V3.Dot(n, hint) < 0) { (b, c) = (c, b); (tb, tc) = (tc, tb); n = n * -1; }
            n = n.Norm();
            Idx.Add(Vertex(a, n, ta)); Idx.Add(Vertex(b, n, tb)); Idx.Add(Vertex(c, n, tc));
        }

        /// <summary>Triangolo con normali per vertice (superfici morbide, es. chiome).</summary>
        public void TriSmooth(V3 a, V3 b, V3 c, V3 na, V3 nb, V3 nc, V3 hint)
        {
            var n = V3.Cross(b - a, c - a);
            if (V3.Dot(n, n) < 1e-20) return;
            if (V3.Dot(n, hint) < 0) { (b, c) = (c, b); (nb, nc) = (nc, nb); }
            var z = new UV(0, 0);
            Idx.Add(Vertex(a, na, z)); Idx.Add(Vertex(b, nb, z)); Idx.Add(Vertex(c, nc, z));
        }

        public void Quad(V3 a, V3 b, V3 c, V3 d, V3 hint, UV ta, UV tb, UV tc, UV td)
        { Tri(a, b, c, hint, ta, tb, tc); Tri(a, c, d, hint, ta, tc, td); }
    }

    public class Node
    {
        public string Name = "";
        public Dictionary<Mat, MeshPart> Parts = new();
        public List<Node> Children = new();
        public JsonObject? Camera;
        public int? Light;
        public double[]? Translation, Rotation;
        public MeshPart Part(Mat m) => Parts.TryGetValue(m, out var p) ? p : Parts[m] = new MeshPart();
        public int Tris => Parts.Values.Sum(p => p.Tris) + Children.Sum(c => c.Tris);
    }

    // ───────────── primitive geometriche ─────────────

    static IEnumerable<Polygon> Polys(NtsGeometry? g)
    {
        if (g == null || g.IsEmpty) yield break;
        if (g is Polygon p) { yield return p; yield break; }
        for (int i = 0; i < g.NumGeometries; i++)
            foreach (var x in Polys(g.GetGeometryN(i))) yield return x;
    }

    static NtsGeometry Valid(NtsGeometry g)
    {
        if (g.IsValid) return g;
        try { return g.Buffer(0); } catch { return g; }
    }

    /// <summary>Mappatura planare delle texture: rotazione (per allineare alla strada) e scala in metri.</summary>
    public readonly record struct Planar(double Angle, double W, double H)
    {
        public UV At(double x, double y)
        {
            double c = Math.Cos(Angle), s = Math.Sin(Angle);
            return new UV((x * c + y * s) / W, -(-x * s + y * c) / H);
        }
        public static Planar For(Mat m, double angle = 0) => new(angle, m.TexW, m.TexH);
    }

    /// <summary>Faccia orizzontale (con buchi) alla quota h.</summary>
    public static void Top(MeshPart m, Polygon poly, double h, Planar uv, bool facingDown = false)
    {
        NtsGeometry tris;
        try { tris = NetTopologySuite.Triangulate.Polygon.ConstrainedDelaunayTriangulator.Triangulate(poly); }
        catch
        {
            try { tris = NetTopologySuite.Triangulate.Polygon.PolygonTriangulator.Triangulate(poly); }
            catch { return; }
        }
        for (int i = 0; i < tris.NumGeometries; i++)
        {
            var c = tris.GetGeometryN(i).Coordinates;
            if (c.Length < 3) continue;
            m.Tri(P(c[0].X, c[0].Y, h), P(c[1].X, c[1].Y, h), P(c[2].X, c[2].Y, h), facingDown ? Down : Up,
                uv.At(c[0].X, c[0].Y), uv.At(c[1].X, c[1].Y), uv.At(c[2].X, c[2].Y));
        }
    }

    /// <summary>Lato di un anello con la normale esterna del solido (in pianta).</summary>
    public record WallEdge(Coordinate A, Coordinate B, V3 Outward);

    static IEnumerable<WallEdge> Edges(Polygon poly)
    {
        IEnumerable<WallEdge> Ring(LineString r, bool hole)
        {
            var c = r.Coordinates;
            bool ccw = Orientation.IsCCW(c);
            bool flip = hole ? ccw : !ccw; // l'esterno del solido è a destra per i contorni antiorari e per i buchi orari
            for (int i = 0; i < c.Length - 1; i++)
            {
                var a = c[i]; var b = c[i + 1];
                if (a.Distance(b) < 1e-6) continue;
                double dx = b.X - a.X, dy = b.Y - a.Y;
                // verso scelto in modo che l'esterno sia sempre a destra: la texture non risulta specchiata
                var o = flip ? P(-dy, dx, 0) : P(dy, -dx, 0);
                yield return flip ? new WallEdge(b, a, new V3(o.X, 0, o.Z).Norm()) : new WallEdge(a, b, new V3(o.X, 0, o.Z).Norm());
            }
        }
        foreach (var e in Ring(poly.ExteriorRing, false)) yield return e;
        foreach (var h in poly.InteriorRings) foreach (var e in Ring(h, true)) yield return e;
    }

    /// <summary>Una fascia di parete da h0 a h1, con UV: u lungo il muro (u0..u1), v dall'alto (v0) al basso (v1).</summary>
    static void WallBand(MeshPart m, WallEdge e, double h0, double h1, double u0, double u1, double vTop, double vBottom)
    {
        m.Quad(P(e.A.X, e.A.Y, h0), P(e.B.X, e.B.Y, h0), P(e.B.X, e.B.Y, h1), P(e.A.X, e.A.Y, h1), e.Outward,
            new UV(u0, vBottom), new UV(u1, vBottom), new UV(u1, vTop), new UV(u0, vTop));
    }

    /// <summary>Pareti semplici con texture in metri (cordoli, rialzi).</summary>
    static void Walls(Polygon poly, double h0, double h1, Func<WallEdge, (MeshPart part, Mat mat)> partFor)
    {
        double acc = 0;
        foreach (var e in Edges(poly))
        {
            var (part, mat) = partFor(e);
            double L = e.A.Distance(e.B);
            WallBand(part, e, h0, h1, acc / mat.TexW, (acc + L) / mat.TexW, -h1 / mat.TexH, -h0 / mat.TexH);
            acc += L;
        }
    }

    static Polygon? ToPoly(IReadOnlyList<V2> ring)
    {
        if (ring.Count < 3) return null;
        var c = ring.Select(p => new Coordinate(p.X, p.Y)).ToList();
        c.Add(c[0]);
        try { return Gf.CreatePolygon(c.ToArray()); } catch { return null; }
    }

    static double Angle(IReadOnlyList<V2> line) => line.Count < 2 ? 0 : Math.Atan2(line[^1].Y - line[0].Y, line[^1].X - line[0].X);

    /// <summary>Striscia di larghezza w centrata sulla linea, alla quota h.</summary>
    static void Stripe(MeshPart m, IReadOnlyList<V2> line, double w, double h)
    {
        if (line.Count < 2 || PL.Length(line) < 0.05) return;
        var l = PL.OffsetProfile(line, _ => w / 2, 1, Array.Empty<double>(), 1);
        var r = PL.OffsetProfile(line, _ => w / 2, -1, Array.Empty<double>(), 1);
        var ring = new List<V2>(l); for (int i = r.Count - 1; i >= 0; i--) ring.Add(r[i]);
        var pg = ToPoly(PL.Dedup(ring));
        if (pg == null) return;
        foreach (var p in Polys(Valid(pg))) Top(m, p, h, new Planar(Angle(line), 1, 1));
    }

    /// <summary>Linea tratteggiata: tratti di lunghezza dash, intervalli gap.</summary>
    static void Dashed(MeshPart m, IReadOnlyList<V2> line, double w, double dash, double gap, double h)
    {
        double L = PL.Length(line);
        double start = Math.Max(0, (L % (dash + gap)) / 2);
        for (double s = start; s < L - 0.2; s += dash + gap)
            Stripe(m, PL.Sub(line, s, Math.Min(L, s + dash)), w, h);
    }

    /// <summary>Linea mediana tra due bordi (per l'asse della carreggiata).</summary>
    static List<V2> MidLine(IReadOnlyList<V2> a, IReadOnlyList<V2> b)
    {
        double L = PL.Length(a);
        var res = new List<V2>();
        for (double s = 0; ; s += 1.0)
        {
            bool last = s >= L; if (last) s = L;
            var p = PL.PointAt(a, s);
            res.Add((p + PL.Project(b, p).Point) * 0.5);
            if (last) break;
        }
        return PL.Simplify(PL.Dedup(res), 0.03);
    }

    /// <summary>Rettangolo orizzontale orientato (decalcomanie, es. simbolo bici), lungo = direzione di marcia.</summary>
    static void Decal(MeshPart m, V2 c, V2 dir, double width, double length, double h)
    {
        var t = dir.Norm(); var n = t.LeftNormal;
        var p0 = c - t * (length / 2) - n * (width / 2);
        var p1 = c - t * (length / 2) + n * (width / 2);
        var p2 = c + t * (length / 2) + n * (width / 2);
        var p3 = c + t * (length / 2) - n * (width / 2);
        // v = 0 in alto nell'immagine = davanti
        m.Quad(P(p0.X, p0.Y, h), P(p1.X, p1.Y, h), P(p2.X, p2.Y, h), P(p3.X, p3.Y, h), Up,
            new UV(1, 1), new UV(0, 1), new UV(0, 0), new UV(1, 0));
    }

    /// <summary>Prisma verticale a sezione poligonale regolare.</summary>
    static void Prism(MeshPart m, V2 c, double r, int sides, double h0, double h1, bool cap = true)
    {
        for (int i = 0; i < sides; i++)
        {
            double a0 = 2 * Math.PI * i / sides, a1 = 2 * Math.PI * (i + 1) / sides;
            var A = new V2(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
            var B = new V2(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
            var mid = (A + B) * 0.5 - c;
            m.Quad(P(A.X, A.Y, h0), P(B.X, B.Y, h0), P(B.X, B.Y, h1), P(A.X, A.Y, h1), P(mid.X, mid.Y, 0),
                new UV(0, 1), new UV(1, 1), new UV(1, 0), new UV(0, 0));
            if (cap) m.Tri(P(c.X, c.Y, h1), P(A.X, A.Y, h1), P(B.X, B.Y, h1), Up, new UV(0, 0), new UV(0, 0), new UV(0, 0));
        }
    }

    /// <summary>Parallelepipedo orientato (asse lungo = dir).</summary>
    static void Box(MeshPart m, V2 c, V2 dir, double len, double wid, double h0, double h1)
    {
        var t = dir.Norm() * (len / 2); var n = dir.Norm().LeftNormal * (wid / 2);
        var ring = new[] { c - t - n, c + t - n, c + t + n, c - t + n };
        var poly = ToPoly(ring)!;
        Top(m, poly, h1, new Planar(0, 1, 1));
        Top(m, poly, h0, new Planar(0, 1, 1), facingDown: true);
        foreach (var e in Edges(poly)) WallBand(m, e, h0, h1, 0, 1, 0, 1);
    }

    // ───────────── scena ─────────────

    public static Node BuildScene(Project project, GpkgData? data, Export3DOptions o, out int nElements, out int nBuildings, out int nTrees)
    {
        var root = new Node { Name = "CicloDraft" };
        nElements = 0; nBuildings = 0; nTrees = 0;
        double hc = o.CurbHeight;

        var box = data?.Box ?? BBox.Empty;
        foreach (var e in project.Shown.Where(e => e.Polygon.Count >= 3)) box = box.Union(BBox.Of(e.Polygon));
        foreach (var e in project.Edges.Where(e => e.Points.Count >= 2)) box = box.Union(BBox.Of(e.Points));
        if (box.IsEmpty) box = new BBox(-50, -50, 50, 50);

        if (o.Ground)
        {
            var g = new Node { Name = "Terreno" };
            double m = 10;
            Top(g.Part(Asfalto), Gf.CreatePolygon(new[]
            {
                new Coordinate(box.MinX - m, box.MinY - m), new Coordinate(box.MaxX + m, box.MinY - m),
                new Coordinate(box.MaxX + m, box.MaxY + m), new Coordinate(box.MinX - m, box.MaxY + m), new Coordinate(box.MinX - m, box.MinY - m)
            }), 0, Planar.For(Asfalto));
            root.Children.Add(g);
        }

        // ── progetto ──
        var proj = new Node { Name = "Progetto" };
        var projectArea = new List<NtsGeometry>();
        foreach (var e in project.Shown)
        {
            if (e.Error != null || e.Polygon.Count < 3) continue;
            var pg = ToPoly(e.Polygon);
            if (pg == null) continue;
            var geom = Valid(pg);
            projectArea.Add(geom);
            var n = new Node { Name = e.Name };
            BuildElement(n, e, geom, o, data);
            proj.Children.Add(n);
            nElements++;
        }
        root.Children.Add(proj);
        NtsGeometry? projectUnion = null;
        if (projectArea.Count > 0)
            try { projectUnion = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(projectArea); } catch { }

        // ── marciapiedi esistenti fuori dal progetto ──
        if (o.ExistingSidewalks && data != null)
        {
            var src = data.Layers.Where(l => (LayerRole)l.Role == LayerRole.Marciapiedi).SelectMany(l => l.Polys).Select(p => (NtsGeometry)Valid(p.Poly)).ToList();
            if (src.Count > 0)
            {
                var n = new Node { Name = "Marciapiedi esistenti" };
                NtsGeometry u;
                try { u = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(src); } catch { u = Gf.BuildGeometry(src); }
                if (projectUnion != null)
                    try { u = u.Difference(projectUnion.Buffer(0.02)); } catch { }
                foreach (var p in Polys(u))
                {
                    if (p.Area < 0.2) continue;
                    Top(n.Part(MarciapiedeEsistente), p, hc, Planar.For(MarciapiedeEsistente));
                    Walls(p, 0, hc, _ => (n.Part(Cordolo), Cordolo));
                }
                root.Children.Add(n);
            }
        }

        // ── edifici ──
        if (o.Buildings && data != null)
        {
            var vol = data.Layers.Where(l => (LayerRole)l.Role == LayerRole.Edifici3D).ToList();
            var src = vol.Count > 0 ? vol : data.Layers.Where(l => (LayerRole)l.Role == LayerRole.Muri).ToList();
            var n = new Node { Name = "Edifici" };
            foreach (var l in src)
                foreach (var lp in l.Polys)
                {
                    double h = lp.Height is > 0.5 and < 400 ? lp.Height : o.DefaultBuildingHeight;
                    foreach (var p in Polys(Valid(lp.Poly)))
                    {
                        // si scartano le schegge (volumi larghi meno di 80 cm): in 3D diventerebbero lame verticali
                        if (p.Area < 0.5 || 2 * p.Area / p.Length < 0.4) continue;
                        Building(n, p, h, o.Textures);
                        nBuildings++;
                    }
                }
            if (n.Parts.Count > 0) root.Children.Add(n);
        }

        // ── alberi ──
        if (o.Trees && data != null)
        {
            var n = new Node { Name = "Alberi" };
            var blocked = new List<Ring>();
            foreach (var l in data.Layers.Where(l => l.Role != 0 && l.Points.Count > 0 && l.IsTrees))
                foreach (var tp in l.Points)
                {
                    if (!box.Contains(tp)) continue;
                    if (data.InsideBuilding(tp)) continue;
                    if (projectUnion != null && projectUnion.Distance(Gf.CreatePoint(new Coordinate(tp.X, tp.Y))) < 0.6) continue; // l'albero cadrebbe sul progetto
                    Tree(n, tp);
                    nTrees++;
                }
            if (nTrees > 0) root.Children.Add(n);
        }

        // ── lampioni ──
        if (o.StreetLights)
        {
            var n = new Node { Name = "Lampioni" };
            foreach (var e in project.Shown.Where(e => e.Error == null && e.Type == ElementType.Marciapiede && e.Mode == ElementMode.Offset))
            {
                double L = PL.Length(e.Guide);
                for (double s = Math.Min(8, L / 2); s < L - 2; s += o.StreetLightSpacing)
                {
                    var t = PL.TangentAt(e.Guide, s); var nn = t.LeftNormal * e.Side;
                    var pos = PL.PointAt(e.Guide, s) + nn * Math.Max(0.3, e.WidthAt(s) - o.CurbWidth - 0.35);
                    if (data?.InsideBuilding(pos) == true) continue;
                    StreetLight(n, pos, nn, hc);
                }
            }
            if (n.Parts.Count > 0) root.Children.Add(n);
        }

        AddCameras(root, project, box);
        return root;
    }

    static int Variant(Polygon p, int count)
    {
        var c = p.Centroid;
        unchecked { uint h = (uint)((int)Math.Round(c.X * 3) * 73856093 ^ (int)Math.Round(c.Y * 3) * 19349663); return (int)(h % (uint)count); }
    }

    /// <summary>Edificio: piano terra, piani con finestre, cornice di gronda, copertura in coppi.</summary>
    static void Building(Node n, Polygon p, double h, bool textures)
    {
        int k = Variant(p, FacadeVariants);
        var ground = n.Part(FacciataTerra[k]); var upper = n.Part(FacciataPiani[k]); var plain = n.Part(Intonaco[k]);
        bool tall = h >= GroundH + 1.5;
        double cornH = tall && p.Area > 15 && 2 * p.Area / p.Length > 1.5 ? 0.28 : 0;
        double hw = h - cornH; // le pareti arrivano sotto la cornice
        int floors = tall ? Math.Max(1, (int)Math.Round((hw - GroundH) / FloorH)) : 0;
        double fh = tall ? (hw - GroundH) / floors : 0;

        foreach (var e in Edges(p))
        {
            double L = e.A.Distance(e.B);
            if (!textures || L < 2.2 || !tall)
            {
                // muri corti o edifici bassi: solo intonaco
                WallBand(plain, e, 0, hw, 0, L / 3, -hw / 3, 0);
                continue;
            }
            int bays = Math.Max(1, (int)Math.Round(L / BayW));
            WallBand(ground, e, 0, GroundH, 0, bays / 2.0, 0, 1); // la texture del piano terra copre due campate
            WallBand(upper, e, GroundH, hw, 0, bays, 0, floors);
        }

        // copertura (sulla sagoma allargata dalla cornice)
        Polygon roof = p;
        if (cornH > 0)
        {
            try
            {
                var bp = new NetTopologySuite.Operation.Buffer.BufferParameters { JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre, MitreLimit = 2, QuadrantSegments = 1 };
                var buf = p.Buffer(0.22, bp);
                if (buf is Polygon bpoly)
                {
                    roof = bpoly;
                    var cor = n.Part(Cornice);
                    foreach (var e in Edges(bpoly)) WallBand(cor, e, hw, h, 0, 1, 0, 1);
                    foreach (var under in Polys(bpoly.Difference(p))) Top(cor, under, hw, new Planar(0, 1, 1), facingDown: true);
                }
            }
            catch { roof = p; }
        }
        // coppi allineati al lato più lungo
        double ang = 0, best = 0;
        var rc = p.ExteriorRing.Coordinates;
        for (int i = 0; i < rc.Length - 1; i++) { double d = rc[i].Distance(rc[i + 1]); if (d > best) { best = d; ang = Math.Atan2(rc[i + 1].Y - rc[i].Y, rc[i + 1].X - rc[i].X); } }
        Top(n.Part(Copertura), roof, h, Planar.For(Copertura, ang + Math.PI / 2));
    }

    static double Rnd(V2 p, int salt)
    {
        unchecked
        {
            uint h = (uint)((int)Math.Round(p.X * 10) * 73856093 ^ (int)Math.Round(p.Y * 10) * 19349663 ^ salt * 83492791);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
        }
    }

    // icosaedro unitario (per le chiome)
    static readonly V3[] IcoV;
    static readonly int[] IcoF;
    static Export3D()
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        var v = new List<V3> { new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1) };
        var f = new List<int> { 0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8, 3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1 };
        // una suddivisione: 80 facce
        var mid = new Dictionary<(int, int), int>();
        int Mid(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (mid.TryGetValue(key, out var i)) return i;
            v.Add(((v[a] + v[b]) * 0.5).Norm()); return mid[key] = v.Count - 1;
        }
        for (int i = 0; i < v.Count; i++) v[i] = v[i].Norm();
        var f2 = new List<int>();
        for (int i = 0; i < f.Count; i += 3)
        {
            int a = f[i], b = f[i + 1], c = f[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            f2.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
        }
        IcoV = v.ToArray(); IcoF = f2.ToArray();
    }

    /// <summary>Albero: tronco e chioma irregolare, dimensioni variabili ma ripetibili.</summary>
    static void Tree(Node n, V2 p)
    {
        double s = 0.75 + Rnd(p, 1) * 0.6;           // scala
        double trunkH = 2.4 * s, rx = 2.3 * s, ry = 2.8 * s;
        Prism(n.Part(Tronco), p, 0.16 * s + 0.05, 6, 0, trunkH + ry * 0.5, cap: false);
        var crown = n.Part(Chiome[(int)(Rnd(p, 2) * Chiome.Length) % Chiome.Length]);
        var c = P(p.X, p.Y, trunkH + ry * 0.9);
        var pts = new V3[IcoV.Length];
        for (int i = 0; i < IcoV.Length; i++)
        {
            var d = IcoV[i];
            double j = 0.82 + 0.36 * Rnd(new V2(p.X + d.X * 7, p.Y + d.Z * 7 + d.Y * 3), 3);
            pts[i] = c + new V3(d.X * rx * j, d.Y * ry * j, d.Z * rx * j);
        }
        for (int i = 0; i < IcoF.Length; i += 3)
        {
            int a = IcoF[i], b = IcoF[i + 1], cc = IcoF[i + 2];
            var hint = ((pts[a] + pts[b] + pts[cc]) * (1.0 / 3)) - c;
            crown.TriSmooth(pts[a], pts[b], pts[cc], IcoV[a], IcoV[b], IcoV[cc], hint);
        }
    }

    /// <summary>Lampione stradale: palo, sbraccio verso la strada, armatura.</summary>
    static void StreetLight(Node n, V2 pos, V2 towardRoad, double baseH)
    {
        var m = n.Part(Metallo);
        const double H = 6.5, arm = 1.1;
        Prism(m, pos, 0.11, 8, baseH, baseH + 0.6);           // basamento
        Prism(m, pos, 0.065, 8, baseH + 0.6, baseH + H);       // palo
        var d = towardRoad.Norm();
        Box(m, pos + d * (arm / 2), d, arm + 0.1, 0.07, baseH + H - 0.08, baseH + H);
        var head = pos + d * arm;
        Box(m, head, d, 0.62, 0.26, baseH + H - 0.18, baseH + H - 0.02);
        Box(n.Part(VetroLampada), head, d, 0.5, 0.2, baseH + H - 0.21, baseH + H - 0.18);
    }

    static void BuildElement(Node n, Element e, NtsGeometry geom, Export3DOptions o, GpkgData? data)
    {
        double hc = e.HasHeight ? e.Height : o.CurbHeight;
        bool raised = e.Type is ElementType.Marciapiede or ElementType.Aiuola
                      || (e.Type == ElementType.Ciclabile && o.RaisedBikeLane);
        var surface = e.Type switch
        {
            ElementType.Marciapiede => Marciapiede,
            ElementType.Ciclabile or ElementType.CorsiaCiclabile => Ciclabile,
            ElementType.Separatore => SegnaleticaGialla,
            ElementType.Aiuola => Verde,
            ElementType.Parcheggio => Parcheggio,
            _ => Asfalto
        };
        double h = raised ? hc : e.Type == ElementType.Separatore ? 0.015 : 0.005;
        double hm = h + 0.012; // quota della segnaletica / del cordolo
        double ang = Angle(e.Inner);

        foreach (var p in Polys(geom))
        {
            Top(n.Part(surface), p, h, Planar.For(surface, ang));
            if (raised)
                Walls(p, 0, h, w =>
                {
                    var mid = new V2((w.A.X + w.B.X) / 2, (w.A.Y + w.B.Y) / 2);
                    bool outer = e.Outer.Count >= 2 && PL.Project(e.Outer, mid).Distance < 0.05;
                    var mat = outer || e.Type == ElementType.Aiuola ? Cordolo : surface;
                    return (n.Part(mat), mat);
                });
        }

        // cordolo in testa sul bordo esterno
        if (raised && e.Mode == ElementMode.Offset && e.Outer.Count >= 2)
        {
            var inner = PL.OffsetProfile(e.Outer, _ => o.CurbWidth, -e.Side, Array.Empty<double>(), 2);
            var ring = new List<V2>(e.Outer); for (int i = inner.Count - 1; i >= 0; i--) ring.Add(inner[i]);
            if (ToPoly(PL.Dedup(ring)) is { } k)
                foreach (var p in Polys(Valid(k))) Top(n.Part(Cordolo), p, h + 0.01, Planar.For(Cordolo, ang));
        }

        const double W = 0.12; // larghezza strisce (m)
        if (o.Markings)
        {
            var mk = n.Part(Segnaletica);
            switch (e.Type)
            {
                case ElementType.Ciclabile when e.Mode == ElementMode.Offset && !e.Centered:
                {
                    var edge = PL.OffsetProfile(e.Guide, t => Math.Max(0.1, e.WidthAt(t) - W / 2 - 0.05), e.Side, e.WidthBreaks, 5);
                    Stripe(mk, edge, W, hm);
                    break;
                }
                case ElementType.CorsiaCiclabile when e.Mode == ElementMode.Offset && !e.Centered:
                    // striscia bianca continua verso la carreggiata (in Fill su entrambi i lati non serve: la delimitano gli altri elementi)
                    Stripe(mk, PL.OffsetProfile(e.Guide, t => Math.Max(0.1, e.WidthAt(t) - W / 2), e.Side, e.WidthBreaks, 5), W, hm);
                    break;
                case ElementType.CorsiaDoppia:
                {
                    if (e.Mode == ElementMode.Offset && !e.Centered)
                        Dashed(mk, PL.OffsetProfile(e.Guide, t => Math.Max(0.1, e.WidthAt(t) - W / 2), e.Side, e.WidthBreaks, 5), W, 4.5, 7.5, hm);
                    break;
                }
                case ElementType.Corsia when e.Mode == ElementMode.Offset && !e.Centered:
                    Dashed(mk, PL.OffsetProfile(e.Guide, t => Math.Max(0.1, e.WidthAt(t) - W / 2), e.Side, e.WidthBreaks, 5), W, 4.5, 7.5, hm);
                    break;
                case ElementType.Parcheggio when e.HasStalls:
                    foreach (var line in e.StallMarkings()) if (line.Count >= 2) Stripe(mk, line, W, hm);
                    break;
            }
        }

        // strisce di mezzeria (numero e tipo scelti per l'elemento, o automatiche)
        if (o.Markings)
        {
            var mk = n.Part(Segnaletica);
            bool bike = e.Type is ElementType.Ciclabile or ElementType.CorsiaCiclabile;
            foreach (var line in e.DividerLines())
            {
                if (line.Count < 2) continue;
                switch (e.DividerStyle)
                {
                    case DividerStyle.Continua: Stripe(mk, line, W, hm); break;
                    case DividerStyle.DoppiaContinua:
                        Stripe(mk, PL.OffsetProfile(line, _ => 0.1, 1, Array.Empty<double>(), 1), W, hm);
                        Stripe(mk, PL.OffsetProfile(line, _ => 0.1, -1, Array.Empty<double>(), 1), W, hm);
                        break;
                    default:
                        if (bike) Dashed(mk, line, W, 1.0, 1.0, hm);
                        else Dashed(mk, line, W, 4.5, 7.5, hm);   // striscia discontinua urbana 4,5 / 7,5 m
                        break;
                }
            }
        }

        // pittogrammi della bicicletta ogni 25 m (due, in versi opposti, se la pista è bidirezionale)
        if (o.BikeSymbols && e.Type is ElementType.Ciclabile or ElementType.CorsiaCiclabile && e.Mode == ElementMode.Offset)
        {
            var sym = n.Part(SimboloBici);
            double L = PL.Length(e.Guide);
            bool bidir = e.Type == ElementType.Ciclabile && e.EffectiveDividers >= 1;
            for (double s = Math.Min(6, L / 2); s < L - 1; s += 25)
            {
                var t = PL.TangentAt(e.Guide, s); var nn = t.LeftNormal * e.Side;
                double w = e.WidthAt(s);
                var g = PL.PointAt(e.Guide, s);
                if (bidir)
                {
                    double size = Math.Min(1.2, w / 2 - 0.25);
                    Decal(sym, g + nn * (w * 0.25), t, size * 0.75, size * 1.25, hm + 0.002);
                    Decal(sym, g + nn * (w * 0.75) + t * 3, t * -1, size * 0.75, size * 1.25, hm + 0.002);
                }
                else
                {
                    double size = Math.Min(1.4, w - 0.4);
                    Decal(sym, g + nn * (w * 0.5), t, size * 0.75, size * 1.25, hm + 0.002);
                }
            }
        }
    }

    static double[] Yaw(double dirX, double dirZ, double pitch = 0)
    {
        // la camera (e la luce) guardano lungo -Z: rotazione attorno a Y verso la direzione voluta, poi beccheggio
        double yaw = Math.Atan2(-dirX, -dirZ);
        double cy = Math.Cos(yaw / 2), sy = Math.Sin(yaw / 2), cp = Math.Cos(pitch / 2), sp = Math.Sin(pitch / 2);
        return new[] { cy * sp, sy * cp, -sy * sp, cy * cp };
    }

    static void AddCameras(Node root, Project p, BBox box)
    {
        JsonObject Cam(double fovDeg) => new()
        {
            ["type"] = "perspective",
            ["perspective"] = new JsonObject { ["yfov"] = fovDeg * Math.PI / 180, ["znear"] = 0.1, ["zfar"] = 5000, ["aspectRatio"] = 16.0 / 9 }
        };
        // a terra: sulla pista ciclabile (punto di vista del ciclista), altrimenti in carreggiata o sul primo elemento
        var bike = p.Shown.FirstOrDefault(e => e.Error == null && e.Type == ElementType.Ciclabile && e.Mode == ElementMode.Offset);
        var road = p.Shown.FirstOrDefault(e => e.Error == null && e.Type == ElementType.Carreggiata);
        var line = road?.Inner ?? bike?.Inner ?? p.Shown.FirstOrDefault(e => e.Error == null)?.Inner
                   ?? p.Edges.FirstOrDefault(e => e.Points.Count >= 2)?.Points;
        if (line is { Count: >= 2 })
        {
            double L = PL.Length(line);
            V2 start; V2 t; string name;
            if (bike != null)
            {
                double Lb = PL.Length(bike.Guide);
                double s0 = Math.Min(3, Lb * 0.05);
                start = PL.PointAt(bike.Guide, s0) + PL.TangentAt(bike.Guide, s0).LeftNormal * (bike.Side * bike.WidthAt(s0) * 0.3);
                t = PL.TangentAt(bike.Guide, Math.Min(15, Lb / 2));
                name = "Vista ciclista";
            }
            else
            {
                start = PL.PointAt(line, Math.Min(3, L * 0.05));
                if (road != null) start = (start + PL.Project(road.Outer, start).Point) * 0.5;
                t = PL.TangentAt(line, Math.Min(15, L / 2));
                name = "Vista a piedi";
            }
            var pos = P(start.X, start.Y, bike != null ? 1.6 : 1.7);
            root.Children.Add(new Node { Name = name, Camera = Cam(60), Translation = new[] { pos.X, pos.Y, pos.Z }, Rotation = Yaw(t.X, -t.Y) });

            var mid = PL.PointAt(line, L / 2);
            var tm = PL.TangentAt(line, L / 2);
            double hor = Math.Max(30, L * 0.35);
            var back = mid - tm * (hor * 0.8) + tm.LeftNormal * (hor * 0.35);
            double height = Math.Max(45, L * 0.45);
            var ap = P(back.X, back.Y, height);
            var dir = (mid - back).Norm();
            double pitch = -Math.Atan2(height, V2.Dist(mid, back));
            root.Children.Add(new Node { Name = "Vista dall'alto", Camera = Cam(50), Translation = new[] { ap.X, ap.Y, ap.Z }, Rotation = Yaw(dir.X, -dir.Y, pitch) });
        }
        else
        {
            var c = new V2((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
            var ap = P(c.X, c.Y - 100, 80);
            root.Children.Add(new Node { Name = "Vista dall'alto", Camera = Cam(45), Translation = new[] { ap.X, ap.Y, ap.Z }, Rotation = Yaw(0, -1, -0.6) });
        }
    }

    // ───────────── scrittura GLB ─────────────

    static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static Export3DResult Write(string path, Project project, GpkgData? data, Export3DOptions o)
    {
        var root = BuildScene(project, data, o, out int nEl, out int nBu, out int nTr);
        if (o.Sun)
        {
            // sole da sud-ovest, alto circa 40°
            root.Children.Add(new Node { Name = "Sole", Light = 0, Rotation = Yaw(0.62, -0.78, -0.72) });
        }

        var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var cameras = new JsonArray();
        var mats = new List<Mat>();
        var materials = new JsonArray();
        var images = new JsonArray();
        var textures = new JsonArray();
        var texIndex = new Dictionary<string, int>();
        int tris = 0;

        int View(byte[] bytes, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            long off = bin.Length;
            bin.Write(bytes);
            var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = bytes.Length };
            if (target is { } t) v["target"] = t;
            bufferViews.Add(v);
            return bufferViews.Count - 1;
        }

        int Texture(string key)
        {
            if (texIndex.TryGetValue(key, out var i)) return i;
            var png = MakeTexture(key).Png();
            images.Add(new JsonObject { ["name"] = key, ["bufferView"] = View(png, null), ["mimeType"] = "image/png" });
            textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = images.Count - 1, ["name"] = key });
            return texIndex[key] = textures.Count - 1;
        }

        int MatIndex(Mat m)
        {
            int i = mats.IndexOf(m);
            if (i >= 0) return i;
            var c = Avalonia.Media.Color.Parse(m.Hex);
            bool tex = o.Textures && m.TexKey != null;
            var pbr = new JsonObject
            {
                ["baseColorFactor"] = tex ? new JsonArray(1.0, 1.0, 1.0, 1.0) : new JsonArray(Lin(c.R / 255.0), Lin(c.G / 255.0), Lin(c.B / 255.0), 1.0),
                ["metallicFactor"] = m.Metal, ["roughnessFactor"] = m.Rough
            };
            if (tex) pbr["baseColorTexture"] = new JsonObject { ["index"] = Texture(m.TexKey!) };
            var jm = new JsonObject { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr };
            if (m.AlphaMask) { jm["alphaMode"] = "MASK"; jm["alphaCutoff"] = 0.5; }
            if (m.DoubleSided) jm["doubleSided"] = true;
            materials.Add(jm);
            mats.Add(m);
            return mats.Count - 1;
        }

        int Accessor(List<float> f, int comps, bool withMinMax)
        {
            var bytes = new byte[f.Count * 4];
            Buffer.BlockCopy(f.ToArray(), 0, bytes, 0, bytes.Length);
            var a = new JsonObject
            {
                ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = f.Count / comps,
                ["type"] = comps == 3 ? "VEC3" : "VEC2"
            };
            if (withMinMax)
            {
                var mn = new double[comps]; var mx = new double[comps];
                for (int k = 0; k < comps; k++) { mn[k] = double.MaxValue; mx[k] = double.MinValue; }
                for (int i = 0; i < f.Count; i += comps)
                    for (int k = 0; k < comps; k++) { mn[k] = Math.Min(mn[k], f[i + k]); mx[k] = Math.Max(mx[k], f[i + k]); }
                a["min"] = new JsonArray(mn.Select(v => (JsonNode)(float)v).ToArray());
                a["max"] = new JsonArray(mx.Select(v => (JsonNode)(float)v).ToArray());
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        int Indices(List<uint> idx)
        {
            var bytes = new byte[idx.Count * 4];
            Buffer.BlockCopy(idx.ToArray(), 0, bytes, 0, bytes.Length);
            accessors.Add(new JsonObject { ["bufferView"] = View(bytes, 34963), ["componentType"] = 5125, ["count"] = idx.Count, ["type"] = "SCALAR" });
            return accessors.Count - 1;
        }

        int AddNode(Node n)
        {
            var jn = new JsonObject { ["name"] = n.Name };
            int idx = nodes.Count;
            nodes.Add(jn);
            var prims = new JsonArray();
            foreach (var (m, part) in n.Parts)
            {
                if (part.Tris == 0) continue;
                tris += part.Tris;
                var attrs = new JsonObject { ["POSITION"] = Accessor(part.Pos, 3, true), ["NORMAL"] = Accessor(part.Nrm, 3, false) };
                if (o.Textures && m.TexKey != null) attrs["TEXCOORD_0"] = Accessor(part.Tex, 2, false);
                prims.Add(new JsonObject { ["attributes"] = attrs, ["indices"] = Indices(part.Idx), ["material"] = MatIndex(m), ["mode"] = 4 });
            }
            if (prims.Count > 0)
            {
                meshes.Add(new JsonObject { ["name"] = n.Name, ["primitives"] = prims });
                jn["mesh"] = meshes.Count - 1;
            }
            if (n.Camera != null) { cameras.Add(n.Camera); jn["camera"] = cameras.Count - 1; }
            if (n.Light is { } li) jn["extensions"] = new JsonObject { ["KHR_lights_punctual"] = new JsonObject { ["light"] = li } };
            if (n.Translation != null) jn["translation"] = new JsonArray(n.Translation.Select(v => (JsonNode)v).ToArray());
            if (n.Rotation != null) jn["rotation"] = new JsonArray(n.Rotation.Select(v => (JsonNode)v).ToArray());
            if (n.Children.Count > 0)
            {
                var ch = new JsonArray();
                foreach (var c in n.Children) ch.Add(AddNode(c));
                jn["children"] = ch;
            }
            return idx;
        }

        int rootIdx = AddNode(root);
        while (bin.Length % 4 != 0) bin.WriteByte(0);

        var extras = new JsonObject
        {
            ["programma"] = "CicloDraft",
            ["unita"] = "metri",
            ["assi"] = "X = Est, Y = quota, -Z = Nord",
            ["origine_E"] = data?.Origin.X ?? 0,
            ["origine_N"] = data?.Origin.Y ?? 0,
            ["sistema_riferimento"] = data?.Crs ?? "",
            ["nota"] = "Coordinate reali: E = X + origine_E, N = -Z + origine_N. Quote relative al piano stradale."
        };
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "CicloDraft", ["extras"] = extras },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["name"] = "Scena", ["nodes"] = new JsonArray(rootIdx) }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length })
        };
        if (cameras.Count > 0) gltf["cameras"] = cameras;
        if (images.Count > 0)
        {
            gltf["images"] = images;
            gltf["textures"] = textures;
            gltf["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }
        if (o.Sun)
        {
            gltf["extensionsUsed"] = new JsonArray("KHR_lights_punctual");
            gltf["extensions"] = new JsonObject
            {
                ["KHR_lights_punctual"] = new JsonObject
                {
                    ["lights"] = new JsonArray(new JsonObject { ["name"] = "Sole", ["type"] = "directional", ["color"] = new JsonArray(1.0, 0.96, 0.9), ["intensity"] = 3.0 })
                }
            };
        }

        var json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        int jsonPad = (4 - json.Length % 4) % 4;
        var binBytes = bin.ToArray();
        int total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length;
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
            w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au);
            w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
            w.Write((uint)binBytes.Length); w.Write(0x004E4942u);
            w.Write(binBytes);
        }
        return new Export3DResult(path, tris, nEl, nBu, nTr, new FileInfo(path).Length);
    }
}
