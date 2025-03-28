using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using System;
using Elements.Core;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AdvancedInventoryWithStorage
{
    public class Mod : ResoniteMod
    {
        // Combined mod information
        public override string Name => "AdvancedInventoryWithStorage";
        public override string Author => "xlinka & art0007i";
        public override string Version => "1.0.0";
        public override string Link => "https://github.com/xlinka/AdvancedInventoryWithStorage";

        // Inventory Search config keys
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> SEARCH_ENABLED =
            new("search_enabled", "Enable Inventory Search", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> CASE_SENSITIVE =
            new("case_sensitive", "Case-Sensitive Search", () => false);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<InventorySearchSystem.SearchScope> DEFAULT_SEARCH_SCOPE =
            new("default_search_scope", "Default Search Scope", () => InventorySearchSystem.SearchScope.All);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<InventorySearchSystem.SortMethod> DEFAULT_SORT_METHOD =
            new("default_sort_method", "Default Sort Method", () => InventorySearchSystem.SortMethod.RecentlyAdded);

        // Local Storage config keys
        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> REC_PATH_KEY = new ModConfigurationKey<string>(
            "record_path", "The path in which records will be stored. Changing this setting requires a game restart to apply.",
            () => Path.Combine(Engine.Current.LocalDB.PermanentPath, "LocalStorage", "Records")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> DATA_PATH_KEY = new ModConfigurationKey<string>(
            "data_path", "The path in which item data will be stored. Changing this setting requires a game restart to apply.",
            () => Path.Combine(Engine.Current.LocalDB.PermanentPath, "LocalStorage", "Data")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> VARIANT_PATH_KEY = new ModConfigurationKey<string>(
            "variant_path", "The path in which asset variants will be stored. Changing this setting requires a game restart to apply.",
            () => Path.Combine(Engine.Current.LocalDB.PermanentPath, "LocalStorage", "Variants")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> ENABLE_SEARCH_INTEGRATION = new ModConfigurationKey<bool>(
            "enable_search_integration", "Enable integration between search and local storage.",
            () => true
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> STORE_ASSET_VARIANTS = new ModConfigurationKey<bool>(
            "store_asset_variants", "Store asset variants locally instead of using the cache/local DB.",
            () => true
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> INDEX_TAGS = new ModConfigurationKey<bool>(
            "index_tags", "Index and enable searching by tags for local items.",
            () => true
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> INDEX_JSON_CONTENT = new ModConfigurationKey<bool>(
            "index_json_content", "Index and enable searching by content in JSON files (more intensive).",
            () => false
        );

        // Shared config and resources
        public static ModConfiguration Config;

        public override void OnEngineInit()
        {
            Config = GetConfiguration();

            // Create a single Harmony instance for both systems
            Harmony harmony = new Harmony("com.xlinka.AdvancedInventoryWithStorage");

            // Initialize all subsystems
            InventorySearchSystem.Initialize(Config, harmony);
            LocalStorageSystem.Initialize(Config, harmony);
            WorldSavingPatches.Initialize(harmony);

            // Apply all patches in a single operation
            harmony.PatchAll();

            Debug("AdvancedInventoryWithStorage initialized successfully!");
        }
    }
}