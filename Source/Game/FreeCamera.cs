using FlaxEngine;

namespace Game.Game;

public class FreeCamera : Script
{
    [Limit(0, 100), Tooltip("Camera movement speed factor")]
    public float MoveSpeed { get; set; } = 4;

    [Tooltip("Camera rotation smoothing factor")]
    public float CameraSmoothing { get; set; } = 20.0f;

    private float _pitch;
    private float _yaw;

    public override void OnStart()
    {
        Float3 initialEulerAngles = Actor.Orientation.EulerAngles;
        _pitch = initialEulerAngles.X;
        _yaw = initialEulerAngles.Y;
    }

    public override void OnUpdate()
    {
        if (Input.Mouse.GetButton(MouseButton.Right))
        {
            var mouseDelta = new Float2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
            _pitch = Mathf.Clamp(_pitch + mouseDelta.Y, -88, 88);
            _yaw += mouseDelta.X;
        }
    }

    public override void OnFixedUpdate()
    {
        Transform camTrans = Actor.Transform;
        float camFactor = Mathf.Saturate(CameraSmoothing * Time.DeltaTime);

        camTrans.Orientation = Quaternion.Lerp(camTrans.Orientation, Quaternion.Euler(_pitch, _yaw, 0), camFactor);

        float inputH = Input.GetAxis("Horizontal");
        float inputV = Input.GetAxis("Vertical");
        Vector3 move = new Vector3(inputH, 0.0f, inputV);
        move.Normalize();
        move = camTrans.TransformDirection(move);

        camTrans.Translation += move * MoveSpeed;

        Actor.Transform = camTrans;
    }
}