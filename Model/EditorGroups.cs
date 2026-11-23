using CicloDraft.Geometry;
using CicloDraft.IO;

namespace CicloDraft.Model;

/// <summary>Blocchi di elementi: creare, rinominare, mostrare/nascondere, duplicare (versioni), spostare elementi.</summary>
public partial class Editor
{
    /// <summary>Blocco in cui vanno i nuovi elementi (0 = nessuno).</summary>
    public int CurrentGroup;

    public ElementGroup? FindGroup(int id) => id == 0 ? null : Project.Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>Il nuovo elemento entra nel blocco corrente (se esiste ed è visibile).</summary>
    void Adopt(Element e) => e.Group = FindGroup(CurrentGroup) is { Visible: true } g ? g.Id : 0;

    int NextGroupId() => Project.Groups.Count == 0 ? 1 : Project.Groups.Max(g => g.Id) + 1;

    /// <summary>Nuovo blocco; gli elementi selezionati (uno o più) ci vengono spostati. Diventa il blocco corrente.</summary>
    public ElementGroup NewGroup()
    {
        var g = new ElementGroup { Id = NextGroupId(), Name = Project.NewGroupName() };
        Project.Groups.Add(g);
        foreach (var e in SelectedElements()) e.Group = g.Id;
        CurrentGroup = g.Id;
        NotifyAll();
        return g;
    }

    IEnumerable<Element> SelectedElements() => MultiSel.Count > 0 ? MultiSel.ToList() : Selected != null ? new[] { Selected } : Array.Empty<Element>();

    public void RenameGroup(ElementGroup g, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == g.Name) return;
        g.Name = name;
        NotifyAll();
    }

    public void SetGroupVisible(ElementGroup g, bool visible)
    {
        if (g.Visible == visible) return;
        g.Visible = visible;
        if (!visible)
        {
            if (Selected is { } s && s.Group == g.Id) { Selected = null; VarSel = -1; }
            MultiSel.RemoveAll(e => e.Group == g.Id);
            if (!HasPick) ClearPick(); else UpdatePick();
        }
        Project.Regenerate();
        NotifyAll();
    }

    /// <summary>Mostra solo questo blocco e nasconde tutti gli altri.</summary>
    public void SoloGroup(ElementGroup g)
    {
        foreach (var o in Project.Groups) o.Visible = o == g;
        if (Selected is { } s && !Project.IsShown(s)) Selected = null;
        ClearPick();
        Project.Regenerate();
        NotifyAll();
    }

    /// <summary>Sposta l'elemento selezionato in un blocco (0 = nessun blocco).</summary>
    public void SetSelectedGroup(int id)
    {
        if (Selected is not { } e || e.Group == id) return;
        e.Group = FindGroup(id) is { } g ? g.Id : 0;
        if (!Project.IsShown(e)) Selected = null;
        NotifyAll();
    }

    /// <summary>Elimina il blocco: gli elementi restano, senza blocco.</summary>
    public void DeleteGroup(ElementGroup g)
    {
        foreach (var e in Project.Elements.Where(e => e.Group == g.Id)) e.Group = 0;
        Project.Groups.Remove(g);
        if (CurrentGroup == g.Id) CurrentGroup = 0;
        Project.Regenerate();
        NotifyAll();
    }

    /// <summary>
    /// Nuova versione del blocco: copia tutti i suoi elementi (con le loro linee) in un nuovo blocco, che diventa
    /// visibile e corrente, mentre l'originale viene nascosto. Gli agganci tra elementi del blocco passano alle copie;
    /// quelli verso elementi fuori dal blocco restano.
    /// </summary>
    public ElementGroup DuplicateGroup(ElementGroup g)
    {
        int k = 2;
        string baseName = System.Text.RegularExpressions.Regex.Replace(g.Name, @" – versione \d+$", "");
        while (Project.Groups.Any(x => x.Name == $"{baseName} – versione {k}")) k++;
        var ng = new ElementGroup { Id = NextGroupId(), Name = $"{baseName} – versione {k}" };

        // copia profonda passando per il formato del progetto (stesse regole di salvataggio e caricamento)
        var origin = Data?.Origin ?? new V2(0, 0);
        var copy = ProjectFile.ToProject(ProjectFile.FromJson(ProjectFile.ToJson("copia.ciclo", Project, Data)), origin);
        var els = copy.Elements.Where(e => e.Group == g.Id).ToList();
        var elMap = new Dictionary<int, int>();
        foreach (var e in els) elMap[e.Id] = Project.NextId++;
        var edges = copy.Edges.Where(ed => ed.Owner >= 0 && elMap.ContainsKey(ed.Owner)).ToList();
        int nextEdge = Project.Edges.Count == 0 ? 0 : Project.Edges.Max(x => x.Id) + 1;
        var edMap = new Dictionary<int, int>();
        foreach (var ed in edges) edMap[ed.Id] = nextEdge++;

        EdgeRef Map(EdgeRef r) => r.Kind == EdgeKind.Road
            ? (edMap.TryGetValue(r.Id, out var ne) ? new EdgeRef(r.Kind, ne) : r)
            : (elMap.TryGetValue(r.Id, out var nl) ? new EdgeRef(r.Kind, nl) : r);

        foreach (var ed in edges)
        {
            ed.Id = edMap[ed.Id]; ed.Owner = elMap[ed.Owner];
            if (ed.Anchors != null) foreach (var a in ed.Anchors) if (elMap.TryGetValue(a.El, out var na)) a.El = na;
            Project.Edges.Add(ed);
        }
        foreach (var e in els)
        {
            e.Id = elMap[e.Id]; e.Group = ng.Id;
            e.Base = Map(e.Base);
            if (e.Other is { } o) e.Other = Map(o);
            Project.Elements.Add(e);
        }
        Project.Groups.Insert(Project.Groups.IndexOf(g) + 1, ng);
        g.Visible = false;
        if (Selected is { } s && s.Group == g.Id) Selected = null;
        ClearPick();
        CurrentGroup = ng.Id;
        Project.Regenerate();
        NotifyAll();
        return ng;
    }
}
