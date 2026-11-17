using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Capture.Services;

/// <summary>설정: %LOCALAPPDATA%\Capture\settings.json (설치 폴더 밖이라 업데이트해도 남음)</summary>
public sealed class AppSettings
{
    /// <summary>CAPTURE_DATA_DIR 이 있으면 그 폴더 (확인 · 시험용)</summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("CAPTURE_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capture");
    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>저장 창이 처음 여는 곳 (폴더를 아직 고르지 않았을 때): 사진</summary>
    public static string PicturesFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>자동 저장: 켜면 캡처 · 녹화를 SaveFolder 에 바로 저장. 끄면(처음 값) 복사만 하고 사용자가 저장할 때 위치를 고름</summary>
    public bool AutoSave { get; set; }

    /// <summary>사용자가 고른 저장 폴더 (자동 저장 · 저장 창의 처음 위치). 고르지 않았으면 빈 값</summary>
    public string SaveFolder { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFolder => !string.IsNullOrWhiteSpace(SaveFolder);

    /// <summary>저장 창이 처음 여는 폴더</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DialogFolder => HasFolder && Directory.Exists(SaveFolder) ? SaveFolder : PicturesFolder;

    /// <summary>처음 실행할 때 "Windows 시작할 때 실행"을 켰는지 (한 번만)</summary>
    public bool StartupConfigured { get; set; }

    /// <summary>처음 설정(단축키 · 저장 방식)을 보였는지 (한 번만)</summary>
    public bool SetupDone { get; set; }

    /// <summary>정한 단축키 (모드 이름 → 단축키). 없으면 모두 없음</summary>
    public Dictionary<string, HotkeyBinding>? Hotkeys { get; set; }

    /// <summary>창을 닫으면 트레이로 간다는 안내를 보였는지</summary>
    public bool TrayHintShown { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) is { } settings)
            {
                if (settings.AutoSave && !settings.HasFolder) settings.AutoSave = false;  // 폴더 없이 자동 저장은 없음
                return settings;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string temp = FilePath + ".tmp";
            // 한글 경로를 그대로 (이스케이프하지 않고) 써서 열어 봐도 읽히게
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>새 파일 이름: Capture_20261117_143012</summary>
    public static string NewName() => "Capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    /// <summary>저장 폴더의 새 파일 (같은 이름이 있으면 _2, _3 …)</summary>
    public string NewFilePath(string extension) => UniquePath(SaveFolder, extension);

    /// <summary>저장하지 않은 녹화 · 공유용 임시 파일 (%TEMP%\Capture, 시작할 때 이틀 지난 것은 지움)</summary>
    public static string TempFilePath(string extension) => UniquePath(TempFolder, extension);

    public static string TempFolder => Path.Combine(Path.GetTempPath(), "Capture");

    private static string UniquePath(string folder, string extension)
    {
        Directory.CreateDirectory(folder);
        string stem = NewName();
        string path = Path.Combine(folder, stem + "." + extension);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{n}.{extension}");
        return path;
    }

    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempFolder)) return;
            foreach (var file in new DirectoryInfo(TempFolder).EnumerateFiles())
                if (file.LastWriteTime < DateTime.Now.AddDays(-2)) file.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Windows 시작할 때 실행 (HKCU Run, 트레이로만 시작)</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Capture";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string value && value.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\" --tray");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>실행 파일이 옮겨졌으면 (업데이트 · 다시 설치) 등록된 경로를 지금 것으로</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}
