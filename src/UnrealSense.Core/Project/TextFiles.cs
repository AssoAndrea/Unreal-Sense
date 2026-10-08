using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnrealSense.Project
{
    /// <summary>Reads and writes project text files (.uproject, .ini, .Build.cs) keeping their encoding.</summary>
    public static class TextFiles
    {
        /// <summary>Text and encoding of a file: UTF-16 or UTF-8 with BOM as found, otherwise UTF-8 without BOM.</summary>
        public static (string Text, Encoding Encoding) Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
            return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(false));
        }

        /// <summary>
        /// Clears the read-only flag Perforce sets on files that are not checked out, recording the path so the user
        /// can be told to check it out.
        /// </summary>
        public static void MakeWritable(string path, List<string> madeWritable)
        {
            var info = new FileInfo(path);
            if (!info.Exists || !info.IsReadOnly) return;
            info.IsReadOnly = false;
            madeWritable?.Add(path);
        }
    }
}
