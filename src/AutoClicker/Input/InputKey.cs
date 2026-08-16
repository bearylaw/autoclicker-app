using System.Text.Json.Serialization;
using System.Windows.Input;

namespace AutoClicker.Input;

public enum InputKind
{
    None = 0,
    Keyboard = 1,
    Mouse = 2
}

public enum MouseCode
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4
}

/// <summary>
/// An immutable reference to a single physical input: either a keyboard virtual key
/// or a mouse button. Used both for hotkeys (what the user presses) and for actions
/// (what the app sends).
/// </summary>
public sealed class InputKey : IEquatable<InputKey>
{
    public static readonly InputKey None = new(InputKind.None, 0);
    public static readonly InputKey LeftClick = new(InputKind.Mouse, (int)MouseCode.Left);

    [JsonConstructor]
    public InputKey(InputKind kind, int code)
    {
        Kind = kind;
        Code = code;
    }

    public InputKind Kind { get; }

    /// <summary>Virtual key code for <see cref="InputKind.Keyboard"/>, <see cref="MouseCode"/> for mouse.</summary>
    public int Code { get; }

    [JsonIgnore]
    public bool IsSet => Kind != InputKind.None;

    public static InputKey FromKeyboard(int virtualKey) => new(InputKind.Keyboard, virtualKey);

    public static InputKey FromMouse(MouseCode button) => new(InputKind.Mouse, (int)button);

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        InputKind.Mouse => MouseName((MouseCode)Code),
        InputKind.Keyboard => KeyName(Code),
        _ => "Not set"
    };

    private static string MouseName(MouseCode button) => button switch
    {
        MouseCode.Left => "Left Click",
        MouseCode.Right => "Right Click",
        MouseCode.Middle => "Middle Click",
        MouseCode.X1 => "Mouse 4",
        MouseCode.X2 => "Mouse 5",
        _ => "Mouse"
    };

    private static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left Arrow",
        0x26 => "Up Arrow",
        0x27 => "Right Arrow",
        0x28 => "Down Arrow",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5B => "Left Win",
        0x5C => "Right Win",
        0x5D => "Menu",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        0xBA => ";",
        0xBB => "+",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),               // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),               // A-Z
        >= 0x60 and <= 0x69 => "Numpad " + (vk - 0x60),
        0x6A => "Numpad *",
        0x6B => "Numpad +",
        0x6D => "Numpad -",
        0x6E => "Numpad .",
        0x6F => "Numpad /",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),                   // F1-F24
        _ => FallbackName(vk)
    };

    private static string FallbackName(int vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            if (key != Key.None) return key.ToString();
        }
        catch (ArgumentException)
        {
            // Unmapped virtual key - fall through to the raw code.
        }

        return $"Key {vk}";
    }

    public bool Equals(InputKey? other) => other is not null && other.Kind == Kind && other.Code == Code;

    public override bool Equals(object? obj) => Equals(obj as InputKey);

    public override int GetHashCode() => HashCode.Combine(Kind, Code);

    public override string ToString() => DisplayName;
}
