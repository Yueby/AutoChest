using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BongoCat;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System;

namespace AutoChest;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    // Configuration options
    internal static ConfigEntry<bool> AutoOpenEnabled = null!;
    internal static ConfigEntry<float> OpenDelay = null!;

    // Singleton instance
    private static Plugin _instance = null!;

    private void Awake()
    {
        Log = Logger;
        _instance = this;

        // Create configuration options
        AutoOpenEnabled = Config.Bind("AutoChest", "Enabled", true, "Enable automatic chest opening");
        OpenDelay = Config.Bind("AutoChest", "Open Delay", 1f, "Open delay in seconds");

        Harmony.CreateAndPatchAll(typeof(Plugin));
        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    // Listen to ShopItem's CanBuy method - auto open if returns true
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.CanBuy))]
    public static void OnShopItemCanBuy(ShopItem __instance, bool __result)
    {
        if (!AutoOpenEnabled.Value || !__result)
            return;

        try
        {
            var priceFieldInfo = AccessTools.Field(typeof(ShopItem), "_price");
            int price = (int)priceFieldInfo.GetValue(__instance);

            if (!__instance.Pets.CanSpendPets(price))
            {
                Log.LogInfo("Not enough pets to open chest, skipping auto open");
                return;
            }
        }
        catch (Exception e)
        {
            Log.LogError($"Error getting price of chest: {e}");
            return;
        }

        Log.LogInfo("Detected purchasable chest, starting auto open...");

        if (OpenDelay.Value > 0)
        {
            _instance.StartCoroutine(DelayedOpen(__instance));
        }
        else
        {
            __instance.Buy();
        }
    }

    // Delayed open coroutine
    private static IEnumerator DelayedOpen(ShopItem shopItem)
    {
        yield return new WaitForSeconds(OpenDelay.Value);

        if (shopItem != null)
        {
            Log.LogInfo("Executing automatic chest opening");
            shopItem.Buy();
        }
    }
}