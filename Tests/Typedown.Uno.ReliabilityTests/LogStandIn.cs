namespace Typedown.Uno.Services;

// The app's Log needs the app around it (version, data folder); what the files compiled here write goes to the console.
public static class Log
{
    public static void Write(string message) => System.Console.WriteLine($"  log: {message}");
}
