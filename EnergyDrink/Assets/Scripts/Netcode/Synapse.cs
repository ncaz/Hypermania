using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class Synapse
{
    private string _url;
    public Synapse(string url, int port)
    {
        _url = $"http://{url}:{port}";
    }

    [Serializable] private class CreateRoomReq { public string client_id; }
    [Serializable] public class CreateRoomResp { public ulong room_id; }

    public IEnumerator CreateRoomCo(string clientId, Action<CreateRoomResp> onSuccess)
    {
        var url = $"{_url}/create_room";
        var body = JsonUtility.ToJson(new CreateRoomReq { client_id = clientId });

        yield return SendHttp(url, body, text =>
        {
            var resp = JsonUtility.FromJson<CreateRoomResp>(text);
            Debug.Log($"[HTTP] Created room {resp.room_id}");
            onSuccess(resp);
        });
    }

    [Serializable] private class JoinRoomReq { public string client_id; }
    [Serializable] public class JoinRoomResp { }

    public IEnumerator JoinRoomCo(string clientId, ulong roomId, Action<JoinRoomResp> onSuccess)
    {
        var url = $"{_url}/join_room/{roomId}";
        var body = JsonUtility.ToJson(new JoinRoomReq { client_id = clientId });

        yield return SendHttp(url, body, text =>
        {
            var resp = JsonUtility.FromJson<JoinRoomResp>(text);
            Debug.Log($"[HTTP] Joined room {roomId}");
            onSuccess(resp);
        });
    }

    [Serializable] private class LeaveRoomReq { public string client_id; }
    [Serializable] public class LeaveRoomResp { }

    public IEnumerator LeaveRoomCo(string clientId, Action<LeaveRoomResp> onSuccess)
    {
        var url = $"{_url}/leave_room";
        var body = JsonUtility.ToJson(new LeaveRoomReq { client_id = clientId });

        yield return SendHttp(url, body, text =>
        {
            var resp = JsonUtility.FromJson<LeaveRoomResp>(text);
            Debug.Log($"[HTTP] Left room {resp}");
            onSuccess(resp);
        });
    }

    private static IEnumerator SendHttp(string url, string body, Action<string> onSuccess)
    {
        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[HTTP] {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
            yield break;
        }

        Debug.Log($"[HTTP] {req.responseCode}: {req.downloadHandler.text}");
        onSuccess(req.downloadHandler.text);
    }
}