using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace TidyPots;

/// <summary>
/// Entry point. The loader finds this via <see cref="ModInitializerAttribute"/> and calls
/// <see cref="Init"/> once at startup.
/// </summary>
[ModInitializer(nameof(Init))]
internal static class TidyPotsMod
{
    private const string HarmonyId = "tomasapan.TidyPots";

    private static void Init()
    {
        try
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            Log.Info("[TidyPots] Initialised.");
        }
        catch (Exception ex)
        {
            Log.Error($"[TidyPots] Failed to initialise: {ex}");
        }
    }
}
