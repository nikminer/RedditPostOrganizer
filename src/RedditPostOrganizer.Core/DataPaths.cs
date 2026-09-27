namespace RedditPostOrganizer.Core;

public static class DataPaths
{
    public static string ResolveDefault(string? localAppData = null)
    {
        var parent =
            localAppData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(parent, "RedditPostOrganizer");
        var legacy = Path.Combine(parent, "RedditGallery");
        if (Directory.Exists(legacy))
        {
            if (Directory.Exists(current))
                throw new InvalidOperationException(
                    "Найдены две папки данных. Выберите нужную через --data-dir; автоматическое объединение отключено."
                );
            Directory.Move(legacy, current);
        }
        return current;
    }
}
