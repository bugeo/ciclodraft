using CicloDraft.Model;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NetTopologySuite.Geometries;

namespace CicloDraft.IO;

/// <summary>Un oggetto trovato nella foto (auto, furgone, persona…), con la sua sagoma e la decisione presa.</summary>
public class Detection
{
    public string Kind = "";
    public int Cls;
    public float Score;
    public float X0, Y0, X1, Y1;
    /// <summary>Sagoma nella foto: rettangolo [Mx, My, Mw, Mh] e pixel (true = oggetto).</summary>
    public int Mx, My, Mw, Mh;
    public bool[] Mask = Array.Empty<bool>();
    /// <summary>Punti di appoggio a terra che cadono nel progetto, su quelli calcolati.</summary>
    public int Inside, Contacts;
    public bool Remove;
    public bool IsVehicle => Cls is 2 or 3 or 5 or 7;
    public bool At(int x, int y) => x >= Mx && y >= My && x < Mx + Mw && y < My + Mh && Mask[(y - My) * Mw + (x - Mx)];
}

public class CarRemovalResult
{
    public Rgba Clean = null!;
    public List<Detection> Detections = new();
    /// <summary>Pixel ricostruiti (true) e pixel degli oggetti lasciati al loro posto.</summary>
    public bool[] Removed = Array.Empty<bool>(), Front = Array.Empty<bool>();
    public TimeSpan Time;
}

/// <summary>
/// Rimozione automatica delle auto che si sovrappongono al progetto:
/// YOLOX trova i veicoli, SAM ne ritaglia la sagoma, la sagoma si appoggia a terra con la fotocamera calcolata,
/// e se cade sui tracciati disegnati l'auto viene cancellata e ricostruita con LaMa. Le altre restano e vanno nella maschera.
/// </summary>
public static class CarRemoval
{
    public const string YoloFile = "yolox_s.onnx", SamEncFile = "vit_b-encoder-quant.onnx", SamDecFile = "vit_b-decoder-quant.onnx", LamaFile = "lama_512.onnx";
    static readonly string[] Names = { "persona", "bici", "auto", "moto", "", "autobus", "", "furgone" };

    /// <summary>Cartella dei modelli: «modelli» accanto al programma, oppure la variabile CICLODRAFT_MODELLI.</summary>
    public static string? FindModels()
    {
        var cands = new List<string>();
        if (Environment.GetEnvironmentVariable("CICLODRAFT_MODELLI") is { Length: > 0 } env) cands.Add(env);
        var b = AppContext.BaseDirectory;
        cands.Add(Path.Combine(b, "modelli"));
        cands.Add(Path.Combine(b, "..", "modelli"));
        if (Environment.ProcessPath is { } pp && Path.GetDirectoryName(pp) is { } pd) cands.Add(Path.Combine(pd, "modelli"));
        // Mac: CicloDraft.app/Contents/MacOS → la cartella «modelli» accanto a CicloDraft.app
        var full = Path.GetFullPath(b);
        int k = full.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents", StringComparison.OrdinalIgnoreCase);
        if (k > 0 && Path.GetDirectoryName(full[..(k + 4)]) is { } appDir) cands.Add(Path.Combine(appDir, "modelli"));
        return cands.FirstOrDefault(d => new[] { YoloFile, SamEncFile, SamDecFile, LamaFile }.All(f => File.Exists(Path.Combine(d, f))));
    }

    public static CarRemovalResult Run(Rgba photo, PhotoCamera cam, Project project, string modelDir, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var t0 = DateTime.Now;
        int W = photo.W, H = photo.H;
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

        // ── 1. rilevamento ──
        progress?.Report("Cerco auto e persone nella foto…");
        List<Detection> dets;
        using (var yolo = new InferenceSession(Path.Combine(modelDir, YoloFile), so)) dets = Detect(yolo, photo);
        ct.ThrowIfCancellationRequested();

        // ── 2. sagome ──
        progress?.Report($"Trovati {dets.Count} oggetti. Ritaglio le sagome (circa 20 secondi)…");
        using (var enc = new InferenceSession(Path.Combine(modelDir, SamEncFile), so))
        using (var dec = new InferenceSession(Path.Combine(modelDir, SamDecFile), so))
            Segment(enc, dec, photo, dets, ct);
        ct.ThrowIfCancellationRequested();

        // ── 3. quali toccano il progetto ──
        NetTopologySuite.Geometries.Geometry? area = null;
        var polys = project.Elements.Where(e => e.Polygon.Count >= 3).Select(e => Project.ToNts(e.Polygon)).Where(p => p != null && p.IsValid).Cast<NetTopologySuite.Geometries.Geometry>().ToList();
        if (polys.Count > 0) area = NetTopologySuite.Operation.Union.CascadedPolygonUnion.Union(polys).Buffer(0.5);
        foreach (var d in dets) Collide(d, cam, area);

        var removed = new bool[W * H]; var front = new bool[W * H];
        foreach (var d in dets)
        {
            if (d.Remove) MarkRemoval(d, removed, W, H);
            else for (int y = 0; y < d.Mh; y++) for (int x = 0; x < d.Mw; x++) if (d.Mask[y * d.Mw + x]) front[(d.My + y) * W + d.Mx + x] = true;
        }
        for (int i = 0; i < W * H; i++) if (removed[i]) front[i] = false;

        // ── 4. ricostruzione ──
        var clean = new Rgba(W, H); Array.Copy(photo.Px, clean.Px, photo.Px.Length);
        var groups = Groups(dets.Where(d => d.Remove).ToList(), removed, W, H);
        if (groups.Count > 0)
        {
            using var lama = new InferenceSession(Path.Combine(modelDir, LamaFile), so);
            var feather = Feather(removed, W, H);
            // ciò che è già stato ricostruito diventa contesto per le zone successive (dalle più piccole e lontane alle più grandi)
            var todo = (bool[])removed.Clone();
            int gi = 0;
            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Ricostruisco la foto senza le auto: zona {++gi} di {groups.Count}…");
                Inpaint(lama, clean, todo, feather, g);
                if (Environment.GetEnvironmentVariable("CICLODRAFT_DEBUG") is { Length: > 0 } dbg) { Console.WriteLine($"zona {g}"); clean.SavePng(Path.Combine(dbg, $"passo{gi}.png")); }
            }
            // secondo passaggio: ogni zona si rifà con tutto il resto già ripulito intorno (contesto migliore)
            if (groups.Count > 1)
            {
                gi = 0;
                foreach (var g in groups)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Rifinisco la ricostruzione: zona {++gi} di {groups.Count}…");
                    var only = new bool[W * H];
                    for (int y = Math.Max(0, g.By0); y <= Math.Min(H - 1, g.By1); y++)
                        for (int x = Math.Max(0, g.Bx0); x <= Math.Min(W - 1, g.Bx1); x++) only[y * W + x] = removed[y * W + x];
                    Inpaint(lama, clean, only, feather, g);
                }
            }
        }
        return new CarRemovalResult { Clean = clean, Detections = dets, Removed = removed, Front = front, Time = DateTime.Now - t0 };
    }

    // ───────────── YOLOX ─────────────

    static List<Detection> Detect(InferenceSession yolo, Rgba img)
    {
        int W = img.W, H = img.H; const int N = 640;
        double r = Math.Min((double)N / W, (double)N / H);
        int rw = (int)(W * r), rh = (int)(H * r);
        var t = new DenseTensor<float>(new[] { 1, 3, N, N });
        var buf = t.Buffer.Span; buf.Fill(114f);
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                var (cr, cg, cb) = Sample(img, (x + 0.5) / r - 0.5, (y + 0.5) / r - 0.5);
                buf[0 * N * N + y * N + x] = cb; buf[1 * N * N + y * N + x] = cg; buf[2 * N * N + y * N + x] = cr; // BGR
            }
        using var res = yolo.Run(new[] { NamedOnnxValue.CreateFromTensor(yolo.InputMetadata.Keys.First(), t) });
        var o = res.First().AsTensor<float>();
        int n = o.Dimensions[1], C = o.Dimensions[2];
        var raw = new List<Detection>();
        int idx = 0;
        foreach (int s in new[] { 8, 16, 32 })
        {
            int g = N / s;
            for (int gy = 0; gy < g; gy++)
                for (int gx = 0; gx < g; gx++, idx++)
                {
                    if (idx >= n) break;
                    float obj = o[0, idx, 4];
                    if (obj < 0.2f) continue;
                    int best = -1; float bs = 0;
                    foreach (int c in new[] { 0, 1, 2, 3, 5, 7 }) { float v = obj * o[0, idx, 5 + c]; if (v > bs) { bs = v; best = c; } }
                    if (best < 0 || bs < 0.35f) continue;
                    float cx = (o[0, idx, 0] + gx) * s, cy = (o[0, idx, 1] + gy) * s, w = MathF.Exp(o[0, idx, 2]) * s, h = MathF.Exp(o[0, idx, 3]) * s;
                    raw.Add(new Detection
                    {
                        Cls = best, Kind = Names[best], Score = bs,
                        X0 = Math.Clamp((cx - w / 2) / (float)r, 0, W - 1), Y0 = Math.Clamp((cy - h / 2) / (float)r, 0, H - 1),
                        X1 = Math.Clamp((cx + w / 2) / (float)r, 0, W - 1), Y1 = Math.Clamp((cy + h / 2) / (float)r, 0, H - 1)
                    });
                }
        }
        _ = C;
        // soppressione dei doppioni (i veicoli tra loro, le persone tra loro)
        var sel = new List<Detection>();
        foreach (var d in raw.OrderByDescending(d => d.Score))
            if (sel.All(q => q.IsVehicle != d.IsVehicle || IoU(q, d) < 0.5)) sel.Add(d);
        return sel.Where(d => d.X1 - d.X0 > 6 && d.Y1 - d.Y0 > 6).ToList();
    }

    static double IoU(Detection a, Detection b)
    {
        double x0 = Math.Max(a.X0, b.X0), y0 = Math.Max(a.Y0, b.Y0), x1 = Math.Min(a.X1, b.X1), y1 = Math.Min(a.Y1, b.Y1);
        double i = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        return i / ((a.X1 - a.X0) * (a.Y1 - a.Y0) + (b.X1 - b.X0) * (b.Y1 - b.Y0) - i);
    }

    // ───────────── SAM ─────────────

    static void Segment(InferenceSession enc, InferenceSession dec, Rgba img, List<Detection> dets, CancellationToken ct)
    {
        int W = img.W, H = img.H; const int N = 1024;
        double s = (double)N / Math.Max(W, H);
        int nw = (int)(W * s + 0.5), nh = (int)(H * s + 0.5);
        float[] mean = { 123.675f, 116.28f, 103.53f }, std = { 58.395f, 57.12f, 57.375f };
        var x = new DenseTensor<float>(new[] { 1, 3, N, N });
        var xb = x.Buffer.Span;
        for (int y = 0; y < nh; y++)
            for (int xx = 0; xx < nw; xx++)
            {
                var (r, g, b) = Sample(img, (xx + 0.5) * W / nw - 0.5, (y + 0.5) * H / nh - 0.5);
                xb[y * N + xx] = (r - mean[0]) / std[0]; xb[N * N + y * N + xx] = (g - mean[1]) / std[1]; xb[2 * N * N + y * N + xx] = (b - mean[2]) / std[2];
            }
        DenseTensor<float> emb;
        using (var er = enc.Run(new[] { NamedOnnxValue.CreateFromTensor(enc.InputMetadata.Keys.First(), x) }))
            emb = (DenseTensor<float>)er.First().AsTensor<float>().Clone();

        foreach (var d in dets)
        {
            ct.ThrowIfCancellationRequested();
            var coords = new DenseTensor<float>(new[] { (float)(d.X0 * s), (float)(d.Y0 * s), (float)(d.X1 * s), (float)(d.Y1 * s) }, new[] { 1, 2, 2 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", emb),
                NamedOnnxValue.CreateFromTensor("point_coords", coords),
                NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<float>(new float[] { 2, 3 }, new[] { 1, 2 })),
                NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new[] { 1, 1, 256, 256 })),
                NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new float[] { 0 }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new float[] { H, W }, new[] { 2 })),
            };
            using var res = dec.Run(inputs);
            var low = res.First(v => v.Name == "low_res_masks").AsTensor<float>();
            // la maschera 256×256 si riferisce al quadrato 1024 (immagine in alto a sinistra): si campiona per ogni pixel della foto
            int pad = (int)Math.Ceiling(Math.Max(d.X1 - d.X0, d.Y1 - d.Y0) * 0.08) + 4;
            d.Mx = Math.Max(0, (int)d.X0 - pad); d.My = Math.Max(0, (int)d.Y0 - pad);
            d.Mw = Math.Min(W, (int)d.X1 + pad + 1) - d.Mx; d.Mh = Math.Min(H, (int)d.Y1 + pad + 1) - d.My;
            d.Mask = new bool[d.Mw * d.Mh];
            for (int y = 0; y < d.Mh; y++)
                for (int xx = 0; xx < d.Mw; xx++)
                {
                    double u1024 = (d.Mx + xx + 0.5) * nw / W - 0.5, v1024 = (d.My + y + 0.5) * nh / H - 0.5;
                    double lu = (u1024 + 0.5) / 4 - 0.5, lv = (v1024 + 0.5) / 4 - 0.5;
                    d.Mask[y * d.Mw + xx] = Bilinear(low, lu, lv) > 0;
                }
        }
    }

    static float Bilinear(Tensor<float> m, double u, double v)
    {
        u = Math.Clamp(u, 0, 255); v = Math.Clamp(v, 0, 255);
        int x0 = (int)u, y0 = (int)v, x1 = Math.Min(255, x0 + 1), y1 = Math.Min(255, y0 + 1);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float a = m[0, 0, y0, x0] * (1 - fx) + m[0, 0, y0, x1] * fx, b = m[0, 0, y1, x0] * (1 - fx) + m[0, 0, y1, x1] * fx;
        return a * (1 - fy) + b * fy;
    }

    // ───────────── l'oggetto tocca il progetto? ─────────────

    static void Collide(Detection d, PhotoCamera cam, NetTopologySuite.Geometries.Geometry? area)
    {
        d.Remove = false; d.Inside = d.Contacts = 0;
        if (area == null || !d.IsVehicle) return;   // le persone restano sempre
        var a = cam.GetAxes();
        (double x, double y)? Ground(double u, double v)
        {
            double cx = (u - cam.W / 2.0) / cam.F, cy = -(v - cam.H / 2.0) / cam.F;
            double dx = a.Fx + a.Rx * cx + a.Ux * cy, dy = a.Fy + a.Ry * cx + a.Uy * cy, dz = a.Fz + a.Rz * cx + a.Uz * cy;
            if (dz >= -1e-6) return null;
            double t = -cam.Z / dz;
            if (t > 400) return null;
            return (cam.X + dx * t, cam.Y + dy * t);
        }
        // punti di appoggio: per ogni colonna della sagoma, il pixel più basso
        var pts = new List<(double x, double y)>();
        int step = Math.Max(1, d.Mw / 40);
        for (int col = 0; col < d.Mw; col += step)
        {
            int low = -1;
            for (int row = d.Mh - 1; row >= 0; row--) if (d.Mask[row * d.Mw + col]) { low = row; break; }
            if (low < 0) continue;
            if (Ground(d.Mx + col + 0.5, d.My + low + 0.5) is { } g) pts.Add(g);
        }
        if (pts.Count == 0) return;
        // la carrozzeria si estende fino a circa 1,8 m oltre le ruote visibili, dalla parte opposta all'asse della fotocamera
        double rx = a.Rx, ry = a.Ry, rl = Math.Sqrt(rx * rx + ry * ry); if (rl < 1e-9) return;
        rx /= rl; ry /= rl;
        double lat = pts.Average(p => (p.x - cam.X) * rx + (p.y - cam.Y) * ry);
        int sgn = lat >= 0 ? 1 : -1;
        var pg = area.Factory;
        int inside = 0;
        foreach (var p in pts)
            foreach (double k in new[] { 0.0, 0.6, 1.2, 1.8 })
                if (area.Contains(pg.CreatePoint(new Coordinate(p.x + rx * sgn * k, p.y + ry * sgn * k)))) { inside++; break; }
        d.Inside = inside; d.Contacts = pts.Count;
        d.Remove = inside >= Math.Max(2, 0.2 * pts.Count);
    }

    /// <summary>Area da ricostruire: sagoma allargata un poco, più l'ombra sotto l'auto.</summary>
    static void MarkRemoval(Detection d, bool[] removed, int W, int H)
    {
        double bh = d.Y1 - d.Y0;
        int dil = (int)Math.Round(Math.Max(4, bh * 0.035));
        int shadow = (int)Math.Round(Math.Max(3, bh * 0.09));
        // colonne: dal basso della sagoma si scende di «shadow» pixel
        var m = new bool[W * H];
        for (int y = 0; y < d.Mh; y++)
            for (int x = 0; x < d.Mw; x++)
                if (d.Mask[y * d.Mw + x]) m[(d.My + y) * W + d.Mx + x] = true;
        for (int x = 0; x < d.Mw; x++)
        {
            int low = -1;
            for (int y = d.Mh - 1; y >= 0; y--) if (d.Mask[y * d.Mw + x]) { low = y; break; }
            if (low < 0) continue;
            for (int k = 1; k <= shadow; k++) { int yy = d.My + low + k; if (yy < H) m[yy * W + d.Mx + x] = true; }
        }
        int x0 = Math.Max(0, d.Mx - dil), y0 = Math.Max(0, d.My - dil), x1 = Math.Min(W - 1, d.Mx + d.Mw + dil), y1 = Math.Min(H - 1, d.My + d.Mh + shadow + dil);
        Dilate(m, W, H, dil, x0, y0, x1, y1, removed);
    }

    /// <summary>Dilatazione separabile (quadrata) della maschera m nel rettangolo, scritta in OR su dst.</summary>
    static void Dilate(bool[] m, int W, int H, int r, int x0, int y0, int x1, int y1, bool[] dst)
    {
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var tmp = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int last = int.MinValue / 2;
            for (int x = -r; x < w + r; x++)
            {
                int gx = x0 + x;
                if (gx >= 0 && gx < W && m[(y0 + y) * W + gx]) last = x;
                int c = x - r; if (c >= 0 && c < w && x - last <= 2 * r) tmp[y * w + c] = true;
            }
        }
        for (int x = 0; x < w; x++)
        {
            int last = int.MinValue / 2;
            for (int y = -r; y < h + r; y++)
            {
                if (y >= 0 && y < h && tmp[y * w + x]) last = y;
                int c = y - r; if (c >= 0 && c < h && y - last <= 2 * r) dst[(y0 + c) * W + x0 + x] = true;
            }
        }
    }

    // ───────────── LaMa ─────────────

    record struct Rect(int X, int Y, int W, int H, int Bx0, int By0, int Bx1, int By1);

    /// <summary>Riquadri da ricostruire: le auto vicine si uniscono, ogni riquadro è almeno 512 px e circa 1,8 volte la zona.</summary>
    static List<Rect> Groups(List<Detection> rem, bool[] removed, int W, int H)
    {
        var boxes = new List<(int x0, int y0, int x1, int y1)>();
        foreach (var d in rem)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            int ex = (int)((d.X1 - d.X0) * 0.4) + 20, ey = (int)((d.Y1 - d.Y0) * 0.6) + 20;
            for (int y = Math.Max(0, (int)d.Y0 - ey); y <= Math.Min(H - 1, (int)d.Y1 + ey); y++)
                for (int x = Math.Max(0, (int)d.X0 - ex); x <= Math.Min(W - 1, (int)d.X1 + ex); x++)
                    if (removed[y * W + x]) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            if (x1 >= 0) boxes.Add((x0, y0, x1, y1));
        }
        // unione dei riquadri vicini
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < boxes.Count && !merged; i++)
                for (int j = i + 1; j < boxes.Count && !merged; j++)
                {
                    var a = boxes[i]; var b = boxes[j];
                    int gap = 30;
                    if (a.x0 - gap <= b.x1 && b.x0 - gap <= a.x1 && a.y0 - gap <= b.y1 && b.y0 - gap <= a.y1)
                    {
                        var u = (Math.Min(a.x0, b.x0), Math.Min(a.y0, b.y0), Math.Max(a.x1, b.x1), Math.Max(a.y1, b.y1));
                        // non si uniscono riquadri che diventerebbero troppo grandi (perderebbero risoluzione)
                        if (Math.Max(u.Item3 - u.Item1, u.Item4 - u.Item2) > 420) continue;
                        boxes[i] = u; boxes.RemoveAt(j); merged = true;
                    }
                }
        }
        var res = new List<Rect>();
        foreach (var b in boxes.OrderBy(b => (b.x1 - b.x0) * (b.y1 - b.y0)))
        {
            int bw = b.x1 - b.x0 + 1, bh = b.y1 - b.y0 + 1;
            int side = Math.Max(512, (int)(Math.Max(bw, bh) * 1.8));
            int cw = Math.Min(side, W), ch = Math.Min(side, H);
            int cx = (b.x0 + b.x1) / 2, cy = (b.y0 + b.y1) / 2;
            int x = Math.Clamp(cx - cw / 2, 0, W - cw), y = Math.Clamp(cy - ch / 2, 0, H - ch);
            res.Add(new Rect(x, y, cw, ch, b.x0, b.y0, b.x1, b.y1));
        }
        return res;
    }

    static float[] Feather(bool[] removed, int W, int H)
    {
        var f = new float[W * H];
        for (int i = 0; i < f.Length; i++) f[i] = removed[i] ? 1 : 0;
        f = Blur(f, W, H, 2);
        for (int i = 0; i < f.Length; i++) f[i] = removed[i] ? 1 : Math.Min(1, f[i] * 1.6f);
        return f;
    }

    static void Inpaint(InferenceSession lama, Rgba img, bool[] removed, float[] feather, Rect c)
    {
        const int N = 512;
        int W = img.W;
        var im = new DenseTensor<float>(new[] { 1, 3, N, N });
        var mk = new DenseTensor<float>(new[] { 1, 1, N, N });
        var ib = im.Buffer.Span; var mb = mk.Buffer.Span;
        double sx = (double)c.W / N, sy = (double)c.H / N;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                // maschera: un pixel del riquadro ridotto è da ricostruire se lo è un qualsiasi pixel della foto che copre
                int px0 = c.X + (int)(x * sx), px1 = c.X + Math.Max((int)(x * sx), (int)((x + 1) * sx) - 1);
                int py0 = c.Y + (int)(y * sy), py1 = c.Y + Math.Max((int)(y * sy), (int)((y + 1) * sy) - 1);
                bool hole = false; double r = 0, g = 0, b = 0; int n = 0;
                for (int py = py0; py <= py1; py++)
                    for (int px = px0; px <= px1; px++)
                    {
                        int i = py * W + px;
                        if (removed[i]) hole = true;
                        r += img.Px[4 * i]; g += img.Px[4 * i + 1]; b += img.Px[4 * i + 2]; n++;
                    }
                float keep = hole ? 0 : 1;
                ib[y * N + x] = (float)(r / n / 255) * keep; ib[N * N + y * N + x] = (float)(g / n / 255) * keep; ib[2 * N * N + y * N + x] = (float)(b / n / 255) * keep;
                mb[y * N + x] = hole ? 1 : 0;
            }
        // si allarga di un pixel la maschera ridotta, per non lasciare aloni
        var m2 = mb.ToArray();
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                if (m2[y * N + x] > 0)
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= N || yy >= N) continue;
                        mb[yy * N + xx] = 1; ib[yy * N + xx] = 0; ib[N * N + yy * N + xx] = 0; ib[2 * N * N + yy * N + xx] = 0;
                    }
        var names = lama.InputMetadata.Keys.ToList();
        using var res = lama.Run(new[] { NamedOnnxValue.CreateFromTensor(names.First(n => n.Contains("image")), im), NamedOnnxValue.CreateFromTensor(names.First(n => n.Contains("mask")), mk) });
        var o = res.First().AsTensor<float>();
        float scale = 1;
        // alcune esportazioni restituiscono 0..255
        float mx = 0; for (int i = 0; i < 2000; i++) mx = Math.Max(mx, o.GetValue(i * 97 % (3 * N * N)));
        if (mx > 2) scale = 1 / 255f;
        for (int y = 0; y < c.H; y++)
            for (int x = 0; x < c.W; x++)
            {
                int gx = c.X + x, gy = c.Y + y, gi = gy * W + gx;
                if (gx < c.Bx0 - 4 || gx > c.Bx1 + 4 || gy < c.By0 - 4 || gy > c.By1 + 4) continue;   // solo la zona di questo gruppo
                float a = feather[gi];
                if (a <= 0) continue;
                double u = (x + 0.5) / sx - 0.5, v = (y + 0.5) / sy - 0.5;
                for (int k = 0; k < 3; k++)
                {
                    double val = BilinearPlane(o, k, u, v, N) * scale * 255;
                    img.Px[4 * gi + k] = (byte)Math.Clamp(Math.Round(img.Px[4 * gi + k] * (1 - a) + val * a), 0, 255);
                }
            }
        for (int y = Math.Max(0, c.By0); y <= Math.Min(img.H - 1, c.By1); y++)
            for (int x = Math.Max(0, c.Bx0); x <= Math.Min(W - 1, c.Bx1); x++) removed[y * W + x] = false;
    }

    static double BilinearPlane(Tensor<float> t, int ch, double u, double v, int N)
    {
        u = Math.Clamp(u, 0, N - 1); v = Math.Clamp(v, 0, N - 1);
        int x0 = (int)u, y0 = (int)v, x1 = Math.Min(N - 1, x0 + 1), y1 = Math.Min(N - 1, y0 + 1);
        double fx = u - x0, fy = v - y0;
        double a = t[0, ch, y0, x0] * (1 - fx) + t[0, ch, y0, x1] * fx, b = t[0, ch, y1, x0] * (1 - fx) + t[0, ch, y1, x1] * fx;
        return a * (1 - fy) + b * fy;
    }

    // ───────────── utilità ─────────────

    static (float r, float g, float b) Sample(Rgba img, double u, double v)
    {
        u = Math.Clamp(u, 0, img.W - 1); v = Math.Clamp(v, 0, img.H - 1);
        int x0 = (int)u, y0 = (int)v, x1 = Math.Min(img.W - 1, x0 + 1), y1 = Math.Min(img.H - 1, y0 + 1);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float C(int x, int y, int k) => img.Px[4 * (y * img.W + x) + k];
        float L(int k) => (C(x0, y0, k) * (1 - fx) + C(x1, y0, k) * fx) * (1 - fy) + (C(x0, y1, k) * (1 - fx) + C(x1, y1, k) * fx) * fy;
        return (L(0), L(1), L(2));
    }

    static float[] Blur(float[] src, int w, int h, int r)
    {
        var tmp = new float[w * h]; var dst = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int xx = x + k; if (xx >= 0 && xx < w) { s += src[y * w + xx]; n++; } }
                tmp[y * w + x] = s / n;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int yy = y + k; if (yy >= 0 && yy < h) { s += tmp[yy * w + x]; n++; } }
                dst[y * w + x] = s / n;
            }
        return dst;
    }

    /// <summary>Immagine di controllo: in rosso le auto rimosse, in blu gli oggetti lasciati (aggiunti alla maschera).</summary>
    public static Rgba Overlay(Rgba photo, CarRemovalResult r)
    {
        var o = new Rgba(photo.W, photo.H); Array.Copy(photo.Px, o.Px, o.Px.Length);
        for (int i = 0; i < photo.W * photo.H; i++)
        {
            if (r.Removed[i]) Tint(o, i, 230, 40, 40, 0.55);
            else if (r.Front[i]) Tint(o, i, 30, 110, 255, 0.45);
        }
        return o;
    }

    static void Tint(Rgba o, int i, int r, int g, int b, double a)
    {
        o.Px[4 * i] = (byte)(o.Px[4 * i] * (1 - a) + r * a); o.Px[4 * i + 1] = (byte)(o.Px[4 * i + 1] * (1 - a) + g * a); o.Px[4 * i + 2] = (byte)(o.Px[4 * i + 2] * (1 - a) + b * a);
    }
}
