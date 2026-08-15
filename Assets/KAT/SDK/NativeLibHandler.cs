using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public class NativeLibHandler : MonoBehaviour
{
    [NonSerialized]
    public string lib_name = "";

    IntPtr handle = IntPtr.Zero;
    Dictionary<string, IntPtr> functionPointers = new Dictionary<string, IntPtr>();

#if UNITY_ANDROID && !UNITY_EDITOR_WIN
    [DllImport("kdroidhelper")]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kdroidhelper")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kdroidhelper")]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);

#else
    [DllImport("kernel32")]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32")]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procedureName);
#endif

    protected static Dictionary<string, NativeLibHandler> loaderGOList = new Dictionary<string, NativeLibHandler>();

    public Action<string> onLibLoad = null;
    public Action<string> onLibLoaded = null;
    public Action<string> onLibLoadError = null;
    public Action<string,string> onFunctionLoadError = null;
    public Action<string> onLibUnload = null;
    public Action<string> onLibUnloaded = null;

    public static NativeLibHandler LoadNativeLib(string fileName)
    {
        if (!loaderGOList.ContainsKey(fileName))
        {
            var go = new GameObject("loader_" + fileName);
            var cmp = go.AddComponent<NativeLibHandler>();
            cmp.lib_name = fileName;
            DontDestroyOnLoad(cmp);
            loaderGOList[fileName] = cmp;

#if UNITY_ANDROID && !UNITY_EDITOR_WIN
            using (AndroidJavaClass javaSystem = new AndroidJavaClass("java.lang.System"))
            {
                var fname = fileName.Replace("lib", "");
                fname = fname.Replace(".so", "");
                javaSystem.CallStatic("loadLibrary", fname);
            }
#endif
        }

        return loaderGOList[fileName];
    }

    protected IntPtr GetFunc(string function)
    {
        if (handle == IntPtr.Zero)
        {
#if UNITY_EDITOR_WIN
            var sdkFileName = Path.Combine(Application.dataPath, "KAT/SDK/Plugin/Win64/" + lib_name);
#elif UNITY_STANDALONE_WIN
            var sdkFileName = Path.Combine(Application.dataPath, "Plugins/x86_64/" + lib_name);
#else
            var sdkFileName = lib_name; 
#endif

            Debug.Log("Going to LoadLibrary:" + sdkFileName);

            onLibLoad?.Invoke(sdkFileName);

            handle = LoadLibrary(sdkFileName);
            if (handle == IntPtr.Zero)
            {
                onLibLoadError?.Invoke(sdkFileName);
                throw new Exception("Failed to load library " + sdkFileName);
            }

            Debug.Log("Library Loaded, Handle:0x" + handle.ToString("x"));

            onLibLoaded?.Invoke(sdkFileName);
        }

        if (!functionPointers.ContainsKey(function))
        {
            IntPtr pointer = GetProcAddress(handle, function);
            if (pointer == IntPtr.Zero)
            {
                onFunctionLoadError?.Invoke(lib_name, function);
                throw new Exception("Failed to get function pointer " + function);
            }
            Debug.Log("Function " + function + " Getted @ 0x" + pointer.ToString("x"));
            functionPointers[function] = pointer;
        }

        return functionPointers[function];
    }
    
    public T GetFunction<T>(string function)
    {
        var f = GetFunc(function);

        var d = Marshal.GetDelegateForFunctionPointer<T>(f);

        if(d == null)
        {
            throw new Exception($"{function} is not a function");
        }

        return d;
    }
   
    protected void Release()
    {
        if (handle != IntPtr.Zero)
        {
            foreach (System.Diagnostics.ProcessModule mod in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (mod.ModuleName.EndsWith(lib_name))
                {
                    onLibUnload?.Invoke(lib_name);
                    Debug.Log("Free Library:" + lib_name);
                    FreeLibrary(mod.BaseAddress);
                    onLibUnloaded?.Invoke(lib_name);
                }
            }

            handle = IntPtr.Zero;
            functionPointers.Clear();
        }
    }

    void OnDestroy()
    {
        Release();
    }
}
