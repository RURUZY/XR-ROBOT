using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Receives the Insta360 H.264 stream from MediaMTX through WHEP.
/// Attach this to a GameObject and assign the inside-out sphere Renderer.
/// </summary>
public sealed class MediaMtxWhepReceiver : MonoBehaviour
{
    [Header("Husky MediaMTX")]
    [Tooltip("WHEP endpoint exposed by the Husky.")]
    public string whepUrl = "http://192.168.2.36:8889/insta360/whep";

    [Header("Video output")]
    [Tooltip("Renderer of the sphere viewed from the inside.")]
    public Renderer targetRenderer;
    [Tooltip("Optional flat preview for initial testing.")]
    public RawImage targetRawImage;
    [Tooltip("Use the unlit, double-sided material required when viewing a sphere from inside.")]
    public bool useInsideSphereMaterial = true;
    [Tooltip("At startup, move the target sphere to the active VR camera's world position.")]
    public bool centerSphereOnMainCamera = true;

    [Header("Startup")]
    public bool startOnAwake = true;
    public bool autoReconnect = true;
    [Min(0.5f)] public float reconnectDelay = 2f;
    [Min(2f)] public float iceGatheringTimeout = 10f;

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack receivedTrack;
    private Coroutine webRtcUpdateCoroutine;
    private Coroutine connectCoroutine;
    private bool stopping;
    private Material runtimeVideoMaterial;

    private void Start()
    {
        if (centerSphereOnMainCamera && targetRenderer != null &&
            Camera.main != null)
        {
            targetRenderer.transform.position = Camera.main.transform.position;
        }

        if (startOnAwake)
            Connect();
    }

    public void Connect()
    {
        if (peerConnection != null)
            return;

        stopping = false;
        connectCoroutine = StartCoroutine(ConnectRoutine());
    }

    private IEnumerator ConnectRoutine()
    {
        RTCConfiguration configuration = default;
        configuration.iceServers = new RTCIceServer[0];

        peerConnection = new RTCPeerConnection(ref configuration);
        peerConnection.OnIceConnectionChange = state =>
            Debug.Log($"Insta360 WHEP ICE: {state}");
        peerConnection.OnConnectionStateChange = state =>
            Debug.Log($"Insta360 WHEP connection: {state}");
        peerConnection.OnTrack = OnRemoteTrack;

        peerConnection.AddTransceiver(
            TrackKind.Video,
            new RTCRtpTransceiverInit
            {
                direction = RTCRtpTransceiverDirection.RecvOnly
            });

        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());

        RTCSessionDescriptionAsyncOperation offerOperation =
            peerConnection.CreateOffer();
        yield return offerOperation;
        if (offerOperation.IsError)
        {
            Fail($"CreateOffer failed: {offerOperation.Error.message}");
            yield break;
        }

        RTCSessionDescription offer = offerOperation.Desc;
        RTCSetSessionDescriptionAsyncOperation localOperation =
            peerConnection.SetLocalDescription(ref offer);
        yield return localOperation;
        if (localOperation.IsError)
        {
            Fail($"SetLocalDescription failed: {localOperation.Error.message}");
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + iceGatheringTimeout;
        while (!stopping &&
               peerConnection.GatheringState != RTCIceGatheringState.Complete &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (stopping || peerConnection == null)
            yield break;

        string localSdp = peerConnection.LocalDescription.sdp;
        using (UnityWebRequest request =
               new UnityWebRequest(whepUrl, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler =
                new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(localSdp));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/sdp");
            request.SetRequestHeader("Accept", "application/sdp");
            request.timeout = 15;

            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail(
                    $"WHEP POST failed ({request.responseCode}): " +
                    $"{request.error}\n{request.downloadHandler.text}");
                yield break;
            }

            RTCSessionDescription answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = request.downloadHandler.text
            };
            RTCSetSessionDescriptionAsyncOperation remoteOperation =
                peerConnection.SetRemoteDescription(ref answer);
            yield return remoteOperation;
            if (remoteOperation.IsError)
            {
                Fail(
                    $"SetRemoteDescription failed: " +
                    remoteOperation.Error.message);
                yield break;
            }
        }

        Debug.Log($"Insta360 WHEP connected to {whepUrl}");
        connectCoroutine = null;
    }

    private void OnRemoteTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack))
            return;

        receivedTrack = videoTrack;
        receivedTrack.OnVideoReceived += OnVideoReceived;
        Debug.Log("Insta360 WebRTC video track received.");
    }

    private void OnVideoReceived(Texture texture)
    {
        if (targetRenderer != null)
        {
            if (useInsideSphereMaterial && runtimeVideoMaterial == null)
            {
                Shader shader = Shader.Find("Insta360/Inside Sphere");
                if (shader != null)
                {
                    runtimeVideoMaterial = new Material(shader);
                    targetRenderer.material = runtimeVideoMaterial;
                }
                else
                {
                    Debug.LogError("Insta360 inside-sphere shader was not found.");
                }
            }

            targetRenderer.material.mainTexture = texture;
        }
        if (targetRawImage != null)
            targetRawImage.texture = texture;
    }

    private void Fail(string message)
    {
        Debug.LogError(message);
        connectCoroutine = null;
        CleanupPeerConnection();

        if (autoReconnect && !stopping)
            connectCoroutine = StartCoroutine(ReconnectAfterDelay());
    }

    private IEnumerator ReconnectAfterDelay()
    {
        yield return new WaitForSecondsRealtime(reconnectDelay);
        connectCoroutine = null;
        if (!stopping)
            Connect();
    }

    private void CleanupPeerConnection()
    {
        if (webRtcUpdateCoroutine != null)
            StopCoroutine(webRtcUpdateCoroutine);
        webRtcUpdateCoroutine = null;

        if (receivedTrack != null)
        {
            receivedTrack.OnVideoReceived -= OnVideoReceived;
            receivedTrack.Dispose();
            receivedTrack = null;
        }

        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
    }

    public void Disconnect()
    {
        stopping = true;

        if (connectCoroutine != null)
            StopCoroutine(connectCoroutine);
        connectCoroutine = null;

        CleanupPeerConnection();

        if (runtimeVideoMaterial != null)
        {
            Destroy(runtimeVideoMaterial);
            runtimeVideoMaterial = null;
        }
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}
