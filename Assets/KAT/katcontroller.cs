using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;


public class katcontroller : MonoBehaviour
{
    [Header("KAT Device")]
    public string katSerialNumber = "KAT20230308935";
    [Tooltip("Keep this off for most KAT Industry/Gateway setups. The official SDK demos query the first active treadmill with GetWalkStatus().")]
    public bool requireConfiguredSerialNumber = false;
    [Tooltip("Only use this if KAT Industry/Gateway reports the device is busy and you intentionally want Unity to take it over.")]
    public bool forceConnectWhenBusy = false;

    [Header("ROS")]
    public string cmdVelTopic = "/cmd_vel";

    [Header("Linear control")]
    public float maxForwardSpeed = 0.35f;
    public float maxReverseSpeed = 0.20f;
    [Tooltip("KAT input below this value is treated as standing still.")]
    public float walkDeadZone = 0.02f;
    [Tooltip("KAT input that produces maximum robot speed. Lower values make walking more sensitive.")]
    public float inputSpeedForMax = 0.50f;
    [Tooltip("Minimum commanded speed once walking is detected, so the Husky starts moving reliably.")]
    public float minDriveSpeed = 0.08f;
    [Tooltip("Values below 1 boost gentle walking. 0.6 gives a responsive but controllable curve.")]
    [Range(0.3f, 1f)]
    public float responseExponent = 0.60f;
    [Tooltip("Acceleration smoothing time. Lower values respond faster.")]
    public float forwardAttackTime = 0.04f;
    [Tooltip("Stopping smoothing time. A slightly larger value prevents jerky stops.")]
    public float forwardReleaseTime = 0.15f;

    [Header("Turning control (rate-based)")]
    [Tooltip("Robot rotation angle / body rotation angle. 1 means approximately 1:1.")]
    public float turnGain = 1.0f;
    public float maxAngularSpeed = 0.8f;
    [Tooltip("Ignore body rotation slower than this many degrees per second.")]
    public float yawRateDeadZoneDegPerSec = 8f;
    [Tooltip("Body yaw-rate smoothing time in seconds.")]
    public float yawRateSmoothingTime = 0.08f;
    [Tooltip("When enabled, body rotation controls the robot only while walking.")]
    public bool turnOnlyWhileWalking = false;
    public bool invertAngular = true;

    [Header("Publishing")]
    public float publishRateHz = 50f;

    [Header("Safety")]
    [Tooltip("Default is enabled for local testing. Disable this only when you want to keep the robot idle.")]
    public bool enableRobotControl = true;

    [Header("VR Activation")]
    [Tooltip("Press the trigger on the selected hand to activate movement after adjusting direction.")]
    public bool enableActivationButton = true;
    public XRNode activationHand = XRNode.RightHand;
    [Tooltip("If true, trigger press toggles activation on/off. If false, movement is active only while the trigger is held.")]
    public bool activationLatch = true;
    [Tooltip("Start ready to publish KAT movement. The trigger can still toggle movement off when activation latching is enabled.")]
    public bool startActivated = true;
    [Tooltip("Trigger must exceed this value to count as pressed. Lower this if the trigger feels too insensitive.")]
    public float activationTriggerThreshold = 0.20f;
    [Tooltip("Keyboard fallback only. Leave disabled if you are using VR controllers only.")]
    public bool enableActivationKeyboardFallback = false;
    public KeyCode activationKey = KeyCode.T;

    [Header("VR Emergency Stop")]
    [Tooltip("Press the primary VR button on the selected hand to engage/disengage emergency stop.")]
    public bool enableEmergencyStopButton = true;
    public XRNode emergencyStopHand = XRNode.RightHand;
    [Tooltip("If true, the stop stays engaged until the button is pressed again. If false, it stops only while the button is held.")]
    public bool emergencyStopLatch = true;
    [Tooltip("Keyboard fallback only. Leave disabled if you are using VR controllers only.")]
    public bool enableKeyboardFallback = false;
    public KeyCode emergencyStopKey = KeyCode.E;

    [Header("VR Reverse Mode")]
    [Tooltip("Press the secondary VR button on the selected hand to toggle reverse mode.")]
    public bool enableReverseModeButton = true;
    public XRNode reverseModeHand = XRNode.RightHand;
    [Tooltip("If true, reverse mode toggles on/off with a button press. If false, reverse mode is active only while the button is held.")]
    public bool reverseModeLatch = true;
    [Tooltip("Keyboard fallback only. Leave disabled if you are using VR controllers only.")]
    public KeyCode reverseModeKey = KeyCode.R;

    [Header("Debug")]
    public bool enableDebugLog = true;
    public float debugLogInterval = 0.2f;

    [Header("UI")]
    public bool showStatusGUI = true;

    private ROSConnection ros;

    private float nextPublishTime;
    private float nextLogTime;
    private float nextDeviceScanTime;
    private string activeSerialNumber = "";

    private bool activationEnabled;
    private bool lastActivationButtonState;
    private bool emergencyStopEngaged;
    private bool lastEmergencyStopButtonState;
    private bool reverseModeEnabled;
    private bool lastReverseModeButtonState;
    private string currentStatusText = "STANDBY";
    private float filteredForwardSpeed;
    private float filteredYawRateDegPerSec;
    private float previousBodyAngle;
    private bool hasPreviousBodyAngle;
    private float currentTriggerValue;
    private float lastPublishedLinear;
    private float lastPublishedAngular;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(cmdVelTopic);
        activationEnabled = !enableActivationButton || startActivated;
        currentStatusText = activationEnabled
            ? "ACTIVE"
            : (activationLatch ? "PRESS TRIGGER TO START" : "HOLD TRIGGER TO DRIVE");

        Debug.Log(
            $"KAT Husky Controller started. " +
            $"Robot control enabled: {enableRobotControl}, " +
            $"movement activation: {(activationEnabled ? "ON" : "OFF")}"
        );
    }

    private void FixedUpdate()
    {
        string sn = katSerialNumber != null ? katSerialNumber.Trim() : "";
        RefreshDeviceSelectionIfDue(sn);

        // Match the official KAT SDK demos: read the first active treadmill stream.
        // Some KAT service versions report disconnected when the serial-number overload is used.
        KATNativeSDK.TreadMillData data = KATNativeSDK.GetWalkStatus();
        string querySerial = string.IsNullOrEmpty(activeSerialNumber) ? "(first device)" : activeSerialNumber;

        if (
            data.connected &&
            requireConfiguredSerialNumber &&
            !string.IsNullOrEmpty(sn) &&
            !string.Equals(activeSerialNumber, sn, System.StringComparison.OrdinalIgnoreCase)
        )
        {
            LogWrongDeviceIfDue(sn);
            ResetMotionFilters();
            PublishStopIfDue();
            return;
        }

        if (!data.connected)
        {
            LogDisconnectedIfDue();
            ResetMotionFilters();
            PublishStopIfDue();
            return;
        }

        float bodyAngle =
            data.bodyRotationRaw.eulerAngles.y;

        bool reverseModeActive =
            enableReverseModeButton &&
            IsReverseModeRequested();

        float rawForwardSpeed =
            data.moveSpeed.z;

        float smoothingTime =
            Mathf.Abs(rawForwardSpeed) > Mathf.Abs(filteredForwardSpeed)
                ? forwardAttackTime
                : forwardReleaseTime;

        float forwardBlend = 1f - Mathf.Exp(
            -Time.fixedDeltaTime / Mathf.Max(smoothingTime, 0.01f)
        );

        filteredForwardSpeed = Mathf.Lerp(
            filteredForwardSpeed,
            rawForwardSpeed,
            forwardBlend
        );

        float forwardSpeed =
            reverseModeActive ?
            -filteredForwardSpeed :
            filteredForwardSpeed;

        float rawYawRateDegPerSec = 0f;
        if (hasPreviousBodyAngle)
        {
            float deltaDeg = Mathf.DeltaAngle(previousBodyAngle, bodyAngle);
            rawYawRateDegPerSec = deltaDeg / Time.fixedDeltaTime;
        }
        previousBodyAngle = bodyAngle;
        hasPreviousBodyAngle = true;

        float yawBlend = 1f - Mathf.Exp(
            -Time.fixedDeltaTime / Mathf.Max(yawRateSmoothingTime, 0.01f)
        );
        filteredYawRateDegPerSec = Mathf.Lerp(
            filteredYawRateDegPerSec,
            rawYawRateDegPerSec,
            yawBlend
        );

        if (Input.GetKeyDown(KeyCode.C))
        {
            ResetMotionFilters();
            Debug.Log("KAT motion filters reset.");
        }

        float linear =
            CalculateLinearVelocity(forwardSpeed);

        float angular =
            CalculateAngularVelocity(
                forwardSpeed,
                filteredYawRateDegPerSec
            );

        LogDataIfDue(
            data,
            forwardSpeed,
            bodyAngle,
            filteredYawRateDegPerSec,
            linear,
            angular,
            querySerial
        );

        if (enableEmergencyStopButton && IsEmergencyStopRequested())
        {
            currentStatusText = "EMERGENCY STOP";
            PublishStopIfDue();
            return;
        }

        if (!enableRobotControl)
        {
            currentStatusText = "ROBOT CONTROL OFF";
            PublishStopIfDue();
            return;
        }

        if (!IsActivationRequested())
        {
            currentStatusText = activationLatch
                ? "PRESS TRIGGER TO START"
                : "HOLD TRIGGER TO DRIVE";
            PublishStopIfDue();
            return;
        }

        currentStatusText = reverseModeActive ? "ACTIVE / REVERSE" : "ACTIVE";

        PublishVelocityIfDue(
            linear,
            angular
        );
    }

    private float CalculateLinearVelocity(
        float forwardSpeed
    )
    {
        float magnitude = Mathf.Abs(forwardSpeed);

        if (magnitude < walkDeadZone)
        {
            return 0f;
        }

        // Map the useful KAT walking range to the whole robot-speed range.
        // The exponent below 1 boosts gentle walking without changing the stop dead zone.
        float inputRange = Mathf.Max(inputSpeedForMax - walkDeadZone, 0.001f);
        float normalizedInput = Mathf.Clamp01((magnitude - walkDeadZone) / inputRange);
        float response = Mathf.Pow(normalizedInput, responseExponent);
        float maxSpeed = forwardSpeed >= 0f ? maxForwardSpeed : maxReverseSpeed;
        float linearMagnitude = Mathf.Lerp(
            Mathf.Min(minDriveSpeed, maxSpeed),
            maxSpeed,
            response
        );
        float linear = forwardSpeed >= 0f ? linearMagnitude : -linearMagnitude;

        if (enableDebugLog && Mathf.Abs(forwardSpeed) > 0.01f)
        {
            Debug.Log($"[LINEAR DEBUG] forwardSpeed={forwardSpeed:F3}, linear={linear:F3}");
        }

        return linear;
    }

    private float CalculateAngularVelocity(float forwardSpeed, float yawRateDegPerSec)
    {
        if (turnOnlyWhileWalking && Mathf.Abs(forwardSpeed) < walkDeadZone)
        {
            return 0f;
        }

        float rate = yawRateDegPerSec;
        if (Mathf.Abs(rate) < yawRateDeadZoneDegPerSec)
        {
            return 0f;
        }

        rate -= Mathf.Sign(rate) * yawRateDeadZoneDegPerSec;
        float angular = rate * Mathf.Deg2Rad * turnGain;
        angular = Mathf.Clamp(angular, -maxAngularSpeed, maxAngularSpeed);

        if (invertAngular)
        {
            angular *= -1f;
        }

        return angular;
    }

    private void ResetMotionFilters()
    {
        filteredForwardSpeed = 0f;
        filteredYawRateDegPerSec = 0f;
        hasPreviousBodyAngle = false;
    }

    private void OnGUI()
    {
        if (!showStatusGUI)
        {
            return;
        }

        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = 20;
        style.fontStyle = FontStyle.Bold;
        style.normal.textColor = Color.white;
        style.alignment = TextAnchor.UpperLeft;

        GUI.Label(
            new Rect(20f, 20f, 520f, 40f),
            $"KAT STATUS: {currentStatusText}",
            style
        );

        GUI.Label(
            new Rect(20f, 55f, 620f, 40f),
            $"Trigger: {currentTriggerValue:F2}   YawRate: {filteredYawRateDegPerSec:F1} deg/s   cmd_vel: linear={lastPublishedLinear:F3}, angular={lastPublishedAngular:F3}",
            style
        );
    }

    private bool IsActivationRequested()
    {
        if (!enableActivationButton)
        {
            return true;
        }

        bool currentButtonState = false;
        var device = InputDevices.GetDeviceAtXRNode(activationHand);
        currentTriggerValue = 0f;

        if (device.isValid)
        {
            if (device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue))
            {
                currentTriggerValue = triggerValue;
                currentButtonState = triggerValue > activationTriggerThreshold;
            }
            else
            {
                device.TryGetFeatureValue(CommonUsages.triggerButton, out currentButtonState);
                currentTriggerValue = currentButtonState ? 1f : 0f;
            }
        }

        if (!activationLatch && enableActivationKeyboardFallback)
        {
            currentButtonState |= Input.GetKey(activationKey);
            if (Input.GetKey(activationKey))
            {
                currentTriggerValue = 1f;
            }
        }

        if (activationLatch)
        {
            if (currentButtonState && !lastActivationButtonState)
            {
                activationEnabled = !activationEnabled;
                Debug.Log($"Movement activation {(activationEnabled ? "ON" : "OFF")}.");
            }
        }
        else
        {
            activationEnabled = currentButtonState;
        }

        if (activationLatch && enableActivationKeyboardFallback && Input.GetKeyDown(activationKey))
        {
            activationEnabled = !activationEnabled;
            Debug.Log($"Movement activation {(activationEnabled ? "ON" : "OFF")} by keyboard.");
        }

        lastActivationButtonState = currentButtonState;
        return activationEnabled;
    }

    private bool IsEmergencyStopRequested()
    {
        if (!enableEmergencyStopButton)
        {
            return false;
        }

        bool currentButtonState = false;
        var device = InputDevices.GetDeviceAtXRNode(emergencyStopHand);

        if (device.isValid)
        {
            device.TryGetFeatureValue(CommonUsages.primaryButton, out currentButtonState);
        }

        bool requested = false;

        if (emergencyStopLatch)
        {
            if (currentButtonState && !lastEmergencyStopButtonState)
            {
                emergencyStopEngaged = !emergencyStopEngaged;
                Debug.Log($"Emergency stop {(emergencyStopEngaged ? "ENGAGED" : "RELEASED")}.");
            }

            requested = emergencyStopEngaged;
        }
        else
        {
            requested = currentButtonState;
        }

        if (enableKeyboardFallback && Input.GetKeyDown(emergencyStopKey))
        {
            emergencyStopEngaged = !emergencyStopEngaged;
            requested = emergencyStopEngaged;
            Debug.Log($"Emergency stop {(emergencyStopEngaged ? "ENGAGED" : "RELEASED")} by keyboard.");
        }

        lastEmergencyStopButtonState = currentButtonState;
        return requested;
    }

    private bool IsReverseModeRequested()
    {
        if (!enableReverseModeButton)
        {
            return false;
        }

        bool currentButtonState = false;
        var device = InputDevices.GetDeviceAtXRNode(reverseModeHand);

        if (device.isValid)
        {
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out currentButtonState);
        }

        if (reverseModeLatch)
        {
            if (currentButtonState && !lastReverseModeButtonState)
            {
                reverseModeEnabled = !reverseModeEnabled;
                Debug.Log($"Reverse mode {(reverseModeEnabled ? "ENABLED" : "DISABLED")}.");
            }
        }
        else
        {
            reverseModeEnabled = currentButtonState;
        }

        if (enableKeyboardFallback && Input.GetKeyDown(reverseModeKey))
        {
            reverseModeEnabled = !reverseModeEnabled;
            Debug.Log($"Reverse mode {(reverseModeEnabled ? "ENABLED" : "DISABLED")} by keyboard.");
        }

        lastReverseModeButtonState = currentButtonState;
        return reverseModeEnabled;
    }

    private void PublishVelocityIfDue(
        float linear,
        float angular
    )
    {
        if (Time.unscaledTime < nextPublishTime)
        {
            return;
        }

        nextPublishTime =
            Time.unscaledTime +
            1f / Mathf.Max(publishRateHz, 1f);

        PublishVelocity(
            linear,
            angular
        );
    }

    private void PublishStopIfDue()
    {
        if (Time.unscaledTime < nextPublishTime)
        {
            return;
        }

        nextPublishTime =
            Time.unscaledTime +
            1f / Mathf.Max(publishRateHz, 1f);

        PublishVelocity(
            0f,
            0f
        );
    }

    private void PublishVelocity(
        float linear,
        float angular
    )
    {
        TwistMsg msg = new TwistMsg
        {
            linear = new Vector3Msg
            {
                x = linear,
                y = 0.0,
                z = 0.0
            },
            angular = new Vector3Msg
            {
                x = 0.0,
                y = 0.0,
                z = angular
            }
        };

        // Safety: if robot control is disabled, force zero velocities.
        if (!enableRobotControl)
        {
            msg.linear.x = 0.0;
            msg.angular.z = 0.0;
        }

        if (ros != null)
        {
            ros.Publish(cmdVelTopic, msg);
        }

        lastPublishedLinear = (float)msg.linear.x;
        lastPublishedAngular = (float)msg.angular.z;
    }

    private void LogDataIfDue(
        KATNativeSDK.TreadMillData data,
        float forwardSpeed,
        float bodyAngle,
        float yawRateDegPerSec,
        float linear,
        float angular,
        string querySerial
    )
    {
        if (!enableDebugLog)
        {
            return;
        }

        if (Time.unscaledTime < nextLogTime)
        {
            return;
        }

        nextLogTime =
            Time.unscaledTime +
            Mathf.Max(debugLogInterval, 0.05f);

        Debug.Log(
            $"KAT | " +
            $"QuerySN={querySerial} | " +
            $"Connected={data.connected} | " +
            $"Forward={forwardSpeed:F2} | " +
            $"Body={bodyAngle:F1} deg | " +
            $"YawRate={yawRateDegPerSec:F1} deg/s | " +
            $"Linear={linear:F3} m/s | " +
            $"Angular={angular:F3} rad/s | " +
            $"RobotEnabled={enableRobotControl}"
        );
    }

    private void RefreshDeviceSelectionIfDue(string configuredSerialNumber)
    {
        if (Time.unscaledTime < nextDeviceScanTime)
        {
            return;
        }

        nextDeviceScanTime = Time.unscaledTime + 1f;
        activeSerialNumber = "";

        int count = KATNativeSDK.DeviceCount();

        if (enableDebugLog)
        {
            Debug.Log($"KAT DeviceCount={count}");
        }

        for (uint i = 0; i < count; i++)
        {
            var device = KATNativeSDK.GetDevicesDesc(i);

            if (
                i == 0 ||
                string.Equals(device.serialNumber, configuredSerialNumber, System.StringComparison.OrdinalIgnoreCase)
            )
            {
                activeSerialNumber = device.serialNumber;
            }

            if (enableDebugLog)
            {
                Debug.Log(
                    $"KAT Device[{i}] Name={device.device} SN={device.serialNumber} Busy={device.isBusy}"
                );
            }

            if (
                forceConnectWhenBusy &&
                device.isBusy &&
                !string.IsNullOrEmpty(configuredSerialNumber) &&
                string.Equals(device.serialNumber, configuredSerialNumber, System.StringComparison.OrdinalIgnoreCase)
            )
            {
                Debug.LogWarning($"KAT device {configuredSerialNumber} is busy. Calling ForceConnect.");
                KATNativeSDK.ForceConnect(configuredSerialNumber);
            }
        }
    }

    private void LogWrongDeviceIfDue(string expectedSerialNumber)
    {
        if (!enableDebugLog)
        {
            return;
        }

        if (Time.unscaledTime < nextLogTime)
        {
            return;
        }

        nextLogTime =
            Time.unscaledTime +
            Mathf.Max(debugLogInterval, 0.05f);

        Debug.LogWarning(
            $"KAT connected device SN is '{activeSerialNumber}', expected '{expectedSerialNumber}'. " +
            "Publishing zero velocity."
        );
    }

    private void LogDisconnectedIfDue()
    {
        if (!enableDebugLog)
        {
            return;
        }

        if (Time.unscaledTime < nextLogTime)
        {
            return;
        }

        nextLogTime =
            Time.unscaledTime +
            Mathf.Max(debugLogInterval, 0.05f);

        Debug.LogWarning(
            "KAT device is not connected. " +
            "Publishing zero velocity."
        );
    }

    private void OnDisable()
    {
        if (ros != null)
        {
            PublishVelocity(
                0f,
                0f
            );
        }
    }

    private void OnApplicationQuit()
    {
        if (ros != null)
        {
            PublishVelocity(
                0f,
                0f
            );
        }
    }
}
