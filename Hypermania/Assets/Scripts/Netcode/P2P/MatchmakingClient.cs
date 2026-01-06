using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using UnityEngine;

namespace Netcode.P2P
{
    public sealed class SteamMatchmakingClient
    {
        public CSteamID CurrentLobby => _currentLobby;
        public bool InLobby => _currentLobby.IsValid();
        public Action<List<CSteamID>> OnStartWithPlayers;

        public SteamMatchmakingClient()
        {
            RegisterCallbacks();

            Debug.Log("[Matchmaking] SteamMatchmakingClient constructed.");
        }

        public async Task<CSteamID> Create(int maxMembers = 2)
        {
            if (maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxMembers));
            Debug.Log($"[Matchmaking] Create(maxMembers={maxMembers})");
            await Leave();

            _lobbyCreatedTcs = new TaskCompletionSource<CSteamID>(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxMembers);
            _lobbyCreatedCallResult.Set(call);

            Debug.Log("[Matchmaking] CreateLobby issued. Waiting for LobbyCreated_t...");
            var lobbyId = await _lobbyCreatedTcs.Task;

            _currentLobby = lobbyId;
            Debug.Log($"[Matchmaking] Lobby created: {_currentLobby.m_SteamID}");

            SteamMatchmaking.SetLobbyData(_currentLobby, "version", "1");
            SteamMatchmaking.SetLobbyData(_currentLobby, "game", "Hypermania");

            Debug.Log($"[Matchmaking] Lobby data set.");

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
        }

        public Task Leave()
        {
            Debug.Log("[Matchmaking] Leave()");
            if (_currentLobby.IsValid())
            {
                Debug.Log($"[Matchmaking] Leaving lobby {_currentLobby.m_SteamID}");
                SteamMatchmaking.LeaveLobby(_currentLobby);
                _currentLobby = default;
            }
            return Task.CompletedTask;
        }

        public Task StartGame()
        {
            if (!_currentLobby.IsValid()) throw new InvalidOperationException("Not in a lobby.");

            CSteamID host = SteamMatchmaking.GetLobbyOwner(_currentLobby);

            if (host != SteamUser.GetSteamID())
            {
                throw new InvalidOperationException("Non-host tried to start the game");
            }

            Debug.Log($"[Matchmaking] StartGame(): lobby={_currentLobby.m_SteamID}");

            PublishPlayersToLobby();
            SendLobbyStartMessage();

            Debug.Log("[Matchmaking] Host sent START message.");
            return Task.CompletedTask;
        }

        private const string START_MSG = "__START";
        private const string HANDLE_CNT = "__HANDLE_CNT";
        private const string HANDLE_NUM = "__HANDLE_NUM";

        private CSteamID _currentLobby;

        private Callback<LobbyEnter_t> _lobbyEnterCb;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdateCb;
        private Callback<LobbyChatMsg_t> _lobbyChatMsgCb;
        private Callback<GameLobbyJoinRequested_t> _joinRequestedCb;
        private CallResult<LobbyCreated_t> _lobbyCreatedCallResult;
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdateCb;
        private bool _waitingForPlayers;


        private TaskCompletionSource<CSteamID> _lobbyCreatedTcs;
        private TaskCompletionSource<bool> _lobbyEnterTcs;

        private void RegisterCallbacks()
        {
            Debug.Log("[Matchmaking] RegisterCallbacks()");
            _lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _lobbyChatMsgCb = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMessage);
            _joinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);

            _lobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyDataUpdateCb = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
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
                CSteamID host = SteamMatchmaking.GetLobbyOwner(_currentLobby);
                Debug.Log($"[Matchmaking] Received START. host={host.m_SteamID}, me={SteamUser.GetSteamID()}");

                SteamMatchmaking.RequestLobbyData(_currentLobby);
                var players = GetPlayersFromLobbyData();
                if (players == null) { _waitingForPlayers = true; }
                else { OnStartWithPlayers?.Invoke(players); }
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            Debug.Log($"[Matchmaking] OnGameLobbyJoinRequested: lobby={data.m_steamIDLobby.m_SteamID}");
            // SteamMatchmaking.JoinLobby(data.m_steamIDLobby);
        }

        private void OnLobbyDataUpdate(LobbyDataUpdate_t data)
        {
            if (!_currentLobby.IsValid() || data.m_ulSteamIDLobby != _currentLobby.m_SteamID)
                return;

            if (_waitingForPlayers)
            {
                _waitingForPlayers = false;
                var handles = GetPlayersFromLobbyData();
                OnStartWithPlayers?.Invoke(handles);
            }
        }

        private void SendLobbyStartMessage()
        {
            Debug.Log($"[Matchmaking] Sending START lobby chat message. lobby={_currentLobby.m_SteamID}");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(START_MSG);
            SteamMatchmaking.SendLobbyChatMsg(_currentLobby, bytes, bytes.Length);
        }

        private void PublishPlayersToLobby()
        {
            if (!_currentLobby.IsValid()) return;

            if (SteamUser.GetSteamID() != SteamMatchmaking.GetLobbyOwner(_currentLobby))
            {
                throw new InvalidOperationException("Cannot publish handles to lobby as non host");
            }

            int count = SteamMatchmaking.GetNumLobbyMembers(_currentLobby);
            if (count <= 0) return;
            var players = new List<CSteamID>(count);
            for (int i = 0; i < count; i++)
            {
                var m = SteamMatchmaking.GetLobbyMemberByIndex(_currentLobby, i);
                if (!m.IsValid()) continue;
                players.Add(m);
            }
            players.Sort((a, b) => a.m_SteamID.CompareTo(b.m_SteamID));

            SteamMatchmaking.SetLobbyData(_currentLobby, HANDLE_CNT, $"{players.Count}");
            for (int i = 0; i < players.Count; i++)
            {
                SteamMatchmaking.SetLobbyData(_currentLobby, $"{HANDLE_NUM}_{i}", $"{players[i].m_SteamID}");
            }
            Debug.Log($"[Matchmaking] Published handles to lobby data");
        }

        private List<CSteamID> GetPlayersFromLobbyData()
        {
            if (!_currentLobby.IsValid())
                throw new InvalidOperationException("Lobby is not valid.");

            string handleCntStr = SteamMatchmaking.GetLobbyData(_currentLobby, HANDLE_CNT);
            if (string.IsNullOrEmpty(handleCntStr))
            {
                Debug.Log("[Matchmaking] No handles lobby data present.");
                return null;
            }
            if (!int.TryParse(handleCntStr, out int handleCnt) || handleCnt <= 0)
            {
                throw new InvalidOperationException($"Invalid handle count '{handleCntStr}'.");
            }

            var result = new List<CSteamID>(handleCnt);
            for (int i = 0; i < handleCnt; i++)
            {
                string key = $"{HANDLE_NUM}_{i}";
                string steamIdStr = SteamMatchmaking.GetLobbyData(_currentLobby, key);

                if (string.IsNullOrEmpty(steamIdStr))
                {
                    throw new InvalidOperationException($"Missing lobby data key '{key}'.");
                }

                if (!ulong.TryParse(steamIdStr, out ulong steamIdU64))
                {
                    throw new InvalidOperationException($"Invalid SteamID value '{steamIdStr}' for '{key}'.");
                }

                var steamId = new CSteamID(steamIdU64);
                if (!steamId.IsValid())
                {
                    throw new InvalidOperationException($"SteamID '{steamIdU64}' is not valid.");
                }

                result.Add(steamId);
            }
            return result;
        }
    }
}