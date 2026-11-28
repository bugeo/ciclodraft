using System.Text.Json;

namespace CicloDraft.IO;

/// <summary>
/// File aperti di recente (progetti e GeoPackage) per il riquadro «Inizia»: gli ultimi 5 più quelli fissati con la puntina.
/// Si salvano nella cartella delle impostazioni dell'utente (Windows: %APPDATA%\CicloDraft, Mac: ~/Library/Application Support/CicloDraft).
/// </summary>
public static class RecentFiles
{
    public class Entry
    {
        public string Path { get; set; } = "";
        /// <summary>«progetto» o «geopackage».</summary>
        public string Kind { get; set; } = "progetto";
        public bool Pinned { get; set; }
        public DateTime Opened { get; set; }
    }

    public const int MaxRecent = 5;
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static string FilePath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CICLODRAFT_SETTINGS")
                      ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CicloDraft");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "recenti.json");
        }
    }

    public static List<Entry> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    static void Save(List<Entry> list)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Opts)); } catch { }
    }

    /// <summary>Elenco da mostrare: prima i fissati, poi gli ultimi aperti (al massimo 5 non fissati).</summary>
    public static List<Entry> Visible()
    {
        var all = Load();
        return all.Where(e => e.Pinned).OrderByDescending(e => e.Opened)
            .Concat(all.Where(e => !e.Pinned).OrderByDescending(e => e.Opened).Take(MaxRecent)).ToList();
    }

    public static void Add(string path, string kind)
    {
        var full = System.IO.Path.GetFullPath(path);
        var all = Load();
        var e = all.FirstOrDefault(x => Same(x.Path, full));
        if (e == null) { e = new Entry { Path = full, Kind = kind }; all.Add(e); }
        e.Kind = kind; e.Opened = DateTime.Now;
        // si tengono tutti i fissati e gli ultimi 5 non fissati
        var keep = all.Where(x => x.Pinned).Concat(all.Where(x => !x.Pinned).OrderByDescending(x => x.Opened).Take(MaxRecent)).ToList();
        Save(keep);
    }

    public static void TogglePin(string path)
    {
        var all = Load();
        if (all.FirstOrDefault(x => Same(x.Path, path)) is { } e) { e.Pinned = !e.Pinned; Save(all); }
    }

    public static void Remove(string path)
    {
        var all = Load();
        all.RemoveAll(x => Same(x.Path, path));
        Save(all);
    }

    static bool Same(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
