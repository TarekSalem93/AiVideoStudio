using System.Text.Json;

namespace AiVideoStudio.Services;

// ponytail: same flat-JSON pattern as GenerationStore; one file per concern, graduates together if ever needed
public record Scene(string Id, int Index, string Beats, string H3Prompt, long Seed, string Status, List<string> ClipPaths, string ChosenClip);
public record Project(string Id, string Title, DateTime Created, string Template, int Steps, List<string> Media, List<Scene> Scenes, string FinalPath, string Aspect = "9:16 (Portrait Widescreen)", double Mp = 0.4);

public class ProjectStore
{
    private static string Path_ => Path.Combine(FileSystem.AppDataDirectory, "projects.json");
    private List<Project> _items = new();

    public IReadOnlyList<Project> Items => _items;

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            _items = JsonSerializer.Deserialize<List<Project>>(await File.ReadAllTextAsync(Path_)) ?? new();
        }
        catch { _items = new(); }
    }

    public async Task SaveAsync()
    {
        try { await File.WriteAllTextAsync(Path_, JsonSerializer.Serialize(_items)); }
        catch { }
    }

    public async Task AddAsync(Project p) { _items.Insert(0, p); await SaveAsync(); }

    public async Task UpdateAsync(Project p)
    {
        var i = _items.FindIndex(x => x.Id == p.Id);
        if (i >= 0) _items[i] = p;
        await SaveAsync();
    }

    public async Task DeleteAsync(string id)
    {
        _items.RemoveAll(x => x.Id == id);
        await SaveAsync();
    }
}
