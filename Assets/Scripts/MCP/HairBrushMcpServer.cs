using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

// MCP COMMAND CHANNEL - the loopback side of tools/hairbrush-mcp/server.mjs.
//
// The Node bridge speaks MCP to Claude over stdio and forwards each tool call here as one line of
// JSON. This component owns the socket and nothing else: it authenticates the bridge, queues the
// commands, and runs them ONE AT A TIME on the main thread through HairBrushMcpCommands, which is
// where the actual grooming happens. Every Unity call has to be on the main thread, and running
// them strictly in order means a tool call can never observe another one half-applied.
//
// Wire format, one JSON object per LF-terminated line:
//   bridge -> app   {"hello":"<token>"}                        once, first line, within 5s
//   app -> bridge   {"ready":true,"app":"HairBrush","version":"0.3.3"}
//   bridge -> app   {"id":7,"method":"place_cards","args":{...}}
//   app -> bridge   {"id":7,"ok":true,"result":{...}}  or  {"id":7,"ok":false,"error":"..."}
//
// SECURITY. Bound to 127.0.0.1 only, never to a LAN address, and nothing is accepted until the
// first line carries the shared token from %USERPROFILE%\.hairbrush-mcp\token - a 64-hex secret
// whichever side starts first generates. A local web page cannot open a raw TCP socket, and any
// other local process would need to read the user's own profile to get in.
//
// ENABLED in the Editor always, and in a built player only when launched with -mcp (or with
// HAIRBRUSH_MCP=1 in the environment), so a shipped copy never opens a port nobody asked for.
[DefaultExecutionOrder(10600)]
public class HairBrushMcpServer : MonoBehaviour
{
    public const int DefaultPort = 5170;

    // Inbound only. Replies can be much bigger - a screenshot is a few hundred KB of base64 -
    // and are bounded by the bridge instead.
    const int MaxInboundLine = 1 << 20;
    const int MaxClients = 4;

    class Client
    {
        public TcpClient tcp;
        public NetworkStream stream;
        public readonly object writeLock = new object();
        public volatile bool authed;
        public volatile bool closed;
    }

    struct Pending
    {
        public Client client;
        public long id;
        public string method;
        public Dictionary<string, object> args;
    }

    static HairBrushMcpServer instance;

    TcpListener listener;
    Thread acceptThread;
    volatile bool stopping;
    string token;
    int port;
    readonly List<Client> clients = new List<Client>();
    readonly ConcurrentQueue<Pending> queue = new ConcurrentQueue<Pending>();
    HairBrushMcpCommands commands;
    bool busy;

    // Read once on the main thread. The handshake reply is built on a socket thread, where any
    // Unity API call - even Application.version - throws and silently drops the connection.
    string appVersion;
    bool isEditor;
    float lastAutosave;

    public static bool IsListening => instance != null && instance.listener != null;
    public static int ConnectedClients
    {
        get
        {
            if (instance == null) return 0;
            lock (instance.clients) return instance.clients.Count;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (!ShouldEnable()) return;
        if (FindFirstObjectByType<HairBrushMcpServer>() != null) return;
        GameObject go = new GameObject("HairBrushMcpServer");
        DontDestroyOnLoad(go);
        go.AddComponent<HairBrushMcpServer>();
    }

    static bool ShouldEnable()
    {
        if (Application.isEditor) return true;
        if (Environment.GetEnvironmentVariable("HAIRBRUSH_MCP") == "1") return true;
        foreach (string arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, "-mcp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string TokenPath()
    {
        string custom = Environment.GetEnvironmentVariable("HAIRBRUSH_MCP_TOKEN_FILE");
        if (!string.IsNullOrEmpty(custom)) return custom;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".hairbrush-mcp", "token");
    }

    // Same rules as the bridge's loadConfig: read it if it is there, otherwise create it. Whichever
    // side runs first wins and the other simply reads what it wrote.
    static string LoadOrCreateToken()
    {
        string path = TokenPath();
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (IsValidToken(existing)) return existing;
            throw new InvalidDataException("HairBrush MCP: the token file is not 64 hex characters - " + path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] bytes = new byte[32];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        StringBuilder sb = new StringBuilder(64);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        File.WriteAllText(path, sb + "\n");
        return sb.ToString();
    }

    static bool IsValidToken(string t)
    {
        if (t == null || t.Length != 64) return false;
        foreach (char c in t) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    // Constant time, so the comparison cannot be timed one character at a time.
    static bool TokenMatches(string offered, string expected)
    {
        if (offered == null || offered.Length != expected.Length) return false;
        int diff = 0;
        for (int i = 0; i < expected.Length; i++) diff |= offered[i] ^ expected[i];
        return diff == 0;
    }

    void Awake()
    {
        instance = this;
        appVersion = Application.version;
        isEditor = Application.isEditor;
        commands = gameObject.AddComponent<HairBrushMcpCommands>();
    }

    void Start()
    {
        port = DefaultPort;
        string portEnv = Environment.GetEnvironmentVariable("HAIRBRUSH_MCP_PORT");
        if (!string.IsNullOrEmpty(portEnv) && int.TryParse(portEnv, out int parsed) && parsed >= 1024 && parsed <= 65535) port = parsed;

        try
        {
            token = LoadOrCreateToken();
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (Exception ex)
        {
            listener = null;
            Debug.LogWarning("HairBrush MCP: not listening - " + ex.Message);
            return;
        }

        // ProjectSettings has runInBackground off, which in a player stops Update the moment the
        // window loses focus - and while Claude is driving it, the focus is in Claude. Without
        // this every command would sit in the queue until the user clicked back into HairBrush.
        Application.runInBackground = true;

        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HairBrushMcpAccept" };
        acceptThread.Start();
        Debug.Log("HairBrush MCP: listening on 127.0.0.1:" + port);
    }

    void OnDestroy()
    {
        stopping = true;
        try { listener?.Stop(); } catch { }
        lock (clients)
        {
            foreach (Client c in clients) Close(c);
            clients.Clear();
        }
        if (instance == this) instance = null;
    }

    // Leaving Play mode in the editor arrives here too, while every groom object still exists -
    // the last moment the session can be written before a recompile throws it away.
    void OnApplicationQuit()
    {
        if (listener != null) HairBrushMcpCommands.WriteAutosave();
        OnDestroy();
    }

    // ---------------------------------------------------------------------------------
    // Socket threads
    // ---------------------------------------------------------------------------------

    void AcceptLoop()
    {
        while (!stopping)
        {
            TcpClient tcp;
            try { tcp = listener.AcceptTcpClient(); }
            catch { if (stopping) return; continue; }

            Client client = new Client { tcp = tcp, stream = tcp.GetStream() };
            tcp.NoDelay = true;
            lock (clients)
            {
                if (clients.Count >= MaxClients) { Close(client); continue; }
                clients.Add(client);
            }
            Thread reader = new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "HairBrushMcpClient" };
            reader.Start();
        }
    }

    void ReadLoop(Client client)
    {
        // An unauthenticated connection gets five seconds to say hello and is then dropped, so
        // nothing can hold one of the client slots open by connecting and saying nothing.
        Timer authTimer = new Timer(_ => { if (!client.authed) Close(client); }, null, 5000, Timeout.Infinite);
        try
        {
            List<byte> line = new List<byte>(1024);
            byte[] buffer = new byte[8192];
            while (!client.closed)
            {
                int n = client.stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') { line.Add(buffer[i]); if (line.Count > MaxInboundLine) throw new IOException("Line too long"); continue; }
                    string text = Encoding.UTF8.GetString(line.ToArray());
                    line.Clear();
                    if (text.Trim().Length == 0) continue;
                    if (!HandleLine(client, text)) { Close(client); return; }
                }
            }
        }
        catch { }
        finally
        {
            authTimer.Dispose();
            Close(client);
            lock (clients) clients.Remove(client);
        }
    }

    // Runs on the reader thread. Returns false to drop the connection.
    bool HandleLine(Client client, string text)
    {
        Dictionary<string, object> message;
        try { message = HairBrushMcpJson.Parse(text) as Dictionary<string, object>; }
        catch { return false; }
        if (message == null) return false;

        if (!client.authed)
        {
            if (!message.TryGetValue("hello", out object offered) || !TokenMatches(offered as string, token)) return false;
            client.authed = true;
            Send(client, new Dictionary<string, object>
            {
                { "ready", true },
                { "app", "HairBrush" },
                { "version", appVersion },
                { "editor", isEditor }
            });
            return true;
        }

        if (!message.TryGetValue("id", out object idObj) || !(idObj is double idNum)) return false;
        if (!message.TryGetValue("method", out object methodObj) || !(methodObj is string method)) return false;
        message.TryGetValue("args", out object argsObj);
        queue.Enqueue(new Pending
        {
            client = client,
            id = (long)idNum,
            method = method,
            args = argsObj as Dictionary<string, object> ?? new Dictionary<string, object>()
        });
        return true;
    }

    static void Send(Client client, Dictionary<string, object> message)
    {
        if (client.closed) return;
        byte[] data = Encoding.UTF8.GetBytes(HairBrushMcpJson.Write(message) + "\n");
        lock (client.writeLock)
        {
            try { client.stream.Write(data, 0, data.Length); }
            catch { Close(client); }
        }
    }

    static void Close(Client client)
    {
        if (client.closed) return;
        client.closed = true;
        try { client.tcp.Close(); } catch { }
    }

    // ---------------------------------------------------------------------------------
    // Main thread
    // ---------------------------------------------------------------------------------

    void Update()
    {
        if (busy) return;
        if (!queue.TryDequeue(out Pending next)) return;
        busy = true;
        StartCoroutine(Execute(next));
    }

    // Each command is a coroutine because a few of them genuinely span frames - a project load
    // settles over several, and a screenshot has to wait for the end of the frame. busy holds the
    // queue until it finishes, so commands still run strictly one after another.
    IEnumerator Execute(Pending p)
    {
        bool finished = false;
        object result = null;
        string error = null;

        // Stepped by hand rather than through StartCoroutine so an exception thrown inside a
        // command is caught here and reported, instead of silently killing the coroutine and
        // leaving the bridge waiting for a reply that will never come. A command that yields
        // another IEnumerator (a sub-step) is pushed and stepped the same way, for the same reason.
        Stack<IEnumerator> stack = new Stack<IEnumerator>();
        try { stack.Push(commands.Run(p.method, p.args, r => { result = r; finished = true; }, e => { error = e; finished = true; })); }
        catch (Exception ex) { error = Describe(ex); finished = true; }

        while (!finished && error == null && stack.Count > 0)
        {
            IEnumerator top = stack.Peek();
            bool more;
            object yielded = null;
            try
            {
                more = top.MoveNext();
                if (more) yielded = top.Current;
            }
            catch (Exception ex) { error = Describe(ex); break; }
            if (!more) { stack.Pop(); continue; }
            if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
            yield return yielded;
        }

        if (error == null && !finished) error = "Command '" + p.method + "' finished without a result.";

        Dictionary<string, object> reply = new Dictionary<string, object> { { "id", p.id } };
        if (error != null) { reply["ok"] = false; reply["error"] = error; }
        else { reply["ok"] = true; reply["result"] = result ?? new Dictionary<string, object>(); }
        Send(p.client, reply);

        // Undo commits a step only after 0.3s of quiet; wait that out (with margin) so this
        // command gets a step of its own. The reply has already gone, so the caller is not kept
        // waiting - only the next command is.
        if (HairBrushMcpCommands.EditPending)
        {
            HairBrushMcpCommands.EditPending = false;
            yield return new WaitForSecondsRealtime(.45f);
            yield return null;

            // Rolling autosave, at most every 45s of MCP editing, on top of the one at quit.
            if (Time.realtimeSinceStartup - lastAutosave > 45f)
            {
                lastAutosave = Time.realtimeSinceStartup;
                HairBrushMcpCommands.WriteAutosave();
            }
        }
        busy = false;
    }

    static string Describe(Exception ex)
    {
        if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
        if (ex is HairBrushMcpCommands.CommandException) return ex.Message;
        Debug.LogException(ex);
        return ex.GetType().Name + ": " + ex.Message;
    }
}
