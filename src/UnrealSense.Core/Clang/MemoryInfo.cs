using System;
using System.Runtime.InteropServices;

namespace UnrealSense.Clang
{
    /// <summary>Physical memory figures (Windows); 0 when unavailable.</summary>
    public static class MemoryInfo
    {
        public static long TotalPhysicalBytes => Query()?.ullTotalPhys is ulong v ? (long)v : 0;

        public static long AvailablePhysicalBytes => Query()?.ullAvailPhys is ulong v ? (long)v : 0;

        static MEMORYSTATUSEX? Query()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                return GlobalMemoryStatusEx(ref status) ? status : (MEMORYSTATUSEX?)null;
            }
            catch (Exception)
            {
                return null; // not Windows
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
