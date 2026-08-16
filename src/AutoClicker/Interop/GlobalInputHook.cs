using AutoClicker.Input;

namespace AutoClicker.Interop;

public sealed class InputHookEventArgs : EventArgs
{
    public InputHookEventArgs(InputKey key, bool isDown)
    {
        Key = key;
        IsDown = isDown;
    }

    public InputKey Key { get; }

    public bool IsDown { get; }

    /// <summary>Set to true to swallow the event so no other application receives it.</summary>
    public bool Suppress { get; set; }
}

/// <summary>
/// Installs low-level keyboard and mouse hooks on a dedicated thread with its own message
/// pump, so UI work on the main thread can never stall input delivery (Windows silently
/// drops hooks that take too long to answer).
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly LowLevelHookProc _keyboardProc;
    private readonly LowLevelHookProc _mouseProc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile bool _disposed;

    public GlobalInputHook()
    {
        // Held in fields so the GC never collects the delegates while Windows holds them.
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>Raised on the hook thread. Handlers must be fast and must not block.</summary>
    public event EventHandler<InputHookEventArgs>? Input;

    public void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "AutoClicker.InputHook",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    private void ThreadMain()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var module = NativeMethods.GetModuleHandle(null);

        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == InputSender.Signature)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var message = (int)wParam;
        bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (!isDown && !isUp) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        return Raise(InputKey.FromKeyboard((int)data.vkCode), isDown, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var message = (int)wParam;
        bool isDown;
        MouseCode button;

        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN: button = MouseCode.Left; isDown = true; break;
            case NativeMethods.WM_LBUTTONUP: button = MouseCode.Left; isDown = false; break;
            case NativeMethods.WM_RBUTTONDOWN: button = MouseCode.Right; isDown = true; break;
            case NativeMethods.WM_RBUTTONUP: button = MouseCode.Right; isDown = false; break;
            case NativeMethods.WM_MBUTTONDOWN: button = MouseCode.Middle; isDown = true; break;
            case NativeMethods.WM_MBUTTONUP: button = MouseCode.Middle; isDown = false; break;
            case NativeMethods.WM_XBUTTONDOWN:
            case NativeMethods.WM_XBUTTONUP:
            {
                var xdata = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (xdata.dwExtraInfo == InputSender.Signature)
                    return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                button = (xdata.mouseData >> 16) == NativeMethods.XBUTTON1 ? MouseCode.X1 : MouseCode.X2;
                isDown = message == NativeMethods.WM_XBUTTONDOWN;
                return Raise(InputKey.FromMouse(button), isDown, nCode, wParam, lParam);
            }
            default:
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == InputSender.Signature)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        return Raise(InputKey.FromMouse(button), isDown, nCode, wParam, lParam);
    }

    private IntPtr Raise(InputKey key, bool isDown, int nCode, IntPtr wParam, IntPtr lParam)
    {
        var handler = Input;
        if (handler is not null)
        {
            var args = new InputHookEventArgs(key, isDown);
            try
            {
                handler(this, args);
            }
            catch
            {
                // A throwing handler must never take the hook down with it.
            }

            if (args.Suppress) return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_thread is not null && _threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }

        _ready.Dispose();
    }
}
