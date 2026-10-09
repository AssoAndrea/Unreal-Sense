using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Project;

namespace UnrealSense.Remote
{
    /// <summary>An Unreal Editor instance that answered a remote execution "ping".</summary>
    public sealed class RemoteNode
    {
        public string NodeId { get; set; }
        public string ProjectRoot { get; set; }
        public string ProjectName { get; set; }
        public string EngineRoot { get; set; }
        public string EngineVersion { get; set; }
        public string Machine { get; set; }

        public override string ToString() => $"{ProjectName ?? "?"} ({ProjectRoot ?? "no project"}, engine {EngineVersion ?? "?"}, node {NodeId})";
    }

    /// <summary>Outcome of a Python command run in the editor.</summary>
    public sealed class RemoteCommandResult
    {
        public bool Success { get; set; }
        public string Result { get; set; }
        public string Output { get; set; }
    }

    /// <summary>
    /// Client for the Python Script Plugin's remote execution protocol (the one Epic's remote_execution.py uses):
    /// UTF-8 JSON messages, discovery over a host-local UDP multicast group, then a TCP connection that the editor
    /// opens to us and that carries "command" / "command_result". Requires the PythonScriptPlugin and
    /// "Enable Remote Execution?" in the project settings.
    /// </summary>
    public sealed class RemoteExecutionClient
    {
        const int ProtocolVersion = 1;
        const string ProtocolMagic = "ue_py";
        // Defaults of UPythonScriptPluginSettings (Multicast Group Endpoint / Bind Address / TTL 0 = this machine only).
        static readonly IPEndPoint MulticastGroup = new IPEndPoint(IPAddress.Parse("239.0.0.1"), 6766);
        static readonly IPAddress BindAddress = IPAddress.Loopback;

        readonly string nodeId = Guid.NewGuid().ToString();

        /// <summary>Running editor processes (any configuration: UnrealEditor.exe, UnrealEditor-Win64-DebugGame.exe...).</summary>
        public static List<Process> FindEditorProcesses()
        {
            var result = new List<Process>();
            foreach (var process in Process.GetProcesses())
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception) { process.Dispose(); continue; }
                // UnrealEditor-Cmd runs commandlets (cooks, builds): no UI to open an asset in.
                if (name.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase) && name.IndexOf("-Cmd", StringComparison.OrdinalIgnoreCase) < 0)
                    result.Add(process);
                else process.Dispose();
            }
            return result;
        }

        /// <summary>Pings the multicast group for <paramref name="wait"/> and returns the editors that answered.</summary>
        public Task<List<RemoteNode>> DiscoverAsync(TimeSpan wait, CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            var nodes = new Dictionary<string, RemoteNode>();
            using (var socket = OpenMulticastSocket())
            {
                var buffer = new byte[65536];
                var deadline = DateTime.UtcNow + wait;
                var nextPing = DateTime.MinValue;
                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= nextPing)
                    {
                        Send(socket, Message("ping"));
                        nextPing = DateTime.UtcNow.AddMilliseconds(300);
                    }
                    var message = Receive(socket, buffer);
                    if (message == null || (string)message["type"] != "pong") continue;
                    var data = message["data"] as JObject;
                    var source = (string)message["source"];
                    nodes[source] = new RemoteNode
                    {
                        NodeId = source,
                        ProjectRoot = (string)data?["project_root"],
                        ProjectName = (string)data?["project_name"],
                        EngineRoot = (string)data?["engine_root"],
                        EngineVersion = (string)data?["engine_version"],
                        Machine = (string)data?["machine"],
                    };
                }
            }
            return nodes.Values.ToList();
        }, cancellationToken);

        /// <summary>The node whose project is <paramref name="projectDirectory"/> (also through a substituted drive).</summary>
        public static RemoteNode FindNodeForProject(IEnumerable<RemoteNode> nodes, string projectDirectory)
        {
            string Normalize(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                try { return RealPaths.CanonicalPath(path.Replace('/', '\\').TrimEnd('\\')).TrimEnd('\\'); }
                catch (Exception) { return path.Replace('/', '\\').TrimEnd('\\'); }
            }
            var wanted = Normalize(projectDirectory);
            var plain = projectDirectory?.Replace('/', '\\').TrimEnd('\\');
            return nodes.FirstOrDefault(n =>
            {
                var root = n.ProjectRoot?.Replace('/', '\\').TrimEnd('\\');
                return root != null && (string.Equals(root, plain, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(Normalize(root), wanted, StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// Runs a Python script in the editor: we listen on an ephemeral loopback TCP port, ask the node over UDP to
        /// connect to it, send the command and wait for its result.
        /// </summary>
        public Task<RemoteCommandResult> RunAsync(RemoteNode node, string python, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            var listener = new TcpListener(BindAddress, 0);
            listener.Start(1);
            try
            {
                using (var udp = OpenMulticastSocket())
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var open = Message("open_connection", node.NodeId, new JObject { ["command_ip"] = BindAddress.ToString(), ["command_port"] = port });
                    var deadline = DateTime.UtcNow + timeout;

                    // The editor handles "open_connection" on its next tick: repeat it until it connects.
                    var accept = listener.AcceptTcpClientAsync();
                    while (!accept.IsCompleted)
                    {
                        if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
                            throw new TimeoutException("The Unreal Editor did not open the command connection.");
                        Send(udp, open);
                        await Task.WhenAny(accept, Task.Delay(500)).ConfigureAwait(false);
                    }

                    try
                    {
                        using (var client = await accept.ConfigureAwait(false))
                        using (var stream = client.GetStream())
                        {
                            var command = Message("command", node.NodeId, new JObject { ["command"] = python, ["unattended"] = true, ["exec_mode"] = "ExecuteFile" });
                            var bytes = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
                            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);

                            var reply = await ReadMessageAsync(stream, deadline, cancellationToken).ConfigureAwait(false);
                            if ((string)reply["type"] != "command_result")
                                throw new IOException($"Unexpected reply from the Unreal Editor: {(string)reply["type"]}");
                            var data = reply["data"] as JObject;
                            return new RemoteCommandResult
                            {
                                Success = (bool?)data?["success"] ?? false,
                                Result = (string)data?["result"],
                                Output = string.Join("\n", (data?["output"] as JArray ?? new JArray()).Select(o => (string)o["output"]).Where(o => !string.IsNullOrEmpty(o))),
                            };
                        }
                    }
                    finally
                    {
                        Send(udp, Message("close_connection", node.NodeId));
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }, cancellationToken);

        /// <summary>Python that opens the asset editor for an object path (e.g. /Game/BP/BP_Foo.BP_Foo).</summary>
        public static string OpenAssetScript(string objectPath)
        {
            // A JSON string literal is a valid Python string literal (escapes included).
            var literal = JsonConvert.ToString(objectPath);
            return "import unreal\n"
                 + $"asset = unreal.load_asset({literal})\n"
                 + "if asset is None:\n"
                 + $"    raise RuntimeError('Asset not found: ' + {literal})\n"
                 + "unreal.get_editor_subsystem(unreal.AssetEditorSubsystem).open_editor_for_assets([asset])\n";
        }

        static async Task<JObject> ReadMessageAsync(Stream stream, DateTime deadline, CancellationToken cancellationToken)
        {
            // Messages are not framed: one JSON object per write. Read until what we have parses.
            var received = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The Unreal Editor did not answer the command in time.");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(remaining);
                    var read = await Task.WhenAny(stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token), Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                    if (read.IsCanceled) throw new TimeoutException("The Unreal Editor did not answer the command in time.");
                    int count = await ((Task<int>)read).ConfigureAwait(false);
                    if (count == 0) throw new IOException("The Unreal Editor closed the command connection.");
                    received.Write(buffer, 0, count);
                }
                var message = TryParse(received.ToArray(), received.Length);
                if (message != null) return message;
            }
        }

        static Socket OpenMulticastSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // The editor (and other tools) are bound to the same port: share it.
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(BindAddress, MulticastGroup.Port));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, BindAddress.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup.Address, BindAddress));
                socket.ReceiveTimeout = 100;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        JObject Message(string type, string dest = null, JObject data = null)
        {
            var message = new JObject { ["version"] = ProtocolVersion, ["magic"] = ProtocolMagic, ["type"] = type, ["source"] = nodeId };
            if (dest != null) message["dest"] = dest;
            if (data != null) message["data"] = data;
            return message;
        }

        static void Send(Socket socket, JObject message) =>
            socket.SendTo(Encoding.UTF8.GetBytes(message.ToString(Formatting.None)), MulticastGroup);

        JObject Receive(Socket socket, byte[] buffer)
        {
            int count;
            try { count = socket.Receive(buffer); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut || e.SocketErrorCode == SocketError.WouldBlock) { return null; }
            var message = TryParse(buffer, count);
            // Our own pings come back (loopback); messages for other clients are not ours either.
            if (message == null || (string)message["source"] == nodeId) return null;
            var dest = (string)message["dest"];
            return string.IsNullOrEmpty(dest) || dest == nodeId ? message : null;
        }

        static JObject TryParse(byte[] bytes, long count)
        {
            try
            {
                var message = JObject.Parse(Encoding.UTF8.GetString(bytes, 0, (int)count));
                return (int?)message["version"] == ProtocolVersion && (string)message["magic"] == ProtocolMagic ? message : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
