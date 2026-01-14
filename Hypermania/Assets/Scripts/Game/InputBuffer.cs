using Game.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game
{
    // TODO: implement with actual buffer, allow customization
    public class InputBuffer
    {
        private InputFlags _curInput;

        public void Saturate()
        {
            InputFlags input = InputFlags.None;

            if (Keyboard.current.aKey.isPressed)
                input |= InputFlags.Left;
            if (Keyboard.current.dKey.isPressed)
                input |= InputFlags.Right;
            if (Keyboard.current.wKey.isPressed)
                input |= InputFlags.Up;
            if (Keyboard.current.jKey.isPressed)
                input |= InputFlags.LightAttack;
            if (Keyboard.current.kKey.isPressed)
                input |= InputFlags.MediumAttack;
            if (Keyboard.current.lKey.isPressed)
                input |= InputFlags.SuperAttack;

            if (Keyboard.current.sKey.isPressed)
                input |= InputFlags.Mania5;
            if (Keyboard.current.dKey.isPressed)
                input |= InputFlags.Mania3;
            if (Keyboard.current.fKey.isPressed)
                input |= InputFlags.Mania1;
            if (Keyboard.current.jKey.isPressed)
                input |= InputFlags.Mania2;
            if (Keyboard.current.kKey.isPressed)
                input |= InputFlags.Mania4;
            if (Keyboard.current.lKey.isPressed)
                input |= InputFlags.Mania6;

            _curInput |= input;
        }

        public GameInput Consume()
        {
            GameInput res = new GameInput(_curInput);
            _curInput = InputFlags.None;
            return res;
        }
    }
}
