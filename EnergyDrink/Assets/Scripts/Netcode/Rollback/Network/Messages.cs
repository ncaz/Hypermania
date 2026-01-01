using System;
using MemoryPack;

namespace Netcode.Rollback.Network
{
    [MemoryPackable]
    public partial struct ConnectionStatus
    {
        public bool Disconnected;
        public Frame LastFrame;

        public static readonly ConnectionStatus Default = new()
        {
            Disconnected = false,
            LastFrame = Frame.NullFrame
        };
    }

    public enum MessageKind : byte
    {
        SyncRequest,
        SyncReply,
        Input,
        InputAck,
        QualityReport,
        QualityReply,
        ChecksumReport,
        KeepAlive,
    }

    [MemoryPackable]
    public partial struct MessageHeader
    {
        public ushort Magic;
    }

    [MemoryPackable]
    public partial struct MessageBody
    {
        [MemoryPackable]
        public partial struct SyncRequest
        {
            public uint RandomRequest;
        }

        [MemoryPackable]
        public partial struct SyncReply
        {
            public uint RandomReply;
        }

        [MemoryPackable]
        public partial struct Input
        {
            public ConnectionStatus[] PeerConnectStatus;
            public bool DisconnectRequested;
            public Frame StartFrame;
            public Frame AckFrame;
            public byte[] Bytes;

            public static readonly Input Default = new()
            {
                PeerConnectStatus = Array.Empty<ConnectionStatus>(),
                DisconnectRequested = false,
                StartFrame = Frame.NullFrame,
                AckFrame = Frame.NullFrame,
                Bytes = Array.Empty<byte>(),
            };
        }

        [MemoryPackable]
        public partial struct InputAck
        {
            public Frame AckFrame;

            public static readonly InputAck Default = new()
            {
                AckFrame = Frame.NullFrame
            };
        }

        [MemoryPackable]
        public partial struct QualityReport
        {
            public short FrameAdvantage;
            public ulong Ping;
        }

        [MemoryPackable]
        public partial struct QualityReply
        {
            public ulong Pong;
        }

        [MemoryPackable]
        public partial struct ChecksumReport
        {
            public ulong Checksum;
            public Frame Frame;
        }

        [MemoryPackable]
        public partial struct KeepAlive { }

        public MessageKind Kind;

        [MemoryPackInclude]
        private SyncRequest _syncRequest;
        [MemoryPackInclude]
        private SyncReply _syncReply;
        [MemoryPackInclude]
        private Input _input;
        [MemoryPackInclude]
        private InputAck _inputAck;
        [MemoryPackInclude]
        private QualityReport _qualityReport;
        [MemoryPackInclude]
        private QualityReply _qualityReply;
        [MemoryPackInclude]
        private ChecksumReport _checksumReport;

        public static MessageBody From(in SyncRequest body) =>
            new() { Kind = MessageKind.SyncRequest, _syncRequest = body };

        public static MessageBody From(in SyncReply body) =>
            new() { Kind = MessageKind.SyncReply, _syncReply = body };

        public static MessageBody From(in Input body) =>
            new() { Kind = MessageKind.Input, _input = body };

        public static MessageBody From(in InputAck body) =>
            new() { Kind = MessageKind.InputAck, _inputAck = body };

        public static MessageBody From(in QualityReport body) =>
            new() { Kind = MessageKind.QualityReport, _qualityReport = body };

        public static MessageBody From(in QualityReply body) =>
            new() { Kind = MessageKind.QualityReply, _qualityReply = body };

        public static MessageBody From(in ChecksumReport body) =>
            new() { Kind = MessageKind.ChecksumReport, _checksumReport = body };

        public static MessageBody From(in KeepAlive _) =>
            new() { Kind = MessageKind.KeepAlive };

        public SyncRequest GetSyncRequest() =>
            Kind == MessageKind.SyncRequest ? _syncRequest : throw new InvalidOperationException("body type mismatch");

        public SyncReply GetSyncReply() =>
            Kind == MessageKind.SyncReply ? _syncReply : throw new InvalidOperationException("body type mismatch");

        public Input GetInput() =>
            Kind == MessageKind.Input ? _input : throw new InvalidOperationException("body type mismatch");

        public InputAck GetInputAck() =>
            Kind == MessageKind.InputAck ? _inputAck : throw new InvalidOperationException("body type mismatch");

        public QualityReport GetQualityReport() =>
            Kind == MessageKind.QualityReport ? _qualityReport : throw new InvalidOperationException("body type mismatch");

        public QualityReply GetQualityReply() =>
            Kind == MessageKind.QualityReply ? _qualityReply : throw new InvalidOperationException("body type mismatch");

        public ChecksumReport GetChecksumReport() =>
            Kind == MessageKind.ChecksumReport ? _checksumReport : throw new InvalidOperationException("body type mismatch");
    }

    [MemoryPackable]
    public partial struct Message
    {
        public MessageHeader Header;
        public MessageBody Body;
    }
}
