using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfStudio.App.Services;

public sealed class RecentFileItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string LastOpenedDisplay
    {
        get
        {
            DateTime local = LastOpenedUtc.ToLocalTime();
            DateTime today = DateTime.Today;

            if (local.Date == today) return $"Hôm nay, {local:HH:mm}";
            if (local.Date == today.AddDays(-1)) return $"Hôm qua, {local:HH:mm}";
            return local.ToString("dd/MM/yyyy HH:mm");
        }
    }
}

public static class RecentFilesService
{
    public const int MaxItems = 8;

    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfStudio");

    private static readonly string StoreFilePath = Path.Combine(StoreDirectory, "recent.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<RecentFileItem> Load()
    {
        try
        {
            if (!File.Exists(StoreFilePath)) return [];

            string json = File.ReadAllText(StoreFilePath);
            var items = JsonSerializer.Deserialize<List<RecentFileItem>>(json, JsonOptions);
            if (items is null) return [];

            var result = new List<RecentFileItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.FilePath) || !seen.Add(item.FilePath))
                    continue;

                item.FileName = string.IsNullOrWhiteSpace(item.FileName)
                    ? Path.GetFileName(item.FilePath)
                    : item.FileName;
                item.DirectoryPath = string.IsNullOrWhiteSpace(item.DirectoryPath)
                    ? Path.GetDirectoryName(item.FilePath) ?? string.Empty
                    : item.DirectoryPath;

                result.Add(item);
                if (result.Count >= MaxItems) break;
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<RecentFileItem> items)
    {
        try
        {
            Directory.CreateDirectory(StoreDirectory);
            string json = JsonSerializer.Serialize(items.Take(MaxItems).ToList(), JsonOptions);
            File.WriteAllText(StoreFilePath, json);
        }
        catch
        {
            // Bỏ qua nếu không có quyền ghi đĩa
        }
    }
}