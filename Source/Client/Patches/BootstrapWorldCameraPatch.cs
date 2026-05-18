using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowsPreventCameraMotion), MethodType.Getter)]
static class BootstrapStartingSiteCameraPatch
{
    private static int lastLogFrame = -1;

    static void Postfix(WindowStack __instance, ref bool __result)
    {
        if (!__result ||
            !BootstrapConfiguratorWindow.AwaitingBootstrapMapInit ||
            __instance.WindowOfType<Page_SelectStartingSite>() == null ||
            !WorldRendererUtility.WorldSelected)
        {
            return;
        }

        if (Multiplayer.ShowDevInfo && lastLogFrame != Time.frameCount)
        {
            lastLogFrame = Time.frameCount;
            var blockers = __instance.Windows
                .Where(window => window.preventCameraMotion)
                .Select(window => window.GetType().Name);

            MpLog.Log("[Bootstrap] Allowing world camera during starting site selection. Blockers: " + string.Join(", ", blockers));
        }

        __result = false;
    }
}
