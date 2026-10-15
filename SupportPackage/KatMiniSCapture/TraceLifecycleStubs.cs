// Test doubles for Editor/ROS only. No hardware or network calls.
using System;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine {
 public class Object {
  public static List<Object> Objects=new List<Object>();
  public static void DestroyImmediate(Object o) {}
  public int GetInstanceID()=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
  public static T FindObjectOfType<T>() where T:Object => Objects.OfType<T>().FirstOrDefault();
  public static T[] FindObjectsOfType<T>() where T:Object => Objects.OfType<T>().ToArray();
 }
 public class MonoBehaviour:Object { public bool isActiveAndEnabled=true; public Object gameObject=new Object(); }
 public static class Time { public static float realtimeSinceStartup=>1; }
 public static class GUILayout { public static void Label(string text){} public static bool Button(string text)=>false; }
}
namespace UnityEditor {
 public class EditorWindow { public static T GetWindow<T>(string title) where T:new()=>new T(); }
 public static class EditorGUILayout { public static string TextField(string label,string value)=>value; }
}
public class KatHuskyDirectAngleController:UnityEngine.MonoBehaviour { public string cmdVelTopic="/cmd_vel",odometryTopic="/odometry/filtered"; }
public class CmdVelPublisher:UnityEngine.MonoBehaviour { public string topicName="/cmd_vel"; }
namespace RosMessageTypes.Std {
 public class Stamp { public uint sec=123,nanosec=456; }
 public class Header { public Stamp stamp=new Stamp(); public string frame_id="odom"; }
}
namespace RosMessageTypes.Geometry {
 public class Vector3Msg { public double x,y,z; }
 public class QuaternionMsg { public double x,y,z,w=1; }
 public class TwistMsg { public Vector3Msg linear=new Vector3Msg(),angular=new Vector3Msg(); }
 public class TwistStampedMsg { public RosMessageTypes.Std.Header header=new RosMessageTypes.Std.Header(); public TwistMsg twist=new TwistMsg(); }
 public class PoseMsg { public Vector3Msg position=new Vector3Msg(); public QuaternionMsg orientation=new QuaternionMsg(); }
 public class PoseWithCovarianceMsg { public PoseMsg pose=new PoseMsg(); }
 public class TwistWithCovarianceMsg { public TwistMsg twist=new TwistMsg(); }
}
namespace RosMessageTypes.Nav {
 public class OdometryMsg {
  public RosMessageTypes.Std.Header header=new RosMessageTypes.Std.Header(); public string child_frame_id="base_link";
  public RosMessageTypes.Geometry.PoseWithCovarianceMsg pose=new RosMessageTypes.Geometry.PoseWithCovarianceMsg();
  public RosMessageTypes.Geometry.TwistWithCovarianceMsg twist=new RosMessageTypes.Geometry.TwistWithCovarianceMsg();
 }
}
namespace Unity.Robotics.ROSTCPConnector {
 public class ROSConnection:UnityEngine.MonoBehaviour {
  public string RosIPAddress="127.0.0.1";
  readonly Dictionary<string,Delegate> callbacks=new Dictionary<string,Delegate>();
  public int subscriptions;
  public void Subscribe<T>(string topic,Action<T> callback) { subscriptions++; callbacks[topic]=callback; }
  public void Deliver<T>(string topic,T message) { ((Action<T>)callbacks[topic])(message); }
 }
}

public class KatViewFrameCapture:UnityEngine.MonoBehaviour {
 public static int starts,stops;
 public static KatViewFrameCapture StartCapture(string folder,Func<double> elapsed) { starts++; return new KatViewFrameCapture(); }
 public void StopRecording() { stops++; }
}
