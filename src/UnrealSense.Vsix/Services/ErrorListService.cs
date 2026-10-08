using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using UnrealSense.Analysis;
using UnrealSense.Extension.Options;

namespace UnrealSense.Extension.Services
{
    /// <summary>Publishes inspection errors/warnings of open documents to the Error List.</summary>
    internal static class ErrorListService
    {
        static ErrorListProvider provider;
        static readonly Dictionary<string, List<ErrorTask>> tasksByFile = new Dictionary<string, List<ErrorTask>>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            provider = new ErrorListProvider(serviceProvider) { ProviderName = "UnrealSense", ProviderGuid = new Guid("5d2c7a1e-8b3f-4e9a-a1c6-7f0e3b5d9c44") };
        }

        public static void Update(string filePath, ITextSnapshot snapshot, IReadOnlyList<Diagnostic> diagnostics)
        {
            if (provider == null) return;
            var items = diagnostics
                .Where(d => d.Severity != DiagnosticSeverity.Suggestion)
                .Select(d => (d, line: snapshot.GetLineFromPosition(Math.Min(d.Start, snapshot.Length))))
                .ToList();

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Remove(filePath);
                if (!General.Instance.ShowInErrorList) return;

                var tasks = new List<ErrorTask>();
                foreach (var (d, line) in items)
                {
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.CodeSense,
                        ErrorCategory = d.Severity == DiagnosticSeverity.Error ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                        Text = $"{d.Id}: {d.Message}",
                        Document = filePath,
                        Line = line.LineNumber,
                        Column = d.Start - line.Start.Position,
                    };
                    task.Navigate += (s, e) =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        var t = (ErrorTask)s;
                        EditorNavigation.OpenAtLineAsync(t.Document, t.Line, t.Column).FireAndForget();
                    };
                    tasks.Add(task);
                    provider.Tasks.Add(task);
                }
                tasksByFile[filePath] = tasks;
            }).FireAndForget();
        }

        public static void Clear(string filePath)
        {
            if (provider == null) return;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Remove(filePath);
            }).FireAndForget();
        }

        static void Remove(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!tasksByFile.TryGetValue(filePath, out var tasks)) return;
            provider.SuspendRefresh();
            foreach (var task in tasks) provider.Tasks.Remove(task);
            provider.ResumeRefresh();
            tasksByFile.Remove(filePath);
        }
    }
}
