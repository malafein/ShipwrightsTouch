using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimBoatCustomizer
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.malafein.shipwrightstouch";
        public const string ModName = "Shipwright's Touch";
        public const string ModVersion = "1.0.0";
        
        public static ConfigEntry<bool> AllowShipDeconstruction;

        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake()
        {
            AllowShipDeconstruction = Config.Bind("General", "AllowShipDeconstruction", false, "Allow deconstructing ships with the hammer (middle-mouse button).");
            
            Logger.LogInfo($"{ModName} {ModVersion} is loading...");
            try 
            {
                harmony.PatchAll();
                Logger.LogInfo($"{ModName} patches applied successfully.");
            }
            catch (System.Exception e)
            {
                Logger.LogError($"{ModName} failed to apply some patches: {e}");
            }
            Logger.LogInfo($"{ModName} loaded!");
        }
    }
}
