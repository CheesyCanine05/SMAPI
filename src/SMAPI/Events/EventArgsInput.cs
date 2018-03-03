using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace StardewModdingAPI.Events
{
    /// <summary>Event arguments when a button is pressed or released.</summary>
    public class EventArgsInput : EventArgs
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The button on the controller, keyboard, or mouse.</summary>
        public SButton Button { get; }

        /// <summary>The current cursor position.</summary>
        public ICursorPosition Cursor { get; }

        /// <summary>Whether the input should trigger actions on the affected tile.</summary>
        public bool IsActionButton { get; }

        /// <summary>Whether the input should use tools on the affected tile.</summary>
        public bool IsUseToolButton { get; }

        /// <summary>Whether a mod has indicated the key was already handled.</summary>
        public bool IsSuppressed { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="button">The button on the controller, keyboard, or mouse.</param>
        /// <param name="cursor">The cursor position.</param>
        /// <param name="isActionButton">Whether the input should trigger actions on the affected tile.</param>
        /// <param name="isUseToolButton">Whether the input should use tools on the affected tile.</param>
        public EventArgsInput(SButton button, ICursorPosition cursor, bool isActionButton, bool isUseToolButton)
        {
            this.Button = button;
            this.Cursor = cursor;
            this.IsActionButton = isActionButton;
            this.IsUseToolButton = isUseToolButton;
        }

        /// <summary>Prevent the game from handling the vurrent button press. This doesn't prevent other mods from receiving the event.</summary>
        public void SuppressButton()
        {
            this.SuppressButton(this.Button);
        }

        /// <summary>Prevent the game from handling a button press. This doesn't prevent other mods from receiving the event.</summary>
        /// <param name="button">The button to suppress.</param>
        public void SuppressButton(SButton button)
        {
            if (button == this.Button)
                this.IsSuppressed = true;

            // keyboard
            if (button.TryGetKeyboard(out Keys key))
                Game1.oldKBState = new KeyboardState(Game1.oldKBState.GetPressedKeys().Union(new[] { key }).ToArray());

            // controller
            else if (button.TryGetController(out Buttons controllerButton))
            {
                var newState = GamePad.GetState(PlayerIndex.One);
                var thumbsticks = Game1.oldPadState.ThumbSticks;
                var triggers = Game1.oldPadState.Triggers;
                var buttons = Game1.oldPadState.Buttons;
                var dpad = Game1.oldPadState.DPad;

                switch (controllerButton)
                {
                    // d-pad
                    case Buttons.DPadDown:
                        dpad = new GamePadDPad(dpad.Up, newState.DPad.Down, dpad.Left, dpad.Right);
                        break;
                    case Buttons.DPadLeft:
                        dpad = new GamePadDPad(dpad.Up, dpad.Down, newState.DPad.Left, dpad.Right);
                        break;
                    case Buttons.DPadRight:
                        dpad = new GamePadDPad(dpad.Up, dpad.Down, dpad.Left, newState.DPad.Right);
                        break;
                    case Buttons.DPadUp:
                        dpad = new GamePadDPad(newState.DPad.Up, dpad.Down, dpad.Left, dpad.Right);
                        break;

                    // trigger
                    case Buttons.LeftTrigger:
                        triggers = new GamePadTriggers(newState.Triggers.Left, triggers.Right);
                        break;
                    case Buttons.RightTrigger:
                        triggers = new GamePadTriggers(triggers.Left, newState.Triggers.Right);
                        break;

                    // thumbstick
                    case Buttons.LeftThumbstickDown:
                    case Buttons.LeftThumbstickLeft:
                    case Buttons.LeftThumbstickRight:
                    case Buttons.LeftThumbstickUp:
                        thumbsticks = new GamePadThumbSticks(newState.ThumbSticks.Left, thumbsticks.Right);
                        break;
                    case Buttons.RightThumbstickDown:
                    case Buttons.RightThumbstickLeft:
                    case Buttons.RightThumbstickRight:
                    case Buttons.RightThumbstickUp:
                        thumbsticks = new GamePadThumbSticks(newState.ThumbSticks.Right, thumbsticks.Left);
                        break;

                    // buttons
                    default:
                        var mask =
                            (buttons.A == ButtonState.Pressed ? Buttons.A : 0)
                            | (buttons.B == ButtonState.Pressed ? Buttons.B : 0)
                            | (buttons.Back == ButtonState.Pressed ? Buttons.Back : 0)
                            | (buttons.BigButton == ButtonState.Pressed ? Buttons.BigButton : 0)
                            | (buttons.LeftShoulder == ButtonState.Pressed ? Buttons.LeftShoulder : 0)
                            | (buttons.LeftStick == ButtonState.Pressed ? Buttons.LeftStick : 0)
                            | (buttons.RightShoulder == ButtonState.Pressed ? Buttons.RightShoulder : 0)
                            | (buttons.RightStick == ButtonState.Pressed ? Buttons.RightStick : 0)
                            | (buttons.Start == ButtonState.Pressed ? Buttons.Start : 0)
                            | (buttons.X == ButtonState.Pressed ? Buttons.X : 0)
                            | (buttons.Y == ButtonState.Pressed ? Buttons.Y : 0);
                        mask = mask ^ controllerButton;
                        buttons = new GamePadButtons(mask);
                        break;
                }

                Game1.oldPadState = new GamePadState(thumbsticks, triggers, buttons, dpad);
            }

            // mouse
            else if (button == SButton.MouseLeft || button == SButton.MouseMiddle || button == SButton.MouseRight || button == SButton.MouseX1 || button == SButton.MouseX2)
            {
                Game1.oldMouseState = new MouseState(
                    x: Game1.oldMouseState.X,
                    y: Game1.oldMouseState.Y,
                    scrollWheel: Game1.oldMouseState.ScrollWheelValue,
                    leftButton: button == SButton.MouseLeft ? ButtonState.Pressed : Game1.oldMouseState.LeftButton,
                    middleButton: button == SButton.MouseMiddle ? ButtonState.Pressed : Game1.oldMouseState.MiddleButton,
                    rightButton: button == SButton.MouseRight ? ButtonState.Pressed : Game1.oldMouseState.RightButton,
                    xButton1: button == SButton.MouseX1 ? ButtonState.Pressed : Game1.oldMouseState.XButton1,
                    xButton2: button == SButton.MouseX2 ? ButtonState.Pressed : Game1.oldMouseState.XButton2
                );
            }
        }
    }
}
