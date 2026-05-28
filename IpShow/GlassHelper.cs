using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IpShow;

/// <summary>
/// Keeps the overlay visually light on Windows 11, falling back to a very subtle
/// legacy acrylic tint on Windows 10.
/// </summary>
internal static class GlassHelper
{
    // --- Win11 DWM backdrop ---
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // --- Win10 Accent (undocumented) ---
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public static void Apply(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Dark mode title hint (affects border tint on Win11)
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (TryApplyWin11TransparentBackdrop(hwnd))
        {
            return;
        }

        TryApplyLegacyAcrylic(hwnd);
    }

    private static bool TryApplyWin11TransparentBackdrop(IntPtr hwnd)
    {
        // DWMWA_SYSTEMBACKDROP_TYPE requires Windows 11 22H2 (build 22621) or newer.
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 22621)
        {
            return false;
        }

        int backdrop = 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
        {
            return false;
        }

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        return true;
    }

    private static void TryApplyLegacyAcrylic(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy
            {
                // On Win10 1803+ ACRYLIC is supported; older builds fall back to plain blur.
                AccentState = Environment.OSVersion.Version.Build >= 17134
                    ? ACCENT_ENABLE_ACRYLICBLURBEHIND
                    : ACCENT_ENABLE_BLURBEHIND,
                // ABGR: low alpha dark tint so the blurred desktop shows through
                GradientColor = unchecked((int)0x141D1D1D)
            };

            var size = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    SizeOfData = size,
                    Data = ptr
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
        }
    }
}
