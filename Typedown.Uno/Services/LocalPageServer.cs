using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Serves a page the app made (the theme designer) to the reader's browser at http://127.0.0.1:port/token/name. A
/// file:// address was refused by browsers packaged as a Snap or a Flatpak (Ubuntu's own Firefox and Chromium): they
/// may not read hidden folders such as ~/.local/share, where the page is written. The address is the loopback one,
/// so nothing outside the machine reaches it, and its path carries a random token, so another account on the machine
/// cannot guess it. One listener for the app's lifetime; each page put here replaces the one before under its name.
/// </summary>
public static class LocalPageServer
{
    private static readonly object gate = new();
    private static HttpListener? listener;
    private static string prefix = "";
    private static readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static readonly Dictionary<string, byte[]> pages = new();

    /// <summary>The page's address, or null when no listener could be started (the caller then uses the file).</summary>
    public static string? Serve(string name, string html)
    {
        lock (gate)
        {
            if (listener == null && !Start()) return null;
            pages[name] = Encoding.UTF8.GetBytes(html);
            return $"{prefix}{token}/{Uri.EscapeDataString(name)}";
        }
    }

    private static bool Start()
    {
        try
        {
            // A free port, picked by the system, then listened on.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            prefix = $"http://127.0.0.1:{port}/";
            var http = new HttpListener();
            http.Prefixes.Add(prefix);
            http.Start();
            listener = http;
            _ = Task.Run(() => LoopAsync(http));
            Log.Write($"local pages at {prefix} (loopback only)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"local pages could not be served: {ex.Message}");
            listener = null;
            return false;
        }
    }

    private static async Task LoopAsync(HttpListener http)
    {
        while (http.IsListening)
        {
            HttpListenerContext context;
            try { context = await http.GetContextAsync(); }
            catch { return; }
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                byte[]? body = null;
                var expected = "/" + token + "/";
                if (path.StartsWith(expected, StringComparison.Ordinal))
                    lock (gate) pages.TryGetValue(Uri.UnescapeDataString(path[expected.Length..]), out body);
                if (body == null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"local page request failed: {ex.Message}");
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }
    }
}
