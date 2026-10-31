using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Imp2Net
{
    internal static class DPlayConstants
    {
        public const int DP_OK = 0;
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

        public const int DPERR_GENERIC = unchecked((int)0x80004005);
        public const int DPERR_UNSUPPORTED = unchecked((int)0x80004001);
        public const int DPERR_INVALIDPARAM = unchecked((int)0x80070057);
        public const int DPERR_BUFFERTOOSMALL = unchecked((int)0x8877001E);
        public const int DPERR_INVALIDPLAYER = unchecked((int)0x88770096);
        public const int DPERR_NOMESSAGES = unchecked((int)0x887700BE);
        public const int DPERR_NOSESSIONS = unchecked((int)0x887700C8);
        public const int DPERR_SESSIONLOST = unchecked((int)0x8877012C);
        public const int DPERR_NOCONNECTION = unchecked((int)0x887700AA);
        public const int DPERR_NOTLOBBIED = unchecked((int)0x8877010E);

        public const uint DPOPEN_JOIN = 0x00000001;
        public const uint DPOPEN_CREATE = 0x00000002;

        public const uint DPSESSION_NEWPLAYERSDISABLED = 0x00000001;
        public const uint DPSESSION_JOINDISABLED = 0x00000020;

        public const uint DPENUMPLAYERS_LOCAL = 0x00000008;
        public const uint DPENUMPLAYERS_REMOTE = 0x00000010;

        public const uint DPPLAYERTYPE_PLAYER = 1;
        public const uint DPID_SYSMSG = 0;
        public const uint DPID_ALLPLAYERS = 0;

        public const uint DPSYS_CREATEPLAYERORGROUP = 0x0003;
        public const uint DPSYS_DESTROYPLAYERORGROUP = 0x0005;
        public const uint DPSYS_SESSIONLOST = 0x0021;
        public const uint DPSYS_SETSESSIONDESC = 0x0104;

        public static readonly Guid CLSID_DirectPlay = new Guid("d1eb6d20-8923-11d0-9d97-00a0c90a43cb");
        public static readonly Guid IID_IDirectPlay4A = new Guid("0ab1c531-4745-11d1-a7a1-0000f803abfc");
        public static readonly Guid IID_IDirectPlay3A = new Guid("133efe41-32dc-11d0-9cfb-00a0c90a43cb");
        public static readonly Guid IID_IDirectPlay2A = new Guid("2b74f7c0-9154-11cf-a9cd-00aa006886e3");
        public static readonly Guid CLSID_DirectPlayLobby = new Guid("2fe8f810-b2a5-11d0-a787-0000f803abfc");
        public static readonly Guid IID_IDirectPlayLobby3A = new Guid("2db72491-652c-11d1-a7a8-0000f803abfc");
        public static readonly Guid IID_IDirectPlayLobby2A = new Guid("0194c220-a303-11d0-9c4f-00a0c905425e");
        public static readonly Guid IID_IDirectPlayLobbyA = new Guid("af465c71-9588-11cf-a020-00aa006157ac");
        public static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-c000-000000000046");
        public static readonly Guid IID_IClassFactory = new Guid("00000001-0000-0000-c000-000000000046");

        public static readonly Guid DPSPGUID_TCPIP = new Guid("36e95ee0-8577-11cf-960c-0080c7534e82");
        public static readonly Guid DPAID_TotalSize = new Guid("1318f560-912c-11cf-9c4e-00a0c905425e");
        public static readonly Guid DPAID_ServiceProvider = new Guid("07d916c0-e0af-11cf-9c4e-00a0c905425e");
        public static readonly Guid DPAID_INet = new Guid("c4a54da0-e0af-11cf-9c4e-00a0c905425e");

        public const uint MAGIC = 0x32504D49; // "IMP2"
        public const byte PKT_UDP_DISCOVER_REQ = 1;
        public const byte PKT_UDP_DISCOVER_RESP = 2;
        public const byte PKT_TCP_QUERY_SESSION = 3;
        public const byte PKT_TCP_QUERY_RESP = 4;
        public const byte PKT_TCP_JOIN_REQ = 5;
        public const byte PKT_TCP_JOIN_ACK = 6;
        public const byte PKT_TCP_CREATE_PLAYER = 7;
        public const byte PKT_TCP_CREATE_PLAYER_ACK = 8;
        public const byte PKT_TCP_PLAYER_JOINED = 9;
        public const byte PKT_TCP_PLAYER_LEFT = 10;
        public const byte PKT_TCP_SET_PLAYER_DATA = 11;
        public const byte PKT_TCP_DATA_MSG = 12;
        public const byte PKT_TCP_SET_SESSION_DESC = 13;
        public const byte PKT_TCP_PING = 14;
    }

    internal static class DPlayConfig
    {
        public static int DefaultPort = 2350;
        public static string BindInterface = "All";
        public static bool PromptInterface = false;
        public static int DiscoveryTimeoutMs = 3000;
        public static bool EnableLog = false;

        private static readonly object s_logLock = new object();
        private static string s_baseDir;
        private static bool s_loaded;

        public static void EnsureLoaded()
        {
            if (s_loaded) return;
            s_loaded = true;
            try
            {
                s_baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(s_baseDir))
                    s_baseDir = Environment.CurrentDirectory;

                string iniPath = Path.Combine(s_baseDir, "dplayx.ini");
                if (!File.Exists(iniPath))
                {
                    WriteDefaultIni(iniPath);
                }
                else
                {
                    string[] lines = File.ReadAllLines(iniPath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                            continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        int parsedInt;
                        if (string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out parsedInt) && parsedInt > 0 && parsedInt <= 65535)
                                DefaultPort = parsedInt;
                        }
                        else if (string.Equals(key, "BindInterface", StringComparison.OrdinalIgnoreCase))
                        {
                            if (val.Length > 0) BindInterface = val;
                        }
                        else if (string.Equals(key, "PromptInterface", StringComparison.OrdinalIgnoreCase))
                        {
                            PromptInterface = (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase));
                        }
                        else if (string.Equals(key, "DiscoveryTimeoutMs", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out parsedInt) && parsedInt >= 200 && parsedInt <= 30000)
                                DiscoveryTimeoutMs = parsedInt;
                        }
                        else if (string.Equals(key, "Log", StringComparison.OrdinalIgnoreCase))
                        {
                            EnableLog = (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }

                // Environment variable overrides for multi-instance testing on a single PC
                string envPort = Environment.GetEnvironmentVariable("IMP2_PORT");
                int ep;
                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out ep) && ep > 0 && ep <= 65535)
                    DefaultPort = ep;

                string envBind = Environment.GetEnvironmentVariable("IMP2_BIND");
                if (!string.IsNullOrEmpty(envBind))
                    BindInterface = envBind;

                string envLog = Environment.GetEnvironmentVariable("IMP2_LOG");
                if (envLog == "1") EnableLog = true;
            }
            catch
            {
            }
        }

        private static void WriteDefaultIni(string path)
        {
            try
            {
                string content =
                    "[Multiplayer]\r\n" +
                    "; Single port used for both UDP LAN discovery and TCP session traffic.\r\n" +
                    "; For Internet hosting, forward only TCP port 2350 (UDP optional).\r\n" +
                    "Port=2350\r\n\r\n" +
                    "; Network interface selection:\r\n" +
                    ";   All              = Listen on 0.0.0.0 when hosting; scan across ALL interfaces when joining.\r\n" +
                    ";   <Adapter Name>   = Bind/scan a specific adapter (e.g., Ethernet, Wi-Fi, Tailscale).\r\n" +
                    ";   <IPv4 Address>   = Bind/scan a specific local IPv4 (e.g., 192.168.1.50).\r\n" +
                    "; Tip: Hold SHIFT while clicking Host or Join in-game to open the interactive\r\n" +
                    ";      Network Interface Picker window, or append @<AdapterOrIP> in the Join IP box!\r\n" +
                    "BindInterface=All\r\n\r\n" +
                    "; Set PromptInterface=1 to always pop up the Network Interface Picker on Host/Join.\r\n" +
                    "PromptInterface=0\r\n\r\n" +
                    "; Timeout in milliseconds when discovering games.\r\n" +
                    "DiscoveryTimeoutMs=3000\r\n\r\n" +
                    "; Set Log=1 to write diagnostic logs to dplayx.log.\r\n" +
                    "Log=0\r\n";
                File.WriteAllText(path, content, Encoding.ASCII);
            }
            catch
            {
            }
        }

        public static void Log(string fmt, params object[] args)
        {
            if (!EnableLog) return;
            try
            {
                string msg = (args != null && args.Length > 0) ? string.Format(fmt, args) : fmt;
                string line = string.Format("[{0:HH:mm:ss.fff}] [PID:{1}/TID:{2}] {3}\r\n",
                    DateTime.Now, Process.GetCurrentProcess().Id, Thread.CurrentThread.ManagedThreadId, msg);
                lock (s_logLock)
                {
                    File.AppendAllText(Path.Combine(s_baseDir ?? ".", "dplayx.log"), line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class NetAdapterInfo
    {
        public string Name;
        public string Description;
        public IPAddress Address;
        public IPAddress SubnetMask;
        public IPAddress BroadcastAddress;
        public bool IsLoopback;

        public override string ToString()
        {
            return string.Format("{0} ({1})", Name, Address);
        }
    }

    internal static class NetAdapterManager
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int VK_SHIFT = 0x10;
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;

        public static List<NetAdapterInfo> GetActiveIPv4Adapters()
        {
            List<NetAdapterInfo> list = new List<NetAdapterInfo>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    NetworkInterface nic = nics[i];
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    UnicastIPAddressInformationCollection unicast = props.UnicastAddresses;
                    for (int j = 0; j < unicast.Count; j++)
                    {
                        UnicastIPAddressInformation ua = unicast[j];
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        IPAddress ip = ua.Address;
                        IPAddress mask = ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                        IPAddress bcast = ComputeBroadcast(ip, mask);
                        list.Add(new NetAdapterInfo
                        {
                            Name = nic.Name,
                            Description = nic.Description,
                            Address = ip,
                            SubnetMask = mask,
                            BroadcastAddress = bcast,
                            IsLoopback = IPAddress.IsLoopback(ip)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("GetActiveIPv4Adapters error: {0}", ex.Message);
            }

            bool hasLoop = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsLoopback) { hasLoop = true; break; }
            }
            if (!hasLoop)
            {
                list.Add(new NetAdapterInfo
                {
                    Name = "Loopback",
                    Description = "Localhost Loopback",
                    Address = IPAddress.Loopback,
                    SubnetMask = IPAddress.Parse("255.0.0.0"),
                    BroadcastAddress = IPAddress.Loopback,
                    IsLoopback = true
                });
            }
            return list;
        }

        private static IPAddress ComputeBroadcast(IPAddress ip, IPAddress mask)
        {
            try
            {
                byte[] ipBytes = ip.GetAddressBytes();
                byte[] maskBytes = mask.GetAddressBytes();
                if (ipBytes.Length == 4 && maskBytes.Length == 4)
                {
                    byte[] b = new byte[4];
                    for (int i = 0; i < 4; i++)
                        b[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));
                    return new IPAddress(b);
                }
            }
            catch { }
            return IPAddress.Broadcast;
        }

        public static List<NetAdapterInfo> ResolveSelection(string spec, List<NetAdapterInfo> all)
        {
            if (string.IsNullOrEmpty(spec) ||
                string.Equals(spec, "All", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec, "*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return all;
            }

            List<NetAdapterInfo> matched = new List<NetAdapterInfo>();
            IPAddress parsedIp;
            bool isIp = IPAddress.TryParse(spec, out parsedIp);

            for (int i = 0; i < all.Count; i++)
            {
                NetAdapterInfo a = all[i];
                if (isIp && a.Address.Equals(parsedIp))
                {
                    matched.Add(a);
                }
                else if (!isIp && (
                    string.Equals(a.Name, spec, StringComparison.OrdinalIgnoreCase) ||
                    a.Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    a.Description.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    matched.Add(a);
                }
            }

            if (matched.Count == 0 && isIp && parsedIp.AddressFamily == AddressFamily.InterNetwork)
            {
                matched.Add(new NetAdapterInfo
                {
                    Name = spec,
                    Description = spec,
                    Address = parsedIp,
                    SubnetMask = IPAddress.Parse("255.255.255.0"),
                    BroadcastAddress = IPAddress.Broadcast,
                    IsLoopback = IPAddress.IsLoopback(parsedIp)
                });
            }

            return matched.Count > 0 ? matched : all;
        }

        public static bool ShouldPromptInteractive()
        {
            if (DPlayConfig.PromptInterface) return true;
            try
            {
                short state = GetAsyncKeyState(VK_SHIFT);
                return (state & unchecked((short)0x8000)) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static void PromptInterfaceDialog(bool isHost, ref string bindSpec, ref int port)
        {
            IntPtr fg = IntPtr.Zero;
            try
            {
                fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                    ShowWindow(fg, SW_MINIMIZE);

                List<NetAdapterInfo> adapters = GetActiveIPv4Adapters();
                using (Form form = new Form())
                {
                    form.Text = isHost ? "Imperialism II - Select Host Network Interface" : "Imperialism II - Select Join Network Interface";
                    form.FormBorderStyle = FormBorderStyle.FixedDialog;
                    form.StartPosition = FormStartPosition.CenterScreen;
                    form.MaximizeBox = false;
                    form.MinimizeBox = false;
                    form.TopMost = true;
                    form.ClientSize = new Size(440, 235);
                    form.Font = new Font("Segoe UI", 9f);

                    Label lbl = new Label();
                    lbl.Text = isHost
                        ? "Choose which network interface(s) to host on:"
                        : "Choose which network interface(s) to scan / bind:";
                    lbl.SetBounds(14, 12, 410, 20);

                    ListBox lb = new ListBox();
                    lb.SetBounds(14, 36, 412, 115);
                    lb.Items.Add("[All Interfaces] Listen / Scan across all adapters (0.0.0.0)");
                    int selIdx = 0;
                    for (int i = 0; i < adapters.Count; i++)
                    {
                        NetAdapterInfo a = adapters[i];
                        lb.Items.Add(string.Format("{0}  —  {1}  ({2})", a.Name, a.Address, a.Description));
                        if (!string.Equals(bindSpec, "All", StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(a.Name, bindSpec, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a.Address.ToString(), bindSpec, StringComparison.OrdinalIgnoreCase)))
                        {
                            selIdx = i + 1;
                        }
                    }
                    lb.SelectedIndex = selIdx;

                    Label lblPort = new Label();
                    lblPort.Text = "Port (TCP/UDP):";
                    lblPort.SetBounds(14, 162, 105, 22);

                    TextBox txtPort = new TextBox();
                    txtPort.Text = port.ToString();
                    txtPort.SetBounds(120, 159, 80, 24);

                    Button btnOk = new Button();
                    btnOk.Text = "OK";
                    btnOk.DialogResult = DialogResult.OK;
                    btnOk.SetBounds(256, 192, 80, 28);

                    Button btnCancel = new Button();
                    btnCancel.Text = "Cancel";
                    btnCancel.DialogResult = DialogResult.Cancel;
                    btnCancel.SetBounds(346, 192, 80, 28);

                    form.Controls.AddRange(new Control[] { lbl, lb, lblPort, txtPort, btnOk, btnCancel });
                    form.AcceptButton = btnOk;
                    form.CancelButton = btnCancel;

                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        if (lb.SelectedIndex <= 0)
                            bindSpec = "All";
                        else
                            bindSpec = adapters[lb.SelectedIndex - 1].Address.ToString();

                        int p;
                        if (int.TryParse(txtPort.Text.Trim(), out p) && p > 0 && p <= 65535)
                            port = p;
                    }
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("PromptInterfaceDialog error: {0}", ex.Message);
            }
            finally
            {
                if (fg != IntPtr.Zero)
                    ShowWindow(fg, SW_RESTORE);
            }
        }

        public static string FormatDisplayIPs(string bindSpec, int port)
        {
            List<NetAdapterInfo> all = GetActiveIPv4Adapters();
            List<NetAdapterInfo> selected = ResolveSelection(bindSpec, all);

            List<string> ips = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                if (!selected[i].IsLoopback)
                {
                    string s = selected[i].Address.ToString();
                    if (!ips.Contains(s)) ips.Add(s);
                }
            }
            if (ips.Count == 0)
                ips.Add("127.0.0.1");

            string joined = string.Join(" / ", ips.ToArray());
            if (port != 2350)
                joined = string.Format("{0} (:{1})", joined, port);

            if (joined.Length > 60)
                joined = joined.Substring(0, 60);
            return joined;
        }
    }

    internal sealed class DiscoveredSession
    {
        public Guid GuidInstance;
        public Guid GuidApplication;
        public uint MaxPlayers;
        public uint CurrentPlayers;
        public uint Flags;
        public string SessionName;
        public IPEndPoint HostEndPoint;
        public IPAddress LocalBindIP;
    }

    internal sealed class PlayerRecord
    {
        public uint PlayerId;
        public string ShortName = "";
        public string LongName = "";
        public byte[] RemoteData = new byte[0];
        public bool IsLocal;
        public ClientPeer Connection;
    }

    internal sealed class QueuedMessage
    {
        public uint IdFrom;
        public uint IdTo;
        public byte[] Data;
    }

    internal sealed class ClientPeer
    {
        public TcpClient Client;
        public NetworkStream Stream;
        public readonly object WriteLock = new object();
        public List<uint> OwnedPlayers = new List<uint>();
        public IPEndPoint RemoteEndPoint;
        public volatile bool Closed;

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            try { if (Stream != null) Stream.Close(); } catch { }
            try { if (Client != null) Client.Close(); } catch { }
        }
    }

    internal sealed class DPlaySessionEngine : IDisposable
    {
        private readonly object m_lock = new object();
        private readonly Queue<QueuedMessage> m_recvQueue = new Queue<QueuedMessage>();
        private readonly Dictionary<uint, PlayerRecord> m_players = new Dictionary<uint, PlayerRecord>();
        private readonly List<ClientPeer> m_peers = new List<ClientPeer>();
        private readonly Dictionary<Guid, DiscoveredSession> m_discovered = new Dictionary<Guid, DiscoveredSession>();

        private string m_targetAddressRaw = "";
        private string m_activeBindSpec = "All";
        private int m_activePort = 2350;

        private bool m_isOpen;
        private bool m_isHost;
        private Guid m_guidInstance;
        private Guid m_guidApplication;
        private uint m_maxPlayers = 6;
        private uint m_sessionFlags;
        private string m_sessionName = "Imperialism II";
        private uint m_nextDpId = 2;

        private TcpListener m_tcpListener;
        private UdpClient m_udpListener;
        private ClientPeer m_hostConn;
        private volatile bool m_running;

        private AutoResetEvent m_joinAckEvent = new AutoResetEvent(false);
        private bool m_joinAccepted;
        private AutoResetEvent m_createPlayerAckEvent = new AutoResetEvent(false);
        private uint m_createdPlayerIdResult;

        public int InitializeConnection(string inetTarget)
        {
            DPlayConfig.EnsureLoaded();
            ShutdownSession();

            m_targetAddressRaw = (inetTarget ?? "").Trim();
            m_activeBindSpec = DPlayConfig.BindInterface;
            m_activePort = DPlayConfig.DefaultPort;

            // Parse optional @interface suffix in target string: e.g. "host:2350@Wi-Fi" or "@192.168.1.50"
            int atIdx = m_targetAddressRaw.LastIndexOf('@');
            if (atIdx >= 0)
            {
                string ifacePart = m_targetAddressRaw.Substring(atIdx + 1).Trim();
                m_targetAddressRaw = m_targetAddressRaw.Substring(0, atIdx).Trim();
                if (ifacePart.Length > 0)
                    m_activeBindSpec = ifacePart;
            }

            DPlayConfig.Log("InitializeConnection: target='{0}', bind='{1}', defaultPort={2}",
                m_targetAddressRaw, m_activeBindSpec, m_activePort);
            return DPlayConstants.DP_OK;
        }

        public int HostSession(ref Guid guidInstance, Guid guidApp, uint maxPlayers, uint flags, string sessionName)
        {
            ShutdownSession();

            if (NetAdapterManager.ShouldPromptInteractive())
            {
                NetAdapterManager.PromptInterfaceDialog(true, ref m_activeBindSpec, ref m_activePort);
            }

            IPAddress bindIp = IPAddress.Any;
            List<NetAdapterInfo> all = NetAdapterManager.GetActiveIPv4Adapters();
            if (!string.Equals(m_activeBindSpec, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m_activeBindSpec, "*", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m_activeBindSpec, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                List<NetAdapterInfo> sel = NetAdapterManager.ResolveSelection(m_activeBindSpec, all);
                if (sel.Count == 1)
                    bindIp = sel[0].Address;
            }

            m_guidInstance = Guid.NewGuid();
            guidInstance = m_guidInstance;
            m_guidApplication = guidApp;
            m_maxPlayers = maxPlayers > 0 ? maxPlayers : 6u;
            m_sessionFlags = flags;
            m_sessionName = string.IsNullOrEmpty(sessionName) ? "Imperialism II" : sessionName;
            m_nextDpId = 2;

            try
            {
                m_tcpListener = new TcpListener(bindIp, m_activePort);
                m_tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_tcpListener.Start();

                try
                {
                    Socket udpSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    udpSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpSock.EnableBroadcast = true;
                    udpSock.Bind(new IPEndPoint(bindIp, m_activePort));
                    m_udpListener = new UdpClient { Client = udpSock };
                }
                catch (Exception uex)
                {
                    DPlayConfig.Log("Host UDP discovery socket warning (TCP still active): {0}", uex.Message);
                }

                m_running = true;
                m_isHost = true;
                m_isOpen = true;

                Thread acceptThread = new Thread(HostAcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "Imp2HostAccept";
                acceptThread.Start();

                if (m_udpListener != null)
                {
                    Thread udpThread = new Thread(HostUdpDiscoveryLoop);
                    udpThread.IsBackground = true;
                    udpThread.Name = "Imp2HostUdp";
                    udpThread.Start();
                }

                DPlayConfig.Log("Hosting session '{0}' ({1}) on {2}:{3}", m_sessionName, m_guidInstance, bindIp, m_activePort);
                return DPlayConstants.DP_OK;
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("HostSession failed: {0}", ex);
                ShutdownSession();
                return DPlayConstants.DPERR_NOCONNECTION;
            }
        }

        public List<DiscoveredSession> EnumerateSessions(Guid guidApp, uint callerTimeoutMs)
        {
            if (NetAdapterManager.ShouldPromptInteractive())
            {
                NetAdapterManager.PromptInterfaceDialog(false, ref m_activeBindSpec, ref m_activePort);
            }

            int timeoutMs = Math.Max((int)callerTimeoutMs, DPlayConfig.DiscoveryTimeoutMs);
            if (timeoutMs > 8000) timeoutMs = 8000;

            List<DiscoveredSession> results = new List<DiscoveredSession>();
            lock (m_lock)
            {
                m_discovered.Clear();
            }

            List<NetAdapterInfo> allAdapters = NetAdapterManager.GetActiveIPv4Adapters();
            List<NetAdapterInfo> selectedAdapters = NetAdapterManager.ResolveSelection(m_activeBindSpec, allAdapters);
            IPAddress explicitLocalBind = (selectedAdapters.Count == 1 && !string.Equals(m_activeBindSpec, "All", StringComparison.OrdinalIgnoreCase))
                ? selectedAdapters[0].Address
                : null;

            string target = m_targetAddressRaw;
            bool isScanAll = string.IsNullOrEmpty(target) || target == "*" || string.Equals(target, "lan", StringComparison.OrdinalIgnoreCase);

            if (!isScanAll)
            {
                string hostPart = target;
                int targetPort = m_activePort;
                int colon = target.LastIndexOf(':');
                if (colon > 0)
                {
                    int parsedP;
                    if (int.TryParse(target.Substring(colon + 1).Trim(), out parsedP) && parsedP > 0 && parsedP <= 65535)
                    {
                        hostPart = target.Substring(0, colon).Trim();
                        targetPort = parsedP;
                    }
                }

                IPAddress[] resolved = ResolveHostIPv4(hostPart);
                for (int i = 0; i < resolved.Length; i++)
                {
                    IPEndPoint ep = new IPEndPoint(resolved[i], targetPort);
                    DiscoveredSession ds = TryTcpQuerySession(ep, explicitLocalBind, guidApp, Math.Min(timeoutMs, 3000));
                    if (ds != null)
                    {
                        lock (m_lock)
                        {
                            m_discovered[ds.GuidInstance] = ds;
                        }
                        results.Add(ds);
                    }
                }

                if (results.Count == 0 && resolved.Length > 0)
                {
                    UdpProbeEndpoints(resolved, targetPort, selectedAdapters, guidApp, Math.Min(timeoutMs, 1500), results);
                }
            }
            else
            {
                // Multi-interface broadcast scan across all selected adapters + loopback!
                ScanAcrossInterfacesUdp(selectedAdapters, m_activePort, guidApp, Math.Min(timeoutMs, 1800), results);

                // Also fast TCP loopback check on localhost:port in case firewall blocks UDP broadcast on same PC
                if (results.Count == 0)
                {
                    DiscoveredSession loopDs = TryTcpQuerySession(new IPEndPoint(IPAddress.Loopback, m_activePort), null, guidApp, 400);
                    if (loopDs != null)
                    {
                        lock (m_lock) { m_discovered[loopDs.GuidInstance] = loopDs; }
                        results.Add(loopDs);
                    }
                }
            }

            DPlayConfig.Log("EnumerateSessions found {0} session(s).", results.Count);
            return results;
        }

        private static IPAddress[] ResolveHostIPv4(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return new IPAddress[] { ip };
                return new IPAddress[0];
            }
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                List<IPAddress> v4 = new List<IPAddress>();
                for (int i = 0; i < addrs.Length; i++)
                {
                    if (addrs[i].AddressFamily == AddressFamily.InterNetwork)
                        v4.Add(addrs[i]);
                }
                return v4.ToArray();
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("DNS resolve failed for '{0}': {1}", host, ex.Message);
                return new IPAddress[0];
            }
        }

        private DiscoveredSession TryTcpQuerySession(IPEndPoint ep, IPAddress localBind, Guid guidApp, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                if (localBind != null)
                    client = new TcpClient(new IPEndPoint(localBind, 0));
                else
                    client = new TcpClient(AddressFamily.InterNetwork);

                client.NoDelay = true;
                IAsyncResult ar = client.BeginConnect(ep.Address, ep.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    client.Close();
                    return null;
                }
                client.EndConnect(ar);
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;

                NetworkStream ns = client.GetStream();
                SendFrame(ns, DPlayConstants.PKT_TCP_QUERY_SESSION, guidApp.ToByteArray());

                byte pktType;
                byte[] payload = ReadFrame(ns, out pktType);
                client.Close();

                if (pktType == DPlayConstants.PKT_TCP_QUERY_RESP && payload != null && payload.Length >= 49)
                {
                    DiscoveredSession ds = ParseSessionPayload(payload, ep, localBind);
                    if (ds != null && (guidApp == Guid.Empty || ds.GuidApplication == guidApp))
                        return ds;
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("TryTcpQuerySession({0}) failed: {1}", ep, ex.Message);
                if (client != null) { try { client.Close(); } catch { } }
            }
            return null;
        }

        private void UdpProbeEndpoints(IPAddress[] targets, int port, List<NetAdapterInfo> adapters, Guid guidApp, int waitMs, List<DiscoveredSession> results)
        {
            List<Socket> sockets = new List<Socket>();
            try
            {
                Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(adapters.Count == 1 ? adapters[0].Address : IPAddress.Any, 0));
                sockets.Add(s);

                byte[] req = BuildDiscoveryRequest(guidApp);
                for (int i = 0; i < targets.Length; i++)
                {
                    try { s.SendTo(req, new IPEndPoint(targets[i], port)); } catch { }
                }
                CollectUdpResponses(sockets, null, guidApp, waitMs, results);
            }
            catch { }
            finally
            {
                for (int i = 0; i < sockets.Count; i++) { try { sockets[i].Close(); } catch { } }
            }
        }

        private void ScanAcrossInterfacesUdp(List<NetAdapterInfo> adapters, int port, Guid guidApp, int waitMs, List<DiscoveredSession> results)
        {
            List<Socket> sockets = new List<Socket>();
            Dictionary<Socket, IPAddress> socketLocalIp = new Dictionary<Socket, IPAddress>();
            byte[] req = BuildDiscoveryRequest(guidApp);

            try
            {
                for (int i = 0; i < adapters.Count; i++)
                {
                    NetAdapterInfo a = adapters[i];
                    try
                    {
                        Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        s.EnableBroadcast = true;
                        s.Bind(new IPEndPoint(a.Address, 0));
                        sockets.Add(s);
                        socketLocalIp[s] = a.Address;

                        if (a.IsLoopback)
                        {
                            try { s.SendTo(req, new IPEndPoint(IPAddress.Loopback, port)); } catch { }
                        }
                        else
                        {
                            try { s.SendTo(req, new IPEndPoint(a.BroadcastAddress, port)); } catch { }
                            if (!a.BroadcastAddress.Equals(IPAddress.Broadcast))
                            {
                                try { s.SendTo(req, new IPEndPoint(IPAddress.Broadcast, port)); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DPlayConfig.Log("UDP scan bind failed on {0}: {1}", a.Address, ex.Message);
                    }
                }

                // Send a second wave after 250ms in case a Wi-Fi frame dropped
                CollectUdpResponses(sockets, socketLocalIp, guidApp, Math.Min(300, waitMs), results);
                for (int i = 0; i < sockets.Count; i++)
                {
                    Socket s = sockets[i];
                    IPAddress lip = socketLocalIp[s];
                    if (IPAddress.IsLoopback(lip))
                    {
                        try { s.SendTo(req, new IPEndPoint(IPAddress.Loopback, port)); } catch { }
                    }
                    else
                    {
                        for (int j = 0; j < adapters.Count; j++)
                        {
                            if (adapters[j].Address.Equals(lip))
                            {
                                try { s.SendTo(req, new IPEndPoint(adapters[j].BroadcastAddress, port)); } catch { }
                            }
                        }
                    }
                }
                if (waitMs > 300)
                    CollectUdpResponses(sockets, socketLocalIp, guidApp, waitMs - 300, results);
            }
            finally
            {
                for (int i = 0; i < sockets.Count; i++) { try { sockets[i].Close(); } catch { } }
            }
        }

        private void CollectUdpResponses(List<Socket> sockets, Dictionary<Socket, IPAddress> localMap, Guid guidApp, int waitMs, List<DiscoveredSession> results)
        {
            if (sockets.Count == 0) return;
            Stopwatch sw = Stopwatch.StartNew();
            byte[] buf = new byte[1024];

            while (sw.ElapsedMilliseconds < waitMs)
            {
                List<Socket> checkRead = new List<Socket>(sockets);
                int remainUs = (int)Math.Max(1000, Math.Min(100000, (waitMs - sw.ElapsedMilliseconds) * 1000));
                try
                {
                    Socket.Select(checkRead, null, null, remainUs);
                }
                catch { break; }

                for (int i = 0; i < checkRead.Count; i++)
                {
                    Socket s = checkRead[i];
                    try
                    {
                        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                        int n = s.ReceiveFrom(buf, ref remoteEp);
                        if (n >= 54)
                        {
                            uint magic = BitConverter.ToUInt32(buf, 0);
                            byte type = buf[4];
                            if (magic == DPlayConstants.MAGIC && type == DPlayConstants.PKT_UDP_DISCOVER_RESP)
                            {
                                byte[] payload = new byte[n - 5];
                                Buffer.BlockCopy(buf, 5, payload, 0, payload.Length);
                                IPEndPoint rep = (IPEndPoint)remoteEp;
                                int tcpPort = BitConverter.ToInt32(payload, 44);
                                IPEndPoint hostTcpEp = new IPEndPoint(rep.Address, tcpPort > 0 ? tcpPort : rep.Port);
                                IPAddress localIp = (localMap != null && localMap.ContainsKey(s)) ? localMap[s] : null;

                                DiscoveredSession ds = ParseSessionPayload(payload, hostTcpEp, localIp);
                                if (ds != null && (guidApp == Guid.Empty || ds.GuidApplication == guidApp))
                                {
                                    bool isNew = false;
                                    lock (m_lock)
                                    {
                                        if (!m_discovered.ContainsKey(ds.GuidInstance))
                                        {
                                            m_discovered[ds.GuidInstance] = ds;
                                            isNew = true;
                                        }
                                    }
                                    if (isNew)
                                        results.Add(ds);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        public int JoinSession(Guid guidInstance)
        {
            DiscoveredSession target = null;
            lock (m_lock)
            {
                m_discovered.TryGetValue(guidInstance, out target);
            }

            if (target == null)
            {
                DPlayConfig.Log("JoinSession failed: unknown guidInstance {0}", guidInstance);
                return DPlayConstants.DPERR_NOSESSIONS;
            }

            ShutdownSession();

            try
            {
                TcpClient tcp = (target.LocalBindIP != null && !IPAddress.IsLoopback(target.LocalBindIP) && !IPAddress.IsLoopback(target.HostEndPoint.Address))
                    ? new TcpClient(new IPEndPoint(target.LocalBindIP, 0))
                    : new TcpClient(AddressFamily.InterNetwork);

                tcp.NoDelay = true;
                IAsyncResult ar = tcp.BeginConnect(target.HostEndPoint.Address, target.HostEndPoint.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000))
                {
                    tcp.Close();
                    return DPlayConstants.DPERR_NOCONNECTION;
                }
                tcp.EndConnect(ar);

                ClientPeer peer = new ClientPeer
                {
                    Client = tcp,
                    Stream = tcp.GetStream(),
                    RemoteEndPoint = target.HostEndPoint
                };

                m_hostConn = peer;
                m_running = true;
                m_isHost = false;
                m_joinAccepted = false;
                m_joinAckEvent.Reset();

                Thread reader = new Thread(() => ClientReadLoop(peer));
                reader.IsBackground = true;
                reader.Name = "Imp2ClientReader";
                reader.Start();

                SendFrame(peer, DPlayConstants.PKT_TCP_JOIN_REQ, guidInstance.ToByteArray());
                if (!m_joinAckEvent.WaitOne(5000) || !m_joinAccepted)
                {
                    DPlayConfig.Log("JoinSession handshake rejected or timed out.");
                    ShutdownSession();
                    return DPlayConstants.DPERR_NOCONNECTION;
                }

                m_isOpen = true;
                DPlayConfig.Log("Joined session '{0}' at {1}", m_sessionName, target.HostEndPoint);
                return DPlayConstants.DP_OK;
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("JoinSession exception: {0}", ex.Message);
                ShutdownSession();
                return DPlayConstants.DPERR_NOCONNECTION;
            }
        }

        public int CreatePlayer(string shortName, string longName, out uint outPlayerId)
        {
            outPlayerId = 0;
            if (!m_isOpen) return DPlayConstants.DPERR_NOCONNECTION;

            shortName = shortName ?? "";
            longName = longName ?? "";

            if (m_isHost)
            {
                uint id;
                byte[] notifyPayload;
                List<ClientPeer> peersSnap;
                lock (m_lock)
                {
                    id = 1u;
                    while (m_players.ContainsKey(id))
                        id = m_nextDpId++;

                    PlayerRecord rec = new PlayerRecord
                    {
                        PlayerId = id,
                        ShortName = shortName,
                        LongName = longName,
                        IsLocal = true,
                        RemoteData = new byte[0]
                    };
                    m_players[id] = rec;
                    notifyPayload = SerializePlayerRecord(rec);
                    peersSnap = new List<ClientPeer>(m_peers);
                }

                for (int i = 0; i < peersSnap.Count; i++)
                    SendFrame(peersSnap[i], DPlayConstants.PKT_TCP_PLAYER_JOINED, notifyPayload);

                outPlayerId = id;
                DPlayConfig.Log("Host CreatePlayer '{0}' -> DPID {1}", shortName, id);
                return DPlayConstants.DP_OK;
            }
            else
            {
                m_createdPlayerIdResult = 0;
                m_createPlayerAckEvent.Reset();

                byte[] sBytes = Encoding.ASCII.GetBytes(shortName);
                byte[] lBytes = Encoding.ASCII.GetBytes(longName);
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write(sBytes.Length);
                    bw.Write(sBytes);
                    bw.Write(lBytes.Length);
                    bw.Write(lBytes);
                    SendFrame(m_hostConn, DPlayConstants.PKT_TCP_CREATE_PLAYER, ms.ToArray());
                }

                if (!m_createPlayerAckEvent.WaitOne(5000) || m_createdPlayerIdResult == 0)
                    return DPlayConstants.DPERR_GENERIC;

                outPlayerId = m_createdPlayerIdResult;
                DPlayConfig.Log("Client CreatePlayer '{0}' -> DPID {1}", shortName, outPlayerId);
                return DPlayConstants.DP_OK;
            }
        }

        public int DestroyPlayer(uint playerId)
        {
            if (!m_isOpen || playerId == 0) return DPlayConstants.DP_OK;

            if (m_isHost)
            {
                List<ClientPeer> peersSnap;
                lock (m_lock)
                {
                    if (!m_players.Remove(playerId))
                        return DPlayConstants.DP_OK;
                    peersSnap = new List<ClientPeer>(m_peers);
                }
                byte[] body = BitConverter.GetBytes(playerId);
                for (int i = 0; i < peersSnap.Count; i++)
                    SendFrame(peersSnap[i], DPlayConstants.PKT_TCP_PLAYER_LEFT, body);
            }
            else if (m_hostConn != null && !m_hostConn.Closed)
            {
                lock (m_lock) { m_players.Remove(playerId); }
                SendFrame(m_hostConn, DPlayConstants.PKT_TCP_PLAYER_LEFT, BitConverter.GetBytes(playerId));
            }
            return DPlayConstants.DP_OK;
        }

        public int SetPlayerData(uint playerId, byte[] data)
        {
            if (!m_isOpen) return DPlayConstants.DPERR_NOCONNECTION;
            byte[] copy = data != null ? (byte[])data.Clone() : new byte[0];

            byte[] pkt;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(playerId);
                bw.Write(copy.Length);
                bw.Write(copy);
                pkt = ms.ToArray();
            }

            if (m_isHost)
            {
                List<ClientPeer> peersSnap;
                lock (m_lock)
                {
                    PlayerRecord rec;
                    if (!m_players.TryGetValue(playerId, out rec))
                        return DPlayConstants.DPERR_INVALIDPLAYER;
                    rec.RemoteData = copy;
                    peersSnap = new List<ClientPeer>(m_peers);
                }
                for (int i = 0; i < peersSnap.Count; i++)
                    SendFrame(peersSnap[i], DPlayConstants.PKT_TCP_SET_PLAYER_DATA, pkt);
            }
            else
            {
                lock (m_lock)
                {
                    PlayerRecord rec;
                    if (m_players.TryGetValue(playerId, out rec))
                        rec.RemoteData = copy;
                }
                if (m_hostConn == null || m_hostConn.Closed)
                    return DPlayConstants.DPERR_SESSIONLOST;

                // Send and flush synchronously so Host updates immediately
                SendFrame(m_hostConn, DPlayConstants.PKT_TCP_SET_PLAYER_DATA, pkt);
            }
            return DPlayConstants.DP_OK;
        }

        public int GetPlayerData(uint playerId, out byte[] data)
        {
            lock (m_lock)
            {
                PlayerRecord rec;
                if (m_players.TryGetValue(playerId, out rec))
                {
                    data = rec.RemoteData ?? new byte[0];
                    return DPlayConstants.DP_OK;
                }
            }
            data = null;
            return DPlayConstants.DPERR_INVALIDPLAYER;
        }

        public List<PlayerRecord> GetPlayersSnapshot(uint flags)
        {
            List<PlayerRecord> list = new List<PlayerRecord>();
            bool wantRemote = (flags & DPlayConstants.DPENUMPLAYERS_REMOTE) != 0;
            bool wantLocal = (flags & DPlayConstants.DPENUMPLAYERS_LOCAL) != 0;
            if (!wantRemote && !wantLocal)
            {
                wantRemote = true;
                wantLocal = true;
            }

            lock (m_lock)
            {
                foreach (KeyValuePair<uint, PlayerRecord> kv in m_players)
                {
                    PlayerRecord p = kv.Value;
                    if ((p.IsLocal && wantLocal) || (!p.IsLocal && wantRemote))
                        list.Add(p);
                }
            }
            return list;
        }

        public string GetPlayerIPString(uint playerId)
        {
            if (m_isHost)
            {
                lock (m_lock)
                {
                    PlayerRecord rec;
                    if (m_players.TryGetValue(playerId, out rec) && !rec.IsLocal && rec.Connection != null && rec.Connection.RemoteEndPoint != null)
                        return rec.Connection.RemoteEndPoint.Address.ToString();
                }
                return NetAdapterManager.FormatDisplayIPs(m_activeBindSpec, m_activePort);
            }
            else
            {
                if (m_hostConn != null && m_hostConn.Client != null && m_hostConn.Client.Client != null)
                {
                    IPEndPoint lep = m_hostConn.Client.Client.LocalEndPoint as IPEndPoint;
                    if (lep != null) return lep.Address.ToString();
                }
                return NetAdapterManager.FormatDisplayIPs(m_activeBindSpec, m_activePort);
            }
        }

        public void GetSessionInfo(out Guid guidInst, out Guid guidApp, out uint maxPlayers, out uint curPlayers, out uint flags, out string name)
        {
            lock (m_lock)
            {
                guidInst = m_guidInstance;
                guidApp = m_guidApplication;
                maxPlayers = m_maxPlayers;
                curPlayers = (uint)m_players.Count;
                flags = m_sessionFlags;
                name = m_sessionName;
            }
        }

        public int SetSessionInfo(uint flags, uint maxPlayers, string name)
        {
            if (!m_isOpen) return DPlayConstants.DPERR_NOCONNECTION;
            byte[] snapPayload;
            List<ClientPeer> peersSnap = null;
            lock (m_lock)
            {
                m_sessionFlags = flags;
                if (maxPlayers > 0) m_maxPlayers = maxPlayers;
                if (!string.IsNullOrEmpty(name)) m_sessionName = name;
                snapPayload = BuildSessionPayload();
                if (m_isHost) peersSnap = new List<ClientPeer>(m_peers);
            }

            if (m_isHost && peersSnap != null)
            {
                for (int i = 0; i < peersSnap.Count; i++)
                    SendFrame(peersSnap[i], DPlayConstants.PKT_TCP_SET_SESSION_DESC, snapPayload);
            }
            return DPlayConstants.DP_OK;
        }

        public int SendMessage(uint idFrom, uint idTo, byte[] data)
        {
            if (!m_isOpen) return DPlayConstants.DPERR_NOCONNECTION;
            if (data == null) data = new byte[0];

            byte[] frame;
            using (MemoryStream ms = new MemoryStream(12 + data.Length))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(idFrom);
                bw.Write(idTo);
                bw.Write(data.Length);
                bw.Write(data);
                frame = ms.ToArray();
            }

            if (m_isHost)
            {
                RouteDataFromHost(idFrom, idTo, data, frame, null);
                return DPlayConstants.DP_OK;
            }
            else
            {
                if (m_hostConn == null || m_hostConn.Closed)
                    return DPlayConstants.DPERR_SESSIONLOST;

                return SendFrame(m_hostConn, DPlayConstants.PKT_TCP_DATA_MSG, frame)
                    ? DPlayConstants.DP_OK
                    : DPlayConstants.DPERR_SESSIONLOST;
            }
        }

        private void RouteDataFromHost(uint idFrom, uint idTo, byte[] rawData, byte[] wireFrame, ClientPeer senderPeer)
        {
            List<ClientPeer> targets = new List<ClientPeer>();
            bool deliverToHostLocal = false;

            lock (m_lock)
            {
                if (idTo == DPlayConstants.DPID_ALLPLAYERS)
                {
                    // Deliver to local host player(s) except idFrom
                    foreach (KeyValuePair<uint, PlayerRecord> kv in m_players)
                    {
                        if (kv.Value.IsLocal && kv.Key != idFrom)
                        {
                            deliverToHostLocal = true;
                            break;
                        }
                    }
                    // Deliver to all connected peers except senderPeer
                    for (int i = 0; i < m_peers.Count; i++)
                    {
                        if (m_peers[i] != senderPeer)
                            targets.Add(m_peers[i]);
                    }
                }
                else
                {
                    PlayerRecord dest;
                    if (m_players.TryGetValue(idTo, out dest))
                    {
                        if (dest.IsLocal)
                            deliverToHostLocal = true;
                        else if (dest.Connection != null)
                            targets.Add(dest.Connection);
                    }
                }

                if (deliverToHostLocal)
                {
                    m_recvQueue.Enqueue(new QueuedMessage
                    {
                        IdFrom = idFrom,
                        IdTo = idTo,
                        Data = (byte[])rawData.Clone()
                    });
                }
            }

            for (int i = 0; i < targets.Count; i++)
                SendFrame(targets[i], DPlayConstants.PKT_TCP_DATA_MSG, wireFrame);
        }

        public bool PeekOrDequeueMessage(int bufCapacity, out uint idFrom, out uint idTo, out byte[] data, out int requiredSize)
        {
            lock (m_lock)
            {
                if (m_recvQueue.Count == 0)
                {
                    idFrom = 0;
                    idTo = 0;
                    data = null;
                    requiredSize = 0;
                    return false;
                }

                QueuedMessage front = m_recvQueue.Peek();
                requiredSize = front.Data != null ? front.Data.Length : 0;
                idFrom = front.IdFrom;
                idTo = front.IdTo;

                if (bufCapacity < requiredSize)
                {
                    data = null;
                    return true; // Caller receives DPERR_BUFFERTOOSMALL with requiredSize
                }

                m_recvQueue.Dequeue();
                data = front.Data ?? new byte[0];
                return true;
            }
        }

        private void EnqueueSystemDestroyPlayer(uint droppedPlayerId)
        {
            // DPMSG_DESTROYPLAYERORGROUP: [0]=DWORD dwType (0x0005), [4]=DWORD dwPlayerType (1), [8]=DPID dpId
            byte[] msg = new byte[32];
            Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.DPSYS_DESTROYPLAYERORGROUP), 0, msg, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.DPPLAYERTYPE_PLAYER), 0, msg, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(droppedPlayerId), 0, msg, 8, 4);
            lock (m_lock)
            {
                m_recvQueue.Enqueue(new QueuedMessage { IdFrom = DPlayConstants.DPID_SYSMSG, IdTo = 0, Data = msg });
            }
        }

        private void EnqueueSystemSessionLost()
        {
            byte[] msg = BitConverter.GetBytes(DPlayConstants.DPSYS_SESSIONLOST);
            lock (m_lock)
            {
                m_recvQueue.Enqueue(new QueuedMessage { IdFrom = DPlayConstants.DPID_SYSMSG, IdTo = 0, Data = msg });
            }
        }

        private void HostUdpDiscoveryLoop()
        {
            while (m_running && m_udpListener != null)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] pkt = m_udpListener.Receive(ref remote);
                    if (pkt == null || pkt.Length < 21) continue;
                    if (BitConverter.ToUInt32(pkt, 0) != DPlayConstants.MAGIC) continue;
                    if (pkt[4] != DPlayConstants.PKT_UDP_DISCOVER_REQ) continue;

                    byte[] gBytes = new byte[16];
                    Buffer.BlockCopy(pkt, 5, gBytes, 0, 16);
                    Guid reqApp = new Guid(gBytes);

                    byte[] respPayload = null;
                    lock (m_lock)
                    {
                        bool joinDisabled = (m_sessionFlags & (DPlayConstants.DPSESSION_NEWPLAYERSDISABLED | DPlayConstants.DPSESSION_JOINDISABLED)) != 0;
                        bool full = m_players.Count >= m_maxPlayers;
                        if (!joinDisabled && !full && (reqApp == Guid.Empty || reqApp == m_guidApplication))
                        {
                            respPayload = BuildSessionPayload();
                        }
                    }

                    if (respPayload != null)
                    {
                        byte[] resp = new byte[5 + respPayload.Length];
                        Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.MAGIC), 0, resp, 0, 4);
                        resp[4] = DPlayConstants.PKT_UDP_DISCOVER_RESP;
                        Buffer.BlockCopy(respPayload, 0, resp, 5, respPayload.Length);
                        m_udpListener.Send(resp, resp.Length, remote);
                    }
                }
                catch
                {
                    if (!m_running) break;
                }
            }
        }

        private void HostAcceptLoop()
        {
            while (m_running && m_tcpListener != null)
            {
                try
                {
                    TcpClient tcp = m_tcpListener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    Thread t = new Thread(() => HostClientHandler(tcp));
                    t.IsBackground = true;
                    t.Name = "Imp2HostPeer";
                    t.Start();
                }
                catch
                {
                    if (!m_running) break;
                }
            }
        }

        private void HostClientHandler(TcpClient tcp)
        {
            ClientPeer peer = new ClientPeer
            {
                Client = tcp,
                Stream = tcp.GetStream(),
                RemoteEndPoint = tcp.Client.RemoteEndPoint as IPEndPoint
            };
            bool joinedSession = false;

            try
            {
                byte firstType;
                byte[] firstPayload = ReadFrame(peer.Stream, out firstType);
                if (firstPayload == null)
                {
                    peer.Close();
                    return;
                }

                if (firstType == DPlayConstants.PKT_TCP_QUERY_SESSION)
                {
                    Guid reqApp = firstPayload.Length >= 16 ? new Guid(SubArray(firstPayload, 0, 16)) : Guid.Empty;
                    byte[] resp = null;
                    lock (m_lock)
                    {
                        bool joinDisabled = (m_sessionFlags & (DPlayConstants.DPSESSION_NEWPLAYERSDISABLED | DPlayConstants.DPSESSION_JOINDISABLED)) != 0;
                        bool full = m_players.Count >= m_maxPlayers;
                        if (!joinDisabled && !full && (reqApp == Guid.Empty || reqApp == m_guidApplication))
                            resp = BuildSessionPayload();
                    }
                    if (resp != null)
                        SendFrame(peer, DPlayConstants.PKT_TCP_QUERY_RESP, resp);
                    peer.Close();
                    return;
                }

                if (firstType != DPlayConstants.PKT_TCP_JOIN_REQ || firstPayload.Length < 16)
                {
                    peer.Close();
                    return;
                }

                Guid reqInst = new Guid(SubArray(firstPayload, 0, 16));
                byte[] ackPayload;
                lock (m_lock)
                {
                    bool joinDisabled = (m_sessionFlags & (DPlayConstants.DPSESSION_NEWPLAYERSDISABLED | DPlayConstants.DPSESSION_JOINDISABLED)) != 0;
                    bool full = m_players.Count >= m_maxPlayers;
                    if (joinDisabled || full || (reqInst != Guid.Empty && reqInst != m_guidInstance))
                    {
                        SendFrame(peer, DPlayConstants.PKT_TCP_JOIN_ACK, new byte[] { 0 });
                        peer.Close();
                        return;
                    }

                    m_peers.Add(peer);
                    joinedSession = true;

                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter bw = new BinaryWriter(ms))
                    {
                        bw.Write((byte)1); // accepted
                        byte[] sessBytes = BuildSessionPayload();
                        bw.Write(sessBytes.Length);
                        bw.Write(sessBytes);
                        bw.Write(m_players.Count);
                        foreach (KeyValuePair<uint, PlayerRecord> kv in m_players)
                        {
                            byte[] pr = SerializePlayerRecord(kv.Value);
                            bw.Write(pr.Length);
                            bw.Write(pr);
                        }
                        ackPayload = ms.ToArray();
                    }
                }

                SendFrame(peer, DPlayConstants.PKT_TCP_JOIN_ACK, ackPayload);

                while (m_running && !peer.Closed)
                {
                    byte pktType;
                    byte[] payload = ReadFrame(peer.Stream, out pktType);
                    if (payload == null) break;

                    switch (pktType)
                    {
                        case DPlayConstants.PKT_TCP_CREATE_PLAYER:
                        {
                            using (MemoryStream ms = new MemoryStream(payload))
                            using (BinaryReader br = new BinaryReader(ms))
                            {
                                int sLen = br.ReadInt32();
                                string sName = Encoding.ASCII.GetString(br.ReadBytes(sLen));
                                int lLen = br.ReadInt32();
                                string lName = Encoding.ASCII.GetString(br.ReadBytes(lLen));

                                uint newId;
                                byte[] joinedBytes;
                                List<ClientPeer> otherPeers = new List<ClientPeer>();
                                lock (m_lock)
                                {
                                    newId = m_nextDpId++;
                                    PlayerRecord rec = new PlayerRecord
                                    {
                                        PlayerId = newId,
                                        ShortName = sName,
                                        LongName = lName,
                                        IsLocal = false,
                                        Connection = peer,
                                        RemoteData = new byte[0]
                                    };
                                    m_players[newId] = rec;
                                    peer.OwnedPlayers.Add(newId);
                                    joinedBytes = SerializePlayerRecord(rec);
                                    for (int i = 0; i < m_peers.Count; i++)
                                    {
                                        if (m_peers[i] != peer)
                                            otherPeers.Add(m_peers[i]);
                                    }
                                }

                                SendFrame(peer, DPlayConstants.PKT_TCP_CREATE_PLAYER_ACK, BitConverter.GetBytes(newId));
                                for (int i = 0; i < otherPeers.Count; i++)
                                    SendFrame(otherPeers[i], DPlayConstants.PKT_TCP_PLAYER_JOINED, joinedBytes);

                                DPlayConfig.Log("Remote player '{0}' assigned DPID {1}", sName, newId);
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_SET_PLAYER_DATA:
                        {
                            if (payload.Length >= 8)
                            {
                                uint pid = BitConverter.ToUInt32(payload, 0);
                                int dLen = BitConverter.ToInt32(payload, 4);
                                byte[] data = SubArray(payload, 8, dLen);
                                List<ClientPeer> otherPeers = new List<ClientPeer>();
                                lock (m_lock)
                                {
                                    PlayerRecord rec;
                                    if (m_players.TryGetValue(pid, out rec))
                                        rec.RemoteData = data;
                                    for (int i = 0; i < m_peers.Count; i++)
                                    {
                                        if (m_peers[i] != peer)
                                            otherPeers.Add(m_peers[i]);
                                    }
                                }
                                for (int i = 0; i < otherPeers.Count; i++)
                                    SendFrame(otherPeers[i], DPlayConstants.PKT_TCP_SET_PLAYER_DATA, payload);
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_DATA_MSG:
                        {
                            if (payload.Length >= 12)
                            {
                                uint idFrom = BitConverter.ToUInt32(payload, 0);
                                uint idTo = BitConverter.ToUInt32(payload, 4);
                                int dLen = BitConverter.ToInt32(payload, 8);
                                byte[] raw = SubArray(payload, 12, dLen);
                                RouteDataFromHost(idFrom, idTo, raw, payload, peer);
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_PLAYER_LEFT:
                        {
                            if (payload.Length >= 4)
                            {
                                uint pid = BitConverter.ToUInt32(payload, 0);
                                HandlePeerPlayerRemoved(peer, pid);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("HostClientHandler peer ended: {0}", ex.Message);
            }
            finally
            {
                peer.Close();
                if (joinedSession)
                {
                    List<uint> dropped = new List<uint>();
                    lock (m_lock)
                    {
                        m_peers.Remove(peer);
                        dropped.AddRange(peer.OwnedPlayers);
                        peer.OwnedPlayers.Clear();
                    }
                    for (int i = 0; i < dropped.Count; i++)
                        HandlePeerPlayerRemoved(peer, dropped[i]);
                }
            }
        }

        private void HandlePeerPlayerRemoved(ClientPeer sourcePeer, uint pid)
        {
            List<ClientPeer> others = new List<ClientPeer>();
            bool removed = false;
            lock (m_lock)
            {
                sourcePeer.OwnedPlayers.Remove(pid);
                removed = m_players.Remove(pid);
                if (removed)
                {
                    for (int i = 0; i < m_peers.Count; i++)
                    {
                        if (m_peers[i] != sourcePeer)
                            others.Add(m_peers[i]);
                    }
                }
            }
            if (removed)
            {
                DPlayConfig.Log("Player DPID {0} disconnected -> queuing DPSYS_DESTROYPLAYERORGROUP", pid);
                EnqueueSystemDestroyPlayer(pid);
                byte[] body = BitConverter.GetBytes(pid);
                for (int i = 0; i < others.Count; i++)
                    SendFrame(others[i], DPlayConstants.PKT_TCP_PLAYER_LEFT, body);
            }
        }

        private void ClientReadLoop(ClientPeer hostPeer)
        {
            try
            {
                while (m_running && !hostPeer.Closed)
                {
                    byte pktType;
                    byte[] payload = ReadFrame(hostPeer.Stream, out pktType);
                    if (payload == null) break;

                    switch (pktType)
                    {
                        case DPlayConstants.PKT_TCP_JOIN_ACK:
                        {
                            if (payload.Length >= 1 && payload[0] == 1)
                            {
                                using (MemoryStream ms = new MemoryStream(payload, 1, payload.Length - 1))
                                using (BinaryReader br = new BinaryReader(ms))
                                {
                                    int sLen = br.ReadInt32();
                                    byte[] sBytes = br.ReadBytes(sLen);
                                    DiscoveredSession ds = ParseSessionPayload(sBytes, hostPeer.RemoteEndPoint, null);
                                    int playerCount = br.ReadInt32();
                                    lock (m_lock)
                                    {
                                        if (ds != null)
                                        {
                                            m_guidInstance = ds.GuidInstance;
                                            m_guidApplication = ds.GuidApplication;
                                            m_maxPlayers = ds.MaxPlayers;
                                            m_sessionFlags = ds.Flags;
                                            m_sessionName = ds.SessionName;
                                        }
                                        m_players.Clear();
                                        for (int i = 0; i < playerCount; i++)
                                        {
                                            int pLen = br.ReadInt32();
                                            PlayerRecord pr = DeserializePlayerRecord(br.ReadBytes(pLen), false);
                                            m_players[pr.PlayerId] = pr;
                                        }
                                    }
                                    m_joinAccepted = true;
                                }
                            }
                            m_joinAckEvent.Set();
                            break;
                        }
                        case DPlayConstants.PKT_TCP_CREATE_PLAYER_ACK:
                        {
                            if (payload.Length >= 4)
                            {
                                uint assignedId = BitConverter.ToUInt32(payload, 0);
                                lock (m_lock)
                                {
                                    PlayerRecord rec;
                                    if (!m_players.TryGetValue(assignedId, out rec))
                                    {
                                        rec = new PlayerRecord { PlayerId = assignedId, IsLocal = true };
                                        m_players[assignedId] = rec;
                                    }
                                    else
                                    {
                                        rec.IsLocal = true;
                                    }
                                }
                                m_createdPlayerIdResult = assignedId;
                            }
                            m_createPlayerAckEvent.Set();
                            break;
                        }
                        case DPlayConstants.PKT_TCP_PLAYER_JOINED:
                        {
                            PlayerRecord pr = DeserializePlayerRecord(payload, false);
                            lock (m_lock)
                            {
                                if (!m_players.ContainsKey(pr.PlayerId))
                                    m_players[pr.PlayerId] = pr;
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_PLAYER_LEFT:
                        {
                            if (payload.Length >= 4)
                            {
                                uint pid = BitConverter.ToUInt32(payload, 0);
                                bool had;
                                lock (m_lock) { had = m_players.Remove(pid); }
                                if (had) EnqueueSystemDestroyPlayer(pid);
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_SET_PLAYER_DATA:
                        {
                            if (payload.Length >= 8)
                            {
                                uint pid = BitConverter.ToUInt32(payload, 0);
                                int dLen = BitConverter.ToInt32(payload, 4);
                                byte[] d = SubArray(payload, 8, dLen);
                                lock (m_lock)
                                {
                                    PlayerRecord rec;
                                    if (m_players.TryGetValue(pid, out rec))
                                        rec.RemoteData = d;
                                }
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_SET_SESSION_DESC:
                        {
                            DiscoveredSession ds = ParseSessionPayload(payload, hostPeer.RemoteEndPoint, null);
                            if (ds != null)
                            {
                                lock (m_lock)
                                {
                                    m_sessionFlags = ds.Flags;
                                    m_maxPlayers = ds.MaxPlayers;
                                    m_sessionName = ds.SessionName;
                                    // Queue DPSYS_SETSESSIONDESC (0x0104 + 80-byte DPSESSIONDESC2)
                                    byte[] sysMsg = new byte[84];
                                    Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.DPSYS_SETSESSIONDESC), 0, sysMsg, 0, 4);
                                    Buffer.BlockCopy(BitConverter.GetBytes(80u), 0, sysMsg, 4, 4);
                                    Buffer.BlockCopy(BitConverter.GetBytes(m_sessionFlags), 0, sysMsg, 8, 4);
                                    Buffer.BlockCopy(m_guidInstance.ToByteArray(), 0, sysMsg, 12, 16);
                                    Buffer.BlockCopy(m_guidApplication.ToByteArray(), 0, sysMsg, 28, 16);
                                    Buffer.BlockCopy(BitConverter.GetBytes(m_maxPlayers), 0, sysMsg, 44, 4);
                                    Buffer.BlockCopy(BitConverter.GetBytes((uint)m_players.Count), 0, sysMsg, 48, 4);
                                    m_recvQueue.Enqueue(new QueuedMessage { IdFrom = DPlayConstants.DPID_SYSMSG, IdTo = 0, Data = sysMsg });
                                }
                            }
                            break;
                        }
                        case DPlayConstants.PKT_TCP_DATA_MSG:
                        {
                            if (payload.Length >= 12)
                            {
                                uint idFrom = BitConverter.ToUInt32(payload, 0);
                                uint idTo = BitConverter.ToUInt32(payload, 4);
                                int dLen = BitConverter.ToInt32(payload, 8);
                                byte[] raw = SubArray(payload, 12, dLen);
                                lock (m_lock)
                                {
                                    m_recvQueue.Enqueue(new QueuedMessage { IdFrom = idFrom, IdTo = idTo, Data = raw });
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("ClientReadLoop disconnected: {0}", ex.Message);
            }
            finally
            {
                bool wasOpen = m_isOpen && m_running;
                hostPeer.Close();
                m_joinAckEvent.Set();
                m_createPlayerAckEvent.Set();
                if (wasOpen)
                {
                    DPlayConfig.Log("Host connection lost -> queuing DPSYS_SESSIONLOST");
                    EnqueueSystemSessionLost();
                }
            }
        }

        private byte[] BuildDiscoveryRequest(Guid guidApp)
        {
            byte[] req = new byte[21];
            Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.MAGIC), 0, req, 0, 4);
            req[4] = DPlayConstants.PKT_UDP_DISCOVER_REQ;
            Buffer.BlockCopy(guidApp.ToByteArray(), 0, req, 5, 16);
            return req;
        }

        private byte[] BuildSessionPayload()
        {
            // [0..15] guidInstance, [16..31] guidApp, [32..35] maxPlayers, [36..39] curPlayers, [40..43] flags, [44..47] tcpPort, [48] nameLen, [49..] name
            byte[] nameBytes = Encoding.ASCII.GetBytes(m_sessionName ?? "Imperialism II");
            if (nameBytes.Length > 200) nameBytes = SubArray(nameBytes, 0, 200);
            byte[] buf = new byte[49 + nameBytes.Length];
            Buffer.BlockCopy(m_guidInstance.ToByteArray(), 0, buf, 0, 16);
            Buffer.BlockCopy(m_guidApplication.ToByteArray(), 0, buf, 16, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(m_maxPlayers), 0, buf, 32, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)Math.Max(1, m_players.Count)), 0, buf, 36, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(m_sessionFlags), 0, buf, 40, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(m_activePort), 0, buf, 44, 4);
            buf[48] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, buf, 49, nameBytes.Length);
            return buf;
        }

        private static DiscoveredSession ParseSessionPayload(byte[] payload, IPEndPoint hostEp, IPAddress localIp)
        {
            if (payload == null || payload.Length < 49) return null;
            Guid gInst = new Guid(SubArray(payload, 0, 16));
            Guid gApp = new Guid(SubArray(payload, 16, 16));
            uint maxP = BitConverter.ToUInt32(payload, 32);
            uint curP = BitConverter.ToUInt32(payload, 36);
            uint flags = BitConverter.ToUInt32(payload, 40);
            int nameLen = payload[48];
            if (49 + nameLen > payload.Length) nameLen = payload.Length - 49;
            string sName = Encoding.ASCII.GetString(payload, 49, nameLen);

            return new DiscoveredSession
            {
                GuidInstance = gInst,
                GuidApplication = gApp,
                MaxPlayers = maxP,
                CurrentPlayers = curP,
                Flags = flags,
                SessionName = sName,
                HostEndPoint = hostEp,
                LocalBindIP = localIp
            };
        }

        private static byte[] SerializePlayerRecord(PlayerRecord pr)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(pr.PlayerId);
                byte[] sb = Encoding.ASCII.GetBytes(pr.ShortName ?? "");
                bw.Write(sb.Length);
                bw.Write(sb);
                byte[] lb = Encoding.ASCII.GetBytes(pr.LongName ?? "");
                bw.Write(lb.Length);
                bw.Write(lb);
                byte[] rd = pr.RemoteData ?? new byte[0];
                bw.Write(rd.Length);
                bw.Write(rd);
                return ms.ToArray();
            }
        }

        private static PlayerRecord DeserializePlayerRecord(byte[] bytes, bool isLocal)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
            using (BinaryReader br = new BinaryReader(ms))
            {
                uint pid = br.ReadUInt32();
                int sLen = br.ReadInt32();
                string sName = Encoding.ASCII.GetString(br.ReadBytes(sLen));
                int lLen = br.ReadInt32();
                string lName = Encoding.ASCII.GetString(br.ReadBytes(lLen));
                int dLen = br.ReadInt32();
                byte[] rd = br.ReadBytes(dLen);
                return new PlayerRecord
                {
                    PlayerId = pid,
                    ShortName = sName,
                    LongName = lName,
                    RemoteData = rd,
                    IsLocal = isLocal
                };
            }
        }

        private static bool SendFrame(ClientPeer peer, byte pktType, byte[] payload)
        {
            if (peer == null || peer.Closed) return false;
            try
            {
                lock (peer.WriteLock)
                {
                    if (peer.Closed) return false;
                    SendFrame(peer.Stream, pktType, payload);
                }
                return true;
            }
            catch
            {
                peer.Close();
                return false;
            }
        }

        private static void SendFrame(NetworkStream ns, byte pktType, byte[] payload)
        {
            int len = payload != null ? payload.Length : 0;
            byte[] hdr = new byte[9];
            Buffer.BlockCopy(BitConverter.GetBytes(DPlayConstants.MAGIC), 0, hdr, 0, 4);
            hdr[4] = pktType;
            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, hdr, 5, 4);
            ns.Write(hdr, 0, 9);
            if (len > 0)
                ns.Write(payload, 0, len);
            ns.Flush();
        }

        private static byte[] ReadFrame(NetworkStream ns, out byte pktType)
        {
            pktType = 0;
            byte[] hdr = ReadExact(ns, 9);
            if (hdr == null) return null;
            uint magic = BitConverter.ToUInt32(hdr, 0);
            if (magic != DPlayConstants.MAGIC) return null;
            pktType = hdr[4];
            int len = BitConverter.ToInt32(hdr, 5);
            if (len < 0 || len > 16 * 1024 * 1024) return null;
            if (len == 0) return new byte[0];
            return ReadExact(ns, len);
        }

        private static byte[] ReadExact(NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int readTotal = 0;
            while (readTotal < count)
            {
                int r = ns.Read(buf, readTotal, count - readTotal);
                if (r <= 0) return null;
                readTotal += r;
            }
            return buf;
        }

        private static byte[] SubArray(byte[] src, int offset, int count)
        {
            if (count <= 0 || offset >= src.Length) return new byte[0];
            if (offset + count > src.Length) count = src.Length - offset;
            byte[] dst = new byte[count];
            Buffer.BlockCopy(src, offset, dst, 0, count);
            return dst;
        }

        public void ShutdownSession()
        {
            m_running = false;
            m_isOpen = false;
            m_isHost = false;

            try { if (m_udpListener != null) m_udpListener.Close(); } catch { }
            m_udpListener = null;

            try { if (m_tcpListener != null) m_tcpListener.Stop(); } catch { }
            m_tcpListener = null;

            if (m_hostConn != null)
            {
                m_hostConn.Close();
                m_hostConn = null;
            }

            List<ClientPeer> peersSnap;
            lock (m_lock)
            {
                peersSnap = new List<ClientPeer>(m_peers);
                m_peers.Clear();
                m_players.Clear();
                m_recvQueue.Clear();
            }
            for (int i = 0; i < peersSnap.Count; i++)
                peersSnap[i].Close();

            m_joinAckEvent.Set();
            m_createPlayerAckEvent.Set();
        }

        public void Dispose()
        {
            ShutdownSession();
        }
    }

    // Native COM Wrapper for IDirectPlay4A & IDirectPlayLobby3A
    internal static class ComRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int DPEnumCallbackA(IntPtr lpguidSP, IntPtr lpSPName, uint dwMajorVersion, uint dwMinorVersion, IntPtr lpContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumSessionsCallback2(IntPtr lpThisSD, ref uint lpdwTimeOut, uint dwFlags, IntPtr lpContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumPlayersCallback2(uint dpId, uint dwPlayerType, IntPtr lpName, uint dwFlags, IntPtr lpContext);

        // NOTE: Verified against Imperialism II.exe VA 0x00490B40 - CDirectPlay::EnumAddressCallback is __cdecl!
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumAddressCallbackCdecl(IntPtr guidDataType, uint dwDataSize, IntPtr lpData, IntPtr lpContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QI_Del(IntPtr self, IntPtr riid, IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefRel_Del(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Close_Del(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreatePlayer_Del(IntPtr self, IntPtr lpidPlayer, IntPtr lpPlayerName, IntPtr hEvent, IntPtr lpData, uint dwDataSize, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DestroyPlayer_Del(IntPtr self, uint idPlayer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumPlayers_Del(IntPtr self, IntPtr lpguidInstance, IntPtr lpEnumPlayersCallback2, IntPtr lpContext, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumSessions_Del(IntPtr self, IntPtr lpsd, uint dwTimeout, IntPtr lpEnumSessionsCallback2, IntPtr lpContext, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPlayerAddress_Del(IntPtr self, uint idPlayer, IntPtr lpAddress, IntPtr lpdwAddressSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPlayerData_Del(IntPtr self, uint idPlayer, IntPtr lpData, IntPtr lpdwDataSize, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSessionDesc_Del(IntPtr self, IntPtr lpData, IntPtr lpdwDataSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Open_Del(IntPtr self, IntPtr lpsd, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Receive_Del(IntPtr self, IntPtr lpidFrom, IntPtr lpidTo, uint dwFlags, IntPtr lpData, IntPtr lpdwDataSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Send_Del(IntPtr self, uint idFrom, uint idTo, uint dwFlags, IntPtr lpData, uint dwDataSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetPlayerData_Del(IntPtr self, uint idPlayer, IntPtr lpData, uint dwDataSize, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetSessionDesc_Del(IntPtr self, IntPtr lpSessDesc, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeConnection_Del(IntPtr self, IntPtr lpConnection, uint dwFlags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Stub0_Del(IntPtr self);

        // Lobby delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAddress_Del(IntPtr self, IntPtr lpEnumAddressCallback, IntPtr lpAddress, uint dwAddressSize, IntPtr lpContext);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetConnectionSettings_Del(IntPtr self, uint dwAppID, IntPtr lpData, IntPtr lpdwDataSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateCompoundAddress_Del(IntPtr self, IntPtr lpElements, uint dwElementCount, IntPtr lpAddress, IntPtr lpdwAddressSize);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Connect_Del(IntPtr self, uint dwFlags, IntPtr lplpDP, IntPtr pUnk);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConnectEx_Del(IntPtr self, uint dwFlags, IntPtr riid, IntPtr lplpDP, IntPtr pUnk);

        // ClassFactory delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CF_CreateInstance_Del(IntPtr self, IntPtr pUnkOuter, IntPtr riid, IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CF_LockServer_Del(IntPtr self, int fLock);

        private static readonly List<Delegate> s_pinnedDelegates = new List<Delegate>();
        private static IntPtr s_dpVtable;
        private static IntPtr s_lobbyVtable;
        private static IntPtr s_cfVtable;
        private static readonly object s_instLock = new object();
        private static readonly Dictionary<IntPtr, DPlayComObject> s_dpObjects = new Dictionary<IntPtr, DPlayComObject>();
        private static readonly Dictionary<IntPtr, LobbyComObject> s_lobbyObjects = new Dictionary<IntPtr, LobbyComObject>();
        private static readonly Dictionary<IntPtr, FactoryComObject> s_cfObjects = new Dictionary<IntPtr, FactoryComObject>();

        private sealed class DPlayComObject
        {
            public IntPtr Ptr;
            public int RefCount = 1;
            public DPlaySessionEngine Engine = new DPlaySessionEngine();
            public IntPtr LastSessionNameAlloc = IntPtr.Zero;

            public void FreeNameAlloc()
            {
                if (LastSessionNameAlloc != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(LastSessionNameAlloc);
                    LastSessionNameAlloc = IntPtr.Zero;
                }
            }
        }

        private sealed class LobbyComObject
        {
            public IntPtr Ptr;
            public int RefCount = 1;
        }

        private sealed class FactoryComObject
        {
            public IntPtr Ptr;
            public int RefCount = 1;
            public Guid Clsid;
        }

        private static IntPtr Pin(Delegate d)
        {
            s_pinnedDelegates.Add(d);
            return Marshal.GetFunctionPointerForDelegate(d);
        }

        private static void EnsureVtables()
        {
            lock (s_instLock)
            {
                if (s_dpVtable != IntPtr.Zero) return;

                IntPtr unimpl = Pin(new Stub0_Del(DP_Unimplemented));

                // IDirectPlay4A has 53 vtable slots (0..52)
                s_dpVtable = Marshal.AllocHGlobal(53 * IntPtr.Size);
                for (int i = 0; i < 53; i++)
                    Marshal.WriteIntPtr(s_dpVtable, i * IntPtr.Size, unimpl);

                Marshal.WriteIntPtr(s_dpVtable, 0 * IntPtr.Size, Pin(new QI_Del(DP_QueryInterface)));
                Marshal.WriteIntPtr(s_dpVtable, 1 * IntPtr.Size, Pin(new AddRefRel_Del(DP_AddRef)));
                Marshal.WriteIntPtr(s_dpVtable, 2 * IntPtr.Size, Pin(new AddRefRel_Del(DP_Release)));
                Marshal.WriteIntPtr(s_dpVtable, 4 * IntPtr.Size, Pin(new Close_Del(DP_Close)));
                Marshal.WriteIntPtr(s_dpVtable, 6 * IntPtr.Size, Pin(new CreatePlayer_Del(DP_CreatePlayer)));
                Marshal.WriteIntPtr(s_dpVtable, 9 * IntPtr.Size, Pin(new DestroyPlayer_Del(DP_DestroyPlayer)));
                Marshal.WriteIntPtr(s_dpVtable, 12 * IntPtr.Size, Pin(new EnumPlayers_Del(DP_EnumPlayers)));
                Marshal.WriteIntPtr(s_dpVtable, 13 * IntPtr.Size, Pin(new EnumSessions_Del(DP_EnumSessions)));
                Marshal.WriteIntPtr(s_dpVtable, 18 * IntPtr.Size, Pin(new GetPlayerAddress_Del(DP_GetPlayerAddress)));
                Marshal.WriteIntPtr(s_dpVtable, 20 * IntPtr.Size, Pin(new GetPlayerData_Del(DP_GetPlayerData)));
                Marshal.WriteIntPtr(s_dpVtable, 22 * IntPtr.Size, Pin(new GetSessionDesc_Del(DP_GetSessionDesc)));
                Marshal.WriteIntPtr(s_dpVtable, 24 * IntPtr.Size, Pin(new Open_Del(DP_Open)));
                Marshal.WriteIntPtr(s_dpVtable, 25 * IntPtr.Size, Pin(new Receive_Del(DP_Receive)));
                Marshal.WriteIntPtr(s_dpVtable, 26 * IntPtr.Size, Pin(new Send_Del(DP_Send)));
                Marshal.WriteIntPtr(s_dpVtable, 29 * IntPtr.Size, Pin(new SetPlayerData_Del(DP_SetPlayerData)));
                Marshal.WriteIntPtr(s_dpVtable, 31 * IntPtr.Size, Pin(new SetSessionDesc_Del(DP_SetSessionDesc)));
                Marshal.WriteIntPtr(s_dpVtable, 38 * IntPtr.Size, Pin(new InitializeConnection_Del(DP_InitializeConnection)));

                // IDirectPlayLobby3A has 19 vtable slots (0..18)
                s_lobbyVtable = Marshal.AllocHGlobal(19 * IntPtr.Size);
                for (int i = 0; i < 19; i++)
                    Marshal.WriteIntPtr(s_lobbyVtable, i * IntPtr.Size, unimpl);

                Marshal.WriteIntPtr(s_lobbyVtable, 0 * IntPtr.Size, Pin(new QI_Del(Lobby_QueryInterface)));
                Marshal.WriteIntPtr(s_lobbyVtable, 1 * IntPtr.Size, Pin(new AddRefRel_Del(Lobby_AddRef)));
                Marshal.WriteIntPtr(s_lobbyVtable, 2 * IntPtr.Size, Pin(new AddRefRel_Del(Lobby_Release)));
                Marshal.WriteIntPtr(s_lobbyVtable, 3 * IntPtr.Size, Pin(new Connect_Del(Lobby_Connect)));
                Marshal.WriteIntPtr(s_lobbyVtable, 5 * IntPtr.Size, Pin(new EnumAddress_Del(Lobby_EnumAddress)));
                Marshal.WriteIntPtr(s_lobbyVtable, 8 * IntPtr.Size, Pin(new GetConnectionSettings_Del(Lobby_GetConnectionSettings)));
                Marshal.WriteIntPtr(s_lobbyVtable, 14 * IntPtr.Size, Pin(new CreateCompoundAddress_Del(Lobby_CreateCompoundAddress)));
                Marshal.WriteIntPtr(s_lobbyVtable, 15 * IntPtr.Size, Pin(new ConnectEx_Del(Lobby_ConnectEx)));

                // IClassFactory (5 slots)
                s_cfVtable = Marshal.AllocHGlobal(5 * IntPtr.Size);
                Marshal.WriteIntPtr(s_cfVtable, 0 * IntPtr.Size, Pin(new QI_Del(CF_QueryInterface)));
                Marshal.WriteIntPtr(s_cfVtable, 1 * IntPtr.Size, Pin(new AddRefRel_Del(CF_AddRef)));
                Marshal.WriteIntPtr(s_cfVtable, 2 * IntPtr.Size, Pin(new AddRefRel_Del(CF_Release)));
                Marshal.WriteIntPtr(s_cfVtable, 3 * IntPtr.Size, Pin(new CF_CreateInstance_Del(CF_CreateInstance)));
                Marshal.WriteIntPtr(s_cfVtable, 4 * IntPtr.Size, Pin(new CF_LockServer_Del(CF_LockServer)));
            }
        }

        public static IntPtr CreateDirectPlayInstance()
        {
            EnsureVtables();
            IntPtr mem = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(mem, s_dpVtable);
            DPlayComObject obj = new DPlayComObject { Ptr = mem, RefCount = 1 };
            lock (s_instLock) { s_dpObjects[mem] = obj; }
            return mem;
        }

        public static IntPtr CreateLobbyInstance()
        {
            EnsureVtables();
            IntPtr mem = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(mem, s_lobbyVtable);
            LobbyComObject obj = new LobbyComObject { Ptr = mem, RefCount = 1 };
            lock (s_instLock) { s_lobbyObjects[mem] = obj; }
            return mem;
        }

        public static IntPtr CreateFactoryInstance(Guid clsid)
        {
            EnsureVtables();
            IntPtr mem = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(mem, s_cfVtable);
            FactoryComObject obj = new FactoryComObject { Ptr = mem, RefCount = 1, Clsid = clsid };
            lock (s_instLock) { s_cfObjects[mem] = obj; }
            return mem;
        }

        private static DPlayComObject GetDP(IntPtr self)
        {
            lock (s_instLock)
            {
                DPlayComObject o;
                return s_dpObjects.TryGetValue(self, out o) ? o : null;
            }
        }

        internal static Guid ReadGuid(IntPtr p)
        {
            byte[] b = new byte[16];
            Marshal.Copy(p, b, 0, 16);
            return new Guid(b);
        }

        private static void WriteGuid(IntPtr p, Guid g)
        {
            byte[] b = g.ToByteArray();
            Marshal.Copy(b, 0, p, 16);
        }

        private static int DP_Unimplemented(IntPtr self)
        {
            DPlayConfig.Log("WARN: Unimplemented vtable method called on 0x{0:X}", self.ToInt32());
            return DPlayConstants.DPERR_UNSUPPORTED;
        }

        private static int DP_QueryInterface(IntPtr self, IntPtr riid, IntPtr ppv)
        {
            if (ppv == IntPtr.Zero) return DPlayConstants.E_POINTER;
            Guid iid = ReadGuid(riid);
            if (iid == DPlayConstants.IID_IUnknown ||
                iid == DPlayConstants.IID_IDirectPlay4A ||
                iid == DPlayConstants.IID_IDirectPlay3A ||
                iid == DPlayConstants.IID_IDirectPlay2A)
            {
                DP_AddRef(self);
                Marshal.WriteIntPtr(ppv, self);
                return DPlayConstants.DP_OK;
            }
            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return DPlayConstants.E_NOINTERFACE;
        }

        private static uint DP_AddRef(IntPtr self)
        {
            lock (s_instLock)
            {
                DPlayComObject o;
                if (s_dpObjects.TryGetValue(self, out o))
                    return (uint)(++o.RefCount);
            }
            return 1;
        }

        private static uint DP_Release(IntPtr self)
        {
            DPlayComObject toDispose = null;
            uint rc = 0;
            lock (s_instLock)
            {
                DPlayComObject o;
                if (s_dpObjects.TryGetValue(self, out o))
                {
                    rc = (uint)(--o.RefCount);
                    if (o.RefCount <= 0)
                    {
                        s_dpObjects.Remove(self);
                        toDispose = o;
                    }
                }
            }
            if (toDispose != null)
            {
                toDispose.Engine.Dispose();
                toDispose.FreeNameAlloc();
                Marshal.FreeHGlobal(self);
            }
            return rc;
        }

        private static int DP_Close(IntPtr self)
        {
            DPlayComObject o = GetDP(self);
            if (o != null) o.Engine.ShutdownSession();
            return DPlayConstants.DP_OK;
        }

        private static int DP_InitializeConnection(IntPtr self, IntPtr lpConnection, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null) return DPlayConstants.DPERR_GENERIC;

            string inet = "";
            if (lpConnection != IntPtr.Zero)
            {
                inet = ExtractINetFromCompoundAddress(lpConnection);
            }
            return o.Engine.InitializeConnection(inet);
        }

        private static string ExtractINetFromCompoundAddress(IntPtr lpConnection)
        {
            try
            {
                // Walk DirectPlay compound address chunks: [GUID 16B][DWORD size 4B][data]
                // First chunk is typically DPAID_TotalSize (size=4, DWORD totalBytes)
                int totalLen = 512;
                Guid firstGuid = ReadGuid(lpConnection);
                int firstSize = Marshal.ReadInt32(lpConnection, 16);
                int pos = 0;
                if (firstGuid == DPlayConstants.DPAID_TotalSize && firstSize == 4)
                {
                    totalLen = Marshal.ReadInt32(lpConnection, 20);
                    pos = 24;
                }

                while (pos + 20 <= totalLen && pos < 4096)
                {
                    Guid g = ReadGuid(new IntPtr(lpConnection.ToInt64() + pos));
                    int sz = Marshal.ReadInt32(lpConnection, pos + 16);
                    if (sz < 0 || pos + 20 + sz > totalLen) break;
                    if (g == DPlayConstants.DPAID_INet && sz > 0)
                    {
                        byte[] raw = new byte[sz];
                        Marshal.Copy(new IntPtr(lpConnection.ToInt64() + pos + 20), raw, 0, sz);
                        int nul = Array.IndexOf(raw, (byte)0);
                        int strLen = nul >= 0 ? nul : sz;
                        return Encoding.ASCII.GetString(raw, 0, strLen);
                    }
                    pos += 20 + sz;
                }
            }
            catch (Exception ex)
            {
                DPlayConfig.Log("ExtractINetFromCompoundAddress error: {0}", ex.Message);
            }
            return "";
        }

        private static int DP_Open(IntPtr self, IntPtr lpsd, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpsd == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            uint sFlags = (uint)Marshal.ReadInt32(lpsd, 0x04);
            Guid gInst = ReadGuid(new IntPtr(lpsd.ToInt64() + 0x08));
            Guid gApp = ReadGuid(new IntPtr(lpsd.ToInt64() + 0x18));
            uint maxPlayers = (uint)Marshal.ReadInt32(lpsd, 0x28);
            IntPtr pName = Marshal.ReadIntPtr(lpsd, 0x30);
            string sessName = pName != IntPtr.Zero ? Marshal.PtrToStringAnsi(pName) : "Imperialism II";

            if ((dwFlags & DPlayConstants.DPOPEN_CREATE) != 0)
            {
                int hr = o.Engine.HostSession(ref gInst, gApp, maxPlayers, sFlags, sessName);
                if (hr == DPlayConstants.DP_OK)
                    WriteGuid(new IntPtr(lpsd.ToInt64() + 0x08), gInst);
                return hr;
            }
            else if ((dwFlags & DPlayConstants.DPOPEN_JOIN) != 0)
            {
                return o.Engine.JoinSession(gInst);
            }
            return DPlayConstants.DPERR_INVALIDPARAM;
        }

        private static int DP_EnumSessions(IntPtr self, IntPtr lpsd, uint dwTimeout, IntPtr lpCallback, IntPtr lpContext, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpsd == IntPtr.Zero || lpCallback == IntPtr.Zero)
                return DPlayConstants.DPERR_INVALIDPARAM;

            Guid gApp = ReadGuid(new IntPtr(lpsd.ToInt64() + 0x18));
            List<DiscoveredSession> found = o.Engine.EnumerateSessions(gApp, dwTimeout);

            EnumSessionsCallback2 cb = (EnumSessionsCallback2)Marshal.GetDelegateForFunctionPointer(lpCallback, typeof(EnumSessionsCallback2));
            IntPtr sdBuf = Marshal.AllocHGlobal(80);
            try
            {
                for (int i = 0; i < found.Count; i++)
                {
                    DiscoveredSession ds = found[i];
                    string displayName = ds.SessionName;
                    if (found.Count > 1 && ds.HostEndPoint != null)
                        displayName = string.Format("{0} [{1}]", ds.SessionName, ds.HostEndPoint.Address);

                    IntPtr namePtr = Marshal.StringToHGlobalAnsi(displayName);
                    try
                    {
                        for (int z = 0; z < 80; z += 4) Marshal.WriteInt32(sdBuf, z, 0);
                        Marshal.WriteInt32(sdBuf, 0x00, 80);
                        Marshal.WriteInt32(sdBuf, 0x04, (int)ds.Flags);
                        WriteGuid(new IntPtr(sdBuf.ToInt64() + 0x08), ds.GuidInstance);
                        WriteGuid(new IntPtr(sdBuf.ToInt64() + 0x18), ds.GuidApplication);
                        Marshal.WriteInt32(sdBuf, 0x28, (int)ds.MaxPlayers);
                        Marshal.WriteInt32(sdBuf, 0x2C, (int)ds.CurrentPlayers);
                        Marshal.WriteIntPtr(sdBuf, 0x30, namePtr);

                        uint to = dwTimeout;
                        int cont = cb(sdBuf, ref to, 0, lpContext);
                        if (cont == 0) break;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(namePtr);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sdBuf);
            }
            return DPlayConstants.DP_OK;
        }

        private static int DP_CreatePlayer(IntPtr self, IntPtr lpidPlayer, IntPtr lpPlayerName, IntPtr hEvent, IntPtr lpData, uint dwDataSize, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpidPlayer == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            string shortName = "";
            string longName = "";
            if (lpPlayerName != IntPtr.Zero)
            {
                IntPtr ps = Marshal.ReadIntPtr(lpPlayerName, 0x08);
                IntPtr pl = Marshal.ReadIntPtr(lpPlayerName, 0x0C);
                if (ps != IntPtr.Zero) shortName = Marshal.PtrToStringAnsi(ps) ?? "";
                if (pl != IntPtr.Zero) longName = Marshal.PtrToStringAnsi(pl) ?? "";
            }

            uint pid;
            int hr = o.Engine.CreatePlayer(shortName, longName, out pid);
            if (hr == DPlayConstants.DP_OK)
            {
                Marshal.WriteInt32(lpidPlayer, (int)pid);
                if (lpData != IntPtr.Zero && dwDataSize > 0)
                {
                    byte[] initData = new byte[dwDataSize];
                    Marshal.Copy(lpData, initData, 0, (int)dwDataSize);
                    o.Engine.SetPlayerData(pid, initData);
                }
            }
            return hr;
        }

        private static int DP_DestroyPlayer(IntPtr self, uint idPlayer)
        {
            DPlayComObject o = GetDP(self);
            if (o == null) return DPlayConstants.DP_OK;
            return o.Engine.DestroyPlayer(idPlayer);
        }

        private static int DP_EnumPlayers(IntPtr self, IntPtr lpguidInstance, IntPtr lpCallback, IntPtr lpContext, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpCallback == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            List<PlayerRecord> players = o.Engine.GetPlayersSnapshot(dwFlags);
            EnumPlayersCallback2 cb = (EnumPlayersCallback2)Marshal.GetDelegateForFunctionPointer(lpCallback, typeof(EnumPlayersCallback2));

            IntPtr dpName = Marshal.AllocHGlobal(16);
            try
            {
                for (int i = 0; i < players.Count; i++)
                {
                    PlayerRecord pr = players[i];
                    IntPtr ps = Marshal.StringToHGlobalAnsi(pr.ShortName ?? "");
                    IntPtr pl = Marshal.StringToHGlobalAnsi(pr.LongName ?? "");
                    try
                    {
                        Marshal.WriteInt32(dpName, 0x00, 16);
                        Marshal.WriteInt32(dpName, 0x04, 0);
                        Marshal.WriteIntPtr(dpName, 0x08, ps);
                        Marshal.WriteIntPtr(dpName, 0x0C, pl);

                        int cont = cb(pr.PlayerId, DPlayConstants.DPPLAYERTYPE_PLAYER, dpName, dwFlags, lpContext);
                        if (cont == 0) break;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ps);
                        Marshal.FreeHGlobal(pl);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dpName);
            }
            return DPlayConstants.DP_OK;
        }

        private static int DP_SetPlayerData(IntPtr self, uint idPlayer, IntPtr lpData, uint dwDataSize, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null) return DPlayConstants.DPERR_GENERIC;
            byte[] buf = new byte[dwDataSize];
            if (lpData != IntPtr.Zero && dwDataSize > 0)
                Marshal.Copy(lpData, buf, 0, (int)dwDataSize);
            return o.Engine.SetPlayerData(idPlayer, buf);
        }

        private static int DP_GetPlayerData(IntPtr self, uint idPlayer, IntPtr lpData, IntPtr lpdwDataSize, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpdwDataSize == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            byte[] data;
            int hr = o.Engine.GetPlayerData(idPlayer, out data);
            if (hr != DPlayConstants.DP_OK) return hr;

            int cap = Marshal.ReadInt32(lpdwDataSize);
            Marshal.WriteInt32(lpdwDataSize, data.Length);
            if (lpData == IntPtr.Zero || cap < data.Length)
                return data.Length == 0 ? DPlayConstants.DP_OK : DPlayConstants.DPERR_BUFFERTOOSMALL;

            if (data.Length > 0)
                Marshal.Copy(data, 0, lpData, data.Length);
            return DPlayConstants.DP_OK;
        }

        private static int DP_Send(IntPtr self, uint idFrom, uint idTo, uint dwFlags, IntPtr lpData, uint dwDataSize)
        {
            DPlayComObject o = GetDP(self);
            if (o == null) return DPlayConstants.DPERR_NOCONNECTION;
            byte[] buf = new byte[dwDataSize];
            if (lpData != IntPtr.Zero && dwDataSize > 0)
                Marshal.Copy(lpData, buf, 0, (int)dwDataSize);
            return o.Engine.SendMessage(idFrom, idTo, buf);
        }

        private static int DP_Receive(IntPtr self, IntPtr lpidFrom, IntPtr lpidTo, uint dwFlags, IntPtr lpData, IntPtr lpdwDataSize)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpdwDataSize == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            int cap = lpData == IntPtr.Zero ? -1 : Marshal.ReadInt32(lpdwDataSize);
            uint idFrom, idTo;
            byte[] msg;
            int reqSize;

            if (!o.Engine.PeekOrDequeueMessage(cap, out idFrom, out idTo, out msg, out reqSize))
                return DPlayConstants.DPERR_NOMESSAGES;

            Marshal.WriteInt32(lpdwDataSize, reqSize);
            if (lpidFrom != IntPtr.Zero) Marshal.WriteInt32(lpidFrom, (int)idFrom);
            if (lpidTo != IntPtr.Zero) Marshal.WriteInt32(lpidTo, (int)idTo);

            if (lpData == IntPtr.Zero || cap < reqSize)
                return DPlayConstants.DPERR_BUFFERTOOSMALL;

            if (reqSize > 0)
                Marshal.Copy(msg, 0, lpData, reqSize);
            return DPlayConstants.DP_OK;
        }

        private static int DP_GetPlayerAddress(IntPtr self, uint idPlayer, IntPtr lpAddress, IntPtr lpdwAddressSize)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpdwAddressSize == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            string ipStr = o.Engine.GetPlayerIPString(idPlayer);
            byte[] ipBytes = Encoding.ASCII.GetBytes(ipStr + "\0");
            byte[] compound = BuildCompoundAddressBuffer(new CompoundElement[]
            {
                new CompoundElement { DataType = DPlayConstants.DPAID_ServiceProvider, Data = DPlayConstants.DPSPGUID_TCPIP.ToByteArray() },
                new CompoundElement { DataType = DPlayConstants.DPAID_INet, Data = ipBytes }
            });

            int cap = Marshal.ReadInt32(lpdwAddressSize);
            Marshal.WriteInt32(lpdwAddressSize, compound.Length);
            if (lpAddress == IntPtr.Zero || cap < compound.Length)
                return DPlayConstants.DPERR_BUFFERTOOSMALL;

            Marshal.Copy(compound, 0, lpAddress, compound.Length);
            return DPlayConstants.DP_OK;
        }

        private static int DP_GetSessionDesc(IntPtr self, IntPtr lpData, IntPtr lpdwDataSize)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpdwDataSize == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            Guid gInst, gApp;
            uint maxP, curP, flags;
            string sName;
            o.Engine.GetSessionInfo(out gInst, out gApp, out maxP, out curP, out flags, out sName);

            byte[] nameBytes = Encoding.ASCII.GetBytes((sName ?? "Imperialism II") + "\0");
            int needed = 80 + nameBytes.Length;
            int cap = Marshal.ReadInt32(lpdwDataSize);
            Marshal.WriteInt32(lpdwDataSize, needed);

            if (lpData == IntPtr.Zero || cap < needed)
                return DPlayConstants.DPERR_BUFFERTOOSMALL;

            for (int z = 0; z < 80; z += 4) Marshal.WriteInt32(lpData, z, 0);
            IntPtr strDst = new IntPtr(lpData.ToInt64() + 80);
            Marshal.Copy(nameBytes, 0, strDst, nameBytes.Length);

            Marshal.WriteInt32(lpData, 0x00, 80);
            Marshal.WriteInt32(lpData, 0x04, (int)flags);
            WriteGuid(new IntPtr(lpData.ToInt64() + 0x08), gInst);
            WriteGuid(new IntPtr(lpData.ToInt64() + 0x18), gApp);
            Marshal.WriteInt32(lpData, 0x28, (int)maxP);
            Marshal.WriteInt32(lpData, 0x2C, (int)curP);
            Marshal.WriteIntPtr(lpData, 0x30, strDst);
            return DPlayConstants.DP_OK;
        }

        private static int DP_SetSessionDesc(IntPtr self, IntPtr lpSessDesc, uint dwFlags)
        {
            DPlayComObject o = GetDP(self);
            if (o == null || lpSessDesc == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            uint sFlags = (uint)Marshal.ReadInt32(lpSessDesc, 0x04);
            uint maxP = (uint)Marshal.ReadInt32(lpSessDesc, 0x28);
            IntPtr pName = Marshal.ReadIntPtr(lpSessDesc, 0x30);
            string sName = pName != IntPtr.Zero ? Marshal.PtrToStringAnsi(pName) : null;
            return o.Engine.SetSessionInfo(sFlags, maxP, sName);
        }

        // --- IDirectPlayLobby3A methods ---
        private struct CompoundElement
        {
            public Guid DataType;
            public byte[] Data;
        }

        private static byte[] BuildCompoundAddressBuffer(CompoundElement[] elements)
        {
            int bodySize = 24; // DPAID_TotalSize chunk (16 + 4 + 4)
            for (int i = 0; i < elements.Length; i++)
                bodySize += 20 + (elements[i].Data != null ? elements[i].Data.Length : 0);

            byte[] buf = new byte[bodySize];
            Buffer.BlockCopy(DPlayConstants.DPAID_TotalSize.ToByteArray(), 0, buf, 0, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(4), 0, buf, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(bodySize), 0, buf, 20, 4);

            int pos = 24;
            for (int i = 0; i < elements.Length; i++)
            {
                byte[] d = elements[i].Data ?? new byte[0];
                Buffer.BlockCopy(elements[i].DataType.ToByteArray(), 0, buf, pos, 16);
                Buffer.BlockCopy(BitConverter.GetBytes(d.Length), 0, buf, pos + 16, 4);
                if (d.Length > 0)
                    Buffer.BlockCopy(d, 0, buf, pos + 20, d.Length);
                pos += 20 + d.Length;
            }
            return buf;
        }

        private static int Lobby_QueryInterface(IntPtr self, IntPtr riid, IntPtr ppv)
        {
            if (ppv == IntPtr.Zero) return DPlayConstants.E_POINTER;
            Guid iid = ReadGuid(riid);
            if (iid == DPlayConstants.IID_IUnknown ||
                iid == DPlayConstants.IID_IDirectPlayLobby3A ||
                iid == DPlayConstants.IID_IDirectPlayLobby2A ||
                iid == DPlayConstants.IID_IDirectPlayLobbyA)
            {
                Lobby_AddRef(self);
                Marshal.WriteIntPtr(ppv, self);
                return DPlayConstants.DP_OK;
            }
            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return DPlayConstants.E_NOINTERFACE;
        }

        private static uint Lobby_AddRef(IntPtr self)
        {
            lock (s_instLock)
            {
                LobbyComObject o;
                if (s_lobbyObjects.TryGetValue(self, out o))
                    return (uint)(++o.RefCount);
            }
            return 1;
        }

        private static uint Lobby_Release(IntPtr self)
        {
            bool free = false;
            uint rc = 0;
            lock (s_instLock)
            {
                LobbyComObject o;
                if (s_lobbyObjects.TryGetValue(self, out o))
                {
                    rc = (uint)(--o.RefCount);
                    if (o.RefCount <= 0)
                    {
                        s_lobbyObjects.Remove(self);
                        free = true;
                    }
                }
            }
            if (free) Marshal.FreeHGlobal(self);
            return rc;
        }

        private static int Lobby_CreateCompoundAddress(IntPtr self, IntPtr lpElements, uint dwElementCount, IntPtr lpAddress, IntPtr lpdwAddressSize)
        {
            if (lpdwAddressSize == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;
            CompoundElement[] elems = new CompoundElement[dwElementCount];
            for (int i = 0; i < (int)dwElementCount; i++)
            {
                IntPtr ep = new IntPtr(lpElements.ToInt64() + i * 24);
                Guid dt = ReadGuid(ep);
                int sz = Marshal.ReadInt32(ep, 16);
                IntPtr dp = Marshal.ReadIntPtr(ep, 20);
                byte[] data = new byte[Math.Max(0, sz)];
                if (sz > 0 && dp != IntPtr.Zero)
                    Marshal.Copy(dp, data, 0, sz);
                elems[i] = new CompoundElement { DataType = dt, Data = data };
            }

            byte[] built = BuildCompoundAddressBuffer(elems);
            int cap = Marshal.ReadInt32(lpdwAddressSize);
            Marshal.WriteInt32(lpdwAddressSize, built.Length);
            if (lpAddress == IntPtr.Zero || cap < built.Length)
                return DPlayConstants.DPERR_BUFFERTOOSMALL;

            Marshal.Copy(built, 0, lpAddress, built.Length);
            return DPlayConstants.DP_OK;
        }

        private static int Lobby_EnumAddress(IntPtr self, IntPtr lpCallback, IntPtr lpAddress, uint dwAddressSize, IntPtr lpContext)
        {
            if (lpCallback == IntPtr.Zero || lpAddress == IntPtr.Zero)
                return DPlayConstants.DPERR_INVALIDPARAM;

            EnumAddressCallbackCdecl cb = (EnumAddressCallbackCdecl)Marshal.GetDelegateForFunctionPointer(lpCallback, typeof(EnumAddressCallbackCdecl));
            int pos = 0;
            int total = (int)dwAddressSize;
            IntPtr guidMem = Marshal.AllocHGlobal(16);
            try
            {
                while (pos + 20 <= total)
                {
                    IntPtr chunkGuidPtr = new IntPtr(lpAddress.ToInt64() + pos);
                    int sz = Marshal.ReadInt32(lpAddress, pos + 16);
                    if (sz < 0 || pos + 20 + sz > total) break;
                    IntPtr dataPtr = new IntPtr(lpAddress.ToInt64() + pos + 20);

                    byte[] gb = new byte[16];
                    Marshal.Copy(chunkGuidPtr, gb, 0, 16);
                    Marshal.Copy(gb, 0, guidMem, 16);

                    int cont = cb(guidMem, (uint)sz, dataPtr, lpContext);
                    if (cont == 0) break;
                    pos += 20 + sz;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(guidMem);
            }
            return DPlayConstants.DP_OK;
        }

        private static int Lobby_GetConnectionSettings(IntPtr self, uint dwAppID, IntPtr lpData, IntPtr lpdwDataSize)
        {
            return DPlayConstants.DPERR_NOTLOBBIED;
        }

        private static int Lobby_Connect(IntPtr self, uint dwFlags, IntPtr lplpDP, IntPtr pUnk)
        {
            return DPlayConstants.DPERR_NOTLOBBIED;
        }

        private static int Lobby_ConnectEx(IntPtr self, uint dwFlags, IntPtr riid, IntPtr lplpDP, IntPtr pUnk)
        {
            return DPlayConstants.DPERR_NOTLOBBIED;
        }

        // --- IClassFactory ---
        private static int CF_QueryInterface(IntPtr self, IntPtr riid, IntPtr ppv)
        {
            if (ppv == IntPtr.Zero) return DPlayConstants.E_POINTER;
            Guid iid = ReadGuid(riid);
            if (iid == DPlayConstants.IID_IUnknown || iid == DPlayConstants.IID_IClassFactory)
            {
                CF_AddRef(self);
                Marshal.WriteIntPtr(ppv, self);
                return DPlayConstants.DP_OK;
            }
            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return DPlayConstants.E_NOINTERFACE;
        }

        private static uint CF_AddRef(IntPtr self)
        {
            lock (s_instLock)
            {
                FactoryComObject o;
                if (s_cfObjects.TryGetValue(self, out o))
                    return (uint)(++o.RefCount);
            }
            return 1;
        }

        private static uint CF_Release(IntPtr self)
        {
            bool free = false;
            uint rc = 0;
            lock (s_instLock)
            {
                FactoryComObject o;
                if (s_cfObjects.TryGetValue(self, out o))
                {
                    rc = (uint)(--o.RefCount);
                    if (o.RefCount <= 0)
                    {
                        s_cfObjects.Remove(self);
                        free = true;
                    }
                }
            }
            if (free) Marshal.FreeHGlobal(self);
            return rc;
        }

        private static int CF_CreateInstance(IntPtr self, IntPtr pUnkOuter, IntPtr riid, IntPtr ppv)
        {
            if (pUnkOuter != IntPtr.Zero) return DPlayConstants.CLASS_E_NOAGGREGATION;
            FactoryComObject o = null;
            lock (s_instLock) { s_cfObjects.TryGetValue(self, out o); }
            if (o == null) return DPlayConstants.CLASS_E_CLASSNOTAVAILABLE;
            return Exports.CoCreateDirectPlay(ref o.Clsid, IntPtr.Zero, 1, riid, ppv);
        }

        private static int CF_LockServer(IntPtr self, int fLock)
        {
            return DPlayConstants.DP_OK;
        }
    }

    public static class Exports
    {
        public static int DirectPlayCreate(IntPtr lpGUID, IntPtr lplpDP, IntPtr pUnk)
        {
            DPlayConfig.EnsureLoaded();
            if (lplpDP == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;
            IntPtr inst = ComRuntime.CreateDirectPlayInstance();
            Marshal.WriteIntPtr(lplpDP, inst);
            return DPlayConstants.DP_OK;
        }

        public static int DirectPlayEnumerateA(IntPtr lpEnumDPCallback, IntPtr lpContext)
        {
            DPlayConfig.EnsureLoaded();
            if (lpEnumDPCallback == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;

            ComRuntime.DPEnumCallbackA cb = (ComRuntime.DPEnumCallbackA)Marshal.GetDelegateForFunctionPointer(
                lpEnumDPCallback, typeof(ComRuntime.DPEnumCallbackA));

            IntPtr guidMem = Marshal.AllocHGlobal(16);
            IntPtr nameMem = Marshal.StringToHGlobalAnsi("Internet TCP/IP Connection For DirectPlay (Imp2 Modern Wrapper)");
            try
            {
                byte[] gb = DPlayConstants.DPSPGUID_TCPIP.ToByteArray();
                Marshal.Copy(gb, 0, guidMem, 16);
                cb(guidMem, nameMem, 6, 1, lpContext);
            }
            finally
            {
                Marshal.FreeHGlobal(guidMem);
                Marshal.FreeHGlobal(nameMem);
            }
            return DPlayConstants.DP_OK;
        }

        public static int DirectPlayLobbyCreateA(IntPtr lpguidSP, IntPtr lplpDPL, IntPtr lpUnk, IntPtr lpData, uint dwDataSize)
        {
            DPlayConfig.EnsureLoaded();
            if (lplpDPL == IntPtr.Zero) return DPlayConstants.DPERR_INVALIDPARAM;
            IntPtr inst = ComRuntime.CreateLobbyInstance();
            Marshal.WriteIntPtr(lplpDPL, inst);
            return DPlayConstants.DP_OK;
        }

        public static int DllGetClassObject(IntPtr rclsid, IntPtr riid, IntPtr ppv)
        {
            DPlayConfig.EnsureLoaded();
            if (ppv == IntPtr.Zero || rclsid == IntPtr.Zero || riid == IntPtr.Zero)
                return DPlayConstants.E_POINTER;

            Guid clsid = ComRuntime.ReadGuid(rclsid);
            Guid iid = ComRuntime.ReadGuid(riid);
            if (clsid != DPlayConstants.CLSID_DirectPlay && clsid != DPlayConstants.CLSID_DirectPlayLobby)
            {
                Marshal.WriteIntPtr(ppv, IntPtr.Zero);
                return DPlayConstants.CLASS_E_CLASSNOTAVAILABLE;
            }

            if (iid == DPlayConstants.IID_IClassFactory || iid == DPlayConstants.IID_IUnknown)
            {
                Marshal.WriteIntPtr(ppv, ComRuntime.CreateFactoryInstance(clsid));
                return DPlayConstants.DP_OK;
            }

            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return DPlayConstants.E_NOINTERFACE;
        }

        public static int Imp2CoCreateInstance(IntPtr rclsid, IntPtr pUnkOuter, uint dwClsContext, IntPtr riid, IntPtr ppv)
        {
            DPlayConfig.EnsureLoaded();
            if (ppv == IntPtr.Zero || rclsid == IntPtr.Zero || riid == IntPtr.Zero)
                return DPlayConstants.E_POINTER;
            if (pUnkOuter != IntPtr.Zero)
                return DPlayConstants.CLASS_E_NOAGGREGATION;

            Guid clsid = ComRuntime.ReadGuid(rclsid);
            return CoCreateDirectPlay(ref clsid, pUnkOuter, dwClsContext, riid, ppv);
        }

        internal static int CoCreateDirectPlay(ref Guid clsid, IntPtr pUnkOuter, uint dwClsContext, IntPtr riid, IntPtr ppv)
        {
            Guid iid = ComRuntime.ReadGuid(riid);
            DPlayConfig.Log("Imp2CoCreateInstance(clsid={0}, iid={1})", clsid, iid);

            if (clsid == DPlayConstants.CLSID_DirectPlay)
            {
                if (iid == DPlayConstants.IID_IDirectPlay4A ||
                    iid == DPlayConstants.IID_IDirectPlay3A ||
                    iid == DPlayConstants.IID_IDirectPlay2A ||
                    iid == DPlayConstants.IID_IUnknown)
                {
                    IntPtr dp = ComRuntime.CreateDirectPlayInstance();
                    Marshal.WriteIntPtr(ppv, dp);
                    return DPlayConstants.DP_OK;
                }
                Marshal.WriteIntPtr(ppv, IntPtr.Zero);
                return DPlayConstants.E_NOINTERFACE;
            }
            else if (clsid == DPlayConstants.CLSID_DirectPlayLobby)
            {
                if (iid == DPlayConstants.IID_IDirectPlayLobby3A ||
                    iid == DPlayConstants.IID_IDirectPlayLobby2A ||
                    iid == DPlayConstants.IID_IDirectPlayLobbyA ||
                    iid == DPlayConstants.IID_IUnknown)
                {
                    IntPtr lob = ComRuntime.CreateLobbyInstance();
                    Marshal.WriteIntPtr(ppv, lob);
                    return DPlayConstants.DP_OK;
                }
                Marshal.WriteIntPtr(ppv, IntPtr.Zero);
                return DPlayConstants.E_NOINTERFACE;
            }

            Marshal.WriteIntPtr(ppv, IntPtr.Zero);
            return DPlayConstants.CLASS_E_CLASSNOTAVAILABLE;
        }
    }
}
