namespace AlchemyStars.Engine;

/// <summary>
/// Writes generated Lua with LF newlines, like the MW Base sources. Builders mix "\n" literals with AppendLine
/// (CRLF on Windows), so the newline style is decided here instead of at every call site.
/// </summary>
internal static class LuaFile
{
    public static void Write(string path, string text) => File.WriteAllText(path, text.ReplaceLineEndings("\n"));

    /// <summary>Adds a section to a script several generators share (fire + foley sounds); creates folder and file.</summary>
    public static void Append(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, text.ReplaceLineEndings("\n"));
    }
}
