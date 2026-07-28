using FlaxEngine;
using FlaxEngine.Utilities;

namespace Game.Game;

public class SimpleMoving : Script
{
    public Vector2 MoveRange = new(4000f, 4000f);
    public float MoveSpeed = 120f;
    public float TurnSpeed = 120f;
    public float TurnInterval = 3f;
    public Vector3 CurDirection = Vector3.Forward;
    public Vector3 TargetDirection = Vector3.Forward;

    private float _turnInterval = 3f;

    public override void OnUpdate()
    {
        if (_turnInterval <= 0)
        {
            // Generate random direction and normalize it
            Vector3 randomDir = new Vector3(RandomUtil.Random.NextFloat(-1f,1f), 0f, RandomUtil.Random.NextFloat(-1f,1f));
            if (randomDir != Vector3.Zero)
                randomDir.Normalize();
            else
                randomDir = Vector3.Forward;
            TargetDirection = randomDir;
            _turnInterval = TurnInterval;
        }
        _turnInterval -= Time.DeltaTime;

        Vector3 pos = Actor.Position;
        bool clamped = false;
        // Clamp position to range
        if (pos.X > MoveRange.X) { pos.X = MoveRange.X; clamped = true; }
        else if (pos.X < -MoveRange.X) { pos.X = -MoveRange.X; clamped = true; }
        if (pos.Z > MoveRange.Y) { pos.Z = MoveRange.Y; clamped = true; }
        else if (pos.Z < -MoveRange.Y) { pos.Z = -MoveRange.Y; clamped = true; }
        if (clamped)
        {
            // Adjust target direction only for axes that were clamped to point inward
            if (pos.X >= MoveRange.X) TargetDirection.X = -Mathf.Abs(TargetDirection.X);
            else if (pos.X <= -MoveRange.X) TargetDirection.X = Mathf.Abs(TargetDirection.X);
            if (pos.Z >= MoveRange.Y) TargetDirection.Z = -Mathf.Abs(TargetDirection.Z);
            else if (pos.Z <= -MoveRange.Y) TargetDirection.Z = Mathf.Abs(TargetDirection.Z);
            // Ensure direction is not zero
            if (TargetDirection.X == 0 && TargetDirection.Z == 0)
            {
                // Set direction towards center
                Vector3 toCenter = -pos;
                toCenter.Y = 0;
                if (toCenter != Vector3.Zero)
                    TargetDirection = toCenter.Normalized;
                else
                    TargetDirection = Vector3.Forward;
            }
            // Re-normalize to keep unit length (though changing sign doesn't affect length, but if we used Abs, length unchanged)
            // But if we set toCenter, it's normalized.
        }

        CurDirection = Vector3.MoveTowards(CurDirection, TargetDirection, TurnSpeed * Time.DeltaTime);
        // Ensure CurDirection is normalized? MoveTowards may produce non-unit if TargetDirection is not unit, but we keep unit.
        // To be safe, normalize CurDirection if needed, but since both are unit, it should stay unit.
        Vector3 targetPosition = pos + MoveSpeed * CurDirection * Time.DeltaTime;
        Actor.Position = targetPosition;
    }

}
