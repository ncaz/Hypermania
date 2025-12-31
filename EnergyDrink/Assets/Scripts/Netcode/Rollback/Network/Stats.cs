using System;

namespace Netcode.Rollback.Network
{
    public struct NetworkStats
    {
        public int SendQueueLen;
        public ulong Ping;
        public int LocalFramesBehind;
        public int RemoteFramesBehind;
    }
}