using FlaxEngine;

namespace Game.Game.Trials;

/// <summary>
/// 浮力物体: 与水面交互 (入水浪花、移动尾迹) 并受浮力/水阻影响。
/// 挂载在带有 RigidBody 的物体上 (或其父级)。
/// </summary>
public class BuoyantObject : Script
{
    [Tooltip("Object radius in world units (interaction & submersion scale)")]
    public float Radius = 50.0f;

    [Tooltip("Mass factor (heavier = bigger splash)")]
    public float MassFactor = 1.0f;

    [Tooltip("Splash strength on water entry")]
    public float SplashStrength = 2.0f;

    [Tooltip("Wake strength while moving underwater")]
    public float WakeStrength = 0.5f;

    [Tooltip("Buoyancy multiplier (1 = neutral buoyancy, >1 floats, <1 sinks)")]
    public float BuoyancyFactor = 1.2f;

    [Tooltip("Gravity magnitude to counteract (match scene gravity)")]
    public float GravityMagnitude = 981.0f;

    [Tooltip("Water drag coefficient (velocity damping underwater)")]
    public float WaterDrag = 2.0f;

    private RigidBody _rigidbody;
    private bool _wasUnderwater;

    public override void OnStart()
    {
        // 兼容: 脚本挂在 RigidBody 上, 或挂在带 RigidBody 子物体的父级上
        _rigidbody = Actor as RigidBody ?? Actor.GetChild<RigidBody>();
        _wasUnderwater = false;
    }

    public override void OnFixedUpdate()
    {
        var water = InteractiveWater.Instance;
        if (water == null)
            return;

        Vector3 pos = Actor.Position;
        // 水面高度 (基准 + 高度场缓存)
        float waterY = water.GetWaterHeight(pos);
        bool isUnderwater = pos.Y < waterY;

        if (_rigidbody != null)
        {
            Vector3 velocity = _rigidbody.LinearVelocity;

            // 入水瞬间: 按冲击速度注入径向冲量 (浪花)
            if (isUnderwater && !_wasUnderwater)
            {
                float impactSpeed = Mathf.Abs(velocity.Y);
                float strength = Mathf.Clamp(impactSpeed * MassFactor * SplashStrength * 0.05f, 0.5f, 20.0f);
                water.AddRadialImpulse(pos, strength, Radius);
            }
            // 水下移动: 沿速度方向施加方向力 (尾迹)
            else if (isUnderwater && Engine.FrameCount % 10 == 0)
            {
                float speed = Mathf.Max(velocity.Length, 1.0f);
                float strength = Mathf.Clamp(speed * MassFactor * WakeStrength * 0.02f, 0.0f, 5.0f);
                var dir = new Float2(velocity.X, -velocity.Z);
                if (dir.Length > 0.001f)
                    water.AddDirectionalForce(pos, dir.Normalized, strength, Radius * 0.7f);
            }

            // 浮力 + 水阻 (简化阿基米德, 按质量缩放以自动平衡重力)
            if (isUnderwater)
            {
                float submerged = Mathf.Clamp((waterY - pos.Y) / (Radius * 2.0f), 0.0f, 1.0f);
                float mass = _rigidbody.Mass;
                // 浮力 = 质量 × 重力 × 浮力系数 × 浸没比例 (系数>1 时大于重力 → 上浮)
                Vector3 buoyancy = Vector3.Up * (mass * GravityMagnitude * BuoyancyFactor * submerged);
                // 水阻按质量缩放 → 减速度与质量无关
                Vector3 drag = -velocity * (WaterDrag * submerged * mass);
                _rigidbody.AddForce(buoyancy + drag);
            }
        }

        _wasUnderwater = isUnderwater;
    }
}
