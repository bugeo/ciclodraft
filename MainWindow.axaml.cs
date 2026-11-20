using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CicloDraft.Controls;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;

namespace CicloDraft;

/// <summary>Riga dell'elenco elementi: aggiornata al suo posto, mai ricreata durante una selezione.</summary>
public class ElementRow : System.ComponentModel.INotifyPropertyChanged
{
    public Element El { get; }
    string _name = "", _area = "";
    IBrush _swatch = Brushes.Transparent;
    public ElementRow(Element el) => El = el;
    public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
    public string Area { get => _area; set => Set(ref _area, value, nameof(Area)); }
    public IBrush Swatch { get => _swatch; set => Set(ref _swatch, value, nameof(Swatch)); }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, string name)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
public record TotalRow(string Label, string Value, IBrush Swatch);

public partial class MainWindow : Window
{
    readonly Editor _ed = new();
    readonly ObservableCollection<ElementRow> _rows = new();
    readonly ObservableCollection<TotalRow> _totals = new();
    bool _updating;
    /// <summary>I livelli sono stati controllati e confermati: si possono tracciare i bordi e aggiungere elementi.</summary>
    bool _layersConfirmed;
    /// <summary>Il riquadro «Aggiungi elemento» è aperto (si apre dal pulsante sulla mappa).</summary>
    bool _addOpen;
    int _lastElemCount;
    public Editor Editor => _ed;
    public void ConfirmLayersForDemo() { _layersConfirmed = true; UpdatePanels(); }
    public MapCanvas MapView => Map;
    public void AttachDataForDemo(GpkgData d) { AttachLayers(d); Map.InvalidateBackground(); }
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public MainWindow()
    {
        InitializeComponent();
        Map.Editor = _ed;
        _ed.StructureChanged += UpdatePanels;
        Map.PointerWorldMoved += w =>
        {
            CoordText.Text = w is { } v && _ed.Data != null
                ? $"E {(v.X + _ed.Data.Origin.X).ToString("0.00", It)}   N {(v.Y + _ed.Data.Origin.Y).ToString("0.00", It)}   ·   1 px = {(1 / Map.Scale).ToString("0.000", It)} m"
                : "";
        };

        TypeCombo.ItemsSource = Enum.GetValues<ElementType>().Select(Element.TypeLabel).ToList();
        TypeCombo.SelectedIndex = -1;
        TypeCombo.SelectionChanged += (_, _) =>
        {
            if (TypeCombo.SelectedIndex < 0) { UpdatePanels(); return; }
            var t = (ElementType)TypeCombo.SelectedIndex;
            // a disegno già avviato si cambia solo l'elemento: la linea e la modalità restano
            if (_ed.Mode != Mode.Idle) { if (!_updating) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ed.SetDraftType(t)); return; }
            // carreggiate, corsie doppie e strisce si disegnano di solito dall'interasse, il resto dal bordo
            if (t is ElementType.Carreggiata or ElementType.CorsiaDoppia or ElementType.Separatore) RefAxisRadio.IsChecked = true;
            else RefBorderRadio.IsChecked = true;
            UpdatePanels();
        };
        SelTypeCombo.ItemsSource = Enum.GetValues<ElementType>().Select(Element.TypeLabel).ToList();
        SelTypeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating || SelTypeCombo.SelectedIndex < 0) return;
            var t = (ElementType)SelTypeCombo.SelectedIndex;
            // applicato dopo che la tendina si è chiusa
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ed.SetSelectedType(t));
        };
        DivCountBox.ValueChanged += (_, _) =>
        {
            if (_updating || _ed.Selected is not { } se || DivCountBox.Value is not { } v) return;
            _ed.SetSelectedDividers((int)v, (DividerStyle)Math.Max(0, DivStyleCombo.SelectedIndex));
        };
        DivStyleCombo.SelectionChanged += (_, _) =>
        {
            if (_updating || _ed.Selected is not { } se) return;
            int cnt = se.Dividers >= 0 ? se.Dividers : se.EffectiveDividers;
            var st = (DividerStyle)Math.Max(0, DivStyleCombo.SelectedIndex);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ed.SetSelectedDividers(cnt, st));
        };
        StallCheck.IsCheckedChanged += (_, _) =>
        {
            if (_updating || _ed.Selected is not { } se) return;
            _ed.SetSelectedStalls(StallCheck.IsChecked == true, se.StallLength);
        };
        StallLenBox.ValueChanged += (_, _) =>
        {
            if (_updating || _ed.Selected is not { } se || StallLenBox.Value is not { } v) return;
            _ed.SetSelectedStalls(se.StallLines, (double)v);
        };
        DivAutoBtn.Click += (_, _) => { if (_ed.Selected is { } se) _ed.SetSelectedDividers(-1, se.DividerStyle); };
        ElementsList.ItemsSource = _rows;
        TotalsList.ItemsSource = _totals;

        OpenGpkgBtn.Click += async (_, _) => await OpenGpkg();
        ClipMenu.Click += (_, _) => OpenClip(Array.Empty<string>());
        OpenGpkgMenu.Click += async (_, _) => await OpenGpkg();
        OpenProjMenu.Click += async (_, _) => await OpenProject();
        ClipLinkBtn.Click += (_, _) => OpenClip(Array.Empty<string>());
        ConfirmLayersBtn.Click += (_, _) =>
        {
            _layersConfirmed = true;
            var p = _ed.Project;
            _ed.Message = p.Edges.All(e => e.Points.Count >= 2)
                ? "Livelli confermati. Premi «Aggiungi elemento» in basso a destra sulla mappa per disegnare un elemento."
                : "Livelli confermati. Premi «Aggiungi elemento» in basso a destra sulla mappa per disegnare un elemento.";
            _ed.NotifyAll();
        };
        EditLayersBtn.Click += (_, _) =>
        {
            if (_ed.Mode != Mode.Idle) _ed.CancelDraft();
            _layersConfirmed = false;
            _ed.Message = "Modifica ruoli e visibilità dei livelli, poi conferma.";
            _ed.NotifyAll();
        };
        NewPhotoBtn.Click += async (_, _) => await NewPhoto();
        Map.PhotoClicked += r => _ = OpenPhotoRef(r);
        Export3DMenu.Click += (_, _) =>
        {
            if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
            new Export3DWindow(_ed.Project, _ed.Data).ShowDialog(this);
        };
        OpenProjBtn.Click += async (_, _) => await OpenProject();
        SaveProjMenu.Click += async (_, _) => await SaveProject();
        ExportDxfMenu.Click += async (_, _) => await ExportDxf();
        ExportCsvMenu.Click += async (_, _) => await ExportCsv();
        ZoomBtn.Click += (_, _) => Map.ZoomTo(_ed.Extent());
        RotPlusBtn.Click += (_, _) => Map.Rotate(5);
        AddVarBtn.Click += (_, _) => { _ed.StartVariator(); Map.Focus(); };
        TabDatiBtn.Click += (_, _) => SetSelTab(0);
        TabVarBtn.Click += (_, _) => SetSelTab(1);
        TabInfoBtn.Click += (_, _) => SetSelTab(2);
        CancelVarBtn.Click += (_, _) => _ed.CancelVariator();
        MapLabelsMenu.Click += (_, _) => Map.ShowLabels = MapLabelsMenu.IsChecked;
        PhotoIconsMenu.Click += (_, _) => Map.ShowPhotoIcons = PhotoIconsMenu.IsChecked;
        RotMinusBtn.Click += (_, _) => Map.Rotate(-5);
        RotResetBtn.Click += (_, _) => Map.Rotate(-Map.Rotation);
        Map.ViewChanged += UpdateRotationLabel;

        RedrawLineBtn.Click += (_, _) => { _ed.RedrawSelectedLine(); Map.Focus(); };
        EditSelLineBtn.Click += (_, _) => { _ed.StartEditLine(); Map.Focus(); };
        EditDraftLineBtn.Click += (_, _) => { _ed.StartEditLine(); Map.Focus(); };
        ExtendStartBtn.Click += (_, _) => { _ed.StartExtend(-1); Map.Focus(); };
        ExtendEndBtn.Click += (_, _) => { _ed.StartExtend(1); Map.Focus(); };
        DeleteKeyBtn.Click += (_, _) => { _ed.DeleteKeyPoint(); Map.Focus(); };
        FinishEditBtn.Click += (_, _) => _ed.FinishEditLine();
        EditSnapCheck.IsCheckedChanged += (_, _) => { _ed.SnapOn = EditSnapCheck.IsChecked == true; if (!_updating) SnapCheck.IsChecked = EditSnapCheck.IsChecked; };
        EditFollowCheck.IsCheckedChanged += (_, _) => { _ed.FollowOn = EditFollowCheck.IsChecked == true; if (!_updating) FollowCheck.IsChecked = EditFollowCheck.IsChecked; };
        MapAddBtn.Click += (_, _) => { TypeCombo.SelectedIndex = -1; _addOpen = true; _lastElemCount = _ed.Project.Elements.Count; _ed.Select(null); UpdatePanels(); };
        CloseAddBtn.Click += (_, _) =>
        {
            if (_ed.Mode == Mode.Trace) _ed.TraceCancel();
            else if (_ed.Mode != Mode.Idle) _ed.CancelDraft();
            _addOpen = false; UpdatePanels();
        };
        KindTrackRadio.IsCheckedChanged += (_, _) => UpdateAddOptions();
        RefBorderRadio.IsCheckedChanged += (_, _) => UpdateAddOptions();
        RefAxisRadio.IsCheckedChanged += (_, _) => UpdateAddOptions();
        // a fine inserimento il riquadro si chiude; il pulsante sulla mappa lo riapre

        ModeFillRadio.IsCheckedChanged += (_, _) => UpdateAddOptions();
        TraceUndoBtn.Click += (_, _) => _ed.TraceUndo();
        MatchHostBtn.Click += (_, _) => { _ed.MatchFirstHostLength(); Map.Focus(); };
        TraceFinishBtn.Click += (_, _) => _ed.TraceFinish();
        TraceCancelBtn.Click += (_, _) => _ed.TraceCancel();
        SnapCheck.IsCheckedChanged += (_, _) => _ed.SnapOn = SnapCheck.IsChecked == true;
        FollowCheck.IsCheckedChanged += (_, _) => _ed.FollowOn = FollowCheck.IsChecked == true;

        NewElemBtn.Click += (_, _) =>
        {
            if (TypeCombo.SelectedIndex < 0) return;
            var t = (ElementType)TypeCombo.SelectedIndex;
            if (KindPolyRadio.IsChecked == true) _ed.StartDrawElement(t, false, polygon: true);
            else if (ModeFillRadio.IsChecked == true) _ed.StartNewElement(t, ElementMode.Fill);
            else _ed.StartDrawElement(t, RefAxisRadio.IsChecked == true);
            Map.Focus();
        };
        RangeFrom.ValueChanged += (_, _) => { if (!_updating) _ed.SetRange((double)(RangeFrom.Value ?? 0), (double)(RangeTo.Value ?? 0)); };
        RangeTo.ValueChanged += (_, _) => { if (!_updating) _ed.SetRange((double)(RangeFrom.Value ?? 0), (double)(RangeTo.Value ?? 0)); };
        WholeEdgeBtn.Click += (_, _) => _ed.WholeEdge();
        ConfirmRangeBtn.Click += (_, _) => _ed.ConfirmRange();
        DraftWidthBox.ValueChanged += (_, _) => { if (!_updating && DraftWidthBox.Value is { } v) _ed.SetDraftWidth((double)v); };
        DraftSmoothBox.ValueChanged += (_, _) => { if (!_updating && DraftSmoothBox.Value is { } v) _ed.SetDraftSmooth((double)v); };
        SelSmoothBox.ValueChanged += (_, _) => { if (!_updating && SelSmoothBox.Value is { } v) _ed.SetSelectedSmooth((double)v); };
        FlipDraftBtn.Click += (_, _) => _ed.FlipDraftSide();
        CommitBtn.Click += (_, _) => _ed.Commit();
        CancelDraftBtn.Click += (_, _) => _ed.CancelDraft();

        ElementsList.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            // La selezione viene applicata dopo che l'elenco ha finito di gestire il clic
            if (ElementsList.SelectedItem is ElementRow r && _ed.Selected != r.El)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_ed.Selected != r.El) _ed.Select(r.El); });
        };
        NameBox.LostFocus += (_, _) => RenameSelected();
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RenameSelected(); Map.Focus(); } };
        SelWidthBox.ValueChanged += (_, _) => { if (!_updating && SelWidthBox.Value is { } v) _ed.SetSelectedWidth((double)v); };
        WMinus50.Click += (_, _) => Nudge(-0.5);
        WMinus10.Click += (_, _) => Nudge(-0.1);
        WPlus10.Click += (_, _) => Nudge(0.1);
        WPlus50.Click += (_, _) => Nudge(0.5);
        FlipSelBtn.Click += (_, _) => _ed.FlipSelected();
        ResetProfileBtn.Click += (_, _) => _ed.ResetProfile();
        SelFrom.ValueChanged += (_, _) => { if (!_updating) _ed.SetSelectedRange((double)(SelFrom.Value ?? 0), (double)(SelTo.Value ?? 0)); };
        SelTo.ValueChanged += (_, _) => { if (!_updating) _ed.SetSelectedRange((double)(SelFrom.Value ?? 0), (double)(SelTo.Value ?? 0)); };
        DeleteBtn.Click += (_, _) => _ed.DeleteSelected();
        AddLeftBtn.Click += (_, _) => { _ed.AddAlongside(true); Map.Focus(); };
        AddRightBtn.Click += (_, _) => { _ed.AddAlongside(false); Map.Focus(); };

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        if (string.IsNullOrEmpty(_ed.Message)) _ed.Message = "Per iniziare apri un progetto salvato o un GeoPackage (al centro, o dal menu File).";
        UpdatePanels();
    }

    /// <summary>Allinea l'elenco agli elementi del progetto toccando solo le righe che cambiano.</summary>
    void SyncRows(Project p)
    {
        bool same = _rows.Count == p.Elements.Count && _rows.Select(r => r.El).SequenceEqual(p.Elements);
        if (!same)
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (!p.Elements.Contains(_rows[i].El)) _rows.RemoveAt(i);
            for (int i = 0; i < p.Elements.Count; i++)
            {
                var e = p.Elements[i];
                int at = -1;
                for (int k = i; k < _rows.Count; k++) if (_rows[k].El == e) { at = k; break; }
                if (at < 0) _rows.Insert(i, new ElementRow(e));
                else if (at != i) _rows.Move(at, i);
            }
        }
        foreach (var r in _rows)
        {
            var e = r.El;
            r.Name = e.Name + (e.Error != null ? "  ⚠" : "");
            r.Area = e.Error == null ? M(e.Metrics.Area, "m²") : "—";
            r.Swatch = MapCanvas.Colors(e.Type).fill;
        }
    }

    void Nudge(double d)
    {
        if (_ed.Selected is { } e) _ed.SetSelectedWidth(Math.Round(e.Width + d, 2));
    }

    void RenameSelected()
    {
        if (_ed.Selected is { } e && !string.IsNullOrWhiteSpace(NameBox.Text) && NameBox.Text != e.Name)
        {
            e.Name = NameBox.Text.Trim(); _ed.NotifyAll();
        }
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        // con il fotoinserimento aperto i tasti vanno a lui (salvo Ctrl+S)
        if (PhotoHost.IsVisible && !(e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.O) { _ = OpenProject(); e.Handled = true; return; }
            if (e.Key == Key.S) { _ = SaveProject(); e.Handled = true; return; }
        }
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (_ed.Mode == Mode.EditLine) { if (_ed.EditExtend != 0) _ed.StartExtend(0); else _ed.FinishEditLine(); }
                else if (_ed.Mode == Mode.Trace) _ed.TraceCancel();
                else if (_ed.Mode != Mode.Idle) _ed.CancelDraft();
                else _ed.Select(null);
                e.Handled = true; break;
            case Key.Enter:
                if (_ed.Mode == Mode.EditLine) _ed.FinishEditLine();
                else if (_ed.Mode == Mode.Trace) _ed.TraceFinish();
                else if (_ed.Mode == Mode.PickRange) _ed.ConfirmRange();
                else if (_ed.Mode is Mode.SetWidth or Mode.PickOther) _ed.Commit();
                e.Handled = true; break;
            case Key.Back:
                if (_ed.Mode == Mode.Trace) { _ed.TraceUndo(); e.Handled = true; }
                break;
            case Key.Delete:
                if (_ed.Mode == Mode.EditLine) { _ed.DeleteKeyPoint(); e.Handled = true; break; }
                if (_ed.Mode == Mode.Idle && _ed.Selected != null) { _ed.DeleteSelected(); e.Handled = true; }
                break;
            case Key.Add or Key.OemPlus:
                if (_ed.Selected != null) { Nudge(0.1); e.Handled = true; }
                break;
            case Key.Subtract or Key.OemMinus:
                if (_ed.Selected != null) { Nudge(-0.1); e.Handled = true; }
                break;
        }
    }

    void UpdateAddOptions()
    {
        bool poly = KindPolyRadio.IsChecked == true;
        TrackOptions.IsVisible = !poly;
        KindNote.Text = poly
            ? "Disegni il contorno chiuso dell'elemento: clic su ogni vertice, doppio clic o Invio per chiuderlo."
            : "L'elemento si sviluppa lungo una linea che disegni sulla mappa.";
        NewElemBtn.Content = poly ? "Disegna il poligono sulla mappa"
            : ModeFillRadio.IsChecked == true ? "Scegli i due elementi sulla mappa" : "Disegna sulla mappa";
    }

    static void SetActive(Border b, bool on) => b.Classes.Set("active", on);

    static string M(double v, string unit = "m") => v.ToString("0.00", It) + " " + unit;

    void UpdatePanels()
    {
        _updating = true;
        try
        {
            var p = _ed.Project;
            StatusText.Text = _ed.Message;

            // Guida: un riquadro alla volta ha la cornice verde
            bool hasData = _ed.Data != null;
            bool draftingNow = _ed.Mode is Mode.PickBase or Mode.PickRange or Mode.SetWidth or Mode.PickOther;
            StartCard.IsVisible = !hasData;
            ViewBtns.IsVisible = hasData;
            UpdateRotationLabel();
            LayersCard.IsVisible = hasData;
            SetActive(LayersCard, hasData && !_layersConfirmed);
            LayersBody.IsVisible = !_layersConfirmed;
            LayersSummary.IsVisible = EditLayersBtn.IsVisible = _layersConfirmed;
            // elemento appena aggiunto: il riquadro si chiude (si riapre con il pulsante sulla mappa)
            if (p.Elements.Count > _lastElemCount && _ed.Mode == Mode.Idle) _addOpen = false;
            _lastElemCount = p.Elements.Count;
            // il riquadro resta aperto mentre si disegna; si apre con il pulsante sulla mappa
            if (draftingNow || _ed.Mode == Mode.Trace && _ed.DrawingElement) _addOpen = true;
            bool editingLine = _ed.Mode == Mode.EditLine;
            AddCard.IsVisible = hasData && _layersConfirmed && _addOpen && !editingLine;
            EditLineCard.IsVisible = editingLine;
            // durante l'aggiunta la tendina mostra sempre l'elemento in corso
            if ((draftingNow || _ed.Mode == Mode.Trace && _ed.DrawingElement) && TypeCombo.SelectedIndex != (int)_ed.DraftType)
                TypeCombo.SelectedIndex = (int)_ed.DraftType;
            AddBody.IsVisible = TypeCombo.SelectedIndex >= 0;
            if (editingLine && _ed.EditEdge is { Anchors: { } ean } eedge)
            {
                EditLineInfo.Text = $"{ean.Count(a => !a.Auto)} punti chiave · {M(PL.Length(eedge.Points))}"
                    + (_ed.EditExtend > 0 ? " · aggiungi punti alla fine" : _ed.EditExtend < 0 ? " · aggiungi punti all'inizio" : "");
                DeleteKeyBtn.IsEnabled = _ed.EditSel >= 0;
                ExtendStartBtn.Classes.Set("accent", _ed.EditExtend < 0);
                ExtendEndBtn.Classes.Set("accent", _ed.EditExtend > 0);
                EditSnapCheck.IsChecked = _ed.SnapOn; EditFollowCheck.IsChecked = _ed.FollowOn;
            }
            EditDraftLineBtn.IsVisible = _ed.Mode == Mode.SetWidth && _ed.EditableLine() != null;
            SetActive(AddCard, AddCard.IsVisible);
            MapAddBtn.IsVisible = hasData && _layersConfirmed && !_addOpen && _ed.Mode == Mode.Idle;
            KindTrackRadio.IsEnabled = KindPolyRadio.IsEnabled = !draftingNow && _ed.Mode != Mode.Trace;

            // Livelli
            if (_ed.Data is { } d)
            {
                FileText.Text = "File: " + Path.GetFileName(d.Path);
                CrsText.Text = "Sistema di riferimento: " + (string.IsNullOrWhiteSpace(d.Crs) ? "non indicato" : d.Crs);
                if (!ReferenceEquals(LayersList.ItemsSource, d.Layers)) LayersList.ItemsSource = d.Layers;
                int Count(LayerRole r) => d.Layers.Count(l => (LayerRole)l.Role == r);
                var parts = new List<string>();
                if (Count(LayerRole.Muri) > 0) parts.Add($"{Count(LayerRole.Muri)} muri");
                if (Count(LayerRole.Marciapiedi) > 0) parts.Add($"{Count(LayerRole.Marciapiedi)} marciapiedi");
                if (Count(LayerRole.Edifici3D) > 0) parts.Add($"{Count(LayerRole.Edifici3D)} edifici 3D");
                if (Count(LayerRole.Sfondo) > 0) parts.Add($"{Count(LayerRole.Sfondo)} sfondo");
                int hidden = d.Layers.Count(l => l.Role != 0 && !l.Visible);
                LayersSummary.Text = $"{d.Layers.Count(l => l.Role != 0)} livelli in uso" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "")
                                     + (hidden > 0 ? $" · {hidden} non visibili sulla mappa" : "") + ".";
            }
            if (hasData && !_layersConfirmed && _ed.Mode == Mode.Idle && !_ed.Message.StartsWith("Modifica ruoli"))
                StatusText.Text = "Controlla i livelli a sinistra (ruolo e visibilità) e premi «Conferma i livelli».";

            // Disegno della linea
            DrawPanel.IsVisible = _ed.Mode == Mode.Trace || (_ed.Mode == Mode.Idle && hasData);
            TracePanel.IsVisible = _ed.Mode == Mode.Trace;
            var fh = _ed.FirstPointHost;
            MatchHostBtn.IsVisible = fh != null;
            if (fh is { } fhv) MatchHostBtn.Content = $"Lungo come «{fhv.e.Name}»";
            TraceNote.IsVisible = _ed.Mode == Mode.Trace;

            // Nuovo elemento
            bool drafting = _ed.Mode is Mode.PickBase or Mode.PickRange or Mode.SetWidth or Mode.PickOther;
            NewElemBtn.IsVisible = !drafting && _ed.Mode != Mode.Trace;
            NewElemBtn.IsEnabled = hasData;
            TypeCombo.IsEnabled = true;   // l'elemento si può cambiare in ogni momento
            ModeFillRadio.IsEnabled = RefBorderRadio.IsEnabled = RefAxisRadio.IsEnabled = !drafting && _ed.Mode != Mode.Trace;
            // il tratto si sceglie solo riempiendo tra due elementi; una linea disegnata si usa per intero
            RangePanel.IsVisible = _ed.DraftMode == ElementMode.Fill && _ed.Mode is Mode.PickRange or Mode.PickOther;
            if (RangePanel.IsVisible && _ed.DraftBase is { } db)
            {
                double L = _ed.DraftBaseLength;
                RangeInfo.Text = $"Agganciato a: {p.EdgeName(db)} (lunghezza {M(L)})";
                RangeFrom.Maximum = RangeTo.Maximum = (decimal)Math.Ceiling(L * 100) / 100;
                RangeFrom.Value = (decimal)Math.Round(Math.Min(_ed.DraftStart, _ed.DraftEnd), 2);
                RangeTo.Value = (decimal)Math.Round(Math.Max(_ed.DraftStart, _ed.DraftEnd), 2);
                ConfirmRangeBtn.IsVisible = WholeEdgeBtn.IsVisible = _ed.Mode == Mode.PickRange;
            }
            WidthPanel.IsVisible = _ed.Mode == Mode.SetWidth && _ed.DraftMode != ElementMode.Polygon;
            FlipDraftBtn.IsVisible = !(_ed.Preview?.Centered ?? false);
            DraftWidthBox.Value = (decimal)_ed.DraftWidth;
            DraftSmoothBox.Value = (decimal)_ed.DraftSmooth;
            OtherInfo.IsVisible = _ed.Mode == Mode.PickOther;
            OtherInfo.Text = _ed.DraftOther is { } o ? "Secondo elemento: " + p.EdgeName(o) : "Clicca sul contorno del secondo elemento nella mappa.";
            CommitPanel.IsVisible = drafting;
            ViewHelp.IsVisible = !drafting && _ed.Mode != Mode.Trace;
            CommitBtn.IsVisible = _ed.Mode is Mode.SetWidth or Mode.PickOther;

            // Elenco elementi
            SyncRows(p);
            NoElemText.IsVisible = p.Elements.Count == 0;
            var wanted = _rows.FirstOrDefault(r => r.El == _ed.Selected);
            if (!ReferenceEquals(ElementsList.SelectedItem, wanted)) ElementsList.SelectedItem = wanted;

            // Selezione
            var s = _ed.Selected;
            SelPanel.IsVisible = s != null && !editingLine;
            // i riquadri di destra galleggiano sulla mappa e compaiono solo quando servono
            RightHintCard.IsVisible = false;
            RightPanel.IsVisible = AddCard.IsVisible || SelPanel.IsVisible || EditLineCard.IsVisible;
            RightHintText.Text = !hasData ? "Apri un progetto o un GeoPackage per iniziare."
                : !_layersConfirmed ? "Conferma i livelli a sinistra: poi qui potrai aggiungere e modificare gli elementi."
                : "Nessun elemento selezionato. Clicca un elemento sulla mappa o nell'elenco a sinistra per modificarlo, oppure premi «Aggiungi elemento».";
            ElementsCard.IsVisible = TotalsCard.IsVisible = PhotosCard.IsVisible = _ed.Data != null && _layersConfirmed;
            UpdatePhotoList();
            if (s != null)
            {
                if (NameBox.Text != s.Name && !NameBox.IsFocused) NameBox.Text = s.Name;
                if (SelTypeCombo.SelectedIndex != (int)s.Type) SelTypeCombo.SelectedIndex = (int)s.Type;
                DividerPanel.IsVisible = s.SupportsDividers;
                StallPanel.IsVisible = s.Type == ElementType.Parcheggio && s.Mode == ElementMode.Offset;
                if (StallPanel.IsVisible)
                {
                    StallCheck.IsChecked = s.StallLines;
                    StallLenBox.Value = (decimal)Math.Round(s.StallLength, 2);
                    StallLenBox.IsEnabled = s.StallLines;
                    int ns = s.HasStalls ? (int)Math.Floor((PL.Length(s.Guide) - 0.5) / s.StallLength) + 1 : 0;
                    StallInfo.Text = s.StallLines ? $"circa {ns} stalli lungo {M(s.Metrics.Length)}" : "Linee degli stalli nascoste (sulla mappa, nel 3D, nel fotoinserimento e nel DXF).";
                }
                if (s.SupportsDividers)
                {
                    int nd = s.EffectiveDividers;
                    DivCountBox.Value = nd;
                    DivStyleCombo.SelectedIndex = (int)s.DividerStyle;
                    DivInfo.Text = (s.Dividers < 0 ? "automatico · " : "") + (nd == 0 ? "nessuna striscia" : $"{nd + 1} parti da {M(s.Metrics.WMean / (nd + 1))}");
                    DivAutoBtn.IsVisible = s.Dividers >= 0;
                }
                var own = s.Base.Kind == EdgeKind.Road && p.FindEdge(s.Base.Id) is { } be && be.Owner == s.Id ? be : null;
                var follows = own?.Anchors?.Where(x => x.El >= 0).Select(x => p.Find(x.El)?.Name).Where(n => n != null).Distinct().ToList();
                AttachText.Text = s.Mode == ElementMode.Polygon
                    ? $"{Element.TypeLabel(s.Type)} · poligono di {s.Polygon.Count} vertici" + (follows is { Count: > 0 } ? " · segue " + string.Join(", ", follows) : "")
                    : own != null
                    ? $"{Element.TypeLabel(s.Type)} · disegnato dal{(s.Centered ? "l'interasse" : " bordo")} ({M(PL.Length(own.Points))})"
                      + (follows is { Count: > 0 } ? " · segue " + string.Join(", ", follows) : "")
                    : $"{Element.TypeLabel(s.Type)} · agganciato a {p.EdgeName(s.Base)}"
                      + (s.Mode == ElementMode.Fill && s.Other is { } so ? $" e {p.EdgeName(so)}" : "");
                LineBtns.IsVisible = own != null;
                AlongPanel.IsVisible = _ed.CanAddAlongside;
                SelRangeLabel.IsVisible = SelRangeGrid.IsVisible = own == null;
                FlipSelBtn.IsVisible = !s.Centered;
                SelError.IsVisible = s.Error != null; SelError.Text = s.Error;
                SelWidthPanel.IsVisible = s.Mode == ElementMode.Offset;
                UpdateVariators(s);
                TabVarText.Text = $"Variatori ({s.Variators.Count})";
                bool canVar = s.Mode == ElementMode.Offset;
                VarNotAvail.IsVisible = !canVar; VarPanel.IsVisible = canVar;
                if (_ed.Mode == Mode.PickVar && _selTab != 1) SetSelTab(1);
                SelWidthBox.Value = (decimal)Math.Round(s.Width, 2);
                SelSmoothBox.Value = (decimal)Math.Round(s.Smooth, 2);
                ProfileText.Text = s.Profile.Count == 0
                    ? (s.Variators.Count == 0 ? "Larghezza costante." : $"Larghezza iniziale; cambia con {(s.Variators.Count == 1 ? "un variatore" : s.Variators.Count + " variatori")} di sezione.")
                    : $"Larghezza variabile: {s.Profile.Count} punti ({string.Join(" · ", s.Profile.Select(w => $"{w.T.ToString("0.0", It)} m → {w.W.ToString("0.00", It)}"))})";
                double L = p.GetEdge(s.Base) is { } bl ? PL.Length(bl) : 0;
                SelFrom.Maximum = SelTo.Maximum = (decimal)Math.Ceiling(L * 100) / 100;
                SelFrom.Value = (decimal)Math.Round(Math.Min(s.Start, L), 2);
                SelTo.Value = (decimal)Math.Round(Math.Min(s.End, L), 2);
                bool isPoly = s.Mode == ElementMode.Polygon;
                MLenLabel.Text = isPoly ? "Perimetro" : "Estensione";
                MLen.Text = M(s.Metrics.Length);
                MArea.Text = M(s.Metrics.Area, "m²");
                MWMin.Text = isPoly ? "—" : M(s.Metrics.WMin);
                MWMax.Text = isPoly ? "—" : M(s.Metrics.WMax);
                MWMean.Text = isPoly ? "—" : M(s.Metrics.WMean);
            }

            _totals.Clear();
            foreach (var g in p.Elements.Where(e => e.Error == null).GroupBy(e => e.Type).OrderBy(g => g.Key))
                _totals.Add(new TotalRow(Element.TypeLabel(g.Key),
                    $"{M(g.Sum(e => e.Metrics.Area), "m²")}  ·  {M(g.Sum(e => e.Metrics.Length))}",
                    MapCanvas.Colors(g.Key).fill));
        }
        finally { _updating = false; }
    }

    // ───────────── file ─────────────

    async Task<string?> PickOpen(string title, string label, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(label) { Patterns = new[] { pattern } } }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<string?> PickSave(string title, string label, string ext, string suggested)
    {
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = ext, ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(label) { Patterns = new[] { "*." + ext } } }
        });
        return f?.TryGetLocalPath();
    }

    void AttachLayers(GpkgData d)
    {
        foreach (var l in d.Layers)
            l.PropertyChanged += (_, _) => Map.InvalidateBackground();
    }

    string BaseName => _ed.Data != null ? Path.GetFileNameWithoutExtension(_ed.Data.Path) : "ciclodraft";

    /// <summary>Oltre questa dimensione il GeoPackage passa prima dal ritaglio.</summary>
    const long LargeFileBytes = 25L * 1024 * 1024;

    void OpenClip(IEnumerable<string> files)
    {
        var w = new ClipWindow(files, path => LoadGpkg(path));
        w.Show(this);
    }

    async Task OpenGpkg()
    {
        var path = await PickOpen("Apri GeoPackage", "GeoPackage", "*.gpkg");
        if (path == null) return;
        if (new FileInfo(path).Length > LargeFileBytes)
        {
            Ok($"{Path.GetFileName(path)} è grande ({new FileInfo(path).Length / 1048576.0:0} MB): scegli l'area e i livelli da tenere, poi «Salva e apri in CicloDraft».");
            OpenClip(new[] { path });
            return;
        }
        LoadGpkg(path);
    }

    public void LoadGpkg(string path)
    {
        ClosePhotoView();
        try
        {
            var d = GpkgReader.Load(path);
            AttachLayers(d);
            LayersList.ItemsSource = null;
            _layersConfirmed = false;
            _addOpen = false;
            _ed.SetData(d);
            _ed.Message = $"GeoPackage aperto: {d.Layers.Count} livelli. Controlla i ruoli e conferma i livelli.";
            _ed.NotifyAll();
            Map.InvalidateBackground();
            Map.ZoomTo(_ed.Extent());
        }
        catch (Exception ex) { Fail("Non riesco ad aprire il GeoPackage: " + ex.Message); }
    }

    async Task OpenProject()
    {
        var path = await PickOpen("Apri progetto", "Progetto CicloDraft", "*.ciclo");
        if (path == null) return;
        await OpenProjectPath(path);
    }

    public async Task OpenProjectPath(string path)
    {
        ClosePhotoView();
        try
        {
            var dto = ProjectFile.Read(path);
            var gp = dto.GpkgPath;
            if (gp == null || !File.Exists(gp))
            {
                var alt = gp != null ? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(gp)) : null;
                if (alt != null && File.Exists(alt)) gp = alt;
                else
                {
                    _ed.Message = "Il GeoPackage del progetto non è stato trovato: indicane la posizione.";
                    StatusText.Text = _ed.Message;
                    gp = await PickOpen("Dov'è il GeoPackage del progetto?", "GeoPackage", "*.gpkg");
                    if (gp == null) return;
                }
            }
            V2? origin = dto.Origin is { Length: 2 } o ? new V2(o[0], o[1]) : null;
            var d = GpkgReader.Load(gp, origin);
            foreach (var l in d.Layers)
            {
                if (dto.LayerRoles.TryGetValue(l.Name, out var r)) l.Role = r;
                l.Visible = !dto.HiddenLayers.Contains(l.Name);
            }
            AttachLayers(d);
            LayersList.ItemsSource = null;
            // un progetto salvato ha già i livelli impostati: il riquadro resta chiuso (si riapre con «Modifica»)
            _layersConfirmed = true;
            _addOpen = false;
            var proj = ProjectFile.ToProject(dto, d.Origin);
            ProjectFile.LoadPhotos(dto, proj, path, d.Origin);
            _ed.SetData(d, proj);
            _lastElemCount = _ed.Project.Elements.Count;
            _ed.Message = $"Progetto aperto: {_ed.Project.Elements.Count} elementi. Seleziona un elemento per modificarlo o premi «Aggiungi elemento».";
            _ed.NotifyAll();
            Map.InvalidateBackground();
            Map.ZoomTo(_ed.Extent());
        }
        catch (Exception ex) { Fail("Non riesco ad aprire il progetto: " + ex.Message); }
    }

    async Task SaveProject()
    {
        if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
        var path = await PickSave("Salva progetto", "Progetto CicloDraft", "ciclo", BaseName + ".ciclo");
        if (path == null) return;
        try { ProjectFile.Save(path, _ed.Project, _ed.Data); Ok("Progetto salvato in " + path); }
        catch (Exception ex) { Fail("Salvataggio non riuscito: " + ex.Message); }
    }

    async Task ExportDxf()
    {
        if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
        var path = await PickSave("Esporta DXF", "Disegno DXF", "dxf", BaseName + "_progetto.dxf");
        if (path == null) return;
        try
        {
            DxfWriter.Write(path, _ed.Project, _ed.Data, DxfContextOnMenu.IsChecked, DxfLabelsOnMenu.IsChecked);
            Ok($"DXF esportato in {path} (unità: metri, {_ed.Project.Elements.Count} elementi).");
        }
        catch (Exception ex) { Fail("Esportazione DXF non riuscita: " + ex.Message); }
    }

    async Task ExportCsv()
    {
        if (_ed.Project.Elements.Count == 0) { Fail("Non ci sono elementi da esportare."); return; }
        var path = await PickSave("Esporta tabella", "CSV (Excel)", "csv", BaseName + "_misure.csv");
        if (path == null) return;
        try { CsvWriter.Write(path, _ed.Project); Ok("Tabella esportata in " + path); }
        catch (Exception ex) { Fail("Esportazione CSV non riuscita: " + ex.Message); }
    }

    void Ok(string m) { _ed.Message = m; _ed.NotifyAll(); }
    void Fail(string m) { _ed.Message = m; _ed.NotifyAll(); }

    /// <summary>Accanto ai pulsanti di rotazione: l'angolo attuale (clic = nord in alto), solo se il disegno è ruotato.</summary>
    void UpdateRotationLabel()
    {
        double r = Map.Rotation;
        RotResetBtn.IsVisible = Math.Abs(r) > 1e-9;
        RotResetBtn.Content = $"{(r > 0 ? "+" : "")}{r.ToString("0.#", It)}°  ·  N ↑";
    }


    string _varSig = "";
    /// <summary>Elenco dei variatori di sezione dell'elemento selezionato: tratto, larghezza prima → dopo, − / + 10 cm, elimina.</summary>
    void UpdateVariators(Element s)
    {
        bool picking = _ed.Mode == Mode.PickVar;
        AddVarBtn.IsVisible = !picking;
        AddVarBtn.IsEnabled = _ed.CanAddVariator;
        VarPickPanel.IsVisible = picking;
        if (picking)
            VarPickText.Text = _ed.VarStart is { } t1
                ? $"Inizio a {t1.ToString("0.00", It)} m. Clic sull'elemento dove finisce il cambio di larghezza."
                : "Clic sull'elemento nel punto in cui inizia il cambio di larghezza.";
        var sig = s.Id + "|" + _ed.VarSel + "|" + string.Join(";", s.Variators.Select((v, i) =>
        {
            var (b, a) = _ed.VariatorWidths(s, i);
            return $"{v.T1:0.00},{v.T2:0.00},{b:0.00},{a:0.00}";
        }));
        if (sig == _varSig) return;
        _varSig = sig;
        VarList.Children.Clear();
        for (int i = 0; i < s.Variators.Count; i++)
        {
            int idx = i;
            var v = s.Variators[i];
            var (wb, wa) = _ed.VariatorWidths(s, i);
            var row = new Border { Classes = { "inset" }, Padding = new Avalonia.Thickness(8, 6) };
            if (i == _ed.VarSel) row.BorderBrush = (IBrush)this.FindResource("AccentBrush")!;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 4 };
            var info = new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Classes = { "v" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Text = $"V{i + 1}   {wb.ToString("0.00", It)} → {wa.ToString("0.00", It)} m" });
            info.Children.Add(new TextBlock { Classes = { "note" },
                Text = $"da {v.T1.ToString("0.0", It)} a {v.T2.ToString("0.0", It)} m · {(v.Delta >= 0 ? "+" : "")}{(v.Delta * 100).ToString("0", It)} cm" });
            g.Children.Add(info);
            Button B(string text, int col, string tip, Action act)
            {
                var b = new Button { Content = text, Padding = new Avalonia.Thickness(10, 4), FontSize = 14, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                ToolTip.SetTip(b, tip);
                Grid.SetColumn(b, col);
                b.Click += (_, _) => { act(); Map.Focus(); };
                g.Children.Add(b);
                return b;
            }
            B("−", 1, "Restringe il tratto in uscita di 10 cm", () => _ed.ChangeVariator(idx, -0.10));
            B("+", 2, "Allarga il tratto in uscita di 10 cm", () => _ed.ChangeVariator(idx, 0.10));
            var del = B("", 3, "Elimina il variatore", () => _ed.RemoveVariator(idx));
            del.Content = new PathIcon { Data = (Avalonia.Media.Geometry)this.FindResource("IconTrash")!, Width = 13, Height = 13 };
            row.Child = g;
            VarList.Children.Add(row);
        }
    }


    // ───────────── Fotoinserimenti ─────────────

    PhotoView? _photoView;
    PhotoInsertRef? _photoRef;
    string _photoSig = "";
    public bool PhotoViewOpen => PhotoHost.IsVisible;
    public PhotoView? CurrentPhotoView => _photoView;

    async Task NewPhoto()
    {
        if (_ed.Data == null) { Fail("Apri prima un GeoPackage o un progetto."); return; }
        var path = await PickOpenImage();
        if (path == null) return;
        var r = EnsurePhotoRef(path);
        await OpenPhotoRef(r);
    }

    async Task<string?> PickOpenImage()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Nuovo fotoinserimento: scegli la foto",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Foto") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.JPG", "*.JPEG", "*.PNG" } } }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public PhotoInsertRef EnsurePhotoRef(string path)
    {
        var full = Path.GetFullPath(path);
        var r = _ed.Project.Photos.FirstOrDefault(x => string.Equals(Path.GetFullPath(x.Path), full, StringComparison.OrdinalIgnoreCase));
        if (r == null)
        {
            r = new PhotoInsertRef { Path = full, Name = Path.GetFileNameWithoutExtension(full) };
            _ed.Project.Photos.Add(r);
            _ed.Message = $"Fotoinserimento «{r.Name}» aggiunto al progetto. Punti, fotocamera e maschera si salvano accanto alla foto; salva il progetto per ritrovarlo nell'elenco.";
        }
        if (_ed.Data != null) ProjectFile.RefreshPhoto(r, _ed.Data.Origin);
        _ed.NotifyAll();
        Map.InvalidateVisual();
        return r;
    }

    /// <summary>Apre il fotoinserimento al posto della mappa centrale (la colonna di sinistra resta).</summary>
    public async Task OpenPhotoRef(PhotoInsertRef r)
    {
        if (_ed.Data == null) return;
        if (_photoRef == r && PhotoHost.IsVisible) { _photoView?.Focus(); return; }
        if (!File.Exists(r.Path)) { Fail($"Non trovo la foto «{r.Path}»."); r.Missing = true; UpdatePhotoList(); return; }
        if (_ed.Mode != Mode.Idle) { if (_ed.Mode == Mode.Trace) _ed.TraceCancel(); else if (_ed.Mode == Mode.EditLine) _ed.FinishEditLine(); else _ed.CancelDraft(); }
        ClosePhotoView();
        var v = new PhotoView(_ed);
        _photoView = v; _photoRef = r;
        // se nel fotoinserimento si apre un'altra foto, diventa un altro fotoinserimento del progetto
        v.PhotoOpened += path =>
        {
            if (_photoRef != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_photoRef.Path), StringComparison.OrdinalIgnoreCase)) return;
            _photoRef = EnsurePhotoRef(path);
        };
        v.Saved += _ => { if (_ed.Data != null && _photoRef != null) ProjectFile.RefreshPhoto(_photoRef, _ed.Data.Origin); _photoSig = ""; UpdatePhotoList(); };
        v.CloseRequested += ClosePhotoView;
        PhotoHost.Child = v;
        PhotoHost.IsVisible = true;
        _photoSig = ""; UpdatePhotoList();
        await v.OpenPhoto(r.Path);
        v.Focus();
    }

    /// <summary>Chiude il fotoinserimento e torna alla mappa (punti, fotocamera e maschera sono già salvati accanto alla foto).</summary>
    public void ClosePhotoView()
    {
        if (_photoView == null) return;
        var r = _photoRef;
        PhotoHost.Child = null; PhotoHost.IsVisible = false;
        _photoView = null; _photoRef = null;
        if (r != null && _ed.Data != null) ProjectFile.RefreshPhoto(r, _ed.Data.Origin);
        _photoSig = ""; UpdatePhotoList();
        Map.InvalidateVisual(); Map.Focus();
    }

    void RemovePhotoRef(PhotoInsertRef r)
    {
        _ed.Project.Photos.Remove(r);
        if (_photoRef == r) ClosePhotoView();
        _ed.Message = $"«{r.Name}» tolto dall'elenco del progetto (la foto e i suoi file restano dove sono).";
        _ed.NotifyAll();
        Map.InvalidateVisual();
    }

    void UpdatePhotoList()
    {
        var ph = _ed.Project.Photos;
        NoPhotoText.IsVisible = ph.Count == 0;
        var sig = (_photoRef?.Name ?? "") + "#" + string.Join("|", ph.Select(r => $"{r.Name},{r.Pairs},{r.Cam.HasValue},{r.Missing}"));
        if (sig == _photoSig && PhotoList.Children.Count == ph.Count) return;
        _photoSig = sig;
        PhotoList.Children.Clear();
        foreach (var r in ph)
        {
            var rr = r;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 2 };
            var open = new Button { Classes = { "transparent" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Padding = new Avalonia.Thickness(6, 4) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            row.Children.Add(new PathIcon
            {
                Data = (Avalonia.Media.Geometry)this.FindResource("IconEye")!, Width = 15, Height = 15, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = r.Cam.HasValue ? (IBrush)this.FindResource("AccentBrush")! : (IBrush)this.FindResource("TextTertiary")!
            });
            var txt = new StackPanel { Spacing = 0 };
            txt.Children.Add(new TextBlock { Text = r.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            txt.Children.Add(new TextBlock
            {
                FontSize = 11, Foreground = (IBrush)this.FindResource("TextSecondary")!,
                Text = r.Missing ? "foto non trovata" : r.Cam.HasValue ? $"fotocamera calcolata · {r.Pairs} punti" : r.Pairs > 0 ? $"{r.Pairs} punti · fotocamera da calcolare" : "da iniziare"
            });
            Grid.SetColumn(txt, 1); row.Children.Add(txt);
            open.Content = row;
            ToolTip.SetTip(open, rr.Path);
            if (rr == _photoRef) { open.Background = (IBrush)this.FindResource("SelectedBackground")!; open.FontWeight = FontWeight.SemiBold; }
            open.Click += (_, _) => _ = OpenPhotoRef(rr);
            g.Children.Add(open);
            var del = new Button { Classes = { "transparent" }, Padding = new Avalonia.Thickness(6, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new PathIcon { Data = (Avalonia.Media.Geometry)this.FindResource("IconTrash")!, Width = 12, Height = 12, Foreground = (IBrush)this.FindResource("TextTertiary")! } };
            ToolTip.SetTip(del, "Togli dall'elenco (la foto e i suoi file restano)");
            del.Click += (_, _) => RemovePhotoRef(rr);
            Grid.SetColumn(del, 1); g.Children.Add(del);
            PhotoList.Children.Add(g);
        }
    }


    int _selTab;
    /// <summary>Schede del riquadro «Elemento selezionato»: 0 = Dati, 1 = Variatori, 2 = Info.</summary>
    public void SetSelTab(int t)
    {
        _selTab = t;
        TabDati.IsVisible = t == 0; TabVar.IsVisible = t == 1; TabInfo.IsVisible = t == 2;
        TabDatiBtn.Classes.Set("on", t == 0); TabVarBtn.Classes.Set("on", t == 1); TabInfoBtn.Classes.Set("on", t == 2);
    }

}
