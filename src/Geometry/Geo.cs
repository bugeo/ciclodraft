namespace CicloDraft.Geometry;

/// <summary>Vettore 2D in metri.</summary>
public readonly record struct V2(double X, double Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(V2 a, double k) => new(a.X * k, a.Y * k);
    public static V2 operator *(double k, V2 a) => new(a.X * k, a.Y * k);
    public double Len => Math.Sqrt(X * X + Y * Y);
    public V2 Norm() { var l = Len; return l < 1e-12 ? new V2(0, 0) : new V2(X / l, Y / l); }
    /// <summary>Normale a sinistra rispetto al verso del vettore.</summary>
    public V2 LeftNormal => new(-Y, X);
    public static double Dot(V2 a, V2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(V2 a, V2 b) => a.X * b.Y - a.Y * b.X;
    public static double Dist(V2 a, V2 b) => (a - b).Len;
    public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###})");
}

public readonly record struct Projection(double Station, double Distance, V2 Point, int Segment);

/// <summary>Operazioni su polilinee aperte (progressive in metri).</summary>
public static class PL
{
    public static double[] Cumulative(IReadOnlyList<V2> p)
    {
        var c = new double[p.Count];
        for (int i = 1; i < p.Count; i++) c[i] = c[i - 1] + V2.Dist(p[i - 1], p[i]);
        return c;
    }

    public static double Length(IReadOnlyList<V2> p)
    {
        double l = 0;
        for (int i = 1; i < p.Count; i++) l += V2.Dist(p[i - 1], p[i]);
        return l;
    }

    public static V2 PointAt(IReadOnlyList<V2> p, double s)
    {
        if (p.Count == 0) return default;
        if (s <= 0 || p.Count == 1) return p[0];
        double acc = 0;
        for (int i = 1; i < p.Count; i++)
        {
            double d = V2.Dist(p[i - 1], p[i]);
            if (acc + d >= s)
            {
                double t = d < 1e-12 ? 0 : (s - acc) / d;
                return p[i - 1] + (p[i] - p[i - 1]) * t;
            }
            acc += d;
        }
        return p[^1];
    }

    /// <summary>Direzione (unitaria) della polilinea alla progressiva s.</summary>
    public static V2 TangentAt(IReadOnlyList<V2> p, double s)
    {
        if (p.Count < 2) return new V2(1, 0);
        double acc = 0;
        for (int i = 1; i < p.Count; i++)
        {
            double d = V2.Dist(p[i - 1], p[i]);
            if (d < 1e-12) continue;
            if (acc + d >= s || i == p.Count - 1) return (p[i] - p[i - 1]).Norm();
            acc += d;
        }
        return (p[^1] - p[0]).Norm();
    }

    /// <summary>Tratto tra s0 e s1. Se s0 &gt; s1 il tratto è restituito invertito.</summary>
    public static List<V2> Sub(IReadOnlyList<V2> p, double s0, double s1)
    {
        bool rev = s0 > s1;
        if (rev) (s0, s1) = (s1, s0);
        var c = Cumulative(p);
        var r = new List<V2> { PointAt(p, s0) };
        for (int i = 1; i < p.Count - 1; i++)
            if (c[i] > s0 + 1e-9 && c[i] < s1 - 1e-9) r.Add(p[i]);
        r.Add(PointAt(p, s1));
        if (rev) r.Reverse();
        return r;
    }

    public static Projection Project(IReadOnlyList<V2> p, V2 q)
    {
        if (p.Count == 0) return new Projection(0, double.MaxValue, q, -1);
        if (p.Count == 1) return new Projection(0, V2.Dist(p[0], q), p[0], 0);
        var best = new Projection(0, double.MaxValue, p[0], 0);
        double acc = 0;
        for (int i = 0; i < p.Count - 1; i++)
        {
            V2 a = p[i], b = p[i + 1], ab = b - a;
            double l2 = V2.Dot(ab, ab);
            double t = l2 < 1e-18 ? 0 : Math.Clamp(V2.Dot(q - a, ab) / l2, 0, 1);
            var pt = a + ab * t;
            double d = V2.Dist(q, pt);
            double seg = Math.Sqrt(l2);
            if (d < best.Distance) best = new Projection(acc + t * seg, d, pt, i);
            acc += seg;
        }
        return best;
    }

    public static List<V2> Dedup(IReadOnlyList<V2> p, double eps = 1e-6)
    {
        var r = new List<V2>(p.Count);
        foreach (var v in p)
            if (r.Count == 0 || V2.Dist(r[^1], v) > eps) r.Add(v);
        return r;
    }

    public static bool SegIntersect(V2 a, V2 b, V2 c, V2 d, out V2 x)
    {
        x = default;
        V2 r = b - a, s = d - c;
        double den = V2.Cross(r, s);
        if (Math.Abs(den) < 1e-14) return false;
        double t = V2.Cross(c - a, s) / den;
        double u = V2.Cross(c - a, r) / den;
        if (t <= 1e-9 || t >= 1 - 1e-9 || u <= 1e-9 || u >= 1 - 1e-9) return false;
        x = a + r * t;
        return true;
    }

    /// <summary>
    /// Elimina i piccoli cappi creati dall'offset in corrispondenza delle rientranze
    /// (es. gradini delle facciate). Un cappo viene tagliato solo se il tratto rimosso è
    /// più corto di maxLoop metri, per non toccare geometrie volutamente lunghe.
    /// </summary>
    public static List<V2> RemoveLoops(List<V2> input, double maxLoop)
    {
        var pts = new List<V2>(input);
        for (int guard = 0; guard < 2000; guard++)
        {
            bool changed = false;
            for (int i = 0; i < pts.Count - 3 && !changed; i++)
            {
                double loopLen = 0;
                int lastJ = -1; V2 lastX = default;
                for (int j = i + 2; j < pts.Count - 1; j++)
                {
                    loopLen += V2.Dist(pts[j - 1], pts[j]);
                    if (loopLen > maxLoop) break;
                    if (SegIntersect(pts[i], pts[i + 1], pts[j], pts[j + 1], out var x)) { lastJ = j; lastX = x; }
                }
                if (lastJ > 0)
                {
                    pts.RemoveRange(i + 1, lastJ - i);
                    pts.Insert(i + 1, lastX);
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return pts;
    }

    /// <summary>Semplificazione Douglas-Peucker (mantiene gli estremi).</summary>
    public static List<V2> Simplify(IReadOnlyList<V2> p, double tol)
    {
        if (p.Count < 3 || tol <= 0) return new List<V2>(p);
        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double dmax = 0; int idx = -1;
            var ab = p[b] - p[a]; double l = ab.Len;
            for (int i = a + 1; i < b; i++)
            {
                double d = l < 1e-12 ? V2.Dist(p[i], p[a]) : Math.Abs(V2.Cross(ab, p[i] - p[a])) / l;
                if (d > dmax) { dmax = d; idx = i; }
            }
            if (idx > 0 && dmax > tol) { keep[idx] = true; stack.Push((a, idx)); stack.Push((idx, b)); }
        }
        var r = new List<V2>();
        for (int i = 0; i < p.Count; i++) if (keep[i]) r.Add(p[i]);
        return r;
    }

    public static V2 SegNormal(IReadOnlyList<V2> b, int i) => (b[i + 1] - b[i]).Norm().LeftNormal;

    /// <summary>
    /// Offset a larghezza variabile. side = +1 a sinistra del verso della linea, -1 a destra.
    /// width(s) restituisce la larghezza alla progressiva s del tratto base.
    /// </summary>
    public static List<V2> OffsetProfile(IReadOnlyList<V2> b, Func<double, double> width, int side,
        IEnumerable<double> extraStations, double maxLoop)
    {
        var cum = Cumulative(b);
        double L = cum[^1];
        var st = new List<(double s, int v)>();
        for (int i = 0; i < b.Count; i++) st.Add((cum[i], i));
        foreach (var e in extraStations)
            if (e > 1e-6 && e < L - 1e-6 && !cum.Any(c => Math.Abs(c - e) < 1e-4)) st.Add((e, -1));
        st.Sort((x, y) => x.s.CompareTo(y.s));

        // linea chiusa (es. bordo tutto attorno a un edificio): il punto di chiusura è uno spigolo come gli altri
        bool closed = b.Count >= 4 && V2.Dist(b[0], b[^1]) < 1e-6;
        var o = new List<V2>(st.Count);
        foreach (var (s, v) in st)
        {
            V2 P, n;
            if (v >= 0)
            {
                P = b[v];
                V2? n1 = v > 0 ? SegNormal(b, v - 1) : closed ? SegNormal(b, b.Count - 2) : null;
                V2? n2 = v < b.Count - 1 ? SegNormal(b, v) : closed ? SegNormal(b, 0) : null;
                if (n1 is { } a && n2 is { } c)
                {
                    var m = (a + c).Norm();
                    if (m.Len < 1e-9) m = a;
                    double cos = V2.Dot(m, a);
                    n = m * (1.0 / Math.Max(cos, 0.25));
                }
                else n = n1 ?? n2 ?? new V2(0, 1);
            }
            else
            {
                P = PointAt(b, s);
                n = TangentAt(b, s).LeftNormal;
            }
            o.Add(P + n * (side * width(s)));
        }
        var res = RemoveLoops(Dedup(o), maxLoop);
        if (closed && res.Count >= 5 && V2.Dist(res[0], res[^1]) < 1e-6)
        {
            // i cappi a cavallo del punto di chiusura: si ripete la pulizia con l'anello ruotato di mezzo giro,
            // poi lo si rimette a partire dal punto più vicino a quello di chiusura originale
            var seam = res[0];
            var ring = res.Take(res.Count - 1).ToList();
            int h = ring.Count / 2;
            var rot = ring.Skip(h).Concat(ring.Take(h)).ToList(); rot.Add(rot[0]);
            rot = RemoveLoops(rot, maxLoop);
            var r2 = rot.Take(rot.Count - 1).ToList();
            if (r2.Count >= 3)
            {
                int k = Enumerable.Range(0, r2.Count).OrderBy(i => V2.Dist(r2[i], seam)).First();
                res = r2.Skip(k).Concat(r2.Take(k)).ToList(); res.Add(res[0]);
            }
        }
        return res;
    }

    public static bool PointInRing(IReadOnlyList<V2> ring, V2 q)
    {
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Y > q.Y) != (b.Y > q.Y) &&
                q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double RingArea(IReadOnlyList<V2> r)
    {
        double a = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += r[j].X * r[i].Y - r[i].X * r[j].Y;
        return a / 2;
    }
}

public readonly record struct BBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public static BBox Of(IEnumerable<V2> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return new BBox(x0, y0, x1, y1);
    }
    public bool IsEmpty => MinX > MaxX;
    public BBox Union(BBox o) => IsEmpty ? o : o.IsEmpty ? this :
        new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    public bool Intersects(BBox o) => !(o.MinX > MaxX || o.MaxX < MinX || o.MinY > MaxY || o.MaxY < MinY);
    public bool Contains(V2 p, double tol = 0) => p.X >= MinX - tol && p.X <= MaxX + tol && p.Y >= MinY - tol && p.Y <= MaxY + tol;
    public static BBox Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
}
