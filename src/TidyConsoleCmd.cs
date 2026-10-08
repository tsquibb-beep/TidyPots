using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace TidyPots;

/// <summary>
/// `tidy diag` shows the real slots next to the on-screen order; `tidy apply` forces a re-sort;
/// `tidy slosh n` plays discard slosh n at the hold's pitch, to tell the clips apart by ear.
///
/// The dev console discovers commands in loaded mods by reflection, so shipping this public
/// class with a parameterless constructor is enough to register it.
/// </summary>
public class TidyConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "tidy";

    public override string Args => "[diag|apply|slosh <n>]";

    public override string Description => "TidyPots: inspect or re-sort the potion belt.";

    /// <summary>Screen order is local to this client — nothing to synchronise.</summary>
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.FirstOrDefault()?.ToLowerInvariant() == "slosh")
        {
            return Slosh(args.Skip(1).FirstOrDefault());
        }

        NPotionContainer? container = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
        if (container == null)
        {
            return new CmdResult(success: false, "No potion belt on screen — start a run first.");
        }

        switch (args.FirstOrDefault()?.ToLowerInvariant() ?? "diag")
        {
            case "diag":
                return new CmdResult(success: true, BeltOrder.Diagnostics(container));

            case "apply":
                BeltOrder.ApplyNow(container);
                return new CmdResult(success: true, BeltOrder.Diagnostics(container));

            default:
                return new CmdResult(success: false, "Use: tidy diag, tidy apply, tidy slosh <n>.");
        }
    }

    private static CmdResult Slosh(string? arg)
    {
        string[] clips = HoldToDiscard.SloshClips;
        if (!int.TryParse(arg, out int n) || n < 1 || n > clips.Length)
        {
            return new CmdResult(success: false, $"Use: tidy slosh <1-{clips.Length}>.");
        }

        if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
        {
            return new CmdResult(success: false, "No scene tree to play the sound in.");
        }

        HoldToDiscard.PlayClip(tree.Root, clips[n - 1], HoldToDiscard.SloshPitch);
        return new CmdResult(success: true, $"Playing slosh {n} ({clips[n - 1]}) at pitch {HoldToDiscard.SloshPitch}.");
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            return CompleteArgument(new[] { "diag", "apply", "slosh" }, System.Array.Empty<string>(), args.FirstOrDefault() ?? "");
        }

        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}
