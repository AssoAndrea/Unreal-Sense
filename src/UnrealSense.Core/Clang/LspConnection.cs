using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnrealSense.Clang
{
    /// <summary>Minimal JSON-RPC 2.0 / LSP base-protocol connection over a pair of streams.</summary>
    public sealed class LspConnection : IDisposable
    {
        readonly Stream input;
        readonly Stream output;
        readonly ConcurrentDictionary<int, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JToken>>();
        readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        readonly Thread readerThread;
        int nextId;
        volatile bool disposed;

        public LspConnection(Stream input, Stream output)
        {
            this.input = input;
            this.output = output;
            readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "UnrealSense LSP reader" };
            readerThread.Start();
        }

        /// <summary>Server → client notifications (method, params). Raised on the reader thread.</summary>
        public event Action<string, JToken> Notification;

        /// <summary>Server → client requests; return the result (null is fine for most).</summary>
        public Func<string, JToken, JToken> RequestHandler { get; set; }

        public event Action<Exception> Closed;

        public async Task<JToken> RequestAsync(string method, object parameters, CancellationToken cancellationToken = default)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            using (cancellationToken.Register(() =>
            {
                if (pending.TryRemove(id, out var t))
                {
                    _ = SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest", ["params"] = new JObject { ["id"] = id } });
                    t.TrySetCanceled();
                }
            }))
            {
                await SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = ToToken(parameters) }).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task NotifyAsync(string method, object parameters) =>
            SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = ToToken(parameters) });

        static JToken ToToken(object value) => value == null ? JValue.CreateNull() : value as JToken ?? JToken.FromObject(value);

        async Task SendAsync(JObject message)
        {
            if (disposed) throw new ObjectDisposedException(nameof(LspConnection));
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await output.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                await output.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        void ReadLoop()
        {
            Exception error = null;
            try
            {
                while (!disposed)
                {
                    int length = ReadHeaders();
                    if (length < 0) break;
                    var buffer = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = input.Read(buffer, read, length - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }
                    Dispatch(JObject.Parse(Encoding.UTF8.GetString(buffer)));
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            foreach (var p in pending.Values) p.TrySetException(error ?? new EndOfStreamException("LSP server exited."));
            pending.Clear();
            if (!disposed) Closed?.Invoke(error);
        }

        int ReadHeaders()
        {
            int length = -1;
            var line = new StringBuilder();
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0) return -1;
                if (b == '\n')
                {
                    var text = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (text.Length == 0) return length;
                    if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(text.Substring("Content-Length:".Length).Trim());
                }
                else
                {
                    line.Append((char)b);
                }
            }
        }

        void Dispatch(JObject message)
        {
            var id = message["id"];
            var method = (string)message["method"];
            if (method != null && id != null)
            {
                JToken result = null;
                try { result = RequestHandler?.Invoke(method, message["params"]); }
                catch (Exception) { }
                _ = SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? JValue.CreateNull() });
            }
            else if (method != null)
            {
                Notification?.Invoke(method, message["params"]);
            }
            else if (id != null && pending.TryRemove((int)id, out var tcs))
            {
                if (message["error"] is JObject err)
                    tcs.TrySetException(new InvalidOperationException($"LSP error {(int?)err["code"]}: {(string)err["message"]}"));
                else
                    tcs.TrySetResult(message["result"]);
            }
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var p in pending.Values) p.TrySetCanceled();
        }
    }
}
