using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 鼠标水面交互: 射线检测水面命中点, 施加径向冲量。
/// 独立于 InteractiveWater, 挂载到场景中任意 Actor 即可启用鼠标交互。
/// </summary>
public class MouseWaterInteraction : Script
{
    [Tooltip("Mouse interaction radius (world units)")]
    public float Radius = 20.0f;

    [Tooltip("Mouse interaction strength")]
    public float Strength = 5.0f;

    [Tooltip("Mouse button to use")]
    public MouseButton Button = MouseButton.Left;

    public override void OnUpdate()
    {
        var water = InteractiveWater.Instance;
        if (water == null)
            return;

        if (!Input.GetMouseButton(Button))
            return;

        var mainCam = Camera.MainCamera;
        if (mainCam == null)
            return;

        var ray = mainCam.ConvertMouseToRay(Input.MousePosition);
        if (Physics.RayCast(ray.Position, ray.Direction, out var hitInfo))
        {
            water.AddRadialImpulse(hitInfo.Point, Strength, Radius);
        }
    }
}
