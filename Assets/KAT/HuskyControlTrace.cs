using System;
using System.Threading;
using UnityEngine;

/// <summary>
/// Read-only observations at actual input/request/publish sites. Publishing means
/// handed to ROSConnection, not acknowledged by the chassis. Logging cannot drive
/// the robot; a failing listener is detached so it cannot interrupt control.
/// </summary>
public static class HuskyControlTrace
{
    public sealed class Entry
    {
        public long sequence, hostUtcNs, inputSequence;
        public double unityReceiptTime;
        public string stage, source, topic;
        public object[] values;
    }

    public static event Action<Entry> Recorded;
    public static bool IsRecording => Recorded != null;
    private static long sequence;

    public static string Columns(string stage)
    {
        switch (stage)
        {
            case "input": return "sdk_stamp_raw,connected,move_x,move_y,move_z,quat_x,quat_y,quat_z,quat_w";
            case "keyboard_input": return "move_axis,turn_axis";
            case "request": return "unassisted_linear,requested_linear,requested_angular,target_yaw_deg,error_deg,forward_multiplier,heading_mode,activated,heading_usable,status,slam_correction_deg,slam_status";
            case "publish": return "linear_x,angular_z,activated,status,transport_available";
            case "odometry": return "ros_stamp_sec,ros_stamp_nsec,frame_id,child_frame_id,x,y,z,quat_x,quat_y,quat_z,quat_w,linear_x,angular_z";
            case "chassis_command": return "ros_stamp_sec,ros_stamp_nsec,frame_id,linear_x,linear_y,linear_z,angular_x,angular_y,angular_z";
            case "configuration": return "component_enabled,settings_json";
            case "marker": return "label";
            case "panorama": return "heading_fresh,calibrated,calibration_revision,source_mode,compensation_enabled,compensation_sign,flip_horizontal,delay_s,odom_stamp_s,odom_yaw_deg,odom_age_s,calibration_ros_yaw_deg,calibration_world_yaw_deg,robot_world_yaw_deg,sphere_world_yaw_deg,head_world_yaw_deg";
            case "view_overlay": return "heading_valid,target_valid,robot_world_yaw_deg,target_world_yaw_deg,target_ros_yaw_deg,controller_robot_ros_yaw_deg,error_deg,head_world_yaw_deg,anchor_override,heading_topic_matches";
            case "minimap": return "orientation,heading_valid,slam_pose_fresh,slam_localized,slam_age_s,slam_yaw_deg,robot_world_yaw_deg,dial_world_yaw_deg,content_local_yaw_deg,arrow_world_yaw_deg,slam_x,slam_y,slam_z";
            default: throw new ArgumentException("Unknown control trace stage: " + stage);
        }
    }

    public static void Record(string stage, string source, string topic, long inputSequence, params object[] values)
    {
        var listeners = Recorded;
        if (listeners == null) return;
        var entry = new Entry {
            sequence = Interlocked.Increment(ref sequence),
            hostUtcNs = (DateTime.UtcNow.Ticks - 621355968000000000L) * 100L,
            unityReceiptTime = Time.realtimeSinceStartup,
            stage = stage, source = source, topic = topic, inputSequence = inputSequence, values = values
        };
        foreach (Action<Entry> listener in listeners.GetInvocationList())
        {
            try { listener(entry); }
            catch (Exception ex)
            {
                Recorded -= listener;
                Debug.LogWarning("Control trace listener disabled after an error: " + ex.Message);
            }
        }
    }
}
