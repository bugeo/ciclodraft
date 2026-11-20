using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CicloDraft.IO;
using CicloDraft.Model;

namespace CicloDraft;

public partial class Export3DWindow : Window
{
    readonly Project? _project;
    readonly GpkgData? _data;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    public Export3DWindow() { InitializeComponent(); }

    public Export3DWindow(Project project, GpkgData? data) : this()
    {
        _project = project; _data = data;
        var vol = data?.Layers.Where(l => (LayerRole)l.Role == LayerRole.Edifici3D).ToList() ?? new();
        var walls = data?.Layers.Where(l => (LayerRole)l.Role == LayerRole.Muri).ToList() ?? new();
        var src = vol.Count > 0 ? vol : walls;
        int withH = src.Sum(l => l.Polys.Count(p => p.Height is > 0.5 and < 400));
        int tot = src.Sum(l => l.Polys.Count);
        HeightsText.Text = tot == 0
            ? "Nessun livello con ruolo «Edifici 3D» o «Muri abitazioni»: la scena avrà solo il progetto."
            : (vol.Count > 0 ? "Volumi dal livello " : "Volumi dai muri delle abitazioni: ") + string.Join(", ", src.Select(l => l.Name))
              + (withH > 0 ? $". {withH.ToString("N0", It)} su {tot.ToString("N0", It)} hanno l'altezza nei dati ({string.Join(", ", src.Where(l => l.HeightColumn != null).Select(l => l.HeightColumn).Distinct())})."
                           : $". Nessuna altezza nei dati: si usa il valore qui sotto. Se hai il livello delle unità volumetriche (DBGT: UN_VOL), aggiungilo nel ritaglio e dagli il ruolo «Edifici 3D».");
        BuildingsCheck.IsChecked = tot > 0;
        int trees = data?.Layers.Where(l => l.Role != 0 && l.IsTrees).Sum(l => l.Points.Count) ?? 0;
        TreesText.Text = trees > 0
            ? $"Alberi dal livello {string.Join(", ", data!.Layers.Where(l => l.Role != 0 && l.IsTrees).Select(l => l.Name))}: {trees.ToString("N0", It)} (quelli che cadono sul progetto vengono tolti). Lampioni ogni 25 m sui marciapiedi del progetto."
            : "Nessun livello di alberi (es. DBGT: ALBERO; aggiungilo nel ritaglio). Lampioni ogni 25 m sui marciapiedi del progetto.";
        TreesCheck.IsEnabled = trees > 0;

        CloseBtn.Click += (_, _) => Close();
        ExportBtn.Click += async (_, _) => await Export();
    }

    Export3DOptions Options() => new()
    {
        Buildings = BuildingsCheck.IsChecked == true,
        DefaultBuildingHeight = (double)(DefaultHBox.Value ?? 10),
        ExistingSidewalks = SidewalksCheck.IsChecked == true,
        Markings = MarkingsCheck.IsChecked == true,
        Ground = GroundCheck.IsChecked == true,
        CurbHeight = (double)(CurbHBox.Value ?? 0.15m),
        CurbWidth = (double)(CurbWBox.Value ?? 0.15m),
        RaisedBikeLane = RaisedBikeCheck.IsChecked == true,
        Textures = TexturesCheck.IsChecked == true,
        Trees = TreesCheck.IsChecked == true,
        StreetLights = LightsCheck.IsChecked == true,
        BikeSymbols = BikeSymCheck.IsChecked == true,
        Sun = SunCheck.IsChecked == true
    };

    async Task Export()
    {
        if (_project == null) return;
        var name = _data != null ? Path.GetFileNameWithoutExtension(_data.Path) + "_3d.glb" : "progetto_3d.glb";
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Esporta 3D", SuggestedFileName = name, DefaultExtension = "glb", ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("glTF binario") { Patterns = new[] { "*.glb" } } }
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        await ExportTo(path);
    }

    public async Task<Export3DResult?> ExportTo(string path)
    {
        var o = Options();
        ExportBtn.IsEnabled = false;
        StatusText.Text = "Costruzione della scena 3D…";
        try
        {
            var res = await Task.Run(() => Export3D.Write(path, _project!, _data, o));
            StatusText.Text = $"Esportato {Path.GetFileName(res.Path)}: {res.Elements} elementi del progetto, {res.Buildings.ToString("N0", It)} volumi di edifici, {res.Trees.ToString("N0", It)} alberi, " +
                              $"{res.Triangles.ToString("N0", It)} triangoli, {(res.Bytes / 1048576.0).ToString("0.0", It)} MB.";
            return res;
        }
        catch (Exception ex) { StatusText.Text = "Esportazione non riuscita: " + ex.Message; return null; }
        finally { ExportBtn.IsEnabled = true; }
    }
}
