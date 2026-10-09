using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealSense.Project
{
    /// <summary>
    /// Real paths versus the paths the user works with. A drive created with subst, a junction or a symbolic link gives
    /// the same file two names; the own indexer and the Unreal Editor report real paths, which are given back under the
    /// folder the user knows (and the same project must never look like two).
    /// </summary>
    public static class RealPaths
    {
        static readonly ConcurrentDictionary<string, string> canonicalPaths = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The directory's real path (case on disk, links and substituted drives resolved) plus the file name as it is on
        /// disk.
        /// </summary>
        public static string CanonicalPath(string path) => canonicalPaths.GetOrAdd(Path.GetFullPath(path), full =>
        {
            try
            {
                string real;
                if (Directory.Exists(full)) real = RealDirectory(full);
                else
                {
                    var directory = Path.GetDirectoryName(full);
                    if (directory == null || !Directory.Exists(directory)) return full;
                    var name = Path.GetFileName(full);
                    var onDisk = Directory.EnumerateFiles(directory, name).FirstOrDefault();
                    real = Path.Combine(RealDirectory(directory), onDisk != null ? Path.GetFileName(onDisk) : name);
                }
                RememberView(full, real);
                return real;
            }
            catch (Exception) { return full; }
        });

        // Real path prefix -> the prefix the user works with (e.g. "D:\workspaces" -> "S:" for a substituted drive).
        static readonly List<(string Real, string View)> viewPrefixes = new List<(string, string)>();

        /// <summary>Records how a path the user works with maps to its real path (see <see cref="ToViewPath"/>).</summary>
        static void RememberView(string view, string real)
        {
            if (string.Equals(view, real, StringComparison.Ordinal)) return;
            var v = view.TrimEnd('\\').Split('\\');
            var r = real.TrimEnd('\\').Split('\\');
            int common = 0;
            while (common < v.Length && common < r.Length && string.Equals(v[v.Length - 1 - common], r[r.Length - 1 - common], StringComparison.OrdinalIgnoreCase))
                common++;
            var viewPrefix = string.Join("\\", v.Take(v.Length - common));
            var realPrefix = string.Join("\\", r.Take(r.Length - common));
            if (realPrefix.Length == 0 || viewPrefix.Length == 0 || string.Equals(viewPrefix, realPrefix, StringComparison.OrdinalIgnoreCase)) return;
            lock (viewPrefixes)
            {
                if (viewPrefixes.Any(p => string.Equals(p.Real, realPrefix, StringComparison.OrdinalIgnoreCase))) return;
                viewPrefixes.Add((realPrefix, viewPrefix));
                viewPrefixes.Sort((a, b) => b.Real.Length.CompareTo(a.Real.Length));
            }
        }

        /// <summary>A real path as the user works with it (needs a <see cref="CanonicalPath"/> of that folder first).</summary>
        public static string ToViewPath(string realPath)
        {
            lock (viewPrefixes)
                foreach (var (real, view) in viewPrefixes)
                    if (realPath.StartsWith(real + "\\", StringComparison.OrdinalIgnoreCase))
                        return view + realPath.Substring(real.Length);
            return realPath;
        }

        static string RealDirectory(string directory)
        {
            var handle = NativeMethods.CreateFileW(directory, 0, 7 /* read | write | delete sharing */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: needed to open a directory */, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return directory;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                uint length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return directory;
                var real = buffer.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + real.Substring(8);
                if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) return real.Substring(4);
                return real;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            public static extern uint GetFinalPathNameByHandleW(IntPtr handle, System.Text.StringBuilder path, uint length, uint flags);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
