using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

[RequireComponent(typeof(VideoPlayer))]
public class Insta360StreamReader : MonoBehaviour
{
    [Header("Stream Source")]
    [Tooltip("RTSP, HTTP, or other URL that exposes a video stream.")]
    public string streamUrl = "";

    [Tooltip("Whether to start playback automatically when the scene starts.")]
    public bool playOnAwake = true;

    [Header("Display")]
    [Tooltip("Optional: display the stream on a RawImage UI element.")]
    public RawImage targetRawImage;

    [Tooltip("Optional: display the stream on a 3D object renderer.")]
    public Renderer targetRenderer;

    [Tooltip("If true, the script will create a RenderTexture automatically.")]
    public bool autoCreateRenderTexture = true;

    public RenderTexture renderTexture;

    private VideoPlayer videoPlayer;
    private bool isInitialized;

    private void Awake()
    {
        InitializeVideoPlayer();
    }

    private void Start()
    {
        if (playOnAwake)
        {
            PlayStream();
        }
    }

    private void InitializeVideoPlayer()
    {
        videoPlayer = GetComponent<VideoPlayer>();
        if (videoPlayer == null)
        {
            videoPlayer = gameObject.AddComponent<VideoPlayer>();
        }

        videoPlayer.source = VideoSource.Url;
        videoPlayer.playOnAwake = playOnAwake;
        videoPlayer.waitForFirstFrame = true;
        videoPlayer.isLooping = true;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        videoPlayer.errorReceived += OnVideoPlayerError;
        videoPlayer.prepareCompleted += OnPrepareCompleted;

        if (autoCreateRenderTexture || renderTexture == null)
        {
            CreateRenderTexture();
        }

        videoPlayer.targetTexture = renderTexture;

        if (targetRawImage != null && renderTexture != null)
        {
            targetRawImage.texture = renderTexture;
        }

        if (targetRenderer != null && renderTexture != null)
        {
            Material mat = targetRenderer.material;
            if (mat == null)
            {
                mat = new Material(Shader.Find("Unlit/Texture"));
            }
            else if (mat.shader == null || mat.shader.name != "Unlit/Texture")
            {
                mat = new Material(Shader.Find("Unlit/Texture"));
            }

            mat.mainTexture = renderTexture;
            targetRenderer.material = mat;
        }

        isInitialized = true;
    }

    private void CreateRenderTexture()
    {
        if (renderTexture != null)
        {
            return;
        }

        renderTexture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        renderTexture.Create();
    }

    public void PlayStream(string url)
    {
        streamUrl = url;
        PlayStream();
    }

    public void PlayStream()
    {
        if (!isInitialized)
        {
            InitializeVideoPlayer();
        }

        if (string.IsNullOrEmpty(streamUrl))
        {
            Debug.LogWarning("Insta360StreamReader: streamUrl is empty. Please assign a valid RTSP/HTTP URL.");
            return;
        }

        videoPlayer.url = streamUrl;
        videoPlayer.Play();
        Debug.Log($"Trying to play stream: {streamUrl}");
    }

    public void StopStream()
    {
        if (videoPlayer != null)
        {
            videoPlayer.Stop();
        }
    }

    private void OnPrepareCompleted(VideoPlayer source)
    {
        Debug.Log("Video stream prepared successfully.");
    }

    private void OnVideoPlayerError(VideoPlayer source, string message)
    {
        Debug.LogError($"Video stream error: {message}");
    }
}
