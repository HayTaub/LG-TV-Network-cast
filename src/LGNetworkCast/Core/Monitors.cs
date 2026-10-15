using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LGNetworkCast.Core
{
    public class MonitorInfo
    {
        public int Adapter { get; set; }     // DXGI adapter index (for ffmpeg -init_hw_device d3d11va=dx:N)
        public int Output { get; set; }      // DXGI output index on that adapter (ddagrab output_idx)
        public string DeviceName { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Primary { get; set; }
        public int Number { get; set; }      // 1-based number shown to the user

        public string Label => $"Display {Number}";
        public string Detail => $"{Width}×{Height}{(Primary ? " · Main" : "")}";
    }

    /// <summary>Enumerates monitors the way ffmpeg's ddagrab sees them (DXGI adapters/outputs).</summary>
    public static class Monitors
    {
        public static List<MonitorInfo> List()
        {
            var result = new List<MonitorInfo>();
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out var factory) != 0) return Fallback();
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter) == 0; a++)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output) == 0; o++)
                    {
                        output.GetDesc(out var desc);
                        var r = desc.DesktopCoordinates;
                        result.Add(new MonitorInfo
                        {
                            Adapter = (int)a,
                            Output = (int)o,
                            DeviceName = desc.DeviceName,
                            Width = r.Right - r.Left,
                            Height = r.Bottom - r.Top,
                            Primary = r.Left == 0 && r.Top == 0,
                        });
                        Marshal.ReleaseComObject(output);
                    }
                    Marshal.ReleaseComObject(adapter);
                }
                Marshal.ReleaseComObject(factory);
            }
            catch (Exception ex)
            {
                Log.Error("DXGI enumeration failed", ex);
                return Fallback();
            }
            // Number displays like Windows does (\\.\DISPLAY1, \\.\DISPLAY2 ...), left-to-right as a tie breaker.
            var ordered = result.OrderBy(m => DisplayNumber(m.DeviceName)).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].Number = i + 1;
            return ordered;
        }

        static int DisplayNumber(string deviceName) =>
            int.TryParse(new string((deviceName ?? "").Where(char.IsDigit).ToArray()), out var n) ? n : 99;

        static List<MonitorInfo> Fallback() =>
            new List<MonitorInfo> { new MonitorInfo { Adapter = 0, Output = 0, DeviceName = "", Width = 1920, Height = 1080, Primary = true, Number = 1 } };

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        // Only the vtable slots we call need real signatures; the others are placeholders to keep slot order.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
            void GetDesc(); void CheckInterfaceSupport(); void GetDesc1();
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }
    }
}
