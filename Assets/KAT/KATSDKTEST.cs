using System.Collections;
using System.Collections.Generic;

using UnityEngine;

public class KATSDKTEST : MonoBehaviour
{
    private bool printed = false;

    // Update is called once per frame
    void Update()
    {

        int count = KATNativeSDK.DeviceCount();

        if (!printed)
        {
            Debug.Log("========== KAT SDK ==========");
            Debug.Log("Device Count = " + count);

            for (uint i = 0; i < count; i++)
            {
                var device = KATNativeSDK.GetDevicesDesc(i);

                Debug.Log("----------------------------");
                Debug.Log("Index : " + i);
                Debug.Log("Name  : " + device.device);
                Debug.Log("SN    : " + device.serialNumber);
            }

            printed = true;
        }
    }
}
