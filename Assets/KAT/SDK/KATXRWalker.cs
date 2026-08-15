using System;
using UnityEngine;
using UnityEngine.XR;

public class KATXRWalker : MonoBehaviour
{
    public GameObject xr;
    public GameObject eye;

    public enum ExecuteMethod
    {
        RigidBody,
        CharactorController,
        MovePosition
    }
    public ExecuteMethod executeMethod = ExecuteMethod.RigidBody;

    [Header("SpeedSettings")]
    [Range(0.5f, 10.0f)]
    public float speedMul = 1.0f;
    public enum SpeedMode
    {
        [Tooltip("Movement speed increases as actual speed increases")]
        linear,
        [Tooltip("Movement speed remains constant")]
        constant
    }
    [Tooltip("Character movement speed mode")]
    public SpeedMode speedMode = SpeedMode.linear;
    [Range(0.0f, 6.0f)]
    [Tooltip("Movement speed in constant mode")]
    public float constantSpeed = 2.0f;
    [Range(0.0f, 6.0f)]
    [Tooltip("Only when the actual speed exceeds this value will it move")]
    public float constantSpeedThreshold = 1.0f;

    protected Vector3 lastPosition = Vector3.zero;
    //protected Vector3 defaultEyeOffset = Vector3.zero;

    protected float yawCorrection;

    protected new Transform transform
    {
        get { return base.transform; }
    }

    public static bool force_calibrate = false;

    void Awake()
    {
        Application.targetFrameRate = 60;
    }

    void Start()
    {
        
    }

    void OnDestroy()
    {

    }

    void FixedUpdate()
    {
        var ws = KATNativeSDK.GetWalkStatus();

        if (!ws.connected)
        {
            return;
        }

        //Calibration Stage 
        var lastCalibrationTime = KATNativeSDK.GetLastCalibratedTimeEscaped();                 //Get last calibration time as double
        var rigidBody = GetComponent<Rigidbody>();

        //btnPressed means user pressed button on sensor, by default is calibration request
        if (ws.deviceDatas[0].btnPressed || lastCalibrationTime < 0.08 || force_calibrate)                                               //Check if need calibration
        {
            var hmdYaw = eye.transform.eulerAngles.y;
            var bodyYaw = ws.bodyRotationRaw.eulerAngles.y;

            yawCorrection = bodyYaw - hmdYaw;

            var pos = transform.position;
            var eyePos = eye.transform.position;
            pos.x = eyePos.x;
            pos.z = eyePos.z;
            transform.position = pos;
            lastPosition = transform.position;
            rigidBody.velocity = Vector3.zero;
            force_calibrate = false;
            
            return;
        }

        transform.rotation = ws.bodyRotationRaw * Quaternion.Inverse( Quaternion.Euler(new Vector3(0,yawCorrection,0)));

        switch (speedMode)
        {
            case SpeedMode.linear:
                break;
            case SpeedMode.constant:
                if (ws.moveSpeed.magnitude >= constantSpeedThreshold)
                    ws.moveSpeed = ws.moveSpeed.normalized * constantSpeed;
                else
                    ws.moveSpeed = Vector3.zero;
                break;
            default:
                break;
        }

        switch(executeMethod)
        {
            case ExecuteMethod.CharactorController: 
                {
                    var ch = GetComponent<CharacterController>();
                    ch.SimpleMove(transform.rotation * ws.moveSpeed);
                }
                break;
            case ExecuteMethod.MovePosition:
                {
                    transform.position += (transform.rotation * ws.moveSpeed * Time.fixedDeltaTime);
                }
                break;
            case ExecuteMethod.RigidBody:
                {
                    rigidBody.velocity = transform.rotation * ws.moveSpeed;
                }
                break;
        }
    }


    void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            KATWifiLatencyHelper.OnDisableWifiLowLatencyTest();
        }
        else
        {
            KATWifiLatencyHelper.OnEnableWifiLowLatencyTest();
        }
    }


    void LateUpdate()
    {
        var offset = transform.position - lastPosition;
        offset.y = 0;
        xr.transform.position += offset;

        lastPosition = transform.position;
    }
}
