using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RdpPoc.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace RdpPoc.HostAgent.Sdk;

internal interface IHostCapturePlatform : IDisposable
{
    IReadOnlyList<CaptureTargetDescriptor> GetTargets();

    RelayFrame CaptureFrame(HostSessionAssignment assignment, long sequenceId);

    void HandlePointerInput(HostSessionAssignment assignment, PointerInputEvent input);

    ModifierKeyState HandleKeyboardInput(
        HostSessionAssignment assignment,
        KeyboardInputEvent input,
        ModifierKeyState currentModifierState);
}

internal readonly record struct ModifierKeyState(bool Alt, bool Control, bool Shift);

internal static class HostCapturePlatformFactory
{
    public static IHostCapturePlatform Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsHostCapturePlatform();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxHostCapturePlatform();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsHostCapturePlatform();
        }

        return new UnsupportedHostCapturePlatform();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsHostCapturePlatform : IHostCapturePlatform
{
    public IReadOnlyList<CaptureTargetDescriptor> GetTargets()
    {
        var targets = new List<CaptureTargetDescriptor>
        {
            new("desktop:primary", "Primary Desktop", CaptureTargetKind.Desktop),
        };

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    continue;
                }

                targets.Add(new CaptureTargetDescriptor(
                    $"window:{process.Id}",
                    $"{process.ProcessName} - {process.MainWindowTitle}",
                    CaptureTargetKind.Window));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return targets
            .DistinctBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.Kind)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RelayFrame CaptureFrame(HostSessionAssignment assignment, long sequenceId)
    {
        using var bitmap = assignment.TargetKind switch
        {
            CaptureTargetKind.Desktop => CaptureDesktopBitmap(),
            CaptureTargetKind.Window => CaptureWindowBitmap(assignment.TargetId),
            _ => throw new InvalidOperationException($"Unsupported target kind '{assignment.TargetKind}'."),
        };

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);

        return new RelayFrame(
            assignment.SessionId,
            sequenceId,
            DateTimeOffset.UtcNow,
            "image/jpeg",
            bitmap.Width,
            bitmap.Height,
            stream.ToArray());
    }

    public void HandlePointerInput(HostSessionAssignment assignment, PointerInputEvent input)
    {
        if (!TryResolveTargetBounds(assignment, out var bounds))
        {
            throw new InvalidOperationException($"Unable to resolve bounds for target '{assignment.TargetId}'.");
        }

        var targetX = bounds.Left + (int)Math.Round(Math.Clamp(input.X, 0d, 1d) * Math.Max(bounds.Width - 1, 0));
        var targetY = bounds.Top + (int)Math.Round(Math.Clamp(input.Y, 0d, 1d) * Math.Max(bounds.Height - 1, 0));

        FocusTarget(assignment);
        _ = SetCursorPos(targetX, targetY);

        if (string.Equals(input.EventType, "wheel", StringComparison.OrdinalIgnoreCase))
        {
            if (input.WheelDeltaY != 0)
            {
                SendMouseInput(0x0800u, checked(-input.WheelDeltaY * 120));
            }

            if (input.WheelDeltaX != 0)
            {
                SendMouseInput(0x1000u, checked(input.WheelDeltaX * 120));
            }

            return;
        }

        var mouseFlags = input.EventType switch
        {
            "down" => GetMouseDownFlag(input.Button),
            "up" => GetMouseUpFlag(input.Button),
            _ => 0u,
        };

        if (mouseFlags != 0)
        {
            SendMouseInput(mouseFlags);
        }
    }

    public ModifierKeyState HandleKeyboardInput(
        HostSessionAssignment assignment,
        KeyboardInputEvent input,
        ModifierKeyState currentModifierState)
    {
        if (!TryMapVirtualKey(input.Code, out var virtualKey))
        {
            if (!TryGetModifierVirtualKey(input.Code, out virtualKey))
            {
                return currentModifierState;
            }
        }

        FocusTarget(assignment);

        if (TryGetModifierVirtualKey(input.Code, out _))
        {
            SendKeyboardInput(input.EventType, virtualKey);
            return new ModifierKeyState(input.Alt, input.Control, input.Shift);
        }

        var desiredModifierState = new ModifierKeyState(input.Alt, input.Control, input.Shift);
        ApplyModifierState(currentModifierState, desiredModifierState);
        SendKeyboardInput(input.EventType, virtualKey);
        return desiredModifierState;
    }

    public void Dispose()
    {
    }

    private static System.Drawing.Bitmap CaptureDesktopBitmap()
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The desktop capture bounds are invalid.");
        }

        var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
        return bitmap;
    }

    private static System.Drawing.Bitmap CaptureWindowBitmap(string targetId)
    {
        var processIdSegment = targetId.Split(':', 2).LastOrDefault();
        if (!int.TryParse(processIdSegment, out var processId))
        {
            throw new InvalidOperationException($"Window target '{targetId}' does not contain a valid process ID.");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Process '{processId}' is no longer running.");
        }

        using var _ = process;
        var handle = process.MainWindowHandle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Process '{processId}' does not currently expose a main window.");
        }

        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException($"Unable to read window bounds for process '{processId}'.");
        }

        var width = Math.Max(rect.Right - rect.Left, 1);
        var height = Math.Max(rect.Bottom - rect.Top, 1);

        var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        return bitmap;
    }

    private static bool TryResolveTargetBounds(HostSessionAssignment assignment, out RawRect bounds)
    {
        if (assignment.TargetKind == CaptureTargetKind.Desktop)
        {
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78);
            var height = GetSystemMetrics(79);
            bounds = new RawRect(left, top, left + width, top + height);
            return width > 0 && height > 0;
        }

        var handle = GetTargetWindowHandle(assignment.TargetId);
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out bounds))
        {
            bounds = default;
            return false;
        }

        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static void FocusTarget(HostSessionAssignment assignment)
    {
        if (assignment.TargetKind != CaptureTargetKind.Window)
        {
            return;
        }

        var handle = GetTargetWindowHandle(assignment.TargetId);
        if (handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(handle);
        }
    }

    private static IntPtr GetTargetWindowHandle(string targetId)
    {
        var processIdSegment = targetId.Split(':', 2).LastOrDefault();
        if (!int.TryParse(processIdSegment, out var processId))
        {
            return IntPtr.Zero;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;
        }
    }

    private static uint GetMouseDownFlag(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => 0x0008,
        "middle" => 0x0020,
        _ => 0x0002,
    };

    private static uint GetMouseUpFlag(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => 0x0010,
        "middle" => 0x0040,
        _ => 0x0004,
    };

    private static void SendMouseInput(uint flags, int mouseData = 0)
    {
        var input = new INPUT
        {
            Type = 0,
            Union = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    MouseData = unchecked((uint)mouseData),
                    DwFlags = flags,
                },
            },
        };

        _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyboardInput(string eventType, ushort virtualKey)
    {
        var input = new INPUT
        {
            Type = 1,
            Union = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    WVk = virtualKey,
                    DwFlags = string.Equals(eventType, "keyup", StringComparison.OrdinalIgnoreCase)
                        ? 0x0002u
                        : 0u,
                },
            },
        };

        _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void ApplyModifierState(ModifierKeyState current, ModifierKeyState desired)
    {
        ApplyModifierTransition(current.Control, desired.Control, 0x11);
        ApplyModifierTransition(current.Shift, desired.Shift, 0x10);
        ApplyModifierTransition(current.Alt, desired.Alt, 0x12);
    }

    private static void ApplyModifierTransition(bool current, bool desired, ushort virtualKey)
    {
        if (current == desired)
        {
            return;
        }

        SendKeyboardInput(desired ? "keydown" : "keyup", virtualKey);
    }

    private static bool TryGetModifierVirtualKey(string code, out ushort virtualKey)
    {
        virtualKey = code switch
        {
            "ShiftLeft" or "ShiftRight" => 0x10,
            "ControlLeft" or "ControlRight" => 0x11,
            "AltLeft" or "AltRight" => 0x12,
            _ => (ushort)0,
        };

        return virtualKey != 0;
    }

    private static bool TryMapVirtualKey(string code, out ushort virtualKey)
    {
        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
        {
            virtualKey = code[3];
            return true;
        }

        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6)
        {
            virtualKey = code[5];
            return true;
        }

        virtualKey = code switch
        {
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            "Space" => 0x20,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "ShiftLeft" or "ShiftRight" => 0x10,
            "ControlLeft" or "ControlRight" => 0x11,
            "AltLeft" or "AltRight" => 0x12,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => (ushort)0,
        };

        return virtualKey != 0;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RawRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxHostCapturePlatform : IHostCapturePlatform
{
    private const string X11Library = "libX11";
    private const string XTestLibrary = "libXtst";
    private const int ZPixmap = 2;
    private const int IsViewable = 2;
    private const int RevertToPointerRoot = 1;
    private static readonly IntPtr AllPlanes = new(-1);
    private static readonly IntPtr CurrentTime = IntPtr.Zero;
    private static readonly StringComparer TargetComparer = StringComparer.OrdinalIgnoreCase;

    public IReadOnlyList<CaptureTargetDescriptor> GetTargets()
    {
        using var session = OpenDisplay();
        var screen = XDefaultScreen(session.Display);
        var width = XDisplayWidth(session.Display, screen);
        var height = XDisplayHeight(session.Display, screen);
        var rootWindow = XRootWindow(session.Display, screen);

        var targets = new List<CaptureTargetDescriptor>
        {
            new("desktop:primary", $"Desktop ({width}x{height})", CaptureTargetKind.Desktop),
        };

        foreach (var target in EnumerateWindowTargets(session.Display, rootWindow))
        {
            targets.Add(target);
        }

        return targets
            .DistinctBy(target => target.Id, TargetComparer)
            .OrderBy(target => target.Kind)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RelayFrame CaptureFrame(HostSessionAssignment assignment, long sequenceId)
    {
        using var session = OpenDisplay();
        var screen = XDefaultScreen(session.Display);
        var rootWindow = XRootWindow(session.Display, screen);

        return assignment.TargetKind switch
        {
            CaptureTargetKind.Desktop => CaptureWindowFrame(session.Display, rootWindow, assignment.SessionId, sequenceId),
            CaptureTargetKind.Window => CaptureWindowFrame(
                session.Display,
                ResolveTargetWindow(session.Display, rootWindow, assignment.TargetId),
                assignment.SessionId,
                sequenceId),
            _ => throw new InvalidOperationException($"Unsupported target kind '{assignment.TargetKind}'."),
        };
    }

    public void HandlePointerInput(HostSessionAssignment assignment, PointerInputEvent input)
    {
        using var session = OpenDisplay();
        var screen = XDefaultScreen(session.Display);
        var rootWindow = XRootWindow(session.Display, screen);
        var targetWindow = assignment.TargetKind == CaptureTargetKind.Window
            ? ResolveTargetWindow(session.Display, rootWindow, assignment.TargetId)
            : IntPtr.Zero;
        var bounds = assignment.TargetKind switch
        {
            CaptureTargetKind.Desktop => new X11Bounds(
                0,
                0,
                XDisplayWidth(session.Display, screen),
                XDisplayHeight(session.Display, screen)),
            CaptureTargetKind.Window => GetWindowBounds(
                session.Display,
                rootWindow,
                targetWindow),
            _ => throw new InvalidOperationException($"Unsupported target kind '{assignment.TargetKind}'."),
        };

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Target '{assignment.TargetId}' does not have valid bounds.");
        }

        var targetX = bounds.X + (int)Math.Round(Math.Clamp(input.X, 0d, 1d) * Math.Max(bounds.Width - 1, 0));
        var targetY = bounds.Y + (int)Math.Round(Math.Clamp(input.Y, 0d, 1d) * Math.Max(bounds.Height - 1, 0));

        if (targetWindow != IntPtr.Zero)
        {
            FocusWindow(session.Display, targetWindow);
        }

        _ = XWarpPointer(session.Display, IntPtr.Zero, rootWindow, 0, 0, 0u, 0u, targetX, targetY);

        if (string.Equals(input.EventType, "wheel", StringComparison.OrdinalIgnoreCase))
        {
            SendWheelInput(session.Display, input.WheelDeltaX, input.WheelDeltaY);
            _ = XFlush(session.Display);
            return;
        }

        var buttonNumber = GetButtonNumber(input.Button);
        if (buttonNumber != 0 && (string.Equals(input.EventType, "down", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(input.EventType, "up", StringComparison.OrdinalIgnoreCase)))
        {
            _ = XTestFakeButtonEvent(
                session.Display,
                buttonNumber,
                string.Equals(input.EventType, "down", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                IntPtr.Zero);
        }

        _ = XFlush(session.Display);
    }

    public ModifierKeyState HandleKeyboardInput(
        HostSessionAssignment assignment,
        KeyboardInputEvent input,
        ModifierKeyState currentModifierState)
    {
        using var session = OpenDisplay();
        var screen = XDefaultScreen(session.Display);
        var rootWindow = XRootWindow(session.Display, screen);
        var targetWindow = assignment.TargetKind == CaptureTargetKind.Window
            ? ResolveTargetWindow(session.Display, rootWindow, assignment.TargetId)
            : rootWindow;

        if (assignment.TargetKind == CaptureTargetKind.Window)
        {
            FocusWindow(session.Display, targetWindow);
        }

        if (!TryMapKeyCode(session.Display, input.Code, out var keyCode))
        {
            return currentModifierState;
        }

        var desiredModifierState = new ModifierKeyState(input.Alt, input.Control, input.Shift);
        if (TryMapModifierKeyCode(session.Display, input.Code, out _))
        {
            _ = XTestFakeKeyEvent(
                session.Display,
                keyCode,
                string.Equals(input.EventType, "keydown", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                IntPtr.Zero);
            _ = XFlush(session.Display);
            return desiredModifierState;
        }

        ApplyModifierState(session.Display, currentModifierState, desiredModifierState);
        _ = XTestFakeKeyEvent(
            session.Display,
            keyCode,
            string.Equals(input.EventType, "keydown", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            IntPtr.Zero);
        _ = XFlush(session.Display);
        return desiredModifierState;
    }

    public void Dispose()
    {
    }

    private static IReadOnlyList<CaptureTargetDescriptor> EnumerateWindowTargets(IntPtr display, IntPtr rootWindow)
    {
        if (XQueryTree(display, rootWindow, out _, out _, out var childrenPointer, out var childCount) == 0)
        {
            return [];
        }

        var targets = new List<CaptureTargetDescriptor>();
        try
        {
            var childSize = IntPtr.Size;
            for (var index = 0; index < checked((int)childCount); index++)
            {
                var childWindow = Marshal.ReadIntPtr(childrenPointer, index * childSize);
                if (childWindow == IntPtr.Zero || !TryGetWindowAttributes(display, childWindow, out var attributes))
                {
                    continue;
                }

                if (attributes.map_state != IsViewable || attributes.override_redirect != 0 || attributes.width <= 0 || attributes.height <= 0)
                {
                    continue;
                }

                var title = GetWindowTitle(display, childWindow);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                targets.Add(new CaptureTargetDescriptor(
                    $"window:x11:0x{childWindow.ToInt64():X}",
                    title,
                    CaptureTargetKind.Window));
            }
        }
        finally
        {
            if (childrenPointer != IntPtr.Zero)
            {
                _ = XFree(childrenPointer);
            }
        }

        return targets;
    }

    private static RelayFrame CaptureWindowFrame(IntPtr display, IntPtr drawable, string sessionId, long sequenceId)
    {
        if (!TryGetWindowAttributes(display, drawable, out var attributes))
        {
            throw new InvalidOperationException("Linux capture could not read target window attributes.");
        }

        var width = attributes.width;
        var height = attributes.height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Linux capture target does not expose valid dimensions.");
        }

        var imageHandle = XGetImage(display, drawable, 0, 0, (uint)width, (uint)height, AllPlanes, ZPixmap);
        if (imageHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Linux capture failed to acquire an X11 image for the selected target.");
        }

        try
        {
            var image = Marshal.PtrToStructure<XImage>(imageHandle);
            if (image.bits_per_pixel < 24 || image.bytes_per_line <= 0 || image.data == IntPtr.Zero)
            {
                throw new InvalidOperationException("Linux capture returned an unsupported X11 image format.");
            }

            if (image.width != width || image.height != height)
            {
                throw new InvalidOperationException(
                    $"Linux capture target resized during capture (requested {width}x{height}, received {image.width}x{image.height}).");
            }

            var sourceLength = checked(image.bytes_per_line * image.height);
            var source = new byte[sourceLength];
            Marshal.Copy(image.data, source, 0, source.Length);

            var packed = new byte[checked(width * height * 4)];
            var bytesPerPixel = image.bits_per_pixel / 8;
            if (bytesPerPixel < 3)
            {
                throw new InvalidOperationException("Linux capture returned fewer than 24 bits per pixel.");
            }

            var requiredBytesPerRow = checked(width * bytesPerPixel);
            if (image.bytes_per_line < requiredBytesPerRow)
            {
                throw new InvalidOperationException(
                    $"Linux capture returned an invalid row stride ({image.bytes_per_line}) for {width} pixels at {bytesPerPixel} bytes per pixel.");
            }

            for (var row = 0; row < height; row++)
            {
                var sourceRowOffset = row * image.bytes_per_line;
                var targetRowOffset = row * width * 4;

                for (var column = 0; column < width; column++)
                {
                    var sourceOffset = sourceRowOffset + (column * bytesPerPixel);
                    var targetOffset = targetRowOffset + (column * 4);

                    packed[targetOffset] = source[sourceOffset];
                    packed[targetOffset + 1] = source[sourceOffset + 1];
                    packed[targetOffset + 2] = source[sourceOffset + 2];
                    packed[targetOffset + 3] = bytesPerPixel > 3 ? source[sourceOffset + 3] : (byte)255;
                }
            }

            return ImageEncoding.CreateJpegFrame(sessionId, sequenceId, packed, width, height);
        }
        finally
        {
            XDestroyImage(imageHandle);
        }
    }

    private static IntPtr ResolveTargetWindow(IntPtr display, IntPtr rootWindow, string targetId)
    {
        if (!TryParseWindowTargetId(targetId, out var windowHandle))
        {
            throw new InvalidOperationException($"Window target '{targetId}' does not contain a valid X11 window ID.");
        }

        if (!TryGetWindowAttributes(display, windowHandle, out var attributes) ||
            attributes.map_state != IsViewable ||
            attributes.width <= 0 ||
            attributes.height <= 0)
        {
            throw new InvalidOperationException($"Window target '{targetId}' is not currently available.");
        }

        if (XTranslateCoordinates(display, windowHandle, rootWindow, 0, 0, out _, out _, out _) == 0)
        {
            throw new InvalidOperationException($"Window target '{targetId}' could not be translated to root coordinates.");
        }

        return windowHandle;
    }

    private static X11Bounds GetWindowBounds(IntPtr display, IntPtr rootWindow, IntPtr windowHandle)
    {
        if (!TryGetWindowAttributes(display, windowHandle, out var attributes))
        {
            throw new InvalidOperationException("Linux input could not read target window attributes.");
        }

        if (XTranslateCoordinates(display, windowHandle, rootWindow, 0, 0, out var x, out var y, out _) == 0)
        {
            throw new InvalidOperationException("Linux input could not translate the target window to root coordinates.");
        }

        return new X11Bounds(x, y, attributes.width, attributes.height);
    }

    private static void FocusWindow(IntPtr display, IntPtr windowHandle)
    {
        _ = XRaiseWindow(display, windowHandle);
        _ = XSetInputFocus(display, windowHandle, RevertToPointerRoot, CurrentTime);
    }

    private static uint GetButtonNumber(string? button) => button?.ToLowerInvariant() switch
    {
        "middle" => 2u,
        "right" => 3u,
        _ => 1u,
    };

    private static void SendWheelInput(IntPtr display, int wheelDeltaX, int wheelDeltaY)
    {
        SendWheelButton(display, wheelDeltaY < 0 ? 4u : 5u, Math.Abs(wheelDeltaY));
        SendWheelButton(display, wheelDeltaX < 0 ? 6u : 7u, Math.Abs(wheelDeltaX));
    }

    private static void SendWheelButton(IntPtr display, uint buttonNumber, int count)
    {
        for (var index = 0; index < count; index++)
        {
            _ = XTestFakeButtonEvent(display, buttonNumber, 1, IntPtr.Zero);
            _ = XTestFakeButtonEvent(display, buttonNumber, 0, IntPtr.Zero);
        }
    }

    private static void ApplyModifierState(IntPtr display, ModifierKeyState current, ModifierKeyState desired)
    {
        ApplyModifierTransition(display, current.Control, desired.Control, "ControlLeft");
        ApplyModifierTransition(display, current.Shift, desired.Shift, "ShiftLeft");
        ApplyModifierTransition(display, current.Alt, desired.Alt, "AltLeft");
    }

    private static void ApplyModifierTransition(IntPtr display, bool current, bool desired, string code)
    {
        if (current == desired || !TryMapModifierKeyCode(display, code, out var keyCode))
        {
            return;
        }

        _ = XTestFakeKeyEvent(display, keyCode, desired ? 1 : 0, IntPtr.Zero);
    }

    private static bool TryMapModifierKeyCode(IntPtr display, string code, out uint keyCode)
    {
        keyCode = 0;
        if (!TryMapDomCodeToKeysym(code, out var keySym) ||
            keySym == IntPtr.Zero)
        {
            return false;
        }

        keyCode = XKeysymToKeycode(display, keySym);
        return keyCode != 0;
    }

    private static bool TryMapKeyCode(IntPtr display, string code, out uint keyCode)
    {
        keyCode = 0;
        if (!TryMapDomCodeToKeysym(code, out var keySym) ||
            keySym == IntPtr.Zero)
        {
            return false;
        }

        keyCode = XKeysymToKeycode(display, keySym);
        return keyCode != 0;
    }

    private static bool TryMapDomCodeToKeysym(string code, out IntPtr keySym)
    {
        keySym = code switch
        {
            "Enter" => XStringToKeysym("Return"),
            "Escape" => XStringToKeysym("Escape"),
            "Backspace" => XStringToKeysym("BackSpace"),
            "Tab" => XStringToKeysym("Tab"),
            "Space" => XStringToKeysym("space"),
            "ArrowLeft" => XStringToKeysym("Left"),
            "ArrowUp" => XStringToKeysym("Up"),
            "ArrowRight" => XStringToKeysym("Right"),
            "ArrowDown" => XStringToKeysym("Down"),
            "Delete" => XStringToKeysym("Delete"),
            "Insert" => XStringToKeysym("Insert"),
            "Home" => XStringToKeysym("Home"),
            "End" => XStringToKeysym("End"),
            "PageUp" => XStringToKeysym("Page_Up"),
            "PageDown" => XStringToKeysym("Page_Down"),
            "ShiftLeft" => XStringToKeysym("Shift_L"),
            "ShiftRight" => XStringToKeysym("Shift_R"),
            "ControlLeft" => XStringToKeysym("Control_L"),
            "ControlRight" => XStringToKeysym("Control_R"),
            "AltLeft" => XStringToKeysym("Alt_L"),
            "AltRight" => XStringToKeysym("Alt_R"),
            "F1" => XStringToKeysym("F1"),
            "F2" => XStringToKeysym("F2"),
            "F3" => XStringToKeysym("F3"),
            "F4" => XStringToKeysym("F4"),
            "F5" => XStringToKeysym("F5"),
            "F6" => XStringToKeysym("F6"),
            "F7" => XStringToKeysym("F7"),
            "F8" => XStringToKeysym("F8"),
            "F9" => XStringToKeysym("F9"),
            "F10" => XStringToKeysym("F10"),
            "F11" => XStringToKeysym("F11"),
            "F12" => XStringToKeysym("F12"),
            _ => IntPtr.Zero,
        };

        if (keySym != IntPtr.Zero)
        {
            return true;
        }

        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
        {
            keySym = XStringToKeysym(char.ToLowerInvariant(code[3]).ToString());
            return keySym != IntPtr.Zero;
        }

        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6)
        {
            keySym = XStringToKeysym(code[5].ToString());
            return keySym != IntPtr.Zero;
        }

        return false;
    }

    private static bool TryParseWindowTargetId(string targetId, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        var segments = targetId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rawWindowId = segments.LastOrDefault();
        if (string.IsNullOrWhiteSpace(rawWindowId))
        {
            return false;
        }

        var style = rawWindowId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.NumberStyles.HexNumber
            : System.Globalization.NumberStyles.Integer;
        var raw = rawWindowId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? rawWindowId[2..]
            : rawWindowId;

        if (!ulong.TryParse(raw, style, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value == 0 || value > (ulong)nint.MaxValue)
        {
            return false;
        }

        windowHandle = (nint)value;
        return true;
    }

    private static string? GetWindowTitle(IntPtr display, IntPtr windowHandle)
    {
        if (XFetchName(display, windowHandle, out var namePointer) == 0 || namePointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringAnsi(namePointer);
        }
        finally
        {
            _ = XFree(namePointer);
        }
    }

    private static bool TryGetWindowAttributes(IntPtr display, IntPtr windowHandle, out XWindowAttributes attributes)
    {
        attributes = default;
        return XGetWindowAttributes(display, windowHandle, out attributes) != 0;
    }

    private static X11DisplaySession OpenDisplay()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            throw new InvalidOperationException("Linux capture/input could not open an X11 display. Ensure an X server is available.");
        }

        return new X11DisplaySession(display);
    }

    [DllImport(X11Library)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11Library)]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport(X11Library)]
    private static extern IntPtr XRootWindow(IntPtr display, int screenNumber);

    [DllImport(X11Library)]
    private static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport(X11Library)]
    private static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport(X11Library)]
    private static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, IntPtr planeMask, int format);

    [DllImport(X11Library)]
    private static extern int XDestroyImage(IntPtr image);

    [DllImport(X11Library)]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport(X11Library)]
    private static extern int XTranslateCoordinates(
        IntPtr display,
        IntPtr srcWindow,
        IntPtr destWindow,
        int srcX,
        int srcY,
        out int destX,
        out int destY,
        out IntPtr childWindow);

    [DllImport(X11Library)]
    private static extern int XQueryTree(
        IntPtr display,
        IntPtr window,
        out IntPtr rootReturn,
        out IntPtr parentReturn,
        out IntPtr childrenReturn,
        out uint childCountReturn);

    [DllImport(X11Library)]
    private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr windowName);

    [DllImport(X11Library)]
    private static extern int XFree(IntPtr data);

    [DllImport(X11Library)]
    private static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport(X11Library)]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, IntPtr time);

    [DllImport(X11Library)]
    private static extern int XWarpPointer(
        IntPtr display,
        IntPtr srcWindow,
        IntPtr destWindow,
        int srcX,
        int srcY,
        uint srcWidth,
        uint srcHeight,
        int destX,
        int destY);

    [DllImport(X11Library)]
    private static extern int XFlush(IntPtr display);

    [DllImport(X11Library)]
    private static extern IntPtr XStringToKeysym(string name);

    [DllImport(X11Library)]
    private static extern uint XKeysymToKeycode(IntPtr display, IntPtr keySym);

    [DllImport(XTestLibrary)]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, IntPtr delay);

    [DllImport(XTestLibrary)]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, IntPtr delay);

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width;
        public int height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public uint red_mask;
        public uint green_mask;
        public uint blue_mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x;
        public int y;
        public int width;
        public int height;
        public int border_width;
        public int depth;
        public IntPtr visual;
        public IntPtr root;
        public int @class;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public nuint backing_planes;
        public nuint backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed;
        public int map_state;
        public nint all_event_masks;
        public nint your_event_mask;
        public nint do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    private readonly record struct X11Bounds(int X, int Y, int Width, int Height);

    private sealed class X11DisplaySession : IDisposable
    {
        public X11DisplaySession(IntPtr display) => Display = display;

        public IntPtr Display { get; }

        public void Dispose()
        {
            _ = XCloseDisplay(Display);
        }
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacOsHostCapturePlatform : IHostCapturePlatform
{
    private const string ApplicationServicesPath = "/System/Library/Frameworks/ApplicationServices.framework/Versions/Current/ApplicationServices";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CgNullWindowId = 0;
    private const uint CgWindowListOptionOnScreenOnly = 1u << 0;
    private const uint CgWindowListOptionIncludingWindow = 1u << 3;
    private const uint CgWindowListExcludeDesktopElements = 1u << 4;
    private const uint CgWindowImageBoundsIgnoreFraming = 1u << 0;
    private const uint CgEventTapHid = 0;
    private const int CfNumberSInt64Type = 4;
    private const uint CfStringEncodingUtf8 = 0x08000100;
    private const int AxValueCgPointType = 1;
    private const int AxValueCgSizeType = 2;
    private const int LeftMouseButton = 0;
    private const int RightMouseButton = 1;
    private const int CenterMouseButton = 2;
    private const uint LeftMouseDown = 1;
    private const uint LeftMouseUp = 2;
    private const uint RightMouseDown = 3;
    private const uint RightMouseUp = 4;
    private const uint MouseMoved = 5;
    private const uint LeftMouseDragged = 6;
    private const uint RightMouseDragged = 7;
    private const uint ScrollWheel = 22;
    private const uint OtherMouseDown = 25;
    private const uint OtherMouseUp = 26;
    private const uint OtherMouseDragged = 27;
    private const uint KeyDown = 10;
    private const uint KeyUp = 11;
    private const int WindowMatchTolerance = 2;
    private const uint CgScrollEventUnitLine = 1;
    private const uint CgEventFieldMouseEventClickState = 1;
    private const int CgWindowSharingNone = 0;

    private static readonly IntPtr CgWindowNumberKey = CreateStringKey("kCGWindowNumber");
    private static readonly IntPtr CgWindowOwnerPidKey = CreateStringKey("kCGWindowOwnerPID");
    private static readonly IntPtr CgWindowOwnerNameKey = CreateStringKey("kCGWindowOwnerName");
    private static readonly IntPtr CgWindowNameKey = CreateStringKey("kCGWindowName");
    private static readonly IntPtr CgWindowBoundsKey = CreateStringKey("kCGWindowBounds");
    private static readonly IntPtr CgWindowLayerKey = CreateStringKey("kCGWindowLayer");
    private static readonly IntPtr CgWindowSharingStateKey = CreateStringKey("kCGWindowSharingState");
    private static readonly IntPtr AxWindowsAttribute = CreateStringKey("AXWindows");
    private static readonly IntPtr AxTitleAttribute = CreateStringKey("AXTitle");
    private static readonly IntPtr AxPositionAttribute = CreateStringKey("AXPosition");
    private static readonly IntPtr AxSizeAttribute = CreateStringKey("AXSize");
    private static readonly IntPtr AxRaiseAction = CreateStringKey("AXRaise");

    public IReadOnlyList<CaptureTargetDescriptor> GetTargets()
    {
        var bounds = GetPrimaryDisplayBounds();
        var targets = new List<CaptureTargetDescriptor>();
        var displayName = bounds.Width > 0 && bounds.Height > 0
            ? $"Desktop ({bounds.Width}x{bounds.Height})"
            : "Primary Desktop";

        targets.Add(new CaptureTargetDescriptor("desktop:primary", displayName, CaptureTargetKind.Desktop));
        targets.AddRange(GetVisibleWindows()
            .Select(window => new CaptureTargetDescriptor(
                CreateWindowTargetId(window.WindowId),
                string.IsNullOrWhiteSpace(window.WindowTitle)
                    ? $"{window.OwnerName} (Window {window.WindowId})"
                    : $"{window.OwnerName} - {window.WindowTitle}",
                CaptureTargetKind.Window)));

        return targets
            .DistinctBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.Kind)
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RelayFrame CaptureFrame(HostSessionAssignment assignment, long sequenceId)
    {
        IntPtr imageHandle = IntPtr.Zero;
        try
        {
            imageHandle = assignment.TargetKind switch
            {
                CaptureTargetKind.Desktop => CaptureDesktopImage(),
                CaptureTargetKind.Window => CaptureWindowImage(ResolveWindow(assignment.TargetId)),
                _ => throw new InvalidOperationException($"Unsupported target kind '{assignment.TargetKind}'."),
            };

            return CreateFrameFromCgImage(
                assignment.SessionId,
                sequenceId,
                imageHandle,
                assignment.TargetKind == CaptureTargetKind.Window
                    ? "macOS window capture failed to read the selected window image."
                    : "macOS desktop capture failed to read the display image.");
        }
        finally
        {
            if (imageHandle != IntPtr.Zero)
            {
                CGImageRelease(imageHandle);
            }
        }
    }

    public void HandlePointerInput(HostSessionAssignment assignment, PointerInputEvent input)
    {
        EnsureInputTrusted();

        var targetWindow = assignment.TargetKind == CaptureTargetKind.Window
            ? ResolveWindow(assignment.TargetId)
            : default;
        var bounds = assignment.TargetKind switch
        {
            CaptureTargetKind.Desktop => GetPrimaryDisplayBounds(),
            CaptureTargetKind.Window => targetWindow.Bounds,
            _ => throw new InvalidOperationException($"Unsupported target kind '{assignment.TargetKind}'."),
        };

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Target '{assignment.TargetId}' does not have valid bounds.");
        }

        var targetPoint = new CGPoint(
            bounds.X + (Math.Clamp(input.X, 0d, 1d) * Math.Max(bounds.Width - 1, 0)),
            bounds.Y + (Math.Clamp(input.Y, 0d, 1d) * Math.Max(bounds.Height - 1, 0)));

        if (assignment.TargetKind == CaptureTargetKind.Window)
        {
            FocusWindow(targetWindow);
        }

        if (string.Equals(input.EventType, "wheel", StringComparison.OrdinalIgnoreCase))
        {
            PostMouseEvent(MouseMoved, targetPoint, LeftMouseButton, 0);
            PostScrollEvent(input.WheelDeltaX, input.WheelDeltaY);
            return;
        }

        var eventType = GetMouseEventType(input.EventType, input.Button);
        if (eventType == 0)
        {
            return;
        }

        PostMouseEvent(eventType, targetPoint, GetMouseButton(input.Button), input.ClickCount);
    }

    public ModifierKeyState HandleKeyboardInput(
        HostSessionAssignment assignment,
        KeyboardInputEvent input,
        ModifierKeyState currentModifierState)
    {
        EnsureInputTrusted();

        if (assignment.TargetKind == CaptureTargetKind.Window)
        {
            FocusWindow(ResolveWindow(assignment.TargetId));
        }

        if (!TryMapVirtualKey(input.Code, out var virtualKey))
        {
            if (!TryGetModifierVirtualKey(input.Code, out virtualKey))
            {
                return currentModifierState;
            }
        }

        var desiredModifierState = new ModifierKeyState(input.Alt, input.Control, input.Shift);
        if (TryGetModifierVirtualKey(input.Code, out _))
        {
            PostKeyboardEvent(virtualKey, string.Equals(input.EventType, "keydown", StringComparison.OrdinalIgnoreCase));
            return desiredModifierState;
        }

        ApplyModifierState(currentModifierState, desiredModifierState);
        PostKeyboardEvent(virtualKey, string.Equals(input.EventType, "keydown", StringComparison.OrdinalIgnoreCase));
        return desiredModifierState;
    }

    public void Dispose()
    {
    }

    private static MacWindowInfo ResolveWindow(string targetId)
    {
        if (!TryParseWindowTargetId(targetId, out var windowId))
        {
            throw new InvalidOperationException($"Window target '{targetId}' does not contain a valid macOS window ID.");
        }

        var window = GetVisibleWindows().FirstOrDefault(candidate => candidate.WindowId == windowId);
        return window.WindowId == 0
            ? throw new InvalidOperationException($"Window target '{targetId}' is not currently available.")
            : window;
    }

    private static IReadOnlyList<MacWindowInfo> GetVisibleWindows()
    {
        var windowArray = CGWindowListCopyWindowInfo(
            CgWindowListOptionOnScreenOnly | CgWindowListExcludeDesktopElements,
            CgNullWindowId);
        if (windowArray == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS window enumeration could not query the current window server session.");
        }

        try
        {
            var windows = new List<MacWindowInfo>();
            var count = checked((int)CFArrayGetCount(windowArray));
            for (var index = 0; index < count; index++)
            {
                var dictionary = CFArrayGetValueAtIndex(windowArray, index);
                if (dictionary == IntPtr.Zero || !TryReadWindowInfo(dictionary, out var windowInfo))
                {
                    continue;
                }

                windows.Add(windowInfo);
            }

            return windows;
        }
        finally
        {
            CFRelease(windowArray);
        }
    }

    private static bool TryReadWindowInfo(IntPtr dictionary, out MacWindowInfo windowInfo)
    {
        windowInfo = default;
        if (!TryGetDictionaryUInt32(dictionary, CgWindowNumberKey, out var windowId) ||
            !TryGetDictionaryInt32(dictionary, CgWindowOwnerPidKey, out var ownerPid) ||
            !TryGetDictionaryString(dictionary, CgWindowOwnerNameKey, out var ownerName) ||
            !TryGetDictionaryCGRect(dictionary, CgWindowBoundsKey, out var bounds))
        {
            return false;
        }

        if (!TryGetDictionaryInt32(dictionary, CgWindowLayerKey, out var layer))
        {
            layer = 0;
        }

        if (!TryGetDictionaryInt32(dictionary, CgWindowSharingStateKey, out var sharingState))
        {
            sharingState = 1;
        }

        var title = TryGetDictionaryString(dictionary, CgWindowNameKey, out var rawTitle)
            ? rawTitle
            : string.Empty;

        if (windowId == 0 ||
            ownerPid <= 0 ||
            bounds.Width <= 1 ||
            bounds.Height <= 1 ||
            layer != 0 ||
            sharingState == CgWindowSharingNone ||
            string.IsNullOrWhiteSpace(ownerName))
        {
            return false;
        }

        windowInfo = new MacWindowInfo(windowId, ownerPid, ownerName, title ?? string.Empty, bounds);
        return true;
    }

    private static IntPtr CaptureDesktopImage()
    {
        var displayId = CGMainDisplayID();
        var imageHandle = CGDisplayCreateImage(displayId);
        if (imageHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS desktop capture failed to create a display image.");
        }

        return imageHandle;
    }

    private static IntPtr CaptureWindowImage(MacWindowInfo window)
    {
        var imageHandle = CGWindowListCreateImage(
            window.Bounds.ToCGRect(),
            CgWindowListOptionIncludingWindow,
            window.WindowId,
            CgWindowImageBoundsIgnoreFraming);
        if (imageHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"macOS window capture failed for window '{window.WindowId}'. Verify Screen Recording permission is granted and the window is shareable.");
        }

        return imageHandle;
    }

    private static RelayFrame CreateFrameFromCgImage(string sessionId, long sequenceId, IntPtr imageHandle, string errorPrefix)
    {
        var width = checked((int)CGImageGetWidth(imageHandle));
        var height = checked((int)CGImageGetHeight(imageHandle));
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(errorPrefix);
        }

        var bytesPerRow = checked((int)CGImageGetBytesPerRow(imageHandle));
        var provider = CGImageGetDataProvider(imageHandle);
        if (provider == IntPtr.Zero)
        {
            throw new InvalidOperationException(errorPrefix);
        }

        var dataHandle = CGDataProviderCopyData(provider);
        if (dataHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(errorPrefix);
        }

        try
        {
            var sourceLength = checked((int)CFDataGetLength(dataHandle));
            var sourcePointer = CFDataGetBytePtr(dataHandle);
            var source = new byte[sourceLength];
            Marshal.Copy(sourcePointer, source, 0, source.Length);

            var requiredBytesPerRow = checked(width * 4);
            if (bytesPerRow < requiredBytesPerRow)
            {
                throw new InvalidOperationException(
                    $"macOS capture returned an unsupported pixel format (bytesPerRow={bytesPerRow}).");
            }

            var requiredLength = checked(((height - 1) * bytesPerRow) + requiredBytesPerRow);
            if (sourceLength < requiredLength)
            {
                throw new InvalidOperationException(
                    $"macOS capture returned insufficient image data ({sourceLength} bytes, expected at least {requiredLength}).");
            }

            var packed = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source, row * bytesPerRow, packed, row * width * 4, requiredBytesPerRow);
            }

            return ImageEncoding.CreateJpegFrame(sessionId, sequenceId, packed, width, height);
        }
        finally
        {
            CFRelease(dataHandle);
        }
    }

    private static void EnsureInputTrusted()
    {
        if (!AXIsProcessTrusted())
        {
            throw new InvalidOperationException(
                "macOS interactive input requires Accessibility permission for the host process.");
        }
    }

    private static void FocusWindow(MacWindowInfo window)
    {
        var broughtFront = TrySetFrontProcess(window.OwnerPid);
        if (TryRaiseAccessibilityWindow(window))
        {
            return;
        }

        if (!broughtFront)
        {
            throw new InvalidOperationException(
                $"macOS input could not bring process '{window.OwnerPid}' to the foreground.");
        }
    }

    private static bool TrySetFrontProcess(int pid)
    {
        if (GetProcessForPID(pid, out var processSerialNumber) != 0)
        {
            return false;
        }

        return SetFrontProcess(ref processSerialNumber) == 0;
    }

    private static bool TryRaiseAccessibilityWindow(MacWindowInfo target)
    {
        var application = AXUIElementCreateApplication(target.OwnerPid);
        if (application == IntPtr.Zero)
        {
            return false;
        }

        if (AXUIElementCopyAttributeValue(application, AxWindowsAttribute, out var windowsArray) != 0 || windowsArray == IntPtr.Zero)
        {
            CFRelease(application);
            return false;
        }

        try
        {
            var count = checked((int)CFArrayGetCount(windowsArray));
            for (var index = 0; index < count; index++)
            {
                var windowElement = CFArrayGetValueAtIndex(windowsArray, index);
                if (windowElement == IntPtr.Zero)
                {
                    continue;
                }

                if (!TryGetAccessibilityWindow(windowElement, out var candidateBounds, out var candidateTitle))
                {
                    continue;
                }

                if (!BoundsMatch(candidateBounds, target.Bounds))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(target.WindowTitle) &&
                    !string.Equals(candidateTitle, target.WindowTitle, StringComparison.Ordinal))
                {
                    continue;
                }

                return AXUIElementPerformAction(windowElement, AxRaiseAction) == 0;
            }
        }
        finally
        {
            CFRelease(windowsArray);
            CFRelease(application);
        }

        return false;
    }

    private static bool TryGetAccessibilityWindow(IntPtr windowElement, out DisplayBounds bounds, out string? title)
    {
        bounds = default;
        title = null;

        if (AXUIElementCopyAttributeValue(windowElement, AxPositionAttribute, out var positionValue) != 0 || positionValue == IntPtr.Zero)
        {
            return false;
        }

        if (AXUIElementCopyAttributeValue(windowElement, AxSizeAttribute, out var sizeValue) != 0 || sizeValue == IntPtr.Zero)
        {
            CFRelease(positionValue);
            return false;
        }

        try
        {
            if (AXValueGetType(positionValue) != AxValueCgPointType ||
                AXValueGetType(sizeValue) != AxValueCgSizeType ||
                !AXValueGetValue(positionValue, AxValueCgPointType, out CGPoint position) ||
                !AXValueGetValue(sizeValue, AxValueCgSizeType, out CGSize size))
            {
                return false;
            }

            bounds = new DisplayBounds(
                (int)Math.Round(position.X),
                (int)Math.Round(position.Y),
                (int)Math.Round(size.Width),
                (int)Math.Round(size.Height));
        }
        finally
        {
            CFRelease(positionValue);
            CFRelease(sizeValue);
        }

        if (AXUIElementCopyAttributeValue(windowElement, AxTitleAttribute, out var titleValue) == 0 && titleValue != IntPtr.Zero)
        {
            try
            {
                title = ReadCfString(titleValue);
            }
            finally
            {
                CFRelease(titleValue);
            }
        }

        return true;
    }

    private static bool BoundsMatch(DisplayBounds left, DisplayBounds right)
    {
        return Math.Abs(left.X - right.X) <= WindowMatchTolerance &&
               Math.Abs(left.Y - right.Y) <= WindowMatchTolerance &&
               Math.Abs(left.Width - right.Width) <= WindowMatchTolerance &&
               Math.Abs(left.Height - right.Height) <= WindowMatchTolerance;
    }

    private static uint GetMouseEventType(string eventType, string? button) => eventType.ToLowerInvariant() switch
    {
        "move" when string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) => RightMouseDragged,
        "move" when string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase) => OtherMouseDragged,
        "move" when string.Equals(button, "left", StringComparison.OrdinalIgnoreCase) => LeftMouseDragged,
        "move" => MouseMoved,
        "down" when string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) => RightMouseDown,
        "up" when string.Equals(button, "right", StringComparison.OrdinalIgnoreCase) => RightMouseUp,
        "down" when string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase) => OtherMouseDown,
        "up" when string.Equals(button, "middle", StringComparison.OrdinalIgnoreCase) => OtherMouseUp,
        "down" => LeftMouseDown,
        "up" => LeftMouseUp,
        _ => 0u,
    };

    private static void PostMouseEvent(uint eventType, CGPoint point, int button, int clickCount)
    {
        var mouseEvent = CGEventCreateMouseEvent(IntPtr.Zero, eventType, point, button);
        if (mouseEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS interactive input failed to create a mouse event.");
        }

        try
        {
            if (clickCount > 0)
            {
                CGEventSetIntegerValueField(mouseEvent, CgEventFieldMouseEventClickState, clickCount);
            }

            CGEventPost(CgEventTapHid, mouseEvent);
        }
        finally
        {
            CFRelease(mouseEvent);
        }
    }

    private static void PostScrollEvent(int wheelDeltaX, int wheelDeltaY)
    {
        if (wheelDeltaX == 0 && wheelDeltaY == 0)
        {
            return;
        }

        var scrollEvent = CGEventCreateScrollWheelEvent(
            IntPtr.Zero,
            CgScrollEventUnitLine,
            2,
            checked(-wheelDeltaY),
            wheelDeltaX);
        if (scrollEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS interactive input failed to create a scroll event.");
        }

        try
        {
            CGEventPost(CgEventTapHid, scrollEvent);
        }
        finally
        {
            CFRelease(scrollEvent);
        }
    }

    private static void PostKeyboardEvent(ushort virtualKey, bool isKeyDown)
    {
        var keyboardEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, virtualKey, isKeyDown);
        if (keyboardEvent == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS interactive input failed to create a keyboard event.");
        }

        try
        {
            CGEventPost(CgEventTapHid, keyboardEvent);
        }
        finally
        {
            CFRelease(keyboardEvent);
        }
    }

    private static void ApplyModifierState(ModifierKeyState current, ModifierKeyState desired)
    {
        ApplyModifierTransition(current.Control, desired.Control, 59);
        ApplyModifierTransition(current.Shift, desired.Shift, 56);
        ApplyModifierTransition(current.Alt, desired.Alt, 58);
    }

    private static void ApplyModifierTransition(bool current, bool desired, ushort keyCode)
    {
        if (current == desired)
        {
            return;
        }

        PostKeyboardEvent(keyCode, desired);
    }

    private static int GetMouseButton(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => RightMouseButton,
        "middle" => CenterMouseButton,
        _ => LeftMouseButton,
    };

    private static bool TryGetModifierVirtualKey(string code, out ushort virtualKey)
    {
        virtualKey = code switch
        {
            "ShiftLeft" => 56,
            "ShiftRight" => 60,
            "ControlLeft" => 59,
            "ControlRight" => 62,
            "AltLeft" => 58,
            "AltRight" => 61,
            _ => (ushort)0,
        };

        return virtualKey != 0;
    }

    private static bool TryMapVirtualKey(string code, out ushort virtualKey)
    {
        virtualKey = code switch
        {
            "Enter" => 36,
            "Escape" => 53,
            "Backspace" => 51,
            "Tab" => 48,
            "Space" => 49,
            "ArrowLeft" => 123,
            "ArrowUp" => 126,
            "ArrowRight" => 124,
            "ArrowDown" => 125,
            "Delete" => 117,
            "Home" => 115,
            "End" => 119,
            "PageUp" => 116,
            "PageDown" => 121,
            "ShiftLeft" => 56,
            "ShiftRight" => 60,
            "ControlLeft" => 59,
            "ControlRight" => 62,
            "AltLeft" => 58,
            "AltRight" => 61,
            "F1" => 122,
            "F2" => 120,
            "F3" => 99,
            "F4" => 118,
            "F5" => 96,
            "F6" => 97,
            "F7" => 98,
            "F8" => 100,
            "F9" => 101,
            "F10" => 109,
            "F11" => 103,
            "F12" => 111,
            _ => (ushort)0,
        };

        if (virtualKey != 0)
        {
            return true;
        }

        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
        {
            virtualKey = char.ToUpperInvariant(code[3]) switch
            {
                'A' => 0,
                'S' => 1,
                'D' => 2,
                'F' => 3,
                'H' => 4,
                'G' => 5,
                'Z' => 6,
                'X' => 7,
                'C' => 8,
                'V' => 9,
                'B' => 11,
                'Q' => 12,
                'W' => 13,
                'E' => 14,
                'R' => 15,
                'Y' => 16,
                'T' => 17,
                'O' => 31,
                'U' => 32,
                'I' => 34,
                'P' => 35,
                'L' => 37,
                'J' => 38,
                'K' => 40,
                'N' => 45,
                'M' => 46,
                _ => (ushort)0,
            };

            return virtualKey != 0;
        }

        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6)
        {
            virtualKey = code[5] switch
            {
                '1' => 18,
                '2' => 19,
                '3' => 20,
                '4' => 21,
                '5' => 23,
                '6' => 22,
                '7' => 26,
                '8' => 28,
                '9' => 25,
                '0' => 29,
                _ => (ushort)0,
            };

            return virtualKey != 0;
        }

        return false;
    }

    private static bool TryParseWindowTargetId(string targetId, out uint windowId)
    {
        windowId = 0;
        var segments = targetId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rawWindowId = segments.LastOrDefault();
        return rawWindowId is not null &&
               uint.TryParse(rawWindowId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out windowId) &&
               windowId != 0;
    }

    private static string CreateWindowTargetId(uint windowId)
    {
        return $"window:mac:{windowId}";
    }

    private static DisplayBounds GetPrimaryDisplayBounds()
    {
        var displayId = CGMainDisplayID();
        var rect = CGDisplayBounds(displayId);
        return new DisplayBounds(0, 0, (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
    }

    private static bool TryGetDictionaryUInt32(IntPtr dictionary, IntPtr key, out uint value)
    {
        value = 0;
        if (!TryGetDictionaryInt64(dictionary, key, out var signedValue) || signedValue <= 0 || signedValue > uint.MaxValue)
        {
            return false;
        }

        value = (uint)signedValue;
        return true;
    }

    private static bool TryGetDictionaryInt32(IntPtr dictionary, IntPtr key, out int value)
    {
        value = 0;
        if (!TryGetDictionaryInt64(dictionary, key, out var signedValue) || signedValue < int.MinValue || signedValue > int.MaxValue)
        {
            return false;
        }

        value = (int)signedValue;
        return true;
    }

    private static bool TryGetDictionaryInt64(IntPtr dictionary, IntPtr key, out long value)
    {
        value = 0;
        var number = CFDictionaryGetValue(dictionary, key);
        return number != IntPtr.Zero && CFNumberGetValue(number, CfNumberSInt64Type, out value);
    }

    private static bool TryGetDictionaryString(IntPtr dictionary, IntPtr key, out string? value)
    {
        value = null;
        var stringHandle = CFDictionaryGetValue(dictionary, key);
        if (stringHandle == IntPtr.Zero)
        {
            return false;
        }

        value = ReadCfString(stringHandle);
        return value is not null;
    }

    private static string? ReadCfString(IntPtr stringHandle)
    {
        if (stringHandle == IntPtr.Zero)
        {
            return null;
        }

        var length = CFStringGetLength(stringHandle);
        var buffer = new System.Text.StringBuilder(checked((int)(length * 4 + 1)));
        return CFStringGetCString(stringHandle, buffer, buffer.Capacity, CfStringEncodingUtf8)
            ? buffer.ToString()
            : null;
    }

    private static bool TryGetDictionaryCGRect(IntPtr dictionary, IntPtr key, out DisplayBounds bounds)
    {
        bounds = default;
        var boundsDictionary = CFDictionaryGetValue(dictionary, key);
        if (boundsDictionary == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(boundsDictionary, out var rect))
        {
            return false;
        }

        bounds = new DisplayBounds(
            (int)Math.Round(rect.Origin.X),
            (int)Math.Round(rect.Origin.Y),
            (int)Math.Round(rect.Width),
            (int)Math.Round(rect.Height));
        return true;
    }

    private static IntPtr CreateStringKey(string value)
    {
        var handle = CFStringCreateWithCString(IntPtr.Zero, value, CfStringEncodingUtf8);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to allocate macOS string constant '{value}'.");
        }

        return handle;
    }

    [DllImport(ApplicationServicesPath)]
    private static extern uint CGMainDisplayID();

    [DllImport(ApplicationServicesPath)]
    private static extern CGRect CGDisplayBounds(uint displayId);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGDisplayCreateImage(uint displayId);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGWindowListCreateImage(
        CGRect screenBounds,
        uint listOption,
        uint windowId,
        uint imageOption);

    [DllImport(ApplicationServicesPath)]
    private static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(ApplicationServicesPath)]
    private static extern nuint CGImageGetHeight(IntPtr image);

    [DllImport(ApplicationServicesPath)]
    private static extern nuint CGImageGetBytesPerRow(IntPtr image);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGImageGetDataProvider(IntPtr image);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGDataProviderCopyData(IntPtr provider);

    [DllImport(ApplicationServicesPath)]
    private static extern void CGImageRelease(IntPtr image);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGEventCreateMouseEvent(
        IntPtr source,
        uint mouseType,
        CGPoint mouseCursorPosition,
        int mouseButton);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGEventCreateScrollWheelEvent(
        IntPtr source,
        uint units,
        uint wheelCount,
        int wheel1,
        int wheel2);

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

    [DllImport(ApplicationServicesPath)]
    private static extern void CGEventPost(uint tap, IntPtr @event);

    [DllImport(ApplicationServicesPath)]
    private static extern void CGEventSetIntegerValueField(IntPtr @event, uint field, long value);

    [DllImport(ApplicationServicesPath)]
    private static extern int GetProcessForPID(int pid, out ProcessSerialNumber processSerialNumber);

    [DllImport(ApplicationServicesPath)]
    private static extern int SetFrontProcess(ref ProcessSerialNumber processSerialNumber);

    [DllImport(ApplicationServicesPath)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServicesPath)]
    private static extern IntPtr AXUIElementCreateApplication(int pid);

    [DllImport(ApplicationServicesPath)]
    private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [DllImport(ApplicationServicesPath)]
    private static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);

    [DllImport(ApplicationServicesPath, EntryPoint = "AXValueGetValue")]
    private static extern bool AXValueGetValue(IntPtr value, int axValueType, out CGPoint point);

    [DllImport(ApplicationServicesPath, EntryPoint = "AXValueGetValue")]
    private static extern bool AXValueGetValue(IntPtr value, int axValueType, out CGSize size);

    [DllImport(ApplicationServicesPath)]
    private static extern int AXValueGetType(IntPtr value);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationPath)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFStringGetLength(IntPtr handle);

    [DllImport(CoreFoundationPath)]
    private static extern bool CFStringGetCString(
        IntPtr handle,
        System.Text.StringBuilder buffer,
        int bufferSize,
        uint encoding);

    [DllImport(CoreFoundationPath)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, int index);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundationPath)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out long value);

    [DllImport(ApplicationServicesPath)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionaryRepresentation, out CGRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(CGPoint Origin, CGSize Size)
    {
        public double Width => Size.Width;

        public double Height => Size.Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessSerialNumber
    {
        public uint HighLongOfPSN;
        public uint LowLongOfPSN;
    }

    private readonly record struct DisplayBounds(int X, int Y, int Width, int Height)
    {
        public CGRect ToCGRect() => new(new CGPoint(X, Y), new CGSize(Width, Height));
    }

    private readonly record struct MacWindowInfo(
        uint WindowId,
        int OwnerPid,
        string OwnerName,
        string WindowTitle,
        DisplayBounds Bounds);
}

internal sealed class UnsupportedHostCapturePlatform : IHostCapturePlatform
{
    public IReadOnlyList<CaptureTargetDescriptor> GetTargets() =>
        throw new PlatformNotSupportedException("The current host platform is not supported by the capture foundation.");

    public RelayFrame CaptureFrame(HostSessionAssignment assignment, long sequenceId) =>
        throw new PlatformNotSupportedException("The current host platform is not supported by the capture foundation.");

    public void HandlePointerInput(HostSessionAssignment assignment, PointerInputEvent input) =>
        throw new PlatformNotSupportedException("The current host platform is not supported by interactive input.");

    public ModifierKeyState HandleKeyboardInput(
        HostSessionAssignment assignment,
        KeyboardInputEvent input,
        ModifierKeyState currentModifierState) =>
        throw new PlatformNotSupportedException("The current host platform is not supported by interactive input.");

    public void Dispose()
    {
    }
}

internal static class ImageEncoding
{
    public static RelayFrame CreateJpegFrame(string sessionId, long sequenceId, byte[] bgraPixels, int width, int height)
    {
        using var image = Image.LoadPixelData<Bgra32>(bgraPixels, width, height);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder { Quality = 75 });

        return new RelayFrame(
            sessionId,
            sequenceId,
            DateTimeOffset.UtcNow,
            "image/jpeg",
            width,
            height,
            stream.ToArray());
    }
}
