using System;
using System.Collections.Generic;
using System.Net;
using Netcode.P2P;
using Netcode.Rollback;
using Netcode.Rollback.Sessions;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    [Header("Servers")]
    public string ServerIp = "144.126.152.174";
    public int HttpPort = 9000;
    public int RelayPort = 9001;

    [SerializeField]
    private GameObject _bob1;
    [SerializeField]
    private GameObject _bob2;
    private GameState _curState;
    private P2PSession<GameState, Input, EndPoint> _session;
    private SynapseClient _synapse;

    private int? _handle;
    private int? _opponentHandle;
    private ulong? _roomId;

    private bool _playing;

    void Awake()
    {
        _synapse = new SynapseClient(ServerIp, HttpPort, RelayPort);
        _playing = false;
        _handle = null;
        _roomId = null;
        _opponentHandle = null;
    }

    async void OnDestroy()
    {
        if (_synapse != null)
        {
            await _synapse.LeaveRoomAsync();
            _synapse.Dispose();
        }
        _handle = null;
        _opponentHandle = null;
        _roomId = null;
        _session = null;
        _playing = false;
        _curState = GameState.New();
    }

    void FixedUpdate()
    {
        NetworkingLoop();
        if (!_playing)
        {
            return;
        }
        GameLoop();
    }

    void NetworkingLoop()
    {
        List<WsEvent> events = _synapse.PumpWebSocket();
        foreach (WsEvent ev in events)
        {
            switch (ev.Kind)
            {
                case WsEventKind.JoinedRoom:
                    _roomId = ev.RoomId;
                    Debug.Log($"Joined room {ev.RoomId}");
                    break;
                case WsEventKind.PeerLeft:
                    _opponentHandle = null;
                    Debug.Log($"Peer {ev.Handle} left");
                    break;
                case WsEventKind.PeerJoined:
                    _opponentHandle = (int)ev.Handle;
                    Debug.Log($"Peer {ev.Handle} joined");
                    break;
                case WsEventKind.YouAre:
                    _handle = (int)ev.Handle;
                    Debug.Log($"You are {ev.Handle}");
                    break;
            }
        }
    }

    public async void CreateRoom()
    {
        Debug.Log("creating room...");
        await _synapse.CreateRoomAsync();
    }

    public async void JoinRoom(ulong roomId)
    {
        Debug.Log($"joining room {roomId}...");
        await _synapse.JoinRoomAsync(roomId);
    }

    public async void LeaveRoom()
    {
        Debug.Log($"leaving room...");
        await _synapse.LeaveRoomAsync();
        _roomId = null;
        _handle = null;
        _opponentHandle = null;
    }

    public async void StartGame()
    {
        Debug.Log($"handle: {_handle}, opponentHandle: {_opponentHandle}, roomId: {_roomId}");
        if (_handle == null || _opponentHandle == null || _roomId == null) return;

        EndPoint ep = await _synapse.ConnectAsync();
        Debug.Log($"using remote ep {ep}");
        _curState = GameState.New();
        SessionBuilder<Input, EndPoint> builder = new SessionBuilder<Input, EndPoint>().WithNumPlayers(2).WithFps(50);
        builder.AddPlayer(new PlayerType<EndPoint> { Kind = PlayerKind.Local, Address = null }, new PlayerHandle(_handle.Value));
        builder.AddPlayer(new PlayerType<EndPoint> { Kind = PlayerKind.Remote, Address = ep }, new PlayerHandle(_opponentHandle.Value));
        _session = builder.StartP2PSession<GameState>(_synapse);
        _playing = true;
    }

    void GameLoop()
    {
        InputFlags[] inputs = new InputFlags[2];

        InputFlags f1Input = InputFlags.None;
        if (UnityEngine.Input.GetKey(KeyCode.A))
            f1Input |= InputFlags.Left;
        if (UnityEngine.Input.GetKey(KeyCode.D))
            f1Input |= InputFlags.Right;
        if (UnityEngine.Input.GetKey(KeyCode.W))
            f1Input |= InputFlags.Up;
        inputs[0] = f1Input;

        InputFlags f2Input = InputFlags.None;
        if (UnityEngine.Input.GetKey(KeyCode.LeftArrow))
            f2Input |= InputFlags.Left;
        if (UnityEngine.Input.GetKey(KeyCode.RightArrow))
            f2Input |= InputFlags.Right;
        if (UnityEngine.Input.GetKey(KeyCode.UpArrow))
            f2Input |= InputFlags.Up;
        inputs[1] = f2Input;

        _session.PollRemoteClients();

        foreach (RollbackEvent<Input, EndPoint> ev in _session.DrainEvents())
        {
            // Debug.Log($"Event: {ev}");
        }

        if (_session.CurrentState == SessionState.Running)
        {
            if (_handle == 0)
                _session.AddLocalInput(new PlayerHandle(0), new Input(inputs[0]));
            if (_handle == 1)
                _session.AddLocalInput(new PlayerHandle(1), new Input(inputs[1]));

            try
            {
                List<RollbackRequest<GameState, Input>> requests = _session.AdvanceFrame();
                foreach (RollbackRequest<GameState, Input> request in requests)
                {
                    switch (request.Kind)
                    {
                        case RollbackRequestKind.SaveGameStateReq:
                            RollbackRequest<GameState, Input>.SaveGameState saveReq = request.GetSaveGameStateReq();
                            saveReq.Cell.Save(saveReq.Frame, _curState, _curState.Checksum());
                            break;
                        case RollbackRequestKind.LoadGameStateReq:
                            _curState = request.GetLoadGameStateReq().Cell.State.Value.Data;
                            break;
                        case RollbackRequestKind.AdvanceFrameReq:
                            _curState.Simulate(request.GetAdvanceFrameRequest().Inputs);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log($"Exception {e}");
            }
        }

        Render();
    }

    void Render()
    {
        if (_bob1 != null)
            _bob1.transform.position = _curState.F1Info.Position;
        if (_bob2 != null)
            _bob2.transform.position = _curState.F2Info.Position;
    }
}
