using System.Collections;
using UnityEngine;
using UnityEngine.XR;

public class LongPressCalibrate : MonoBehaviour
{
    [Range(0.5f, 5.0f)]
    public float longPressTime = 2.0f;//seconds

    public Material testMaterial = null;

    private InputDevice rightHand;
    private InputDevice leftHand;
    private float pressedTime = 0.0f;

    // Start is called before the first frame update
    private void Start()
    {
        rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

    }

    // Update is called once per frame
    void Update()
    {
        if (rightHand == null || leftHand == null)
        {
            return;
        }

        if (GetTrigger(rightHand) && GetTrigger(leftHand))
        {
            pressedTime += Time.deltaTime;
        }
        else
        {
            pressedTime = 0.0f;
        }

        if (pressedTime > longPressTime && KATXRWalker.force_calibrate == false) 
        {
            KATXRWalker.force_calibrate = true;
            KATNativeSDK.KATExtension.VibrateInSeconds(1.0f, 1.0f);
            KATNativeSDK.KATExtension.LEDInSeconds(1.0f, 1.0f);
        }
    }

    void OnApplicationPause(bool pause)
    {
        Debug.Log("OnApplicationPause:" + pause);
        if(!pause)
        {
            //KATNativeSDK.Test.AlertWindowTest(KATWifiLatencyHelper.CurrentActivity().GetRawObject());
        }
    }

    void OnApplicationFocus(bool focus)
    {
        Debug.Log("OnApplicationFocus:" + focus);
        if (focus)
        {
            //KATNativeSDK.Test.AlertWindowTest(KATWifiLatencyHelper.CurrentActivity().GetRawObject());
        }
    }


    private bool GetTrigger(InputDevice device)
    {
        return device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger;
    }
}
