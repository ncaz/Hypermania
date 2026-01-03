using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Netcode.Rollback;
using Netcode.Rollback.Network;
using Steamworks;
using UnityEngine;

public sealed class SteamMatchmakingClient : INonBlockingSocket<CSteamID>
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

        Debug.Log("[Matchmaking] SteamMatchmakingClient constructed. Initializing relay network access.");
        SteamNetworkingUtils.InitRelayNetworkAccess();
    }

    public async Task<CSteamID> Create(int maxMembers = 2)
    {
        if (maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxMembers));
        Debug.Log($"[Matchmaking] Create(maxMembers={maxMembers})");
        await Leave();

        _maxMembers = maxMembers;

        _lobbyCreatedTcs = new TaskCompletionSource<CSteamID>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxMembers);
        _lobbyCreatedCallResult.Set(call);

        Debug.Log("[Matchmaking] CreateLobby issued. Waiting for LobbyCreated_t...");
        var lobbyId = await _lobbyCreatedTcs.Task;

        _currentLobby = lobbyId;
        Debug.Log($"[Matchmaking] Lobby created: {_currentLobby.m_SteamID}");

        SteamMatchmaking.SetLobbyData(_currentLobby, "version", "1");
        SteamMatchmaking.SetLobbyData(_currentLobby, "game", "EnergyDrink");
        SteamMatchmaking.SetLobbyData(_currentLobby, "maxMembers", maxMembers.ToString());

        RefreshPeerFromLobby();
        Debug.Log($"[Matchmaking] Lobby data set. Peer={(_peer.IsValid() ? _peer.m_SteamID.ToString() : "none")}");

        return lobbyId;
    }

    public async Task Join(CSteamID lobbyId)
    {
        if (!lobbyId.IsValid()) throw new ArgumentException("Invalid lobby id.", nameof(lobbyId));
        Debug.Log($"[Matchmaking] Join(lobbyId={lobbyId.m_SteamID})");
        await Leave();

        _lobbyEnterTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SteamMatchmaking.JoinLobby(lobbyId);

        Debug.Log("[Matchmaking] JoinLobby issued. Waiting for LobbyEnter_t...");
        await _lobbyEnterTcs.Task;

        _currentLobby = lobbyId;
        Debug.Log($"[Matchmaking] Joined lobby: {_currentLobby.m_SteamID}");

        if (int.TryParse(SteamMatchmaking.GetLobbyData(_currentLobby, "maxMembers"), out int mm) && mm > 0)
        {
            _maxMembers = mm;
            Debug.Log($"[Matchmaking] Read lobby maxMembers={_maxMembers}");
        }
        else
        {
            Debug.Log("[Matchmaking] Lobby maxMembers missing/invalid; keeping current.");
        }

        RefreshPeerFromLobby();
        Debug.Log($"[Matchmaking] After join, Peer={(_peer.IsValid() ? _peer.m_SteamID.ToString() : "none")}");
    }

    public Task Leave()
    {
        Debug.Log("[Matchmaking] Leave()");
        CloseConnection();

        if (_currentLobby.IsValid())
        {
            Debug.Log($"[Matchmaking] Leaving lobby {_currentLobby.m_SteamID}");
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
        if (!_currentLobby.IsValid()) throw new InvalidOperationException("Not in a lobby.");

        RefreshPeerFromLobby();
        if (!_peer.IsValid()) throw new InvalidOperationException("No peer in lobby yet.");

        _startArmed = true;
        _startGameTcs = new TaskCompletionSource<Dictionary<CSteamID, int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _host = SteamMatchmaking.GetLobbyOwner(_currentLobby);
        _iAmHost = (_host == Me);

        Debug.Log($"[Matchmaking] StartGame(): lobby={_currentLobby.m_SteamID}, host={_host.m_SteamID}, me={Me.m_SteamID}, iAmHost={_iAmHost}, peer={_peer.m_SteamID}");

        if (_iAmHost)
        {
            ComputeHandlesDeterministic();
            PublishHandlesToLobby();

            EnsureListenSocket();

            SendLobbyStartMessage();
            _startSentByHost = true;

            Debug.Log("[Matchmaking] Host started listen socket, published handles, and sent START message. Awaiting inbound connection...");
        }
        else
        {
            Debug.Log("[Matchmaking] Client armed StartGame. Waiting for START message from host...");
        }

        return _startGameTcs.Task;
    }

    // ---------- INonBlockingSocket<CSteamID> ----------
    public void SendTo(in Message message, CSteamID addr)
    {
        if (!addr.IsValid()) throw new ArgumentException("Invalid addr.", nameof(addr));
        if (_conn == HSteamNetConnection.Invalid)
            throw new InvalidOperationException("No active connection.");

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

    // Transport tuning:
    // - Enable ICE so direct/NAT-punched routes are considered.
    // - Add an SDR penalty so relayed routes are only chosen when needed.
    private const int SDR_PENALTY_MS = 1000;
    private const int ICE_ENABLE_ALL = unchecked((int)0x7fffffff);

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
        Debug.Log("[Matchmaking] RegisterCallbacks()");
        _lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
        _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        _lobbyChatMsgCb = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);
        _joinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);

        _connStatusCb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnNetConnectionStatusChanged);

        _lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
    }

    private void OnLobbyCreated(LobbyCreated_t data, bool ioFailure)
    {
        if (_lobbyCreatedTcs == null)
        {
            Debug.Log("[Matchmaking] OnLobbyCreated: no TCS (ignored).");
            return;
        }

        Debug.Log($"[Matchmaking] OnLobbyCreated: ioFailure={ioFailure}, result={data.m_eResult}, lobby={data.m_ulSteamIDLobby}");

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
        Debug.Log($"[Matchmaking] OnLobbyEnter: lobby={data.m_ulSteamIDLobby}, response={data.m_EChatRoomEnterResponse}");

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

        Debug.Log($"[Matchmaking] OnLobbyChatUpdate: lobby={data.m_ulSteamIDLobby}, userChanged={data.m_ulSteamIDUserChanged}, makingChange={data.m_ulSteamIDMakingChange}, stateChange={data.m_rgfChatMemberStateChange}");

        RefreshPeerFromLobby();
        Debug.Log($"[Matchmaking] Peer refresh after chat update: Peer={(_peer.IsValid() ? _peer.m_SteamID.ToString() : "none")}");
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
        Debug.Log($"[Matchmaking] OnLobbyChatMessage: from={user.m_SteamID}, type={type}, text='{text}'");

        if (text == START_MSG)
        {
            _startSentByHost = true;

            _host = SteamMatchmaking.GetLobbyOwner(_currentLobby);
            _iAmHost = (_host == Me);

            Debug.Log($"[Matchmaking] Received START. host={_host.m_SteamID}, me={Me.m_SteamID}, iAmHost={_iAmHost}");

            ComputeHandlesDeterministic();
            TryVerifyHandlesFromLobbyData();

            RefreshPeerFromLobby();
            Debug.Log($"[Matchmaking] After START: Peer={(_peer.IsValid() ? _peer.m_SteamID.ToString() : "none")}");

            if (!_iAmHost && _host.IsValid())
            {
                Debug.Log($"[Matchmaking] Client connecting to host via ConnectP2P. host={_host.m_SteamID}, sdrPenaltyMs={SDR_PENALTY_MS}, iceEnable=ALL");
                EnsureClientConnectionToHost(_host);
            }
        }
    }

    private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
    {
        Debug.Log($"[Matchmaking] OnGameLobbyJoinRequested: lobby={data.m_steamIDLobby.m_SteamID}");
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
        Debug.Log($"[Matchmaking] Sending START lobby chat message. lobby={_currentLobby.m_SteamID}");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(START_MSG);
        SteamMatchmaking.SendLobbyChatMsg(_currentLobby, bytes, bytes.Length);
    }

    private void ComputeHandlesDeterministic()
    {
        _handles.Clear();
        if (!_currentLobby.IsValid()) return;

        int count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
        if (count <= 0) return;

        var owner = SteamMatchmaking.GetLobbyOwner(_currentLobby);

        var others = new List<CSteamID>(count);
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

        Debug.Log($"[Matchmaking] Computed handles: owner={owner.m_SteamID}->0, count={_handles.Count}");
    }

    private void PublishHandlesToLobby()
    {
        if (!_currentLobby.IsValid()) return;

        var parts = new List<string>(_handles.Count);
        foreach (var kv in _handles)
            parts.Add($"{kv.Key.m_SteamID}:{kv.Value}");

        string encoded = string.Join(",", parts);
        SteamMatchmaking.SetLobbyData(_currentLobby, HANDLES_KEY, encoded);

        Debug.Log($"[Matchmaking] Published handles to lobby data key='{HANDLES_KEY}': {encoded}");
    }

    private void TryVerifyHandlesFromLobbyData()
    {
        string s = SteamMatchmaking.GetLobbyData(_currentLobby, HANDLES_KEY);
        if (string.IsNullOrEmpty(s))
        {
            Debug.Log("[Matchmaking] No handles lobby data to verify.");
            return;
        }

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
            {
                Debug.LogWarning($"[Matchmaking] Handle verify mismatch for {kv.Key.m_SteamID}: local={kv.Value}, lobby={(parsed.TryGetValue(kv.Key.m_SteamID, out int lh) ? lh.ToString() : "missing")}");
                return;
            }
        }

        Debug.Log("[Matchmaking] Handles verified against lobby data.");
    }

    private void EnsureListenSocket()
    {
        if (_listen != HSteamListenSocket.Invalid)
            return;

        Debug.Log($"[Matchmaking] Creating listen socket (P2P). virtPort=0");
        _listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
        if (_listen == HSteamListenSocket.Invalid)
            throw new InvalidOperationException("CreateListenSocketP2P returned invalid listen socket handle.");

        Debug.Log($"[Matchmaking] Listen socket created: {_listen.m_HSteamListenSocket}. Applying ICE/SDR config via SetConfigValue...");

        // Apply settings to the listen socket so accepted connections inherit them.
        unsafe
        {
            int iceEnable = ICE_ENABLE_ALL;         // 0x7fffffff ("All")
            int sdrPenalty = SDR_PENALTY_MS;        // e.g. 1000

            bool okIce = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_ListenSocket,
                (IntPtr)_listen.m_HSteamListenSocket,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                new IntPtr(&iceEnable));

            bool okSdr = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_SDR_Penalty,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_ListenSocket,
                (IntPtr)_listen.m_HSteamListenSocket,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                new IntPtr(&sdrPenalty));

            Debug.Log($"[Matchmaking] SetConfigValue(listen): ICE_Enable={(okIce ? "OK" : "FAIL")} value={iceEnable}, SDR_Penalty={(okSdr ? "OK" : "FAIL")} value={sdrPenalty}");
        }
    }

    private HSteamNetConnection EnsureClientConnectionToHost(CSteamID host)
    {
        if (!host.IsValid()) throw new InvalidOperationException("Host invalid.");
        if (_iAmHost) throw new InvalidOperationException("Host should not ConnectP2P to itself.");

        if (_conn != HSteamNetConnection.Invalid)
        {
            Debug.Log($"[Matchmaking] Already have connection handle: {_conn.m_HSteamNetConnection}");
            return _conn;
        }

        var id = new SteamNetworkingIdentity();
        id.SetSteamID(host);

        Debug.Log($"[Matchmaking] Calling ConnectP2P to host={host.m_SteamID} (virtPort=0)");
        _conn = SteamNetworkingSockets.ConnectP2P(ref id, 0, 0, null);

        if (_conn == HSteamNetConnection.Invalid)
            throw new InvalidOperationException("ConnectP2P returned invalid connection handle.");

        Debug.Log($"[Matchmaking] ConnectP2P returned connection handle: {_conn.m_HSteamNetConnection}. Applying ICE/SDR config via SetConfigValue...");

        // Apply settings to this specific outbound connection attempt.
        unsafe
        {
            int iceEnable = ICE_ENABLE_ALL;     // 0x7fffffff ("All")
            int sdrPenalty = SDR_PENALTY_MS;    // e.g. 1000

            bool okIce = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                (IntPtr)_conn.m_HSteamNetConnection,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                new IntPtr(&iceEnable));

            bool okSdr = SteamNetworkingUtils.SetConfigValue(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_SDR_Penalty,
                ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection,
                (IntPtr)_conn.m_HSteamNetConnection,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                new IntPtr(&sdrPenalty));

            Debug.Log($"[Matchmaking] SetConfigValue(conn): ICE_Enable={(okIce ? "OK" : "FAIL")} value={iceEnable}, SDR_Penalty={(okSdr ? "OK" : "FAIL")} value={sdrPenalty}");
        }

        return _conn;
    }


    private void OnNetConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t data)
    {
        Debug.Log($"[Matchmaking] OnNetConnectionStatusChanged: conn={data.m_hConn.m_HSteamNetConnection}, old={data.m_eOldState}, new={data.m_info.m_eState}, listen={data.m_info.m_hListenSocket.m_HSteamListenSocket}, endReason={data.m_info.m_eEndReason}");

        switch (data.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                {
                    if (_iAmHost)
                    {
                        Debug.Log("[Matchmaking] Incoming connection in Connecting state. Accepting now.");
                        var r = SteamNetworkingSockets.AcceptConnection(data.m_hConn);
                        Debug.Log($"[Matchmaking] AcceptConnection result: {r}");
                    }
                    else
                    {
                        Debug.Log("[Matchmaking] Outbound connection in Connecting state.");
                    }
                    break;
                }

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                {
                    if (_conn == HSteamNetConnection.Invalid)
                    {
                        _conn = data.m_hConn;
                        Debug.Log($"[Matchmaking] Stored connection handle: {_conn.m_HSteamNetConnection}");
                    }

                    var remoteId = data.m_info.m_identityRemote;
                    var remoteSteamId = remoteId.GetSteamID();
                    if (remoteSteamId.IsValid())
                    {
                        _peer = remoteSteamId;
                        Debug.Log($"[Matchmaking] Connected. Remote SteamID={_peer.m_SteamID}");
                    }
                    else
                    {
                        RefreshPeerFromLobby();
                        Debug.Log($"[Matchmaking] Connected but remote identity invalid; using lobby-derived peer={(_peer.IsValid() ? _peer.m_SteamID.ToString() : "none")}");
                    }

                    if (_handles.Count == 0)
                    {
                        ComputeHandlesDeterministic();
                        TryVerifyHandlesFromLobbyData();
                    }

                    if (_startArmed && _startSentByHost)
                    {
                        Debug.Log("[Matchmaking] Connection established and start armed. Completing StartGame TCS.");
                        _startGameTcs?.TrySetResult(new Dictionary<CSteamID, int>(_handles));
                        OnStartGame?.Invoke(_peer);
                    }
                    else
                    {
                        Debug.Log($"[Matchmaking] Connected but start not armed/sent yet. startArmed={_startArmed}, startSentByHost={_startSentByHost}");
                    }

                    break;
                }

            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                {
                    Debug.LogWarning($"[Matchmaking] Connection ended. state={data.m_info.m_eState}, endReason={data.m_info.m_eEndReason}, debug='{data.m_info.m_szEndDebug}'");

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
            Debug.Log($"[Matchmaking] Closing connection: {_conn.m_HSteamNetConnection}");
            SteamNetworkingSockets.CloseConnection(_conn, 0, "leaving", false);
            _conn = HSteamNetConnection.Invalid;
        }

        if (_listen != HSteamListenSocket.Invalid)
        {
            Debug.Log($"[Matchmaking] Closing listen socket: {_listen.m_HSteamListenSocket}");
            SteamNetworkingSockets.CloseListenSocket(_listen);
            _listen = HSteamListenSocket.Invalid;
        }
    }
}
