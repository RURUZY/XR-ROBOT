#!/usr/bin/env python3
"""
fake_husky_sim.py

Lightweight fake Husky robot for testing KatHuskySimTestRig.cs (or the real
KatHuskyClosedLoopController) end-to-end over ROS, without needing Gazebo or
the real robot.

Subscribes:  /cmd_vel            (geometry_msgs/Twist)
Publishes:   /odometry/filtered  (nav_msgs/Odometry)

Model: simple unicycle integration.
    x'     = v * cos(theta)
    y'     = v * sin(theta)
    theta' = w

Includes a command watchdog: if no /cmd_vel is received for
CMD_TIMEOUT_SEC, velocity is forced to zero (mirrors a real base's
safety behavior instead of coasting forever on a stale command).

Usage:
    1. roscore
    2. Launch ROS-TCP-Endpoint so Unity can connect, e.g.:
       roslaunch ros_tcp_endpoint endpoint.launch tcp_ip:=<this-machine-ip> tcp_port:=10000
    3. chmod +x fake_husky_sim.py
       python3 fake_husky_sim.py
       (or drop this file into an existing catkin package's scripts/ folder
       and `rosrun <package> fake_husky_sim.py`)
    4. In Unity, point the ROS Connection Prefab's IP at this machine and
       press Play with KatHuskySimTestRig (or the real controller) enabled.

Topic names / rates can be overridden via ROS params:
    _cmd_vel_topic       (default: /cmd_vel)
    _odom_topic          (default: /odometry/filtered)
    _publish_rate_hz      (default: 50.0)
    _cmd_timeout_sec      (default: 1.0)
"""

import math

import rospy
from geometry_msgs.msg import Twist
from nav_msgs.msg import Odometry


class FakeHuskySim:
    def __init__(self):
        self.cmd_vel_topic = rospy.get_param("~cmd_vel_topic", "/cmd_vel")
        self.odom_topic = rospy.get_param("~odom_topic", "/odometry/filtered")
        self.publish_rate_hz = rospy.get_param("~publish_rate_hz", 50.0)
        self.cmd_timeout_sec = rospy.get_param("~cmd_timeout_sec", 1.0)

        self.x = 0.0
        self.y = 0.0
        self.theta = 0.0  # radians
        self.v = 0.0
        self.w = 0.0
        self.last_cmd_time = rospy.Time(0)

        self.odom_pub = rospy.Publisher(self.odom_topic, Odometry, queue_size=10)
        rospy.Subscriber(self.cmd_vel_topic, Twist, self._on_cmd_vel)

        self._seq = 0
        rospy.loginfo(
            "fake_husky_sim: cmd_vel=%s odom=%s rate=%.1fHz timeout=%.2fs",
            self.cmd_vel_topic, self.odom_topic, self.publish_rate_hz, self.cmd_timeout_sec,
        )

    def _on_cmd_vel(self, msg: Twist):
        self.v = msg.linear.x
        self.w = msg.angular.z
        self.last_cmd_time = rospy.Time.now()

    def _apply_watchdog(self):
        if (rospy.Time.now() - self.last_cmd_time).to_sec() > self.cmd_timeout_sec:
            self.v = 0.0
            self.w = 0.0

    def _step(self, dt: float):
        self._apply_watchdog()
        self.x += self.v * math.cos(self.theta) * dt
        self.y += self.v * math.sin(self.theta) * dt
        self.theta += self.w * dt
        # wrap to [-pi, pi]
        self.theta = math.atan2(math.sin(self.theta), math.cos(self.theta))

    def _publish_odom(self):
        odom = Odometry()
        odom.header.stamp = rospy.Time.now()
        odom.header.seq = self._seq
        self._seq += 1
        odom.header.frame_id = "odom"
        odom.child_frame_id = "base_link"

        odom.pose.pose.position.x = self.x
        odom.pose.pose.position.y = self.y
        odom.pose.pose.position.z = 0.0

        half = self.theta / 2.0
        odom.pose.pose.orientation.x = 0.0
        odom.pose.pose.orientation.y = 0.0
        odom.pose.pose.orientation.z = math.sin(half)
        odom.pose.pose.orientation.w = math.cos(half)

        odom.twist.twist.linear.x = self.v
        odom.twist.twist.angular.z = self.w

        self.odom_pub.publish(odom)

    def run(self):
        rate = rospy.Rate(self.publish_rate_hz)
        dt = 1.0 / self.publish_rate_hz
        while not rospy.is_shutdown():
            self._step(dt)
            self._publish_odom()
            rate.sleep()


def main():
    rospy.init_node("fake_husky_sim")
    FakeHuskySim().run()


if __name__ == "__main__":
    main()
