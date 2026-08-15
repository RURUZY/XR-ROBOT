using UnityEngine;

public class WebCamTest : MonoBehaviour
{
    public Renderer targetRenderer;
    WebCamTexture webCamTexture;

    void Start()
    {
        webCamTexture = new WebCamTexture();
        targetRenderer.material.mainTexture = webCamTexture;
        webCamTexture.Play();
    }
}