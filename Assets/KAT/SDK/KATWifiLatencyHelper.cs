using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;


public class KATWifiLatencyHelper
{
    static AndroidJavaClass UnityPlayerClz = null;

    static AndroidJavaObject wifiLock = null;

    public  static AndroidJavaObject CurrentActivity()
    {
        if (Application.isEditor)
        {
            return null;
        }

        if (UnityPlayerClz == null)
        {
            UnityPlayerClz = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        }

        return UnityPlayerClz.GetStatic<AndroidJavaObject>("currentActivity");
    }


    public static void OnEnableWifiLowLatencyTest()
    {
        if (Application.isEditor)
        {
            return;
        }

        var wifiManager = CurrentActivity().Call<AndroidJavaObject>("getSystemService","wifi");
        wifiLock = wifiManager.Call<AndroidJavaObject>("createWifiLock",4, null);
        wifiLock.Call("acquire");
    }

    public static void OnDisableWifiLowLatencyTest()
    {
        if (Application.isEditor)
        {
            return;
        }

        if (wifiLock != null)
        {
            wifiLock.Call("release");
            wifiLock = null;
        }
    }
}
