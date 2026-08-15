using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

public class CmdVelPublisher : MonoBehaviour
{
    // ROS topic name.
    // 之后接真实 Husky 时，一般仍然是 /cmd_vel，不一定要改。
    public string topicName = "/cmd_vel";

    // 之后可以根据 Husky 实际速度限制调整
    public float linearSpeed = 0.5f;   // m/s
    public float angularSpeed = 0.8f;  // rad/s

    private ROSConnection ros;

    void Start()
    {
        // Connect to ROS using Robotics -> ROS Settings 中配置的 IP 和 Port
        ros = ROSConnection.GetOrCreateInstance();

        // Register /cmd_vel publisher
        ros.RegisterPublisher<TwistMsg>(topicName);
    }

    void Update()
    {
        // 当前阶段：键盘输入
        // W/S -> forward/backward
        // A/D -> turn left/right
        float moveInput = Input.GetAxis("Vertical");
        float turnInput = Input.GetAxis("Horizontal");

        TwistMsg cmdVel = new TwistMsg();

        // Husky forward/backward velocity
        cmdVel.linear.x = moveInput * linearSpeed;

        // Husky turning velocity
        // 如果以后发现左右方向反了，把这里的负号去掉
        cmdVel.angular.z = -turnInput * angularSpeed;

        // 其他方向保持 0
        cmdVel.linear.y = 0;
        cmdVel.linear.z = 0;
        cmdVel.angular.x = 0;
        cmdVel.angular.y = 0;

        // Publish to ROS2
        ros.Publish(topicName, cmdVel);
    }
}