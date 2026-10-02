using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

partial class Program
{
    // Experiments use the actual controller with simulated transport/plant.
    // No production parameters are edited. Gain/lag are assumptions, not a
    // calibration of the user's Husky. Completion is measured in world angle,
    // so re-anchoring cannot masquerade as successful tracking.
    static void RunTurnAudit()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("cap,gain,delay,lag,angle,rate,walking,glitch,atBodyStop,maxLag,settleAfterStop,finalWorldError,targetLost,peakCommand,overshoot\n");
        int scenarios = 0, cleanFailures = 0;
        foreach(float cap in new[]{.15f,.35f,1.2f})
        foreach(float gain in new[]{1f,.65f,.45f})
        foreach(float angle in new[]{90f,180f,360f})
        foreach(float rate in new[]{10f,30f,90f,180f})
        foreach(float sign in new[]{-1f,1f})
        foreach(bool walking in new[]{false,true})
        {
            string row = AuditTurn(cap,gain,angle*sign,rate,walking,false,out bool success);
            csv.AppendLine(row); scenarios++; if(!success)cleanFailures++;
            if(sign==1 && gain==.65f && !walking)Console.WriteLine(row);
        }
        int glitchFailures=0;
        foreach(float cap in new[]{.15f,.35f,1.2f})
        foreach(float sign in new[]{-1f,1f})
        {
            csv.AppendLine(AuditTurn(cap,.65f,360*sign,90,false,true,out bool success));
            if(!success)glitchFailures++;
        }
        int stressFailures=0;
        foreach(float gain in new[]{1f,.65f,.45f})
        foreach(float angle in new[]{-360f,-180f,180f,360f})
        foreach(bool walking in new[]{false,true})
        {
            csv.AppendLine(AuditTurn(1.2f,gain,angle,90,walking,false,out bool success,.45f,.4f));
            if(!success)stressFailures++;
        }
        string path = Path.GetFullPath("SupportPackage/KatDirectAngleRegression/turn-audit-target-follow.csv");
        File.WriteAllText(path,csv.ToString());
        Console.WriteLine($"Clean scenarios={scenarios}, world-angle failures={cleanFailures}; six persistent-jump experiments, failures={glitchFailures}; output={path}");
        Assert(cleanFailures==0,"clean scenario did not settle in world coordinates");
        Assert(glitchFailures==0,"persistent jump cancelled an accepted target");
        Console.WriteLine($"24 longer-delay/lag cases; failures={stressFailures}");
        Assert(stressFailures==0,"longer delay/lag failed");
    }

    static string AuditTurn(float cap,float gain,float angle,float rate,bool walking,bool glitch,out bool success,float delay=.30f,float lag=.25f)
    {
        New(); c.maxHeadingCorrectionSpeed=cap;
        const float dt=.02f;
        var history=new Queue<float>(); for(int i=0;i<(int)Math.Round(delay/dt);i++)history.Enqueue(0);
        float robot=0,velocity=0,atStop=float.NaN,maxLag=0,peakCommand=0;
        float stopTime=Math.Abs(angle)/rate, stableSince=-1,settled=-1;
        bool targetLost=false;
        float overshoot=0;
        int ticks=(int)Math.Ceiling((stopTime+120)/dt);
        for(int i=1;i<=ticks;i++)
        {
            float t=i*dt;
            float body=Math.Sign(angle)*Math.Min(t*rate,Math.Abs(angle));
            // A persistent orientation discontinuity after the turn, similar
            // to the captured field failure. Physical body angle stays fixed.
            float sensed=body+(glitch && t>=stopTime+.4f?139:0);
            Tick(sensed,history.Dequeue(),walking && t<=stopTime && i%25<8?1.7f:0);
            velocity+=(c.LastPublishedAngular*gain-velocity)*(1-MathF.Exp(-dt/lag));
            robot+=velocity*dt*Mathf.Rad2Deg;
            overshoot=Math.Max(overshoot,Math.Sign(angle)*robot-Math.Abs(angle));
            history.Enqueue(robot);
            if(t>=stopTime && float.IsNaN(atStop))atStop=robot;
            maxLag=Math.Max(maxLag,Math.Abs(body-robot));
            peakCommand=Math.Max(peakCommand,Math.Abs(c.LastPublishedAngular));
            if(t>stopTime+.3f && Math.Abs(c.BodyTurnDeg-angle)>5)targetLost=true;
            bool close=t>=stopTime && Math.Abs(angle-robot)<=2 && Math.Abs(velocity*Mathf.Rad2Deg)<.5f;
            if(!close){stableSince=-1;settled=-1;}
            else if(stableSince<0)stableSince=t;
            else if(t-stableSince>=1 && settled<0)settled=stableSince-stopTime;
        }
        success=settled>=0 && Math.Abs(angle-robot)<=2 && !targetLost;
        Assert(overshoot<10,"excessive overshoot="+overshoot);
        return $"{cap:F2},{gain:F2},{delay:F2},{lag:F2},{angle:F0},{rate:F0},{walking},{glitch},{atStop:F1},{maxLag:F1},{settled:F2},{angle-robot:F2},{targetLost},{peakCommand:F3},{overshoot:F2}";
    }
}
