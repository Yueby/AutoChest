using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BongoCat;
using HarmonyLib;
using UnityEngine;
using System.Collections;

namespace AutoChest;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    
    // Configuration options
    internal static ConfigEntry<bool> autoBuyEnabled = null!;
    internal static ConfigEntry<float> buyDelay = null!;
    
    // Singleton instance
    private static Plugin _instance = null!;
    private static bool _hasTriedBuy = false;

    private void Awake()
    {
        Log = Logger;
        _instance = this;

        // Create configuration options
        autoBuyEnabled = Config.Bind("AutoChest", "Enabled", true, "Enable automatic chest buying");
        buyDelay = Config.Bind("AutoChest", "BuyDelay", 0.5f, "Buy delay in seconds");

        Harmony.CreateAndPatchAll(typeof(Plugin));
        Log.LogInfo($"Plugin {Name} is loaded!");
    }
    
    // Listen to ShopItem's CanBuy method - auto buy if returns true
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.CanBuy))]
    public static void OnShopItemCanBuy(ShopItem __instance, bool __result)
    {
        if (!autoBuyEnabled.Value || _hasTriedBuy || !__result)
            return;
            
        Log.LogInfo("Detected purchasable chest, starting auto buy...");
        _hasTriedBuy = true;
        
        if (buyDelay.Value > 0)
        {
            _instance.StartCoroutine(DelayedBuy(__instance));
        }
        else
        {
            __instance.Buy();
        }
    }
    
    // Delayed buy coroutine
    private static IEnumerator DelayedBuy(ShopItem shopItem)
    {
        yield return new WaitForSeconds(buyDelay.Value);
        
        if (shopItem != null && shopItem.CanBuy())
        {
            Log.LogInfo("Executing automatic chest purchase");
            shopItem.Buy();
        }
    }
    
    // Reset buy flag
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Shop), nameof(Shop.ItemGotBought))]
    public static void OnItemBought()
    {
        _hasTriedBuy = false;
        Log.LogInfo("Chest purchase completed, resetting auto buy flag");
    }
}