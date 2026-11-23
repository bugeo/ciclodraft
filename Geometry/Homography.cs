namespace CicloDraft.Geometry;

/// <summary>
/// Trasformazione prospettica del piano (omografia 3×3): porta il rettangolo dell'immagine (0,0)-(W,H)
/// sui quattro angoli scelti dall'utente (in alto a sinistra, in alto a destra, in basso a destra, in basso a sinistra).
/// h = [a b c; d e f; g h 1]:  x' = (a x + b y + c) / (g x + h y + 1),  y' = (d x + e y + f) / (g x + h y + 1).
/// </summary>
public static class Homography
{
    /// <summary>Angoli del rettangolo senza trasformazione.</summary>
    public static double[] Identity(double w, double h) => new[] { 0, 0, w, 0, w, h, 0, h };

    public static bool IsIdentity(double[]? quad, double w, double h) =>
        quad == null || quad.Length != 8 || Identity(w, h).Zip(quad).All(p => Math.Abs(p.First - p.Second) < 0.5);

    /// <summary>Omografia dal rettangolo (0,0)-(W,H) al quadrilatero quad; null se il quadrilatero è degenere.</summary>
    public static double[]? RectToQuad(double w, double h, double[] quad)
    {
        var src = Identity(w, h);
        var A = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[2 * i], y = src[2 * i + 1], u = quad[2 * i], v = quad[2 * i + 1];
            int r = 2 * i;
            A[r, 0] = x; A[r, 1] = y; A[r, 2] = 1; A[r, 6] = -x * u; A[r, 7] = -y * u; A[r, 8] = u;
            A[r + 1, 3] = x; A[r + 1, 4] = y; A[r + 1, 5] = 1; A[r + 1, 6] = -x * v; A[r + 1, 7] = -y * v; A[r + 1, 8] = v;
        }
        // eliminazione di Gauss con pivot parziale
        for (int c = 0; c < 8; c++)
        {
            int piv = c; for (int r = c + 1; r < 8; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            if (Math.Abs(A[piv, c]) < 1e-12) return null;
            if (piv != c) for (int k = 0; k < 9; k++) (A[c, k], A[piv, k]) = (A[piv, k], A[c, k]);
            for (int r = 0; r < 8; r++)
            {
                if (r == c) continue;
                double f = A[r, c] / A[c, c];
                if (f == 0) continue;
                for (int k = c; k < 9; k++) A[r, k] -= f * A[c, k];
            }
        }
        var hm = new double[9];
        for (int i = 0; i < 8; i++) hm[i] = A[i, 8] / A[i, i];
        hm[8] = 1;
        return hm;
    }

    /// <summary>Prodotto a·b (si applica prima b, poi a).</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
        return r;
    }

    public static double[] IdentityMatrix() => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    /// <summary>Omografia che porta il quadrilatero src su dst (stesso ordine dei vertici).</summary>
    public static double[]? QuadToQuad(double[] src, double[] dst)
    {
        var hs = RectToQuad(1, 1, src); var hd = RectToQuad(1, 1, dst);
        if (hs == null || hd == null || Invert(hs) is not { } inv) return null;
        return Mul(hd, inv);
    }

    /// <summary>Riordina quattro punti attorno al loro baricentro (così il poligono non è intrecciato).</summary>
    public static double[] SortAround(double[] q)
    {
        double cx = (q[0] + q[2] + q[4] + q[6]) / 4, cy = (q[1] + q[3] + q[5] + q[7]) / 4;
        var pts = Enumerable.Range(0, 4).Select(i => (x: q[2 * i], y: q[2 * i + 1])).OrderBy(p => Math.Atan2(p.y - cy, p.x - cx)).ToList();
        return pts.SelectMany(p => new[] { p.x, p.y }).ToArray();
    }

    public static double[]? Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-15) return null;
        double k = 1 / det;
        return new[]
        {
            A * k, -(b * i - c * h) * k, (b * f - c * e) * k,
            B * k, (a * i - c * g) * k, -(a * f - c * d) * k,
            C * k, -(a * h - b * g) * k, (a * e - b * d) * k
        };
    }

    public static (double x, double y) Apply(double[] m, double x, double y)
    {
        double w = m[6] * x + m[7] * y + m[8];
        return ((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }

    /// <summary>Il quadrilatero è utilizzabile: convesso e con gli angoli nel giusto ordine.</summary>
    public static bool IsConvex(double[] q)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            double ax = q[2 * ((i + 1) % 4)] - q[2 * i], ay = q[2 * ((i + 1) % 4) + 1] - q[2 * i + 1];
            double bx = q[2 * ((i + 2) % 4)] - q[2 * ((i + 1) % 4)], by = q[2 * ((i + 2) % 4) + 1] - q[2 * ((i + 1) % 4) + 1];
            double z = ax * by - ay * bx;
            int s = Math.Sign(z);
            if (s == 0) return false;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }
}
