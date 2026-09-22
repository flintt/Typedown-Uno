using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Where an inserted image is stored and how it is written into the document (ported from Typedown's image
/// settings): the target folder is a template such as <c>./${filename}.assets</c> relative to the document,
/// and the inserted link is relative to the document whenever that is possible.
/// </summary>
public static class ImagePaths
{
    /// <summary>Expands ${filename} ${filedir} ${year} ${month} ${day} in a folder template.</summary>
    public static string ExpandTemplate(string template, string? documentPath)
    {
        var now = DateTime.Now;
        var fileName = documentPath == null ? Loc.Get("Untitled") : Path.GetFileNameWithoutExtension(documentPath);
        var fileDir = documentPath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(documentPath)!;
        return template
            .Replace("${filename}", fileName)
            .Replace("${filedir}", fileDir)
            .Replace("${year}", now.ToString("yyyy"))
            .Replace("${month}", now.ToString("MM"))
            .Replace("${day}", now.ToString("dd"));
    }

    /// <summary>Absolute target folder for a document's images; falls back to the document folder.</summary>
    public static string ResolveFolder(string template, string? documentPath)
    {
        var baseDir = documentPath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(documentPath)!;
        var expanded = ExpandTemplate(string.IsNullOrWhiteSpace(template) ? "." : template.Trim(), documentPath);
        var folder = Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(baseDir, expanded));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A file name in <paramref name="folder"/> that does not exist yet.</summary>
    public static string UniqueFile(string folder, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        for (var i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{name}-{i}{ext}");
        return candidate;
    }

    /// <summary>The link written into the document: relative to the document when asked for and possible.</summary>
    public static string ToLink(string imagePath, string? documentPath, bool preferRelative)
    {
        if (preferRelative && documentPath != null)
        {
            try
            {
                var relative = Path.GetRelativePath(Path.GetDirectoryName(documentPath)!, imagePath).Replace('\\', '/');
                if (!relative.StartsWith("..", StringComparison.Ordinal)) return "./" + relative;
                return relative;
            }
            catch
            {
            }
        }
        return imagePath.Replace('\\', '/');
    }

    /// <summary>Copies an image next to the document (or keeps the original path) and returns the link to insert.</summary>
    public static string PlaceImage(string sourcePath, string? documentPath, AppSettings settings)
    {
        if (settings.ImageAction == ImageInsertAction.KeepPath || documentPath == null)
            return ToLink(sourcePath, documentPath, settings.PreferRelativeImagePaths);
        var folder = ResolveFolder(settings.ImageCopyPath, documentPath);
        var target = UniqueFile(folder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: false);
        return ToLink(target, documentPath, settings.PreferRelativeImagePaths);
    }

    /// <summary>Saves pasted image bytes and returns the link to insert.</summary>
    public static string SaveImageBytes(byte[] bytes, string extension, string? documentPath, AppSettings settings)
    {
        var folder = documentPath == null || settings.ImageAction == ImageInsertAction.KeepPath
            ? Path.Combine(CursorMemory.DataFolder, "images")
            : ResolveFolder(settings.ImageCopyPath, documentPath);
        Directory.CreateDirectory(folder);
        var target = UniqueFile(folder, $"image-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
        File.WriteAllBytes(target, bytes);
        return ToLink(target, documentPath, settings.PreferRelativeImagePaths);
    }

    /// <summary>Decodes a data: URL (as produced by the paste handler in uno-bridge.js).</summary>
    public static (byte[] bytes, string extension)? DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:", StringComparison.Ordinal) || comma < 0) return null;
        var header = dataUrl[5..comma];
        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
        var mime = header.Split(';')[0];
        var extension = mime switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            _ => ".png",
        };
        try { return (Convert.FromBase64String(dataUrl[(comma + 1)..]), extension); }
        catch { return null; }
    }

    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg";

    /// <summary>Percent-encodes the characters that would break a Markdown link.</summary>
    public static string EncodeLink(string link)
    {
        var builder = new StringBuilder();
        foreach (var c in link)
            builder.Append(c switch { ' ' => "%20", '(' => "%28", ')' => "%29", _ => c.ToString() });
        return builder.ToString();
    }
}
