using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;
using SkiaSharp;

namespace CicloDraft.Controls;

/// <summary>
/// Vista 3D della mappa (solo da guardare): la stessa scena dell'esportazione 3D, senza texture, disegnata con
/// l'algoritmo del pittore. Gli edifici che coprono gli elementi disegnati si nascondono da soli (resta l'impronta a terra).
/// Trascina = ruota attorno al centro, tasto destro o centrale = sposta, rotella = avvicina/allontana.
/// </summary>
public class View3D : Control
{
    // ── scena: triangoli in coordinate locali (X = Est, Y = Nord, Z = quota) ──
    sealed class Mesh
    {
        public float[] P = Array.Empty<float>();      // 9 valori per triangolo
        public SKColor[] C = Array.Empty<SKColor>();  // colore già ombreggiato
        public float[] N = Array.Empty<float>();      // normale (3 per triangolo)
        public bool[] Tall = Array.Empty<bool>();     // sporge da terra (si ordina dopo le superfici basse)
        public int[] B = Array.Empty<int>();          // edificio di appartenenza (−1 = nessuno)
        public float[] Z = Array.Empty<float>();      // quota più alta del triangolo
        public float[] A = Array.Empty<float>();      // superficie in pianta dell'oggetto a cui appartiene
        public int Count => C.Length;
    }

    sealed class Bld { public V2[] Ring = Array.Empty<V2>(); public double H; public BBox Box; }

    Mesh _proj = new(), _bldMesh = new(), _foot = new();
    List<Bld> _blds = new();
    List<(double x, double y, double z)> _samples = new();
    bool[] _hidden = Array.Empty<bool>();
    GpkgData? _sceneData;
    string _projSig = "";
    bool _building;

    Editor? _ed;
    public Editor? Editor
    {
        get => _ed;
        set
        {
            if (_ed != null) _ed.StructureChanged -= OnChanged;
            _ed = value;
            if (_ed != null) _ed.StructureChanged += OnChanged;
        }
    }

    // ── camera: punto guardato (a terra), distanza, direzione (gradi da nord, orario), inclinazione ──
    public double Tx, Ty, Dist = 120, Yaw, Pitch = 40;
    const double Fov = 50;
    public int HiddenCount => _hidden.Count(h => h);
    public int TriangleCount => _proj.Count + _bldMesh.Count;

    public View3D() { ClipToBounds = true; Focusable = true; }

    void OnChanged() { if (IsVisible) _ = Rebuild(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) _ = Rebuild();
    }

    /// <summary>Inquadra il progetto (o i dati) partendo dalla vista 2D: stesso centro e stessa direzione dell'alto.</summary>
    /// <summary>Entrando in 3D: stessa direzione dell'alto della mappa 2D, inquadrando il progetto (o il centro della vista).</summary>
    public void StartFrom(double cx, double cy, double rotDeg, double widthM)
    {
        Tx = cx; Ty = cy; Yaw = rotDeg; Pitch = 40;
        Dist = Math.Clamp(widthM * 0.75, 15, 3000);
        Fit();
    }

    public void Fit()
    {
        if (_ed == null) return;
        var pts = _ed.Project.Shown.Where(e => e.Polygon.Count >= 3).SelectMany(e => e.Polygon).ToList();
        if (pts.Count == 0 && _ed.Data != null) pts = new() { new V2(_ed.Data.Box.MinX, _ed.Data.Box.MinY), new V2(_ed.Data.Box.MaxX, _ed.Data.Box.MaxY) };
        if (pts.Count == 0) return;
        var b = BBox.Of(pts);
        Tx = (b.MinX + b.MaxX) / 2; Ty = (b.MinY + b.MaxY) / 2;
        Dist = Math.Clamp(Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) * 0.75, 15, 3000);
        InvalidateVisual();
    }

    public void RotateBy(double deg) { Yaw = (Yaw + deg) % 360; InvalidateVisual(); }

    // ───────────── costruzione della scena ─────────────

    static string Signature(Project p)
    {
        var h = new HashCode();
        foreach (var e in p.Shown)
        {
            h.Add(e.Id); h.Add((int)e.Type); h.Add(e.Polygon.Count); h.Add(e.Height); h.Add(e.Separated); h.Add(e.Dividers); h.Add((int)e.DividerStyle);
            foreach (var q in e.Polygon) { h.Add(Math.Round(q.X, 2)); h.Add(Math.Round(q.Y, 2)); }
        }
        return h.ToHashCode().ToString();
    }

    public async Task Rebuild()
    {
        if (_ed == null || _building) return;
        var data = _ed.Data; var proj = _ed.Project;
        var sig = Signature(proj);
        bool needB = data != _sceneData, needP = sig != _projSig || needB;
        if (!needB && !needP) return;
        _building = true;
        try
        {
            var o = new Export3DOptions { Textures = false, Ground = false, BikeSymbols = false, Sun = false, Trees = true, StreetLights = true, Buildings = false };
            // il progetto si legge qui (thread dell'interfaccia): la scena si calcola su una copia
            var copy = ProjectFile.ToProject(ProjectFile.FromJson(ProjectFile.ToJson("vista3d.ciclo", proj, data)), data?.Origin ?? new V2(0, 0));
            copy.Regenerate();
            var pm = await Task.Run(() => ToMesh(Export3D.BuildScene(copy, data, o, out _, out _, out _), -1));
            var samples = SamplesOf(copy);
            if (needB)
            {
                var (bm, foot, blds) = await Task.Run(() =>
                {
                    var list = data != null ? Export3D.BuildingNodes(data, o) : new();
                    var meshes = new List<Mesh>(); var fl = new List<Mesh>(); var bl = new List<Bld>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        meshes.Add(ToMesh(list[i].Node, i));
                        bl.Add(new Bld { Ring = list[i].Ring, H = list[i].H, Box = BBox.Of(list[i].Ring) });
                        fl.Add(Footprint(list[i].Ring, i));
                    }
                    return (Concat(meshes), Concat(fl), bl);
                });
                _bldMesh = bm; _foot = foot; _blds = blds; _hidden = new bool[blds.Count]; _sceneData = data;
                BuildGrid();
            }
            _proj = pm; _samples = samples; _projSig = sig;
        }
        finally { _building = false; }
        InvalidateVisual();
        if (Signature(_ed.Project) != _projSig) _ = Rebuild();
    }

    static readonly (double x, double y, double z) Sun = Norm(-0.45, -0.55, 0.75);
    static (double, double, double) Norm(double x, double y, double z) { var l = Math.Sqrt(x * x + y * y + z * z); return (x / l, y / l, z / l); }

    static SKColor Shade(SKColor c, double nx, double ny, double nz)
    {
        double d = Math.Max(0, nx * Sun.x + ny * Sun.y + nz * Sun.z);
        double k = 0.58 + 0.42 * d;
        return new SKColor((byte)Math.Min(255, c.Red * k), (byte)Math.Min(255, c.Green * k), (byte)Math.Min(255, c.Blue * k));
    }

    /// <summary>Dalla scena glTF (Y in alto, −Z = Nord) ai triangoli della vista.</summary>
    static Mesh ToMesh(Export3D.Node root, int building)
    {
        var P = new List<float>(); var C = new List<SKColor>(); var N = new List<float>(); var T = new List<bool>(); var B = new List<int>();
        var Z = new List<float>(); var A = new List<float>();
        void Walk(Export3D.Node n, float inherited)
        {
            // superficie in pianta dell'oggetto (un elemento del progetto): gli oggetti più piccoli si disegnano sopra i più grandi
            double area = 0;
            foreach (var part in n.Parts.Values)
                for (int t = 0; t + 2 < part.Idx.Count; t += 3)
                {
                    int i0 = 3 * (int)part.Idx[t], i1 = 3 * (int)part.Idx[t + 1], i2 = 3 * (int)part.Idx[t + 2];
                    double ax = part.Pos[i1] - part.Pos[i0], az = part.Pos[i1 + 2] - part.Pos[i0 + 2];
                    double bx = part.Pos[i2] - part.Pos[i0], bz = part.Pos[i2 + 2] - part.Pos[i0 + 2];
                    area += Math.Abs(ax * bz - az * bx) / 2;
                }
            float own = area > 0.01 ? (float)area : inherited;
            foreach (var (mat, part) in n.Parts)
            {
                if (mat.AlphaMask) continue;
                var col = SKColor.Parse(mat.Hex);
                var pos = part.Pos; var nrm = part.Nrm; var idx = part.Idx;
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    double nx = 0, ny = 0, nz = 0, zmax = double.MinValue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = (int)idx[t + k];
                        float x = pos[3 * v], z = pos[3 * v + 1], y = -pos[3 * v + 2];
                        P.Add(x); P.Add(y); P.Add(z);
                        nx += nrm[3 * v]; nz += nrm[3 * v + 1]; ny -= nrm[3 * v + 2];
                        zmax = Math.Max(zmax, z);
                    }
                    var (ux, uy, uz) = Norm(nx, ny, nz + 1e-12);
                    N.Add((float)ux); N.Add((float)uy); N.Add((float)uz);
                    C.Add(Shade(col, ux, uy, uz));
                    T.Add(zmax > 0.4);
                    B.Add(building);
                    Z.Add((float)zmax); A.Add(own);
                }
            }
            foreach (var c in n.Children) Walk(c, own);
        }
        Walk(root, 1e9f);
        return new Mesh { P = P.ToArray(), C = C.ToArray(), N = N.ToArray(), Tall = T.ToArray(), B = B.ToArray(), Z = Z.ToArray(), A = A.ToArray() };
    }

    static readonly SKColor FootColor = new(0xD9, 0xD6, 0xCF);

    /// <summary>Impronta a terra dell'edificio nascosto.</summary>
    static Mesh Footprint(V2[] ring, int building)
    {
        var P = new List<float>();
        try
        {
            var gf = new NetTopologySuite.Geometries.GeometryFactory();
            var poly = gf.CreatePolygon(ring.Append(ring[0]).Select(q => new NetTopologySuite.Geometries.Coordinate(q.X, q.Y)).ToArray());
            var tris = NetTopologySuite.Triangulate.Polygon.PolygonTriangulator.Triangulate(poly);
            for (int i = 0; i < tris.NumGeometries; i++)
            {
                var c = tris.GetGeometryN(i).Coordinates;
                for (int k = 0; k < 3; k++) { P.Add((float)c[k].X); P.Add((float)c[k].Y); P.Add(0.02f); }
            }
        }
        catch { }
        int n = P.Count / 9;
        return new Mesh
        {
            P = P.ToArray(), C = Enumerable.Repeat(FootColor, n).ToArray(), N = Enumerable.Range(0, n).SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray(),
            Tall = new bool[n], B = Enumerable.Repeat(building, n).ToArray(), Z = Enumerable.Repeat(0.02f, n).ToArray(), A = Enumerable.Repeat(1e9f, n).ToArray()
        };
    }

    static Mesh Concat(List<Mesh> ms) => new()
    {
        P = ms.SelectMany(m => m.P).ToArray(), C = ms.SelectMany(m => m.C).ToArray(), N = ms.SelectMany(m => m.N).ToArray(),
        Tall = ms.SelectMany(m => m.Tall).ToArray(), B = ms.SelectMany(m => m.B).ToArray(),
        Z = ms.SelectMany(m => m.Z).ToArray(), A = ms.SelectMany(m => m.A).ToArray()
    };

    /// <summary>Punti degli elementi disegnati da tenere in vista (contorni ogni 2 m e interno).</summary>
    static List<(double, double, double)> SamplesOf(Project p)
    {
        var res = new List<(double, double, double)>();
        var els = p.Shown.Where(e => e.Error == null && e.Polygon.Count >= 3).ToList();
        double per = els.Sum(e => PL.Length(e.Polygon) + V2.Dist(e.Polygon[^1], e.Polygon[0]));
        double step = Math.Max(2, per / 2500);
        foreach (var e in els)
        {
            double z = e.HasHeight ? e.Height + 0.05 : 0.1;
            var r = e.Polygon;
            for (int i = 0; i < r.Count; i++)
            {
                V2 a = r[i], b = r[(i + 1) % r.Count];
                int n = Math.Max(1, (int)(V2.Dist(a, b) / step));
                for (int k = 0; k < n; k++) { var q = a + (b - a) * (k / (double)n); res.Add((q.X, q.Y, z)); }
            }
        }
        return res;
    }

    // ── griglia degli edifici per il test di visibilità ──
    const double Cell = 8;
    Dictionary<(int, int), List<int>> _grid = new();
    void BuildGrid()
    {
        _grid = new();
        for (int i = 0; i < _blds.Count; i++)
        {
            var b = _blds[i].Box;
            for (int gx = (int)Math.Floor(b.MinX / Cell); gx <= (int)Math.Floor(b.MaxX / Cell); gx++)
                for (int gy = (int)Math.Floor(b.MinY / Cell); gy <= (int)Math.Floor(b.MaxY / Cell); gy++)
                {
                    if (!_grid.TryGetValue((gx, gy), out var l)) _grid[(gx, gy)] = l = new();
                    l.Add(i);
                }
        }
    }

    /// <summary>Il segmento dalla fotocamera al punto attraversa il volume dell'edificio?</summary>
    static bool Blocks(Bld b, double cx, double cy, double cz, double sx, double sy, double sz)
    {
        double dx = sx - cx, dy = sy - cy;
        var ts = new List<double> { 0, 1 };
        var r = b.Ring;
        for (int i = 0; i < r.Length; i++)
        {
            V2 a = r[i], c = r[(i + 1) % r.Length];
            double ex = c.X - a.X, ey = c.Y - a.Y;
            double den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-12) continue;
            double t = ((a.X - cx) * ey - (a.Y - cy) * ex) / den;
            double u = ((a.X - cx) * dy - (a.Y - cy) * dx) / den;
            if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
        }
        ts.Sort();
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            double t0 = ts[i], t1 = ts[i + 1];
            if (t1 - t0 < 1e-9) continue;
            double tm = (t0 + t1) / 2;
            if (!PL.PointInRing(r, new V2(cx + dx * tm, cy + dy * tm))) continue;
            double z0 = cz + (sz - cz) * t0, z1 = cz + (sz - cz) * t1;
            if (Math.Min(z0, z1) < b.H) return true;
        }
        return false;
    }

    void UpdateHidden(double cx, double cy, double cz)
    {
        if (_hidden.Length != _blds.Count) _hidden = new bool[_blds.Count];
        Array.Clear(_hidden);
        if (_blds.Count == 0 || _samples.Count == 0) return;
        double hmax = _blds.Max(b => b.H);
        var seen = new HashSet<int>();
        foreach (var (sx, sy, sz) in _samples)
        {
            // solo il tratto del raggio sotto l'edificio più alto può essere coperto
            double t0 = cz > hmax && cz > sz ? Math.Clamp((cz - hmax) / (cz - sz), 0, 1) : 0;
            double ax = cx + (sx - cx) * t0, ay = cy + (sy - cy) * t0;
            double len = Math.Sqrt((sx - ax) * (sx - ax) + (sy - ay) * (sy - ay));
            int steps = Math.Max(1, (int)(len / (Cell * 0.5)));
            seen.Clear();
            for (int k = 0; k <= steps; k++)
            {
                double x = ax + (sx - ax) * k / steps, y = ay + (sy - ay) * k / steps;
                if (!_grid.TryGetValue(((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell)), out var l)) continue;
                foreach (var bi in l)
                {
                    if (_hidden[bi] || !seen.Add(bi)) continue;
                    if (Blocks(_blds[bi], cx, cy, cz, sx, sy, sz)) _hidden[bi] = true;
                }
            }
        }
    }

    // ───────────── disegno ─────────────

    (double x, double y, double z) Eye()
    {
        double yr = Yaw * Math.PI / 180, pr = Pitch * Math.PI / 180;
        double fx = Math.Cos(pr) * Math.Sin(yr), fy = Math.Cos(pr) * Math.Cos(yr), fz = -Math.Sin(pr);
        return (Tx - fx * Dist, Ty - fy * Dist, -fz * Dist);
    }

    public double LastMs, LastHideMs;
    public override void Render(DrawingContext ctx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#FFE9E7E1")), r);
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var eye = Eye();
        UpdateHidden(eye.x, eye.y, eye.z);
        double th = sw.Elapsed.TotalMilliseconds;
        var fr = Frame(eye);
        LastMs = sw.Elapsed.TotalMilliseconds; LastHideMs = th;
        ctx.Custom(new DrawOp(r, fr));
    }

    record FrameData(SKPoint[] Pos, SKColor[] Col, float HorizonY, int Tris);

    FrameData Frame((double x, double y, double z) eye)
    {
        double W = Bounds.Width, H = Bounds.Height;
        double yr = Yaw * Math.PI / 180, pr = Pitch * Math.PI / 180;
        double fx = Math.Cos(pr) * Math.Sin(yr), fy = Math.Cos(pr) * Math.Cos(yr), fz = -Math.Sin(pr);
        double rx = Math.Cos(yr), ry = -Math.Sin(yr), rz = 0;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        double F = H / 2 / Math.Tan(Fov * Math.PI / 360);
        const double Near = 0.5;
        double far = Math.Max(400, Dist * 8);

        var low = new List<(float key, SKPoint a, SKPoint b, SKPoint c, SKColor col, int z, float area)>();
        var tall = new List<(float key, SKPoint a, SKPoint b, SKPoint c, SKColor col, int z, float area)>();
        var cxs = new double[3]; var cys = new double[3]; var czs = new double[3];

        void Add(Mesh m, bool foot)
        {
            for (int t = 0; t < m.Count; t++)
            {
                int bi = m.B[t];
                if (bi >= 0 && bi < _hidden.Length && _hidden[bi] != foot) continue;
                int o = 9 * t;
                // faccia rivolta altrove: non si vede
                double nx = m.N[3 * t], ny = m.N[3 * t + 1], nz = m.N[3 * t + 2];
                if (!foot && nx * (eye.x - m.P[o]) + ny * (eye.y - m.P[o + 1]) + nz * (eye.z - m.P[o + 2]) < 0) continue;
                bool behind = false, allBehind = true;
                double key = 0;
                for (int k = 0; k < 3; k++)
                {
                    double dx = m.P[o + 3 * k] - eye.x, dy = m.P[o + 3 * k + 1] - eye.y, dz = m.P[o + 3 * k + 2] - eye.z;
                    cxs[k] = dx * rx + dy * ry + dz * rz; cys[k] = dx * ux + dy * uy + dz * uz; czs[k] = dx * fx + dy * fy + dz * fz;
                    if (czs[k] < Near) behind = true; else allBehind = false;
                    key += czs[k];
                }
                if (allBehind || key / 3 > far) continue;
                var col = m.C[t];
                int zmm = (int)Math.Round(m.Z[t] * 1000); float ar = m.A[t];
                var list = m.Tall[t] ? tall : low;
                SKPoint S(double x, double y, double z) => new((float)(W / 2 + F * x / z), (float)(H / 2 - F * y / z));
                if (!behind)
                {
                    var a = S(cxs[0], cys[0], czs[0]); var b = S(cxs[1], cys[1], czs[1]); var c = S(cxs[2], cys[2], czs[2]);
                    if (Math.Max(a.X, Math.Max(b.X, c.X)) < 0 || Math.Min(a.X, Math.Min(b.X, c.X)) > W ||
                        Math.Max(a.Y, Math.Max(b.Y, c.Y)) < 0 || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > H) continue;
                    list.Add(((float)key, a, b, c, col, zmm, ar));
                    continue;
                }
                // taglio sul piano vicino (Sutherland–Hodgman), poi a ventaglio
                var poly = new List<(double x, double y, double z)>(4);
                for (int k = 0; k < 3; k++)
                {
                    int j = (k + 1) % 3;
                    bool inK = czs[k] >= Near, inJ = czs[j] >= Near;
                    if (inK) poly.Add((cxs[k], cys[k], czs[k]));
                    if (inK != inJ)
                    {
                        double s = (Near - czs[k]) / (czs[j] - czs[k]);
                        poly.Add((cxs[k] + (cxs[j] - cxs[k]) * s, cys[k] + (cys[j] - cys[k]) * s, Near));
                    }
                }
                for (int k = 1; k + 1 < poly.Count; k++)
                    list.Add(((float)key, S(poly[0].x, poly[0].y, poly[0].z), S(poly[k].x, poly[k].y, poly[k].z), S(poly[k + 1].x, poly[k + 1].y, poly[k + 1].z), col, zmm, ar));
            }
        }
        Add(_foot, true);
        Add(_proj, false);
        Add(_bldMesh, false);

        // prima le superfici a terra, dal basso verso l'alto (a parità di quota gli elementi più piccoli sopra i più grandi,
        // come nella mappa 2D: corsia ciclabile e parcheggi sopra la carreggiata); poi ciò che sporge, dal più lontano al più vicino
        low.Sort((p, q) => p.z != q.z ? p.z.CompareTo(q.z) : p.area != q.area ? q.area.CompareTo(p.area) : q.key.CompareTo(p.key));
        tall.Sort((p, q) => q.key.CompareTo(p.key));
        int n = low.Count + tall.Count;
        var pos = new SKPoint[n * 3]; var cols = new SKColor[n * 3];
        int i = 0;
        foreach (var tr in low.Concat(tall))
        {
            pos[i] = tr.a; pos[i + 1] = tr.b; pos[i + 2] = tr.c;
            cols[i] = cols[i + 1] = cols[i + 2] = tr.col;
            i += 3;
        }
        float horizon = (float)(H / 2 - F * Math.Tan(pr));
        return new FrameData(pos, cols, horizon, n);
    }

    sealed class DrawOp : ICustomDrawOperation
    {
        readonly FrameData _f;
        public DrawOp(Rect b, FrameData f) { Bounds = b; _f = f; }
        public Rect Bounds { get; }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => Bounds.Contains(p);
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } lf) return;
            using var lease = lf.Lease();
            var cv = lease.SkCanvas;
            float w = (float)Bounds.Width, h = (float)Bounds.Height;
            // cielo sopra l'orizzonte, terreno chiaro sotto
            float hy = Math.Clamp(_f.HorizonY, -1, h + 1);
            using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Math.Max(1, hy)), new[] { new SKColor(0xB9, 0xD3, 0xEA), new SKColor(0xE8, 0xEF, 0xF4) }, SKShaderTileMode.Clamp) })
                if (hy > 0) cv.DrawRect(0, 0, w, hy, sky);
            using (var ground = new SKPaint { Color = new SKColor(0xE9, 0xE7, 0xE1) })
                if (hy < h) cv.DrawRect(0, Math.Max(0, hy), w, h - Math.Max(0, hy), ground);
            if (_f.Tris == 0) return;
            using var verts = SKVertices.CreateCopy(SKVertexMode.Triangles, _f.Pos, null, _f.Col);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            cv.DrawVertices(verts, SKBlendMode.Dst, paint);
        }
    }

    // ───────────── mouse ─────────────

    Point? _last; bool _orbit;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pp = e.GetCurrentPoint(this);
        _orbit = pp.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _last = pp.Position;
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_last is not { } l) return;
        var p = e.GetPosition(this);
        double dx = p.X - l.X, dy = p.Y - l.Y;
        _last = p;
        if (_orbit)
        {
            Yaw = (Yaw - dx * 0.35) % 360;
            Pitch = Math.Clamp(Pitch + dy * 0.25, 4, 89);
        }
        else
        {
            double m = Dist * 2 * Math.Tan(Fov * Math.PI / 360) / Math.Max(1, Bounds.Height);
            double yr = Yaw * Math.PI / 180;
            double rx = Math.Cos(yr), ry = -Math.Sin(yr), fx = Math.Sin(yr), fy = Math.Cos(yr);
            double k = 1 / Math.Max(0.25, Math.Sin(Pitch * Math.PI / 180));
            Tx += (-dx * rx + dy * fx * k) * m;
            Ty += (-dx * ry + dy * fy * k) * m;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _last = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Dist = Math.Clamp(Dist * Math.Pow(0.87, e.Delta.Y), 4, 4000);
        InvalidateVisual();
        e.Handled = true;
    }
}
