using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Netcode.Rollback;
using Netcode.Rollback.Network;
using Steamworks;
using UnityEngine;

public sealed class SteamMatchmakingClient : IDisposable, INonBlockingSocket<CSteamID>
{
    // ---------- Public API ----------
    public CSteamID CurrentLobby => _currentLobby;
    public bool InLobby => _currentLobby.IsValid();
    public bool HasPeer => _peer.IsValid();
    public CSteamID Me => SteamUser.GetSteamID();
    public CSteamID Peer => _peer;

    /// <summary>
    /// After StartGame completes, provides stable contiguous handles in [0, maxPlayers)
    /// for each lobby member. For 1v1, this is {host=0, other=1}.
    /// </summary>
    public IReadOnlyDictionary<CSteamID, int> Handles => _handles;
    public int MyHandle => Handles[Me];

    public event Action<CSteamID> OnStartGame;

    public SteamMatchmakingClient()
    {
        RegisterCallbacks();
    }

    public async Task<CSteamID> Create(int maxMembers = 2)
    {
        if (maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxMembers));
        EnsureNotDisposed();

        await Leave().ConfigureAwait(false);

        _maxMembers = maxMembers;

        _lobbyCreatedTcs = new TaskCompletionSource<CSteamID>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxMembers);
        _lobbyCreatedCallResult.Set(call);

        var lobbyId = await _lobbyCreatedTcs.Task.ConfigureAwait(false);
        _currentLobby = lobbyId;

        SteamMatchmaking.SetLobbyData(_currentLobby, "version", "1");
        SteamMatchmaking.SetLobbyData(_currentLobby, "game", "EnergyDrink");
        SteamMatchmaking.SetLobbyData(_currentLobby, "maxMembers", maxMembers.ToString());

        RefreshPeerFromLobby();
        return lobbyId;
    }

    public async Task Join(CSteamID lobbyId)
    {
        if (!lobbyId.IsValid()) throw new ArgumentException("Invalid lobby id.", nameof(lobbyId));
        EnsureNotDisposed();

        await Leave().ConfigureAwait(false);

        _lobbyEnterTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SteamMatchmaking.JoinLobby(lobbyId);

        await _lobbyEnterTcs.Task.ConfigureAwait(false);
        _currentLobby = lobbyId;

        if (int.TryParse(SteamMatchmaking.GetLobbyData(_currentLobby, "maxMembers"), out int mm) && mm > 0)
            _maxMembers = mm;

        RefreshPeerFromLobby();
    }

    public Task Leave()
    {
        EnsureNotDisposed();

        CloseConnection();

        if (_currentLobby.IsValid())
        {
            SteamMatchmaking.LeaveLobby(_currentLobby);
            _currentLobby = default;
        }

        _peer = default;
        _host = default;
        _startArmed = false;
        _startSentByHost = false;
        _iAmHost = false;
        _handles.Clear();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Host: creates a P2P listen socket, broadcasts "__START__", accepts inbound connection.
    /// Peer: waits for "__START__", then connects to host (lobby owner).
    /// </summary>
    public Task<Dictionary<CSteamID, int>> StartGame()
    {
        EnsureNotDisposed();
        if (!_currentLobby.IsValid()) throw new InvalidOperationException("Not in a lobby.");

        RefreshPeerFromLobby();
        if (!_peer.IsValid()) throw new InvalidOperationException("No peer in lobby yet.");

        _startArmed = true;
        _startGameTcs = new TaskCompletionSource<Dictionary<CSteamID, int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _host = SteamMatchmaking.GetLobbyOwner(_currentLobby);
        _iAmHost = (_host == Me);

        if (_iAmHost)
        {
            // Host publishes deterministic handles (optional) and starts listening BEFORE telling clients to connect.
            ComputeHandlesDeterministic();
            PublishHandlesToLobby();

            EnsureListenSocket();

            SendLobbyStartMessage();
            _startSentByHost = true;

            // Do NOT call ConnectP2P as host; peer will connect inbound and we accept in callback.
        }
        else
        {
            // Peer waits for START message in OnLobbyChatMessage then connects to _host.
        }

        return _startGameTcs.Task;
    }

    // ---------- INonBlockingSocket<CSteamID> ----------
    public void SendTo(in Message message, CSteamID addr)
    {
        EnsureNotDisposed();
        if (!addr.IsValid()) throw new ArgumentException("Invalid addr.", nameof(addr));
        if (_conn == HSteamNetConnection.Invalid)
            throw new InvalidOperationException("No active connection.");

        // 1v1 only: enforce sending only to the connected peer
        if (_peer.IsValid() && addr != _peer)
            throw new InvalidOperationException($"Attempted to send to {addr} but connected peer is {_peer}.");

        byte[] payload = new byte[message.SerdeSize()];
        message.Serialize(payload);

        unsafe
        {
            fixed (byte* pData = payload)
            {
                var res = SteamNetworkingSockets.SendMessageToConnection(
                    _conn,
                    (IntPtr)pData,
                    (uint)payload.Length,
                    Constants.k_nSteamNetworkingSend_UnreliableNoNagle,
                    out _);

                if (res != EResult.k_EResultOK)
                    throw new InvalidOperationException($"SendMessageToConnection failed: {res}");
            }
        }
    }

    public List<(CSteamID addr, Message message)> ReceiveAllMessages()
    {
        EnsureNotDisposed();
        var received = new List<(CSteamID addr, Message message)>();

        if (_conn == HSteamNetConnection.Invalid)
            return received;

        const int BATCH = 32;
        IntPtr[] ptrs = new IntPtr[BATCH];

        while (true)
        {
            int n = SteamNetworkingSockets.ReceiveMessagesOnConnection(_conn, ptrs, BATCH);
            if (n <= 0) break;

            for (int i = 0; i < n; i++)
            {
                var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptrs[i]);

                try
                {
                    byte[] data = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);

                    Message decoded = default;
                    decoded.Deserialize(data);

                    // 1v1: one peer
                    received.Add((_peer, decoded));
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(ptrs[i]);
                }
            }
        }

        return received;
    }

    // ---------- Internals ----------
    private const string START_MSG = "__START__";
    private const string HANDLES_KEY = "handles"; // "steamid:handle,steamid:handle,..."

    private bool _disposed;

    private int _maxMembers = 2;

    private CSteamID _currentLobby;
    private CSteamID _peer;
    private CSteamID _host;

    private bool _iAmHost;

    private readonly Dictionary<CSteamID, int> _handles = new();

    private HSteamNetConnection _conn = HSteamNetConnection.Invalid;
    private HSteamListenSocket _listen = HSteamListenSocket.Invalid;

    private Callback<LobbyEnter_t> _lobbyEnterCb;
    private Callback<LobbyChatUpdate_t> _lobbyChatUpdateCb;
    private Callback<LobbyChatMsg_t> _lobbyChatMsgCb;
    private Callback<GameLobbyJoinRequested_t> _joinRequestedCb;

    private Callback<SteamNetConnectionStatusChangedCallback_t> _connStatusCb;

    private CallResult<LobbyCreated_t> _lobbyCreatedCallResult;
    private TaskCompletionSource<CSteamID> _lobbyCreatedTcs;
    private TaskCompletionSource<bool> _lobbyEnterTcs;

    private TaskCompletionSource<Dictionary<CSteamID, int>> _startGameTcs;
    private bool _startArmed;
    private bool _startSentByHost;

    private void RegisterCallbacks()
    {
        _lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
        _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        _lobbyChatMsgCb = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);
        _joinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);

        _connStatusCb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnNetConnectionStatusChanged);

        _lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
    }

    private void OnLobbyCreated(LobbyCreated_t data, bool ioFailure)
    {
        if (_lobbyCreatedTcs == null) return;

        if (ioFailure || data.m_eResult != EResult.k_EResultOK)
        {
            _lobbyCreatedTcs.TrySetException(
                new InvalidOperationException($"CreateLobby failed: ioFailure={ioFailure}, result={data.m_eResult}"));
            return;
        }

        _lobbyCreatedTcs.TrySetResult(new CSteamID(data.m_ulSteamIDLobby));
    }

    private void OnLobbyEnter(LobbyEnter_t data)
    {
        bool ok = data.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
        if (!ok)
        {
            _lobbyEnterTcs?.TrySetException(
                new InvalidOperationException($"JoinLobby failed: EChatRoomEnterResponse={data.m_EChatRoomEnterResponse}"));
            return;
        }

        _lobbyEnterTcs?.TrySetResult(true);
    }

    private void OnLobbyChatUpdate(LobbyChatUpdate_t data)
    {
        if (!_currentLobby.IsValid() || data.m_ulSteamIDLobby != _currentLobby.m_SteamID)
            return;

        RefreshPeerFromLobby();
    }

    private void OnLobbyChatMessage(LobbyChatMsg_t data)
    {
        if (!_currentLobby.IsValid() || data.m_ulSteamIDLobby != _currentLobby.m_SteamID)
            return;

        CSteamID user;
        EChatEntryType type;
        byte[] buffer = new byte[256];

        int len = SteamMatchmaking.GetLobbyChatEntry(
            new CSteamID(data.m_ulSteamIDLobby),
            (int)data.m_iChatID,
            out user,
            buffer,
            buffer.Length,
            out type);

        if (len <= 0) return;

        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, len).TrimEnd('\0');

        if (text == START_MSG)
        {
            _startSentByHost = true;

            _host = SteamMatchmaking.GetLobbyOwner(_currentLobby);
            _iAmHost = (_host == Me);

            // Compute handles deterministically on all clients (host+joiners).
            ComputeHandlesDeterministic();
            TryVerifyHandlesFromLobbyData();

            RefreshPeerFromLobby();

            // Peer connects to host. Host does NOT connect.
            if (!_iAmHost && _host.IsValid())
                EnsureClientConnectionToHost(_host);
        }
    }

    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
    {
        // Optional: auto-join when invited / clicked join.
        // SteamMatchmaking.JoinLobby(data.m_steamIDLobby);
    }

    private void RefreshPeerFromLobby()
    {
        _peer = default;
        if (!_currentLobby.IsValid()) return;

        int count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
        for (int i = 0; i < count; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(_currentLobby, i);
            if (member.IsValid() && member != Me)
            {
                _peer = member;
                break;
            }
        }
    }

    private void SendLobbyStartMessage()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(START_MSG);
        SteamMatchmaking.SendLobbyChatMsg(_currentLobby, bytes, bytes.Length);
    }

    private void ComputeHandlesDeterministic()
    {
        _handles.Clear();
        if (!_currentLobby.IsValid()) return;

        int count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
        if (count <= 0) return;

        int n = Math.Min(count, Math.Max(1, _maxMembers));

        var owner = SteamMatchmaking.GetLobbyOwner(_currentLobby);

        var others = new List<CSteamID>(n);
        for (int i = 0; i < count; i++)
        {
            var m = SteamMatchmaking.GetLobbyMemberByIndex(_currentLobby, i);
            if (!m.IsValid()) continue;
            if (m == owner) continue;
            others.Add(m);
        }

        others.Sort((a, b) => a.m_SteamID.CompareTo(b.m_SteamID));

        _handles[owner] = 0;

        int handle = 1;
        for (int i = 0; i < others.Count && handle < _maxMembers; i++, handle++)
            _handles[others[i]] = handle;
    }

    private void PublishHandlesToLobby()
    {
        if (!_currentLobby.IsValid()) return;

        var parts = new List<string>(_handles.Count);
        foreach (var kv in _handles)
            parts.Add($"{kv.Key.m_SteamID}:{kv.Value}");

        SteamMatchmaking.SetLobbyData(_currentLobby, HANDLES_KEY, string.Join(",", parts));
    }

    private void TryVerifyHandlesFromLobbyData()
    {
        string s = SteamMatchmaking.GetLobbyData(_currentLobby, HANDLES_KEY);
        if (string.IsNullOrEmpty(s)) return;

        var parsed = new Dictionary<ulong, int>();
        var entries = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var e in entries)
        {
            var kv = e.Split(':');
            if (kv.Length != 2) continue;
            if (!ulong.TryParse(kv[0], out ulong sid)) continue;
            if (!int.TryParse(kv[1], out int h)) continue;
            parsed[sid] = h;
        }

        foreach (var kv in _handles)
        {
            if (!parsed.TryGetValue(kv.Key.m_SteamID, out int h) || h != kv.Value)
                return;
        }
    }

    private void EnsureListenSocket()
    {
        if (_listen != HSteamListenSocket.Invalid)
            return;

        // Host-only listen socket.
        _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
        if (_listen == HSteamListenSocket.Invalid)
            throw new InvalidOperationException("CreateListenSocketP2P returned invalid listen socket handle.");
    }

    private HSteamNetConnection EnsureClientConnectionToHost(CSteamID host)
    {
        if (!host.IsValid()) throw new InvalidOperationException("Host invalid.");
        if (_iAmHost) throw new InvalidOperationException("Host should not ConnectP2P to itself.");

        if (_conn != HSteamNetConnection.Invalid)
            return _conn;

        var id = new SteamNetworkingIdentity();
        id.SetSteamID(host);

        _conn = SteamNetworkingSockets.ConnectP2P(ref id, 0, 0, null);
        if (_conn == HSteamNetConnection.Invalid)
            throw new InvalidOperationException("ConnectP2P returned invalid connection handle.");

        return _conn;
    }

    private void OnNetConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t data)
    {
        switch (data.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
            {
                // Host accepts inbound connections (peer initiated ConnectP2P).
                if (_iAmHost)
                {
                    Debug.Log("Accepting inbound P2P connection");
                    SteamNetworkingSockets.AcceptConnection(data.m_hConn);
                }
                break;
            }

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
            {
                // For host, connection handle arrives here (inbound).
                // For peer, this is the same handle returned by ConnectP2P, but keep it consistent.
                if (_conn == HSteamNetConnection.Invalid)
                    _conn = data.m_hConn;

                // Identify remote and set _peer if needed.
                var remoteId = data.m_info.m_identityRemote;
                var remoteSteamId = remoteId.GetSteamID();
                if (remoteSteamId.IsValid())
                {
                    // For host, remote is the peer. For peer, remote is the host.
                    _peer = remoteSteamId;
                }
                else
                {
                    // Fallback to lobby-derived peer.
                    RefreshPeerFromLobby();
                }

                // Ensure handles exist even if host didn't publish.
                if (_handles.Count == 0)
                {
                    ComputeHandlesDeterministic();
                    TryVerifyHandlesFromLobbyData();
                }

                if (_startArmed && _startSentByHost)
                {
                    _startGameTcs?.TrySetResult(new Dictionary<CSteamID, int>(_handles));
                    OnStartGame?.Invoke(_peer);
                }

                break;
            }

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            {
                if (_conn != HSteamNetConnection.Invalid)
                {
                    SteamNetworkingSockets.CloseConnection(_conn, 0, "closed", false);
                    _conn = HSteamNetConnection.Invalid;
                }

                _startGameTcs?.TrySetException(
                    new InvalidOperationException($"Connection closed: state={data.m_info.m_eState}, endReason={data.m_info.m_eEndReason}"));
                break;
            }
        }
    }

    private void CloseConnection()
    {
        if (_conn != HSteamNetConnection.Invalid)
        {
            SteamNetworkingSockets.CloseConnection(_conn, 0, "leaving", false);
            _conn = HSteamNetConnection.Invalid;
        }

        if (_listen != HSteamListenSocket.Invalid)
        {
            SteamNetworkingSockets.CloseListenSocket(_listen);
            _listen = HSteamListenSocket.Invalid;
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SteamMatchmakingClient));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseConnection();

        _lobbyEnterCb?.Unregister();
        _lobbyChatUpdateCb?.Unregister();
        _lobbyChatMsgCb?.Unregister();
        _joinRequestedCb?.Unregister();
        _connStatusCb?.Unregister();
    }
}
