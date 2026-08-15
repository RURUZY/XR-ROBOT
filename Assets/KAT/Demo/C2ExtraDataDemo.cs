using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// This demo shows how to get KAT Walk C2 extra info, if you interested in other devices extra info, please contact our tech support
/// </summary>

public class C2ExtraDataDemo : MonoBehaviour
{
    enum MotionType:int
    {
         MOTION_STATIC = 0,
         MOTION_SKATING = 1,
         MOTION_KEEP_SKATING = 2,
         MOTION_MICROACTION = 3,
         MOTION_MOVE = 4
    }

    public Text infoText;

    int lastSecond = 0;

    float distance = 0;
    float averageSpeed = 0;

    KATNativeSDK.TreadMillData currentFrameData = new KATNativeSDK.TreadMillData();

    void FixedUpdate()
    {
        currentFrameData = KATNativeSDK.GetWalkStatus();
        var str = "Device:" + currentFrameData.deviceName + "\n";
        str += "    Connected:" + currentFrameData.connected + "\n";
        str += "    Body Rotation:" + currentFrameData.bodyRotationRaw.ToString("f4") + "\n";
        str += "    Speed:" + currentFrameData.moveSpeed.ToString("f4") + "\n";

        distance += currentFrameData.moveSpeed.z * Time.fixedDeltaTime;

        var second = (int)Time.timeSinceLevelLoad;
        if(second >= lastSecond + 2)
        {
            lastSecond = second;
            averageSpeed = distance * 0.5f;
            distance = 0;
        }

        if(averageSpeed < 0.1f)
        {
            averageSpeed = currentFrameData.moveSpeed.z;
        }

        str += "    Speed:" + averageSpeed.ToString("f4") + " meter/second\n";
        
        //Display WalkC2 Extra Data
        if(currentFrameData.deviceName.Contains("Coord2"))
        {
            str += "Battery:\n" + "    H: " + currentFrameData.deviceDatas[0].batteryLevel + ", " ;
            str += "L:" + currentFrameData.deviceDatas[1].batteryLevel + ", ";
            str += "R:" + currentFrameData.deviceDatas[2].batteryLevel + "\n" ;

            var info = WalkC2ExtraData.GetExtraInfoC2(currentFrameData);
            str += "Extra Data:" + "\n";

            str += "    Motion Type:" + (MotionType)info.motionType + "\n";
            str += "    Left Foot OnGround:" + info.isLeftGround + "\n";
            str += "    Right Foot OnGround:" + info.isRightGround + "\n";
            str += "    Left Foot Static:" + info.isLeftStatic + "\n";
            str += "    Right Foot Static:" + info.isRightStatic + "\n";

            str += "    Skating Speed:" + info.skatingSpeed.ToString("F4") + "\n";   
            str += "    lFootSpeed:" + info.lFootSpeed.ToString("F4") + "\n";
            str += "    rFootSpeed:" + info.rFootSpeed.ToString("F4") + "\n";
        }
        

        infoText.text = str;
    }



    void Update()
    {
       
    }
}
