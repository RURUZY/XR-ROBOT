using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;
using RosMessageTypes.Nav;
using RosMessageTypes.Geometry;

// The web laboratory displays traces from the actual controller source included
// by Tests.csproj. Only the plant, sensors, clock and ROS transport are simulated.
static class LabExport
{
    static readonly string[] Scenarios={"turn90","turn270","turn360","turnNegative","turn720","reverse","sway","bodyInvalid","bodyJump","odomShort","odomLong","frameJump","blocked"};
    static readonly string[] Names={"慢转 90°","大转弯 270°","快速转身 360°","反向转身 −360°","连续两圈 720°","先左后右","直线踏步摆动","身体姿态短暂无效","身体朝向跳变","里程计短断流","里程计长断流","里程计坐标跳变","底盘卡住"};
    static readonly string[] Notes={"30°/s 转至 90°，然后等待机器人补齐。","90°/s 转至 270°，验证误差不走最短角。","180°/s 转满一圈，观察积压追赶。","−180°/s 转满一圈，验证方向与跨界。","180°/s 连续转两圈，累计角不归零。","先转至 +90°，再反向回到 0°；新输入优先。","±2°、1 Hz 腰部摆动与间歇踏步；12 秒后停止。","转身过程中 t=2.0–2.2 s 身体姿态无效，短恢复保留累计目标。","t=2.0–2.2 s 注入 +139° 读数跳变；异常期间停止角输出。","t=5.0–5.6 s 里程计断流，恢复后计入期间真实转动。","t=5.0–6.4 s 里程计断流，恢复后取消来源不明的待转角。","t=5 s 起里程计坐标整体增加 100°，真实机器人没有瞬移。","t=5 s 起底盘无法转动；进展检查应取消积压目标。"};
    static readonly float[] AngleFilters={.06f,0f,.18f}, RateFilters={.1f,0f,.1f};
    static readonly float[] Gains={1f,.65f,.45f}, Delays={0f,.3f,.3f}, Lags={.05f,.25f,.4f};
    static object Call(KatHuskyDirectAngleController c,string n,params object[] a)=>typeof(KatHuskyDirectAngleController).GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,a);
    static double R(double v)=>double.IsFinite(v)?Math.Round(v,3):0;
    public static void Export(string output)
    {
        Directory.CreateDirectory(Path.Combine(output,"traces"));
        string controller=Path.GetFullPath("Assets/KAT/KatHuskyDirectAngleController.cs");
        string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(controller))).ToLowerInvariant();
        var catalog=new List<object>();
        for(int s=0;s<Scenarios.Length;s++)
        {
            catalog.Add(new{id=Scenarios[s],name=Names[s],note=Notes[s]});
            for(int f=0;f<3;f++)for(int p=0;p<3;p++)
            {
                var trace=Simulate(Scenarios[s],f,p);
                File.WriteAllText(Path.Combine(output,"traces",$"{Scenarios[s]}-{f}-{p}.json"),JsonSerializer.Serialize(trace));
            }
        }
        File.WriteAllText(Path.Combine(output,"catalog.json"),JsonSerializer.Serialize(new{generatedUtc=DateTime.UtcNow.ToString("O"),controllerSha256=hash,scenarios=catalog,columns=new[]{"t","body","target","robot","odomTurn","error","angular","linear","x","y"},filters=new[]{new{name="当前参数",angle=.06,rate=.10},new{name="关闭滤波",angle=0.0,rate=0.0},new{name="较强滤波",angle=.18,rate=.10}},plants=new[]{new{name="接近理想",gain=1.0,delay=0.0,lag=.05},new{name="延迟与响应损失",gain=.65,delay=.30,lag=.25},new{name="较弱底盘响应",gain=.45,delay=.30,lag=.40}},parameters=new{headingKp=.4,correctionCap=.15,angularCap=.8,tolerance=2,odometryTimeout=.5,grace=1,progressTimeout=5}}));
        Console.WriteLine($"Exported {Scenarios.Length*9} actual-controller simulations to {output}; SHA256 {hash}");
    }
    static object Simulate(string scenario,int filter,int plant)
    {
        Time.realtimeSinceStartup=10;Time.PhysicsTime=null;MiniSExtraData.Step=false;Input.Keys.Clear();
        var c=new KatHuskyDirectAngleController{enableDebugLog=false,startActivated=true,bodyToRosYawSign=1,bodyYawFilterTime=AngleFilters[filter],turnRateSmoothingTime=RateFilters[filter]};
        Call(c,"OnEnable");Call(c,"Start");
        double robot=0,velocity=0,x=0,y=0,peakAngular=0,lastDeliveredWorld=0;var measuredHistory=new List<double>();
        var rows=new List<double[]>();var events=new List<object>();string lastStatus="";
        const float dt=.02f;const int ticks=6000;
        int delayTicks=(int)Math.Round(Delays[plant]/dt);
        for(int i=0;i<=ticks;i++)
        {
            float t=i*dt;Time.realtimeSinceStartup=10+t;
            float targetAngle=scenario=="turn90"?90:scenario=="turn270"?270:scenario=="turn720"?720:360;
            float rate=scenario=="turn90"?30:scenario=="turn270"?90:180;
            float body=Math.Min(Math.Max(t-1,0)*rate,targetAngle);
            if(scenario=="turnNegative")body=-body;
            if(scenario=="reverse")body=t<2?Math.Min(Math.Max(t-1,0)*90,90):Math.Max(90-(t-2)*90,0);
            if(scenario=="sway")body=t<12?2*MathF.Sin(2*MathF.PI*t):0;
            float sensed=body+(scenario=="bodyJump"&&t>=2&&t<2.2?139:0);
            var q=new Quaternion(0,MathF.Sin(sensed*Mathf.Deg2Rad/2),0,MathF.Cos(sensed*Mathf.Deg2Rad/2));
            if(scenario=="bodyInvalid"&&t>=2&&t<2.2)q=new Quaternion();
            float walk=scenario=="sway"&&t<12&&i%4==1?.1f:0;
            KATNativeSDK.Data=new KATNativeSDK.TreadMillData{connected=true,deviceName="MiniS",extraData=new byte[128],moveSpeed=new Vector3{x=walk},bodyRotationRaw=q};
            measuredHistory.Add(robot);
            double measuredWorld=measuredHistory[Math.Max(0,i-delayTicks)];
            bool odomMissing=scenario=="odomShort"&&t>=5&&t<5.6||scenario=="odomLong"&&t>=5&&t<6.4;
            if(!odomMissing)
            {
                lastDeliveredWorld=measuredWorld;
                double measured=measuredWorld+(scenario=="frameJump"&&t>=5?100:0);
                var o=new OdometryMsg();o.header.stamp.sec=(uint)(100+i/50);o.header.stamp.nanosec=(uint)(i%50)*20000000;
                if(scenario=="frameJump"&&t>=5)o.header.frame_id="odom_reset";
                o.pose.pose.orientation=new QuaternionMsg{z=Math.Sin(measured*Math.PI/360),w=Math.Cos(measured*Math.PI/360)};
                Call(c,"OnOdometry",o);
                if(c.TurnStatus.Contains("RE-ANCHORED")||c.TurnStatus.Contains("RESYNCED"))
                    events.Add(new{t=R(t),status=c.TurnStatus});
            }
            Call(c,"Update");Call(c,"FixedUpdate");
            peakAngular=Math.Max(peakAngular,Math.Abs(c.LastPublishedAngular));
            if(c.TurnStatus!=lastStatus){events.Add(new{t=R(t),status=c.TurnStatus});lastStatus=c.TurnStatus;}
            // Put the target back into the simulated world's initial reference
            // for plotting; this also makes deliberate target drops visible.
            if(i%5==0)rows.Add(new[]{R(t),R(body),R(lastDeliveredWorld+c.HeadingErrorDeg),R(robot),R(lastDeliveredWorld),R(c.HeadingErrorDeg),R(c.LastPublishedAngular),R(c.LastPublishedLinear),R(x),R(y)});
            double commanded=c.LastPublishedAngular*Gains[plant];
            velocity+=(commanded-velocity)*(1-Math.Exp(-dt/Lags[plant]));
            if(scenario=="blocked"&&t>=5)velocity=0;
            robot+=velocity*dt*Mathf.Rad2Deg;
            x+=c.LastPublishedLinear*Math.Cos(robot*Math.PI/180)*dt;
            y+=c.LastPublishedLinear*Math.Sin(robot*Math.PI/180)*dt;
        }
        return new{scenario,filter,plant,dt=.1,peakAngular=R(peakAngular),rows,events};
    }
}
