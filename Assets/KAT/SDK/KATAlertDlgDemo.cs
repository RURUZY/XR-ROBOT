using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class KATAlertDlgDemo : MonoBehaviour
{
    // Start is called before the first frame update
    public Button btnY;
    public Button btnN;

    public GameObject waittingFrame;
    public GameObject mainFrame;

    protected string _sn;

    float active_time = 0;

    void Start()
    {
        btnY.onClick.RemoveAllListeners();
        btnN.onClick.RemoveAllListeners();

        btnY.onClick.AddListener(() =>
        {
            KATNativeSDK.ForceConnect(_sn);
            StartCoroutine(delayHide(2.0f));
        });

        btnN.onClick.AddListener(() =>
        {
            Application.Quit();
            Hide();
        });
    }

    IEnumerator delayHide(float delay)
    {
        waittingFrame.SetActive(true);
        mainFrame.SetActive(false);
        yield return new WaitForSeconds(delay);
        Hide();
    }

    public void Show(string sn)
    {
        if (gameObject.activeSelf && sn == _sn)
            return;
        
        _sn = sn;
        gameObject.SetActive(true);
        mainFrame.SetActive(true);
        waittingFrame.SetActive(false);
        active_time = Time.realtimeSinceStartup;
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    public string GetSN()
    {
        return _sn;
    }
}
