
using UnityEngine;

public class katwalk : MonoBehaviour
{
    float timer = 0f;

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < 0.2f) return;
        timer = 0f;

        var data = KATNativeSDK.GetWalkStatus();

        Debug.Log(
            "Connected: " + data.connected +
            " | Device: " + data.deviceName +
            " | BodyAngle: " + data.bodyRotationRaw.eulerAngles.y +
            " | MoveSpeed: " + data.moveSpeed +
            " | MoveSpeedMagnitude: " + data.moveSpeed.magnitude
        );
    }
}