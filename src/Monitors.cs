using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ReelCap;

public sealed class MonitorInfo
{
    public string DeviceName = "";      // \\.\DISPLAY1
    public string FriendlyName = "";    // from the EDID, e.g. "DELL S2721DGF"
    public int X, Y, Width, Height;     // physical desktop pixels
    public int RefreshHz;
    public bool Primary;
    public int DdaIndex = -1;           // ffmpeg ddagrab output_idx, -1 = not on the default adapter (use gdigrab)
    public int Number;                  // 1-based, in display order
    public IntPtr Handle;               // HMONITOR, for gfxcapture

    public string Title => string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {Number}" : FriendlyName;
    public string Detail => $"{Width} x {Height}" + (RefreshHz > 0 ? $"  ·  {RefreshHz} Hz" : "");
}

/// <summary>Enumerates monitors the way ffmpeg's ddagrab does (DXGI outputs of the default adapter) and
/// adds names and refresh rates from the display config.</summary>
public static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        var guid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref guid, out var factory) != 0) return list;
        try
        {
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter) == 0; a++)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output) == 0; o++)
                {
                    output.GetDesc(out var desc);
                    Marshal.ReleaseComObject(output);
                    if (desc.AttachedToDesktop == 0) continue;
                    if (list.Any(m => m.DeviceName == desc.DeviceName)) continue;
                    list.Add(new MonitorInfo
                    {
                        DeviceName = desc.DeviceName,
                        X = desc.Left, Y = desc.Top,
                        Width = desc.Right - desc.Left, Height = desc.Bottom - desc.Top,
                        Primary = desc.Left == 0 && desc.Top == 0,
                        DdaIndex = a == 0 ? (int)o : -1,
                        Handle = desc.Monitor,
                    });
                }
                Marshal.ReleaseComObject(adapter);
            }
        }
        finally { Marshal.ReleaseComObject(factory); }

        try { AddNames(list); } catch { /* names are cosmetic */ }
        list.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
        return list;
    }

    /// <summary>Grabs a downscaled snapshot of the monitor for its card.</summary>
    public static BitmapSource? Thumbnail(MonitorInfo m, int width)
    {
        int height = Math.Max(1, width * m.Height / Math.Max(1, m.Width));
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(mem, bmp);
            SetStretchBltMode(mem, 4); // HALFTONE
            StretchBlt(mem, 0, 0, width, height, screen, m.X, m.Y, m.Width, m.Height, 0x00CC0020 | 0x40000000);
            SelectObject(mem, old);
            // GDI leaves alpha at 0; reading it as Bgr32 keeps the snapshot opaque.
            var src = new FormatConvertedBitmap(
                Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
                System.Windows.Media.PixelFormats.Bgr32, null, 0);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static void AddNames(List<MonitorInfo> list)
    {
        if (GetDisplayConfigBufferSizes(2, out var nPaths, out var nModes) != 0) return; // QDC_ONLY_ACTIVE_PATHS
        var paths = new DISPLAYCONFIG_PATH_INFO[nPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[nModes];
        if (QueryDisplayConfig(2, ref nPaths, paths, ref nModes, modes, IntPtr.Zero) != 0) return;
        for (int i = 0; i < nPaths; i++)
        {
            var p = paths[i];
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = p.sourceAdapter, id = p.sourceId },
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
            var m = list.FirstOrDefault(x => x.DeviceName == src.viewGdiDeviceName);
            if (m == null) continue;
            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = p.targetAdapter, id = p.targetId },
            };
            if (DisplayConfigGetDeviceInfo(ref tgt) == 0) m.FriendlyName = tgt.monitorFriendlyDeviceName;
            if (p.refreshDen != 0) m.RefreshHz = (int)Math.Round((double)p.refreshNum / p.refreshDen);
        }
    }

    // ---- DXGI ----

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // ---- Display config ----

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID sourceAdapter; public uint sourceId, sourceModeIdx, sourceFlags;
        public LUID targetAdapter; public uint targetId, targetModeIdx, outputTechnology, rotation, scaling;
        public uint refreshNum, refreshDen, scanLineOrdering; public int targetAvailable; public uint targetFlags;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)] struct DISPLAYCONFIG_MODE_INFO { public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    // ---- GDI ----

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
}
