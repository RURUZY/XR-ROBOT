using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;


/// <summary>
/// Unity Warpper for KAT Native SDK
/// </summary>
public class KATNativeSDK
{
    const int packAlign = 1;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    public const string sdk_warpper_lib = "KATSDKWarpper.dll";
#else
	public const string sdk_warpper_lib = "libKATSDKWarpper.so";
#endif

    protected static NativeLibHandler sdkLoader
	{
		get 
		{
			var r = NativeLibHandler.LoadNativeLib(sdk_warpper_lib);

			r.onLibUnload = (fname) => 
			{
				UnloadSDKLibrary();
			};

			return r;
		}
	}

	/// <summary>
	/// Description of KAT Devices
	/// </summary>
	/// 
	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	public struct DeviceDescription
	{
		//Device Name
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 63)]
		public string device;

        [MarshalAs(UnmanagedType.I1)]
        public bool isBusy;

		//Device Serial Number
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string serialNumber;

		//Device PID
		public int pid;

		//Device VID
		public int vid;

		//Device Type
		//0. Err 1. Tread Mill 2. Tracker 
		public short deviceType;
		public short deviceSource;
        int hidUsage;
    };

	/// <summary>
	/// Device Status Data
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct DeviceData
	{
		//Is Calibration Button Pressed?
		[MarshalAs(UnmanagedType.I1)]
		public bool btnPressed;
		[MarshalAs(UnmanagedType.I1)]
		//Is Battery Charging?
		public bool isBatteryCharging;
		//Battery Used
		public float batteryLevel;
		[MarshalAs(UnmanagedType.I1)]
		public byte firmwareVersion;
	};

	/// <summary>
	/// TreadMill Device Data
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	public struct TreadMillData
	{
		//Device Name
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string deviceName;
		[MarshalAs(UnmanagedType.I1)]
		//Is Device Connected
		public bool connected;
		//Last Update Time
		public double lastUpdateTimePoint;

		//Body Rotation(Quaternion), for treadmill it will cause GL
		public Quaternion bodyRotationRaw;

		//Target Move Speed With Direction
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
		public Vector3 moveSpeed;

		//Sensor Device Datas
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
		public DeviceData[] deviceDatas;

		//Extra Data of TreadMill
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
		public byte[] extraData;
	};


	delegate int device_count_def();
	public static int DeviceCount()
	{
		return (int)sdkLoader.GetFunction<device_count_def>("DeviceCount").DynamicInvoke();
	}

	delegate DeviceDescription get_device_desc_def(uint index);
	public static DeviceDescription GetDevicesDesc(uint index)
	{
		return (DeviceDescription)sdkLoader.GetFunction<get_device_desc_def>("GetDevicesDesc").DynamicInvoke(index);
	}

	delegate double get_last_calibrated_time_escaped_def();
	public static double GetLastCalibratedTimeEscaped()
	{
		return (double)sdkLoader.GetFunction<get_last_calibrated_time_escaped_def>("GetLastCalibratedTimeEscaped").DynamicInvoke();
	}

	delegate TreadMillData get_walk_status_def(string sn);
	public static TreadMillData GetWalkStatus(string sn = "")
	{
		return (TreadMillData)sdkLoader.GetFunction<get_walk_status_def>("GetWalkStatus").DynamicInvoke(sn);
	}

	delegate void unload_sdk_library_def();
	public static void UnloadSDKLibrary()
	{
		sdkLoader.GetFunction<unload_sdk_library_def>("UnloadSDKLibrary").DynamicInvoke();
    }

	delegate void force_reconnect_def(string sn);
	public static void ForceConnect(string sn)
	{
        sdkLoader.GetFunction<force_reconnect_def>("ForceConnect").DynamicInvoke(sn);
    }

    //KAT Extensions, Only for WalkCoord2 and later device
    public class KATExtension
	{
		//KAT Extensions, amplitude: 0(close) - 1.0(max)
		delegate void vibrate_const_def(float amplitude);

		public static void VibrateConst(float amplitude)
		{
			sdkLoader.GetFunction<vibrate_const_def>("VibrateConst").DynamicInvoke(amplitude);
		}

		delegate void LEDConst_def(float amplitude);
		public static  void LEDConst(float amplitude)
		{
			sdkLoader.GetFunction<LEDConst_def>("LEDConst").DynamicInvoke(amplitude);
		}

		//Vibrate in duration
		delegate void vibrate_in_seconds_def(float amplitude, float duration);
		public static void VibrateInSeconds(float amplitude, float duration)
		{
			sdkLoader.GetFunction<vibrate_in_seconds_def>("VibrateInSeconds").DynamicInvoke(amplitude, duration);
		}

		//Vibrate once, simulate a "Click" like function
		delegate void vibrate_once_def(float amplitude);
		public static void VibrateOnce(float amplitude)
		{
            sdkLoader.GetFunction<vibrate_once_def>("VibrateOnce").DynamicInvoke(amplitude);
        }
		

		//Vibrate with a frequency in duration
		delegate void vibrate_for_def(float duration, float amplitude);
		public static void VibrateFor(float duration, float amplitude)
		{
            sdkLoader.GetFunction<vibrate_for_def>("VibrateFor").DynamicInvoke(duration, amplitude);
        }

		//Lighting LED in Seconds
		delegate void LED_in_seconds_def(float amplitude, float duration);
		public static void LEDInSeconds(float amplitude, float duration)
		{
            sdkLoader.GetFunction<LED_in_seconds_def>("LEDInSeconds").DynamicInvoke(amplitude, duration);
        }

		//Lighting once
		delegate void LED_once_def(float amplitude);
		public static void LEDOnce(float amplitude)
		{
            sdkLoader.GetFunction<LED_once_def>("LEDOnce").DynamicInvoke(amplitude);
        }


		//Vibrate with a frequency in duration
		delegate void LED_for_def(float duration, float frequency, float amplitude);
		public static void LEDFor(float duration, float frequency, float amplitude)
		{
            sdkLoader.GetFunction<LED_for_def>("LEDFor").DynamicInvoke(duration, frequency, amplitude);
        }
	}

    private delegate void device_lost_callback_def(string sn, string message, int resason);
    private static Action<string, string, int> globalOnDeviceLostHandler;

    [AOT.MonoPInvokeCallback(typeof(device_lost_callback_def))]
    private static void __OnDeviceLostCallback(string sn, string message, int resason)
    {
        globalOnDeviceLostHandler?.Invoke(sn, message, resason);
    }

	private delegate void SetDeviceLostCallback_def(device_lost_callback_def deviceLostCallback);
    public static void SetOnDeviceLostHandler(Action<string, string, int> handler)
    {	
        if (handler == null)
        {
            sdkLoader.GetFunction<SetDeviceLostCallback_def>("SetDeviceLostCallback").Invoke(null);
            return;
        }

        globalOnDeviceLostHandler = handler;
        sdkLoader.GetFunction<SetDeviceLostCallback_def>("SetDeviceLostCallback").Invoke(__OnDeviceLostCallback);
    }
}
