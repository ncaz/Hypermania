using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using MemoryPack;
using UnityEngine;

namespace Netcode.Rollback.Network
{
    public static class UdpSocketConstants
    {
        public const int RECV_BUFFER_SIZE = 4096;
        public const int IDEAL_MAX_UDP_PACKET_SIZE = 508;
    }

    public class UdpSocket
    {
        private readonly Socket _socket;
        private readonly byte[] _buffer;

        public UdpSocket(Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _buffer = new byte[UdpSocketConstants.RECV_BUFFER_SIZE];
        }

        public static UdpSocket BindToPort(int port)
        {
            if ((uint)port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.Blocking = false;
            return new UdpSocket(socket);
        }

        public void SendTo(in Message message, in IPEndPoint addr)
        {
            byte[] payload = MemoryPackSerializer.Serialize(message);
            if (payload.Length > UdpSocketConstants.IDEAL_MAX_UDP_PACKET_SIZE)
            {
                Debug.Log($"Sending UDP packet of size {payload.Length} bytes, which is larger than ideal ({UdpSocketConstants.IDEAL_MAX_UDP_PACKET_SIZE}).");
            }

            _socket.SendTo(payload, SocketFlags.None, addr);
        }

        public IEnumerable<(IPEndPoint addr, Message message)> ReceiveAllMessages()
        {
            var received = new List<(IPEndPoint, Message)>();

            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

                try
                {
                    int bytes = _socket.ReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref remote);
                    if (bytes <= 0) continue;
                    if (bytes > UdpSocketConstants.RECV_BUFFER_SIZE)
                        throw new InvalidOperationException("Received more bytes than buffer size.");

                    Message? message = MemoryPackSerializer.Deserialize<Message>(_buffer.AsSpan().Slice(0, bytes));
                    if (message != null) { received.Add(((IPEndPoint)remote, message.Value)); }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { return received; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch (SocketException ex)
                {
                    throw new InvalidOperationException(
                        $"{ex.SocketErrorCode}: {ex.Message} on {_socket.LocalEndPoint}", ex);
                }
            }
        }

        public void Dispose()
        {
            try { _socket?.Dispose(); }
            catch { /* ignore */ }
        }
    }
}