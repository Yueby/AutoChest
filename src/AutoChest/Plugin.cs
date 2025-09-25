using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BongoCat;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System;
using System.Reflection;
using Steam;
using Steamworks;

namespace AutoChest;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;

    internal static ConfigEntry<bool> AutoOpenEnabled = null!;
    internal static ConfigEntry<float> OpenDelay = null!;
    internal static ConfigEntry<float> TimeAcceleration = null!;
    internal static ConfigEntry<float> AutoResumeDelay = null!;
    internal static ConfigEntry<float> AccelerationDelay = null!;

    // Auto pause and resume
    private static bool _isPaused = false;
    private static Coroutine _autoResumeCoroutine = null!;

    // Auto acceleration
    private static bool _accelerationEnabled = false;
    private static Coroutine _accelerationDelayCoroutine = null!;

    // Auto open delay
    private static Coroutine _delayedOpenCoroutine = null!;

    // Cached reflection
    private static readonly FieldInfo _priceField = AccessTools.Field(typeof(ShopItem), "_price");

    private void Awake()
    {
        Log = Logger;
        Instance = this;

        // Auto open
        AutoOpenEnabled = Config.Bind("AutoChest", "Enabled", true, "Enable automatic chest opening");
        OpenDelay = Config.Bind("AutoChest", "Open Delay", 1f, "Open delay in seconds");

        // Auto acceleration
        TimeAcceleration = Config.Bind("AutoChest", "Time Acceleration", 1f,
            new ConfigDescription("Time acceleration multiplier (1.0 = normal, 2.0 = 2x faster)",
                new AcceptableValueRange<float>(1.0f, 2.0f)));
        AccelerationDelay = Config.Bind("AutoChest", "Acceleration Delay", 10f, "Delay in seconds before enabling time acceleration (default: 10s)");

        // Auto resume
        AutoResumeDelay = Config.Bind("AutoChest", "Auto Resume Delay", 150f, "Auto resume delay in seconds after pause (default: 120s = 2 minutes)");

        Harmony.CreateAndPatchAll(typeof(Plugin));
        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    private void Start()
    {
        _accelerationDelayCoroutine = StartCoroutine(EnableAccelerationAfterDelay());
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Shop), "TimerUpdate")]
    public static bool TimerUpdatePrefix(Shop __instance, ref IEnumerator __result)
    {
        __result = TimerUpdateWithAcceleration(__instance);
        return false;
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
                    }

                    shop._triedChestDrop = !shop._triedChestDrop;
                }
                else if (shop.StockRefreshTimeLeft <= 0 && shop._catInventory.ChestToken.m_unQuantity > 0)
                {
                    shop.StockRefreshTimeLeft = 0;
                    shop._shopItem.gameObject.SetActive(value: true);
                    shop._outOfStockObj.SetActive(value: false);
                    shop._triedChestDrop = false;
                    shop.ChestIsReady = true;
                    shop._steamMultiplayer.SendChestReady(shop.ChestIsReady);
                    if (shop._showChestPopup.Value && shop._shopItem.CanBuy())
                    {
                        shop._shopVisuals.SetActive(value: true);
                    }
                }
            }
            else if (shop.StockRefreshTimeLeft <= 0 && !shop._shopVisuals.activeInHierarchy && shop._showChestPopup.Value && shop._shopItem.CanBuy())
            {
                shop._shopVisuals.SetActive(value: true);
            }

            yield return new WaitForSecondsRealtime(CalculateWaitTime());

        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.CanBuy))]
    public static void OnShopItemCanBuy(ShopItem __instance, bool __result)
    {
        if (!AutoOpenEnabled.Value || !__result || _isPaused)
            return;

        try
        {
            var price = (int)_priceField.GetValue(__instance);

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

        // 停止之前的延迟协程
        StopDelayedOpenCoroutine();

        if (OpenDelay.Value > 0)
        {
            _delayedOpenCoroutine = Instance.StartCoroutine(DelayedOpen(__instance));
        }
        else
        {
            __instance.Buy();
        }
    }

    private static IEnumerator DelayedOpen(ShopItem shopItem)
    {
        yield return new WaitForSeconds(OpenDelay.Value);
        _delayedOpenCoroutine = null!;

        if (shopItem != null)
        {
            Log.LogInfo("Executing automatic chest opening");
            shopItem.Buy();
        }
        else
        {
            Log.LogWarning("Skipping delayed auto-open: shopItem is null");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItem), nameof(ShopItem.Buy))]
    public static void OnShopItemBuy(ShopItem __instance)
    {
        // 停止延迟的自动购买协程
        StopDelayedOpenCoroutine("due to manual purchase");

        ResumeAccelerationAndAutoOpen("Manual purchase detected! ");
    }

    private static void PauseAccelerationAndAutoOpen(string reason)
    {
        if (!_isPaused)
        {
            _isPaused = true;
            Log.LogWarning($"{reason} Pausing acceleration and auto-open. Will auto-resume in {AutoResumeDelay.Value} seconds if no manual purchase.");

            // 停止延迟的自动购买协程
            StopDelayedOpenCoroutine("due to pause");

            if (_autoResumeCoroutine != null)
            {
                Instance.StopCoroutine(_autoResumeCoroutine);
            }
            _autoResumeCoroutine = Instance.StartCoroutine(AutoResumeAfterDelay());
        }
    }

    private static void ResumeAccelerationAndAutoOpen(string reason)
    {
        if (_isPaused)
        {
            _isPaused = false;

            if (_autoResumeCoroutine != null)
            {
                Instance.StopCoroutine(_autoResumeCoroutine);
                _autoResumeCoroutine = null!;
            }

            Log.LogInfo($"{reason} Resuming acceleration and auto-open functionality.");
        }
    }

    private static IEnumerator AutoResumeAfterDelay()
    {
        yield return new WaitForSeconds(AutoResumeDelay.Value);

        if (_isPaused)
        {
            _isPaused = false;
            _autoResumeCoroutine = null!;
            Log.LogInfo($"Auto-resuming after {AutoResumeDelay.Value} seconds of pause. Attempting to restore functionality.");

            var shop = FindAnyObjectByType<Shop>();
            if (shop != null && shop._shopItem.CanBuy())
                Log.LogInfo("Auto open chest.");
            else
                Log.LogInfo("Not allow buy, skip auto open chest.");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChestExchanger), "InventoryResultReady")]
    public static bool OnChestExchangerInventoryResultReady(ChestExchanger __instance, ref SteamInventoryResultReady_t result)
    {
        if (__instance._resultHandle == SteamInventoryResult_t.Invalid)
        {
            return true;
        }

        if (__instance._resultHandle.m_SteamInventoryResult != result.m_handle.m_SteamInventoryResult)
        {
            return true;
        }

        var resultStatus = SteamInventory.GetResultStatus(result.m_handle);
        if (resultStatus != EResult.k_EResultOK)
        {
            PauseAccelerationAndAutoOpen($"Steam API failed with result: {resultStatus}! ");
        }

        return true;
    }

    private static IEnumerator EnableAccelerationAfterDelay()
    {
        float delay = AccelerationDelay.Value;
        Log.LogInfo($"Acceleration disabled at startup, will be enabled after {delay} seconds");
        yield return new WaitForSeconds(delay);

        _accelerationEnabled = true;
        Log.LogInfo($"Acceleration enabled! Time acceleration set to {TimeAcceleration.Value}x");
    }

    // Helper methods for optimization
    private static float CalculateWaitTime()
    {
        if (_isPaused) return 1f;
        return _accelerationEnabled ? (1f / TimeAcceleration.Value) : 1f;
    }

    private static void StopDelayedOpenCoroutine(string reason = "")
    {
        if (_delayedOpenCoroutine != null)
        {
            Instance?.StopCoroutine(_delayedOpenCoroutine);
            _delayedOpenCoroutine = null!;
            if (!string.IsNullOrEmpty(reason))
                Log.LogInfo($"Stopped delayed auto-open {reason}");
        }
    }
}