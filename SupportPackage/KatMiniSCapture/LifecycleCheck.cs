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
  public static string ToJson(object o, bool pretty=false) { return System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { IncludeFields=true, WriteIndented=pretty }); }
 }
}
namespace UnityEditor {
 public class InitializeOnLoadAttribute : Attribute {}
 public class MenuItem : Attribute { public MenuItem(string s, bool validate=false) {} }
 public static class SessionState {
  static readonly System.Collections.Generic.Dictionary<string,bool> values=new System.Collections.Generic.Dictionary<string,bool>();
  public static bool GetBool(string key,bool fallback)=>values.TryGetValue(key,out bool value)?value:fallback;
  public static void SetBool(string key,bool value) { values[key]=value; }
 }
 public static class Menu { public static bool lastChecked; public static void SetChecked(string path,bool value) { lastChecked=value; } }
 public enum PlayModeStateChange { EnteredPlayMode, ExitingPlayMode, EnteredEditMode }
 public static class EditorApplication {
  public static bool isPlaying, isPaused;
  public static bool isPlayingOrWillChangePlaymode=>isPlaying;
  public static event Action<PlayModeStateChange> playModeStateChanged;
  public static event Action update, quitting;
  public static Action delayCall;
  public static void Play() { isPlaying=true; playModeStateChanged(PlayModeStateChange.EnteredPlayMode); }
  public static void Stop() {
   playModeStateChanged(PlayModeStateChange.ExitingPlayMode);
   HuskyControlTrace.Record("publish", "Keyboard", "/cmd_vel", 1, 0f, 0f, false, "DISABLED", true);
   isPlaying=false; playModeStateChanged(PlayModeStateChange.EnteredEditMode);
  }
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
 static object ViewMenu(string method) => typeof(KatPlayCapture).GetMethod(method,
  System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,null);
 static void Main(string[] args) {
  string root=Path.GetFullPath(Path.Combine("SupportPackage/KatMiniSCapture/test-output", "SYNTHETIC_"+Guid.NewGuid().ToString("N")));
  UnityEngine.Application.dataPath=Path.Combine(root, "Assets");
  Directory.CreateDirectory(Path.Combine(UnityEngine.Application.dataPath,"KAT/Editor"));
  File.WriteAllText(Path.Combine(UnityEngine.Application.dataPath,"KAT/Editor/KatPlayCapture.README.md"),"SYNTHETIC TEST ONLY. No robot or SDK connection.");
  var ros=new Unity.Robotics.ROSTCPConnector.ROSConnection();
  UnityEngine.Object.Objects.Add(ros);
  UnityEngine.Object.Objects.Add(new CmdVelPublisher());
  RuntimeHelpers.RunClassConstructor(typeof(KatPlayCapture).TypeHandle);
  if(!(bool)ViewMenu("ValidateViewRecordingForNextPlay") || UnityEditor.Menu.lastChecked)throw new Exception("View recording must default off in Edit mode");
  UnityEditor.EditorApplication.Play();
  if(KatViewFrameCapture.starts!=0)throw new Exception("Default Play must not start view capture");
  if((bool)ViewMenu("ValidateViewRecordingForNextPlay"))throw new Exception("View recording menu must be disabled during Play");
  ViewMenu("ToggleViewRecordingForNextPlay");
  UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Tick();
  HuskyControlTrace.Record("keyboard_input", "Keyboard", "/cmd_vel", 1, 1f, 0f);
  HuskyControlTrace.Record("panorama", "Panorama", "/odometry/filtered", 0,
   true,true,2,"Custom",true,1f,true,0f,123.0,60f,.01f,0f,10f,-50f,70f,0f);
  HuskyControlTrace.Record("view_overlay", "Husky overlay", "/odometry/filtered", 0,
   true,true,-50f,-50f,60.0,60f,0f,0f,false,true);
  HuskyControlTrace.Record("minimap", "Livoscan wrist map", "", 0,
   "AlignWithVideo",true,true,true,.02f,-80f,-50f,0f,30f,-50f,1f,0f,2f);
  HuskyControlTrace.Record("publish", "Keyboard", "/cmd_vel", 1, .2f, 0f, true, "KEYBOARD", true);
  var twist=new RosMessageTypes.Geometry.TwistMsg(); twist.linear.x=.2;
  ros.Deliver("/husky_velocity_controller/cmd_vel", twist);
  var output=new RosMessageTypes.Geometry.TwistStampedMsg(); output.header.stamp.sec=123; output.header.stamp.nanosec=456; output.twist=twist;
  ros.Deliver("/husky_velocity_controller/cmd_vel_out", output);
  ros.Deliver("/odometry/filtered", new RosMessageTypes.Nav.OdometryMsg());
  KatPlayCapture.AddControlMarker("test,\"quoted\" marker");
  UnityEditor.EditorApplication.isPaused=true; int before=KATNativeSDK.reads;
  UnityEditor.EditorApplication.Tick(); if(KATNativeSDK.reads!=before)throw new Exception("Pause failed");
  UnityEditor.EditorApplication.isPaused=false;
  UnityEditor.EditorApplication.Stop();
  if(KatViewFrameCapture.stops!=0)throw new Exception("Default capture must not stop an absent view recorder");
  UnityEditor.EditorApplication.Tick(); if(KATNativeSDK.reads!=before)throw new Exception("Stop failed");
  string first=Directory.GetDirectories(Path.Combine(root,"KATCaptures"))[0];
  foreach(string stage in new[]{"panorama","view_overlay","minimap"})
   if(File.ReadAllLines(Path.Combine(first,"control_"+stage+".csv")).Length!=2)throw new Exception("Missing view stage: "+stage);
  using(var info=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(first,"session.json")))) {
   var value=info.RootElement;
   if(value.GetProperty("viewCaptureRequested").GetBoolean())throw new Exception("Default session unexpectedly requested images");
   if(value.GetProperty("unityPublishRows").GetInt32()!=2)throw new Exception("Exit zero command not captured");
   if(value.GetProperty("chassisInputRows").GetInt32()!=1||value.GetProperty("chassisOutputRows").GetInt32()!=1||value.GetProperty("odometryRows").GetInt32()!=1)throw new Exception("Missing ROS stage");
  }
  long size=new FileInfo(Path.Combine(first,"control_chassis_command.csv")).Length;
  ros.Deliver("/husky_velocity_controller/cmd_vel_out", output);
  if(new FileInfo(Path.Combine(first,"control_chassis_command.csv")).Length!=size)throw new Exception("Post-stop callback wrote to closed capture");
  if(!File.Exists(first+".zip"))throw new Exception("Capture not packaged");
  ViewMenu("ValidateViewRecordingForNextPlay");
  if(UnityEditor.Menu.lastChecked)throw new Exception("Mid-Play menu call must not arm future recording");
  ViewMenu("ToggleViewRecordingForNextPlay");
  if(!(bool)ViewMenu("ValidateViewRecordingForNextPlay") || !UnityEditor.Menu.lastChecked)throw new Exception("Edit-mode opt-in must arm next Play");
  UnityEditor.EditorApplication.Play();
  if(KatViewFrameCapture.starts!=1)throw new Exception("Explicit opt-in did not start view capture");
  ViewMenu("ValidateViewRecordingForNextPlay");
  if(UnityEditor.Menu.lastChecked)throw new Exception("View recording opt-in was not consumed");
  UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Stop();
  if(KatViewFrameCapture.stops!=1)throw new Exception("Opt-in view capture did not finish before packaging");
  string withView=Array.Find(Directory.GetDirectories(Path.Combine(root,"KATCaptures")),path=>path!=first);
  using(var info=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(withView,"session.json"))))
   if(!info.RootElement.GetProperty("viewCaptureRequested").GetBoolean())throw new Exception("Opt-in missing from session metadata");
  KATNativeSDK.count=0; KATNativeSDK.failCalibration=true;
  UnityEditor.EditorApplication.Play(); UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Stop();
  if(KatViewFrameCapture.starts!=1 || KatViewFrameCapture.stops!=1)throw new Exception("One-shot opt-in leaked into later Play");
  if(ros.subscriptions!=3)throw new Exception("Duplicate ROS subscriptions across Play sessions");
  FeedbackProbeLifecycleCheck.Run(root);
  File.WriteAllText(Path.Combine("SupportPackage/KatMiniSCapture/test-output", "latest.txt"),first);
  Console.WriteLine("Lifecycle checks passed; test output contains SYNTHETIC DATA ONLY.");
  Console.WriteLine(first);
 }
}
