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
    internal static ConfigEntry<float> TimeAcceleration = null!;

    // 内部状态控制
    private static bool _isPaused = false;

    // Singleton instance
    private static Plugin _instance = null!;

    private void Awake()
    {
        Log = Logger;
        _instance = this;

        // Create configuration options
        AutoOpenEnabled = Config.Bind("AutoChest", "Enabled", true, "Enable automatic chest opening");
        OpenDelay = Config.Bind("AutoChest", "Open Delay", 1f, "Open delay in seconds");
        TimeAcceleration = Config.Bind("AutoChest", "Time Acceleration", 2f,
            new ConfigDescription("Time acceleration multiplier (1.0 = normal, 2.0 = 2x faster)",
                new AcceptableValueRange<float>(1.0f, 2.0f)));

        Harmony.CreateAndPatchAll(typeof(Plugin));
        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Shop), "TimerUpdate")]
    public static bool TimerUpdatePrefix(Shop __instance, ref IEnumerator __result)
    {
        // 计算并显示宝箱开启时间
        float normalTimeMinutes = 1800f / 60f; // 30分钟
        float acceleratedTimeMinutes = normalTimeMinutes / TimeAcceleration.Value;
        Log.LogInfo($"Chest refresh time: {normalTimeMinutes} minutes -> {acceleratedTimeMinutes} minutes (acceleration: {TimeAcceleration.Value}x, range: 1.0-2.0)");

        __result = TimerUpdateWithAcceleration(__instance);
        return false; // 跳过原始方法
    }

    private static IEnumerator TimerUpdateWithAcceleration(Shop shop)
    {
        while (true)
        {
            if (shop._outOfStockObj.activeSelf)
            {
                shop.StockRefreshTimeLeft--;
                PlayerPrefs.SetInt("TIME_LEFT", shop.StockRefreshTimeLeft);
                shop._stockRefreshText.text = $"{TimeSpan.FromSeconds(shop.StockRefreshTimeLeft):mm':'ss}";

                if (shop.StockRefreshTimeLeft <= 0 && shop._catInventory.ChestToken.m_unQuantity == 0)
                {
                    shop.StockRefreshTimeLeft = 0;
                    shop._playtimeItemDrop.TryDropChest();
                    if (shop._triedChestDrop)
                    {
                        shop.SetSuccessVisuals(success: false, showError: false);

                        // 检测到宝箱掉落失败，暂停加速和自动开箱
                        if (!_isPaused)
                        {
                            _isPaused = true;
                            Log.LogWarning("Chest drop failed detected! Pausing acceleration and auto-open. Manual purchase required to resume.");
                        }
                    }
                    shop._triedChestDrop = !shop._triedChestDrop;
                }
                else if (shop.StockRefreshTimeLeft <= 0 && shop._catInventory.ChestToken.m_unQuantity > 0)
                {
                    shop.StockRefreshTimeLeft = 0;
                    shop._shopItem.gameObject.SetActive(true);
                    shop._outOfStockObj.SetActive(false);
                    shop._triedChestDrop = false;
                    shop.ChestIsReady = true;
                    shop._steamMultiplayer.SendChestReady(shop.ChestIsReady);
                    if (shop._showChestPopup.Value && shop._shopItem.CanBuy())
                    {
                        shop._shopVisuals.SetActive(true);
                    }
                }
            }
            else if (shop.StockRefreshTimeLeft <= 0 && !shop._shopVisuals.activeInHierarchy && shop._showChestPopup.Value && shop._shopItem.CanBuy())
            {
                shop._shopVisuals.SetActive(true);
            }

            float waitTime = _isPaused ? 1f : (1f / TimeAcceleration.Value);
            yield return new WaitForSecondsRealtime(waitTime);
        }
    }

    // Listen to ShopItem's CanBuy method - auto open if returns true
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.CanBuy))]
    public static void OnShopItemCanBuy(ShopItem __instance, bool __result)
    {
        if (!AutoOpenEnabled.Value || !__result || _isPaused)
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

    // Patch ShopItem.Buy method to restore functionality after manual purchase
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.Buy))]
    public static void OnShopItemBuy(ShopItem __instance)
    {
        // 如果当前处于暂停状态，恢复加速和自动开箱
        if (_isPaused)
        {
            _isPaused = false;
            Log.LogInfo("Manual purchase detected! Resuming acceleration and auto-open functionality.");
        }
    }
}