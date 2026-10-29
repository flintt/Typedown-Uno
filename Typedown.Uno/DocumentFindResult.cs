namespace Typedown.Uno;

/// <summary>A preview of one match in the active document's find bar.</summary>
public sealed record DocumentFindResult(int Index, int Version, string Preview)
{
    public string Number => (Index + 1).ToString();
}
