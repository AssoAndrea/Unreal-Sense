using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealSense.Remote;
using Xunit;

namespace UnrealSense.Tests
{
    public class RemoteExecutionTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "UnrealSenseRemote_" + Guid.NewGuid().ToString("N"));
        readonly string uproject;

        public RemoteExecutionTests()
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            uproject = Path.Combine(root, "Game.uproject");
        }

        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        string Ini => Path.Combine(root, "Config", "DefaultEngine.ini");

        [Fact]
        public void EnableAddsPluginAndSection()
        {
            File.WriteAllText(uproject, "{\r\n\t\"FileVersion\": 3,\r\n\t\"Plugins\": [\r\n\t\t{\r\n\t\t\t\"Name\": \"ModelingToolsEditorMode\",\r\n\t\t\t\"Enabled\": true\r\n\t\t}\r\n\t]\r\n}\r\n");
            File.WriteAllText(Ini, "[/Script/EngineSettings.GameMapsSettings]\r\nGameDefaultMap=/Game/Map\r\n");
            Assert.False(RemoteExecutionSetup.IsPythonPluginListed(uproject));
            Assert.False(RemoteExecutionSetup.IsRemoteExecutionEnabled(root));

            Assert.Null(RemoteExecutionSetup.Enable(uproject));

            Assert.True(RemoteExecutionSetup.IsPythonPluginListed(uproject));
            Assert.True(RemoteExecutionSetup.IsRemoteExecutionEnabled(root));
            var json = JObject.Parse(File.ReadAllText(uproject));
            Assert.Equal(3, (int)json["FileVersion"]);
            Assert.Equal(2, ((JArray)json["Plugins"]).Count);
            Assert.Contains("\r\n\t\"Plugins\"", File.ReadAllText(uproject));
            Assert.Equal("[/Script/EngineSettings.GameMapsSettings]\r\nGameDefaultMap=/Game/Map\r\n\r\n[/Script/PythonScriptPlugin.PythonScriptPluginSettings]\r\nbRemoteExecution=True\r\n", File.ReadAllText(Ini));
        }

        [Fact]
        public void EnableUpdatesExistingEntries()
        {
            File.WriteAllText(uproject, "{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":false}]}");
            File.WriteAllText(Ini, "[/Script/PythonScriptPlugin.PythonScriptPluginSettings]\nbDeveloperMode=True\nbRemoteExecution=False\n\n[Other]\nA=1\n");

            Assert.Null(RemoteExecutionSetup.Enable(uproject));

            Assert.True(RemoteExecutionSetup.IsPythonPluginListed(uproject));
            Assert.Single((JArray)JObject.Parse(File.ReadAllText(uproject))["Plugins"]);
            Assert.Equal("[/Script/PythonScriptPlugin.PythonScriptPluginSettings]\nbDeveloperMode=True\nbRemoteExecution=True\n\n[Other]\nA=1\n", File.ReadAllText(Ini));
        }

        [Fact]
        public void EnableAddsKeyToExistingSection()
        {
            File.WriteAllText(uproject, "{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":true}]}");
            File.WriteAllText(Ini, "[/Script/PythonScriptPlugin.PythonScriptPluginSettings]\r\nbDeveloperMode=True\r\n\r\n[Other]\r\nA=1\r\n");

            Assert.Null(RemoteExecutionSetup.Enable(uproject));

            Assert.Equal("[/Script/PythonScriptPlugin.PythonScriptPluginSettings]\r\nbDeveloperMode=True\r\nbRemoteExecution=True\r\n\r\n[Other]\r\nA=1\r\n", File.ReadAllText(Ini));
        }

        [Fact]
        public void EnableMakesReadOnlyFilesWritableAndReportsThem()
        {
            File.WriteAllText(uproject, "{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":true}]}");
            File.WriteAllText(Ini, "");
            File.SetAttributes(uproject, FileAttributes.ReadOnly);
            File.SetAttributes(Ini, FileAttributes.ReadOnly);
            var madeWritable = new System.Collections.Generic.List<string>();

            Assert.Null(RemoteExecutionSetup.Enable(uproject, madeWritable));

            Assert.True(RemoteExecutionSetup.IsRemoteExecutionEnabled(root));
            Assert.Equal(new[] { Ini }, madeWritable);
            Assert.False(new FileInfo(Ini).IsReadOnly);
            // The .uproject did not need changes: it stays read-only.
            Assert.True(new FileInfo(uproject).IsReadOnly);
        }

        [Fact]
        public void OpenAssetScriptEscapesThePath()
        {
            var script = RemoteExecutionClient.OpenAssetScript("/Game/It's \"odd\"\\BP.BP");
            Assert.Contains("unreal.load_asset(\"/Game/It's \\\"odd\\\"\\\\BP.BP\")", script);
            Assert.Contains("open_editor_for_assets([asset])", script);
        }

        [Fact]
        public void FindsNodeOfTheProject()
        {
            var nodes = new[]
            {
                new RemoteNode { NodeId = "a", ProjectRoot = "C:/Other/" },
                new RemoteNode { NodeId = "b", ProjectRoot = root.Replace('\\', '/') + "/" },
            };
            Assert.Equal("b", RemoteExecutionClient.FindNodeForProject(nodes, root)?.NodeId);
            Assert.Null(RemoteExecutionClient.FindNodeForProject(nodes, @"C:\Missing"));
        }

        /// <summary>Talks to an in-process stand-in for the editor that follows the documented protocol.</summary>
        [Fact]
        public async Task RunsCommandThroughFakeEditor()
        {
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                string received = null;
                var editor = Task.Run(() => FakeEditor(root, command => { received = command; return "None"; }, cancel.Token));

                var client = new RemoteExecutionClient();
                var nodes = await client.DiscoverAsync(TimeSpan.FromSeconds(1.5));
                var node = RemoteExecutionClient.FindNodeForProject(nodes, root);
                Assert.NotNull(node);
                Assert.Equal("FakeGame", node.ProjectName);

                var result = await client.RunAsync(node, "print('hi')", TimeSpan.FromSeconds(10));
                Assert.True(result.Success);
                Assert.Equal("None", result.Result);
                Assert.Equal("hi", result.Output);
                Assert.Equal("print('hi')", received);
                cancel.Cancel();
                await editor;
            }
        }

        static void FakeEditor(string projectRoot, Func<string, string> run, CancellationToken token)
        {
            var id = Guid.NewGuid().ToString();
            var group = new IPEndPoint(IPAddress.Parse("239.0.0.1"), 6766);
            using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, group.Port));
                udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);
                udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
                udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group.Address, IPAddress.Loopback));
                udp.ReceiveTimeout = 100;
                var buffer = new byte[65536];
                while (!token.IsCancellationRequested)
                {
                    int count;
                    try { count = udp.Receive(buffer); }
                    catch (SocketException) { continue; }
                    var message = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, count));
                    var source = (string)message["source"];
                    if (source == id || ((string)message["dest"] ?? id) != id) continue;
                    switch ((string)message["type"])
                    {
                        case "ping":
                            Send(udp, group, new JObject
                            {
                                ["version"] = 1, ["magic"] = "ue_py", ["type"] = "pong", ["source"] = id, ["dest"] = source,
                                ["data"] = new JObject { ["user"] = "u", ["machine"] = "m", ["engine_version"] = "5.8.0", ["engine_root"] = "C:/UE", ["project_root"] = projectRoot.Replace('\\', '/') + "/", ["project_name"] = "FakeGame" },
                            });
                            break;
                        case "open_connection":
                            using (var tcp = new TcpClient())
                            {
                                tcp.Connect((string)message["data"]["command_ip"], (int)message["data"]["command_port"]);
                                var stream = tcp.GetStream();
                                count = stream.Read(buffer, 0, buffer.Length);
                                var command = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, count));
                                var reply = new JObject
                                {
                                    ["version"] = 1, ["magic"] = "ue_py", ["type"] = "command_result", ["source"] = id, ["dest"] = source,
                                    ["data"] = new JObject
                                    {
                                        ["success"] = true, ["command"] = command["data"]["command"], ["result"] = run((string)command["data"]["command"]),
                                        ["output"] = new JArray(new JObject { ["type"] = "Info", ["output"] = "hi" }),
                                    },
                                };
                                var bytes = Encoding.UTF8.GetBytes(reply.ToString(Formatting.None));
                                stream.Write(bytes, 0, bytes.Length);
                            }
                            break;
                    }
                }
            }
        }

        static void Send(Socket socket, IPEndPoint group, JObject message) =>
            socket.SendTo(Encoding.UTF8.GetBytes(message.ToString(Formatting.None)), group);
    }
}
