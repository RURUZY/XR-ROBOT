using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class Insta360WebRtcPeer : MonoBehaviour
{
    public enum PeerRole { Sender, Receiver }

    [Header("Role")]
    public PeerRole role = PeerRole.Receiver;
    [Tooltip("Windows Editor/Standalone becomes Sender; Android/Quest becomes Receiver.")]
    public bool autoSelectRoleByPlatform = true;
    public bool startOnAwake = true;

    [Header("Signaling")]
    [Tooltip("Use the Windows computer's LAN IP on Quest, never localhost.")]
    public string signalingServer = "http://192.168.1.100:8080";
    public string room = "insta360-x5";
    [Range(0.1f, 2f)] public float pollInterval = 0.25f;
    [Tooltip("Windows sender hosts signaling inside Unity. No PowerShell is needed.")]
    public bool hostSignalingOnSender = true;
    public int embeddedSignalingPort = 8080;

    [Header("Sender")]
    [Tooltip("The stitched equirectangular texture produced by the Insta360 SDK.")]
    public RenderTexture sourceTexture;
    [Tooltip("Optional test source when sourceTexture is empty.")]
    public Camera sourceCamera;
    public Vector2Int cameraCaptureSize = new Vector2Int(1920, 960);

    [Header("Receiver")]
    public Renderer targetRenderer;
    public RawImage targetRawImage;

    [Header("Connection")]
    [Tooltip("Leave empty for same-LAN testing. Add STUN/TURN URLs for routed networks.")]
    public string[] iceServerUrls = Array.Empty<string>();

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack sendTrack;
    private VideoStreamTrack receiveTrack;
    private Coroutine webRtcUpdateCoroutine;
    private Coroutine pollingCoroutine;
    private readonly Queue<SignalMessage> pendingCandidates = new Queue<SignalMessage>();
    private long lastMessageId;
    private bool remoteDescriptionSet;
    private bool stopping;
    private EmbeddedWebRtcSignalingServer embeddedServer;

    [Serializable]
    private class SignalMessage
    {
        public long id;
        public string from;
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }

    [Serializable]
    private class SignalMessageList
    {
        public SignalMessage[] messages;
    }

    private string LocalPeerName => role == PeerRole.Sender ? "sender" : "receiver";

    private void Start()
    {
        if (autoSelectRoleByPlatform)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            role = PeerRole.Receiver;
#else
            role = PeerRole.Sender;
#endif
        }

        if (startOnAwake) StartPeer();
    }

    public void StartPeer()
    {
        if (peerConnection != null) return;

        stopping = false;
        remoteDescriptionSet = false;
        lastMessageId = 0;
        pendingCandidates.Clear();

        if (role == PeerRole.Sender && hostSignalingOnSender)
        {
            embeddedServer = GetComponent<EmbeddedWebRtcSignalingServer>();
            if (embeddedServer == null)
                embeddedServer = gameObject.AddComponent<EmbeddedWebRtcSignalingServer>();
            embeddedServer.port = embeddedSignalingPort;
            embeddedServer.StartServer();
            signalingServer = $"http://127.0.0.1:{embeddedSignalingPort}";
        }

        RTCConfiguration configuration = new RTCConfiguration { iceServers = BuildIceServers() };
        peerConnection = new RTCPeerConnection(ref configuration);
        peerConnection.OnIceCandidate = OnLocalIceCandidate;
        peerConnection.OnIceConnectionChange = state =>
            Debug.Log($"WebRTC {LocalPeerName} ICE state: {state}");
        peerConnection.OnConnectionStateChange = state =>
            Debug.Log($"WebRTC {LocalPeerName} connection state: {state}");

        if (role == PeerRole.Receiver) peerConnection.OnTrack = OnRemoteTrack;

        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
        pollingCoroutine = StartCoroutine(PollSignals());
        if (role == PeerRole.Sender) StartCoroutine(CreateAndSendOffer());
    }

    private RTCIceServer[] BuildIceServers()
    {
        if (iceServerUrls == null || iceServerUrls.Length == 0)
            return Array.Empty<RTCIceServer>();
        return new[] { new RTCIceServer { urls = iceServerUrls } };
    }

    private IEnumerator CreateAndSendOffer()
    {
        float sourceDeadline = Time.realtimeSinceStartup + 30f;
        while (sourceTexture == null && sourceCamera == null &&
               Time.realtimeSinceStartup < sourceDeadline)
        {
            yield return null;
        }

        if (sourceTexture != null)
            sendTrack = new VideoStreamTrack(sourceTexture);
        else if (sourceCamera != null)
            sendTrack = sourceCamera.CaptureStreamTrack(cameraCaptureSize.x, cameraCaptureSize.y);
        else
        {
            Debug.LogError("WebRTC sender needs sourceTexture or sourceCamera.");
            yield break;
        }

        MediaStream stream = new MediaStream();
        stream.AddTrack(sendTrack);
        peerConnection.AddTrack(sendTrack, stream);

        RTCOfferAnswerOptions options = new RTCOfferAnswerOptions { iceRestart = false };
        RTCSessionDescriptionAsyncOperation offerOperation = peerConnection.CreateOffer(ref options);
        yield return offerOperation;
        if (offerOperation.IsError)
        {
            Debug.LogError($"WebRTC CreateOffer failed: {offerOperation.Error.message}");
            yield break;
        }

        RTCSessionDescription offer = offerOperation.Desc;
        RTCSetSessionDescriptionAsyncOperation localOperation =
            peerConnection.SetLocalDescription(ref offer);
        yield return localOperation;
        if (localOperation.IsError)
        {
            Debug.LogError($"WebRTC SetLocalDescription failed: {localOperation.Error.message}");
            yield break;
        }

        yield return SendSignal(new SignalMessage
        {
            from = LocalPeerName, type = "offer", sdp = offer.sdp
        });
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;
        StartCoroutine(SendSignal(new SignalMessage
        {
            from = LocalPeerName,
            type = "candidate",
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex ?? 0
        }));
    }

    private void OnRemoteTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack)) return;
        receiveTrack = videoTrack;
        receiveTrack.OnVideoReceived += texture =>
        {
            if (targetRenderer != null) targetRenderer.material.mainTexture = texture;
            if (targetRawImage != null) targetRawImage.texture = texture;
        };
    }

    private IEnumerator PollSignals()
    {
        while (!stopping)
        {
            string url = $"{signalingServer.TrimEnd('/')}/poll" +
                $"?room={UnityWebRequest.EscapeURL(room)}&peer={LocalPeerName}&after={lastMessageId}";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    SignalMessageList list =
                        JsonUtility.FromJson<SignalMessageList>(request.downloadHandler.text);
                    if (list != null && list.messages != null)
                    {
                        foreach (SignalMessage message in list.messages)
                        {
                            lastMessageId = Math.Max(lastMessageId, message.id);
                            yield return HandleSignal(message);
                        }
                    }
                }
                else Debug.LogWarning($"WebRTC signaling poll failed: {request.error}");
            }
            yield return new WaitForSeconds(pollInterval);
        }
    }

    private IEnumerator HandleSignal(SignalMessage message)
    {
        if (message == null) yield break;

        if (message.type == "offer" && role == PeerRole.Receiver)
        {
            RTCSessionDescription offer =
                new RTCSessionDescription { type = RTCSdpType.Offer, sdp = message.sdp };
            RTCSetSessionDescriptionAsyncOperation remoteOperation =
                peerConnection.SetRemoteDescription(ref offer);
            yield return remoteOperation;
            if (remoteOperation.IsError)
            {
                Debug.LogError($"WebRTC remote offer failed: {remoteOperation.Error.message}");
                yield break;
            }
            remoteDescriptionSet = true;
            FlushCandidates();

            RTCSessionDescriptionAsyncOperation answerOperation = peerConnection.CreateAnswer();
            yield return answerOperation;
            if (answerOperation.IsError)
            {
                Debug.LogError($"WebRTC CreateAnswer failed: {answerOperation.Error.message}");
                yield break;
            }

            RTCSessionDescription answer = answerOperation.Desc;
            RTCSetSessionDescriptionAsyncOperation localOperation =
                peerConnection.SetLocalDescription(ref answer);
            yield return localOperation;
            if (localOperation.IsError)
            {
                Debug.LogError($"WebRTC local answer failed: {localOperation.Error.message}");
                yield break;
            }
            yield return SendSignal(new SignalMessage
            {
                from = LocalPeerName, type = "answer", sdp = answer.sdp
            });
        }
        else if (message.type == "answer" && role == PeerRole.Sender)
        {
            RTCSessionDescription answer =
                new RTCSessionDescription { type = RTCSdpType.Answer, sdp = message.sdp };
            RTCSetSessionDescriptionAsyncOperation remoteOperation =
                peerConnection.SetRemoteDescription(ref answer);
            yield return remoteOperation;
            if (remoteOperation.IsError)
            {
                Debug.LogError($"WebRTC remote answer failed: {remoteOperation.Error.message}");
                yield break;
            }
            remoteDescriptionSet = true;
            FlushCandidates();
        }
        else if (message.type == "candidate")
        {
            if (remoteDescriptionSet) AddRemoteCandidate(message);
            else pendingCandidates.Enqueue(message);
        }
    }

    private void FlushCandidates()
    {
        while (pendingCandidates.Count > 0) AddRemoteCandidate(pendingCandidates.Dequeue());
    }

    private void AddRemoteCandidate(SignalMessage message)
    {
        RTCIceCandidateInit init = new RTCIceCandidateInit
        {
            candidate = message.candidate,
            sdpMid = message.sdpMid,
            sdpMLineIndex = message.sdpMLineIndex
        };
        peerConnection.AddIceCandidate(new RTCIceCandidate(init));
    }

    private IEnumerator SendSignal(SignalMessage message)
    {
        string url = $"{signalingServer.TrimEnd('/')}/signal?room={UnityWebRequest.EscapeURL(room)}";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
        using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 5;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"WebRTC signaling send failed: {request.error}");
        }
    }

    public void StopPeer()
    {
        stopping = true;
        if (pollingCoroutine != null) StopCoroutine(pollingCoroutine);
        if (webRtcUpdateCoroutine != null) StopCoroutine(webRtcUpdateCoroutine);
        pollingCoroutine = null;
        webRtcUpdateCoroutine = null;
        receiveTrack?.Dispose();
        receiveTrack = null;
        sendTrack?.Dispose();
        sendTrack = null;
        peerConnection?.Close();
        peerConnection?.Dispose();
        peerConnection = null;
    }

    private void OnDestroy() => StopPeer();
}
