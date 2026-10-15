using System;

/// <summary>Shared yaw convention for panorama, physical footprint, target and wrist map.
/// ROS positive yaw is left; Unity world positive yaw is right. No sensor or control side effects.</summary>
public static class RobotViewHeading
{
    public static float WorldYaw(float calibratedWorldYaw, double calibratedRosYaw, double rosYaw)
    {
        return Wrap(calibratedWorldYaw - (rosYaw - calibratedRosYaw));
    }

    public static float Wrap(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0) wrapped -= 360.0;
        if (wrapped <= -180.0) wrapped += 360.0;
        return (float)wrapped;
    }

    // The dial is yawed, then tilted about local X. Account for the tilt so the
    // horizontal projection of its robot arrow has the requested world bearing.
    // Subtracting yaw alone distorts that bearing whenever the dial is tilted.
    public static float DialLocalYaw(float worldYaw, float dialYaw, float tiltDeg)
    {
        double bearing = (worldYaw - dialYaw) * Math.PI / 180.0;
        double tilt = Math.Max(-80.0, Math.Min(80.0, tiltDeg)) * Math.PI / 180.0;
        return (float)(Math.Atan2(Math.Sin(bearing) * Math.Cos(tilt), Math.Cos(bearing)) * 180.0 / Math.PI);
    }
}
