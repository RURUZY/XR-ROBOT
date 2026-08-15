using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Nav;

public class OdometrySubscriber : MonoBehaviour
{
    // 当前 fake_husky_node 发布 /odom
    // 之后真实 Husky 一般也发布 /odom
    // 如果真实 Husky topic 不一样，比如 /husky/odom，就改这里
    public string odomTopic = "/odom";

    // ROS odom position
    private Vector3 rosPosition;

    // ROS yaw angle
    private float rosYaw;

    // 是否已经收到第一帧 odom
    private bool receivedOdom = false;

    void Start()
    {
        ROSConnection ros = ROSConnection.GetOrCreateInstance();

        // Subscribe to /odom
        ros.Subscribe<OdometryMsg>(odomTopic, OdomCallback);
    }

    void OdomCallback(OdometryMsg msg)
    {
        // ROS coordinate:
        // x = forward
        // y = left
        //
        // Unity coordinate:
        // z = forward
        // x = right
        //
        // 所以这里做一个简单坐标转换：
        // ROS x  -> Unity z
        // ROS y  -> Unity -x
        double rosX = msg.pose.pose.position.x;
        double rosY = msg.pose.pose.position.y;

        rosPosition = new Vector3(
            (float)(-rosY),
            transform.position.y,
            (float)(rosX)
        );

        // Quaternion from ROS odom
        double qx = msg.pose.pose.orientation.x;
        double qy = msg.pose.pose.orientation.y;
        double qz = msg.pose.pose.orientation.z;
        double qw = msg.pose.pose.orientation.w;

        // Extract yaw from quaternion
        double siny_cosp = 2.0 * (qw * qz + qx * qy);
        double cosy_cosp = 1.0 - 2.0 * (qy * qy + qz * qz);
        rosYaw = (float)System.Math.Atan2(siny_cosp, cosy_cosp);

        receivedOdom = true;
    }

    void Update()
    {
        if (!receivedOdom)
            return;

        // Update Unity object position from ROS odom
        transform.position = rosPosition;

        // ROS yaw -> Unity yaw
        // 如果之后发现方向反了，就把 -rosYaw 改成 rosYaw
        transform.rotation = Quaternion.Euler(
            0f,
            -rosYaw * Mathf.Rad2Deg,
            0f
        );
    }
}