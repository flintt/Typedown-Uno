using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace Typedown.Uno.Services;

/// <summary>
/// The editor ↔ host message protocol of Typedown (see Dev/Typedown.Core/Services/Transport.cs upstream):
/// <list type="bullet">
/// <item><c>{type:"invoke", id, name, args}</c> — the editor calls a host function and awaits <c>{name:id, args:{code,data}}</c>.</item>
/// <item><c>{type:"diffmsg", name, args, diff, start, end}</c> — state reports; after the first full payload only the
/// changed slice of the JSON string is sent, so the previous payload per name is kept to rebuild it.</item>
/// <item><c>{type:"message", name, args}</c> — plain events.</item>
/// </list>
/// Host → editor messages are <c>{name, args}</c> JSON strings handed to <c>window.__unoDeliver</c> (uno-bridge.js).
/// </summary>
public sealed class EditorTransport
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Dictionary<string, string> previous = new();
    private readonly Dictionary<string, Func<JsonNode?, Task<object?>>> handlers = new(StringComparer.Ordinal);
    private readonly Func<string, Task> post;

    public event Action<string, JsonNode?>? MessageReceived;

    public EditorTransport(Func<string, Task> postToEditor)
    {
        post = postToEditor;
    }

    /// <summary>Registers a host function the editor may invoke; the result is serialized camelCase.</summary>
    public void Handle(string name, Func<JsonNode?, Task<object?>> handler) => handlers[name] = handler;

    public void Handle(string name, Func<JsonNode?, object?> handler) => handlers[name] = args => Task.FromResult(handler(args));

    /// <summary>Sends a named message with a payload object to the editor.</summary>
    public Task PostMessage(string name, object? args) => post(JsonSerializer.Serialize(new { name, args }, JsonOptions));

    public async void OnWebMessage(string raw)
    {
        try
        {
            var msg = JsonNode.Parse(raw)?.AsObject();
            if (msg == null) return;
            var type = msg["type"]?.GetValue<string>();
            var name = msg["name"]?.GetValue<string>() ?? "";
            switch (type)
            {
                case "invoke":
                {
                    var id = msg["id"]?.GetValue<string>() ?? "";
                    try
                    {
                        var result = handlers.TryGetValue(name, out var handler)
                            ? await handler(msg["args"])
                            : throw new InvalidOperationException($"no handler for '{name}'");
                        await post(JsonSerializer.Serialize(new { name = id, args = new { code = 0, data = result } }, JsonOptions));
                    }
                    catch (Exception ex)
                    {
                        await post(JsonSerializer.Serialize(new { name = id, args = new { code = 1, msg = ex.Message } }, JsonOptions));
                    }
                    break;
                }
                case "diffmsg":
                {
                    var diff = msg["diff"]?.GetValue<bool>() ?? false;
                    var args = msg["args"]?.GetValue<string>() ?? "";
                    if (diff && previous.TryGetValue(name, out var prev))
                    {
                        var start = msg["start"]?.GetValue<int>() ?? 0;
                        var end = msg["end"]?.GetValue<int>() ?? prev.Length;
                        previous[name] = prev.Substring(0, start) + args + prev.Substring(end);
                    }
                    else
                    {
                        previous[name] = args;
                    }
                    MessageReceived?.Invoke(name, JsonNode.Parse(previous[name]));
                    break;
                }
                case "message":
                    MessageReceived?.Invoke(name, msg["args"]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"EditorTransport: bad message: {ex.Message}");
        }
    }

    /// <summary>Extracts the raw string the editor posted, whichever way the platform surfaces it.</summary>
    public static string? GetRawMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var s = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch
        {
        }
        var json = e.WebMessageAsJson;
        if (string.IsNullOrEmpty(json)) return null;
        // A string posted through the WebKit handler may arrive JSON-encoded ("\"{...}\"").
        if (json.StartsWith("\"", StringComparison.Ordinal))
        {
            try { return JsonSerializer.Deserialize<string>(json); } catch { }
        }
        return json;
    }
}
