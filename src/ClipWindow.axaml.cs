using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CicloDraft.Controls;
using CicloDraft.Geometry;
using CicloDraft.IO;

namespace CicloDraft;

/// <summary>Primo passo per file grandi: scelta dei livelli e dell'area, salvataggio di un GeoPackage ridotto.</summary>
public partial class ClipWindow : Window
{
    readonly List<SourceLayer> _all = new();
    readonly ObservableCollection<SourceLayer> _visible = new();
    readonly List<string> _files = new();
    readonly Action<string>? _openInMain;
    V2 _origin;
    bool _originSet;
    CancellationTokenSource? _countCts, _saveCts;
    DispatcherTimer? _countTimer;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    // Livelli utili per disegnare (nomi DBGT e nomi generici)
    static readonly string[] Suggested =
    {
        "EDIFC_CR_EDF_IS", "EDI_MIN_CR_EDF_IS", "UN_VOL", "ALBERO", "AC_PED", "AC_VEI", "AC_CIC", "EL_STR", "AR_STR", "MAR_STR", "SV_STR"
    };
    static readonly string[] SuggestedWords = { "edific", "fabbric", "build", "marciap", "pedon", "ciclab", "strad", "carregg", "viabil" };

    public ClipWindow() : this(Array.Empty<string>(), null) { }

    public ClipWindow(IEnumerable<string> files, Action<string>? openInMain)
    {
        InitializeComponent();
        _openInMain = openInMain;
        LayersList.ItemsSource = _visible;
        Map.Layers = _all;
        Map.AreaChanged += OnAreaChanged;
        Map.PointerWorldMoved += w => CoordText.Text = w is { } v ? $"E {v.X.ToString("0.00", It)}   N {v.Y.ToString("0.00", It)}" : "";

        AddFilesBtn.Click += async (_, _) => await AddFilesDialog();
        ZoomAllBtn.Click += (_, _) => ZoomAll();
        FilterBox.TextChanged += (_, _) => RefreshList();
        OnlyIncludedCheck.IsCheckedChanged += (_, _) => RefreshList();
        SuggestBtn.Click += (_, _) => { ApplySuggestions(); RefreshList(); };
        NoneBtn.Click += (_, _) => { foreach (var l in _all) { l.Include = false; l.Show = false; } RefreshList(); };

        ToolPan.Click += (_, _) => SetTool(AreaTool.Pan);
        ToolRect.Click += (_, _) => SetTool(AreaTool.Rect);
        ToolPoly.Click += (_, _) => SetTool(AreaTool.Poly);
        UseExtentBtn.Click += (_, _) => UseSelectedExtent();
        ApplyCoordsBtn.Click += (_, _) => ApplyCoords();
        ClearAreaBtn.Click += (_, _) => Map.ClearArea();
        MarginBox.ValueChanged += (_, _) => { Map.Margin = (double)(MarginBox.Value ?? 0); Map.InvalidateVisual(); ScheduleCount(); };
        KeepWholeRadio.IsCheckedChanged += (_, _) => UpdateSummary();
        CutRadio.IsCheckedChanged += (_, _) => UpdateSummary();

        SaveBtn.Click += async (_, _) => await Save(false);
        SaveOpenBtn.Click += async (_, _) => await Save(true);
        CancelBtn.Click += (_, _) => _saveCts?.Cancel();
        Map.Margin = 20;

        Opened += async (_, _) =>
        {
            foreach (var f in files) await AddFile(f);
            ZoomAll();
        };
        Closed += (_, _) => { _countCts?.Cancel(); _saveCts?.Cancel(); };
    }

    void Status(string s) => StatusText.Text = s;

    // ───────────── file e livelli ─────────────

    async Task AddFilesDialog()
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Aggiungi dati da ritagliare", AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GeoPackage o shapefile") { Patterns = new[] { "*.gpkg", "*.shp" } },
                new FilePickerFileType("GeoPackage") { Patterns = new[] { "*.gpkg" } },
                new FilePickerFileType("Shapefile") { Patterns = new[] { "*.shp" } },
            }
        });
        foreach (var f in res)
            if (f.TryGetLocalPath() is { } p) await AddFile(p);
        ZoomAll();
    }

    public async Task AddFile(string path)
    {
        if (_files.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        Status($"Lettura di {Path.GetFileName(path)}…");
        try
        {
            var layers = await Task.Run(() => SourceData.Open(path));
            _files.Add(path);
            foreach (var l in layers)
            {
                l.ColorIndex = _all.Count;
                l.PropertyChanged += Layer_PropertyChanged;
                _all.Add(l);
            }
            if (!_originSet)
            {
                var b = BBox.Empty;
                foreach (var l in layers) b = b.Union(l.Extent);
                if (!b.IsEmpty)
                {
                    _origin = new V2(Math.Round((b.MinX + b.MaxX) / 2 / 100) * 100, Math.Round((b.MinY + b.MaxY) / 2 / 100) * 100);
                    _originSet = true; Map.Origin = _origin;
                }
            }
            ApplySuggestions(layers);
            RefreshList();
            FilesText.Text = string.Join("\n", _files.Select(f => $"{Path.GetFileName(f)} · {new FileInfo(f).Length / 1048576.0:0.0} MB"));
            UpdateCrs();
            Status($"{Path.GetFileName(path)}: {layers.Count} livelli. Spunta «Tieni» per i livelli da salvare e «Mostra» per vederli sulla mappa.");
        }
        catch (Exception ex) { Status($"Non riesco a leggere {Path.GetFileName(path)}: {ex.Message}"); }
    }

    void ApplySuggestions(IEnumerable<SourceLayer>? only = null)
    {
        var set = (only ?? _all).ToList();
        foreach (var l in set)
        {
            var n = l.Name.ToLowerInvariant();
            bool sug = l.Kind == SourceKind.Shp && !l.IsPoint
                       || Suggested.Any(s => s.Equals(l.Name, StringComparison.OrdinalIgnoreCase))
                       || (!l.IsPoint && !n.EndsWith("_sr") && !n.EndsWith("_me") && SuggestedWords.Any(n.Contains));
            if (only == null) l.Include = sug;
            else if (sug) l.Include = true;
            l.Show = l.Include && (!l.IsPoint || GpkgReader.IsTreeName(l.Name));
        }
        // file piccolo senza nomi riconosciuti: si tengono tutte le linee e i poligoni
        if (only != null && !set.Any(l => l.Include) && set.Sum(l => l.Count) < 30000)
            foreach (var l in set.Where(l => !l.IsPoint)) { l.Include = true; l.Show = true; }
    }

    void RefreshList()
    {
        var f = FilterBox.Text?.Trim() ?? "";
        bool only = OnlyIncludedCheck.IsChecked == true;
        var sel = LayersList.SelectedItem;
        _visible.Clear();
        foreach (var l in _all)
            if ((f.Length == 0 || l.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) && (!only || l.Include))
                _visible.Add(l);
        if (sel != null && _visible.Contains(sel)) LayersList.SelectedItem = sel;
    }

    void Layer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SourceLayer l) return;
        if (e.PropertyName == nameof(SourceLayer.Show))
        {
            if (l.Show && l.Preview == null && !l.PreviewLoading) _ = LoadPreview(l);
            Map.InvalidateVisual();
        }
        else if (e.PropertyName == nameof(SourceLayer.Include))
        {
            if (!l.Include) l.InArea = "";
            UpdateCrs(); ScheduleCount(); Map.InvalidateVisual();
        }
    }

    async Task LoadPreview(SourceLayer l)
    {
        l.PreviewLoading = true;
        Status($"Disegno di {l.Name} ({l.Count:N0} elementi)…");
        try
        {
            var o = _origin;
            var p = await Task.Run(() => SourceData.LoadPreview(l, o, CancellationToken.None));
            l.Preview = p;
            Map.InvalidateLayer(l);
            Status($"{l.Name} visibile sulla mappa.");
        }
        catch (Exception ex) { Status($"Non riesco a disegnare {l.Name}: {ex.Message}"); l.Show = false; }
        finally { l.PreviewLoading = false; }
    }

    void ZoomAll()
    {
        var b = BBox.Empty;
        foreach (var l in _all.Where(l => l.Show || l.Include)) b = b.Union(l.Extent);
        if (b.IsEmpty) foreach (var l in _all) b = b.Union(l.Extent);
        if (Map.Area.Count >= 3) b = b.Union(Shift(BBox.Of(Map.Area), _origin));
        if (!b.IsEmpty) Map.ZoomTo(Shift(b, new V2(-_origin.X, -_origin.Y)));
    }

    void ZoomArea()
    {
        if (Map.Area.Count < 3) return;
        var b = BBox.Of(Map.Area);
        double m = Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) * 0.15 + Map.Margin;
        Map.ZoomTo(new BBox(b.MinX - m, b.MinY - m, b.MaxX + m, b.MaxY + m));
    }

    static BBox Shift(BBox b, V2 d) => new(b.MinX + d.X, b.MinY + d.Y, b.MaxX + d.X, b.MaxY + d.Y);

    void UpdateCrs()
    {
        var inc = _all.Where(l => l.Include).ToList();
        var known = inc.Select(l => l.Srs).Where(s => s is { Id: > 0 }).Select(s => s!).DistinctBy(s => s.Id).ToList();
        var main = known.FirstOrDefault() ?? _all.Select(l => l.Srs).FirstOrDefault(s => s is { Id: > 0 });
        var txt = "Sistema di riferimento: " + (main?.Label ?? "non definito");
        if (known.Count > 1) txt += "\nAttenzione: i livelli scelti hanno sistemi di riferimento diversi e non vengono riproiettati.";
        var noSrs = inc.Where(l => l.Srs == null).Select(l => l.Name).ToList();
        if (noSrs.Count > 0 && main != null)
            txt += $"\n{string.Join(", ", noSrs)}: il file non dichiara il sistema di riferimento, verrà salvato come {main.Org}:{main.OrgId}.";
        CrsText.Text = txt;
    }

    // ───────────── area ─────────────

    void SetTool(AreaTool t)
    {
        Map.Tool = t;
        ToolPan.IsChecked = t == AreaTool.Pan; ToolRect.IsChecked = t == AreaTool.Rect; ToolPoly.IsChecked = t == AreaTool.Poly;
        ToolHint.Text = t switch
        {
            AreaTool.Rect => "Trascina sulla mappa per disegnare il rettangolo. Tasto destro: sposta la mappa.",
            AreaTool.Poly => "Clic per aggiungere i vertici, doppio clic o Invio per chiudere, Backspace per togliere l'ultimo. Tasto destro: sposta la mappa.",
            _ => "Trascina per spostare la mappa, rotella per lo zoom."
        };
        Map.Focus();
    }

    void UseSelectedExtent()
    {
        if (LayersList.SelectedItem is not SourceLayer l) { Status("Seleziona un livello nell'elenco (clic sul nome), poi premi di nuovo il pulsante."); return; }
        if (l.Extent.IsEmpty) { Status($"L'estensione di {l.Name} non è nota: spunta «Mostra» per calcolarla."); return; }
        var e = l.Extent;
        Map.SetRect(new V2(e.MinX, e.MinY) - _origin, new V2(e.MaxX, e.MaxY) - _origin);
        ZoomArea();
        Status($"Area = estensione di {l.Name}.");
    }

    void ApplyCoords()
    {
        bool P(TextBox b, out double v) => double.TryParse((b.Text ?? "").Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (P(XMinBox, out var x0) && P(YMinBox, out var y0) && P(XMaxBox, out var x1) && P(YMaxBox, out var y1) && x1 > x0 && y1 > y0)
        {
            Map.SetRect(new V2(x0, y0) - _origin, new V2(x1, y1) - _origin);
            ZoomArea();
        }
        else Status("Coordinate non valide: scrivi E min < E max e N min < N max, in metri.");
    }

    void OnAreaChanged()
    {
        if (Map.Area.Count >= 3)
        {
            var b = Shift(BBox.Of(Map.Area), _origin);
            XMinBox.Text = b.MinX.ToString("0.00", CultureInfo.InvariantCulture); XMaxBox.Text = b.MaxX.ToString("0.00", CultureInfo.InvariantCulture);
            YMinBox.Text = b.MinY.ToString("0.00", CultureInfo.InvariantCulture); YMaxBox.Text = b.MaxY.ToString("0.00", CultureInfo.InvariantCulture);
        }
        ScheduleCount();
    }

    ClipOptions? Options()
    {
        if (!Map.AreaClosed || Map.Area.Count < 3) return null;
        return new ClipOptions
        {
            Area = Map.Area.Select(p => p + _origin).ToList(),
            Margin = (double)(MarginBox.Value ?? 0),
            Cut = KeepWholeRadio.IsChecked != true,
            KeepBuildingsWhole = CutKeepBuildingsRadio.IsChecked == true,
            DefaultSrs = _all.Where(l => l.Include).Select(l => l.Srs).FirstOrDefault(s => s is { Id: > 0 })
                         ?? _all.Select(l => l.Srs).FirstOrDefault(s => s is { Id: > 0 })
        };
    }

    void ScheduleCount()
    {
        _countTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, async (_, _) =>
        {
            _countTimer!.Stop();
            await CountInArea();
        });
        _countTimer.Stop(); _countTimer.Start();
    }

    async Task CountInArea()
    {
        _countCts?.Cancel();
        var o = Options();
        var inc = _all.Where(l => l.Include).ToList();
        if (o == null) { foreach (var l in inc) l.InArea = ""; UpdateSummary(); return; }
        var cts = _countCts = new CancellationTokenSource();
        SummaryText.Text = "Conteggio degli elementi nell'area…";
        try
        {
            foreach (var l in inc)
            {
                var n = await Task.Run(() => GpkgClipper.CountInside(l, o, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;
                l.InArea = $"{n.ToString("N0", It)} nell'area";
                l.LastCount = n;
            }
            UpdateSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SummaryText.Text = "Conteggio non riuscito: " + ex.Message; }
    }

    void UpdateSummary()
    {
        var inc = _all.Where(l => l.Include).ToList();
        if (Options() is not { } o) { SummaryText.Text = inc.Count == 0 ? "Scegli almeno un livello da tenere." : "Disegna l'area per vedere quanti elementi verranno salvati."; return; }
        var b = BBox.Of(o.Area);
        int tot = inc.Sum(l => l.LastCount);
        SummaryText.Text = $"Area {(b.MaxX - b.MinX + 2 * o.Margin).ToString("N0", It)} × {(b.MaxY - b.MinY + 2 * o.Margin).ToString("N0", It)} m (margine compreso)\n" +
                           $"{inc.Count} livelli, {tot.ToString("N0", It)} elementi in totale\n" +
                           (!o.Cut ? "Gli elementi che toccano l'area restano interi." :
                            o.KeepBuildingsWhole ? "Le geometrie vengono tagliate sul bordo, gli edifici restano interi." : "Le geometrie vengono tagliate sul bordo.");
    }

    // ───────────── salvataggio ─────────────

    async Task Save(bool open)
    {
        var inc = _all.Where(l => l.Include).ToList();
        if (inc.Count == 0) { Status("Spunta «Tieni» per almeno un livello."); return; }
        if (Options() is not { } o) { Status("Disegna prima l'area di interesse (rettangolo o poligono)."); return; }
        var first = _files.FirstOrDefault(f => f.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase)) ?? _files.First();
        var suggested = Path.GetFileNameWithoutExtension(first) + "_ritaglio.gpkg";
        var folder = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(first)!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva il GeoPackage ritagliato", SuggestedFileName = suggested, DefaultExtension = "gpkg",
            ShowOverwritePrompt = true, SuggestedStartLocation = folder,
            FileTypeChoices = new[] { new FilePickerFileType("GeoPackage") { Patterns = new[] { "*.gpkg" } } }
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (_files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            Status("Scegli un nome diverso dal file di partenza: il file originale non viene sovrascritto.");
            return;
        }
        await SaveTo(path, open, o, inc);
    }

    public async Task<ClipResult?> SaveTo(string path, bool open, ClipOptions o, List<SourceLayer> inc)
    {
        _saveCts = new CancellationTokenSource();
        var ct = _saveCts.Token;
        SaveBtn.IsEnabled = SaveOpenBtn.IsEnabled = false; CancelBtn.IsVisible = true;
        var progress = new Progress<string>(Status);
        try
        {
            var res = await Task.Run(() => GpkgClipper.Write(path, inc, o, progress, ct), ct);
            var empty = res.Layers.Where(l => l.Features == 0).Select(l => l.SourceName).ToList();
            Status($"Salvato {Path.GetFileName(res.Path)}: {res.Layers.Count(l => l.Features > 0)} livelli, {res.Layers.Sum(l => l.Features).ToString("N0", It)} elementi, {(res.Bytes / 1048576.0).ToString("0.0", It)} MB."
                   + (empty.Count > 0 ? $" Non salvati perché vuoti nell'area: {string.Join(", ", empty)}." : ""));
            if (open && _openInMain != null) { _openInMain(res.Path); Close(); }
            return res;
        }
        catch (OperationCanceledException) { Status("Salvataggio annullato."); }
        catch (Exception ex) { Status("Salvataggio non riuscito: " + ex.Message); }
        finally { SaveBtn.IsEnabled = SaveOpenBtn.IsEnabled = true; CancelBtn.IsVisible = false; }
        return null;
    }

    // per le prove automatiche
    public IReadOnlyList<SourceLayer> AllLayers => _all;
    public ClipCanvas MapView => Map;
    public ClipOptions? CurrentOptions => Options();
    public V2 Origin => _origin;
    public Task RecountForTest() => CountInArea();
}
