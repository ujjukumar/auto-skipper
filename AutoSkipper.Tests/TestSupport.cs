using System.Reflection;
using System.Runtime.InteropServices;
using AutoSkipper.Interfaces;

// Several tests share static state (the logger, WinForms), so run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace AutoSkipper.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoSkipper.Tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

// Records every call so tests can assert the exact input sequence.
internal sealed class FakeInputSender : IInputSender
{
    private readonly List<string> _events = new();

    public string Events { get { lock (_events) return string.Join(" ", _events); } }
    public int Count(string e) { lock (_events) return _events.Count(x => x == e); }
    public void Clear() { lock (_events) _events.Clear(); }

    private void Add(string e) { lock (_events) _events.Add(e); }
    public void TapF() => Add("TapF");
    public void LeftClick() => Add("LeftClick");
    public void KeyDown(ushort vk) => Add($"KD{vk:X2}");
    public void KeyUp(ushort vk) => Add($"KU{vk:X2}");
    public void MouseDown(ushort vk) => Add($"MD{vk:X2}");
    public void MouseUp(ushort vk) => Add($"MU{vk:X2}");
}

internal sealed class TestLogger : ILogger
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }
    private void Add(string m) { lock (_messages) _messages.Add(m); }

    public void Log(string message) => Add(message);
    public void LogWarning(string message) => Add(message);
    public void LogError(string message) => Add(message);
    public void LogSuccess(string message) => Add(message);
    public void LogDebug(Func<string> message) { }
    public bool IsVerbose => false;
}

// A 32bpp memory bitmap with a device context, standing in for the screen so
// pixel detection can be tested without reading the real display.
internal sealed class MemoryDc : IDisposable
{
    public IntPtr Hdc { get; }
    private readonly IntPtr _bitmap;
    private readonly IntPtr _bits;
    private readonly int _width;
    private readonly int _height;

    public MemoryDc(int width, int height)
    {
        _width = width;
        _height = height;
        Hdc = CreateCompatibleDC(IntPtr.Zero);
        var header = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        _bitmap = CreateDIBSection(Hdc, ref header, 0, out _bits, IntPtr.Zero, 0);
        SelectObject(Hdc, _bitmap);
        Fill(32, 32, 32);
    }

    public void Fill(int r, int g, int b)
    {
        for (int i = 0; i < _width * _height; i++) Marshal.WriteInt32(_bits, i * 4, (r << 16) | (g << 8) | b);
    }

    public void SetPixel(int x, int y, int r, int g, int b) => Marshal.WriteInt32(_bits, (y * _width + x) * 4, (r << 16) | (g << 8) | b);

    public void Dispose()
    {
        DeleteDC(Hdc);
        DeleteObject(_bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}

internal static class TestHelpers
{
    // WinForms controls need an STA thread; exceptions are rethrown to the test.
    public static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Xunit.Sdk.XunitException($"STA test failed: {error}");
    }

    // Polls (pumping WinForms messages if on a UI thread) until the condition holds.
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    // Private UI members are reached by name; fails clearly if a field is renamed.
    public static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name);
        return (T)field.GetValue(target)!;
    }

    public static object? Invoke(object target, string method, params object?[] args)
    {
        var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        return info.Invoke(target, args);
    }
}
