using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnrealSense.Extension.Services
{
    /// <summary>
    /// Machine-wide lock on one clangd index folder, so two Visual Studio instances open on the same project do
    /// not both run clangd's background index over the whole engine (double CPU and memory, same shards written
    /// twice). Backed by a named mutex held by a dedicated thread (mutexes are thread-affine, async code is not);
    /// if the owning Visual Studio dies, Windows abandons the mutex and the next instance takes over.
    /// </summary>
    internal sealed class IndexLock : IDisposable
    {
        readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
        readonly TaskCompletionSource<bool> acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        IndexLock(string directory) => Directory = directory;

        public string Directory { get; }

        /// <summary>Completes once this process owns the lock; <paramref name="onWait"/> runs if another process has it.</summary>
        public static async Task<IndexLock> AcquireAsync(string directory, Action onWait)
        {
            var result = new IndexLock(directory);
            var name = "Global\\UnrealSense-clangd-" + Hash(directory.ToUpperInvariant());
            var thread = new Thread(() => result.Hold(name, onWait)) { IsBackground = true, Name = "UnrealSense index lock" };
            thread.Start();
            await result.acquired.Task.ConfigureAwait(false);
            return result;
        }

        void Hold(string name, Action onWait)
        {
            Mutex mutex;
            try { mutex = new Mutex(false, name); }
            catch (UnauthorizedAccessException) { mutex = new Mutex(false, name.Replace("Global\\", "Local\\")); }
            using (mutex)
            {
                bool owned;
                try
                {
                    owned = mutex.WaitOne(0);
                    if (!owned)
                    {
                        onWait?.Invoke();
                        owned = mutex.WaitOne();
                    }
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // the previous owner exited without releasing it
                }
                acquired.TrySetResult(true);
                release.Wait();
                if (owned) mutex.ReleaseMutex();
            }
        }

        public void Dispose() => release.Set();

        static string Hash(string text)
        {
            uint hash = 2166136261;
            foreach (var c in text) hash = (hash ^ c) * 16777619;
            return hash.ToString("x8");
        }
    }
}
