﻿using System;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Vim.UI.Wpf
{
    /// <summary>
    /// The morale of the history surrounding this type is translating key input is
    /// **hard**.  Anytime it's done manually and expected to be 100% correct it 
    /// likely to have a bug.  If you doubt this then I encourage you to read the 
    /// following 10 part blog series
    /// 
    /// http://blogs.msdn.com/b/michkap/archive/2006/04/13/575500.aspx
    ///
    /// Or simply read the keyboard feed on the same blog page.  It will humble you
    /// </summary>
    public class VimKeyProcessor : KeyProcessor
    {
        private readonly IVimBuffer _buffer;

        public IVimBuffer VimBuffer
        {
            get { return _buffer; }
        }

        public ITextBuffer TextBuffer
        {
            get { return _buffer.TextBuffer; }
        }

        public ITextView TextView
        {
            get { return _buffer.TextView; }
        }

        public VimKeyProcessor(IVimBuffer buffer)
        {
            _buffer = buffer;
        }

        public override bool IsInterestedInHandledEvents
        {
            get { return true; }
        }

        /// <summary>
        /// Try and process the given KeyInput with the IVimBuffer.  This is overridable by 
        /// derived classes in order for them to prevent any KeyInput from reaching the 
        /// IVimBuffer
        /// </summary>
        protected virtual bool TryProcess(KeyInput keyInput)
        {
            return _buffer.CanProcess(keyInput) && _buffer.Process(keyInput).IsAnyHandled;
        }

        /// <summary>
        /// Last chance at custom handling of user input.  At this point we have the 
        /// advantage that WPF has properly converted the user input into a char which 
        /// can be effeciently mapped to a KeyInput value.  
        /// </summary>
        public override void TextInput(TextCompositionEventArgs args)
        {
            bool handled = false;

            var text = args.Text;
            if (!String.IsNullOrEmpty(text))
            {
                // In the case of a failed dead key mapping (pressing the accent key twice for
                // example) we will recieve a multi-length string here.  One character for every
                // one of the mappings.  Make sure to handle each of them
                var keyboard = args.Device as KeyboardDevice;
                if (keyboard != null)
                {
                    for (var i = 0; i < text.Length; i++)
                    {
                        var keyInput = KeyUtil.CharAndModifiersToKeyInput(text[i], keyboard.Modifiers);
                        handled = TryProcess(keyInput);
                    }
                }
            }

            args.Handled = handled;
            base.TextInput(args);
        }

        /// <summary>
        /// This handler is necessary to intercept keyboard input which maps to Vim
        /// commands but doesn't map to text input.  Any combination which can be 
        /// translated into actual text input will be done so much more accurately by
        /// WPF and will end up in the TextInput event.
        /// 
        /// An example of why this handler is needed is for key combinations like 
        /// Shift+Escape.  This combination won't translate to an actual character in most
        /// (possibly all) keyboard layouts.  This means it won't ever make it to the
        /// TextInput event.  But it can translate to a Vim command or mapped keyboard 
        /// combination that we do want to handle.  Hence we override here specifically
        /// to capture those circumstances
        /// 
        /// We don't capture everything here.  Instead we limit our search to the keys 
        /// which we have a high degree of confidence can't be mapped to textual input 
        /// and will still be a valid Vim command.  Those are keys with control modifiers 
        /// or those which don't map to direct input characters
        /// </summary>
        public override void KeyDown(KeyEventArgs args)
        {
            bool handled;
            if (KeyUtil.IsDeadKey(args.Key) || args.Key == Key.DeadCharProcessed)
            {
                // When a dead key combination is pressed we will get the key down events in 
                // sequence after the combination is complete.  The dead keys will come first
                // and be followed by the final key which produces the char.  That final key 
                // is marked as DeadCharProcessed.
                //
                // All of these should be ignored.  They will produce a TextInput value which
                // we can process in the TextInput event
                handled = false;
            }
            else if (KeyUtil.IsAltGr(args.KeyboardDevice.Modifiers))
            {
                // AltGr greatly confuses things becuase it's realized in WPF as Control | Alt.  So
                // while it's possible to use Control to further modify a key which used AltGr
                // originally the result is indistinguishable here (and in gVim).  Don't attempt
                // to process it
                handled = false;
            }
            else
            {
                // Attempt to map the key information into a KeyInput value which can be processed
                // by Vim.  If this worksa nd the key is processed then the input is considered
                // to be handled
                KeyInput keyInput;
                if (KeyUtil.TryConvertToKeyInput(args.Key, args.KeyboardDevice.Modifiers, out keyInput))
                {
                    handled = TryProcess(keyInput);
                }
                else
                {
                    handled = false;
                }
            }

            args.Handled = handled;
            base.KeyDown(args);
        }
    }
}
