using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

public class Insta360SdkTextureSource : MonoBehaviour
{
    [Header("Stitched 360 Output")]
    public int outputWidth = 1920;
    public int outputHeight = 960;
    public bool startOnAwake = true;
    public Renderer previewRenderer;

    public RenderTexture OutputTexture => outputTexture;
    public bool IsRunning { get; private set; }

    private Texture2D uploadTexture;
    private RenderTexture outputTexture;
    private byte[] frameBuffer;
    private GCHandle pinnedBuffer;
    private ulong lastFrameNumber;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const string DllName = "Insta360UnityBridge";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Insta360_Start(int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Insta360_Stop();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong Insta360_GetFrameNumber();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Insta360_GetFrameWidth();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Insta360_GetFrameHeight();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Insta360_CopyLatestFrame(IntPtr destination, int capacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Insta360_GetLastError();
#endif

    private IEnumerator Start()
    {
        if (startOnAwake)
        {
            yield return StartCapture();
        }
    }

    public IEnumerator StartCapture()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (IsRunning) yield break;

        outputTexture = new RenderTexture(
            outputWidth, outputHeight, 0, RenderTextureFormat.ARGB32);
        outputTexture.Create();
        uploadTexture = new Texture2D(
            outputWidth, outputHeight, TextureFormat.RGBA32, false, false);
        frameBuffer = new byte[outputWidth * outputHeight * 4];
        pinnedBuffer = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);

        yield return null;
        if (!Insta360_Start(outputWidth, outputHeight))
        {
            string error = Marshal.PtrToStringAnsi(Insta360_GetLastError());
            Debug.LogError($"Insta360 SDK failed to start: {error}");
            ReleaseTextures();
            yield break;
        }

        IsRunning = true;
        if (previewRenderer != null)
            previewRenderer.material.mainTexture = outputTexture;

        Insta360WebRtcPeer peer = GetComponent<Insta360WebRtcPeer>();
        if (peer != null && peer.sourceTexture == null)
            peer.sourceTexture = outputTexture;
#else
        Debug.Log("Insta360 Win64 SDK capture is disabled on Quest; Quest receives WebRTC.");
        yield break;
#endif
    }

    private void Update()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (!IsRunning) return;
        ulong frame = Insta360_GetFrameNumber();
        if (frame == 0 || frame == lastFrameNumber) return;
        if (Insta360_GetFrameWidth() != outputWidth ||
            Insta360_GetFrameHeight() != outputHeight) return;

        int copied = Insta360_CopyLatestFrame(
            pinnedBuffer.AddrOfPinnedObject(), frameBuffer.Length);
        if (copied != frameBuffer.Length) return;

        uploadTexture.LoadRawTextureData(frameBuffer);
        uploadTexture.Apply(false, false);
        Graphics.Blit(uploadTexture, outputTexture);
        lastFrameNumber = frame;
#endif
    }

    public void StopCapture()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (IsRunning) Insta360_Stop();
#endif
        IsRunning = false;
        ReleaseTextures();
    }

    private void ReleaseTextures()
    {
        if (pinnedBuffer.IsAllocated) pinnedBuffer.Free();
        frameBuffer = null;
        if (uploadTexture != null) Destroy(uploadTexture);
        uploadTexture = null;
        if (outputTexture != null)
        {
            outputTexture.Release();
            Destroy(outputTexture);
        }
        outputTexture = null;
    }

    private void OnDestroy()
    {
        StopCapture();
    }
}
