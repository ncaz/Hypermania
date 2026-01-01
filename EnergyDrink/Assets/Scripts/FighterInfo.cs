using UnityEngine;

public enum FighterState
{
    Idle,
    Walking,
    Dashing,
    Jumping,
    Falling,
    Stunned,
    Attacking,
    Grabbing
}

public struct FighterInfo
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Speed;
    public FighterState State;
    public int StateFrameCount;
    public Vector2 FacingDirection;

    public FighterInfo(Vector2 position, Vector2 velocity, float speed, FighterState state, int stateFrameCount, Vector2 facingDirection)
    {
        Position = position;
        Velocity = velocity;
        Speed = speed;
        State = state;
        StateFrameCount = stateFrameCount;
        FacingDirection = facingDirection;
    }

    public void HandleInput(Input input)
    {
        // Horizontal movement
        Velocity.x = 0;
        if (input.Flags.HasFlag(InputFlags.Left))
            Velocity.x = -Speed;
        if (input.Flags.HasFlag(InputFlags.Right))
            Velocity.x = Speed;

        // Vertical movement only if grounded
        if (input.Flags.HasFlag(InputFlags.Up) && Position.y <= Globals.GROUND)
        {
            Velocity.y = Speed * 1.5f;
        }
        UpdatePhysics();
    }

    public void UpdatePhysics()
    {
        // Apply gravity if not grounded
        if (Position.y > Globals.GROUND || Velocity.y > 0)
        {
            Velocity.y += Globals.GRAVITY * 1 / 60;
        }

        // Update Position
        Position += Velocity * 1 / 60;

        // Floor collision
        if (Position.y <= Globals.GROUND)
        {
            Position.y = Globals.GROUND;

            // Only zero vertical velocity if falling
            if (State == FighterState.Falling)
                Velocity.y = 0;
        }

        UpdateState();
    }

    public void UpdateState()
    {
        if (Velocity.y > 0)
        {
            State = FighterState.Jumping;
        }
        else if (Velocity.y < 0)
        {
            State = FighterState.Falling;
        }
        else if (Velocity.x != 0)
        {
            State = FighterState.Walking;
        }
        else
        {
            State = FighterState.Idle;
        }
    }
}
