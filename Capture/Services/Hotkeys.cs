using System.Windows.Input;
using System.Windows.Interop;
using Capture.Core;
using static Capture.Core.NativeMethods;
using CaptureMode = Capture.Core.CaptureMode;

namespace Capture.Services;

/// <summary>단축키 하나: Windows 수정 키(MOD_*) + 가상 키. Key 가 0 이면 없음</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint Key)
{
    public bool IsNone => Key == 0;

    /// <summary>Ctrl+Shift+PrtSc · Alt+F1 · 없음</summary>
    public override string ToString()
    {
        if (IsNone) return "없음";
        var parts = new List<string>();
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk) => vk switch
    {
        VK_SNAPSHOT => "PrtSc",
        0x13 => "Pause",
        0x91 => "ScrollLock",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString()
    };

    /// <summary>
    /// 키보드 입력을 단축키로. 수정 키 없이 쓸 수 있는 것은 PrtSc · F1~F24 · Pause · ScrollLock · Insert 뿐
    /// (글자 키 하나를 단축키로 잡으면 글을 쓸 수 없음). 안 되면 이유를 돌려줌
    /// </summary>
    public static HotkeyBinding? FromKey(Key key, ModifierKeys modifiers, out string? problem)
    {
        problem = null;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        bool alone = vk is VK_SNAPSHOT or 0x13 or 0x91 or 0x2D || vk is >= 0x70 and <= 0x87;
        if (vk == 0 || (mods == 0 && !alone))
        {
            problem = "Ctrl · Alt · Shift 중 하나와 함께 누르세요. (PrtSc · F1~F12 · Pause 는 혼자도 됨)";
            return null;
        }
        if (mods == MOD_SHIFT && !alone)
        {
            problem = "Shift 만으로는 글자를 쓸 때와 겹칩니다. Ctrl 이나 Alt 와 함께 누르세요.";
            return null;
        }
        return new HotkeyBinding(mods, vk);
    }
}

/// <summary>
/// 전역 단축키. 기본은 PrtSc 캡처 · Ctrl+PrtSc 스크롤 · Shift+PrtSc GIF · Ctrl+Shift+PrtSc 영상이고 큰 창의 [단축키]에서 바꿈 (설정에 저장).
/// Windows 11 은 "Print Screen 키로 캡처 도구 열기"가 켜져 있으면 PrtSc 를 먼저 씀 → 등록하지 못한 것을 알려 줌
/// </summary>
public sealed class Hotkeys : IDisposable
{
    public static readonly CaptureMode[] Modes = [CaptureMode.Region, CaptureMode.Scroll, CaptureMode.Gif, CaptureMode.Video];

    private readonly AppSettings _settings;
    private readonly HwndSource _window;
    private readonly Dictionary<CaptureMode, HotkeyBinding> _bindings = [];
    private readonly HashSet<CaptureMode> _registered = [];
    private bool _suspended;

    public Hotkeys(AppSettings settings)
    {
        _settings = settings;
        foreach (var mode in Modes)
            _bindings[mode] = settings.Hotkeys?.TryGetValue(mode.ToString(), out var saved) == true ? saved : Default(mode);
        // 메시지만 받는 창
        _window = new HwndSource(new HwndSourceParameters("Capture hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
    }

    public event Action<CaptureMode>? Pressed;

    /// <summary>단축키가 바뀌었거나 등록 결과가 달라짐 (화면의 단축키 글자를 다시 쓰게)</summary>
    public event Action? Changed;

    public static HotkeyBinding Default(CaptureMode mode) => mode switch
    {
        CaptureMode.Scroll => new(MOD_CONTROL, VK_SNAPSHOT),
        CaptureMode.Gif => new(MOD_SHIFT, VK_SNAPSHOT),
        CaptureMode.Video => new(MOD_CONTROL | MOD_SHIFT, VK_SNAPSHOT),
        _ => new(0, VK_SNAPSHOT)
    };

    public static string Name(CaptureMode mode) => mode switch
    {
        CaptureMode.Scroll => "스크롤",
        CaptureMode.Gif => "GIF",
        CaptureMode.Video => "영상",
        _ => "영역"
    };

    public HotkeyBinding Get(CaptureMode mode) => _bindings[mode];

    public string LabelFor(CaptureMode mode) => _bindings[mode].ToString();

    /// <summary>정했지만 등록하지 못한 것 (다른 프로그램이 쓰는 중)</summary>
    public IReadOnlyList<CaptureMode> Failed => [.. Modes.Where(m => !_bindings[m].IsNone && !_registered.Contains(m))];

    /// <summary>아직 등록하지 못한 것만 다시 시도 (다른 프로그램이 놓았을 수 있음)</summary>
    public void Register()
    {
        if (_suspended) return;
        int before = _registered.Count;
        foreach (var mode in Modes)
        {
            var binding = _bindings[mode];
            if (_registered.Contains(mode) || binding.IsNone) continue;
            if (RegisterHotKey(_window.Handle, Id(mode), binding.Modifiers | MOD_NOREPEAT, binding.Key)) _registered.Add(mode);
        }
        if (_registered.Count != before) Changed?.Invoke();
    }

    /// <summary>새 단축키를 누르는 동안: 모두 놓아 그 키가 이 앱의 단축키로 가로채이지 않게</summary>
    public void Suspend()
    {
        _suspended = true;
        foreach (var mode in Modes) Unregister(mode);
    }

    public void Resume()
    {
        _suspended = false;
        Register();
        Changed?.Invoke();
    }

    /// <summary>바꾸고 저장. 멈춰 둔 동안이면 Resume 때 등록</summary>
    public void Set(CaptureMode mode, HotkeyBinding binding)
    {
        Unregister(mode);
        _bindings[mode] = binding;
        _settings.Hotkeys = Modes.ToDictionary(m => m.ToString(), m => _bindings[m]);
        _settings.Save();
        Register();
        Changed?.Invoke();
    }

    public void ResetDefaults()
    {
        foreach (var mode in Modes) Unregister(mode);
        foreach (var mode in Modes) _bindings[mode] = Default(mode);
        _settings.Hotkeys = null;
        _settings.Save();
        Register();
        Changed?.Invoke();
    }

    private void Unregister(CaptureMode mode)
    {
        if (_registered.Remove(mode)) UnregisterHotKey(_window.Handle, Id(mode));
    }

    private static int Id(CaptureMode mode) => Array.IndexOf(Modes, mode) + 1;

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id >= 1 && id <= Modes.Length)
            {
                handled = true;
                Pressed?.Invoke(Modes[id - 1]);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var mode in Modes) Unregister(mode);
        _window.Dispose();
    }
}
