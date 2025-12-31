using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class Client : MonoBehaviour
{
    [Header("Servers")]
    public string serverIp = "144.126.152.174";
    public int httpPort = 9000;
    public int punchPort = 9001;
    public int relayPort = 9002;

    [Header("ClientId (u128 = hi | lo)")]
    public ulong clientIdHigh = 0;
    public ulong clientIdLow  = 1;

    [Header("Join Settings")]
    public ulong roomIdToJoin;

    ulong currentRoomId;
    UdpClient udp;
    IPEndPoint relayEp;
    Thread recvThread;
    volatile bool running;
    Synapse synapse;

    void Start()
    {
        synapse = new Synapse(serverIp, httpPort);
        relayEp = new IPEndPoint(IPAddress.Parse(serverIp), relayPort);
        udp = new UdpClient(0);
        udp.Client.ReceiveTimeout = 1000;

        running = true;
        recvThread = new Thread(RecvLoop);
        recvThread.Start();

        Debug.Log($"[Client] Started. client_id={ClientIdString()}");
    }

    void OnDestroy()
    {
        running = false;
        recvThread?.Join();
        udp?.Close();
        Debug.Log("[Client] Shutdown");
    }

    public void CreateRoom()
    {
        StartCoroutine(synapse.CreateRoomCo(ClientIdString(), resp =>
        {
            currentRoomId = resp.room_id;
        }));
    }

    public void JoinRoom()
    {
        StartCoroutine(synapse.JoinRoomCo(ClientIdString(), roomIdToJoin, resp =>
        {
            currentRoomId = roomIdToJoin;
        }));
    }

    public void LeaveRoom()
    {
        StartCoroutine(synapse.LeaveRoomCo(ClientIdString(), resp =>
        {
            currentRoomId = 0;
        }));
    }

    public void Bind()
    {
        SendBind();
        Debug.Log("[UDP] Sent bind");
    }

    public void SendTest()
    {
        SendRelay(System.Text.Encoding.UTF8.GetBytes("hello from unity"));
        Debug.Log("[UDP] Sent test relay message");
    }

    private void SendBind()
    {
        byte[] buf = new byte[17];
        buf[0] = 0x1;
        WriteU128(buf, 1, clientIdHigh, clientIdLow);
        udp.Send(buf, buf.Length, relayEp);
    }

    private void SendRelay(byte[] payload)
    {
        byte[] buf = new byte[1 + payload.Length];
        buf[0] = 0x2;
        Buffer.BlockCopy(payload, 0, buf, 1, payload.Length);
        udp.Send(buf, buf.Length, relayEp);
    }

    private void RecvLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                var data = udp.Receive(ref any);
                if (data.Length == 0) continue;

                switch (data[0])
                {
                    case 0x1:
                        Debug.Log("[UDP] Bind ACK");
                        break;

                    case 0x2:
                        var msg = System.Text.Encoding.UTF8.GetString(data, 1, data.Length - 1);
                        Debug.Log($"[UDP] Relay recv: {msg}");
                        break;
                }
            }
            catch (SocketException) { }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
    }

    private string ClientIdString()
    {
        var hi = new System.Numerics.BigInteger(clientIdHigh);
        var lo = new System.Numerics.BigInteger(clientIdLow);
        return ((hi << 64) | lo).ToString();
    }

    private static void WriteU128(byte[] buf, int off, ulong hi, ulong lo)
    {
        WriteU64(buf, off, hi);
        WriteU64(buf, off + 8, lo);
    }

    private static void WriteU64(byte[] buf, int off, ulong v)
    {
        for (int i = 0; i < 8; i++)
            buf[off + i] = (byte)(v >> (56 - 8 * i));
    }
}
