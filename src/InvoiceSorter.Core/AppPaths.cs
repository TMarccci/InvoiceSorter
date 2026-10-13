namespace InvoiceSorter.Core;

public static class AppPaths
{
    /// <summary>Settings and styles (roaming, so they follow the user).</summary>
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InvoiceSorter");

    /// <summary>Logs, work folders and the Surya environment (machine-local). The server uses its ProgramData folder.</summary>
    public static string LocalDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InvoiceSorter");

    public static string LogDir => Path.Combine(LocalDir, "Logs");
    public static string TempRoot => Path.Combine(LocalDir, "Work");
    public static string StylesFile => Path.Combine(DataDir, "styles.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string CreateJobDir()
    {
        var dir = Path.Combine(TempRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Removes work folders of earlier runs (page images) older than the given age.</summary>
    public static void CleanupOldJobs(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                if (DateTime.Now - Directory.GetCreationTime(dir) > maxAge)
                {
                    try { Directory.Delete(dir, true); } catch { /* in use or locked */ }
                }
            }
        }
        catch { /* best effort */ }
    }
}
