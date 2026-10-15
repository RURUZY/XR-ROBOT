using System;

partial class Program
{
    static void NearYaw(float actual, float expected, string message)
        => Assert(Math.Abs(RobotViewHeading.Wrap(actual - expected)) < .001, message + ": " + actual + " != " + expected);

    static void RunViewHeadingTests()
    {
        Test("left and right physical turns change shared world heading in opposite directions", () => {
            NearYaw(RobotViewHeading.WorldYaw(20, -14, 46), -40, "left 60");
            NearYaw(RobotViewHeading.WorldYaw(20, -14, -74), 80, "right 60");
        });
        Test("target stays fixed in the world while robot catches up", () => {
            float target = RobotViewHeading.WorldYaw(20, -14, 46);
            foreach (double robot in new[] { -14.0, 0, 30, 46 })
                NearYaw(RobotViewHeading.Wrap(target - RobotViewHeading.WorldYaw(20, -14, robot)), (float)(robot - 46), "remaining left turn");
        });
        Test("aligned absolute target and physical heading coincide including multiple turns", () => {
            foreach (double yaw in new[] { -740.0, -180, 0, 179, 720 })
                NearYaw(RobotViewHeading.WorldYaw(35, 150, yaw), RobotViewHeading.WorldYaw(35, 150, yaw + 720), "accumulated target");
        });
        Test("heading crossing plus minus 180 has no false full revolution", () => {
            NearYaw(RobotViewHeading.WorldYaw(10, 179, -179), 8, "positive seam");
            NearYaw(RobotViewHeading.WorldYaw(10, -179, 179), 12, "negative seam");
        });
        Test("new forward calibration reanchors both directions identically", () => {
            NearYaw(RobotViewHeading.WorldYaw(120, 70, 70), 120, "new reference");
            NearYaw(RobotViewHeading.WorldYaw(120, 70, 100), 90, "left after recalibration");
        });
        Test("tilted wrist arrow horizontal projection matches live world heading", () => {
            foreach (float tilt in new[] { 0f, 40f, -30f, 80f })
            foreach (float dial in new[] { -175f, 0f, 93f })
            foreach (float world in new[] { -179f, -90f, -40f, 0f, 45f, 179f }) {
                double local = RobotViewHeading.DialLocalYaw(world, dial, tilt) * Math.PI / 180;
                // Independently project a unit local arrow using a 3D X tilt then a world Y rotation.
                double x = Math.Sin(local), z = Math.Cos(local) * Math.Cos(tilt * Math.PI / 180);
                double yaw = dial * Math.PI / 180;
                float projected = (float)(Math.Atan2(x * Math.Cos(yaw) + z * Math.Sin(yaw), -x * Math.Sin(yaw) + z * Math.Cos(yaw)) * 180 / Math.PI);
                NearYaw(projected, world, "wrist bearing");
            }
        });
        Test("map no longer keeps its startup forward after a left turn", () => {
            float initialWorld = RobotViewHeading.WorldYaw(0, 10, 10);
            float turnedWorld = RobotViewHeading.WorldYaw(0, 10, 70);
            NearYaw(initialWorld, 0, "startup");
            NearYaw(turnedWorld, -60, "current forward");
            NearYaw(RobotViewHeading.DialLocalYaw(turnedWorld, 0, 0) - (-100) + (-100), -60, "SLAM frame cancels");
        });
    }
}
