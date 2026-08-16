using AutoClicker.Input;

namespace AutoClicker.Interop;

/// <summary>
/// Sends synthetic key / mouse events. Every event is stamped with <see cref="Signature"/>
/// in dwExtraInfo so our own global hook can recognise and ignore it - without that the
/// clicker would re-trigger itself.
/// </summary>
internal static class InputSender
{
    /// <summary>'CLCK' - marker written into dwExtraInfo of every event we generate.</summary>
    public static readonly UIntPtr Signature = new(0x434C434B);

    public static void Send(InputKey key, bool down)
    {
        switch (key.Kind)
        {
            case InputKind.Mouse:
                SendMouse((MouseCode)key.Code, down);
                break;
            case InputKind.Keyboard:
                SendKeyboard((ushort)key.Code, down);
                break;
        }
    }

    private static void SendMouse(MouseCode button, bool down)
    {
        uint flags;
        uint data = 0;

        switch (button)
        {
            case MouseCode.Left:
                flags = down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
                break;
            case MouseCode.Right:
                flags = down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
                break;
            case MouseCode.Middle:
                flags = down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
                break;
            case MouseCode.X1:
            case MouseCode.X2:
                flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                data = button == MouseCode.X1 ? NativeMethods.XBUTTON1 : NativeMethods.XBUTTON2;
                break;
            default:
                return;
        }

        var input = new INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flags,
                    mouseData = data,
                    dwExtraInfo = Signature
                }
            }
        };

        Dispatch(input);
    }

    private static void SendKeyboard(ushort virtualKey, bool down)
    {
        var scan = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);

        // Scan codes keep DirectInput-based games happy; they ignore plain virtual keys.
        uint flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (!down) flags |= NativeMethods.KEYEVENTF_KEYUP;
        if (IsExtendedKey(virtualKey)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = scan == 0 ? virtualKey : (ushort)0,
                    wScan = scan,
                    dwFlags = scan == 0 ? flags & ~NativeMethods.KEYEVENTF_SCANCODE : flags,
                    dwExtraInfo = Signature
                }
            }
        };

        Dispatch(input);
    }

    private static void Dispatch(INPUT input)
    {
        var buffer = new[] { input };
        NativeMethods.SendInput(1, buffer, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,                    // PageUp/PageDown/End/Home
        0x25 or 0x26 or 0x27 or 0x28 => true,                    // Arrows
        0x2D or 0x2E => true,                                    // Insert / Delete
        0x2C => true,                                            // Print Screen
        0x90 => true,                                            // Num Lock
        0xA3 => true,                                            // Right Ctrl
        0xA5 => true,                                            // Right Alt
        0x5B or 0x5C or 0x5D => true,                            // Win keys / Menu
        0x6F => true,                                            // Numpad /
        _ => false
    };
}
