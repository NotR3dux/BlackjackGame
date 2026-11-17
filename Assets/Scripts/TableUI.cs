using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class MultiplayerHost : MonoBehaviour
{
    public const int PORT = 7777;
    const string CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public bool isConnected;
    public bool isHost;
    public string joinCode = "";

    public Func<int> OnAssignSeat;
    public Action<int, string> OnClientCommand;
    public Action<int> OnClientLeft;
    public Action<int> OnJoinedAsClient;
    public Action<string> OnStateReceived;

    TcpListener server;
    readonly Dictionary<int, StreamWriter> clientWriters = new Dictionary<int, StreamWriter>();
    StreamWriter hostWriter;
    TcpClient clientSocket;
    readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();

    void Update()
    {
        while (mainThread.TryDequeue(out Action action)) action?.Invoke();
    }

    void OnDestroy()
    {
        StopAll();
    }

    public void StopAll()
    {
        isConnected = false;
        isHost = false;
        try { server?.Stop(); } catch { }
        server = null;
        try { clientSocket?.Close(); } catch { }
        clientSocket = null;
        hostWriter = null;
        lock (clientWriters) clientWriters.Clear();
    }

    public string StartHost()
    {
        StopAll();
        try
        {
            server = new TcpListener(IPAddress.Any, PORT);
            server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            server.Start();

            isHost = true;
            isConnected = true;
            joinCode = IpToCode(GetLocalIp());

            new Thread(AcceptLoop) { IsBackground = true }.Start();
            return joinCode;
        }
        catch (Exception e)
        {
            Debug.LogWarning("Host Error: " + e.Message);
            return "ERROR";
        }
    }

    void AcceptLoop()
    {
        while (isConnected && isHost)
        {
            try
            {
                TcpClient client = server.AcceptTcpClient();
                new Thread(() => HandleClient(client)) { IsBackground = true }.Start();
            }
            catch { break; }
        }
    }

    void HandleClient(TcpClient client)
    {
        int seat = -1;
        try
        {
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

                using (ManualResetEventSlim wait = new ManualResetEventSlim(false))
                {
                    mainThread.Enqueue(() => { seat = OnAssignSeat != null ? OnAssignSeat() : -1; wait.Set(); });
                    wait.Wait(2000);
                }

                if (seat < 1 || seat > 3) { client.Close(); return; }

                lock (clientWriters) clientWriters[seat] = writer;
                writer.WriteLine("SEAT|" + seat);
                mainThread.Enqueue(() => OnClientCommand?.Invoke(seat, "JOIN"));

                while (client.Connected)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    int s = seat;
                    mainThread.Enqueue(() => OnClientCommand?.Invoke(s, line));
                }
            }
        }
        catch { }
        finally
        {
            if (seat >= 1)
            {
                lock (clientWriters) clientWriters.Remove(seat);
                mainThread.Enqueue(() => OnClientLeft?.Invoke(seat));
            }
            try { client.Close(); } catch { }
        }
    }

    public void Join(string code)
    {
        string ip = CodeToIp(code);
        if (string.IsNullOrEmpty(ip)) return;

        StopAll();
        joinCode = code.Trim().ToUpperInvariant();

        new Thread(() =>
        {
            try
            {
                clientSocket = new TcpClient();
                clientSocket.Connect(ip, PORT);
                using (NetworkStream stream = clientSocket.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    hostWriter = new StreamWriter(stream) { AutoFlush = true };
                    isHost = false;
                    isConnected = true;

                    while (clientSocket.Connected)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;

                        if (line.StartsWith("SEAT|"))
                        {
                            int mySeat = int.Parse(line.Substring(5));
                            mainThread.Enqueue(() => OnJoinedAsClient?.Invoke(mySeat));
                        }
                        else if (line.StartsWith("STATE|"))
                        {
                            string json = line.Substring(6);
                            mainThread.Enqueue(() => OnStateReceived?.Invoke(json));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Join Error: " + e.Message);
                isConnected = false;
            }
        })
        { IsBackground = true }.Start();
    }

    public void SendToClients(string json)
    {
        if (!isHost || !isConnected) return;
        lock (clientWriters)
        {
            foreach (var kv in new List<KeyValuePair<int, StreamWriter>>(clientWriters))
            {
                try { kv.Value.WriteLine("STATE|" + json); }
                catch { clientWriters.Remove(kv.Key); }
            }
        }
    }

    public void SendToHost(string cmd)
    {
        if (isHost || !isConnected || hostWriter == null) return;
        try { hostWriter.WriteLine(cmd); } catch { }
    }

    public static string GetLocalIp()
    {
        try
        {
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                s.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
        catch { return "127.0.0.1"; }
    }

    public static string IpToCode(string ipStr)
    {
        if (!IPAddress.TryParse(ipStr, out IPAddress ip)) ip = IPAddress.Loopback;
        byte[] b = ip.GetAddressBytes();
        uint val = (((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]) ^ 0x5A3C91u;
        char prefix = b[0] == 192 ? 'C' : b[0] == 10 ? 'A' : b[0] == 172 ? 'B' : 'L';

        char[] res = new char[6];
        res[0] = prefix;
        for (int i = 5; i >= 1; i--) { res[i] = CHARS[(int)(val % 36)]; val /= 36; }
        return new string(res);
    }

    public static string CodeToIp(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().ToUpperInvariant();
        if (code.Contains(".")) return code;
        if (code.Length != 6) return null;

        ulong val = 0;
        for (int i = 1; i < 6; i++)
        {
            int idx = CHARS.IndexOf(code[i]);
            if (idx < 0) return null;
            val = val * 36 + (ulong)idx;
        }

        uint raw = ((uint)val) ^ 0x5A3C91u;
        byte b0 = code[0] == 'C' ? (byte)192 : code[0] == 'A' ? (byte)10 : code[0] == 'B' ? (byte)172 : (byte)127;
        return $"{b0}.{(raw >> 16) & 0xFF}.{(raw >> 8) & 0xFF}.{raw & 0xFF}";
    }
}
