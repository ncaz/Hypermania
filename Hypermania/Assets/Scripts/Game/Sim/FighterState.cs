using System;
using Design;
using Design.Animation;
using MemoryPack;
using UnityEngine;
using Utils;
using Utils.SoftFloat;

namespace Game.Sim
{
    public enum FighterMode
    {
        Neutral,
        Attacking,
        Hitstun,
        Blockstun,
        Knockdown,
    }

    public enum FighterFacing
    {
        Left,
        Right,
    }

    public enum FighterLocation
    {
        Grounded,
        Airborne,
    }

    [MemoryPackable]
    public partial struct FighterState
    {
        public SVector2 Position;
        public SVector2 Velocity;
        public sfloat Health;

        /// <summary>
        /// The animation state of the chararcter, indicates which animation is currently playing.
        /// </summary>
        public CharacterAnimation AnimState { get; private set; }
        public Frame AnimSt { get; private set; }

        public FighterMode Mode { get; private set; }

        /// <summary>
        /// The number of ticks remaining for the current mode. If the mode is Neutral or another mode that should last
        /// indefinitely, you can set this value to int.MaxValue.
        /// <br/><br/>
        /// Note that if you perform a transition in the middle of a frame, the value you set to ModeT will depend on
        /// which part of the frame you set it on. In general, if the state transition happens before
        /// physics/projectile/hurtbox calculations, ModeT should be set to the true value: i.e. a move lasting one
        /// frame (which is applied right after inputs) should set ModeT to 1. If the state transition happens after
        /// physics/projectile/hurtbox calculations, you should set ModeT to the true value + 1: i.e. a 1 frame HitStun
        /// applied after physics calculations should set ModeT to 2.
        /// </summary>
        public int ModeT;

        public FighterFacing FacingDir;

        public FighterLocation LastLocation;
        public Frame LocationSt { get; private set; }

        /// <summary>
        /// Whether or not the character is performing an aerial attack. Must be updated in the future to support moves
        /// </summary>
        [MemoryPackIgnore]
        private bool IsAerial => AnimState == CharacterAnimation.LightAerial;

        public static FighterState Create(SVector2 position, FighterFacing facingDirection)
        {
            FighterState state = new FighterState();
            state.Position = position;
            state.Velocity = SVector2.zero;
            state.Mode = FighterMode.Neutral;
            // TODO: character dependent?
            state.Health = 100;
            state.ModeT = int.MaxValue;
            state.FacingDir = facingDirection;
            state.AnimState = CharacterAnimation.Idle;
            state.AnimSt = Frame.FirstFrame;
            return state;
        }

        public FighterLocation Location(GlobalConfig config)
        {
            if (Position.y > (sfloat)config.GroundY)
            {
                return FighterLocation.Airborne;
            }
            return FighterLocation.Grounded;
        }

        public void FaceTowards(SVector2 location)
        {
            // can only switch locations if in neutral
            if (Mode != FighterMode.Neutral)
            {
                return;
            }
            if (location.x < Position.x)
            {
                FacingDir = FighterFacing.Left;
            }
            else
            {
                FacingDir = FighterFacing.Right;
            }
        }

        public void ApplyMovementIntent(GameInput input, CharacterConfig characterConfig, GlobalConfig config)
        {
            if (Mode != FighterMode.Neutral)
            {
                return;
            }
            if (Location(config) == FighterLocation.Grounded)
                Velocity.x = 0;
            if (input.Flags.HasFlag(InputFlags.Left) && Location(config) == FighterLocation.Grounded)
                Velocity.x += (sfloat)(-characterConfig.Speed);
            if (input.Flags.HasFlag(InputFlags.Right) && Location(config) == FighterLocation.Grounded)
                Velocity.x += (sfloat)characterConfig.Speed;
            if (input.Flags.HasFlag(InputFlags.Up) && Location(config) == FighterLocation.Grounded)
                Velocity.y = (sfloat)characterConfig.JumpVelocity;
        }

        public void ApplyActiveState(Frame frame, GameInput input, CharacterConfig characterConfig, GlobalConfig config)
        {
            if (Mode != FighterMode.Neutral)
            {
                return;
            }
            if (input.Flags.HasFlag(InputFlags.LightAttack))
            {
                switch (Location(config))
                {
                    case FighterLocation.Grounded:
                        {
                            Velocity = SVector2.zero;
                            Mode = FighterMode.Attacking;
                            ModeT = characterConfig.LightAttack.TotalTicks;
                            AnimState = CharacterAnimation.LightAttack;
                            AnimSt = frame;
                        }
                        break;
                    case FighterLocation.Airborne:
                        {
                            Mode = FighterMode.Attacking;
                            ModeT = characterConfig.LightAerial.TotalTicks;
                            AnimState = CharacterAnimation.LightAerial;
                            AnimSt = frame;
                        }
                        break;
                }
            }
            else if (input.Flags.HasFlag(InputFlags.SuperAttack))
            {
                switch (Location(config))
                {
                    case FighterLocation.Grounded:
                        {
                            Velocity = SVector2.zero;
                            Mode = FighterMode.Attacking;
                            ModeT = characterConfig.SuperAttack.TotalTicks;
                            AnimState = CharacterAnimation.SuperAttack;
                            AnimSt = frame;
                        }
                        break;
                }
            }
        }

        public void TickStateMachine(Frame frame, GlobalConfig config)
        {
            ModeT--;
            if (ModeT <= 0)
            {
                Mode = FighterMode.Neutral;
                ModeT = int.MaxValue;
            }
            if (LastLocation != Location(config))
            {
                LastLocation = Location(config);
                LocationSt = frame;
            }
        }

        public void UpdatePosition(Frame frame, GlobalConfig config)
        {
            // Apply gravity if not grounded
            if (Position.y > (sfloat)config.GroundY || Velocity.y > 0)
            {
                Velocity.y += (sfloat)config.Gravity * 1 / 64;
            }

            // Update Position
            Position += Velocity * 1 / 64;

            // Floor collision
            if (Position.y <= (sfloat)config.GroundY)
            {
                Position.y = (sfloat)config.GroundY;

                if (Velocity.y < 0)
                    Velocity.y = 0;
            }
            if (Position.x >= (sfloat)config.WallsX)
            {
                Position.x = (sfloat)config.WallsX;
                if (Velocity.x > 0)
                    Velocity.x = 0;
            }
            if (Position.x <= -(sfloat)config.WallsX)
            {
                Position.x = -(sfloat)config.WallsX;
                if (Velocity.x < 0)
                    Velocity.x = 0;
            }
        }

        public void ApplyAerialCancel(Frame frame)
        {
            if (Mode != FighterMode.Attacking)
            {
                return;
            }
            // TODO: apply some landing lag here
            if (IsAerial)
            {
                Mode = FighterMode.Neutral;
                ModeT = int.MaxValue;
                AnimState = CharacterAnimation.Idle;
                AnimSt = frame;
                Velocity = SVector2.zero;
            }
        }

        public void AddBoxes(Frame frame, CharacterConfig config, Physics<BoxProps> physics, int handle)
        {
            int tick = frame - AnimSt;
            FrameData frameData = config.GetFrameData(AnimState, tick);

            foreach (var box in frameData.Boxes)
            {
                SVector2 centerLocal = (SVector2)box.CenterLocal;
                if (FacingDir == FighterFacing.Left)
                {
                    centerLocal.x *= -1;
                }
                SVector2 sizeLocal = (SVector2)box.SizeLocal;
                SVector2 centerWorld = Position + centerLocal;
                BoxProps newProps = box.Props;
                if (FacingDir == FighterFacing.Left)
                {
                    newProps.Knockback.x *= -1;
                }
                physics.AddBox(handle, centerWorld, sizeLocal, newProps);
            }
        }

        public void ApplyHit(BoxProps props)
        {
            if (Mode == FighterMode.Hitstun)
            {
                throw new InvalidOperationException(
                    "Should not be possible to apply hit to a character in hitstun: the animation has no hurtboxes"
                );
            }
            Mode = FighterMode.Hitstun;
            // We add + 1 here: ApplyHit is called after applying inputs but before ticking the state machine. If
            // hitStun = 1, that means we would immediately make the player actionable next frame, so we additionally
            // add 1. See the comments on ModeT for details.
            ModeT = props.HitstunTicks + 1;
            Health -= props.Damage;

            Velocity = (SVector2)props.Knockback;
        }

        public void ApplyClank(GlobalConfig config)
        {
            if (Mode == FighterMode.Hitstun)
            {
                return;
            }
            Mode = FighterMode.Hitstun;
            ModeT = config.ClankTicks;
            Velocity = SVector2.zero;
        }

        public CharacterAnimation ApplyPassiveState(Frame frame, GlobalConfig config)
        {
            CharacterAnimation newAnim = CharacterAnimation.Idle;
            if (Mode == FighterMode.Neutral)
            {
                if (Location(config) == FighterLocation.Airborne)
                {
                    newAnim = CharacterAnimation.Jump;
                }
                else if (Velocity.magnitude > (sfloat)0.01f)
                {
                    newAnim = CharacterAnimation.Walk;
                }
            }
            else if (Mode == FighterMode.Hitstun)
            {
                newAnim = CharacterAnimation.Hit;
            }
            else
            {
                return AnimState;
            }

            if (newAnim != AnimState)
            {
                AnimState = newAnim;
                AnimSt = frame;
            }
            return AnimState;
        }
    }
}
