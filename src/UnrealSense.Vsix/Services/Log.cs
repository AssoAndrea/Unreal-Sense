using System;
using System.IO;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Writes to the "UnrealSense" Output window pane and to %LOCALAPPDATA%\UnrealSense\Logs\UnrealSense.log
    /// (thread-safe, fire-and-forget; the file is useful when reporting issues).
    /// </summary>
    internal static class Log
    {
        const long MaxLogBytes = 2 * 1024 * 1024;
        static readonly object fileGate = new object();
        static readonly string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealSense", "Logs", "UnrealSense.log");
        static OutputWindowPane pane;
        static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        public static string FilePath => logFile;

        public static void Write(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            WriteToFile(line);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    pane ??= await VS.Windows.CreateOutputWindowPaneAsync("UnrealSense");
                    await pane.WriteLineAsync(line);
                }
                catch (Exception)
                {
                    // Logging must never break the editor.
                }
            }).FireAndForget();
        }

        /// <summary>File-only log for verbose events that would clutter the Output pane.</summary>
        public static void Trace(string message) => WriteToFile($"[{DateTime.Now:HH:mm:ss}] {message}");

        public static void Error(string context, Exception ex) => Write($"{context}: {ex?.GetType().Name}: {ex?.Message}");

        public static void Status(string message)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await VS.StatusBar.ShowMessageAsync(message); }
                catch (Exception) { }
            }).FireAndForget();
        }

        public static OutputWindowPane GetPane()
        {
            return ThreadHelper.JoinableTaskFactory.Run(async () =>
                pane ??= await VS.Windows.CreateOutputWindowPaneAsync("UnrealSense"));
        }

        static void WriteToFile(string line)
        {
            try
            {
                lock (fileGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                    if (File.Exists(logFile) && new FileInfo(logFile).Length > MaxLogBytes)
                        File.Delete(logFile);
                    // The file is shared by every Visual Studio instance: tag lines with the process id.
                    File.AppendAllText(logFile, line.Insert(Math.Min(11, line.Length), $"[{ProcessId}] ") + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }
    }
}
