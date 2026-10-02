using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public class RawdataDemo : MonoBehaviour
{
    public float consoleLogInterval = 0.1f;

    private byte[] previousExtraData;
    private float nextConsoleLogTime;

    void FixedUpdate()
    {
        var data = KATNativeSDK.GetWalkStatus();
        float horizontalSpeed = new Vector2(data.moveSpeed.x, data.moveSpeed.z).magnitude;
        var str = new StringBuilder();
        str.AppendLine("MINI S SDK DIAGNOSTIC");
        str.AppendLine("Device: " + data.deviceName);
        str.AppendLine("Connected: " + data.connected);
        str.AppendLine("Body Rotation: " + data.bodyRotationRaw.ToString("f4"));
        str.AppendLine("SDK moveSpeed: " + data.moveSpeed.ToString("f4"));
        str.AppendLine("Horizontal speed: " + horizontalSpeed.ToString("F4")
            + (horizontalSpeed > 0.001f ? "  <color=green>MOVING</color>" : "  <color=red>ZERO</color>"));

        AppendMiniSExtraData(str, data, out string extraLog);

        var text = GetComponent<Text>();
        text.text = str.ToString();

        if (Time.realtimeSinceStartup >= nextConsoleLogTime)
        {
            nextConsoleLogTime = Time.realtimeSinceStartup + Mathf.Max(consoleLogInterval, 0.05f);
            Debug.Log(
                $"KAT MINI S DIAG | t={Time.realtimeSinceStartup:F3} Device={data.deviceName} Connected={data.connected} " +
                $"KatStamp={data.lastUpdateTimePoint:R} BodyYaw={data.bodyRotationRaw.eulerAngles.y:F1} " +
                $"MoveSpeed=({data.moveSpeed.x:F4},{data.moveSpeed.y:F4},{data.moveSpeed.z:F4}) " +
                $"Horizontal={horizontalSpeed:F4} {extraLog}"
            );
        }
    }

    private void AppendMiniSExtraData(StringBuilder str, KATNativeSDK.TreadMillData data, out string extraLog)
    {
        if (data.extraData == null)
        {
            str.AppendLine("Extra data: NULL");
            previousExtraData = null;
            extraLog = "Extra=NULL";
            return;
        }

        int nonZeroBytes = 0;
        int changedBytes = 0;
        uint signature = 2166136261;
        for (int i = 0; i < data.extraData.Length; i++)
        {
            byte value = data.extraData[i];
            if (value != 0) nonZeroBytes++;
            if (previousExtraData != null && i < previousExtraData.Length && value != previousExtraData[i]) changedBytes++;
            signature = (signature ^ value) * 16777619;
        }
        str.AppendLine($"Extra bytes: nonzero={nonZeroBytes} changed={changedBytes} signature={signature:X8}");

        previousExtraData = (byte[])data.extraData.Clone();

        if (data.deviceName == null
            || data.deviceName.IndexOf("Mini", StringComparison.OrdinalIgnoreCase) < 0)
        {
            str.AppendLine("Mini S parser: device name is not Mini");
            extraLog = $"ExtraNonZero={nonZeroBytes} ExtraChanged={changedBytes} ExtraSignature={signature:X8} MiniParser=SKIPPED";
            return;
        }

        try
        {
            var info = MiniSExtraData.GetExtraInfoMiniS(data);
            str.AppendLine($"MiniS: isMoving={info.isMoving} isForward={info.isForward}");
            str.AppendLine($"motionType={info.motionType} action={info.action}");
            str.AppendLine($"Ground L/R={info.isLeftGround}/{info.isRightGround}  Static L/R={info.isLeftStatic}/{info.isRightStatic}");
            str.AppendLine("Skating: " + info.skatingSpeed.ToString("F3"));
            str.AppendLine("Left foot: " + info.lFootSpeed.ToString("F3"));
            str.AppendLine("Right foot: " + info.rFootSpeed.ToString("F3"));
            extraLog = $"ExtraNonZero={nonZeroBytes} ExtraChanged={changedBytes} ExtraSignature={signature:X8} " +
                $"IsMoving={info.isMoving} IsForward={info.isForward} MotionType={info.motionType} Action={info.action} " +
                $"GroundL={info.isLeftGround} GroundR={info.isRightGround} StaticL={info.isLeftStatic} StaticR={info.isRightStatic} " +
                $"Skating=({info.skatingSpeed.x:F3},{info.skatingSpeed.y:F3},{info.skatingSpeed.z:F3}) " +
                $"LeftFoot=({info.lFootSpeed.x:F3},{info.lFootSpeed.y:F3},{info.lFootSpeed.z:F3}) " +
                $"RightFoot=({info.rFootSpeed.x:F3},{info.rFootSpeed.y:F3},{info.rFootSpeed.z:F3})";
        }
        catch (Exception exception)
        {
            str.AppendLine("Mini S parser failed: " + exception.GetType().Name);
            extraLog = $"ExtraNonZero={nonZeroBytes} ExtraChanged={changedBytes} ExtraSignature={signature:X8} MiniParserError={exception.GetType().Name}";
        }
    }
}
