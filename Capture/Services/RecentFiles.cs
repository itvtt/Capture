using System.IO;
using System.Text.Json;

namespace Capture.Services;

/// <summary>
/// 저장한 캡처 목록 (큰 창의 "최근"): %LOCALAPPDATA%\Capture\recent.json. 자동 저장 · 편집 창의 저장 · 녹화 저장 때 더하고,
/// 지운 파일이나 없는 파일은 보여 주지 않음. 저장하지 않은 캡처는 들어가지 않음
/// </summary>
public static class RecentFiles
{
    private const int Max = 60;
    private static string FilePath => Path.Combine(AppSettings.DataDirectory, "recent.json");

    /// <summary>새것부터, 지금 있는 파일만</summary>
    public static List<string> All() => [.. Read().Where(File.Exists)];

    public static void Add(string path)
    {
        var list = Read();
        list.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        Write([.. list.Where(File.Exists).Take(Max)]);
    }

    public static void Remove(string path)
    {
        var list = Read();
        if (list.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)) > 0) Write(list);
    }

    private static List<string> Read()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } list) return list;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    private static void Write(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
