using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace TidyPots;

/// <summary>
/// Rearranges the potion belt on screen only.
///
/// The game keeps <c>_holders[i]</c> bound to model slot <c>i</c>, and co-op sends potion actions
/// by that slot index, so the model must never move. Every lookup the game does goes through
/// the potion or the model index, never through screen position, so changing the holders'
/// child order under <c>PotionHolders</c> is purely cosmetic and safe in multiplayer.
/// </summary>
internal static class BeltOrder
{
    /// <summary>Removal animations in NPotionHolder: 0.2s scale-out (used), 0.4s fly-up (discarded).</summary>
    public const double AfterRemovalDelay = 0.45;

    private static readonly AccessTools.FieldRef<NPotionContainer, List<NPotionHolder>> Holders =
        AccessTools.FieldRefAccess<NPotionContainer, List<NPotionHolder>>("_holders");

    private static readonly AccessTools.FieldRef<NPotionContainer, Control> HolderParent =
        AccessTools.FieldRefAccess<NPotionContainer, Control>("_potionHolders");

    private static bool _warnedNotContainer;

    /// <summary>
    /// The ordering policy: the order the holders should appear in on screen.
    /// Filled slots first in their real slot order, then the empty ones. A future drag-and-drop
    /// mode would supply its own order here; nothing else needs to change.
    /// </summary>
    public static IReadOnlyList<NPotionHolder> ScreenOrder(IReadOnlyList<NPotionHolder> holders)
    {
        return holders.Where(h => h.HasPotion).Concat(holders.Where(h => !h.HasPotion)).ToList();
    }

    /// <summary>
    /// Re-sorts at the end of the frame. Holders are added with AddChildSafely, which may itself
    /// defer, so sorting straight away could try to move a node that is not a child yet.
    /// </summary>
    public static void Apply(NPotionContainer container)
    {
        Callable.From(() => ApplyNow(container)).CallDeferred();
    }

    /// <summary>Re-sorts once the removal animation has played out in its original spot.</summary>
    public static void ApplyAfterRemoval(NPotionContainer container)
    {
        SceneTree? tree = container.IsInsideTree() ? container.GetTree() : null;
        if (tree == null)
        {
            Apply(container);
            return;
        }

        tree.CreateTimer(AfterRemovalDelay, processAlways: true, processInPhysics: false, ignoreTimeScale: true)
            .Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() => ApplyNow(container)));
    }

    public static void ApplyNow(NPotionContainer container)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(container))
            {
                return;
            }

            Control parent = HolderParent(container);
            List<NPotionHolder> holders = Holders(container);
            if (parent == null || !GodotObject.IsInstanceValid(parent) || holders.Count == 0)
            {
                return;
            }

            if (parent is not Container && !_warnedNotContainer)
            {
                _warnedNotContainer = true;
                Log.Warn($"[TidyPots] PotionHolders is a {parent.GetType().Name}, not a layout Container; reordering children may not move them on screen.");
            }

            // Only sort holders that are already in place; a deferred add will trigger another pass.
            List<NPotionHolder> placed = holders.Where(h => GodotObject.IsInstanceValid(h) && h.GetParent() == parent).ToList();
            if (placed.Count == 0)
            {
                return;
            }

            // Reuse the child indices the holders already occupy, so anything else living
            // under PotionHolders keeps its place.
            List<int> slots = placed.Select(h => h.GetIndex()).OrderBy(i => i).ToList();
            IReadOnlyList<NPotionHolder> order = ScreenOrder(placed);
            for (int k = 0; k < order.Count; k++)
            {
                if (order[k].GetIndex() != slots[k])
                {
                    parent.MoveChild(order[k], slots[k]);
                }
            }

            FixNavigation(container);
        }
        catch (Exception ex)
        {
            Log.Error($"[TidyPots] Failed to reorder the potion belt: {ex}");
        }
    }

    /// <summary>The holders as they currently appear on screen, left to right.</summary>
    public static List<NPotionHolder> OnScreen(NPotionContainer container)
    {
        Control parent = HolderParent(container);
        if (parent == null || !GodotObject.IsInstanceValid(parent))
        {
            return Holders(container).ToList();
        }

        return parent.GetChildren().OfType<NPotionHolder>().ToList();
    }

    /// <summary>
    /// Mirrors NPotionContainer.UpdateNavigation, but walks the holders in screen order so a
    /// controller's left/right follows what the player sees.
    /// </summary>
    public static void FixNavigation(NPotionContainer container)
    {
        var ui = NRun.Instance?.GlobalUi;
        Control? below = ui?.RelicInventory?.RelicNodes.FirstOrDefault();
        if (ui == null || below == null)
        {
            return;
        }

        List<NPotionHolder> order = OnScreen(container);
        for (int i = 0; i < order.Count; i++)
        {
            order[i].FocusNeighborLeft = i > 0 ? order[i - 1].GetPath() : ui.TopBar.Gold.GetPath();
            order[i].FocusNeighborRight = i < order.Count - 1 ? order[i + 1].GetPath() : ui.TopBar.RoomIcon.GetPath();
            order[i].FocusNeighborBottom = below.GetPath();
            order[i].FocusNeighborTop = order[i].GetPath();
        }

        if (order.Count > 0)
        {
            ui.TopBar.Gold.FocusNeighborRight = order[0].GetPath();
            ui.TopBar.RoomIcon.FocusNeighborLeft = order[^1].GetPath();
        }
    }

    public static string Diagnostics(NPotionContainer container)
    {
        var sb = new StringBuilder();
        Control parent = HolderParent(container);
        List<NPotionHolder> holders = Holders(container);

        sb.Append($"PotionHolders node: {parent?.GetType().FullName ?? "null"} (Container: {parent is Container}).\n");
        sb.Append("Real slots: ").Append(string.Join(" ", holders.Select(Describe))).Append('\n');
        sb.Append("On screen:  ").Append(string.Join(" ", OnScreen(container).Select(h => $"{holders.IndexOf(h)}:{Describe(h)}"))).Append('\n');
        if (parent != null)
        {
            sb.Append("Children:   ").Append(string.Join(", ", parent.GetChildren().Select(c => c.GetType().Name)));
        }

        string text = sb.ToString();
        Log.Info("[TidyPots] " + text);
        return text;
    }

    private static string Describe(NPotionHolder h)
    {
        return h.Potion == null ? "[ ]" : $"[{h.Potion.Model.Id}]";
    }
}
