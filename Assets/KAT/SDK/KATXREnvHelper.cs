using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;

public class KATXREnvHelper
{
    public static bool CheckXRReadyStatus()
    {
        if (XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null)
        {
            if (XRGeneralSettings.Instance.Manager.isInitializationComplete)
            {
                //Debug.Log("XR is enabled and initialized.");
                return true;
            }
            else
            {
                //Debug.Log("XR is enabled but not yet initialized.");
                return false;
            }
        }
        else
        {
            //Debug.Log("XR is not enabled.");
            return false;
        }
    }
}
