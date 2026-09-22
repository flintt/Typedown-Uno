using System.Text.Json;

namespace Typedown.Uno.Services;

/// <summary>Documents open when the window closed, restored as tabs on the next start (setting RestoreSession).</summary>
public static class SessionMemory
{
    public class Session
    {
        public List<string> Files { get; set; } = new();
        public int ActiveIndex { get; set; }
        public string? Folder { get; set; }
        public DateTime Time { get; set; }
    }

    private static string StorePath => Path.Combine(CursorMemory.DataFolder, "session.json");

    public static Session? Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(StorePath));
            if (session?.Files == null) return null;
            var active = session.ActiveIndex >= 0 && session.ActiveIndex < session.Files.Count ? session.Files[session.ActiveIndex] : null;
            session.Files = session.Files.Where(File.Exists).ToList();
            session.ActiveIndex = Math.Max(0, active == null ? 0 : session.Files.IndexOf(active));
            return session;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(IEnumerable<string?> files, int activeIndex, string? folder)
    {
        try
        {
            var list = files.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).ToList();
            File.WriteAllText(StorePath, JsonSerializer.Serialize(new Session { Files = list, ActiveIndex = Math.Max(0, activeIndex), Folder = folder, Time = DateTime.UtcNow }));
        }
        catch
        {
        }
    }
}
