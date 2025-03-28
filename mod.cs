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
    // Enum for file server sync mode
    public enum FileServerMode
    {
        Manual,    // User must manually trigger upload/download
        Automatic, // Automatically upload/download when app starts/closes
        Immediate  // Upload immediately after each change
    }

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
            () => GetDefaultPath("Records")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> DATA_PATH_KEY = new ModConfigurationKey<string>(
            "data_path", "The path in which item data will be stored. Changing this setting requires a game restart to apply.",
            () => GetDefaultPath("Data")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> VARIANT_PATH_KEY = new ModConfigurationKey<string>(
            "variant_path", "The path in which asset variants will be stored. Changing this setting requires a game restart to apply.",
            () => GetDefaultPath("Variants")
        );

        // Custom DB config keys
        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> CUSTOM_DB_PATH = new ModConfigurationKey<string>(
            "custom_db_path", "The path to the custom SQLite database file.",
            () => GetDefaultPath("custom_storage.db")
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> USE_CUSTOM_DB = new ModConfigurationKey<bool>(
            "use_custom_db", "Use custom SQLite database instead of JSON files.",
            () => true
        );

        // File Server config keys
        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> USE_REMOTE_STORAGE = new ModConfigurationKey<bool>(
            "use_remote_storage", "Store assets and database on a remote file server (NextCloud/WebDAV).",
            () => false
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> FILESERVER_URL = new ModConfigurationKey<string>(
            "fileserver_url", "The URL of the file server (NextCloud/WebDAV).",
            () => "https://example.com/nextcloud"
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> FILESERVER_USERNAME = new ModConfigurationKey<string>(
            "fileserver_username", "Username for the file server.",
            () => "username"
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> FILESERVER_PASSWORD = new ModConfigurationKey<string>(
            "fileserver_password", "Password for the file server.",
            () => "password"
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<string> FILESERVER_REMOTE_PATH = new ModConfigurationKey<string>(
            "fileserver_remote_path", "Remote path on the file server where files will be stored.",
            () => "resonite/storage"
        );

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<FileServerMode> FILESERVER_MODE = new ModConfigurationKey<FileServerMode>(
            "fileserver_mode", "How to upload/download from the file server.",
            () => FileServerMode.Automatic
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

        // Custom database service
        private static CustomDBStorage _customDBStorage;

        // File server service
        private static FileServerService _fileServerService;

        private static string GetDefaultPath(string subfolder)
        {
            try
            {
                // If Engine is available, use it
                if (Engine.Current != null)
                {
                    return Path.Combine(Engine.Current.LocalDB.PermanentPath, "LocalStorage", subfolder);
                }
                else
                {
                    // Fallback to %AppData%\Resonite
                    string fallbackPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Resonite", "LocalStorage", subfolder);
                    return fallbackPath;
                }
            }
            catch
            {
                // Ultimate fallback if all else fails
                return Path.Combine(Directory.GetCurrentDirectory(), "LocalStorage", subfolder);
            }
        }

        public override void OnEngineInit()
        {
            Config = GetConfiguration();

            // Create a single Harmony instance for both systems
            Harmony harmony = new Harmony("com.xlinka.AdvancedInventoryWithStorage");

            // Initialize all subsystems
            InventorySearchSystem.Initialize(Config, harmony);

            // Initialize the LocalStorage system
            LocalStorageSystem.Initialize(Config, harmony);

            // Initialize custom database if enabled
            if (Config.GetValue(USE_CUSTOM_DB))
            {
                try
                {
                    _customDBStorage = new CustomDBStorage(Config);

                    // We need to initialize async, but can't use async in OnEngineInit
                    Task.Run(async () => {
                        await _customDBStorage.InitializeAsync();
                    });

                    Debug("Custom database storage initialized");
                }
                catch (Exception ex)
                {
                    Error($"Failed to initialize custom database storage: {ex.Message}");
                }
            }

            // Initialize file server if enabled
            if (Config.GetValue(USE_REMOTE_STORAGE))
            {
                try
                {
                    _fileServerService = new FileServerService(Config);

                    // Check connection and initialize
                    Task.Run(async () => {
                        if (await _fileServerService.TestConnection())
                        {
                            await _fileServerService.InitializeFileServerStructure();
                            Debug("Connected to file server successfully");

                            // If automatic mode, download all content at startup
                            if (Config.GetValue(FILESERVER_MODE) == FileServerMode.Automatic)
                            {
                                await DownloadFromFileServer();
                            }
                        }
                        else
                        {
                            Error("Failed to connect to file server");
                        }
                    });

                    Debug("File server service initialized");
                }
                catch (Exception ex)
                {
                    Error($"Failed to initialize file server service: {ex.Message}");
                }
            }

            // Set up hooks for WorldSavingPatches if using custom DB
            if (Config.GetValue(USE_CUSTOM_DB) && _customDBStorage != null)
            {
                try
                {
                    // Use reflection to call SetCustomDBStorage to avoid compile-time errors if method doesn't exist
                    var method = typeof(WorldSavingPatches).GetMethod("SetCustomDBStorage",
                        BindingFlags.Public | BindingFlags.Static);

                    if (method != null)
                    {
                        method.Invoke(null, new object[] { _customDBStorage });
                        Debug("WorldSavingPatches integration enabled");
                    }
                }
                catch (Exception ex)
                {
                    Error($"Failed to set up WorldSavingPatches integration: {ex.Message}");
                }
            }

            WorldSavingPatches.Initialize(harmony);

            // Apply all patches in a single operation
            harmony.PatchAll();

            Debug("AdvancedInventoryWithStorage initialized successfully!");

            // Register event for when game is closing (to upload DB in automatic mode)
            Engine.Current.OnShutdown += OnGameShutdown;
        }

        private void OnGameShutdown()
        {
            // If automatic mode and remote storage is enabled, upload DB
            if (Config.GetValue(USE_REMOTE_STORAGE) &&
                Config.GetValue(FILESERVER_MODE) == FileServerMode.Automatic &&
                _fileServerService != null)
            {
                // We need to do this synchronously since game is shutting down
                UploadToFileServer().Wait();
            }
        }

        /// <summary>
        /// Downloads everything from the file server
        /// </summary>
        public static async Task DownloadFromFileServer()
        {
            Debug("Downloading assets from file server...");

            // Download database
            if (Config.GetValue(USE_CUSTOM_DB))
            {
                string dbPath = Config.GetValue(CUSTOM_DB_PATH);
                await _fileServerService.DownloadDatabase(dbPath);
            }

            // Download record files
            await _fileServerService.DownloadDirectory("records", LocalStorageSystem.REC_PATH);

            // Download asset files
            await _fileServerService.DownloadDirectory("assets", LocalStorageSystem.DATA_PATH);

            // Download variants if enabled
            if (Config.GetValue(STORE_ASSET_VARIANTS))
            {
                await _fileServerService.DownloadDirectory("variants", LocalStorageSystem.VARIANT_PATH);
            }

            Debug("Download from file server complete");
        }

        /// <summary>
        /// Uploads everything to the file server
        /// </summary>
        public static async Task UploadToFileServer()
        {
            Debug("Uploading assets to file server...");

            // Upload database
            if (Config.GetValue(USE_CUSTOM_DB))
            {
                string dbPath = Config.GetValue(CUSTOM_DB_PATH);
                await _fileServerService.UploadDatabase(dbPath);
            }

            // Upload record files
            await _fileServerService.UploadDirectory(LocalStorageSystem.REC_PATH, "records");

            // Upload asset files
            await _fileServerService.UploadDirectory(LocalStorageSystem.DATA_PATH, "assets");

            // Upload variants if enabled
            if (Config.GetValue(STORE_ASSET_VARIANTS))
            {
                await _fileServerService.UploadDirectory(LocalStorageSystem.VARIANT_PATH, "variants");
            }

            Debug("Upload to file server complete");
        }

        private static void Debug(string message)
        {
            UniLog.Log($"[AdvancedInventoryWithStorage] {message}");
        }

        private static void Error(string message)
        {
            UniLog.Error($"[AdvancedInventoryWithStorage] {message}");
        }
    }
}