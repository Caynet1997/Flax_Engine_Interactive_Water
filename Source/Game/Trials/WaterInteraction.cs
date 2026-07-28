using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 交互模式
/// </summary>
public enum WaterInteractionMode
{
    RadialImpulse,   // 径向冲量 (爆炸/入水)
    Directional,     // 方向力 (水流/风)
    Vortex,          // 漩涡 (旋转力场)
    Attractor,       // 吸引/排斥
    HeightModifier,  // 高度修改 (注水/排水)
    FoamSource,      // 泡沫注入
}

/// <summary>
/// 通用水面交互组件: 挂载到任何 Actor, 持续或触发式地对水面施加力。
/// 支持径向冲量、方向力、漩涡、吸引/排斥、高度修改、泡沫注入。
/// </summary>
public class WaterInteraction : Script
{
    [Tooltip("Interaction mode")]
    public WaterInteractionMode Mode = WaterInteractionMode.RadialImpulse;

    [Tooltip("Interaction strength")]
    public float Strength = 5.0f;

    [Tooltip("Interaction radius (world units)")]
    public float Radius = 20.0f;

    [Tooltip("Continuous: apply every frame. Otherwise: apply only on enable (one-shot).")]
    public bool Continuous = true;

    [Tooltip("Direction for Directional mode (XZ plane, will be normalized)")]
    public Float2 Direction = new Float2(1.0f, 0.0f);

    public override void OnEnable()
    {
        // 非持续模式: 启用时施加一次
        if (!Continuous)
            ApplyForce();
    }

    public override void OnUpdate()
    {
        if (!Continuous)
            return;

        ApplyForce();
    }

    private void ApplyForce()
    {
        var water = InteractiveWater.Instance;
        if (water == null)
            return;

        Vector3 pos = Actor.Position;

        switch (Mode)
        {
            case WaterInteractionMode.RadialImpulse:
                water.AddRadialImpulse(pos, Strength, Radius);
                break;
            case WaterInteractionMode.Directional:
                water.AddDirectionalForce(pos, Direction, Strength, Radius);
                break;
            case WaterInteractionMode.Vortex:
                water.AddVortex(pos, Strength, Radius);
                break;
            case WaterInteractionMode.Attractor:
                water.AddAttractor(pos, Strength, Radius);
                break;
            case WaterInteractionMode.HeightModifier:
                water.AddHeightModifier(pos, Strength, Radius);
                break;
            case WaterInteractionMode.FoamSource:
                water.AddFoamSource(pos, Strength, Radius);
                break;
        }
    }
}
