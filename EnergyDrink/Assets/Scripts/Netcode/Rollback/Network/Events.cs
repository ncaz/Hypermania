namespace Netcode.Rollback.Network
{
    public enum EventKind
    {
        Synchronizing,
        Synchronized,
        Input,
        Disconnected,
        NetworkInterrupted,
        NetworkResumed,
    }

    public struct Event<TInput> where TInput : IInput<TInput>
    {
        public struct Synchronizing
        {
            public uint Total;
            public uint Count;
        }

        public struct Synchronized { }

        public struct Input
        {
            public PlayerInput<TInput> Data;
            public PlayerHandle Player;
        }

        public struct Disconnected { }

        public struct NetworkInterrupted
        {
            public ulong DisconnectTimeout;
        }

        public struct NetworkResumed { }

        public EventKind Kind;

        private Synchronizing _synchronizing;
        private Synchronized _synchronized;
        private Input _input;
        private Disconnected _disconnected;
        private NetworkInterrupted _networkInterrupted;
        private NetworkResumed _networkResumed;

        public static Event<TInput> From(in Synchronizing e) =>
            new() { Kind = EventKind.Synchronizing, _synchronizing = e };

        public static Event<TInput> From(in Synchronized e) =>
            new() { Kind = EventKind.Synchronized, _synchronized = e };

        public static Event<TInput> From(in Input e) =>
            new() { Kind = EventKind.Input, _input = e };

        public static Event<TInput> From(in Disconnected e) =>
            new() { Kind = EventKind.Disconnected, _disconnected = e };

        public static Event<TInput> From(in NetworkInterrupted e) =>
            new() { Kind = EventKind.NetworkInterrupted, _networkInterrupted = e };

        public static Event<TInput> From(in NetworkResumed e) =>
            new() { Kind = EventKind.NetworkResumed, _networkResumed = e };

        public Synchronizing GetSynchronizing() =>
            Kind == EventKind.Synchronizing ? _synchronizing : throw new System.InvalidOperationException("event type mismatch");

        public Synchronized GetSynchronized() =>
            Kind == EventKind.Synchronized ? _synchronized : throw new System.InvalidOperationException("event type mismatch");

        public Input GetInput() =>
            Kind == EventKind.Input ? _input : throw new System.InvalidOperationException("event type mismatch");

        public Disconnected GetDisconnected() =>
            Kind == EventKind.Disconnected ? _disconnected : throw new System.InvalidOperationException("event type mismatch");

        public NetworkInterrupted GetNetworkInterrupted() =>
            Kind == EventKind.NetworkInterrupted ? _networkInterrupted : throw new System.InvalidOperationException("event type mismatch");

        public NetworkResumed GetNetworkResumed() =>
            Kind == EventKind.NetworkResumed ? _networkResumed : throw new System.InvalidOperationException("event type mismatch");
    }
}
