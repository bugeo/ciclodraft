using System.IO.Compression;

namespace CicloDraft.IO;

/// <summary>Immagine RGBA generata dal programma, con codifica PNG.</summary>
public class Tex
{
    public readonly int W, H;
    public readonly byte[] Px;
    public Tex(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }

    public void Set(int x, int y, (double r, double g, double b) c, double a = 1)
    {
        x = ((x % W) + W) % W; y = ((y % H) + H) % H; // le texture si ripetono: si disegna "a capo"
        int i = (y * W + x) * 4;
        Px[i] = B(c.r); Px[i + 1] = B(c.g); Px[i + 2] = B(c.b); Px[i + 3] = B(a * 255);
    }

    public (double r, double g, double b) Get(int x, int y)
    {
        x = ((x % W) + W) % W; y = ((y % H) + H) % H;
        int i = (y * W + x) * 4;
        return (Px[i], Px[i + 1], Px[i + 2]);
    }

    public byte A(int x, int y)
    {
        x = ((x % W) + W) % W; y = ((y % H) + H) % H;
        return Px[(y * W + x) * 4 + 3];
    }

    /// <summary>Immagine dimezzata (filtro a scatola), per i livelli di mipmap.</summary>
    public Tex Half()
    {
        var t = new Tex(Math.Max(1, W / 2), Math.Max(1, H / 2));
        for (int y = 0; y < t.H; y++)
            for (int x = 0; x < t.W; x++)
                for (int k = 0; k < 4; k++)
                {
                    int s = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                        s += Px[((Math.Min(H - 1, 2 * y + dy)) * W + Math.Min(W - 1, 2 * x + dx)) * 4 + k];
                    t.Px[(y * t.W + x) * 4 + k] = (byte)(s / 4);
                }
        return t;
    }

    static byte B(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    public void Rect(double x0, double y0, double x1, double y1, Func<int, int, (double, double, double)> color, double a = 1)
    {
        for (int y = (int)Math.Floor(y0); y < (int)Math.Ceiling(y1); y++)
            for (int x = (int)Math.Floor(x0); x < (int)Math.Ceiling(x1); x++)
                Set(x, y, color(x, y), a);
    }

    public byte[] Png()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, W); BE(ihdr, 4, H); ihdr[8] = 8; ihdr[9] = 6; // 8 bit RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                for (int y = 0; y < H; y++) { z.WriteByte(0); z.Write(Px, y * W * 4, W * 4); }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t.Concat(data)) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        c ^= 0xFFFFFFFF;
        var crc = new byte[4]; BE(crc, 0, (int)c); s.Write(crc);
    }
}

/// <summary>Texture procedurali per l'esportazione 3D (tutte ripetibili senza giunte).</summary>
public static class TextureFactory
{
    // ── rumore ──
    static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    /// <summary>Rumore a valori, periodico sul periodo p (in celle).</summary>
    static double Value(double x, double y, int p, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        int M(int v) => ((v % p) + p) % p;
        double a = Hash(M(x0), M(y0), seed), b = Hash(M(x0 + 1), M(y0), seed);
        double c = Hash(M(x0), M(y0 + 1), seed), d = Hash(M(x0 + 1), M(y0 + 1), seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>Rumore frattale periodico sull'immagine (valore circa 0..1).</summary>
    static double Fbm(int x, int y, int size, int baseCells, int octaves, int seed)
    {
        double sum = 0, amp = 0.5, tot = 0;
        int cells = baseCells;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Value(x * cells / (double)size, y * cells / (double)size, cells, seed + o * 31);
            tot += amp; amp *= 0.5; cells *= 2;
        }
        return sum / tot;
    }

    static (double, double, double) Hex(string h)
    {
        var c = Avalonia.Media.Color.Parse(h);
        return (c.R, c.G, c.B);
    }

    static (double, double, double) Mul((double r, double g, double b) c, double k) => (c.r * k, c.g * k, c.b * k);
    static (double, double, double) Mix((double r, double g, double b) a, (double r, double g, double b) b, double t) =>
        (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t);

    // ── superfici ──

    /// <summary>Asfalto: grana fine, aggregati chiari e scuri, leggere macchie.</summary>
    public static Tex Asphalt(int seed = 1)
    {
        const int S = 512; var t = new Tex(S, S);
        var baseC = Hex("#3D4045");
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double n = Fbm(x, y, S, 4, 5, seed) - 0.5;
                double g = Hash(x, y, seed + 7);
                double k = 1 + n * 0.35 + (g > 0.985 ? 0.45 : g < 0.02 ? -0.35 : (g - 0.5) * 0.18);
                t.Set(x, y, Mul(baseC, k));
            }
        return t;
    }

    /// <summary>Lastre di pietra a correre (0,6 × 0,4 m su un riquadro di 2,4 m).</summary>
    public static Tex StoneSlabs(string baseHex, int seed)
    {
        const int S = 512; var t = new Tex(S, S);
        var baseC = Hex(baseHex); var joint = Mul(baseC, 0.62);
        double ppm = S / 2.4; int rowH = (int)(0.4 * ppm); int slabW = (int)(0.6 * ppm);
        for (int y = 0; y < S; y++)
        {
            int row = y / rowH;
            int off = (row % 2) * slabW / 2;
            for (int x = 0; x < S; x++)
            {
                int xx = x + off;
                int col = (xx / slabW) % (S / slabW);
                bool j = y % rowH < 2 || xx % slabW < 2;
                double tone = 0.9 + Hash(col, row, seed) * 0.2;
                double n = Fbm(x, y, S, 8, 4, seed + 3) - 0.5;
                double g = (Hash(x, y, seed + 11) - 0.5) * 0.08;
                t.Set(x, y, j ? Mul(joint, 1 + n * 0.3) : Mul(baseC, tone + n * 0.18 + g));
            }
        }
        return t;
    }

    /// <summary>Granito chiaro puntinato (cordoli).</summary>
    public static Tex Granite()
    {
        const int S = 256; var t = new Tex(S, S);
        var baseC = Hex("#D2CEC6");
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double g = Hash(x, y, 5);
                double n = Fbm(x, y, S, 6, 3, 9) - 0.5;
                var c = g > 0.93 ? Hex("#4A4A4C") : g > 0.86 ? Hex("#8E8C88") : g < 0.05 ? Hex("#F4F1EA") : baseC;
                t.Set(x, y, Mul(c, 1 + n * 0.12));
            }
        // giunto tra i blocchi di cordolo (blocchi da 1 m)
        t.Rect(0, 0, 2, S, (_, _) => Hex("#7D7A74"));
        return t;
    }

    /// <summary>Resina colorata con inerti (piste ciclabili).</summary>
    public static Tex Resin(string baseHex, int seed)
    {
        const int S = 256; var t = new Tex(S, S);
        var baseC = Hex(baseHex);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double n = Fbm(x, y, S, 4, 5, seed) - 0.5;
                double g = Hash(x, y, seed + 2);
                t.Set(x, y, Mul(baseC, 1 + n * 0.22 + (g > 0.97 ? 0.25 : g < 0.03 ? -0.25 : 0)));
            }
        return t;
    }

    /// <summary>Vernice bianca un po' consumata.</summary>
    public static Tex Paint(string color = "#F2F1EB", string wornColor = "#B9BAB6")
    {
        const int S = 128; var t = new Tex(S, S);
        var w = Hex(color); var worn = Hex(wornColor);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                double n = Fbm(x, y, S, 4, 4, 21);
                t.Set(x, y, n < 0.24 ? Mix(worn, w, Math.Pow(n / 0.24, 0.5)) : w);
            }
        return t;
    }

    public static Tex Grass()
    {
        const int S = 256; var t = new Tex(S, S);
        var a = Hex("#5E7F3C"); var b = Hex("#86A251");
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
                t.Set(x, y, Mul(Mix(a, b, Fbm(x, y, S, 6, 5, 4)), 0.9 + Hash(x, y, 8) * 0.2));
        return t;
    }

    /// <summary>Coppi in laterizio (copertura), 5 file in 2 m.</summary>
    public static Tex RoofTiles()
    {
        const int S = 512; var t = new Tex(S, S);
        double ppm = S / 2.0; int rowH = (int)(0.4 * ppm); int colW = (int)(0.2 * ppm);
        for (int y = 0; y < S; y++)
        {
            int row = y / rowH;
            for (int x = 0; x < S; x++)
            {
                int col = x / colW;
                double fx = (x % colW) / (double)colW;
                double shade = 0.62 + 0.45 * Math.Sin(Math.PI * fx);
                double tone = 0.86 + Hash(col, row, 3) * 0.28;
                var c = Mix(Hex("#9C4E37"), Hex("#C0704F"), Hash(col, row, 4));
                double lip = (y % rowH) < 3 ? 0.55 : 1;
                double n = Fbm(x, y, S, 8, 3, 6) - 0.5;
                t.Set(x, y, Mul(c, shade * tone * lip + n * 0.15));
            }
        }
        return t;
    }

    // ── facciate ──

    public record Palette(string Plaster, string Shutter, string Base);

    /// <summary>Intonaci e persiane tipici dei centri storici padani.</summary>
    public static readonly Palette[] Palettes =
    {
        new("#E3C383", "#3F5B45", "#A69C8A"), // giallo ocra, persiane verdi
        new("#DDAA98", "#5A4231", "#9E9586"), // rosa antico, persiane marroni
        new("#C77A5E", "#3F5B45", "#988E80"), // rosso mattone
        new("#EDE4D1", "#3C4F43", "#A39B8E"), // avorio
        new("#E2A86C", "#5A4231", "#9C9282"), // arancio
        new("#D3CFC2", "#45574A", "#8F8A80"), // grigio caldo
    };

    static void Plaster(Tex t, (double, double, double) c, int seed)
    {
        for (int y = 0; y < t.H; y++)
            for (int x = 0; x < t.W; x++)
            {
                double n = Fbm(x, y, t.W, 6, 5, seed) - 0.5;
                // leggera colatura verso il basso
                double drip = (Fbm(x, 0, t.W, 24, 2, seed + 4) - 0.5) * 0.06 * (y / (double)t.H);
                t.Set(x, y, Mul(c, 1 + n * 0.12 + drip + (Hash(x, y, seed) - 0.5) * 0.04));
            }
    }

    /// <summary>Un piano (3,6 × 3,2 m): finestra con cornice, davanzale, persiane aperte, marcapiano.</summary>
    public static Tex FacadeFloor(Palette p, int seed)
    {
        const int S = 512; var t = new Tex(S, S);
        double sx = S / 3.6, sy = S / 3.2;
        var pl = Hex(p.Plaster); Plaster(t, pl, seed);
        var frame = Mul(pl, 1.12); var glass = Hex("#2E3A44"); var shut = Hex(p.Shutter); var sill = Hex("#D8D3C8");
        double wx0 = 1.25 * sx, wx1 = 2.35 * sx, wy0 = (3.2 - 0.95 - 1.75) * sy, wy1 = (3.2 - 0.95) * sy;
        // marcapiano in alto
        t.Rect(0, 0, S, 0.14 * sy, (x, y) => Mul(frame, 0.98 + Hash(x, y, 2) * 0.04));
        t.Rect(0, 0.14 * sy, S, 0.17 * sy, (_, _) => Mul(pl, 0.78));
        // cornice della finestra
        t.Rect(wx0 - 0.12 * sx, wy0 - 0.12 * sy, wx1 + 0.12 * sx, wy1, (_, _) => frame);
        // vetro con riflesso del cielo e telaio
        t.Rect(wx0, wy0, wx1, wy1, (x, y) => Mix(Hex("#7D90A0"), glass, Math.Clamp((y - wy0) / (wy1 - wy0) * 1.4, 0, 1)));
        t.Rect((wx0 + wx1) / 2 - 2, wy0, (wx0 + wx1) / 2 + 2, wy1, (_, _) => Hex("#E9E4DA"));
        t.Rect(wx0, wy0 + (wy1 - wy0) * 0.35, wx1, wy0 + (wy1 - wy0) * 0.35 + 3, (_, _) => Hex("#E9E4DA"));
        // persiane aperte con stecche
        foreach (var (a, b) in new[] { (wx0 - 0.12 * sx - 0.55 * sx, wx0 - 0.12 * sx), (wx1 + 0.12 * sx, wx1 + 0.12 * sx + 0.55 * sx) })
            t.Rect(a, wy0 - 0.05 * sy, b, wy1, (x, y) => Mul(shut, ((int)(y - wy0) % 7 < 2 ? 0.7 : 1.0) * (0.95 + Hash(x, y, 9) * 0.1)));
        // davanzale con ombra
        t.Rect(wx0 - 0.2 * sx, wy1, wx1 + 0.2 * sx, wy1 + 0.07 * sy, (_, _) => sill);
        t.Rect(wx0 - 0.2 * sx, wy1 + 0.07 * sy, wx1 + 0.2 * sx, wy1 + 0.13 * sy, (x, y) => Mul(t.Get(x, y), 0.8));
        return t;
    }

    /// <summary>Piano terra, due campate (7,2 × 3,5 m): zoccolo in pietra, una vetrina e un portone o una seconda vetrina.</summary>
    public static Tex FacadeGround(Palette p, int seed)
    {
        const int S = 512; var t = new Tex(2 * S, S);
        double sx = S / 3.6, sy = S / 3.5;
        var pl = Mul(Hex(p.Plaster), 0.94); Plaster(t, pl, seed + 50);
        var stone = Hex(p.Base);
        t.Rect(0, (3.5 - 0.6) * sy, 2 * S, S, (x, y) => Mul(stone, 0.9 + Fbm(x, y, S, 10, 3, seed) * 0.2));
        t.Rect(0, (3.5 - 0.6) * sy, 2 * S, (3.5 - 0.6) * sy + 3, (_, _) => Mul(stone, 0.7));
        string[] frames = { "#2B2D2F", "#3E4A3F", "#4A3426" };
        for (int bay = 0; bay < 2; bay++)
        {
            double bx = bay * S;
            bool shop = bay == 0 || seed % 2 == 1;
            if (shop)
            {
                var fr = Hex(frames[(seed + bay) % frames.Length]);
                double ox0 = bx + 0.6 * sx, ox1 = bx + 3.0 * sx, oy0 = (3.5 - 2.85) * sy, oy1 = S;
                t.Rect(ox0 - 0.1 * sx, oy0 - 0.1 * sy, ox1 + 0.1 * sx, oy1, (_, _) => fr);
                t.Rect(ox0, oy0, ox1, oy1 - 0.05 * sy, (x, y) => Mix(Hex("#8C9CA8"), Hex("#2A3540"), Math.Clamp((y - oy0) / (oy1 - oy0) * 1.3 + Fbm(x, y, S, 3, 2, seed + bay) * 0.2, 0, 1)));
                t.Rect(ox0, oy0 + 0.55 * sy, ox1, oy0 + 0.55 * sy + 4, (_, _) => fr);
                t.Rect(bx + 1.8 * sx - 2, oy0 + 0.55 * sy, bx + 1.8 * sx + 2, oy1, (_, _) => fr);
                // tenda da sole su alcune vetrine
                if ((seed + bay) % 3 == 0)
                {
                    var aw = Hex((seed % 2 == 0) ? "#7A2E2A" : "#2E4A63");
                    t.Rect(ox0 - 0.15 * sx, oy0 - 0.45 * sy, ox1 + 0.15 * sx, oy0 - 0.12 * sy, (x, y) => Mul(aw, ((x - (int)ox0) / 22) % 2 == 0 ? 1.0 : 0.82));
                }
            }
            else
            {
                double dx0 = bx + 1.1 * sx, dx1 = bx + 2.5 * sx, dy0 = (3.5 - 2.7) * sy, oy1 = S;
                t.Rect(dx0 - 0.15 * sx, dy0 - 0.15 * sy, dx1 + 0.15 * sx, oy1, (_, _) => Hex("#C9C2B4"));
                t.Rect(dx0, dy0, dx1, oy1, (x, y) => Mul(Hex("#5B3D28"), 0.85 + ((x - (int)dx0) % 18 < 2 ? -0.25 : 0) + Fbm(x, y, S, 4, 3, seed) * 0.25));
                t.Rect((dx0 + dx1) / 2 - 2, dy0, (dx0 + dx1) / 2 + 2, oy1, (_, _) => Hex("#2E1F14"));
            }
        }
        return t;
    }

    /// <summary>Intonaco semplice (pareti corte, edifici bassi).</summary>
    public static Tex PlainPlaster(Palette p, int seed)
    {
        var t = new Tex(256, 256);
        Plaster(t, Mul(Hex(p.Plaster), 0.97), seed + 90);
        return t;
    }

    /// <summary>Pittogramma della bicicletta, bianco su fondo trasparente (verso: avanti = in alto).</summary>
    public static Tex BikeSymbol()
    {
        const int S = 256; var t = new Tex(S, S);
        for (int i = 0; i < t.Px.Length; i += 4) { t.Px[i] = 242; t.Px[i + 1] = 241; t.Px[i + 2] = 235; t.Px[i + 3] = 0; }
        var w = (242.0, 241.0, 235.0);
        void Disk(double cx, double cy, double r) { for (int y = (int)(cy - r); y <= cy + r; y++) for (int x = (int)(cx - r); x <= cx + r; x++) if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) t.Set(x, y, w); }
        void Line(double x0, double y0, double x1, double y1, double th)
        {
            double L = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            for (double s = 0; s <= L; s += 0.5) Disk(x0 + (x1 - x0) * s / L, y0 + (y1 - y0) * s / L, th / 2);
        }
        void Ring(double cx, double cy, double r, double th)
        {
            for (int y = (int)(cy - r - th); y <= cy + r + th; y++)
                for (int x = (int)(cx - r - th); x <= cx + r + th; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (Math.Abs(d - r) <= th / 2) t.Set(x, y, w);
                }
        }
        // bici vista di lato (coordinate in "lunghezze di bici"), ruotata: la parte anteriore va verso l'alto dell'immagine
        (double, double) M(double bx, double by) => (128 - (by - 0.16) * 200, 128 - bx * 200);
        void L2(double ax, double ay, double bx2, double by2, double th2) { var (x0, y0) = M(ax, ay); var (x1, y1) = M(bx2, by2); Line(x0, y0, x1, y1, th2); }
        double r = 0.2 * 200, th = 9;
        var (fx, fy) = M(0.36, 0); var (rx, ry) = M(-0.36, 0);
        Ring(fx, fy, r, th); Ring(rx, ry, r, th);
        double cx = -0.02, cy = 0.0, sx2 = -0.14, sy2 = 0.3, hx = 0.24, hy = 0.32;
        L2(-0.36, 0, cx, cy, th);      // fodero basso
        L2(cx, cy, sx2, sy2, th);      // piantone
        L2(-0.36, 0, sx2, sy2, th);    // fodero alto
        L2(cx, cy, hx, hy, th);        // tubo obliquo
        L2(sx2, sy2, hx, hy, th);      // tubo orizzontale
        L2(hx, hy, 0.36, 0, th);       // forcella
        L2(hx, hy, hx + 0.04, hy + 0.1, th); L2(hx + 0.04, hy + 0.1, hx - 0.06, hy + 0.12, th); // manubrio
        L2(sx2, sy2, sx2 - 0.02, sy2 + 0.06, th); L2(sx2 - 0.1, sy2 + 0.07, sx2 + 0.06, sy2 + 0.07, th); // sella
        return t;
    }
}
