using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Interop;
using Nexus.Services;

namespace Nexus.Input;

[Flags]
public enum Pad : ushort
{
    None = 0,
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LB = 0x0100, RB = 0x0200, Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>
/// Reads Xbox controllers through XInput, including the Guide button, which only the hidden
/// XInputGetStateEx (xinput1_4.dll ordinal 100) reports. Runs on its own thread at ~125 Hz while a
/// controller is connected and checks for new controllers once a second otherwise.
///
/// Events fire on that thread; subscribers marshal to the UI themselves.
/// </summary>
public sealed class GamepadService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
        public uint Reserved; // the Ex version writes one extra field on some systems
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputBattery { public byte Type, Level; }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint XInputGetStateEx(uint user, out XInputState state);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint user, byte devType, out XInputBattery info);

    const int HoldMs = 700;          // Guide held this long = "hold" gesture instead of a press
    const int RepeatDelayMs = 420, RepeatEveryMs = 130;
    const short StickNav = 19000;    // stick past ~58% counts as a d-pad direction for navigation
    const Pad NavButtons = Pad.Up | Pad.Down | Pad.Left | Pad.Right | Pad.A | Pad.B | Pad.X | Pad.Y |
                           Pad.LB | Pad.RB | Pad.Start | Pad.Back | Pad.LeftStick | Pad.RightStick;
    const Pad Repeating = Pad.Up | Pad.Down | Pad.Left | Pad.Right;

    readonly Thread _thread;
    volatile bool _running = true;
    readonly bool[] _connected = new bool[4];
    readonly long[] _nextProbe = new long[4];
    readonly Pad[] _prev = new Pad[4];
    readonly long[] _guideDownAt = new long[4];
    readonly bool[] _guideConsumed = new bool[4];
    readonly long[] _repeatAt = new long[4];
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Guide pressed and released quickly (no other button touched meanwhile).</summary>
    public event Action<int, long>? GuidePressed;
    /// <summary>Guide held down for <see cref="HoldMs"/>.</summary>
    public event Action<int>? GuideHeld;
    /// <summary>A navigation button went down (or auto-repeated) while <see cref="Navigating"/> is on.</summary>
    public event Action<Pad>? NavButton;
    public event Action<int, bool>? ConnectionChanged;

    /// <summary>On while the overlay is open: buttons are reported as <see cref="NavButton"/>.</summary>
    public bool Navigating { get; set; }
    /// <summary>Controller drives the mouse while the overlay is closed.</summary>
    public bool CursorMode { get; set; }
    public int LastActiveSlot { get; private set; } = -1;
    public bool AnyConnected => _connected.Any(c => c);
    public int FirstConnectedSlot => Array.IndexOf(_connected, true);
    public bool IsConnected(int slot) => slot is >= 0 and < 4 && _connected[slot];
    public long Now => _clock.ElapsedMilliseconds;

    public GamepadService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Nexus gamepad", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    [StructLayout(LayoutKind.Sequential)] struct XInputVibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint user, ref XInputVibration v);

    [StructLayout(LayoutKind.Sequential)]
    struct XInputCapabilitiesEx
    {
        public byte Type, SubType;
        public ushort Flags, Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
        public ushort LeftMotor, RightMotor;
        public ushort VendorId, ProductId, ProductVersion, Reserved1;
        public uint Reserved2;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    static extern uint XInputGetCapabilitiesEx(uint reserved, uint user, uint flags, out XInputCapabilitiesEx caps);

    /// <summary>The USB product id of the controller in a slot (0 if empty or unknown).</summary>
    public static ushort ProductId(int slot)
    {
        try { return XInputGetCapabilitiesEx(1, (uint)slot, 0, out var c) == 0 ? c.ProductId : (ushort)0; }
        catch { return 0; }
    }

    /// <summary>ViGEm's virtual pads present as a wired Xbox 360 controller (VID 045E, PID 028E).</summary>
    public const ushort VirtualPadProductId = 0x028E;

    /// <summary>The XInput slot of the input bridge's virtual pad (-1 for none); it is not read.</summary>
    public volatile int IgnoredSlot = -1;

    /// <summary>A raw controller reading for the input bridge.</summary>
    public readonly record struct RawState(uint Packet, ushort Buttons, byte LeftTrigger, byte RightTrigger, short LX, short LY, short RX, short RY);

    /// <summary>Reads a slot directly (any thread), including the Guide button. False if nothing is there.</summary>
    public static bool TryRead(int slot, out RawState state)
    {
        state = default;
        try
        {
            if (XInputGetStateEx((uint)slot, out var s) != 0) return false;
            state = new RawState(s.PacketNumber, s.Buttons, s.LeftTrigger, s.RightTrigger, s.LX, s.LY, s.RX, s.RY);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sets a controller's motors (0-65535), e.g. rumble a game sent to the bridge's virtual pad.</summary>
    public static void Vibrate(int slot, ushort left, ushort right)
    {
        var v = new XInputVibration { Left = left, Right = right };
        try { XInputSetState((uint)slot, ref v); } catch { }
    }

    /// <summary>Buzzes a controller for a moment (the Controllers page's "Test").</summary>
    public async Task RumbleAsync(int slot, int ms = 450)
    {
        if (!IsConnected(slot)) return;
        var on = new XInputVibration { Left = 40000, Right = 40000 };
        var off = new XInputVibration();
        XInputSetState((uint)slot, ref on);
        await Task.Delay(ms);
        XInputSetState((uint)slot, ref off);
    }

    /// <summary>Battery of a controller: (wired, level 0-3), or null if unknown/disconnected.</summary>
    public (bool wired, int level)? Battery(int slot)
    {
        if (slot < 0 || slot > 3 || !_connected[slot]) return null;
        try
        {
            if (XInputGetBatteryInformation((uint)slot, 0, out var b) != 0 || b.Type == 0 || b.Type == 0xFF) return null;
            return (b.Type == 1, b.Level);
        }
        catch { return null; }
    }

    void Loop()
    {
        Log.Info("gamepad thread started");
        while (_running)
        {
            long now = _clock.ElapsedMilliseconds;
            for (int slot = 0; slot < 4; slot++)
            {
                if (slot == IgnoredSlot)
                {
                    // The input bridge's virtual pad: Nexus must never read its own output.
                    if (_connected[slot]) { _connected[slot] = false; _prev[slot] = Pad.None; }
                    continue;
                }
                if (!_connected[slot] && now < _nextProbe[slot]) continue;
                uint r;
                XInputState s;
                try { r = XInputGetStateEx((uint)slot, out s); }
                catch (Exception ex) { Log.Error("XInput unavailable", ex); _running = false; return; }

                if (r != 0)
                {
                    if (_connected[slot]) { _connected[slot] = false; ReleaseMouse(); ConnectionChanged?.Invoke(slot, false); }
                    _nextProbe[slot] = now + 1000; // empty slots are slow to query; check once a second
                    _prev[slot] = Pad.None;
                    continue;
                }
                if (!_connected[slot]) { _connected[slot] = true; ConnectionChanged?.Invoke(slot, true); Log.Info($"controller {slot + 1} connected"); }
                // A handler that throws must not end this thread (and with it the Guide button).
                try { Process(slot, s, now); }
                catch (Exception ex) { Log.Error("gamepad handler", ex); _prev[slot] = (Pad)s.Buttons; }
            }
            // 125 Hz in menus (or driving the cursor), 60 Hz otherwise: plenty for the Guide button,
            // and with no controller connected it only checks four times a second.
            Thread.Sleep(!AnyConnected ? 250 : Navigating || CursorMode ? 8 : 16);
        }
    }

    void Process(int slot, XInputState s, long now)
    {
        var raw = (Pad)s.Buttons;
        var pad = raw;
        if (Navigating) // left stick doubles as a d-pad in menus
        {
            if (s.LY > StickNav) pad |= Pad.Up; else if (s.LY < -StickNav) pad |= Pad.Down;
            if (s.LX > StickNav) pad |= Pad.Right; else if (s.LX < -StickNav) pad |= Pad.Left;
        }
        var prev = _prev[slot];
        var down = pad & ~prev;
        if (down != Pad.None) LastActiveSlot = slot;

        // Guide: press / hold, cancelled if it's used as part of a chord.
        if ((down & Pad.Guide) != 0) { _guideDownAt[slot] = now; _guideConsumed[slot] = false; }
        if ((pad & Pad.Guide) != 0)
        {
            if ((down & ~Pad.Guide) != Pad.None) _guideConsumed[slot] = true;
            if (!_guideConsumed[slot] && now - _guideDownAt[slot] >= HoldMs)
            {
                _guideConsumed[slot] = true;
                GuideHeld?.Invoke(slot);
            }
        }
        else if ((prev & Pad.Guide) != 0 && !_guideConsumed[slot])
        {
            GuidePressed?.Invoke(slot, _clock.ElapsedMilliseconds);
        }

        if (Navigating && (pad & Pad.Guide) == 0)
        {
            foreach (Pad b in Enum.GetValues<Pad>())
            {
                if (b == Pad.None || (NavButtons & b) == 0) continue;
                if ((down & b) != 0)
                {
                    NavButton?.Invoke(b);
                    if ((Repeating & b) != 0) _repeatAt[slot] = now + RepeatDelayMs;
                }
            }
            var held = pad & prev & Repeating;
            if (held != Pad.None && now >= _repeatAt[slot])
            {
                foreach (Pad b in new[] { Pad.Up, Pad.Down, Pad.Left, Pad.Right })
                    if ((held & b) != 0) NavButton?.Invoke(b);
                _repeatAt[slot] = now + RepeatEveryMs;
            }
        }
        else if (CursorMode)
        {
            Cursor(s, raw, prev);
        }
        if (Navigating || !CursorMode) ReleaseMouse();
        _prev[slot] = pad;
    }

    // Gamepad cursor: left stick moves, A left click, B right click, right stick scrolls.
    void Cursor(XInputState s, Pad raw, Pad prev)
    {
        static int Curve(short v)
        {
            const int dead = 7500;
            int a = Math.Abs((int)v);
            if (a < dead) return 0;
            double t = (a - dead) / (32767.0 - dead);
            return (int)(Math.Sign(v) * (t * t * 22 + t * 2));
        }
        int dx = Curve(s.LX), dy = -Curve(s.LY);
        if (dx != 0 || dy != 0) Native.mouse_event(Native.MOUSEEVENTF_MOVE, dx, dy, 0, UIntPtr.Zero);

        var down = raw & ~prev;
        var up = prev & ~raw;
        if ((down & Pad.A) != 0) { Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); _leftDown = true; }
        if ((up & Pad.A) != 0 && _leftDown) { Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero); _leftDown = false; }
        if ((down & Pad.B) != 0) { Native.mouse_event(Native.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero); _rightDown = true; }
        if ((up & Pad.B) != 0 && _rightDown) { Native.mouse_event(Native.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero); _rightDown = false; }
        if (Math.Abs((int)s.RY) > 9000) Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, s.RY / 600, UIntPtr.Zero);
    }

    bool _leftDown, _rightDown;

    /// <summary>Lets go of any mouse button the cursor mode is holding (overlay opened, pad gone).</summary>
    void ReleaseMouse()
    {
        if (_leftDown) { Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero); _leftDown = false; }
        if (_rightDown) { Native.mouse_event(Native.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero); _rightDown = false; }
    }

    public void Dispose() { _running = false; ReleaseMouse(); }
}
