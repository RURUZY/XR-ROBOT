// Standalone lifecycle test doubles. Never imported into Unity and never used for real capture.
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
namespace UnityEngine {
 public static class Application { public static string dataPath; }
 public struct Vector3 { public float x,y,z; }
 public struct Quaternion { public float x,y,z,w; }
 public static class Debug {
  public static void Log(object o) { Console.WriteLine(o); }
  public static void LogWarning(object o) { Console.WriteLine(o); }
  public static void LogError(object o) { throw new Exception(o.ToString()); }
 }
 public static class JsonUtility {
  public static string ToJson(object o, bool pretty) { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(o); }
 }
}
namespace UnityEditor {
 public class InitializeOnLoadAttribute : Attribute {}
 public class MenuItem : Attribute { public MenuItem(string s) {} }
 public enum PlayModeStateChange { EnteredPlayMode, ExitingPlayMode }
 public static class EditorApplication {
  public static bool isPlaying, isPaused;
  public static event Action<PlayModeStateChange> playModeStateChanged;
  public static event Action update, quitting;
  public static Action delayCall;
  public static void Play() { isPlaying=true; playModeStateChanged(PlayModeStateChange.EnteredPlayMode); }
  public static void Stop() { playModeStateChanged(PlayModeStateChange.ExitingPlayMode); isPlaying=false; }
  public static void Tick() { Thread.Sleep(15); update(); }
 }
 public static class AssemblyReloadEvents { public static event Action beforeAssemblyReload; }
 public static class EditorUtility { public static void RevealInFinder(string s) {} }
}
public static class KATNativeSDK {
 public struct Desc { public string device, serialNumber; public bool isBusy; public int pid, vid, deviceType, deviceSource; }
 public struct State { public bool btnPressed, isBatteryCharging; public float batteryLevel; public byte firmwareVersion; }
 public struct TreadMillData {
  public string deviceName; public bool connected; public double lastUpdateTimePoint;
  public UnityEngine.Vector3 moveSpeed; public UnityEngine.Quaternion bodyRotationRaw; public State[] deviceDatas;
 }
 public static int reads, count=1; public static bool failCalibration;
 public static int DeviceCount() { return count; }
 public static Desc GetDevicesDesc(uint i) { return new Desc { device="TEST,\"ONLY\"", serialNumber="TEST",deviceType=1 }; }
 public static double GetLastCalibratedTimeEscaped() { if(failCalibration) throw new Exception("test unsupported"); return 3; }
 public static TreadMillData GetWalkStatus(string sn) {
  reads++; return new TreadMillData { deviceName="TEST,\"ONLY\"",connected=count>0,lastUpdateTimePoint=42,
   moveSpeed=new UnityEngine.Vector3{x=3,z=4},bodyRotationRaw=new UnityEngine.Quaternion{w=1},deviceDatas=new State[3] };
 }
}
class LifecycleCheck {
 static void Main(string[] args) {
  UnityEngine.Application.dataPath=Path.GetFullPath(args[0]);
  RuntimeHelpers.RunClassConstructor(typeof(KatPlayCapture).TypeHandle);
  UnityEditor.EditorApplication.Play();
  UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Tick();
  UnityEditor.EditorApplication.isPaused=true; int before=KATNativeSDK.reads;
  UnityEditor.EditorApplication.Tick(); if(KATNativeSDK.reads!=before)throw new Exception("Pause failed");
  UnityEditor.EditorApplication.isPaused=false;
  UnityEditor.EditorApplication.Stop();
  UnityEditor.EditorApplication.Tick(); if(KATNativeSDK.reads!=before)throw new Exception("Stop failed");
  KATNativeSDK.count=0; KATNativeSDK.failCalibration=true;
  UnityEditor.EditorApplication.Play(); UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Stop();
  Console.WriteLine("Lifecycle checks passed; test output contains SYNTHETIC DATA ONLY.");
 }
}
