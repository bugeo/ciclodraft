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
/// <summary>Voce della tendina dei tipi: nome e quadratino del colore.</summary>
public record TypeItem(string Label, IBrush Swatch)
{
    public static TypeItem Of(ElementType t) => new(Element.TypeLabel(t), MapCanvas.Colors(t, 255).fill);
}

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

        TypeCombo.ItemsSource = Enum.GetValues<ElementType>().Where(t => t != ElementType.Indefinito).Select(Element.TypeLabel).ToList();
        TypeCombo.SelectedIndex = -1;
        TypeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating || TypeCombo.SelectedIndex < 0) return;
            var t = (ElementType)TypeCombo.SelectedIndex;
            _typeChosen = true;
            // si cambia solo l'elemento: la linea e la modalità restano
            if (_ed.Mode != Mode.Idle) Avalonia.Threading.Dispatcher.UIThread.Post(() => { _ed.SetDraftType(t); UpdatePanels(); });
            else UpdatePanels();
        };
        SelTypeCombo.ItemsSource = Element.Choosable.Select(TypeItem.Of).ToList();
        foreach (var tb in new[] { GroupSearch, PhotoSearch, SectionSearch, ElemSearch })
            tb.TextChanged += (_, _) => UpdatePanels();
        LTabGroups.Click += (_, _) => SetLeftTab(0);
        LTabPhotos.Click += (_, _) => SetLeftTab(1);
        LTabSections.Click += (_, _) => SetLeftTab(2);
        LTabElements.Click += (_, _) => SetLeftTab(3);
        NewGroupBtn.Click += (_, _) => { _ed.NewGroup(); _groupSig = ""; _focusGroup = true; };
        SaveGroupBtn.Click += (_, _) => { if (_ed.FindGroup(_ed.AssignGroup) is { } sg) _ed.ToggleAssign(sg); Map.Focus(); };
        NewGroupFromSelBtn.Click += (_, _) => { _leftTab = 0; _ed.GroupFromSelection(); _groupSig = ""; _focusGroup = true; };
        AddToGroupBtn.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var g in _ed.Project.Groups)
            {
                var gg = g;
                var mi = new MenuItem { Header = g.Name, Icon = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6), Background = new SolidColorBrush(Color.Parse(g.Color)) } };
                mi.Click += (_, _) => { _ed.AddSelectionToGroup(gg); Map.Focus(); };
                menu.Items.Add(mi);
            }
            menu.ShowAt(AddToGroupBtn);
        };
        SelGroupCombo.SelectionChanged += (_, _) =>
        {
            if (_updating || SelGroupCombo.SelectedIndex < 0) return;
            int i = SelGroupCombo.SelectedIndex;
            int id = i == 0 ? 0 : _ed.Project.Groups[i - 1].Id;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ed.SetSelectedGroup(id));
        };
        SelTypeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating || SelTypeCombo.SelectedIndex < 0) return;
            if (SelTypeCombo.SelectedIndex >= Element.Choosable.Length) return;   // «Indefinito»
            var t = Element.Choosable[SelTypeCombo.SelectedIndex];
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
        MixedBtn.Click += (_, _) => { _ed.SetSelectedSeparated(false); UpdatePanels(); };
        SeparatedBtn.Click += (_, _) => { _ed.SetSelectedSeparated(true); UpdatePanels(); };
        HeightBox.ValueChanged += (_, _) =>
        {
            if (_updating || HeightBox.Value is not { } v) return;
            _ed.SetSelectedHeight((double)v);
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
        UndoMenu.Click += (_, _) => Undo();
        RedoMenu.Click += (_, _) => Redo();
        _ed.StructureChanged += ScheduleHistory;
        NewSectionBtn.Click += (_, _) => { _ed.StartSection(); Map.Focus(); };
        FlipSectionBtn.Click += (_, _) => _ed.FlipSection();
        SaveSectionPngBtn.Click += async (_, _) => await SaveSectionPng();
        DeleteSectionBtn.Click += (_, _) => { if (_ed.SelectedSection is { } s) _ed.DeleteSection(s); };
        CloseSectionBtn.Click += (_, _) => _ed.SelectSection(null);
        SectionNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RenameSection(); Map.Focus(); } };
        SectionNameBox.LostFocus += (_, _) => RenameSection();
        Closing += async (_, e) => await OnClosingAsk(e);
        Map.PhotoClicked += r => _ = OpenPhotoRef(r);
        Export3DMenu.Click += (_, _) =>
        {
            if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
            new Export3DWindow(_ed.Project, _ed.Data).ShowDialog(this);
        };
        OpenProjBtn.Click += async (_, _) => await OpenProject();
        SaveProjMenu.Click += async (_, _) => await SaveProject(direct: true);
        SaveAsProjMenu.Click += async (_, _) => await SaveProject();
        ExportDxfMenu.Click += async (_, _) => await ExportDxf();
        ExportCsvMenu.Click += async (_, _) => await ExportCsv();
        ExportPdfMenu.Click += (_, _) =>
        {
            if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
            new ExportPdfWindow(this, _ed.Project, _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : BaseName).ShowDialog(this);
        };
        ZoomBtn.Click += (_, _) => FitView();
        RotPlusBtn.Click += (_, _) => Map.Rotate(5);
        AddVarBtn.Click += (_, _) => { _ed.StartVariator(); Map.Focus(); };
        TabDatiBtn.Click += (_, _) => SetSelTab(0);
        TabOptBtn.Click += (_, _) => SetSelTab(1);
        TabVarBtn.Click += (_, _) => SetSelTab(2);
        TabInfoBtn.Click += (_, _) => SetSelTab(3);
        CancelVarBtn.Click += (_, _) => _ed.CancelVariator();
        MapLabelsMenu.Click += (_, _) => Map.ShowLabels = MapLabelsMenu.IsChecked;
        PhotoIconsMenu.Click += (_, _) => Map.ShowPhotoIcons = PhotoIconsMenu.IsChecked;
        NotesMenu.Click += (_, _) => { Map.ShowNotes = NotesMenu.IsChecked; if (!NotesMenu.IsChecked) SetNoteTool(MapCanvas.NoteTool.None); };
        SetupNoteTools();
        RotMinusBtn.Click += (_, _) => Map.Rotate(-5);
        RotResetBtn.Click += (_, _) => Map.Rotate(-Map.Rotation);
        Map.ViewChanged += UpdateRotationLabel;

        EditSelLineBtn.Click += (_, _) => { _ed.StartEditLine(); Map.Focus(); };
        EditDraftLineBtn.Click += (_, _) => { _ed.StartEditLine(); Map.Focus(); };
        ExtendStartBtn.Click += (_, _) => { _ed.StartExtend(-1); Map.Focus(); };
        ExtendEndBtn.Click += (_, _) => { _ed.StartExtend(1); Map.Focus(); };
        FinishEditBtn.Click += (_, _) => _ed.FinishEditLine();
        EditSnapCheck.IsCheckedChanged += (_, _) => { _ed.SnapOn = EditSnapCheck.IsChecked == true; if (!_updating) SnapCheck.IsChecked = EditSnapCheck.IsChecked; };
        EditFollowCheck.IsCheckedChanged += (_, _) => { _ed.FollowOn = EditFollowCheck.IsChecked == true; if (!_updating) FollowCheck.IsChecked = EditFollowCheck.IsChecked; };
        MapAddBtn.Click += (_, _) => OpenAddCard();
        ShowGroupsBtn.Click += (_, _) => { Map.ShowGroups = !Map.ShowGroups; UpdatePanels(); Map.Focus(); };
        CreateAreaBtn.Click += (_, _) =>
        {
            // si sceglie solo il tipo: poi ogni gruppo di parti che si toccano diventa un elemento di quel tipo
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var t in Element.Choosable)
            {
                var tt = t;
                var mi = new MenuItem
                {
                    Header = Element.TypeLabel(t),
                    Icon = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(2), Background = MapCanvas.Colors(t).fill }
                };
                mi.Click += (_, _) => { _lastPolyType = tt; _ed.CreateFromPick(tt); SetSelTab(0); Map.Focus(); };
                menu.Items.Add(mi);
            }
            menu.ShowAt(CreateAreaBtn);
        };
        NewElemListBtn.Click += (_, _) => OpenAddCard();
        CloseAddBtn.Click += (_, _) =>
        {
            if (_ed.Mode == Mode.Trace) _ed.TraceCancel();
            else if (_ed.Mode != Mode.Idle) _ed.CancelDraft();
            _addOpen = false; UpdatePanels();
        };
        KindTrackBtn.Click += (_, _) => ChooseKind(1);
        KindPolyBtn.Click += (_, _) => ChooseKind(2);
        SubBorderBtn.Click += (_, _) => ChooseSub(1);
        SubAxisBtn.Click += (_, _) => ChooseSub(2);
        SubFillBtn.Click += (_, _) => ChooseSub(3);
        void StopDraft() { if (_ed.Mode == Mode.Trace) _ed.TraceCancel(); else if (_ed.Mode != Mode.Idle) _ed.CancelDraft(false); }
        // percorso nel titolo: Aggiungi › tipo › Tracciato/Poligono › Bordo…; clic per tornare a quella scelta
        AddCrumbTypeBtn.Click += (_, _) => { StopDraft(); _kind = 0; _sub = 0; ResetTypeStep(); UpdatePanels(); };
        AddCrumbKindBtn.Click += (_, _) => { StopDraft(); _kind = 0; _sub = 0; _drawStarted = false; UpdatePanels(); };
        AddCrumbSubBtn.Click += (_, _) => { StopDraft(); _sub = 0; _drawStarted = false; UpdatePanels(); };
        BuildTypeList();
        TraceUndoBtn.Click += (_, _) => _ed.TraceUndo();
        MatchHostBtn.Click += (_, _) => { _ed.MatchFirstHostLength(); Map.Focus(); };
        TraceFinishBtn.Click += (_, _) => _ed.TraceFinish();
        TraceCancelBtn.Click += (_, _) => _ed.TraceCancel();
        SnapCheck.IsCheckedChanged += (_, _) => _ed.SnapOn = SnapCheck.IsChecked == true;
        FollowCheck.IsCheckedChanged += (_, _) => _ed.FollowOn = FollowCheck.IsChecked == true;

        RangeFrom.ValueChanged += (_, _) => { if (!_updating) _ed.SetRange((double)(RangeFrom.Value ?? 0), (double)(RangeTo.Value ?? 0)); };
        RangeTo.ValueChanged += (_, _) => { if (!_updating) _ed.SetRange((double)(RangeFrom.Value ?? 0), (double)(RangeTo.Value ?? 0)); };
        WholeEdgeBtn.Click += (_, _) => _ed.WholeEdge();
        ConfirmRangeBtn.Click += (_, _) => _ed.ConfirmRange();
        DraftWidthBox.ValueChanged += (_, _) => { if (!_updating && DraftWidthBox.Value is { } v) _ed.SetDraftWidth((double)v); };
        DraftSmoothBox.ValueChanged += (_, _) => { if (!_updating && DraftSmoothBox.Value is { } v) _ed.SetDraftSmooth((double)v); };
        SelSmoothBox.ValueChanged += (_, _) => { if (!_updating && SelSmoothBox.Value is { } v) _ed.SetSelectedSmooth((double)v); };
        FlipDraftBtn.Click += (_, _) => _ed.FlipDraftSide();
        CommitBtn.Click += (_, _) => { if (_typeChosen) _ed.Commit(); };
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
        SelFrom.ValueChanged += (_, _) => { if (!_updating) _ed.SetSelectedRange((double)(SelFrom.Value ?? 0), (double)(SelTo.Value ?? 0)); };
        SelTo.ValueChanged += (_, _) => { if (!_updating) _ed.SetSelectedRange((double)(SelFrom.Value ?? 0), (double)(SelTo.Value ?? 0)); };
        DeleteBtn.Click += (_, _) => _ed.DeleteSelected();
        ToPolygonBtn.Click += async (_, _) => await ConvertToPolygon();
        AddLeftBtn.Click += (_, _) => { _ed.AddAlongside(true); Map.Focus(); };
        AddRightBtn.Click += (_, _) => { _ed.AddAlongside(false); Map.Focus(); };

        _ed.AutoCommit = true;   // finito il disegno si salva subito e si passa a «Elemento selezionato»
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        if (string.IsNullOrEmpty(_ed.Message)) _ed.Message = "Per iniziare apri un progetto salvato o un GeoPackage (al centro, o dal menu File).";
        UpdatePanels();
    }

    /// <summary>Allinea l'elenco agli elementi del progetto toccando solo le righe che cambiano.</summary>
    void SyncRows(Project p)
    {
        var eq = (ElemSearch.Text ?? "").Trim();
        var shown = p.Shown.Where(e => Match(e.Name, eq) || Match(Element.TypeLabel(e.Type), eq)).OrderBy(e => e.Group == 0 ? int.MaxValue : p.Groups.FindIndex(g => g.Id == e.Group)).ToList();
        bool same = _rows.Count == shown.Count && _rows.Select(r => r.El).SequenceEqual(shown);
        if (!same)
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (!shown.Contains(_rows[i].El)) _rows.RemoveAt(i);
            for (int i = 0; i < shown.Count; i++)
            {
                var e = shown[i];
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
            // Ctrl+Z / Ctrl+Y (⌘ sul Mac): annulla e ripeti, tranne mentre si scrive in una casella di testo
            if ((e.Key == Key.Z || e.Key == Key.Y) && FocusManager?.GetFocusedElement() is not TextBox && !PhotoHost.IsVisible)
            {
                if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo(); else Undo();
                e.Handled = true; return;
            }
            if (e.Key == Key.S) { _ = SaveProject(direct: true); e.Handled = true; return; }
        }
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (_ed.AssignGroup != 0 && _ed.FindGroup(_ed.AssignGroup) is { } ag) { _ed.ToggleAssign(ag); e.Handled = true; break; }
                if (Map.Tool != MapCanvas.NoteTool.None && _ed.Mode == Mode.Idle) { SetNoteTool(MapCanvas.NoteTool.None); e.Handled = true; break; }
                if (_ed.Mode == Mode.EditLine) { if (_ed.EditExtend != 0) _ed.StartExtend(0); else _ed.FinishEditLine(); }
                else if (_ed.Mode == Mode.Trace) _ed.TraceCancel();
                else if (_ed.Mode != Mode.Idle) _ed.CancelDraft();
                else if (_ed.SelectedSection != null) _ed.SelectSection(null);
                else _ed.Select(null);
                e.Handled = true; break;
            case Key.Enter:
                if (_ed.Mode == Mode.EditLine) _ed.FinishEditLine();
                else if (_ed.Mode == Mode.Trace) _ed.TraceFinish();
                else if (_ed.Mode == Mode.PickRange) _ed.ConfirmRange();
                else if (_ed.Mode is Mode.SetWidth or Mode.PickOther) { if (_typeChosen) _ed.Commit(); }
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

    /// <summary>Aggiungi elemento: 0 = non ancora scelto, 1 = tracciato, 2 = poligono.</summary>
    int _kind;
    /// <summary>Tracciato: 0 = non ancora scelto, 1 = bordo, 2 = interasse, 3 = riempimento.</summary>
    int _sub;

    /// <summary>Clic su Bordo / Interasse / Riempimento: la scelta vale subito e il disegno parte.</summary>
    /// <summary>Clic su Bordo / Interasse / Riempimento: poi si sceglie il tipo di elemento.</summary>
    void ChooseSub(int s)
    {
        if (_ed.Mode == Mode.Trace) _ed.TraceCancel(); else if (_ed.Mode != Mode.Idle) _ed.CancelDraft(false);
        _sub = s;
        StartDrawing();
    }

    /// <summary>Il disegno è partito (dopo la scelta del tipo): se torna a riposo senza salvare, si torna alla scelta del tipo.</summary>
    bool _drawStarted;
    ElementType _chosenType;

    void ResetTypeStep() { _typeChosen = false; _drawStarted = false; TypeCombo.SelectedIndex = -1; }

    /// <summary>Elenco dei tipi (con il loro colore): un clic sceglie il tipo e il disegno parte.</summary>
    void BuildTypeList()
    {
        TypeList.Children.Clear();
        TypeList.RowDefinitions.Clear();
        var types = Element.Choosable.ToList();
        for (int r = 0; r < (types.Count + 1) / 2; r++) TypeList.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new Avalonia.CornerRadius(3), Background = MapCanvas.Colors(t, 255).fill,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = Element.TypeLabel(t), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var b = new Button { Classes = { "wide" }, Content = row, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Padding = new Avalonia.Thickness(10, 8) };
            b.Click += (_, _) => ChooseType(t);
            Grid.SetRow(b, i / 2); Grid.SetColumn(b, i % 2);
            TypeList.Children.Add(b);
        }
    }

    /// <summary>Primo passo: il tipo di elemento. Poi si sceglie come disegnarlo.</summary>
    void ChooseType(ElementType t)
    {
        _typeChosen = true; _chosenType = t; _kind = 0; _sub = 0; _drawStarted = false;
        _updating = true; TypeCombo.SelectedIndex = (int)t; _updating = false;
        UpdatePanels();
    }

    /// <summary>Scelti tipo e modalità: il disegno parte.</summary>
    void StartDrawing()
    {
        _drawStarted = true;
        var t = _chosenType;
        if (_kind == 2) _ed.StartDrawElement(t, false, polygon: true);
        else if (_sub == 3) _ed.StartNewElement(t, ElementMode.Fill);
        else _ed.StartDrawElement(t, _sub == 2);
        Map.Focus();
        UpdatePanels();
    }
    bool _typeChosen;
    /// <summary>Ultimo tipo usato per un poligono: il prossimo poligono parte da lì.</summary>
    ElementType _lastPolyType = ElementType.Aiuola;

    void ChooseKind(int k)
    {
        // cambiando modalità si lascia il disegno in corso
        if (_ed.Mode == Mode.Trace) _ed.TraceCancel(); else if (_ed.Mode != Mode.Idle) _ed.CancelDraft(false);
        _kind = k; _sub = 0; _drawStarted = false;
        if (k == 2) { StartDrawing(); return; }   // poligono: si disegna subito
        UpdatePanels();
    }

    /// <summary>Due pulsanti affiancati: se uno è nascosto l'altro prende tutta la riga; se lo sono entrambi sparisce la riga.</summary>
    static void PairRow(Grid row, Control a, Control b)
    {
        Grid.SetColumn(a, 0); Grid.SetColumnSpan(a, b.IsVisible ? 1 : 2);
        Grid.SetColumn(b, a.IsVisible ? 1 : 0); Grid.SetColumnSpan(b, a.IsVisible ? 1 : 2);
        row.IsVisible = a.IsVisible || b.IsVisible;
    }

    /// <summary>Pulsanti della stessa larghezza su una riga: si dividono la riga solo quelli visibili.</summary>
    static void EqualRow(Grid row, params Control[] items)
    {
        var vis = items.Where(c => c.IsVisible).ToList();
        row.ColumnDefinitions.Clear();
        foreach (var _ in vis) row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int k = 0; k < vis.Count; k++) Grid.SetColumn(vis[k], k);
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
            if (!hasData) UpdateRecentList();
            UpdateUndoMenu();
            // nessun elemento ancora: «+ Nuovo» verde e cornice verde sul riquadro Elementi
            bool noElems = hasData && _layersConfirmed && p.Elements.Count == 0 && !_addOpen && _ed.Mode == Mode.Idle;
            ElementsCard.Classes.Set("active", noElems);
            NewElemListBtn.Classes.Set("primary", noElems);
            NewElemListBtn.IsEnabled = hasData && _layersConfirmed && _ed.Mode == Mode.Idle;
            ViewBtns.IsVisible = hasData;
            UpdateRotationLabel();
            LayersCard.IsVisible = hasData;
            SetActive(LayersCard, hasData && !_layersConfirmed);
            LayersBody.IsVisible = !_layersConfirmed;
            LayersSummary.IsVisible = EditLayersBtn.IsVisible = _layersConfirmed;
            // elemento appena aggiunto: il riquadro si chiude (si riapre con il pulsante sulla mappa)
            if (p.Elements.Count > _lastElemCount && _ed.Mode == Mode.Idle)
            {
                _addOpen = false;
                if (p.Elements[^1].Mode == ElementMode.Polygon && p.Elements[^1].Type != ElementType.Indefinito) _lastPolyType = p.Elements[^1].Type;
            }
            _lastElemCount = p.Elements.Count;
            // il riquadro resta aperto mentre si disegna; si apre con il pulsante sulla mappa
            if (draftingNow || _ed.Mode == Mode.Trace && _ed.DrawingElement) _addOpen = true;
            bool editingLine = _ed.Mode == Mode.EditLine;
            AddCard.IsVisible = hasData && _layersConfirmed && _addOpen && !editingLine;
            EditLineCard.IsVisible = editingLine;
            // tipo di elemento: si sceglie alla fine; finché non è scelto la tendina resta vuota
            // per il poligono la tendina c'è già mentre si disegna (alla chiusura si salva subito)
            bool polyDrawing = _ed.Mode == Mode.Trace && _ed.DrawingElement && _ed.DraftPolygon;
            bool chooseType = _ed.Mode is Mode.SetWidth or Mode.PickOther;
            if (polyDrawing && !_typeChosen && _ed.DraftType != ElementType.Indefinito && TypeCombo.SelectedIndex != (int)_ed.DraftType) TypeCombo.SelectedIndex = (int)_ed.DraftType;   // siamo già dentro _updating
            TypeRow.IsVisible = false;   // il tipo si è scelto all'inizio
            if (!_typeChosen && !polyDrawing && TypeCombo.SelectedIndex != -1) TypeCombo.SelectedIndex = -1;
            if (_typeChosen && chooseType && TypeCombo.SelectedIndex != (int)_ed.DraftType) TypeCombo.SelectedIndex = (int)_ed.DraftType;
            TypeRow.Classes.Set("missing", chooseType && !_typeChosen && !polyDrawing);
            CommitBtn.IsEnabled = _typeChosen;
            if (editingLine && _ed.EditEdge is { Anchors: { } ean } eedge)
            {
                EditLineInfo.Text = $"{ean.Count(a => !a.Auto)} punti chiave · {M(PL.Length(eedge.Points))}"
                    + (_ed.EditExtend > 0 ? " · aggiungi punti alla fine" : _ed.EditExtend < 0 ? " · aggiungi punti all'inizio" : "");
                ExtendRow.IsVisible = ExtendLabel.IsVisible = !_ed.EditIsPolygon;
                EditLineTitle.Text = _ed.EditIsPolygon ? "Modifica poligono" : "Modifica linea";
                EditLineHelp.Text = _ed.EditIsPolygon
                    ? "Trascina un punto per spostarlo · Canc per toglierlo · doppio clic sul contorno per inserirne uno · doppio clic su un punto per renderlo curvo (trascina le maniglie) o tornare spigolo."
                    : "Trascina un punto per spostarlo · clic su un punto e Canc per toglierlo · doppio clic sulla linea per inserirne uno nuovo · doppio clic su un punto per renderlo curvo (trascina le maniglie) o tornare spigolo. I punti verdi sono agganciati a un altro elemento e lo seguono.";
                ExtendStartBtn.Classes.Set("accent", _ed.EditExtend < 0);
                ExtendEndBtn.Classes.Set("accent", _ed.EditExtend > 0);
                EditSnapCheck.IsChecked = _ed.SnapOn; EditFollowCheck.IsChecked = _ed.FollowOn;
            }
            EditDraftLineBtn.IsVisible = _ed.Mode == Mode.SetWidth && _ed.EditableLine() != null;
            SetActive(AddCard, AddCard.IsVisible);
            // con elementi scelti (Maiusc+clic) in alto ci sono solo «Crea gruppo» e «Aggiungi a gruppo»
            // pulsanti in alto secondo la modalità: normale → «Aggiungi elemento»; modifica di un gruppo → «Salva gruppo»;
            // elementi scelti → «Crea gruppo» / «Aggiungi a gruppo»; aree scelte → «Crea elemento»
            bool editGroup = hasData && _layersConfirmed && _ed.FindGroup(_ed.AssignGroup) != null;
            bool groupSel = hasData && _layersConfirmed && _ed.Mode == Mode.Idle && _ed.GroupSel.Count > 0 && !editGroup;
            SaveGroupBtn.IsVisible = editGroup;
            if (editGroup) SaveGroupText.Text = $"Salva gruppo «{_ed.FindGroup(_ed.AssignGroup)!.Name}»";
            NewGroupFromSelBtn.IsVisible = AddToGroupBtn.IsVisible = groupSel;
            AddToGroupBtn.IsEnabled = p.Groups.Count > 0;
            MapAddBtn.IsVisible = hasData && _layersConfirmed && !_addOpen && _ed.Mode == Mode.Idle && !groupSel && !editGroup;
            ShowGroupsBtn.IsVisible = MapAddBtn.IsVisible;
            ShowGroupsIcon.Data = (Avalonia.Media.Geometry)this.FindResource(Map.ShowGroups ? "IconEye" : "IconEyeOff")!;
            CreateAreaBtn.IsVisible = hasData && _layersConfirmed && _ed.Mode == Mode.Idle && _ed.HasPick && !groupSel && !editGroup;
            if (CreateAreaBtn.IsVisible)
            {
                var why = _ed.PickBlocker();
                CreateAreaBtn.IsEnabled = why == null;
                ToolTip.SetTip(CreateAreaBtn, why);
            }
            // tracciato / poligono: se si sta disegnando un poligono resta acceso «Poligono»
            if (_ed.Mode == Mode.Trace && _ed.DrawingElement) _kind = _ed.DraftPolygon ? 2 : 1;
            else if (draftingNow) _kind = _ed.DraftMode == ElementMode.Polygon ? 2 : 1;
            bool addBusy = _ed.Mode == Mode.Trace && _ed.DrawingElement || draftingNow;
            // annullato il disegno (Esc) si torna alla scelta della modalità; il tipo resta
            if (!addBusy && _ed.Mode == Mode.Idle && _drawStarted) { _drawStarted = false; if (_kind == 2) _kind = 0; else _sub = 0; }
            if (addBusy && _kind == 1 && _sub == 0) _sub = _ed.DraftMode == ElementMode.Fill ? 3 : _ed.DraftCentered ? 2 : 1;
            if (addBusy && !_typeChosen) { _typeChosen = true; _drawStarted = true; _chosenType = _ed.DraftType; }
            // 1) tipo  2) tracciato o poligono  3) per il tracciato: bordo / interasse / riempimento
            TypeList.IsVisible = _addOpen && !_typeChosen && _ed.Mode == Mode.Idle;
            KindTrackBtn.IsChecked = _kind != 2; KindPolyBtn.IsChecked = _kind != 1;
            bool chosen = _kind == 2 || (_kind == 1 && _sub > 0);
            KindRow.IsVisible = _typeChosen && !chosen;
            TrackOptions.IsVisible = _typeChosen && _kind == 1 && _sub == 0;
            AddSep0.IsVisible = AddCrumbTypeBtn.IsVisible = _typeChosen;
            if (_typeChosen) AddCrumbTypeBtn.Content = Element.TypeLabel(addBusy ? _ed.DraftType : _chosenType);
            AddTitle.Text = _typeChosen ? "Aggiungi" : "Aggiungi elemento";
            AddSep1.IsVisible = AddCrumbKindBtn.IsVisible = _kind != 0;
            AddCrumbKindBtn.Content = _kind == 2 ? "Poligono" : "Tracciato";
            AddSep2.IsVisible = AddCrumbSubBtn.IsVisible = _kind == 1 && _sub > 0;
            AddCrumbSubBtn.Content = _sub == 3 ? "Riempimento" : _sub == 2 ? "Interasse" : "Bordo";

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
            DrawPanel.IsVisible = _ed.Mode == Mode.Trace;
            TracePanel.IsVisible = _ed.Mode == Mode.Trace;
            var fh = _ed.FirstPointHost;
            MatchHostBtn.IsVisible = fh != null;
            if (fh is { } fhv) MatchHostBtn.Content = $"Lungo come «{fhv.e.Name}»";
            TraceNote.IsVisible = _ed.Mode == Mode.Trace;

            // Nuovo elemento
            bool drafting = _ed.Mode is Mode.PickBase or Mode.PickRange or Mode.SetWidth or Mode.PickOther;
            TypeCombo.IsEnabled = true;   // l'elemento si può cambiare in ogni momento
            SubBorderBtn.IsEnabled = SubAxisBtn.IsEnabled = SubFillBtn.IsEnabled = hasData;
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
            FlipDraftBtn.IsVisible = WidthPanel.IsVisible && !(_ed.Preview?.Centered ?? false);
            EqualRow(DraftBtnRow, FlipDraftBtn, EditDraftLineBtn, CancelDraftBtn);
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
            NoteTools.IsVisible = hasData;
            PenBtn.IsVisible = TextBtn.IsVisible = hasData && _layersConfirmed;
            // i pulsanti in basso a destra restano fermi: il riquadro di destra si ferma sopra di loro
            NoteTools.Margin = new Avalonia.Thickness(0, 0, 14, 14);   // il riquadro della sezione si apre sopra di loro
            RightHintText.Text = !hasData ? "Apri un progetto o un GeoPackage per iniziare."
                : !_layersConfirmed ? "Conferma i livelli a sinistra: poi qui potrai aggiungere e modificare gli elementi."
                : "Nessun elemento selezionato. Clicca un elemento sulla mappa o nell'elenco a sinistra per modificarlo, oppure premi «Aggiungi elemento».";
            bool leftOn = _ed.Data != null && _layersConfirmed;
            // una sezione aperta (dalla mappa o appena disegnata) mostra la scheda delle sezioni
            if (_ed.SelectedSection?.Id != _lastSecTab) { _lastSecTab = _ed.SelectedSection?.Id; if (_lastSecTab != null) _leftTab = 2; }
            TotalsCard.IsVisible = LeftTabs.IsVisible = leftOn;
            // un riquadro alla volta, scelto con le schede (con il numero di voci)
            GroupsCard.IsVisible = leftOn && _leftTab == 0;
            PhotosCard.IsVisible = leftOn && _leftTab == 1;
            SectionsCard.IsVisible = leftOn && _leftTab == 2;
            ElementsCard.IsVisible = leftOn && _leftTab == 3;
            LCntGroups.Text = $"({p.Groups.Count})";
            LCntPhotos.Text = $"({p.Photos.Count})";
            LCntSections.Text = $"({p.Sections.Count})";
            LCntElements.Text = $"({p.Elements.Count})";
            LTabGroups.Classes.Set("on", _leftTab == 0); LTabPhotos.Classes.Set("on", _leftTab == 1);
            LTabSections.Classes.Set("on", _leftTab == 2); LTabElements.Classes.Set("on", _leftTab == 3);
            UpdateSections();
            UpdateGroups();
            UpdatePhotoList();
            if (s != null)
            {
                if (NameBox.Text != s.Name && !NameBox.IsFocused) NameBox.Text = s.Name;
                // «Indefinito» compare in fondo solo per gli elementi che lo sono ancora
                bool undef = s.Type == ElementType.Indefinito;
                if (SelTypeCombo.ItemCount != Element.Choosable.Length + (undef ? 1 : 0))
                    SelTypeCombo.ItemsSource = Element.Choosable.Select(TypeItem.Of).Concat(undef ? new[] { TypeItem.Of(ElementType.Indefinito) } : Array.Empty<TypeItem>()).ToList();
                int ti = undef ? Element.Choosable.Length : Array.IndexOf(Element.Choosable, s.Type);
                if (SelTypeCombo.SelectedIndex != ti) SelTypeCombo.SelectedIndex = ti;
                // titolo: come è disegnato l'elemento
                SelTitle.Text = s.Mode switch
                {
                    ElementMode.Polygon => "Poligono",
                    ElementMode.Fill => "Tracciato  ›  Riempimento",
                    _ => s.Centered ? "Tracciato  ›  Interasse" : "Tracciato  ›  Bordo"
                };
                SelTypeRow.Classes.Set("missing", s.Type == ElementType.Indefinito);
                SelGroupRow.IsVisible = p.Groups.Count > 0;
                var gnames = new List<string> { "Nessuno" }; gnames.AddRange(p.Groups.Select(g => g.Name));
                if (SelGroupCombo.ItemsSource is not List<string> cur || !cur.SequenceEqual(gnames)) SelGroupCombo.ItemsSource = gnames;
                int gi = s.Group == 0 ? 0 : p.Groups.FindIndex(g => g.Id == s.Group) + 1;
                if (SelGroupCombo.SelectedIndex != gi) SelGroupCombo.SelectedIndex = gi;
                DividerPanel.IsVisible = s.SupportsDividers;
                StallPanel.IsVisible = s.Type == ElementType.Parcheggio && s.Mode == ElementMode.Offset;
                if (StallPanel.IsVisible)
                {
                    StallCheck.IsChecked = s.StallLines;
                    StallLenBox.Value = (decimal)Math.Round(s.StallLength, 2);
                    StallLenBox.IsEnabled = s.StallLines;
                    int ns = s.HasStalls ? (int)Math.Floor((PL.Length(s.Guide) - 0.5) / s.StallLength) + 1 : 0;
                    StallInfo.Text = s.StallLines ? $"circa {ns} stalli lungo {M(s.Metrics.Length)}" : "";
                    StallInfo.IsVisible = s.StallLines;
                }
                SepPanel.IsVisible = s.Type == ElementType.Ciclopedonale && s.Mode != ElementMode.Polygon;
                MixedBtn.IsChecked = !s.Separated; SeparatedBtn.IsChecked = s.Separated;
                HeightPanel.IsVisible = s.HasHeight;
                if (s.HasHeight) HeightBox.Value = (decimal)Math.Round(s.Height, 2);
                OptNone.IsVisible = !DividerPanel.IsVisible && !StallPanel.IsVisible && !HeightPanel.IsVisible && !SepPanel.IsVisible;
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
                EditSelLineBtn.IsVisible = own != null;
                ToPolygonBtn.IsVisible = s.Mode != ElementMode.Polygon;
                AlongPanel.IsVisible = _ed.CanAddAlongside;
                SelRangeLabel.IsVisible = SelRangeGrid.IsVisible = own == null;
                FlipSelBtn.IsVisible = !s.Centered && s.Mode == ElementMode.Offset;
                PairRow(LineBtns, FlipSelBtn, EditSelLineBtn);
                PairRow(EndBtns, ToPolygonBtn, DeleteBtn);
                SelError.IsVisible = s.Error != null; SelError.Text = s.Error;
                SelWidthPanel.IsVisible = s.Mode == ElementMode.Offset;
                UpdateVariators(s);
                TabVarText.Text = $"Variatori ({s.Variators.Count})";
                bool canVar = s.Mode == ElementMode.Offset;
                VarNotAvail.IsVisible = !canVar; VarPanel.IsVisible = canVar;
                if (_ed.Mode == Mode.PickVar && _selTab != 2) SetSelTab(2);
                SelWidthBox.Value = (decimal)Math.Round(s.Width, 2);
                SelSmoothBox.Value = (decimal)Math.Round(s.Smooth, 2);
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
            foreach (var g in p.Shown.Where(e => e.Error == null).GroupBy(e => e.Type).OrderBy(g => g.Key))
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
            _projectPath = null; MarkSaved(); ResetHistory();
            RecentFiles.Add(path, "geopackage");
            _ed.Message = $"GeoPackage aperto: {d.Layers.Count} livelli. Controlla i ruoli e conferma i livelli.";
            _ed.NotifyAll();
            Map.InvalidateBackground();
            Map.ZoomTo(_ed.Extent());
            Avalonia.Threading.Dispatcher.UIThread.Post(FitView, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { Fail("Non riesco ad aprire il GeoPackage: " + ex.Message); }
    }

    async Task OpenProject()
    {
        var path = await PickOpen("Apri progetto", "Progetto CicloDraft", "*.ciclo");
        if (path == null) return;
        await OpenProjectPath(path);
    }

    public async Task OpenProjectPath(string path) => await OpenProjectPath(path, false);

    async Task OpenProjectPath(string path, bool keepView)
    {
        var view = Map.View;
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
            _projectPath = path; MarkSaved(); ResetHistory();
            RecentFiles.Add(path, "progetto");
            _lastElemCount = _ed.Project.Elements.Count;
            _ed.Message = $"Progetto aperto: {_ed.Project.Elements.Count} elementi. Seleziona un elemento per modificarlo o premi «Aggiungi elemento».";
            _ed.NotifyAll();
            Map.InvalidateBackground();
            WatchProject(path);
            if (keepView) { Map.View = view; return; }
            Map.ZoomTo(_ed.Extent());
            Avalonia.Threading.Dispatcher.UIThread.Post(FitView, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { Fail("Non riesco ad aprire il progetto: " + ex.Message); }
    }

    /// <summary>Salva il progetto; con direct = true e un progetto già salvato scrive nello stesso file senza chiedere.</summary>
    async Task<bool> SaveProject(bool direct = false)
    {
        if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return false; }
        var path = direct && _projectPath != null ? _projectPath
            : await PickSave("Salva progetto", "Progetto CicloDraft", "ciclo", _projectPath != null ? Path.GetFileName(_projectPath) : BaseName + ".ciclo");
        if (path == null) return false;
        try
        {
            ProjectFile.Save(path, _ed.Project, _ed.Data);
            _projectPath = path; _savedSnap = Snapshot();
            WatchProject(path);
            Ok("Progetto salvato in " + path);
            return true;
        }
        catch (Exception ex) { Fail("Salvataggio non riuscito: " + ex.Message); return false; }
    }

    // ───────────── progetto modificato da fuori (es. dal server MCP) ─────────────

    FileSystemWatcher? _watch;
    string? _diskText;              // contenuto del file come l'abbiamo letto o scritto noi
    int _watchVer;
    bool _askingReload;

    void WatchProject(string path)
    {
        try { _diskText = File.ReadAllText(path); } catch { _diskText = null; }
        _watch?.Dispose(); _watch = null;
        try
        {
            var full = Path.GetFullPath(path);
            _watch = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
            // si aspetta che il file smetta di cambiare (scrittura finita) prima di rileggerlo
            void Kick()
            {
                int v = Interlocked.Increment(ref _watchVer);
                Task.Delay(700).ContinueWith(_ =>
                {
                    if (v == Volatile.Read(ref _watchVer))
                        Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ProjectChangedOnDisk());
                });
            }
            _watch.Changed += (_, _) => Kick();
            _watch.Created += (_, _) => Kick();
            _watch.Renamed += (_, _) => Kick();
        }
        catch { _watch = null; }
    }

    async Task ProjectChangedOnDisk()
    {
        if (_projectPath is not { } path || _askingReload || !File.Exists(path)) return;
        string text;
        try { text = File.ReadAllText(path); } catch { return; }
        if (text == _diskText) return;
        _diskText = text;
        if (HasUnsavedChanges)
        {
            _askingReload = true;
            try
            {
                if (!await AskConfirm("Progetto modificato", $"Il file «{Path.GetFileName(path)}» è stato modificato da un altro programma (per esempio dall'assistente tramite il server MCP). Ricaricarlo? Le modifiche non salvate qui andrebbero perse.", "Ricarica"))
                    return;
            }
            finally { _askingReload = false; }
        }
        await OpenProjectPath(path, keepView: true);
        _ed.Message = "Progetto ricaricato: è stato modificato da un altro programma.";
        _ed.NotifyAll();
    }

    // ───────────── modifiche non salvate ─────────────

    string? _projectPath;   // file .ciclo aperto o salvato per ultimo
    string? _savedSnap;     // il progetto com'era all'ultimo salvataggio o apertura
    bool _closeConfirmed;

    string Snapshot() => _ed.Data == null ? "" : ProjectFile.ToJson(_projectPath ?? Path.Combine(Path.GetTempPath(), "progetto.ciclo"), _ed.Project, _ed.Data);

    /// <summary>Ci sono modifiche da salvare (un GeoPackage appena aperto e ancora vuoto non conta).</summary>
    public bool HasUnsavedChanges =>
        _ed.Data != null && (_ed.Project.Elements.Count > 0 || _ed.Project.Photos.Count > 0 || _projectPath != null) && Snapshot() != _savedSnap;

    void MarkSaved() => _savedSnap = Snapshot();

    async Task OnClosingAsk(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !HasUnsavedChanges) return;
        e.Cancel = true;
        var r = await AskUnsaved();
        if (r == 0) return;                                  // Annulla: si resta nel programma
        if (r == 2 && !await SaveProject(direct: true)) return;  // Salva (se il salvataggio non riesce o viene annullato, si resta)
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Finestra «Modifiche non salvate»: 2 = Salva, 1 = Non salvare, 0 = Annulla.</summary>
    async Task<int> AskUnsaved()
    {
        int result = 0;
        var dlg = new Window
        {
            Title = "Modifiche non salvate", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var save = new Button { Content = "Salva", Classes = { "primary" }, MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsDefault = true };
        var dont = new Button { Content = "Non salvare", MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var cancel = new Button { Content = "Annulla", MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsCancel = true };
        save.Click += (_, _) => { result = 2; dlg.Close(); };
        dont.Click += (_, _) => { result = 1; dlg.Close(); };
        cancel.Click += (_, _) => { result = 0; dlg.Close(); };
        var name = _projectPath != null ? $"«{Path.GetFileName(_projectPath)}»" : "Il progetto";
        dlg.Content = new Border
        {
            Padding = new Avalonia.Thickness(24, 20), MaxWidth = 460,
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Ci sono modifiche non salvate", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{name} è stato modificato dopo l'ultimo salvataggio. Vuoi salvare le modifiche prima di chiudere?", TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)this.FindResource("TextSecondary")! },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 6, 0, 0), Children = { cancel, dont, save } }
                }
            }
        };
        await dlg.ShowDialog(this);
        return result;
    }

    async Task ExportDxf()
    {
        if (_ed.Data == null) { Fail("Apri prima un GeoPackage."); return; }
        var path = await PickSave("Esporta DXF", "Disegno DXF", "dxf", BaseName + "_progetto.dxf");
        if (path == null) return;
        try
        {
            DxfWriter.Write(path, _ed.Project, _ed.Data, DxfContextOnMenu.IsChecked, DxfLabelsOnMenu.IsChecked);
            Ok($"DXF esportato in {path} (unità: metri, {_ed.Project.Shown.Count()} elementi).");
        }
        catch (Exception ex) { Fail("Esportazione DXF non riuscita: " + ex.Message); }
    }

    async Task ExportCsv()
    {
        if (!_ed.Project.Shown.Any()) { Fail("Non ci sono elementi visibili da esportare."); return; }
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
        _leftTab = 1;
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

    /// <summary>«Adatta vista»: tutta la piantina visibile (livelli mostrati, elementi visibili, sezioni) al massimo
    /// ingrandimento, nell'area della mappa non coperta da pulsanti e riquadri.</summary>
    void FitView()
    {
        IEnumerable<V2> Pts()
        {
            if (_ed.Data is { } d)
                foreach (var l in d.Layers)
                    if (l.Visible && l.Role != 0)
                        foreach (var r in l.Rings) { yield return new V2(r.Box.MinX, r.Box.MinY); yield return new V2(r.Box.MaxX, r.Box.MaxY);
                                                     if (Math.Abs(_ed.Project.ViewRotation) > 1e-9) foreach (var q in r.Pts) yield return q; }
            foreach (var e in _ed.Project.Shown) foreach (var q in e.Polygon) yield return q;
            foreach (var ed in _ed.Project.Edges) if (_ed.Project.IsShown(ed)) foreach (var q in ed.Points) yield return q;
            foreach (var sc in _ed.Project.Sections) { yield return sc.A; yield return sc.B; }
        }
        double right = RightPanel.IsVisible ? RightPanel.Bounds.Width + 24 : 16;
        if (!Map.FitPoints(Pts(), new Avalonia.Thickness(16, 64, right, 64))) Map.ZoomTo(_ed.Extent());
    }

    // ───────────── note: penna e testo ─────────────

    static readonly string[] NoteColorList = { "#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5", "#8E24AA", "#212121", "#FFFFFF" };
    static readonly double[] PenSizes = { 2, 4, 8 };
    static readonly double[] TextSizes = { 12, 16, 24 };
    Annotation? _editingNote;
    V2 _noteAt;

    void SetupNoteTools()
    {
        PenBtn.Click += (_, _) => SetNoteTool(PenBtn.IsChecked == true ? MapCanvas.NoteTool.Pen : MapCanvas.NoteTool.None);
        TextBtn.Click += (_, _) => SetNoteTool(TextBtn.IsChecked == true ? MapCanvas.NoteTool.Text : MapCanvas.NoteTool.None);
        EraserBtn.Click += (_, _) => SetNoteTool(EraserBtn.IsChecked == true ? MapCanvas.NoteTool.Eraser : _lastNoteTool);
        Map.TextRequested += (w, sp, existing) =>
        {
            CommitNoteText();
            _editingNote = existing; _noteAt = w;
            if (existing != null) { Map.NoteColor = existing.Color; Map.TextSize = existing.Size; BuildNoteBar(); }
            NoteTextBox.Text = existing?.Text ?? "";
            NoteTextBox.FontSize = Map.TextSize;
            NoteTextBox.Foreground = new SolidColorBrush(Color.Parse(Map.NoteColor == "#FFFFFF" ? "#212121" : Map.NoteColor));
            Canvas.SetLeft(NoteTextBox, sp.X - 5); Canvas.SetTop(NoteTextBox, sp.Y - 3);
            NoteTextBox.IsVisible = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { NoteTextBox.Focus(); NoteTextBox.SelectAll(); });
        };
        NoteTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { CommitNoteText(); Map.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _editingNote = null; NoteTextBox.IsVisible = false; Map.Focus(); e.Handled = true; }
        };
        NoteTextBox.LostFocus += (_, _) => CommitNoteText();
        BuildNoteBar();
    }

    MapCanvas.NoteTool _lastNoteTool = MapCanvas.NoteTool.Pen;

    void SetNoteTool(MapCanvas.NoteTool t)
    {
        CommitNoteText();
        if (t is MapCanvas.NoteTool.Pen or MapCanvas.NoteTool.Text) _lastNoteTool = t;
        if (t != MapCanvas.NoteTool.None && _ed.Mode != Mode.Idle) { if (_ed.Mode == Mode.Trace) _ed.TraceCancel(); else _ed.CancelDraft(); }
        if (t != MapCanvas.NoteTool.None) { _ed.Select(null); }
        Map.Tool = t;
        PenBtn.IsChecked = t == MapCanvas.NoteTool.Pen || (t == MapCanvas.NoteTool.Eraser && _lastNoteTool == MapCanvas.NoteTool.Pen);
        TextBtn.IsChecked = t == MapCanvas.NoteTool.Text || (t == MapCanvas.NoteTool.Eraser && _lastNoteTool == MapCanvas.NoteTool.Text);
        EraserBtn.IsChecked = t == MapCanvas.NoteTool.Eraser;
        NoteBar.IsVisible = t != MapCanvas.NoteTool.None;
        BuildNoteBar();
        Map.Focus();
    }

    /// <summary>Menu sopra penna e testo: colori e dimensioni (del tratto o del testo).</summary>
    void BuildNoteBar()
    {
        bool text = _lastNoteTool == MapCanvas.NoteTool.Text;
        NoteColors.Children.Clear();
        foreach (var c in NoteColorList)
        {
            var cc = c; bool on = string.Equals(Map.NoteColor, c, StringComparison.OrdinalIgnoreCase);
            var b = new Button { Width = 20, Height = 20, Padding = new Avalonia.Thickness(0), CornerRadius = new Avalonia.CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse(c)), BorderThickness = new Avalonia.Thickness(on ? 2.5 : 1),
                BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.Parse("#66FFFFFF")) };
            b.Click += (_, _) => { Map.NoteColor = cc; if (_editingNote != null || NoteTextBox.IsVisible) NoteTextBox.Foreground = new SolidColorBrush(Color.Parse(cc == "#FFFFFF" ? "#212121" : cc)); BuildNoteBar(); };
            NoteColors.Children.Add(b);
        }
        NoteSizes.Children.Clear();
        var sizes = text ? TextSizes : PenSizes;
        foreach (var sz in sizes)
        {
            var s = sz; bool on = Math.Abs((text ? Map.TextSize : Map.PenSize) - sz) < 0.01;
            object content = text
                ? new TextBlock { Text = "A", FontSize = sz * 0.75 + 4, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                : new Avalonia.Controls.Shapes.Ellipse { Width = sz * 1.3 + 3, Height = sz * 1.3 + 3, Fill = Brushes.White };
            var b = new Button { Width = 28, Height = 28, Padding = new Avalonia.Thickness(0), Content = content,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = on ? new SolidColorBrush(Color.Parse("#FF3A414C")) : Brushes.Transparent, BorderThickness = new Avalonia.Thickness(on ? 1.5 : 0), BorderBrush = (IBrush)this.FindResource("AccentBrush")! };
            ToolTip.SetTip(b, text ? $"Testo {sz:0} px" : $"Tratto {sz:0} px");
            b.Click += (_, _) =>
            {
                if (text) { Map.TextSize = s; if (NoteTextBox.IsVisible) NoteTextBox.FontSize = s; } else Map.PenSize = s;
                BuildNoteBar();
            };
            NoteSizes.Children.Add(b);
        }
    }

    void CommitNoteText()
    {
        if (!NoteTextBox.IsVisible) return;
        NoteTextBox.IsVisible = false;
        var txt = (NoteTextBox.Text ?? "").TrimEnd();
        var n = _editingNote; _editingNote = null;
        if (n != null)
        {
            if (txt.Length == 0) _ed.Project.Annotations.Remove(n);
            else { n.Text = txt; n.Color = Map.NoteColor; n.Size = Map.TextSize; }
        }
        else if (txt.Length > 0)
            _ed.Project.Annotations.Add(new Annotation { IsText = true, Pts = new() { _noteAt }, Text = txt, Color = Map.NoteColor, Size = Map.TextSize });
        else return;
        _ed.NotifyAll();
    }

    /// <summary>Scheda della colonna di sinistra: 0 gruppi, 1 fotoinserimenti, 2 sezioni, 3 elementi.</summary>
    /// <summary>Ricerca nelle schede di sinistra: il testo è contenuto nel nome (senza badare a maiuscole e accenti).</summary>
    static bool Match(string? name, string q) =>
        q.Length == 0 || System.Globalization.CultureInfo.GetCultureInfo("it-IT").CompareInfo.IndexOf(name ?? "", q,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    int _leftTab = 3;
    int? _lastSecTab;
    void SetLeftTab(int t) { _leftTab = t; UpdatePanels(); }

    string _groupSig = "";
    bool _focusGroup;

    /// <summary>Elenco dei blocchi: occhio (mostra/nascondi), nome modificabile, numero di elementi, nuova versione, elimina.
    /// Clic sulla riga = blocco corrente (i nuovi elementi ci entrano).</summary>
    void UpdateGroups()
    {
        var p = _ed.Project;
        if (_ed.FindGroup(_ed.CurrentGroup) == null) _ed.CurrentGroup = 0;
        var gq = (GroupSearch.Text ?? "").Trim();
        var sig = gq + "§" + _ed.CurrentGroup + "/" + _ed.AssignGroup + "#" + string.Join("|", p.Groups.Select(g => $"{g.Id},{g.Name},{g.Visible},{g.Color},{p.Elements.Count(e => e.Group == g.Id)}"));
        if (sig == _groupSig) return;
        if (GroupList.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().SelectMany(x => x.Children).OfType<TextBox>().Any(t => t.IsFocused)) return;
        _groupSig = sig;
        GroupList.Children.Clear();
        IBrush Res(string k) => (IBrush)this.FindResource(k)!;
        Avalonia.Media.Geometry Ico(string k) => (Avalonia.Media.Geometry)this.FindResource(k)!;
        Button IconBtn(string icon, string tip, IBrush fg, Action click)
        {
            var b = new Button { Classes = { "transparent" }, Padding = new Avalonia.Thickness(5, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new PathIcon { Data = Ico(icon), Width = 14, Height = 14, Foreground = fg } };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        TextBox? last = null;
        foreach (var g in p.Groups.Where(x => Match(x.Name, gq)))
        {
            var gg = g;
            bool cur = g.Id == _ed.CurrentGroup;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 2 };
            var border = new Border { CornerRadius = new Avalonia.CornerRadius(6), Padding = new Avalonia.Thickness(2), Child = row,
                Background = cur ? Res("SelectedBackground") : Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            border.PointerPressed += (_, e) =>
            {
                if (e.Source is TextBox || (e.Source is Avalonia.Visual sv && (sv is Button || Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(sv) != null))) return;
                _ed.CurrentGroup = _ed.CurrentGroup == gg.Id ? 0 : gg.Id; _groupSig = ""; UpdateGroups();
            };
            row.Children.Add(IconBtn(g.Visible ? "IconEye" : "IconEyeOff", g.Visible ? "Nascondi il gruppo" : "Mostra il gruppo",
                g.Visible ? Res("AccentBrush") : Res("TextTertiary"), () => _ed.SetGroupVisible(gg, !gg.Visible)));
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6), Margin = new Avalonia.Thickness(2, 0),
                Background = new SolidColorBrush(Color.Parse(g.Color)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            ToolTip.SetTip(dot, "Colore del gruppo: il bordo dei suoi elementi sulla mappa");
            Grid.SetColumn(dot, 1); row.Children.Add(dot);
            var name = new TextBox { Text = g.Name, FontSize = 12, BorderThickness = new Avalonia.Thickness(0), Background = Brushes.Transparent,
                Padding = new Avalonia.Thickness(4, 3), MinHeight = 0, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = g.Visible ? Res("TextPrimary") : Res("TextTertiary"), FontWeight = cur ? FontWeight.SemiBold : FontWeight.Normal };
            void Commit() { if (!string.IsNullOrWhiteSpace(name.Text)) _ed.RenameGroup(gg, name.Text!); else name.Text = gg.Name; }
            name.LostFocus += (_, _) => Commit();
            name.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); Map.Focus(); e.Handled = true; } else if (e.Key == Avalonia.Input.Key.Escape) { name.Text = gg.Name; Map.Focus(); e.Handled = true; } };
            Grid.SetColumn(name, 2); row.Children.Add(name);
            var cnt = new TextBlock { Text = p.Elements.Count(e => e.Group == g.Id).ToString(), FontSize = 11.5, Foreground = Res("TextSecondary"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(4, 0) };
            ToolTip.SetTip(cnt, "Elementi nel gruppo");
            Grid.SetColumn(cnt, 3); row.Children.Add(cnt);
            bool assigning = _ed.AssignGroup == g.Id;
            // «+» attiva soltanto la modifica del gruppo; si esce con «Salva gruppo» in alto (o Esc)
            var add = new Button { Classes = { "transparent" }, Padding = new Avalonia.Thickness(7, 2), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeight.Bold }, IsEnabled = !assigning };
            ToolTip.SetTip(add, "Modifica il gruppo: clic sugli elementi della mappa per aggiungerli o toglierli");
            add.Click += (_, _) => { if (_ed.AssignGroup != gg.Id) _ed.ToggleAssign(gg); Map.Focus(); };
            Grid.SetColumn(add, 4); row.Children.Add(add);
            var dup = IconBtn("IconCopy", "Nuova versione: copia il gruppo e nasconde questo", Res("TextSecondary"), () => _ed.DuplicateGroup(gg));
            Grid.SetColumn(dup, 5); row.Children.Add(dup);
            var del = IconBtn("IconTrash", "Elimina il gruppo (gli elementi restano, senza gruppo)", Res("TextTertiary"), () => _ed.DeleteGroup(gg));
            Grid.SetColumn(del, 6); row.Children.Add(del);
            GroupList.Children.Add(border);
            last = name;
        }
        if (_focusGroup && last != null)
        {
            _focusGroup = false;
            var t = last;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { t.Focus(); t.SelectAll(); }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    void UpdatePhotoList()
    {
        var ph = _ed.Project.Photos;
        NoPhotoText.IsVisible = ph.Count == 0;
        var pq = (PhotoSearch.Text ?? "").Trim();
        var sig = pq + "§" + (_photoRef?.Name ?? "") + "#" + string.Join("|", ph.Select(r => $"{r.Name},{r.Pairs},{r.Cam.HasValue},{r.Missing}"));
        if (sig == _photoSig) return;
        _photoSig = sig;
        PhotoList.Children.Clear();
        foreach (var r in ph.Where(x => Match(x.Name, pq)))
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
        TabDati.IsVisible = t == 0; TabOpt.IsVisible = t == 1; TabVar.IsVisible = t == 2; TabInfo.IsVisible = t == 3;
        TabDatiBtn.Classes.Set("on", t == 0); TabOptBtn.Classes.Set("on", t == 1);
        TabVarBtn.Classes.Set("on", t == 2); TabInfoBtn.Classes.Set("on", t == 3);
    }


    void OpenAddCard()
    {
        _kind = 0; _sub = 0; ResetTypeStep();
        _addOpen = true; _lastElemCount = _ed.Project.Elements.Count; _ed.Select(null); UpdatePanels();
    }

    // ───────────── file aperti di recente (riquadro «Inizia») ─────────────

    string _recentSig = "";

    void UpdateRecentList()
    {
        var list = RecentFiles.Visible();
        var sig = string.Join("|", list.Select(e => e.Path + e.Pinned + File.Exists(e.Path)));
        RecentPanel.IsVisible = list.Count > 0;
        if (sig == _recentSig) return;
        _recentSig = sig;
        RecentList.Children.Clear();
        IBrush R(string k) => (IBrush)this.FindResource(k)!;
        foreach (var e in list)
        {
            var en = e;
            bool exists = File.Exists(e.Path);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 2 };
            var open = new Button { Classes = { "transparent" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Padding = new Avalonia.Thickness(8, 5), Opacity = exists ? 1 : 0.5 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            row.Children.Add(new PathIcon
            {
                Data = (Avalonia.Media.Geometry)this.FindResource(e.Kind == "geopackage" ? "IconMap" : "IconFile")!, Width = 15, Height = 15,
                Foreground = R(e.Kind == "geopackage" ? "LineABrush" : "AccentBrush"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var txt = new StackPanel { Spacing = 0 };
            txt.Children.Add(new TextBlock { Text = Path.GetFileName(e.Path), FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            txt.Children.Add(new TextBlock
            {
                Text = exists ? Path.GetDirectoryName(e.Path) : "file non trovato · " + Path.GetDirectoryName(e.Path),
                FontSize = 11, Foreground = R("TextTertiary"), TextTrimming = TextTrimming.PrefixCharacterEllipsis
            });
            Grid.SetColumn(txt, 1); row.Children.Add(txt);
            open.Content = row;
            ToolTip.SetTip(open, e.Path);
            open.Click += async (_, _) => await OpenRecent(en);
            g.Children.Add(open);
            var pin = new Button
            {
                Classes = { "transparent" }, Padding = new Avalonia.Thickness(7, 5), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Content = new PathIcon { Data = (Avalonia.Media.Geometry)this.FindResource(e.Pinned ? "IconPin" : "IconPinOutline")!, Width = 14, Height = 14, Foreground = e.Pinned ? R("AccentBrush") : R("TextTertiary") }
            };
            ToolTip.SetTip(pin, e.Pinned ? "Togli la puntina" : "Fissa in alto all'elenco");
            pin.Click += (_, _) => { RecentFiles.TogglePin(en.Path); _recentSig = ""; UpdateRecentList(); };
            Grid.SetColumn(pin, 1); g.Children.Add(pin);
            RecentList.Children.Add(g);
        }
    }

    async Task OpenRecent(RecentFiles.Entry e)
    {
        if (!File.Exists(e.Path))
        {
            Fail($"Non trovo «{e.Path}»: forse è stato spostato o cancellato. L'ho tolto dall'elenco.");
            RecentFiles.Remove(e.Path); _recentSig = ""; UpdateRecentList();
            return;
        }
        if (e.Kind == "geopackage")
        {
            if (new FileInfo(e.Path).Length > LargeFileBytes) OpenClip(new[] { e.Path });
            else LoadGpkg(e.Path);
        }
        else await OpenProjectPath(e.Path);
    }


    /// <summary>«Converti in poligono»: avviso che non si torna indietro, poi conversione.</summary>
    async Task ConvertToPolygon()
    {
        if (_ed.Selected is not { } e) return;
        if (_ed.PolygonConversionBlocker() is { } why) { Fail(why); return; }
        var linked = _ed.LinkedToSelected();
        var text = $"Una volta convertito in poligono, «{e.Name}» non potrà più tornare un tracciato: larghezza, variatori di sezione, " +
                   "strisce di mezzeria e lato non saranno più modificabili. Resteranno il tipo, il nome e la forma attuale, e potrai spostare i vertici del contorno.";
        if (linked.Count > 0)
            text += $"\n\n{string.Join(", ", linked.Select(l => "«" + l.Name + "»"))} {(linked.Count == 1 ? "è agganciato" : "sono agganciati")} a questo elemento: {(linked.Count == 1 ? "resterà" : "resteranno")} dove {(linked.Count == 1 ? "è" : "sono")} ma non lo {(linked.Count == 1 ? "seguirà" : "seguiranno")} più.";
        if (!await AskConfirm("Convertire in poligono?", text, "Converti in poligono")) return;
        _ed.ConvertSelectedToPolygon();
    }

    /// <summary>Finestra di conferma con Annulla (predefinito) e il pulsante dell'azione.</summary>
    async Task<bool> AskConfirm(string title, string text, string okText)
    {
        bool ok = false;
        var dlg = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var yes = new Button { Content = okText, Classes = { "primary" }, MinWidth = 110, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var no = new Button { Content = "Annulla", MinWidth = 96, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsCancel = true, IsDefault = true };
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new Border
        {
            Padding = new Avalonia.Thickness(24, 20), MaxWidth = 480,
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children =
                    {
                        new TextBlock { Text = "⚠", FontSize = 18, Foreground = (IBrush)this.FindResource("WarnBrush")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                        new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                    } },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)this.FindResource("TextSecondary")! },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 6, 0, 0), Children = { no, yes } }
                }
            }
        };
        await dlg.ShowDialog(this);
        return ok;
    }


    // ───────────── Sezioni ─────────────

    string _secListSig = "", _secTableSig = "";

    void RenameSection()
    {
        if (_ed.SelectedSection is { } s && !string.IsNullOrWhiteSpace(SectionNameBox.Text) && SectionNameBox.Text.Trim() != s.Name)
        { s.Name = SectionNameBox.Text.Trim(); _ed.NotifyAll(); }
    }

    void UpdateSections()
    {
        var p = _ed.Project;
        NoSectionText.IsVisible = p.Sections.Count == 0;
        NewSectionBtn.IsEnabled = _ed.Data != null && (_ed.Mode == Mode.Idle || _ed.Mode == Mode.PickSection);
        NewSectionBtn.Classes.Set("primary", _ed.Mode == Mode.PickSection);

        // elenco nel riquadro a sinistra
        var sq = (SectionSearch.Text ?? "").Trim();
        var sig = sq + "§" + (_ed.SelectedSection?.Id ?? -1) + "#" + string.Join("|", p.Sections.Select(s => $"{s.Id},{s.Name},{s.Length:0.0}"));
        if (sig != _secListSig)
        {
            _secListSig = sig;
            SectionList.Children.Clear();
            foreach (var s in p.Sections.Where(x => Match(x.Name, sq)))
            {
                var ss = s;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 2 };
                var open = new Button { Classes = { "transparent" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left, Padding = new Avalonia.Thickness(6, 4) };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new Border { Width = 14, Height = 3, Background = Brushes.White, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var nm = new TextBlock { Text = s.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Grid.SetColumn(nm, 1); row.Children.Add(nm);
                var ln = new TextBlock { Text = M(s.Length), FontSize = 11.5, Foreground = (IBrush)this.FindResource("TextSecondary")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Grid.SetColumn(ln, 2); row.Children.Add(ln);
                open.Content = row;
                if (ss == _ed.SelectedSection) { open.Background = (IBrush)this.FindResource("SelectedBackground")!; nm.FontWeight = FontWeight.SemiBold; }
                open.Click += (_, _) => { _ed.SelectSection(ss); Map.Focus(); };
                g.Children.Add(open);
                var del = new Button { Classes = { "transparent" }, Padding = new Avalonia.Thickness(6, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Content = new PathIcon { Data = (Avalonia.Media.Geometry)this.FindResource("IconTrash")!, Width = 12, Height = 12, Foreground = (IBrush)this.FindResource("TextTertiary")! } };
                ToolTip.SetTip(del, "Elimina la sezione");
                del.Click += (_, _) => _ed.DeleteSection(ss);
                Grid.SetColumn(del, 1); g.Children.Add(del);
                SectionList.Children.Add(g);
            }
        }

        // disegno in sezione
        var sec = _ed.SelectedSection;
        SectionPanel.IsVisible = sec != null && !PhotoHost.IsVisible;
        SectionPanel.Margin = new Avalonia.Thickness(12, 0, RightPanel.IsVisible ? 384 : 12, 12);
        if (sec == null) return;
        if (!SectionNameBox.IsFocused && SectionNameBox.Text != sec.Name) SectionNameBox.Text = sec.Name;
        var r = SectionCalc.Compute(sec, p, _ed.Data);
        SectionDrawing.Section = sec; SectionDrawing.Result = r; SectionDrawing.InvalidateVisual();
        var (lft, rgt) = sec.Flip ? ("B", "A") : ("A", "B");
        SectionInfo.Text = $"linea di {M(sec.Length)} · vista con {lft} a sinistra e {rgt} a destra";
        var tsig = sec.Id + "|" + string.Join(";", r.Pieces.Select(x => $"{x.Element!.Id},{x.Element.Name},{x.Element.Type},{x.Width:0.00},{x.Inside?.Id}")) + "|" + r.BetweenBuildings;
        if (tsig == _secTableSig) return;
        _secTableSig = tsig;
        SectionTable.Children.Clear();
        int n = 0;
        foreach (var x in r.Pieces)
        {
            n++;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
            var badge = new Border { Width = 18, Height = 18, CornerRadius = new Avalonia.CornerRadius(9), Background = (IBrush)this.FindResource("AccentBrush")!,
                Child = new TextBlock { Text = n.ToString(), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } };
            g.Children.Add(badge);
            var sw = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(2), Background = MapCanvas.Colors(x.Element!.Type, 255).fill, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(sw, 1); g.Children.Add(sw);
            var txt = new StackPanel { Spacing = 0 };
            txt.Children.Add(new TextBlock { Text = x.Element.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            txt.Children.Add(new TextBlock { Text = Element.TypeLabel(x.Element.Type) + (x.Inside is { } ins ? $" · dentro {ins.Name}" : ""), FontSize = 11, Foreground = (IBrush)this.FindResource("TextSecondary")!, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(txt, 2); g.Children.Add(txt);
            var wd = new TextBlock { Text = M(x.Width), FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(wd, 3); g.Children.Add(wd);
            SectionTable.Children.Add(g);
        }
        if (n > 0)
        {
            SectionTable.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Height = 1, Fill = (IBrush)this.FindResource("CardBorder")!, Margin = new Avalonia.Thickness(0, 4) });
            SectionTable.Children.Add(TotRow("Larghezza complessiva degli elementi", M(r.ElementsWidth), true));
            if (r.BetweenBuildings is { } bb) SectionTable.Children.Add(TotRow("Tra gli edifici", M(bb), false));
        }
        else SectionTable.Children.Add(new TextBlock { Classes = { "note" }, Text = "La linea non attraversa nessun elemento: spostane i capi A e B trascinandoli sulla mappa." });

        Grid TotRow(string label, string value, bool bold)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (IBrush)this.FindResource(bold ? "TextPrimary" : "TextSecondary")!, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap });
            var v = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = (IBrush)this.FindResource(bold ? "AccentBrush" : "TextPrimary")! };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            return g;
        }
    }


    /// <summary>Immagine PNG della sezione su fondo bianco: titolo, disegno (con pedoni, ciclisti e auto) e legenda con le misure e la somma.</summary>
    async Task SaveSectionPng()
    {
        if (_ed.SelectedSection is not { } sec || SectionDrawing.Result is not { } r) return;
        var path = await PickSave("Salva sezione come immagine", "Immagine PNG", "png", (string.IsNullOrWhiteSpace(sec.Name) ? "sezione" : sec.Name) + ".png");
        if (path == null) return;
        try
        {
            var bmp = RenderSectionImage(sec, r);
            bmp.Save(path);
            Ok("Sezione salvata in " + path);
        }
        catch (Exception ex) { Fail("Non riesco a salvare l'immagine: " + ex.Message); }
    }

    public Avalonia.Media.Imaging.RenderTargetBitmap RenderSectionImage(CrossSection sec, SectionResult r)
    {
        var (page, size) = BuildSectionPage(sec, r);
        // a 96 dpi (scala 1): con scale diverse alcune primitive del disegno uscivano fuori posto
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Avalonia.Vector(96, 96));
        bmp.Render(page);
        return bmp;
    }

    /// <summary>Pagina della sezione (titolo, disegno, legenda) pronta da disegnare, con la sua misura.</summary>
    (Control page, Avalonia.Size size) BuildSectionPage(CrossSection sec, SectionResult r)
    {
        const double W = 2200;
        IBrush ink = Brushes.Black, grey = new SolidColorBrush(Color.Parse("#FF5E6672")), line = new SolidColorBrush(Color.Parse("#FFD5D9DE"));
        var root = new StackPanel { Spacing = 14, Width = W };
        root.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(sec.Name) ? "Sezione" : sec.Name, FontSize = 24, FontWeight = FontWeight.Bold, Foreground = ink });
        root.Children.Add(new SectionView { Light = true, TextScale = 1.7, Result = r, Section = sec, Height = 560, Width = W });
        // legenda: numero, colore, nome, tipo, larghezza; poi la somma
        var legend = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 6 };
        int row = 0, n = 0;
        void Add(Control c, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); legend.Children.Add(c); }
        foreach (var x in r.Pieces)
        {
            n++;
            legend.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Add(new Border { Width = 24, Height = 24, CornerRadius = new Avalonia.CornerRadius(12), Background = new SolidColorBrush(Color.Parse("#FF2FCB79")),
                Child = new TextBlock { Text = n.ToString(), FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } }, 0);
            Add(new Border { Width = 18, Height = 18, CornerRadius = new Avalonia.CornerRadius(3), Background = MapCanvas.Colors(x.Element!.Type, 255).fill, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 1);
            Add(new TextBlock { Text = x.Element.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = ink, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 2);
            Add(new TextBlock { Text = Element.TypeLabel(x.Element.Type) + (x.Inside is { } ins ? $" · dentro {ins.Name}" : ""), FontSize = 15, Foreground = grey, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 3);
            Add(new TextBlock { Text = M(x.Width), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = ink, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 4);
            row++;
        }
        legend.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var sep = new Avalonia.Controls.Shapes.Rectangle { Height = 1, Fill = line, Margin = new Avalonia.Thickness(0, 4) };
        Grid.SetColumnSpan(sep, 5); Add(sep, 0); row++;
        void Tot(string label, string value, bool bold)
        {
            legend.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, FontSize = 16, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Foreground = bold ? ink : grey };
            Grid.SetColumnSpan(l, 4); Add(l, 0);
            Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = ink, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }, 4);
            row++;
        }
        Tot($"Totale ({n} {(n == 1 ? "elemento" : "elementi")})", M(r.ElementsWidth), true);
        if (r.BetweenBuildings is { } bb) Tot("Tra gli edifici", M(bb), false);
        root.Children.Add(legend);
        var page = new Border { Background = Brushes.White, Padding = new Avalonia.Thickness(32), Child = root };
        page.Measure(new Avalonia.Size(W + 64, double.PositiveInfinity));
        page.Arrange(new Avalonia.Rect(page.DesiredSize));
        return (page, page.DesiredSize);
    }

    // ───────────── Annulla / Ripeti ─────────────
    // Dopo ogni modifica completata si tiene una copia del progetto (come quando si salva): Annulla torna alla copia precedente.

    readonly List<string> _undo = new(), _redo = new();
    string? _histSnap;
    bool _restoring;
    const int MaxHistory = 100;

    void ResetHistory()
    {
        _undo.Clear(); _redo.Clear();
        _histSnap = _ed.Data != null ? Snapshot() : null;
        UpdateUndoMenu();
    }

    bool _histPending;
    /// <summary>Le notifiche dello stesso momento diventano un solo passo: si registra appena l'operazione è finita.</summary>
    void ScheduleHistory()
    {
        if (_restoring || _ed.Data == null || _histPending) return;
        _histPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _histPending = false; RecordHistory(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Se il progetto è cambiato (e non si sta disegnando), la versione precedente va nella cronologia.</summary>
    void RecordHistory()
    {
        if (_restoring || _ed.Data == null || _ed.Mode != Mode.Idle) return;
        var snap = Snapshot();
        if (_histSnap == null) { _histSnap = snap; UpdateUndoMenu(); return; }
        if (snap == _histSnap) return;
        _undo.Add(_histSnap);
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _redo.Clear();
        _histSnap = snap;
        UpdateUndoMenu();
    }

    public void Undo()
    {
        if (_ed.Data == null) return;
        if (_ed.Mode != Mode.Idle) { CancelCurrent(); return; }   // prima si annulla il disegno in corso
        RecordHistory();
        if (_undo.Count == 0) { _ed.Message = "Niente da annullare."; _ed.NotifyAll(); return; }
        _redo.Add(_histSnap!);
        var prev = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(prev);
        _ed.Message = $"Annullato. ({_undo.Count} {(_undo.Count == 1 ? "passo" : "passi")} ancora da annullare)";
        _ed.NotifyAll();
    }

    public void Redo()
    {
        if (_ed.Data == null || _ed.Mode != Mode.Idle) return;
        RecordHistory();
        if (_redo.Count == 0) { _ed.Message = "Niente da ripetere."; _ed.NotifyAll(); return; }
        _undo.Add(_histSnap!);
        var next = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Restore(next);
        _ed.Message = "Ripetuto.";
        _ed.NotifyAll();
    }

    void CancelCurrent()
    {
        if (_ed.Mode == Mode.Trace) _ed.TraceCancel();
        else if (_ed.Mode == Mode.EditLine) _ed.FinishEditLine();
        else _ed.CancelDraft();
    }

    /// <summary>Rimette il progetto com'era in quella copia, tenendo selezionato (se c'è ancora) lo stesso elemento o la stessa sezione.</summary>
    void Restore(string json)
    {
        if (_ed.Data is not { } d) return;
        _restoring = true;
        try
        {
            int? selId = _ed.Selected?.Id, secId = _ed.SelectedSection?.Id;
            var dto = ProjectFile.FromJson(json);
            var proj = ProjectFile.ToProject(dto, d.Origin);
            ProjectFile.LoadPhotos(dto, proj, _projectPath ?? Path.Combine(Path.GetTempPath(), "progetto.ciclo"), d.Origin);
            foreach (var l in d.Layers)
            {
                if (dto.LayerRoles.TryGetValue(l.Name, out var r)) l.Role = r;
                l.Visible = !dto.HiddenLayers.Contains(l.Name);
            }
            _ed.Project = proj;
            _ed.ClearPick();
            _ed.Selected = selId is { } si ? proj.Find(si) : null;
            _ed.SelectedSection = secId is { } ci ? proj.Sections.FirstOrDefault(s => s.Id == ci) : null;
            if (_photoRef != null && !proj.Photos.Any(p => string.Equals(p.Path, _photoRef.Path, StringComparison.OrdinalIgnoreCase))) ClosePhotoView();
            _histSnap = json;
            Map.InvalidateBackground();
            UpdateUndoMenu();
        }
        finally { _restoring = false; }
    }

    void UpdateUndoMenu()
    {
        UndoMenu.IsEnabled = _undo.Count > 0 || _ed.Mode != Mode.Idle;
        RedoMenu.IsEnabled = _redo.Count > 0;
    }

}
