using System;
using System.Reflection;
using UnityEngine;

internal static class Probe
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(KatHuskyDirectAngleController controller, string field, object value) =>
        typeof(KatHuskyDirectAngleController).GetField(field, Private).SetValue(controller, value);
    static float Evaluate(double bodyTurn, double robotTurn, float bodyRate, float robotRate)
    {
        Time.realtimeSinceStartup = 10;
        var c = new KatHuskyDirectAngleController { enableDebugLog = false, bodyToRosYawSign = -1 };
        Set(c, "hasOdometry", true);
        Set(c, "odometryUsable", true);
        Set(c, "lastOdometryTime", 10f);
        Set(c, "headingCalibrated", true);
        Set(c, "bodyHeadingUsable", true);
        Set(c, "headingSign", -1f);
        Set(c, "robotReference", 0.0);
        Set(c, "robotUnwrapped", robotTurn);
        Set(c, "bodyTurnDeg", bodyTurn);
        Set(c, "filteredBodyTurnDeg", bodyTurn);
        Set(c, "filteredYawRateDegPerSec", bodyRate);
        Set(c, "measuredRobotYawRate", robotRate);
        float command = (float)typeof(KatHuskyDirectAngleController).GetMethod("CalculateTurn", Private).Invoke(c, null);
        Console.WriteLine($"bodyRate={bodyRate:F1} deg/s, target={c.TargetTurnDeg:F1}, robot={robotTurn:F1}, error={c.HeadingErrorDeg:F1}, request={command:F6} rad/s, feedforward={c.LiveTurnDemand:F6}, feedback={c.CorrectionTurnDemand:F6}");
        return command;
    }
    static void Main()
    {
        Console.WriteLine("Actual controller source, simulated inputs, no robot/network connection.");
        Console.WriteLine("Recorded state near latest session t=152.179 (rounded log values):");
        float observed = Evaluate(-74.9, 24.1, 17.8f, 41.5f);
        if (observed <= .5f) throw new Exception("Recorded opposite-direction behavior did not reproduce.");
        Console.WriteLine("Clean reference, new right turn:");
        if (Evaluate(30, 0, 17.8f, 0) >= 0) throw new Exception("Clean right turn has unexpected sign.");
        Console.WriteLine("Clean reference, new left turn:");
        if (Evaluate(-30, 0, -17.8f, 0) <= 0) throw new Exception("Clean left turn has unexpected sign.");
        Console.WriteLine("Confirmed: old target error can command left during a new right turn; sign is correct on clean references.");
    }
}
