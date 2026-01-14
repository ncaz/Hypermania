using System;

namespace Game
{
    public enum CharacterAnimation
    {
        Hit,
        Walk,
        Jump,
        Idle,
        Knockdown,
        LightAttack,
        LightAerial,
        LightCrouching,
        MediumAttack,
        MediumAerial,
        MediumCrouching,
        SuperAttack,
        SuperAerial,
        SuperCrouching,
        SpecialAttack,
        SpecialAerial,
        SpecialCrouching,
        Ultimate,
    }

    [Serializable]
    public enum Character
    {
        SampleFighter,
    }
}
