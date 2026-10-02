using System;
using System.Reflection;
using UnityEngine;
using RosMessageTypes.Nav;
using RosMessageTypes.Geometry;

// Test doubles for engine/hardware/transport only. The actual new controller
// source is compiled unchanged; no KAT, Unity scene or ROS connection is opened.
namespace UnityEngine
{
    public class MonoBehaviour { public bool isActiveAndEnabled = true; }
    public class DisallowMultipleComponent : Attribute {}
    public class HeaderAttribute : Attribute { public HeaderAttribute(string s) {} }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string s) {} }
    public class MinAttribute : Attribute { public MinAttribute(float f) {} }
    public class RangeAttribute : Attribute { public RangeAttribute(float a,float b) {} }
    public enum KeyCode { T,E,R,C }
    public static class Time { public static float realtimeSinceStartup; public static float? PhysicsTime; public static float unscaledTime => PhysicsTime ?? realtimeSinceStartup; public static float fixedDeltaTime=.02f; }
    public static class Input { public static System.Collections.Generic.HashSet<KeyCode> Keys=new System.Collections.Generic.HashSet<KeyCode>(); public static bool GetKey(KeyCode k) => Keys.Contains(k); }
    public static class Debug { public static void Log(object s) {} public static void LogWarning(object s) {} }
    public struct Vector2 { public float x,y; public Vector2(float a,float b){x=a;y=b;} public float magnitude => MathF.Sqrt(x*x+y*y); }
    public struct Vector3 { public float x,y,z; public float sqrMagnitude=>x*x+y*y+z*z; public void Normalize(){float n=MathF.Sqrt(sqrMagnitude);x/=n;y/=n;z/=n;} public static Vector3 forward => new Vector3{z=1}; }
    public struct Quaternion
    {
        public float x,y,z,w;
        public Quaternion(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}
        public static Vector3 operator *(Quaternion q,Vector3 v)
        {
            return new Vector3 {
                x=(1-2*(q.y*q.y+q.z*q.z))*v.x+2*(q.x*q.y-q.z*q.w)*v.y+2*(q.x*q.z+q.y*q.w)*v.z,
                y=2*(q.x*q.y+q.z*q.w)*v.x+(1-2*(q.x*q.x+q.z*q.z))*v.y+2*(q.y*q.z-q.x*q.w)*v.z,
                z=2*(q.x*q.z-q.y*q.w)*v.x+2*(q.y*q.z+q.x*q.w)*v.y+(1-2*(q.x*q.x+q.y*q.y))*v.z };
        }
    }
    public static class Mathf
    {
        public const float PI = MathF.PI, Deg2Rad = PI/180, Rad2Deg=180/PI;
        public static float Sign(float x)=>x>=0?1:-1;
        public static float Abs(float x)=>MathF.Abs(x);
        public static float Min(float a,float b)=>MathF.Min(a,b);
        public static float Max(float a,float b)=>MathF.Max(a,b);
        public static float Clamp(float x,float a,float b)=>Math.Clamp(x,a,b);
        public static float Clamp01(float x)=>Clamp(x,0,1);
        public static float Lerp(float a,float b,float t)=>a+(b-a)*Clamp01(t);
        public static float Pow(float a,float b)=>MathF.Pow(a,b);
        public static float Exp(float x)=>MathF.Exp(x);
        public static float Cos(float x)=>MathF.Cos(x);
        public static float Sqrt(float x)=>MathF.Sqrt(x);
        public static float Atan2(float a,float b)=>MathF.Atan2(a,b);
        public static float DeltaAngle(float a,float b){float d=(b-a)%360;if(d<0)d+=360;return d>180?d-360:d;}
        public static float MoveTowards(float a,float b,float d)=>Abs(b-a)<=d?b:a+Sign(b-a)*d;
    }
}
namespace UnityEngine.XR
{
    public enum XRNode {LeftHand,RightHand}
    public struct InputFeatureUsage<T> {}
    public struct InputDevice { public bool isValid=>false; public bool TryGetFeatureValue<T>(InputFeatureUsage<T> u,out T v){v=default;return false;} }
    public static class InputDevices {public static InputDevice GetDeviceAtXRNode(XRNode n)=>default;}
    public static class CommonUsages {public static InputFeatureUsage<float> trigger; public static InputFeatureUsage<bool> triggerButton,primaryButton,secondaryButton;}
}
namespace RosMessageTypes.Geometry
{
    public class QuaternionMsg {public double x,y,z,w=1;}
    public class Vector3Msg {public double x,y,z; public Vector3Msg(){} public Vector3Msg(double a,double b,double c){x=a;y=b;z=c;} }
    public class TwistMsg {public Vector3Msg linear=new Vector3Msg(), angular=new Vector3Msg(); public TwistMsg(){} public TwistMsg(Vector3Msg a,Vector3Msg b){linear=a;angular=b;} }
    public class PoseMsg {public QuaternionMsg orientation=new QuaternionMsg();}
    public class PoseWithCovarianceMsg {public PoseMsg pose=new PoseMsg();}
}
namespace RosMessageTypes.Nav
{
    public class Stamp {public uint sec,nanosec;}
    public class Header {public Stamp stamp=new Stamp();public string frame_id="odom";}
    public class OdometryMsg {public Header header=new Header();public string child_frame_id="base_link";public PoseWithCovarianceMsg pose=new PoseWithCovarianceMsg();}
}
namespace Unity.Robotics.ROSTCPConnector
{
    public class ROSConnection
    {
        public static ROSConnection Instance=new ROSConnection();
        public static ROSConnection GetOrCreateInstance()=>Instance;
        public int Count; public TwistMsg Last;
        public void RegisterPublisher<T>(string s) {}
        public void Subscribe<T>(string s,Action<T> c) {}
        public void Publish(string s,TwistMsg msg){Last=msg;Count++;}
    }
}
public class KATNativeSDK
{
    public struct TreadMillData {public byte[] extraData; public bool connected;public Quaternion bodyRotationRaw;public Vector3 moveSpeed;public double lastUpdateTimePoint;public string deviceName;}
    public static TreadMillData Data;
    public static TreadMillData GetWalkStatus(string s="")=>Data;
}

namespace UnityEngine {
 public enum FontStyle {Bold} public struct Color {public static Color white=>default;}
 public struct Rect {public Rect(float a,float b,float c,float d){}}
 public class GUIStyleState {public Color textColor;}
 public class GUIStyle {public int fontSize;public FontStyle fontStyle;public GUIStyleState normal=new GUIStyleState();public GUIStyle(GUIStyle s){}}
 public class GUISkin {public GUIStyle label;}
 public static class GUI {public static GUISkin skin=new GUISkin();public static void Label(Rect r,string s,GUIStyle t){}}
}
public class MiniSExtraData {
 public struct extraInfo {public bool isMoving,isLeftGround,isRightGround,isLeftStatic,isRightStatic;public int isForward,motionType,action;public UnityEngine.Vector3 skatingSpeed,lFootSpeed,rFootSpeed;}
 public static bool Step;public static extraInfo GetExtraInfoMiniS(KATNativeSDK.TreadMillData d)=>new extraInfo{isMoving=Step};
}
public class WalkC2ExtraData {public struct extraInfo {public int motionType;public UnityEngine.Vector3 lFootSpeed,rFootSpeed;} public static extraInfo GetExtraInfoC2(KATNativeSDK.TreadMillData d)=>default;}
