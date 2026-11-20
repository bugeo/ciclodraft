using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CicloDraft.Controls;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;

namespace CicloDraft;

public record PairRow(ControlPair Pair, int N, string Text, string Err, IBrush ErrBrush);

public partial class PhotoView : UserControl
{
    readonly Editor? _ed;
    readonly ObservableCollection<PairRow> _rows = new();
    PhotoInsertFile _file = new();
    string? _photoPath;
    Rgba? _photo, _mask, _clean;   // _clean: la foto senza le auto che toccano il progetto
    bool _removing;
    PhotoCamera? _cam;       // coordinate locali
    Point? _pendingPhoto;
    ControlPair? _sel;
    CancellationTokenSource? _solveCts;
    bool _rendering, _renderAgain, _updating;
    DispatcherTimer? _renderTimer;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public PhotoView() : this(null) { }

    public PhotoView(Editor? editor)
    {
        InitializeComponent();
        _ed = editor;
        PairsList.ItemsSource = _rows;
        PickMap.Data = _ed?.Data; PickMap.Project = _ed?.Project;

        OpenPhotoBtn.Click += async (_, _) => await OpenPhotoDialog();
        ModePoints.Click += (_, _) => SetMode(PhotoMode.Points);
        ModeVerify.Click += (_, _) => SetMode(PhotoMode.Verify);
        ModeInsert.Click += (_, _) => SetMode(PhotoMode.Insert);
        PreviewBtn.Click += (_, _) =>
        {
            if (_cam == null) { Status("Per l'anteprima serve la fotocamera: inserisci almeno 4 punti di controllo."); return; }
            SetMode(PhotoMode.Insert);
            _ = RenderNow();
        };
        CompareBtn.AddHandler(PointerPressedEvent, (_, _) => { Photo.ShowOriginal = true; Photo.InvalidateVisual(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CompareBtn.AddHandler(PointerReleasedEvent, (_, _) => { Photo.ShowOriginal = false; Photo.InvalidateVisual(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CompareBtn.PointerCaptureLost += (_, _) => { Photo.ShowOriginal = false; Photo.InvalidateVisual(); };
        RotLeftBtn.Click += (_, _) => PickMap.RotateBy(15);
        RotRightBtn.Click += (_, _) => PickMap.RotateBy(-15);
        RotNorthBtn.Click += (_, _) => PickMap.SetRotation(0);
        RotCamBtn.Click += (_, _) =>
        {
            if (_cam == null) { Status("La vista «come la foto» è disponibile dopo il calcolo della fotocamera."); return; }
            PickMap.SetRotation(90 - _cam.Yaw); // la direzione di vista della foto verso l'alto
        };
        ShowProjectCheck.IsCheckedChanged += (_, _) => { PickMap.ShowProject = ShowProjectCheck.IsChecked == true; PickMap.InvalidateVisual(); };
        BrushPaint.Click += (_, _) => SetBrush(BrushPaint.IsChecked == true ? BrushMode.Paint : BrushMode.None);
        BrushErase.Click += (_, _) => SetBrush(BrushErase.IsChecked == true ? BrushMode.Erase : BrushMode.None);
        BrushSlider.ValueChanged += (_, _) => { Photo.BrushRadius = BrushSlider.Value; Photo.InvalidateVisual(); };
        ClearMaskBtn.Click += (_, _) =>
        {
            if (_mask == null) return;
            Array.Clear(_mask.Px); Photo.RefreshMaskBitmap(); Photo.InvalidateVisual(); SaveSidecar(); ScheduleRender();
        };
        ExportBtn.Click += async (_, _) => await Export();
        RemoveCarsBtn.Click += async (_, _) => await RemoveCars();
        CleanCheck.IsCheckedChanged += (_, _) => { if (!_updating) ScheduleRender(0); };
        foreach (var s in new[] { ExposureSlider, ShadowSlider, OpacitySlider }) s.ValueChanged += (_, _) => { UpdateSliderTexts(); ScheduleRender(); };
        foreach (var c in new[] { MarkingsCheck, BikeSymCheck, LightsCheck }) c.IsCheckedChanged += (_, _) => ScheduleRender();
        HeightHintCombo.SelectionChanged += (_, _) => { if (!_updating) { _file.CameraHeightHint = HeightHint; Solve(); } };

        Photo.PhotoClicked += p =>
        {
            _pendingPhoto = p; Photo.Pending = p; PickMap.AwaitingPick = true;
            Status("Ora clicca lo stesso punto sulla mappa a destra (Maiusc+clic per una linea). Esc per annullare.");
            Photo.InvalidateVisual(); PickMap.InvalidateVisual();
        };
        Photo.MaskChanged += () => { SaveSidecar(); ScheduleRender(); };
        PickMap.Picked += (pts, line) => AddPair(pts, line);

        PairsList.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _sel = (PairsList.SelectedItem as PairRow)?.Pair;
            Dispatcher.UIThread.Post(RefreshPairsView);
        };
        PairZBox.ValueChanged += (_, _) =>
        {
            if (_updating || _sel == null || PairZBox.Value is not { } v) return;
            _sel.Z = (double)v; SaveSidecar(); Solve();
        };
        UseHeightBtn.Click += (_, _) =>
        {
            if (_sel == null) return;
            var h = BuildingHeightAt(_sel);
            if (h is { } hv) { _sel.Z = Math.Round(hv, 2); SaveSidecar(); Solve(); }
            else Status("Nessun edificio con altezza nei dati in quel punto: scrivi la quota a mano.");
        };
        DeletePairBtn.Click += (_, _) =>
        {
            if (_sel == null) return;
            _file.Pairs.Remove(_sel); _sel = null; SaveSidecar(); Solve();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _pendingPhoto = null; Photo.Pending = null; PickMap.AwaitingPick = false; SetBrush(BrushMode.None); Photo.InvalidateVisual(); PickMap.InvalidateVisual(); }
            if (e.Key == Key.Delete && _sel != null && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not NumericUpDown) { _file.Pairs.Remove(_sel); _sel = null; SaveSidecar(); Solve(); }
        };
        CloseViewBtn.Click += (_, _) => CloseRequested?.Invoke();
        AttachedToVisualTree += (_, _) =>
        {
            if (_ed?.Data != null) PickMap.ZoomTo(_ed.Project.Elements.Count > 0
                ? _ed.Project.Elements.Where(e => e.Polygon.Count > 0).Select(e => BBox.Of(e.Polygon)).Aggregate((a, b) => a.Union(b))
                : _ed.Extent());
            UpdateSliderTexts();
        };
    }

    double HeightHint => HeightHintCombo.SelectedIndex switch { 1 => 2.5, 2 => 6.0, _ => 1.7 };
    V2 Origin => _ed?.Data?.Origin ?? new V2(0, 0);
    void Status(string s) => StatusText.Text = s;

    void UpdateSliderTexts()
    {
        ExposureText.Text = ExposureSlider.Value.ToString("0.00", It);
        ShadowText.Text = ShadowSlider.Value.ToString("0.00", It);
        OpacityText.Text = OpacitySlider.Value.ToString("0.00", It);
    }

    // ───────────── foto e file dei punti ─────────────

    async Task OpenPhotoDialog()
    {
        var res = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri la foto della via", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Immagini") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png" } } }
        });
        if (res.Count > 0 && res[0].TryGetLocalPath() is { } p) await OpenPhoto(p);
    }

    static string SidecarPath(string photo) => Path.ChangeExtension(photo, null) + ".fotoinserimento.json";
    static string MaskPath(string photo) => Path.ChangeExtension(photo, null) + ".maschera.png";
    static string CleanPath(string photo) => Path.ChangeExtension(photo, null) + ".senza_auto.png";

    public async Task OpenPhoto(string path)
    {
        try
        {
            _photo = await Task.Run(() => Rgba.Load(path));
            _photoPath = path;
            _mask = File.Exists(MaskPath(path)) ? Rgba.Load(MaskPath(path)) : new Rgba(_photo.W, _photo.H);
            if (_mask.W != _photo.W || _mask.H != _photo.H) _mask = new Rgba(_photo.W, _photo.H);
            _clean = File.Exists(CleanPath(path)) ? Rgba.Load(CleanPath(path)) : null;
            if (_clean != null && (_clean.W != _photo.W || _clean.H != _photo.H)) _clean = null;
            _file = File.Exists(SidecarPath(path)) ? PhotoInsertFile.Load(SidecarPath(path)) : new PhotoInsertFile { CameraHeightHint = HeightHint };
            _file.Photo = Path.GetFileName(path);
            _updating = true;
            HeightHintCombo.SelectedIndex = _file.CameraHeightHint >= 5 ? 2 : _file.CameraHeightHint >= 2.2 ? 1 : 0;
            CleanCheck.IsEnabled = _clean != null; CleanCheck.IsChecked = _clean != null;
            _updating = false;
            Photo.Photo = PhotoCanvas.ToBitmap(_photo);
            Photo.Mask = _mask; Photo.RefreshMaskBitmap();
            Photo.Result = null; _cam = null;
            Photo.Fit();
            TitleText.Text = Path.GetFileName(path);
            Status($"Foto {Path.GetFileName(path)} ({_photo.W} × {_photo.H} px)." + (_file.Pairs.Count > 0 ? $" Ripresi {_file.Pairs.Count} punti salvati." : " Clicca un punto riconoscibile sulla foto."));
            Solve();
            PhotoOpened?.Invoke(path);
        }
        catch (Exception ex) { Status("Non riesco ad aprire la foto: " + ex.Message); }
    }

    void SaveSidecar()
    {
        if (_photoPath == null) return;
        try
        {
            _file.Mask = Path.GetFileName(MaskPath(_photoPath));
            _file.CameraHeightHint = HeightHint;
            _file.Camera = _cam is { } c ? PhotoCamera.FromArray(new[] { c.X + Origin.X, c.Y + Origin.Y, c.Z, c.Yaw, c.Pitch, c.Roll, c.F }, c.W, c.H) : null;
            _file.Save(SidecarPath(_photoPath));
            if (_mask != null) _mask.SavePng(MaskPath(_photoPath));
            Saved?.Invoke(_photoPath);
        }
        catch (Exception ex) { Status("Non riesco a salvare i punti accanto alla foto: " + ex.Message); }
    }

    // ───────────── punti ─────────────

    void AddPair(List<V2> pts, bool line)
    {
        if (_pendingPhoto is not { } p || _photo == null) return;
        var pair = new ControlPair
        {
            U = Math.Round(p.X, 1), V = Math.Round(p.Y, 1), Kind = line ? PairKind.Line : PairKind.Point,
            Map = pts.Select(v => new[] { Math.Round(v.X + Origin.X, 3), Math.Round(v.Y + Origin.Y, 3) }).ToList(),
            Note = line ? "linea" : "punto"
        };
        _file.Pairs.Add(pair);
        _sel = pair;
        _pendingPhoto = null; Photo.Pending = null; PickMap.AwaitingPick = false;
        SaveSidecar();
        Solve();
    }

    double? BuildingHeightAt(ControlPair pair)
    {
        if (_ed?.Data == null || pair.Map.Count == 0) return null;
        var q = new V2(pair.Map[0][0] - Origin.X, pair.Map[0][1] - Origin.Y);
        var pt = new NetTopologySuite.Geometries.Point(q.X, q.Y);
        double? best = null; double bd = 1.0;
        foreach (var l in _ed.Data.Layers.Where(l => (LayerRole)l.Role is LayerRole.Edifici3D or LayerRole.Muri))
            foreach (var lp in l.Polys)
            {
                if (lp.Height is not (> 0.5 and < 400)) continue;
                double d = lp.Poly.Distance(pt);
                if (d < bd || (d == 0 && best != null && lp.Height > best)) { bd = d; best = lp.Height; }
            }
        return best;
    }

    void RefreshPairsView()
    {
        _updating = true;
        try
        {
            _rows.Clear();
            int n = 1;
            foreach (var p in _file.Pairs)
            {
                string kind = p.Kind == PairKind.Line ? "linea" : "punto";
                string z = Math.Abs(p.Z) > 1e-6 ? $" · quota {p.Z.ToString("0.0", It)} m" : " · a terra";
                string err = double.IsNaN(p.Residual) || _cam == null ? "" : $"{p.Residual.ToString("0.0", It)} px";
                IBrush brush = double.IsNaN(p.Residual) || _cam == null ? Brushes.Gray : p.Residual < 6 ? new SolidColorBrush(Color.Parse("#5EE09A")) : p.Residual < 15 ? new SolidColorBrush(Color.Parse("#FFB547")) : new SolidColorBrush(Color.Parse("#FF6B6B"));
                _rows.Add(new PairRow(p, n++, (string.IsNullOrWhiteSpace(p.Note) || p.Note is "punto" or "linea" ? kind : p.Note) + z, err, brush));
            }
            PairsList.SelectedItem = _rows.FirstOrDefault(r => r.Pair == _sel);
            PairEditor.IsVisible = _sel != null;
            if (_sel != null) PairZBox.Value = (decimal)_sel.Z;

            // marcatori sulla foto e sulla mappa
            Photo.Marks = _file.Pairs.Select((p, i) =>
            {
                Point? pred = null;
                if (_cam != null && p.Kind == PairKind.Point && p.Map.Count > 0)
                {
                    var pr = _cam.Project(p.Map[0][0] - Origin.X, p.Map[0][1] - Origin.Y, p.Z);
                    if (pr.depth > 0) pred = new Point(pr.u, pr.v);
                }
                return (i + 1, new Point(p.U, p.V), pred, p == _sel);
            }).ToList();
            PickMap.Marks = _file.Pairs.Select((p, i) => (i + 1, p.Map.Select(m => new V2(m[0] - Origin.X, m[1] - Origin.Y)).ToList(), p.Kind == PairKind.Line, p == _sel)).ToList();
            PickMap.Camera = _cam;
            BuildWire();
            Photo.InvalidateVisual(); PickMap.InvalidateVisual();
        }
        finally { _updating = false; }
    }

    // ───────────── calcolo della fotocamera ─────────────

    void Solve()
    {
        if (_photo == null) { RefreshPairsView(); return; }
        _solveCts?.Cancel();
        var cts = _solveCts = new CancellationTokenSource();
        var pairs = _file.Pairs.ToList(); var origin = Origin; int w = _photo.W, h = _photo.H; double hint = HeightHint;
        var start = _cam;
        int eq = pairs.Sum(p => p.Kind == PairKind.Point ? 2 : 1);
        if (eq < 6)
        {
            _cam = null;
            CameraText.Text = $"Fotocamera non ancora calcolata: servono altri punti ({pairs.Count} inseriti; minimo 4, meglio 6–8).";
            RefreshPairsView();
            return;
        }
        CameraText.Text = "Calcolo della fotocamera…";
        Task.Run(() => CameraSolver.Solve(pairs, origin, w, h, hint, start)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (t.Exception != null || t.Result == null) { CameraText.Text = "Calcolo non riuscito: controlla i punti."; _cam = null; RefreshPairsView(); return; }
                var r = t.Result;
                _cam = r.Camera;
                for (int i = 0; i < pairs.Count; i++) pairs[i].Residual = r.PairResiduals[i];
                var c = r.Camera;
                CameraText.Text = $"Fotocamera: E {(c.X + origin.X).ToString("0.0", It)}  N {(c.Y + origin.Y).ToString("0.0", It)}  ·  quota {c.Z.ToString("0.00", It)} m\n" +
                                  $"direzione {Bearing(c.Yaw)}  ·  inclinazione {c.Pitch.ToString("0.0", It)}°  ·  campo {c.HFov.ToString("0", It)}°\n" +
                                  $"Errore medio {r.Rms.ToString("0.0", It)} px" + (r.Warning != null ? "\n⚠ " + r.Warning : r.Rms < 6 ? " · buono" : "");
                SaveSidecar();
                RefreshPairsView();
                if (Photo.Mode == PhotoMode.Insert) ScheduleRender();
                Status(r.Rms < 6 ? "Fotocamera calcolata. Controlla in «Verifica» che il disegno della mappa combaci con la foto, poi passa a «Fotoinserimento»."
                                 : "Fotocamera calcolata, ma l'errore è alto: il punto con l'errore maggiore (in rosso) è probabilmente sbagliato.");
            });
        });
        RefreshPairsView();
    }

    static string Bearing(double yawDeg)
    {
        // yaw: 0 = Est, antiorario → azimut: 0 = Nord, orario
        double az = ((90 - yawDeg) % 360 + 360) % 360;
        string[] n = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };
        return $"{n[(int)Math.Round(az / 45) % 8]} ({az.ToString("0", It)}°)";
    }

    // ───────────── verifica: disegno della mappa sulla foto ─────────────

    void BuildWire()
    {
        Photo.Wire.Clear();
        if (_cam == null || _ed?.Data == null) return;
        var red = new SolidColorBrush(Color.FromArgb(220, 255, 64, 64));
        var yellow = new SolidColorBrush(Color.FromArgb(220, 255, 214, 0));
        var cyan = new SolidColorBrush(Color.FromArgb(220, 0, 229, 255));
        var blue = new SolidColorBrush(Color.FromArgb(240, 41, 121, 255));
        void Seg(V2 a, V2 b, double z, IBrush col, double w)
        {
            var s = _cam.ProjectSegment(a.X, a.Y, z, b.X, b.Y, z);
            if (s is { } r) Photo.Wire.Add((new Point(r.a.u, r.a.v), new Point(r.b.u, r.b.v), col, w));
        }
        var camP = new V2(_cam.X, _cam.Y);
        foreach (var l in _ed.Data.Layers.Where(l => (LayerRole)l.Role is LayerRole.Muri or LayerRole.Marciapiedi or LayerRole.Edifici3D))
        {
            bool walls = (LayerRole)l.Role == LayerRole.Muri;
            foreach (var r in l.Rings)
            {
                if (!r.Box.Contains(camP, 300)) continue;
                for (int i = 0; i < r.Pts.Count; i++)
                {
                    int j = (i + 1) % r.Pts.Count; if (!r.Closed && j == 0) break;
                    Seg(r.Pts[i], r.Pts[j], 0, (LayerRole)l.Role == LayerRole.Marciapiedi ? cyan : red, walls ? 1.4 : 1);
                }
            }
            if ((LayerRole)l.Role == LayerRole.Edifici3D)
                foreach (var lp in l.Polys)
                {
                    if (lp.Height is not (> 0.5 and < 400)) continue;
                    var c = lp.Poly.ExteriorRing.Coordinates;
                    if (!new BBox(c.Min(q => q.X), c.Min(q => q.Y), c.Max(q => q.X), c.Max(q => q.Y)).Contains(camP, 300)) continue;
                    for (int i = 0; i + 1 < c.Length; i++) Seg(new V2(c[i].X, c[i].Y), new V2(c[i + 1].X, c[i + 1].Y), lp.Height, yellow, 1);
                }
        }
        foreach (var e in _ed.Project.Elements.Where(e => e.Polygon.Count >= 3))
            for (int i = 0; i < e.Polygon.Count; i++) Seg(e.Polygon[i], e.Polygon[(i + 1) % e.Polygon.Count], 0.02, blue, 2);
    }

    // ───────────── modalità, pennello, resa ─────────────

    void SetMode(PhotoMode m)
    {
        Photo.Mode = m;
        ModePoints.IsChecked = m == PhotoMode.Points; ModeVerify.IsChecked = m == PhotoMode.Verify; ModeInsert.IsChecked = m == PhotoMode.Insert;
        if (m == PhotoMode.Insert)
        {
            if (_cam == null) Status("Prima calcola la fotocamera con almeno 4 punti di controllo.");
            else ScheduleRender(0);
        }
        else if (m == PhotoMode.Verify)
            Status("Rosso: edifici a terra · giallo: gronde (altezze del DBGT) · azzurro: marciapiedi · blu: progetto. Se combaciano con la foto, la fotocamera è giusta.");
        Photo.InvalidateVisual();
    }

    void SetBrush(BrushMode b)
    {
        Photo.Brush = b;
        BrushPaint.IsChecked = b == BrushMode.Paint; BrushErase.IsChecked = b == BrushMode.Erase;
        if (b != BrushMode.None) Status("Dipingi sulla foto ciò che deve restare davanti al progetto (auto, persone, pali). Ctrl+rotella cambia la dimensione del pennello.");
        Photo.InvalidateVisual();
    }

    PhotoRenderOptions RenderOptions() => new()
    {
        Exposure = ExposureSlider.Value, ShadowTransfer = ShadowSlider.Value, Opacity = OpacitySlider.Value,
        Markings = MarkingsCheck.IsChecked == true, BikeSymbols = BikeSymCheck.IsChecked == true, StreetLights = LightsCheck.IsChecked == true
    };

    void ScheduleRender(int delayMs = 250)
    {
        if (Photo.Mode != PhotoMode.Insert || _cam == null) return;
        _renderTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => { _renderTimer!.Stop(); await RenderNow(); });
        _renderTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _renderTimer.Stop(); _renderTimer.Start();
    }

    public async Task<PhotoRender.Result?> RenderNow()
    {
        if (_cam == null || _photo == null || _ed == null) return null;
        if (_rendering) { _renderAgain = true; return null; }
        _rendering = true;
        Status("Disegno del progetto sulla foto…");
        try
        {
            var cam = _cam; var photo = CleanCheck.IsChecked == true && _clean != null ? _clean : _photo; var mask = _mask; var o = RenderOptions();
            var res = await Task.Run(() => PhotoRender.Render(cam, photo, mask, _ed.Project, _ed.Data, o));
            Photo.Result = PhotoCanvas.ToBitmap(res.Composite);
            Photo.InvalidateVisual();
            Status("Fotoinserimento pronto. Dipingi la maschera su ciò che deve stare davanti, regola luminosità e ombre, poi «Esporta immagine».");
            return res;
        }
        catch (Exception ex) { Status("Disegno non riuscito: " + ex.Message); return null; }
        finally
        {
            _rendering = false;
            if (_renderAgain) { _renderAgain = false; ScheduleRender(); }
        }
    }

    async Task Export()
    {
        if (_cam == null || _photoPath == null) { Status("Prima calcola la fotocamera."); return; }
        var f = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Esporta il fotoinserimento", SuggestedFileName = Path.GetFileNameWithoutExtension(_photoPath) + "_progetto.png",
            DefaultExtension = "png", ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } }, new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg" } } }
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        await ExportTo(path);
    }

    public async Task ExportTo(string path)
    {
        while (_rendering) await Task.Delay(50);   // si aspetta un eventuale disegno in corso
        var res = await RenderNow();
        if (res == null) return;
        await Task.Run(() =>
        {
            res.Composite.SavePng(path);
            res.Layer.SavePng(Path.ChangeExtension(path, null) + "_livello.png");
        });
        SaveSidecar();
        Status($"Esportati {Path.GetFileName(path)} e {Path.GetFileNameWithoutExtension(path)}_livello.png (solo il progetto, con trasparenza, per il fotoritocco).");
    }

    // ───────────── rimozione delle auto ─────────────

    public async Task RemoveCars()
    {
        if (_removing) return;
        if (_cam == null || _photo == null || _photoPath == null || _ed == null) { Status("Per riconoscere le auto sul progetto serve la fotocamera: inserisci prima i punti di controllo."); return; }
        if (!_ed.Project.Elements.Any(e => e.Polygon.Count >= 3)) { Status("Nel progetto non ci sono ancora elementi disegnati: nessuna auto da togliere."); return; }
        var dir = CarRemoval.FindModels();
        if (dir == null)
        {
            Status($"Mancano i modelli per riconoscere le auto: copia la cartella «modelli» ({CarRemoval.YoloFile}, {CarRemoval.SamEncFile}, {CarRemoval.SamDecFile}, {CarRemoval.LamaFile}) accanto a CicloDraft.exe.");
            return;
        }
        _removing = true; RemoveCarsBtn.IsEnabled = false;
        try
        {
            var cam = _cam; var photo = _photo; var project = _ed.Project;
            var prog = new Progress<string>(m => Status(m + " (la prima volta può richiedere circa un minuto)"));
            var r = await Task.Run(() => CarRemoval.Run(photo, cam, project, dir, prog));
            if (_photo != photo) return;   // nel frattempo è stata aperta un'altra foto
            _clean = r.Clean;
            // le auto rimaste e le persone passano davanti al progetto; dove le auto sono state tolte la maschera si svuota
            _mask ??= new Rgba(photo.W, photo.H);
            for (int i = 0; i < photo.W * photo.H; i++)
            {
                if (r.Front[i]) _mask.Px[4 * i + 3] = 255;
                else if (r.Removed[i]) _mask.Px[4 * i + 3] = 0;
            }
            Photo.Mask = _mask; Photo.RefreshMaskBitmap();
            await Task.Run(() => _clean.SavePng(CleanPath(_photoPath)));
            SaveSidecar();
            _updating = true; CleanCheck.IsEnabled = true; CleanCheck.IsChecked = true; _updating = false;
            int nRem = r.Detections.Count(d => d.Remove), nKeep = r.Detections.Count - nRem;
            SetMode(PhotoMode.Insert);
            await RenderNow();
            Status(nRem == 0
                ? $"Nessuna auto tocca i tracciati del progetto. {nKeep} oggetti (auto, persone) sono stati aggiunti alla maschera e passano davanti al progetto."
                : $"Tolte {nRem} auto che toccavano il progetto e ricostruita la foto ({r.Time.TotalSeconds:0} s). Altri {nKeep} oggetti restano e passano davanti al progetto (maschera). " +
                  "«Senza auto» confronta con la foto originale; con la maschera e la gomma si correggono i dettagli.");
        }
        catch (Exception ex) { Status("Rimozione delle auto non riuscita: " + ex.Message); }
        finally { _removing = false; RemoveCarsBtn.IsEnabled = true; }
    }

    // per le prove automatiche
    /// <summary>Foto aperta nella finestra (null finché non se ne apre una).</summary>
    public string? PhotoPath => _photoPath;
    public event Action<string>? PhotoOpened;
    /// <summary>«Mappa»: si torna alla mappa.</summary>
    public event Action? CloseRequested;
    public event Action<string>? Saved;
    public PhotoInsertFile File_ => _file;
    public PhotoCamera? Camera => _cam;
    public PhotoCanvas PhotoCanvasView => Photo;
    public void SetModeForTest(PhotoMode m) => SetMode(m);
}
