using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CicloDraft.Model;

namespace CicloDraft;

public partial class ExportPdfWindow : Window
{
    readonly MainWindow? _main;
    readonly List<(CheckBox box, int id)> _boxes = new();
    readonly string _name = "progetto";

    public ExportPdfWindow() { InitializeComponent(); }

    public ExportPdfWindow(MainWindow main, Project p, string name) : this()
    {
        _main = main; _name = name;
        if (p.Groups.Count == 0)
            AddRow(0, "Tutto il progetto", null, p.Elements.Count(e => e.Polygon.Count >= 3), true);
        else
            foreach (var g in p.Groups)
                AddRow(g.Id, g.Name, g.Color, p.Elements.Count(e => e.Group == g.Id && e.Polygon.Count >= 3), g.Visible);
        AllBtn.Click += (_, _) => { foreach (var b in _boxes) if (b.box.IsEnabled) b.box.IsChecked = true; };
        NoneBtn.Click += (_, _) => { foreach (var b in _boxes) b.box.IsChecked = false; };
        AllBtn.IsVisible = NoneBtn.IsVisible = _boxes.Count > 1;
        CloseBtn.Click += (_, _) => Close();
        ExportBtn.Click += async (_, _) => await Export();
    }

    void AddRow(int id, string name, string? color, int n, bool on)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        if (color != null)
            row.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new Avalonia.CornerRadius(6), Background = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 11, Foreground = (IBrush)this.FindResource("TextSecondary")!, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var cb = new CheckBox { Content = row, IsChecked = on && n > 0, IsEnabled = n > 0 };
        _boxes.Add((cb, id));
        GroupList.Children.Add(cb);
    }

    public PdfOptions Options() => new()
    {
        Groups = _boxes.Where(b => b.box.IsChecked == true).Select(b => b.id).ToList(),
        Sections = SectionsCheck.IsChecked == true,
        Photos = PhotosCheck.IsChecked == true,
        A4 = FormatCombo.SelectedIndex == 1
    };

    async Task Export()
    {
        var o = Options();
        if (o.Groups.Count == 0) { StatusText.Text = "Scegli almeno un gruppo."; return; }
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Esporta PDF", SuggestedFileName = _name + ".pdf", DefaultExtension = "pdf", ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        await ExportTo(path);
    }

    public async Task<bool> ExportTo(string path)
    {
        if (_main == null) return false;
        ExportBtn.IsEnabled = false;
        try
        {
            var r = await _main.ExportPdf(path, Options(), s => StatusText.Text = s);
            StatusText.Text = $"Esportato {Path.GetFileName(path)}: {r}.";
            return true;
        }
        catch (Exception ex) { StatusText.Text = "Esportazione non riuscita: " + ex.Message; return false; }
        finally { ExportBtn.IsEnabled = true; }
    }
}
