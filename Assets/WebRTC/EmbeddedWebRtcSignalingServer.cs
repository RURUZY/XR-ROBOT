using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Tiny signaling relay hosted inside the Windows Unity sender.
/// It only exchanges SDP/ICE JSON; WebRTC video remains peer-to-peer.
/// </summary>
public class EmbeddedWebRtcSignalingServer : MonoBehaviour
{
    public int port = 8080;
    public bool startOnAwake;

    public bool IsRunning => listener != null;

    private TcpListener listener;
    private Thread listenerThread;
    private volatile bool running;
    private readonly object messageLock = new object();
    private readonly Dictionary<string, List<StoredMessage>> messages =
        new Dictionary<string, List<StoredMessage>>();
    private long nextId = 1;

    [Serializable]
    private class IncomingMessage
    {
        public string from;
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    private class StoredMessage : IncomingMessage
    {
        public long id;
    }

    [Serializable]
    private class MessageList
    {
        public StoredMessage[] messages;
    }

    private void Start()
    {
        if (startOnAwake) StartServer();
    }

    public void StartServer()
    {
        if (running) return;

        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "Unity WebRTC Signaling"
            };
            listenerThread.Start();
            Debug.Log($"Embedded WebRTC signaling listening on port {port}");
        }
        catch (Exception exception)
        {
            running = false;
            listener = null;
            Debug.LogError($"Could not start embedded signaling server: {exception.Message}");
        }
    }

    private void ListenLoop()
    {
        while (running)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException)
            {
                if (running) Debug.LogWarning("Signaling listener socket stopped unexpectedly.");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
        {
            try
            {
                string requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine))
                {
                    WriteResponse(stream, 400, "{\"error\":\"empty request\"}");
                    return;
                }

                string[] requestParts = requestLine.Split(' ');
                if (requestParts.Length < 2)
                {
                    WriteResponse(stream, 400, "{\"error\":\"invalid request\"}");
                    return;
                }

                string method = requestParts[0];
                string requestTarget = requestParts[1];
                int contentLength = 0;
                string header;
                while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                {
                    int separator = header.IndexOf(':');
                    if (separator <= 0) continue;
                    string name = header.Substring(0, separator).Trim();
                    string value = header.Substring(separator + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(value, out contentLength);
                }

                string body = string.Empty;
                if (contentLength > 0)
                {
                    char[] buffer = new char[contentLength];
                    int total = 0;
                    while (total < contentLength)
                    {
                        int count = reader.Read(buffer, total, contentLength - total);
                        if (count <= 0) break;
                        total += count;
                    }
                    body = new string(buffer, 0, total);
                }

                Uri uri = new Uri("http://localhost" + requestTarget);
                Dictionary<string, string> query = ParseQuery(uri.Query);

                if (method == "POST" && uri.AbsolutePath == "/signal")
                    HandleSignal(stream, query, body);
                else if (method == "GET" && uri.AbsolutePath == "/poll")
                    HandlePoll(stream, query);
                else if (method == "GET" && uri.AbsolutePath == "/health")
                    WriteResponse(stream, 200, "{\"ok\":true}");
                else
                    WriteResponse(stream, 404, "{\"error\":\"not found\"}");
            }
            catch (Exception exception)
            {
                WriteResponse(stream, 500,
                    "{\"error\":\"" + EscapeJson(exception.Message) + "\"}");
            }
        }
    }

    private void HandleSignal(
        NetworkStream stream, Dictionary<string, string> query, string body)
    {
        query.TryGetValue("room", out string room);
        IncomingMessage incoming = JsonUtility.FromJson<IncomingMessage>(body);
        if (string.IsNullOrEmpty(room) || incoming == null ||
            (incoming.from != "sender" && incoming.from != "receiver"))
        {
            WriteResponse(stream, 400, "{\"error\":\"invalid room or peer\"}");
            return;
        }

        string recipient = incoming.from == "sender" ? "receiver" : "sender";
        StoredMessage stored = new StoredMessage
        {
            from = incoming.from,
            type = incoming.type,
            sdp = incoming.sdp,
            candidate = incoming.candidate,
            sdpMid = incoming.sdpMid,
            sdpMLineIndex = incoming.sdpMLineIndex
        };

        lock (messageLock)
        {
            stored.id = nextId++;
            string key = MakeKey(room, recipient);
            if (!messages.TryGetValue(key, out List<StoredMessage> list))
            {
                list = new List<StoredMessage>();
                messages.Add(key, list);
            }
            list.Add(stored);
        }

        WriteResponse(stream, 200, $"{{\"ok\":true,\"id\":{stored.id}}}");
    }

    private void HandlePoll(NetworkStream stream, Dictionary<string, string> query)
    {
        query.TryGetValue("room", out string room);
        query.TryGetValue("peer", out string peer);
        long after = 0;
        if (query.TryGetValue("after", out string afterText))
            long.TryParse(afterText, out after);

        List<StoredMessage> result = new List<StoredMessage>();
        lock (messageLock)
        {
            if (messages.TryGetValue(MakeKey(room, peer), out List<StoredMessage> list))
            {
                foreach (StoredMessage item in list)
                    if (item.id > after) result.Add(item);
            }
        }

        string json = JsonUtility.ToJson(new MessageList { messages = result.ToArray() });
        WriteResponse(stream, 200, json);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new Dictionary<string, string>();
        string trimmed = query.TrimStart('?');
        if (string.IsNullOrEmpty(trimmed)) return result;
        foreach (string pair in trimmed.Split('&'))
        {
            string[] parts = pair.Split(new[] { '=' }, 2);
            string key = Uri.UnescapeDataString(parts[0]);
            string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static string MakeKey(string room, string peer) => room + "\n" + peer;

    private static void WriteResponse(NetworkStream stream, int status, string json)
    {
        if (stream == null || !stream.CanWrite) return;
        byte[] body = Encoding.UTF8.GetBytes(json);
        string statusText = status == 200 ? "OK" :
            status == 400 ? "Bad Request" :
            status == 404 ? "Not Found" : "Internal Server Error";
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {statusText}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n");
        stream.Write(headers, 0, headers.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public void StopServer()
    {
        running = false;
        try { listener?.Stop(); }
        catch (SocketException) { }
        listener = null;
        listenerThread = null;
    }

    private void OnDestroy()
    {
        StopServer();
    }
}
