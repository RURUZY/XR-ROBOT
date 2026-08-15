using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

public class TreadmillExtensionsTest : MonoBehaviour
{
    [Range(0.5f, 5.0f)]
    public float lerpSpeed = 1.0f;

    public Slider vibrate_led_power_slider;
    public Text vibrate_led_power_label;

    private float tmpSpeed = 0.0f;

    bool atten = false;

    public KATAlertDlgDemo alertWnd;
    public KATAlertDlgDemo alertWndVR;

    protected string _selectedSN;

    private void Start()
    {
        KATNativeSDK.SetOnDeviceLostHandler((_sn,message,reason) => 
        {
            Debug.Log("Unity: DeviceLostCallback for:" + _sn + " message:" + message + " reason" + reason);
            //if (XRSettings.isDeviceActive && alertWndVR)

            var sn = _sn;
            MainThreadDispatcher.Instance().Enqueue(() => 
            {
                _selectedSN = sn;
                /*
                if (KATXREnvHelper.CheckXRReadyStatus() && alertWndVR)
                {
                    alertWndVR.Show(sn);
                }
                else
                {
                    alertWnd.Show(sn);
                }
                */
            });
        });
    }

    KATAlertDlgDemo GetAlertWnd()
    {
        if (KATXREnvHelper.CheckXRReadyStatus() && alertWndVR)
        {
           return alertWndVR;
        }
        else
        {
            return alertWnd;
        }
    }

    void OnGUI()
    {
        if (vibrate_led_power_label == null || vibrate_led_power_slider == null)
            return;

        vibrate_led_power_label.text = "Power: " + vibrate_led_power_slider.value;
    }

    public void UIVibrateTest()
    {
        var power = vibrate_led_power_slider.value;
        KATNativeSDK.KATExtension.VibrateInSeconds(power,1.0f);
    }

    public void UILEDTest()
    {
        var power = vibrate_led_power_slider.value;
        KATNativeSDK.KATExtension.LEDInSeconds(power, 1.0f);
    }

    public void UICalibrate()
    {
        KATXRWalker.force_calibrate = true;
    }

    // Update is called once per frame
    void Update()
    {
        //Press R to reload scene
        if (Input.GetKeyUp(KeyCode.R))
        {
            SceneManager.LoadScene(0);
        }

        //Press and Release L Key to bright LED Once
        if (Input.GetKeyUp(KeyCode.L))
        {
            KATNativeSDK.KATExtension.LEDOnce(1.0f);
        }

        //Press and Release L Key to vibrate once
        if (Input.GetKeyUp(KeyCode.V))
        {
            KATNativeSDK.KATExtension.VibrateOnce(1.0f);
        }

        //Press J to let LED breath once
        if (Input.GetKey(KeyCode.J))
        {
            tmpSpeed += Time.deltaTime / lerpSpeed;
            if (tmpSpeed > 1.0f)
            {
                tmpSpeed = 1.0f;
            }
            KATNativeSDK.KATExtension.LEDConst(tmpSpeed);
            atten = true;
        }
        else
        {
            if (atten)
            {
                tmpSpeed -= Time.deltaTime / lerpSpeed;
                if (tmpSpeed < 0.0f)
                {
                    tmpSpeed = 0.0f;
                    atten = false;
                }
                KATNativeSDK.KATExtension.LEDConst(tmpSpeed);
            }

        }

    }

    void LateUpdate()
    {
        var wnd = GetAlertWnd();
        var deviceCount = KATNativeSDK.DeviceCount();
        if(deviceCount == 0 || string.IsNullOrEmpty(_selectedSN))
        {
            wnd.Hide();
            return;
        }

        for(var i = 0;i< deviceCount;i++)
        {
            var desc = KATNativeSDK.GetDevicesDesc((uint)i);
            if(desc.serialNumber == _selectedSN)
            {
                if(!desc.isBusy)
                {
                    wnd.Hide();
                    break;
                }
                else
                {
                    wnd.Show(_selectedSN);
                }
            }
        }
    }
}
