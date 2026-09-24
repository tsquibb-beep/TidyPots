using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace TidyPots;

/// <summary>
/// Hooks every point where NPotionContainer changes which holders are filled, and re-sorts the
/// belt on screen. The patched methods are the container's own handlers for the player's
/// potion events, so the model has already changed by the time these run.
/// </summary>
[HarmonyPatch(typeof(NPotionContainer))]
internal static class BeltPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NPotionContainer.Initialize))]
    private static void AfterInitialize(NPotionContainer __instance) => BeltOrder.Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("GrowPotionHolders")]
    private static void AfterGrow(NPotionContainer __instance) => BeltOrder.Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("OnPotionProcured")]
    private static void AfterProcured(NPotionContainer __instance) => BeltOrder.Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("OnUsedPotionRemoved")]
    private static void AfterUsed(NPotionContainer __instance) => BeltOrder.ApplyAfterRemoval(__instance);

    [HarmonyPostfix]
    [HarmonyPatch("Discard")]
    private static void AfterDiscard(NPotionContainer __instance) => BeltOrder.ApplyAfterRemoval(__instance);

    /// <summary>The game rebuilds navigation in slot order whenever relics change; redo it in screen order.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("UpdateNavigation")]
    private static void AfterUpdateNavigation(NPotionContainer __instance) => BeltOrder.FixNavigation(__instance);

    /// <summary>Used by the top bar and relic bar to link controller focus into the belt.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NPotionContainer.FirstPotionControl), MethodType.Getter)]
    private static void FirstOnScreen(NPotionContainer __instance, ref Control? __result)
    {
        var order = BeltOrder.OnScreen(__instance);
        if (order.Count > 0)
        {
            __result = order[0];
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NPotionContainer.LastPotionControl), MethodType.Getter)]
    private static void LastOnScreen(NPotionContainer __instance, ref Control? __result)
    {
        var order = BeltOrder.OnScreen(__instance);
        if (order.Count > 0)
        {
            __result = order[^1];
        }
    }
}
