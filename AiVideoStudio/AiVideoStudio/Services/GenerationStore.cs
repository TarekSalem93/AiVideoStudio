using System.Text.Json;

namespace AiVideoStudio.Services;

// ponytail: flat JSON file instead of SQLite — dozens of rows, no queries, human-readable; migrate when relational needs appear
public record Generation(string Id, DateTime Created, string Template, int Seed, int Steps, string StoryPreview, string H3Prompt, List<string> Outputs, List<string> Media);

public class GenerationStore
{
    private static string Path_ => Path.Combine(FileSystem.AppDataDirectory, "generations.json");
    private List<Generation> _items = new();

    public IReadOnlyList<Generation> Items => _items;

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(Path_)) return;
            _items = JsonSerializer.Deserialize<List<Generation>>(await File.ReadAllTextAsync(Path_)) ?? new();
        }
        catch { _items = new(); }
    }

    public async Task AddAsync(Generation g)
    {
        _items.Insert(0, g);
        _items = _items.Take(100).ToList();
        try { await File.WriteAllTextAsync(Path_, JsonSerializer.Serialize(_items)); }
        catch { }
    }
}
