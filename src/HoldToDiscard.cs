using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace TidyPots;

/// <summary>
/// The potion popup's Discard button only fires after being held for <see cref="HoldSeconds"/>,
/// so a stray click can no longer throw a potion away.
///
/// The game discards on the button's Released signal. That handler is blocked unless we call it
/// ourselves, and a hold that completes calls the game's own handler, so the discard still goes
/// through the normal networked action. A red fill sweeps across the button while it is held, and
/// one of the game's own potion sloshes plays (cut off if the hold is let go).
/// </summary>
[HarmonyPatch]
internal static class HoldToDiscard
{
    public const double HoldSeconds = 1.0;

    private const double RetractSeconds = 0.12;

    private static readonly Color FillColor = new(0.85f, 0.2f, 0.15f, 0.65f);

    /// <summary>
    /// The text outline that sweeps in with the fill. The game's Discard label is red with a 50% black
    /// outline (size 12) and is drawn *under* the button background, so the red fill drowned it; the
    /// swept copy sits on top with a solid outline in the cream the Use label is written in.
    /// </summary>
    private static readonly Color TextOutlineColor = new(1f, 0.9647059f, 0.8862745f, 1f);

    private const int TextOutlineSize = 12;

    /// <summary>The game plays these at 0.5 when a potion is hovered and 0.3 at combat start.</summary>
    private const float SloshVolume = 0.6f;

    /// <summary>
    /// The game has three slosh clips (0.60/0.78/0.60s). potion_slosh_3 is left out: by ear it lacks
    /// the glug the others have. `tidy slosh n` plays clip n of this list.
    /// </summary>
    public static readonly string[] SloshClips = { "potion_slosh_1.mp3", "potion_slosh_2.mp3" };

    /// <summary>Below the game's 1.0 for more of a glug. Lower pitch also stretches the clip: at the
    /// bottom of the range the longest runs ~1.04s and is faded out when the hold completes.</summary>
    public const float SloshPitch = 0.8f;

    private const float SloshPitchVariance = 0.05f;

    private const double SloshFadeSeconds = 0.06;

    /// <summary>
    /// The "it's gone" sound after a completed discard, picked at random. FMOD has no potion or
    /// splash event; these two were chosen by ear from ~20 liquid-sounding enemy sounds.
    /// `tidy splash n` plays entry n.
    /// </summary>
    public static readonly string[] SplashEvents =
    {
        "event:/sfx/enemy/enemy_attacks/sludge_spinner/sludge_spinner_attack_dash",
        "event:/sfx/enemy/enemy_attacks/sludge_spinner/sludge_spinner_attack_spin",
    };

    private const float SplashVolume = 1f;

    /// <summary>The bus NDebugAudioManager plays these on; it follows the game's SFX volume.</summary>
    private static readonly StringName SfxBus = new("SFX");

    private static readonly AccessTools.FieldRef<NClickableControl, bool> IsPressed =
        AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");

    private static readonly AccessTools.FieldRef<NPotionPopup, NPotionPopupButton> DiscardButton =
        AccessTools.FieldRefAccess<NPotionPopup, NPotionPopupButton>("_discardButton");

    private static readonly System.Reflection.MethodInfo OnDiscardPressed =
        AccessTools.Method(typeof(NPotionPopup), "OnDiscardButtonPressed");

    /// <summary>Set only while a completed hold is calling the game's discard handler.</summary>
    private static bool _allowDiscard;

    private static Hold? _current;

    /// <summary>The game's discard handler runs only when a hold has completed.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NPotionPopup), "OnDiscardButtonPressed")]
    private static bool BeforeDiscardPressed() => _allowDiscard;

    /// <summary>
    /// Every press of a clickable control goes through _GuiInput (mouse and controller select).
    /// Watching _isPressed flip to true means we start exactly when the game accepts a press.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
    private static void BeforeGuiInput(NClickableControl __instance, out bool __state)
    {
        __state = __instance is NPotionPopupButton && IsPressed(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
    private static void AfterGuiInput(NClickableControl __instance, InputEvent inputEvent, bool __state)
    {
        try
        {
            if (__state || __instance is not NPotionPopupButton button || !IsPressed(button))
            {
                return;
            }

            if (button.Owner is not NPotionPopup popup || DiscardButton(popup) != button)
            {
                return;
            }

            _current?.Cancel();
            _current = new Hold(popup, button, byMouse: inputEvent is InputEventMouseButton);
        }
        catch (Exception ex)
        {
            Log.Error($"[TidyPots] Failed to start hold-to-discard: {ex}");
        }
    }

    /// <summary>Plays a splash as a one-shot through the game's FMOD bridge.</summary>
    public static void PlaySplash(string fmodEvent)
    {
        NAudioManager.Instance?.PlayOneShot(fmodEvent, SplashVolume);
    }

    /// <summary>Plays one slosh clip on a self-freeing player under <paramref name="parent"/>.</summary>
    public static AudioStreamPlayer PlayClip(Node parent, string clip, float pitch)
    {
        var player = new AudioStreamPlayer
        {
            Stream = PreloadManager.Cache.GetAsset<AudioStream>(TmpSfx.GetPath(clip)),
            Bus = SfxBus,
            VolumeLinear = SloshVolume,
            PitchScale = pitch,
        };
        player.Finished += player.QueueFree;
        parent.AddChild(player);
        player.Play();
        return player;
    }

    private sealed class Hold
    {
        private readonly NPotionPopup _popup;
        private readonly NPotionPopupButton _button;
        private readonly bool _byMouse;
        private readonly ulong _startMs;
        private readonly SceneTree _tree;
        /// <summary>Clipping controls that all reveal left to right with the hold, in step.</summary>
        private readonly System.Collections.Generic.List<Control> _clips;
        private readonly float _fullWidth;
        private readonly Action _tick;
        private readonly AudioStreamPlayer? _sound;
        private bool _done;

        public Hold(NPotionPopup popup, NPotionPopupButton button, bool byMouse)
        {
            _popup = popup;
            _button = button;
            _byMouse = byMouse;
            _startMs = Time.GetTicksMsec();
            _tree = button.GetTree();
            (_clips, _fullWidth) = CreateSweep(button);
            _sound = PlaySlosh(button);
            // One delegate instance, so the -= in Finish disconnects exactly what was connected.
            _tick = Tick;
            _tree.ProcessFrame += _tick;
        }

        private void Tick()
        {
            if (_done)
            {
                return;
            }

            try
            {
                if (!StillHeld())
                {
                    Cancel();
                    return;
                }

                double progress = Math.Min(1.0, (Time.GetTicksMsec() - _startMs) / 1000.0 / HoldSeconds);
                foreach (Control clip in _clips)
                {
                    if (GodotObject.IsInstanceValid(clip))
                    {
                        clip.Size = new Vector2(_fullWidth * (float)progress, clip.Size.Y);
                    }
                }

                if (progress >= 1.0)
                {
                    Finish();
                    Fire();
                    PlaySplash(SplashEvents[Random.Shared.Next(SplashEvents.Length)]);
                }
            }
            catch (Exception ex)
            {
                Finish();
                Log.Error($"[TidyPots] Hold-to-discard failed: {ex}");
            }
        }

        private bool StillHeld()
        {
            if (!GodotObject.IsInstanceValid(_button) || !GodotObject.IsInstanceValid(_popup)
                || _popup.IsMarkedForRemoval || !_button.IsEnabled || !IsPressed(_button))
            {
                return false;
            }

            if (_byMouse)
            {
                return Input.IsMouseButtonPressed(MouseButton.Left)
                    && _button.GetGlobalRect().HasPoint(_button.GetGlobalMousePosition());
            }

            return Input.IsActionPressed(MegaInput.select);
        }

        private void Fire()
        {
            _allowDiscard = true;
            try
            {
                OnDiscardPressed.Invoke(_popup, new object[] { _button });
            }
            finally
            {
                _allowDiscard = false;
            }
        }

        /// <summary>Stops the hold without discarding and slides the fill back out.</summary>
        public void Cancel()
        {
            if (_done)
            {
                return;
            }

            Finish();
            foreach (Control clip in _clips)
            {
                if (!GodotObject.IsInstanceValid(clip))
                {
                    continue;
                }

                if (!clip.IsInsideTree())
                {
                    clip.QueueFree();
                    continue;
                }

                Tween tween = clip.CreateTween();
                tween.TweenProperty(clip, "size:x", 0f, RetractSeconds).SetTrans(Tween.TransitionType.Sine);
                tween.TweenCallback(Callable.From(clip.QueueFree));
            }
        }

        private void Finish()
        {
            _done = true;
            StopSlosh();
            _tree.ProcessFrame -= _tick;
            if (_current == this)
            {
                _current = null;
            }
        }

        /// <summary>
        /// One of the potion sloshes the game itself plays on hover, on a player of our own parented
        /// to the button: stoppable, and freed with the popup. NDebugAudioManager is avoided because
        /// its Stop logs a warning once a sound has already finished.
        /// </summary>
        private static AudioStreamPlayer? PlaySlosh(NPotionPopupButton button)
        {
            try
            {
                string clip = SloshClips[Random.Shared.Next(SloshClips.Length)];
                float pitch = SloshPitch + (float)(Random.Shared.NextDouble() * 2 - 1) * SloshPitchVariance;
                return PlayClip(button, clip, pitch);
            }
            catch (Exception ex)
            {
                Log.Warn($"[TidyPots] Could not play the discard slosh: {ex.Message}");
                return null;
            }
        }

        /// <summary>Cuts the slosh off with a very short fade, so letting go does not click.</summary>
        private void StopSlosh()
        {
            if (_sound == null || !GodotObject.IsInstanceValid(_sound) || _sound.IsQueuedForDeletion())
            {
                return;
            }

            if (!_sound.Playing || !_sound.IsInsideTree())
            {
                _sound.QueueFree();
                return;
            }

            Tween tween = _sound.CreateTween();
            tween.TweenProperty(_sound, "volume_linear", 0f, SloshFadeSeconds);
            tween.TweenCallback(Callable.From(_sound.QueueFree));
        }

        /// <summary>
        /// Builds the two layers that sweep in with the hold, each inside its own clipping control
        /// spanning the background's x range so both edges move together:
        /// a red copy of the button's background texture (follows the button's shape), just above
        /// the background; and an outlined copy of the label on top of everything.
        /// </summary>
        private static (System.Collections.Generic.List<Control>, float) CreateSweep(NPotionPopupButton button)
        {
            var clips = new System.Collections.Generic.List<Control>();
            TextureRect? background = button.GetNodeOrNull<TextureRect>("Background");
            if (background == null)
            {
                return (clips, 0f);
            }

            var fill = new TextureRect
            {
                Texture = background.Texture,
                ExpandMode = background.ExpandMode,
                StretchMode = background.StretchMode,
                FlipH = background.FlipH,
                FlipV = background.FlipV,
                Material = background.Material,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = FillColor,
                Position = Vector2.Zero,
                Size = background.Size,
            };
            Control fillClip = AddClip(button, background.Position, background.Size.Y, fill);
            button.MoveChild(fillClip, background.GetIndex() + 1);
            clips.Add(fillClip);

            Label? label = button.GetNodeOrNull<Label>("Label");
            if (label != null)
            {
                var clipOrigin = new Vector2(background.Position.X, label.Position.Y);
                Label text = OutlinedCopy(label);
                text.Position = label.Position - clipOrigin;
                clips.Add(AddClip(button, clipOrigin, label.Size.Y, text));
            }

            return (clips, background.Size.X);
        }

        private static Control AddClip(Control parent, Vector2 position, float height, Control content)
        {
            var clip = new Control
            {
                ClipContents = true,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = position,
                Size = new Vector2(0f, height),
            };
            clip.AddChild(content);
            parent.AddChild(clip);
            return clip;
        }

        /// <summary>
        /// A stock Label matching the game's MegaLabel as currently laid out (its auto-sized font size
        /// and locale font are read back off it), with our outline. Not a MegaLabel: it asserts a
        /// theme font in _Ready and would re-run its own sizing.
        /// </summary>
        private static Label OutlinedCopy(Label label)
        {
            var copy = new Label
            {
                Text = label.Text,
                HorizontalAlignment = label.HorizontalAlignment,
                VerticalAlignment = label.VerticalAlignment,
                AutowrapMode = label.AutowrapMode,
                Uppercase = label.Uppercase,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = label.Size,
                Modulate = label.Modulate,
                SelfModulate = label.SelfModulate,
            };
            copy.AddThemeFontOverride("font", label.GetThemeFont("font"));
            copy.AddThemeFontSizeOverride("font_size", label.GetThemeFontSize("font_size"));
            copy.AddThemeColorOverride("font_color", label.GetThemeColor("font_color"));
            copy.AddThemeColorOverride("font_outline_color", TextOutlineColor);
            copy.AddThemeConstantOverride("outline_size", TextOutlineSize);
            copy.AddThemeConstantOverride("shadow_outline_size", 0);
            return copy;
        }
    }
}
