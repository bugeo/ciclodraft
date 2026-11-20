using System.Text.Json;
using System.Text.Json.Serialization;
using CicloDraft.Geometry;

namespace CicloDraft.IO;

/// <summary>Fotocamera a foro stenopeico: posizione (coordinate locali del progetto, m), direzione, focale in pixel.</summary>
public class PhotoCamera
{
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Quota dell'obiettivo sul piano stradale (m).</summary>
    public double Z { get; set; } = 1.7;
    /// <summary>Direzione di vista in pianta (gradi, 0 = Est, 90 = Nord).</summary>
    public double Yaw { get; set; }
    /// <summary>Inclinazione (gradi, positivo = verso l'alto).</summary>
    public double Pitch { get; set; }
    public double Roll { get; set; }
    /// <summary>Distanza focale in pixel dell'immagine.</summary>
    public double F { get; set; } = 1000;
    public int W { get; set; }
    public int H { get; set; }

    public double[] ToArray() => new[] { X, Y, Z, Yaw, Pitch, Roll, F };
    public static PhotoCamera FromArray(double[] p, int w, int h) => new() { X = p[0], Y = p[1], Z = p[2], Yaw = p[3], Pitch = p[4], Roll = p[5], F = p[6], W = w, H = h };

    /// <summary>Angolo di campo orizzontale in gradi.</summary>
    [JsonIgnore] public double HFov => 2 * Math.Atan(W / 2.0 / F) * 180 / Math.PI;

    public readonly record struct Axes(double Fx, double Fy, double Fz, double Rx, double Ry, double Rz, double Ux, double Uy, double Uz);

    public Axes GetAxes()
    {
        double yr = Yaw * Math.PI / 180, pr = Pitch * Math.PI / 180, rr = Roll * Math.PI / 180;
        double fx = Math.Cos(yr) * Math.Cos(pr), fy = Math.Sin(yr) * Math.Cos(pr), fz = Math.Sin(pr);
        double rx = Math.Sin(yr), ry = -Math.Cos(yr), rz = 0;
        // up = right × fwd
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        double c = Math.Cos(rr), s = Math.Sin(rr);
        return new Axes(fx, fy, fz, c * rx + s * ux, c * ry + s * uy, c * rz + s * uz, -s * rx + c * ux, -s * ry + c * uy, -s * rz + c * uz);
    }

    /// <summary>Coordinate camera: (destra, alto, profondità).</summary>
    public (double cx, double cy, double cz) ToCam(double x, double y, double z, Axes a)
    {
        double dx = x - X, dy = y - Y, dz = z - Z;
        return (dx * a.Rx + dy * a.Ry + dz * a.Rz, dx * a.Ux + dy * a.Uy + dz * a.Uz, dx * a.Fx + dy * a.Fy + dz * a.Fz);
    }

    public (double u, double v) CamToPixel(double cx, double cy, double cz) => (W / 2.0 + F * cx / cz, H / 2.0 - F * cy / cz);

    /// <summary>Proiezione di un punto (x, y, quota) sulla foto; depth ≤ 0 se il punto è dietro la fotocamera.</summary>
    public (double u, double v, double depth) Project(double x, double y, double z)
    {
        var a = GetAxes();
        var (cx, cy, cz) = ToCam(x, y, z, a);
        if (cz <= 1e-6) return (double.NaN, double.NaN, cz);
        var (u, v) = CamToPixel(cx, cy, cz);
        return (u, v, cz);
    }

    /// <summary>Segmento 3D proiettato, tagliato davanti alla fotocamera (null se tutto dietro).</summary>
    public ((double u, double v) a, (double u, double v) b)? ProjectSegment(double x0, double y0, double z0, double x1, double y1, double z1, double near = 0.3)
    {
        var ax = GetAxes();
        var p = ToCam(x0, y0, z0, ax); var q = ToCam(x1, y1, z1, ax);
        if (p.cz < near && q.cz < near) return null;
        if (p.cz < near) { double t = (near - p.cz) / (q.cz - p.cz); p = (p.cx + (q.cx - p.cx) * t, p.cy + (q.cy - p.cy) * t, near); }
        if (q.cz < near) { double t = (near - q.cz) / (p.cz - q.cz); q = (q.cx + (p.cx - q.cx) * t, q.cy + (p.cy - q.cy) * t, near); }
        return (CamToPixel(p.cx, p.cy, p.cz), CamToPixel(q.cx, q.cy, q.cz));
    }
}

public enum PairKind { Point, Line }

/// <summary>Punto di controllo: un punto della foto e il suo corrispondente sulla mappa (punto o linea), con quota.</summary>
public class ControlPair
{
    public double U { get; set; }
    public double V { get; set; }
    public PairKind Kind { get; set; }
    /// <summary>Punto sulla mappa (coordinate assolute) per i punti; per le linee, i vertici della linea.</summary>
    public List<double[]> Map { get; set; } = new();
    public double Z { get; set; }
    public string Note { get; set; } = "";
    [JsonIgnore] public double Residual = double.NaN;
}

public class PhotoInsertFile
{
    public int Version { get; set; } = 1;
    public string Photo { get; set; } = "";
    public List<ControlPair> Pairs { get; set; } = new();
    /// <summary>Fotocamera calcolata, in coordinate assolute.</summary>
    public PhotoCamera? Camera { get; set; }
    public double CameraHeightHint { get; set; } = 1.7;
    public string? Mask { get; set; }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    public static PhotoInsertFile Load(string path) => JsonSerializer.Deserialize<PhotoInsertFile>(File.ReadAllText(path), Opts) ?? new();
}

/// <summary>Calcolo della fotocamera dai punti di controllo (minimi quadrati di Levenberg–Marquardt, partenze multiple).</summary>
public static class CameraSolver
{
    public record Result(PhotoCamera Camera, double Rms, double[] PairResiduals, string? Warning);

    /// <param name="pairs">punti di controllo (mappa in coordinate locali tramite origin)</param>
    /// <param name="heightHint">quota tipica dell'obiettivo (1,7 m a piedi, 2,5 m Street View)</param>
    public static Result? Solve(IReadOnlyList<ControlPair> pairs, V2 origin, int w, int h, double heightHint, PhotoCamera? start = null)
    {
        var ps = pairs.Where(p => p.Map.Count > 0).ToList();
        int eq = ps.Sum(p => p.Kind == PairKind.Point ? 2 : 1);
        if (eq < 6) return null;
        var local = ps.Select(p => p.Map.Select(m => new V2(m[0] - origin.X, m[1] - origin.Y)).ToList()).ToList();

        double[] Res(double[] prm)
        {
            var cam = PhotoCamera.FromArray(prm, w, h);
            var ax = cam.GetAxes();
            var r = new List<double>(eq + 2);
            for (int i = 0; i < ps.Count; i++)
            {
                var p = ps[i]; var m = local[i];
                if (p.Kind == PairKind.Point)
                {
                    var (cx, cy, cz) = cam.ToCam(m[0].X, m[0].Y, p.Z, ax);
                    if (cz < 0.3) { r.Add(1000 + (0.3 - cz) * 100); r.Add(1000); continue; }
                    var (u, v) = cam.CamToPixel(cx, cy, cz);
                    r.Add(u - p.U); r.Add(v - p.V);
                }
                else
                {
                    double best = double.MaxValue;
                    for (int k = 0; k + 1 < m.Count; k++)
                    {
                        var seg = cam.ProjectSegment(m[k].X, m[k].Y, p.Z, m[k + 1].X, m[k + 1].Y, p.Z);
                        if (seg is not { } s) continue;
                        best = Math.Min(best, SegDist(p.U, p.V, s.a.u, s.a.v, s.b.u, s.b.v));
                    }
                    r.Add(best == double.MaxValue ? 1000 : best);
                }
            }
            r.Add((prm[2] - heightHint) * 4);   // preferenza debole per la quota dell'obiettivo
            r.Add(prm[5] * 8);                   // fotocamera dritta (rollio vicino a 0)
            return r.ToArray();
        }

        // partenze: davanti ai punti, in tutte le direzioni
        var all = local.SelectMany(x => x).ToList();
        var c = new V2(all.Average(p => p.X), all.Average(p => p.Y));
        var starts = new List<double[]>();
        if (start != null) starts.Add(start.ToArray());
        foreach (var dist in new[] { 12.0, 30.0, 60.0 })
            for (int yaw = 0; yaw < 360; yaw += 30)
                foreach (var fk in new[] { 0.6, 0.9, 1.3 })
                {
                    double yr = yaw * Math.PI / 180;
                    starts.Add(new[] { c.X - Math.Cos(yr) * dist, c.Y - Math.Sin(yr) * dist, heightHint, yaw, 0, 0, fk * w });
                }

        double[]? best = null; double bestCost = double.MaxValue;
        foreach (var s in starts)
        {
            var (p, cost) = Lm(Res, s, 60);
            if (cost < bestCost) { bestCost = cost; best = p; }
        }
        if (best == null) return null;
        (best, bestCost) = Lm(Res, best, 400);
        best[3] = ((best[3] % 360) + 540) % 360 - 180;
        var camR = PhotoCamera.FromArray(best, w, h);

        var rr = Res(best);
        var perPair = new double[ps.Count];
        int j = 0;
        double sum = 0; int n = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            if (ps[i].Kind == PairKind.Point) { perPair[i] = Math.Sqrt(rr[j] * rr[j] + rr[j + 1] * rr[j + 1]); j += 2; }
            else { perPair[i] = Math.Abs(rr[j]); j += 1; }
            ps[i].Residual = perPair[i];
            sum += perPair[i] * perPair[i]; n++;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, n));
        string? warn = null;
        if (ps.All(p => p.Kind == PairKind.Line)) warn = "Solo linee: aggiungi almeno un punto (es. uno spigolo di edificio) per fissare la posizione lungo la via.";
        else if (camR.F < 0.25 * w || camR.F > 6 * w) warn = "La focale calcolata è anomala: controlla i punti.";
        else if (rms > 12) warn = "L'errore è alto: controlla i punti con l'errore maggiore.";
        return new Result(camR, rms, perPair, warn);
    }

    static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double qx = ax + t * dx - px, qy = ay + t * dy - py;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    /// <summary>Levenberg–Marquardt con jacobiano numerico.</summary>
    static (double[] p, double cost) Lm(Func<double[], double[]> f, double[] p0, int iters)
    {
        var p = (double[])p0.Clone();
        var r = f(p);
        double cost = r.Sum(x => x * x);
        double lambda = 1e-2;
        double[] steps = { 0.01, 0.01, 0.005, 0.01, 0.01, 0.01, 0.5 };
        int n = p.Length;
        for (int it = 0; it < iters; it++)
        {
            int m = r.Length;
            var J = new double[m, n];
            for (int k = 0; k < n; k++)
            {
                var q = (double[])p.Clone(); q[k] += steps[k];
                var rq = f(q);
                for (int i = 0; i < m; i++) J[i, k] = (rq[i] - r[i]) / steps[k];
            }
            var JtJ = new double[n, n]; var Jtr = new double[n];
            for (int a = 0; a < n; a++)
            {
                for (int i = 0; i < m; i++) Jtr[a] += J[i, a] * r[i];
                for (int b = 0; b < n; b++)
                {
                    double s = 0;
                    for (int i = 0; i < m; i++) s += J[i, a] * J[i, b];
                    JtJ[a, b] = s;
                }
            }
            bool improved = false;
            for (int tries = 0; tries < 8; tries++)
            {
                var A = (double[,])JtJ.Clone();
                for (int a = 0; a < n; a++) A[a, a] += lambda * (JtJ[a, a] + 1e-9);
                var delta = SolveLin(A, Jtr.Select(x => -x).ToArray());
                if (delta == null) { lambda *= 10; continue; }
                var q = p.Select((v, i) => v + delta[i]).ToArray();
                if (q[6] < 20) q[6] = 20;
                var rq = f(q);
                double cq = rq.Sum(x => x * x);
                if (cq < cost)
                {
                    p = q; r = rq;
                    bool small = cost - cq < 1e-9 * (1 + cost);
                    cost = cq; lambda = Math.Max(1e-9, lambda / 3); improved = true;
                    if (small) return (p, cost);
                    break;
                }
                lambda *= 8;
            }
            if (!improved) break;
        }
        return (p, cost);
    }

    static double[]? SolveLin(double[,] A, double[] b)
    {
        int n = b.Length;
        var M = new double[n, n + 1];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) M[i, j] = A[i, j]; M[i, n] = b[i]; }
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
            if (Math.Abs(M[piv, c]) < 1e-14) return null;
            if (piv != c) for (int j = 0; j <= n; j++) (M[c, j], M[piv, j]) = (M[piv, j], M[c, j]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double k = M[r, c] / M[c, c];
                if (k == 0) continue;
                for (int j = c; j <= n; j++) M[r, j] -= k * M[c, j];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = M[i, n] / M[i, i];
        return x;
    }
}
