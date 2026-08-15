using System.Collections;
using System.Collections.Generic;

using UnityEngine;

public class katsdkstatustest : MonoBehaviour
{
    void Update()
    {
        var data = KATNativeSDK.GetWalkStatus();

        Debug.Log(
            "Connected: " + data.connected +
            " | Device: " + data.deviceName +
            " | MoveSpeed: " + data.moveSpeed +
            " | BodyRot: " + data.bodyRotationRaw.eulerAngles
        );
    }
}
