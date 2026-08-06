using System.Text.Json;
using EpicSetup.Models;

namespace EpicSetup.Tests;

public static class CatalogFixture
{
    public static Catalog Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "catalog.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Catalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("catalog.json did not deserialize.");
    }

    public static IReadOnlyList<AppEntry> AllApps(Catalog catalog)
        => catalog.Tabs.SelectMany(t => t.Categories).SelectMany(c => c.Apps).ToList();
}
