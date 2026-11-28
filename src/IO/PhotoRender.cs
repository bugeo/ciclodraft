using CicloDraft.Geometry;
using CicloDraft.Model;
using SkiaSharp;

namespace CicloDraft.IO;

/// <summary>Immagine RGBA a 8 bit (foto, maschera, risultato).</summary>
public class Rgba
{
    public readonly int W, H;
    public readonly byte[] Px;
    public Rgba(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; }

    public static Rgba Load(string path)
    {
        using var bmp = SKBitmap.Decode(path) ?? throw new InvalidDataException("Immagine non leggibile: " + Path.GetFileName(path));
        using var conv = bmp.Copy(SKColorType.Rgba8888);
        var r = new Rgba(conv.Width, conv.Height);
        var span = conv.GetPixelSpan();
        span.CopyTo(r.Px);
        // le immagini senza trasparenza: alfa pieno
        if (conv.AlphaType == SKAlphaType.Unpremul || conv.AlphaType == SKAlphaType.Premul) { }
        return r;
    }

    public void SavePng(string path)
    {
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(Px, 0, bmp.GetPixels(), Px.Length);
        using var img = SKImage.FromBitmap(bmp);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var data = ext is ".jpg" or ".jpeg" ? img.Encode(SKEncodedImageFormat.Jpeg, 92) : img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}

public class PhotoRenderOptions
{
    public bool Markings { get; set; } = true;
    public bool BikeSymbols { get; set; } = true;
    public bool StreetLights { get; set; }
    public double CurbHeight { get; set; } = 0.15;
    public bool RaisedBikeLane { get; set; }
    /// <summary>Luminosità del progetto (1 = normale).</summary>
    public double Exposure { get; set; } = 1.0;
    /// <summary>Quanto il progetto prende luci e ombre dalla foto (0 = niente, 1 = tutto).</summary>
    public double ShadowTransfer { get; set; } = 0.75;
    /// <summary>Opacità del progetto (1 = pieno).</summary>
    public double Opacity { get; set; } = 1.0;
    public int Supersample { get; set; } = 2;
    /// <summary>Morbidezza dei bordi per adattarsi alla nitidezza della foto (0 = nessuna).</summary>
    public double Softness { get; set; } = 0.6;
}

/// <summary>Disegna il progetto in prospettiva sulla foto: edifici come maschera di profondità, texture, ombre prese dalla foto.</summary>
public static class PhotoRender
{
    record struct CV(double X, double Y, double Z, double U, double V); // vertice in coordinate camera, con UV

    class Raster
    {
        public int W, H; public float[] Depth; public float[] R, G, B; public bool[] Cov;
        public Raster(int w, int h)
        {
            W = w; H = h; Depth = new float[w * h]; Array.Fill(Depth, float.PositiveInfinity);
            R = new float[w * h]; G = new float[w * h]; B = new float[w * h]; Cov = new bool[w * h];
        }
    }

    class TexMips
    {
        public readonly List<Tex> Levels = new();
        public TexMips(Tex t) { Levels.Add(t); while (Levels[^1].W > 4 && Levels[^1].H > 4) Levels.Add(Levels[^1].Half()); }
    }

    /// <param name="Raw">il progetto da solo, con trasparenza, senza il taglio della maschera (per la trasformazione libera)</param>
    public record Result(Rgba Composite, Rgba Layer, Rgba Raw);

    /// <summary>
    /// Deformazione: il progetto (Raw) viene deformato con l'omografia hm (pixel del progetto → pixel della foto),
    /// poi tagliato dalla maschera e composto sulla foto base.
    /// </summary>
    public static (Rgba Composite, Rgba Layer) ApplyWarp(Rgba raw, Rgba photo, Rgba? mask, double[] hm)
    {
        int W = photo.W, H = photo.H;
        var comp = new Rgba(W, H); var layer = new Rgba(W, H);
        var inv = Homography.Invert(hm) ?? Homography.IdentityMatrix();
        System.Threading.Tasks.Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                var (sx, sy) = Homography.Apply(inv, x + 0.5, y + 0.5);
                sx -= 0.5; sy -= 0.5;
                float r = 0, g = 0, b = 0, a = 0;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 >= -1 && y0 >= -1 && x0 < W && y0 < H)
                {
                    float fx = (float)(sx - x0), fy = (float)(sy - y0);
                    for (int k = 0; k < 4; k++)
                    {
                        int xx = x0 + (k & 1), yy = y0 + (k >> 1);
                        if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        float wgt = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
                        int j = 4 * (yy * W + xx);
                        float aa = raw.Px[j + 3] / 255f * wgt;
                        r += raw.Px[j] * aa; g += raw.Px[j + 1] * aa; b += raw.Px[j + 2] * aa; a += aa;
                    }
                    if (a > 1e-5f) { r /= a; g /= a; b /= a; }
                }
                if (mask != null) a *= 1 - mask.Px[4 * i + 3] / 255f;
                a = Math.Clamp(a, 0, 1);
                byte R8 = (byte)Math.Clamp(Math.Round(r), 0, 255), G8 = (byte)Math.Clamp(Math.Round(g), 0, 255), B8v = (byte)Math.Clamp(Math.Round(b), 0, 255);
                layer.Px[4 * i] = R8; layer.Px[4 * i + 1] = G8; layer.Px[4 * i + 2] = B8v; layer.Px[4 * i + 3] = (byte)Math.Round(a * 255);
                comp.Px[4 * i] = (byte)Math.Round(photo.Px[4 * i] * (1 - a) + R8 * a);
                comp.Px[4 * i + 1] = (byte)Math.Round(photo.Px[4 * i + 1] * (1 - a) + G8 * a);
                comp.Px[4 * i + 2] = (byte)Math.Round(photo.Px[4 * i + 2] * (1 - a) + B8v * a);
                comp.Px[4 * i + 3] = 255;
            }
        });
        return (comp, layer);
    }

    public static Result Render(PhotoCamera cam, Rgba photo, Rgba? mask, Project project, GpkgData? data, PhotoRenderOptions o)
    {
        int S = Math.Clamp(o.Supersample, 1, 4);
        var ras = new Raster(photo.W * S, photo.H * S);
        var ax = cam.GetAxes();
        double near = 0.25;

        // ── edifici: solo profondità ──
        if (data != null)
        {
            var occ = data.Layers.Where(l => (LayerRole)l.Role == LayerRole.Muri).ToList();
            if (occ.Count == 0) occ = data.Layers.Where(l => (LayerRole)l.Role == LayerRole.Edifici3D).ToList();
            foreach (var l in occ)
                foreach (var lp in l.Polys)
                {
                    double top = lp.Height is > 0.5 and < 400 ? Math.Max(lp.Height + 6, 12) : 40;
                    void Ring(NetTopologySuite.Geometries.LineString r)
                    {
                        var c = r.Coordinates;
                        for (int i = 0; i + 1 < c.Length; i++)
                        {
                            var a0 = cam.ToCam(c[i].X, c[i].Y, -1, ax); var b0 = cam.ToCam(c[i + 1].X, c[i + 1].Y, -1, ax);
                            var b1 = cam.ToCam(c[i + 1].X, c[i + 1].Y, top, ax); var a1 = cam.ToCam(c[i].X, c[i].Y, top, ax);
                            var A = new CV(a0.cx, a0.cy, a0.cz, 0, 0); var B = new CV(b0.cx, b0.cy, b0.cz, 0, 0);
                            var C = new CV(b1.cx, b1.cy, b1.cz, 0, 0); var D = new CV(a1.cx, a1.cy, a1.cz, 0, 0);
                            Tri(ras, cam, S, near, A, B, C, null, null, 0, true);
                            Tri(ras, cam, S, near, A, C, D, null, null, 0, true);
                        }
                    }
                    Ring(lp.Poly.ExteriorRing);
                    foreach (var h in lp.Poly.InteriorRings) Ring(h);
                }
        }

        // ── progetto: la stessa geometria dell'esportazione 3D ──
        var eo = new Export3DOptions
        {
            Buildings = false, Ground = false, ExistingSidewalks = false, Trees = false, Sun = false,
            StreetLights = o.StreetLights, Markings = o.Markings, BikeSymbols = o.BikeSymbols, Textures = true,
            CurbHeight = o.CurbHeight, RaisedBikeLane = o.RaisedBikeLane
        };
        var scene = Export3D.BuildScene(project, data, eo, out _, out _, out _);
        var mips = new Dictionary<string, TexMips>();
        // luce: dall'alto e un po' di lato (le ombre vere arrivano dalla foto)
        double lx = 0.25, ly = 0.2, lz = 0.95, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;

        void Walk(Export3D.Node n)
        {
            foreach (var (mat, part) in n.Parts)
            {
                TexMips? tm = null;
                if (mat.TexKey != null && !mips.TryGetValue(mat.TexKey, out tm)) mips[mat.TexKey] = tm = new TexMips(Export3D.MakeTexture(mat.TexKey));
                var col = Avalonia.Media.Color.Parse(mat.Hex);
                var baseCol = (col.R / 255.0, col.G / 255.0, col.B / 255.0);
                var pos = part.Pos; var nrm = part.Nrm; var uv = part.Tex; var idx = part.Idx;
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    var v = new CV[3];
                    double shade = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int i = (int)idx[t + k];
                        double wx = pos[3 * i], wh = pos[3 * i + 1], wy = -pos[3 * i + 2]; // glTF → (Est, Nord, quota)
                        var cc = cam.ToCam(wx, wy, wh, ax);
                        v[k] = new CV(cc.cx, cc.cy, cc.cz, uv[2 * i], uv[2 * i + 1]);
                        if (k == 0)
                        {
                            double nx = nrm[3 * i], nh = nrm[3 * i + 1], ny = -nrm[3 * i + 2];
                            shade = 0.58 + 0.45 * Math.Max(0, nx * lx + ny * ly + nh * lz);
                        }
                    }
                    Tri(ras, cam, S, near, v[0], v[1], v[2], tm, baseCol, shade * o.Exposure, false, mat.AlphaMask);
                }
            }
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in scene.Children.Where(c => c.Name is "Progetto" or "Lampioni")) Walk(c);

        // ── riduzione (antialias) ──
        int W = photo.W, H = photo.H;
        var layerR = new float[W * H]; var layerG = new float[W * H]; var layerB = new float[W * H]; var alpha = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double r = 0, g = 0, b = 0; int cov = 0;
                for (int sy = 0; sy < S; sy++)
                    for (int sx = 0; sx < S; sx++)
                    {
                        int i = (y * S + sy) * ras.W + x * S + sx;
                        if (!ras.Cov[i]) continue;
                        r += ras.R[i]; g += ras.G[i]; b += ras.B[i]; cov++;
                    }
                int j = y * W + x;
                if (cov > 0) { layerR[j] = (float)(r / cov); layerG[j] = (float)(g / cov); layerB[j] = (float)(b / cov); }
                alpha[j] = cov / (float)(S * S);
            }

        // ── luci e ombre dalla foto ──
        if (o.ShadowTransfer > 0)
        {
            var lum = new float[W * H];
            for (int i = 0; i < W * H; i++)
                lum[i] = (0.2126f * photo.Px[4 * i] + 0.7152f * photo.Px[4 * i + 1] + 0.0722f * photo.Px[4 * i + 2]) / 255f;
            // solo le variazioni ampie (ombre portate, zone in luce): la trama della pavimentazione non deve trasparire
            int br0 = Math.Max(4, W / 110);
            var blur = BoxBlur(BoxBlur(lum, W, H, br0), W, H, br0);
            var samples = new List<float>();
            for (int i = 0; i < W * H; i++)
                if (alpha[i] > 0.5f && (mask == null || mask.Px[4 * i + 3] < 128)) samples.Add(blur[i]);
            if (samples.Count > 50)
            {
                samples.Sort();
                float refL = Math.Max(0.05f, samples[(int)(samples.Count * 0.85)]);
                for (int i = 0; i < W * H; i++)
                {
                    if (alpha[i] <= 0) continue;
                    double f = 1 + o.ShadowTransfer * (blur[i] / refL - 1);
                    f = Math.Clamp(f, 0.32, 1.12);
                    layerR[i] *= (float)f; layerG[i] *= (float)f; layerB[i] *= (float)f;
                }
            }
        }

        // ── leggera morbidezza per somigliare alla foto ──
        if (o.Softness > 0)
        {
            int rad = 1;
            var pr = new float[W * H]; var pg = new float[W * H]; var pb = new float[W * H];
            for (int i = 0; i < W * H; i++) { pr[i] = layerR[i] * alpha[i]; pg[i] = layerG[i] * alpha[i]; pb[i] = layerB[i] * alpha[i]; }
            var br = BoxBlur(pr, W, H, rad); var bg = BoxBlur(pg, W, H, rad); var bb = BoxBlur(pb, W, H, rad); var ba = BoxBlur(alpha, W, H, rad);
            float k = (float)Math.Clamp(o.Softness, 0, 1);
            for (int i = 0; i < W * H; i++)
            {
                float a2 = alpha[i] * (1 - k) + ba[i] * k;
                if (a2 <= 1e-4f) { alpha[i] = 0; continue; }
                float r2 = (pr[i] * (1 - k) + br[i] * k) / a2, g2 = (pg[i] * (1 - k) + bg[i] * k) / a2, b2 = (pb[i] * (1 - k) + bb[i] * k) / a2;
                layerR[i] = r2; layerG[i] = g2; layerB[i] = b2; alpha[i] = a2;
            }
        }

        // ── composizione ──
        var layer = new Rgba(W, H);
        var comp = new Rgba(W, H);
        var raw = new Rgba(W, H);
        for (int i = 0; i < W * H; i++)
        {
            float a = alpha[i] * (float)o.Opacity;
            byte r = B8(layerR[i]), g = B8(layerG[i]), b = B8(layerB[i]);
            raw.Px[4 * i] = r; raw.Px[4 * i + 1] = g; raw.Px[4 * i + 2] = b; raw.Px[4 * i + 3] = (byte)Math.Round(a * 255);
            if (mask != null) a *= 1 - mask.Px[4 * i + 3] / 255f;
            layer.Px[4 * i] = r; layer.Px[4 * i + 1] = g; layer.Px[4 * i + 2] = b; layer.Px[4 * i + 3] = (byte)Math.Round(a * 255);
            for (int k = 0; k < 3; k++)
            {
                double src = k == 0 ? r : k == 1 ? g : b;
                comp.Px[4 * i + k] = (byte)Math.Round(photo.Px[4 * i + k] * (1 - a) + src * a);
            }
            comp.Px[4 * i + 3] = 255;
        }
        return new Result(comp, layer, raw);
    }

    static byte B8(float v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[w * h]; var dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            double s = 0; int n = 0;
            for (int x = -r; x <= r; x++) if (x >= 0 && x < w) { s += src[y * w + x]; n++; }
            for (int x = 0; x < w; x++)
            {
                tmp[y * w + x] = (float)(s / n);
                int xo = x - r, xi = x + r + 1;
                if (xo >= 0) { s -= src[y * w + xo]; n--; }
                if (xi < w) { s += src[y * w + xi]; n++; }
            }
        }
        for (int x = 0; x < w; x++)
        {
            double s = 0; int n = 0;
            for (int y = -r; y <= r; y++) if (y >= 0 && y < h) { s += tmp[y * w + x]; n++; }
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (float)(s / n);
                int yo = y - r, yi = y + r + 1;
                if (yo >= 0) { s -= tmp[yo * w + x]; n--; }
                if (yi < h) { s += tmp[yi * w + x]; n++; }
            }
        }
        return dst;
    }

    /// <summary>Triangolo in coordinate camera: taglio sul piano vicino, rasterizzazione con prospettiva corretta.</summary>
    static void Tri(Raster ras, PhotoCamera cam, int S, double near, CV a, CV b, CV c,
                    TexMips? tex, (double r, double g, double b)? baseCol, double shade, bool depthOnly, bool alphaMask = false)
    {
        // taglio sul piano z = near (Sutherland–Hodgman)
        Span<CV> inp = stackalloc CV[4]; Span<CV> outp = stackalloc CV[4];
        int n = 0, m = 0;
        inp[0] = a; inp[1] = b; inp[2] = c; n = 3;
        for (int i = 0; i < n; i++)
        {
            var p = inp[i]; var q = inp[(i + 1) % n];
            bool pin = p.Z >= near, qin = q.Z >= near;
            if (pin) outp[m++] = p;
            if (pin != qin)
            {
                double t = (near - p.Z) / (q.Z - p.Z);
                outp[m++] = new CV(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, near, p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t);
            }
        }
        if (m < 3) return;
        for (int k = 1; k + 1 < m; k++) RasterTri(ras, cam, S, outp[0], outp[k], outp[k + 1], tex, baseCol, shade, depthOnly, alphaMask);
    }

    static void RasterTri(Raster ras, PhotoCamera cam, int S, CV a, CV b, CV c, TexMips? tex, (double r, double g, double b)? baseCol, double shade, bool depthOnly, bool alphaMask)
    {
        double F = cam.F * S, cx0 = cam.W * S / 2.0, cy0 = cam.H * S / 2.0;
        double ax = cx0 + F * a.X / a.Z, ay = cy0 - F * a.Y / a.Z;
        double bx = cx0 + F * b.X / b.Z, by = cy0 - F * b.Y / b.Z;
        double cx = cx0 + F * c.X / c.Z, cy = cy0 - F * c.Y / c.Z;
        double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.Abs(area) < 1e-9) return;
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(ras.W - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(ras.H - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        if (x0 > x1 || y0 > y1) return;
        double iza = 1 / a.Z, izb = 1 / b.Z, izc = 1 / c.Z;
        double uza = a.U * iza, uzb = b.U * izb, uzc = c.U * izc, vza = a.V * iza, vzb = b.V * izb, vzc = c.V * izc;
        double inv = 1 / area;

        (double u, double v, double z) At(double px, double py)
        {
            double w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
            double w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
            double w2 = 1 - w0 - w1;
            double iz = w0 * iza + w1 * izb + w2 * izc;
            return ((w0 * uza + w1 * uzb + w2 * uzc) / iz, (w0 * vza + w1 * vzb + w2 * vzc) / iz, 1 / iz);
        }

        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5;
                double w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                double w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                double w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                double iz = w0 * iza + w1 * izb + w2 * izc;
                double z = 1 / iz;
                int i = y * ras.W + x;
                if (z >= ras.Depth[i] - 1e-4 * z) continue;
                if (depthOnly) { ras.Depth[i] = (float)z; ras.Cov[i] = false; continue; }
                double r, g, bl;
                if (tex != null)
                {
                    double u = (w0 * uza + w1 * uzb + w2 * uzc) * z, v = (w0 * vza + w1 * vzb + w2 * vzc) * z;
                    // livello di dettaglio dalla variazione delle UV tra pixel vicini
                    var (u1, v1, _) = At(px + 1, py); var (u2, v2, _) = At(px, py + 1);
                    var t0 = tex.Levels[0];
                    double du = Math.Max(Math.Abs(u1 - u) * t0.W + Math.Abs(v1 - v) * t0.H, Math.Abs(u2 - u) * t0.W + Math.Abs(v2 - v) * t0.H);
                    int lod = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, du))), 0, tex.Levels.Count - 1);
                    var t = tex.Levels[lod];
                    double fx = u * t.W - 0.5, fy = v * t.H - 0.5;
                    int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                    double ex = fx - ix, ey = fy - iy;
                    var c00 = t.Get(ix, iy); var c10 = t.Get(ix + 1, iy); var c01 = t.Get(ix, iy + 1); var c11 = t.Get(ix + 1, iy + 1);
                    if (alphaMask)
                    {
                        double aa = (t.A(ix, iy) * (1 - ex) + t.A(ix + 1, iy) * ex) * (1 - ey) + (t.A(ix, iy + 1) * (1 - ex) + t.A(ix + 1, iy + 1) * ex) * ey;
                        if (aa < 128) continue;
                    }
                    r = ((c00.r * (1 - ex) + c10.r * ex) * (1 - ey) + (c01.r * (1 - ex) + c11.r * ex) * ey) / 255;
                    g = ((c00.g * (1 - ex) + c10.g * ex) * (1 - ey) + (c01.g * (1 - ex) + c11.g * ex) * ey) / 255;
                    bl = ((c00.b * (1 - ex) + c10.b * ex) * (1 - ey) + (c01.b * (1 - ex) + c11.b * ex) * ey) / 255;
                }
                else { (r, g, bl) = baseCol ?? (0.7, 0.7, 0.7); }
                ras.Depth[i] = (float)z;
                ras.R[i] = (float)(r * shade); ras.G[i] = (float)(g * shade); ras.B[i] = (float)(bl * shade);
                ras.Cov[i] = true;
            }
        }
    }
}
