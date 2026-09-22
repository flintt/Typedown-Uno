using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Typedown.Uno.Services;

public sealed class HedgeDocShareResult
{
    public string NoteUrl { get; set; } = "";
    public string? PublishedUrl { get; set; }
    public string ShareUrl => PublishedUrl ?? NoteUrl;
}

/// <summary>
/// HedgeDoc 1.x HTTP API (ported from Typedown): POST /new (302 → note URL), GET /{id}/publish (→ /s/{id}),
/// optional email/password session login. Redirect chains are followed by hand and same-host locations are
/// normalized to the configured scheme (proxies commonly hand out http:// for https sites).
/// </summary>
public static class HedgeDocService
{
    public static string NormalizeServer(string? server)
    {
        server = server?.Trim() ?? "";
        if (server.Length == 0) return "";
        if (!server.Contains("://")) server = "https://" + server;
        return server.TrimEnd('/');
    }

    private static HttpClient CreateClient(CookieContainer cookies)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Typedown");
        return client;
    }

    public static async Task<HedgeDocShareResult> ShareAsync(string server, string markdown, string? email, string? password, bool publish)
    {
        server = NormalizeServer(server);
        if (server.Length == 0) throw new ArgumentException("HedgeDoc server is not configured.");
        var cookies = new CookieContainer();
        using var client = CreateClient(cookies);
        if (!string.IsNullOrWhiteSpace(email))
            await LoginAsync(client, server, email, password);

        using var content = new StringContent(markdown, Encoding.UTF8, "text/markdown");
        using var response = await client.PostAsync($"{server}/new", content);
        var noteUrl = ResolveRedirect(server, response, "create the note");
        var result = new HedgeDocShareResult { NoteUrl = noteUrl };
        if (publish)
        {
            try { result.PublishedUrl = await FollowRedirectsAsync(client, server, $"{noteUrl}/publish", "publish the note"); }
            catch (Exception ex) { Console.Error.WriteLine($"HedgeDoc publish failed: {ex.Message}"); }
        }
        return result;
    }

    public static async Task<string> TestAsync(string server, string? email, string? password)
    {
        server = NormalizeServer(server);
        if (server.Length == 0) throw new ArgumentException("Server address is empty.");
        var cookies = new CookieContainer();
        using var client = CreateClient(cookies);
        using var status = await client.GetAsync($"{server}/status");
        if (status.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"{server}/status returned {(int)status.StatusCode}; this does not look like a HedgeDoc 1.x server.");
        if (string.IsNullOrWhiteSpace(email)) return "HedgeDoc 1.x, anonymous";
        var name = await LoginAsync(client, server, email, password);
        return $"HedgeDoc 1.x, signed in as {name}";
    }

    private static async Task<string> LoginAsync(HttpClient client, string server, string email, string? password)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["password"] = password ?? "" });
        using var login = await client.PostAsync($"{server}/login", form);
        using var me = await client.GetAsync($"{server}/me");
        var json = JsonNode.Parse(await me.Content.ReadAsStringAsync());
        if (json?["status"]?.ToString() != "ok")
            throw new UnauthorizedAccessException("HedgeDoc login failed: check the email address and password (email login must be enabled on the server).");
        return json?["name"]?.ToString() ?? email;
    }

    private static async Task<string> FollowRedirectsAsync(HttpClient client, string server, string url, string action)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            using var response = await client.GetAsync(url);
            var code = (int)response.StatusCode;
            if (code < 300 || code >= 400 || response.Headers.Location == null)
            {
                if (hop == 0) ResolveRedirect(server, response, action);
                return url;
            }
            url = NormalizeLocation(server, response.Headers.Location);
        }
        return url;
    }

    private static string NormalizeLocation(string server, Uri location)
    {
        if (!location.IsAbsoluteUri) return $"{server}/{location.ToString().TrimStart('/')}".TrimEnd('/');
        var serverUri = new Uri(server);
        if (string.Equals(location.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase) && location.Scheme != serverUri.Scheme)
            return $"{serverUri.Scheme}://{serverUri.Authority}{location.PathAndQuery}".TrimEnd('/');
        return location.ToString().TrimEnd('/');
    }

    private static string ResolveRedirect(string server, HttpResponseMessage response, string action)
    {
        var code = (int)response.StatusCode;
        if (code >= 300 && code < 400 && response.Headers.Location != null)
            return NormalizeLocation(server, response.Headers.Location);
        var hint = code switch
        {
            403 => "the server does not allow anonymous notes; sign in with an email account",
            413 => "the document exceeds the server's size limit",
            404 => "endpoint not found; is this a HedgeDoc 1.x server?",
            _ => $"HTTP {code}",
        };
        throw new HttpRequestException($"HedgeDoc could not {action}: {hint}.");
    }
}
