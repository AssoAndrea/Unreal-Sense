using System.Threading;
using System.Threading.Tasks;

namespace UnrealSense.Extension.Services
{
    internal static class TaskExtensions
    {
        public static void FireAndForgetLogged(this Task task, string context)
        {
            task.ContinueWith(t => Log.Error(context, t.Exception?.GetBaseException()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
