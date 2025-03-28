using System;
using System.Linq;
using FrooxEngine.Store;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using FrooxEngine.UIX;
using Elements.Core;
using System.IO;
using Newtonsoft.Json;
using Elements.Assets;
using System.Collections.Concurrent;
using HarmonyLib;
using System.Reflection;
using System.Threading;
using SkyFrost.Base;
using ResoniteModLoader;
using System.Text.RegularExpressions;

namespace AdvancedInventoryWithStorage
{
    public static class LocalStorageSystem
    {
        public const string LOCAL_OWNER = "L-LocalStorage";

        public static bool HIDE_LOCAL = true;
        public static string REC_PATH;
        public static string DATA_PATH;
        public static string VARIANT_PATH;

        // Cache for asset variant metadata
        private static ConcurrentDictionary<string, AssetVariantInfo> assetVariantsCache = new ConcurrentDictionary<string, AssetVariantInfo>();

        // Class to store asset variant information
        public class AssetVariantInfo
        {
            public Uri OriginalAssetUri { get; set; }
            public Dictionary<string, string> VariantPaths { get; set; } = new Dictionary<string, string>();
            public DateTime LastAccessed { get; set; }
        }

        // Index data structure for local items to enable faster searching
        private static Dictionary<string, LocalItemSearchInfo> localItemsSearchIndex = new Dictionary<string, LocalItemSearchInfo>();

        // Class to store search-related info for local items
        public class LocalItemSearchInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public HashSet<string> Tags { get; set; } = new HashSet<string>();
            public string JsonContent { get; set; } // Optional, only populated if INDEX_JSON_CONTENT is true
            public DateTime LastModified { get; set; }
        }

        private static ModConfiguration _config;

        public static void Initialize(ModConfiguration config, Harmony harmony)
        {
            _config = config;

            // Note: We don't call harmony.PatchAll() here as the main Mod class will handle that
            Msg("LocalStorage system initializing...");
        }

        private static void Msg(string message)
        {
            UniLog.Log($"[LocalStorage] {message}");
        }

        private static void Error(string message)
        {
            UniLog.Error($"[LocalStorage] {message}");
        }

        private static void Error(Exception ex)
        {
            UniLog.Error($"[LocalStorage] Exception: {ex.Message}\n{ex.StackTrace}");
        }

        // Sanitize file names to remove invalid characters
        public static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "unnamed";

            // Replace invalid file name characters with underscores
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            // Replace HTML and special characters
            string sanitized = Regex.Replace(fileName, invalidRegStr, "_");

            // Ensure the name isn't just periods (Windows restriction)
            sanitized = Regex.Replace(sanitized, @"^\.+$", "_");

            // Trim leading/trailing spaces and dots
            sanitized = sanitized.Trim('.', ' ');

            // If after sanitization the name is empty, use a default
            if (string.IsNullOrEmpty(sanitized))
                return "unnamed";

            // Limit length to avoid path too long errors
            int maxLength = 64;
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);

            return sanitized;
        }

        public static string ResolveLstore(Uri uri)
        {
            var unsafePath = Path.GetFullPath(DATA_PATH + Uri.UnescapeDataString(uri.AbsolutePath)).Replace('\\', '/');
            if (unsafePath.StartsWith(DATA_PATH))
            {
                if (File.Exists(unsafePath))
                {
                    return unsafePath;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                throw new FileNotFoundException("Unexpected path was received. Path: " + unsafePath + "\nDataPath: " + DATA_PATH);
            }
        }

        // Special URI scheme for local asset variants
        public static string ResolveLvariant(Uri uri)
        {
            try
            {
                // Parse the variant info from the URI
                string path = Uri.UnescapeDataString(uri.AbsolutePath);
                string queryPart = Uri.UnescapeDataString(uri.Query).TrimStart('?');

                // Construct the path to the variant
                string variantFilePath = Path.Combine(VARIANT_PATH, path, queryPart).Replace('\\', '/');

                if (File.Exists(variantFilePath))
                {
                    return variantFilePath;
                }

                // If the variant doesn't exist yet, we need to check if we have the base asset
                string baseAssetPath = Path.Combine(DATA_PATH, path).Replace('\\', '/');
                if (!File.Exists(baseAssetPath))
                {
                    return null;
                }

                // Generate the variant
                return GenerateAssetVariant(baseAssetPath, variantFilePath, queryPart);
            }
            catch (Exception e)
            {
                Error($"Error resolving local variant: {e.Message}");
                return null;
            }
        }

        private static string GenerateAssetVariant(string baseAssetPath, string variantFilePath, string variantIdentifier)
        {
            try
            {
                // Ensure the directory exists
                string variantDirectory = Path.GetDirectoryName(variantFilePath);
                if (!Directory.Exists(variantDirectory))
                {
                    Directory.CreateDirectory(variantDirectory);
                }

                // Determine asset type from file extension and content
                string extension = Path.GetExtension(baseAssetPath).ToLowerInvariant();
                AssetVariantType variantType = AssetVariantType.Texture; // Default to texture

                // Try to determine the asset type
                switch (extension)
                {
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".bmp":
                    case ".tga":
                        variantType = AssetVariantType.Texture;
                        break;
                    case ".glb":
                    case ".gltf":
                    case ".obj":
                    case ".fbx":
                        variantType = AssetVariantType.Mesh;
                        break;
                        // Add more type detection as needed
                }

                // Create the appropriate variant descriptor based on the type
                IAssetVariantDescriptor variantDescriptor = AssetVariantHelper.FromIdentifier(variantType, variantIdentifier);

                // Generate the variant
                List<ComputedVariantResult> results = AssetVariantHelper.GenerateVariants(
                    baseAssetPath,
                    variantDescriptor,
                    -1, // Use default thread count
                    null,
                    (variant) => variant == variantIdentifier // Only generate the requested variant
                );

                // Find the generated variant in results
                foreach (var result in results)
                {
                    if (result.identifier == variantIdentifier && result.file != null)
                    {
                        // Copy the generated file to our variant path
                        File.Copy(result.file, variantFilePath, true);

                        // Update the cache
                        Uri originalUri = new Uri($"lstore:///{Path.GetFileName(baseAssetPath)}");
                        string variantKey = GetVariantCacheKey(originalUri, variantIdentifier);

                        var variantInfo = assetVariantsCache.GetOrAdd(variantKey, new AssetVariantInfo
                        {
                            OriginalAssetUri = originalUri,
                            LastAccessed = DateTime.UtcNow
                        });

                        variantInfo.VariantPaths[variantIdentifier] = variantFilePath;
                        variantInfo.LastAccessed = DateTime.UtcNow;

                        return variantFilePath;
                    }
                }

                // If we didn't find the variant, return null
                return null;
            }
            catch (Exception e)
            {
                Error($"Error generating asset variant: {e.Message}");
                return null;
            }
        }

        private static string GetVariantCacheKey(Uri assetUri, string variantIdentifier)
        {
            return $"{assetUri}?{variantIdentifier}";
        }

        // Method to build or update the search index for all local items
        public static void BuildLocalItemsSearchIndex()
        {
            if (HIDE_LOCAL || string.IsNullOrEmpty(REC_PATH))
                return;

            try
            {
                localItemsSearchIndex.Clear();
                RecursivelyIndexDirectory(Path.Combine(REC_PATH, "Inventory"));
                Msg($"LocalStorage search index built with {localItemsSearchIndex.Count} items");
            }
            catch (Exception e)
            {
                Error($"Failed to build local items search index: {e.Message}");
            }
        }

        private static void RecursivelyIndexDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return;

            // Index all JSON files in the current directory
            foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
            {
                try
                {
                    IndexLocalItem(file);
                }
                catch (Exception e)
                {
                    Error($"Error indexing file {file}: {e.Message}");
                }
            }

            // Recursively index subdirectories
            foreach (var dir in Directory.GetDirectories(directoryPath))
            {
                RecursivelyIndexDirectory(dir);
            }
        }

        private static void IndexLocalItem(string filePath)
        {
            try
            {
                var fileContent = File.ReadAllText(filePath);
                var record = JsonConvert.DeserializeObject<SkyFrost.Base.Record>(fileContent);

                if (record == null)
                    return;

                var searchInfo = new LocalItemSearchInfo
                {
                    Name = record.Name,
                    Path = record.Path,
                    LastModified = File.GetLastWriteTime(filePath)
                };

                // Add tags if they exist and indexing is enabled
                if (_config.GetValue(Mod.INDEX_TAGS) && record.Tags != null)
                {
                    searchInfo.Tags = new HashSet<string>(record.Tags);
                }

                // Optionally index JSON content if enabled
                if (_config.GetValue(Mod.INDEX_JSON_CONTENT) && record.AssetURI != null && record.AssetURI.StartsWith("lstore:///"))
                {
                    var assetPath = Path.Combine(DATA_PATH, record.AssetURI.Substring(10));
                    if (File.Exists(assetPath))
                    {
                        searchInfo.JsonContent = File.ReadAllText(assetPath);
                    }
                }

                // Use record ID as key for the index
                localItemsSearchIndex[record.RecordId] = searchInfo;
            }
            catch (Exception e)
            {
                Error($"Error processing file {filePath} for search index: {e.Message}");
            }
        }

        // Method to search local items
        public static List<FrooxEngine.Store.Record> SearchLocalItems(string searchText, bool caseSensitive = false, string searchScope = "All")
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return new List<FrooxEngine.Store.Record>();

            StringComparison comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var results = new List<FrooxEngine.Store.Record>();

            // Ensure the index is built
            if (localItemsSearchIndex.Count == 0)
            {
                BuildLocalItemsSearchIndex();
            }

            // Search through the index
            foreach (var indexEntry in localItemsSearchIndex)
            {
                var info = indexEntry.Value;
                bool isMatch = false;

                // Check name
                if (info.Name != null && info.Name.IndexOf(searchText, comparison) >= 0)
                {
                    isMatch = true;
                }

                // Check tags if not already matched
                if (!isMatch && _config.GetValue(Mod.INDEX_TAGS) && info.Tags != null)
                {
                    foreach (var tag in info.Tags)
                    {
                        if (tag != null && tag.IndexOf(searchText, comparison) >= 0)
                        {
                            isMatch = true;
                            break;
                        }
                    }
                }

                // Check JSON content if enabled and not already matched
                if (!isMatch && _config.GetValue(Mod.INDEX_JSON_CONTENT) && info.JsonContent != null)
                {
                    if (info.JsonContent.IndexOf(searchText, comparison) >= 0)
                    {
                        isMatch = true;
                    }
                }

                if (isMatch)
                {
                    // Load the actual Record from disk to return it
                    var recordPath = Path.Combine(REC_PATH, info.Path, info.Name + ".json").Replace('\\', '/');
                    if (File.Exists(recordPath))
                    {
                        try
                        {
                            var fileContent = File.ReadAllText(recordPath);
                            var record = JsonConvert.DeserializeObject<FrooxEngine.Store.Record>(fileContent);
                            if (record != null)
                            {
                                results.Add(record);
                            }
                        }
                        catch (Exception e)
                        {
                            Error($"Error loading record for search results: {e.Message}");
                        }
                    }
                }
            }

            return results;
        }

        // Helper function to ensure all parent directories exist
        private static void EnsureDirectoryExists(string path)
        {
            try
            {
                // Normalize the path to use forward slashes
                path = path.Replace('\\', '/');

                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory))
                {
                    // Create all directories in the path
                    Directory.CreateDirectory(directory);
                    Msg($"Created directory: {directory}");
                }
            }
            catch (Exception e)
            {
                Error($"Failed to create directory for {path}: {e.Message}");
            }
        }

        // Helper method to ensure all necessary directories exist in the path structure
        private static void EnsureDirectoryStructure(string path)
        {
            try
            {
                // Normalize the path to use forward slashes
                path = path.Replace('\\', '/');

                // Ensure path is under the Inventory folder
                if (!path.StartsWith("Inventory/") && !path.Equals("Inventory"))
                {
                    path = Path.Combine("Inventory", path).Replace('\\', '/');
                    Msg($"Normalized path to ensure it's under Inventory: {path}");
                }

                // Create directories in the Records path
                string fullRecPath = Path.Combine(REC_PATH, path);
                if (!Directory.Exists(fullRecPath))
                {
                    Directory.CreateDirectory(fullRecPath);
                    Msg($"Created record directory: {fullRecPath}");
                }

                // Create directories in the Data path
                string fullDataPath = Path.Combine(DATA_PATH, path);
                if (!Directory.Exists(fullDataPath))
                {
                    Directory.CreateDirectory(fullDataPath);
                    Msg($"Created data directory: {fullDataPath}");
                }

                // Create directories in the Variant path if necessary
                if (_config.GetValue(Mod.STORE_ASSET_VARIANTS))
                {
                    string fullVariantPath = Path.Combine(VARIANT_PATH, path);
                    if (!Directory.Exists(fullVariantPath))
                    {
                        Directory.CreateDirectory(fullVariantPath);
                        Msg($"Created variant directory: {fullVariantPath}");
                    }
                }
            }
            catch (Exception e)
            {
                Error($"Failed to create directory structure for {path}: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(LocalDB), "Initialize")]
        public static class LateInitPatch
        {
            public static void Postfix()
            {
                REC_PATH = _config.GetValue(Mod.REC_PATH_KEY).Replace('\\', '/');
                DATA_PATH = _config.GetValue(Mod.DATA_PATH_KEY).Replace('\\', '/');
                VARIANT_PATH = _config.GetValue(Mod.VARIANT_PATH_KEY).Replace('\\', '/');

                try
                {
                    if (!Directory.Exists(REC_PATH))
                    {
                        // Create the base record directory
                        Directory.CreateDirectory(REC_PATH);

                        // Ensure the Inventory directory exists
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory"));

                        // Create common subdirectories in Inventory
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory", "Worlds"));
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory", "Models"));
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory", "Textures"));
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory", "Materials"));
                        Directory.CreateDirectory(Path.Combine(REC_PATH, "Inventory", "Audio"));
                    }
                }
                catch (Exception e)
                {
                    HIDE_LOCAL = true;
                    Error("A critical error has occurred while creating record directory");
                    Error(e);
                }

                try
                {
                    if (!Directory.Exists(DATA_PATH))
                    {
                        // Create the base data directory
                        Directory.CreateDirectory(DATA_PATH);

                        // Ensure the Inventory directory exists
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory"));

                        // Create common subdirectories in Inventory
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory", "Worlds"));
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory", "Models"));
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory", "Textures"));
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory", "Materials"));
                        Directory.CreateDirectory(Path.Combine(DATA_PATH, "Inventory", "Audio"));
                    }
                }
                catch (Exception e)
                {
                    HIDE_LOCAL = true;
                    Error("A critical error has occurred while creating data directory");
                    Error(e);
                }

                try
                {
                    if (!Directory.Exists(VARIANT_PATH))
                    {
                        // Create the base variant directory
                        Directory.CreateDirectory(VARIANT_PATH);

                        // Create the Inventory directory and common subdirectories
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory"));
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory", "Worlds"));
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory", "Models"));
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory", "Textures"));
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory", "Materials"));
                        Directory.CreateDirectory(Path.Combine(VARIANT_PATH, "Inventory", "Audio"));
                    }
                }
                catch (Exception e)
                {
                    HIDE_LOCAL = true;
                    Error("A critical error has occurred while creating variant directory");
                    Error(e);
                }

                Msg("Initialized LocalStorage\nData Path: " + DATA_PATH + "\nRecord Path: " + REC_PATH + "\nVariant Path: " + VARIANT_PATH);
                HIDE_LOCAL = false;

                // Build the search index after initialization
                if (_config.GetValue(Mod.ENABLE_SEARCH_INTEGRATION))
                {
                    BuildLocalItemsSearchIndex();
                }
            }
        }

        [HarmonyPatch(typeof(InventoryBrowser))]
        public static class InventoryPatch
        {
            [HarmonyPatch("ShowInventoryOwners")]
            [HarmonyPrefix]
            public static bool ShowInventoriesPrefix(InventoryBrowser __instance)
            {
                if (!HIDE_LOCAL && __instance.Engine.Cloud.CurrentUser == null && __instance.CurrentDirectory.OwnerId != LOCAL_OWNER && __instance.World.IsUserspace())
                {
                    RecordDirectory directory = new RecordDirectory(LOCAL_OWNER, "Inventory", __instance.Engine, null);
                    __instance.Open(directory, SlideSwapRegion.Slide.Left);
                    return false;
                }
                else if (__instance.CurrentDirectory.OwnerId == LOCAL_OWNER && __instance.Engine.Cloud.CurrentUser == null && __instance.World.IsUserspace())
                {
                    Traverse.Create(__instance).Method("TryInitialize").GetValue(null);
                    return false;
                }
                return true;
            }

            [HarmonyPatch("BeginGeneratingNewDirectory")]
            [HarmonyPostfix]
            public static void GenerationPostfix(InventoryBrowser __instance, UIBuilder __result, ref GridLayout folders)
            {
                if (!HIDE_LOCAL && __instance.CurrentDirectory == null && __instance.World.IsUserspace())
                {
                    var builder = __result;
                    builder.NestInto(folders.Slot);
                    var colour = MathX.Lerp(colorX.Lime, colorX.Black, 0.5f);
                    var openFunc = (ButtonEventHandler<string>)AccessTools.Method(typeof(InventoryBrowser), "OpenInventory").CreateDelegate(typeof(ButtonEventHandler<string>), __instance);

                    builder.Button("Local Storage", colour, openFunc, LOCAL_OWNER, __instance.ActualDoublePressInterval);

                    builder.NestOut();
                }
            }

            [HarmonyPatch("OnChanges")]
            [HarmonyPostfix]
            public static void OnChangesPostFix(InventoryBrowser __instance, ref SyncRef<Button> ____inventoriesButton)
            {
                ____inventoriesButton.Target.Enabled = true;
                return;
            }

            [HarmonyPatch("OnCommonUpdate")]
            [HarmonyPostfix]
            public static void OnCommonUpdate(InventoryBrowser __instance)
            {
                if (HIDE_LOCAL &&
                    !__instance.World.IsUserspace()
                    && __instance.CurrentDirectory != null
                    && __instance.CurrentDirectory.OwnerId == LOCAL_OWNER)
                {
                    __instance.Open(null, SlideSwapRegion.Slide.Right);
                }
            }
        }

        // This patch integrates with the InventorySearch mod
        [HarmonyPatch]
        public static class SearchIntegrationPatch
        {
            // Use Harmony to find and patch the PerformSearch method in the InventorySearchSystem
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(InventorySearchSystem), "PerformSearch");
            }

            // This will be applied to integrate local search with inventory search
            public static void Prefix(InventoryBrowser browser, InventorySearchSystem.SearchBarData searchBarData, string searchText)
            {
                if (!_config.GetValue(Mod.ENABLE_SEARCH_INTEGRATION) || string.IsNullOrEmpty(searchText))
                    return;

                try
                {
                    // Check if we're in local storage
                    if (browser == null || browser.CurrentDirectory?.OwnerId != LOCAL_OWNER)
                        return;

                    // Get search parameters
                    var scopeString = searchBarData.CurrentSearchScope.ToString();
                    var caseSensitive = _config.GetValue(Mod.CASE_SENSITIVE);

                    // Perform the actual local search
                    var results = SearchLocalItems(searchText, caseSensitive, scopeString);

                    // The actual filtering will be handled by RecordDirectory patches
                }
                catch (Exception e)
                {
                    Error($"Error in search integration: {e.Message}");
                }
            }
        }

        // This patch handles the RecordDirectory to make it work with the search functionality
        [HarmonyPatch(typeof(RecordDirectory))]
        public static class RecordDirectoryPatch
        {
            [HarmonyPatch("get_CanWrite")]
            [HarmonyPrefix]
            public static bool CanWrite(ref bool __result, RecordDirectory __instance)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    __result = true;
                    return false;
                }
                return true;
            }

            [HarmonyPatch("TryLocalCacheLoad")]
            [HarmonyPrefix]
            public static bool CacheLoad(ref Task<bool> __result, RecordDirectory __instance)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    __result = Task<bool>.Run(() => false);
                    return false;
                }
                return true;
            }

            [HarmonyPatch("LoadFrom", new Type[] { typeof(string), typeof(string) })]
            [HarmonyPrefix]
            public static bool LoadFrom(string ownerId, string path, ref Task __result, RecordDirectory __instance)
            {
                if (ownerId == LOCAL_OWNER)
                {
                    __result = Task.Run(() =>
                    {
                        List<RecordDirectory> subdirs = new List<RecordDirectory>();
                        List<FrooxEngine.Store.Record> recs = new List<FrooxEngine.Store.Record>();

                        path = path.Replace('\\', '/');

                        // Ensure path is always under Inventory folder
                        string normalizedPath = path;
                        if (!normalizedPath.StartsWith("Inventory/") && !normalizedPath.Equals("Inventory"))
                        {
                            normalizedPath = Path.Combine("Inventory", normalizedPath).Replace('\\', '/');
                            Msg($"Normalized path from {path} to {normalizedPath}");
                        }

                        var p = Path.Combine(REC_PATH, normalizedPath);

                        // Create directory if it doesn't exist
                        if (!Directory.Exists(p))
                        {
                            try
                            {
                                Directory.CreateDirectory(p);
                            }
                            catch (Exception e)
                            {
                                Error($"Failed to create directory {p}: {e.Message}");
                            }
                        }

                        foreach (string dir in Directory.EnumerateDirectories(p))
                        {
                            var dirRec = RecordHelper.CreateForDirectory<FrooxEngine.Store.Record>(LOCAL_OWNER, normalizedPath, Path.GetFileNameWithoutExtension(dir));
                            subdirs.Add(new RecordDirectory(dirRec, __instance, __instance.Engine));
                        }

                        foreach (string file in Directory.EnumerateFiles(p))
                        {
                            if (Path.GetExtension(file) != ".json") continue;

                            var garbo = new FrooxEngine.Store.Record();

                            var fs = File.ReadAllText(file);
                            // Json parsing is difficult, ok
                            var record = JsonConvert.DeserializeObject<SkyFrost.Base.Record>(fs);

                            garbo.RecordId = record.RecordId;
                            garbo.OwnerId = record.OwnerId;
                            garbo.AssetURI = record.AssetURI;
                            garbo.Name = record.Name;
                            garbo.Description = record.Description;
                            garbo.RecordType = record.RecordType;
                            garbo.OwnerName = record.OwnerName;
                            garbo.Tags = record.Tags;
                            garbo.Path = record.Path;
                            garbo.ThumbnailURI = record.ThumbnailURI;
                            garbo.IsPublic = record.IsPublic;
                            garbo.IsForPatrons = record.IsForPatrons;
                            garbo.IsListed = record.IsListed;
                            garbo.LastModificationTime = record.LastModificationTime;
                            // RootRecordId doesnt exist on CloudX records
                            garbo.CreationTime = record.CreationTime;
                            garbo.FirstPublishTime = record.FirstPublishTime;
                            garbo.Visits = record.Visits;
                            garbo.Rating = record.Rating;
                            garbo.RandomOrder = record.RandomOrder;
                            garbo.Submissions = record.Submissions;
                            garbo.AssetManifest = record.AssetManifest;

                            if (garbo.RecordType == "link")
                            {
                                subdirs.Add(new RecordDirectory(garbo, __instance, __instance.Engine));
                            }
                            else
                            {
                                recs.Add(garbo);
                            }
                        }

                        AccessTools.Field(typeof(RecordDirectory), "subdirectories").SetValue(__instance, subdirs);
                        AccessTools.Field(typeof(RecordDirectory), "records").SetValue(__instance, recs);

                        // When the inventory search mod is active, we need to apply any search filtering
                        TryApplySearchFilter(__instance);
                    });
                    return false;
                }
                return true;
            }

            // Helper method to apply search filter when using search functionality
            private static void TryApplySearchFilter(RecordDirectory directory)
            {
                if (!_config.GetValue(Mod.ENABLE_SEARCH_INTEGRATION))
                    return;

                try
                {
                    // Find the active InventoryBrowser
                    var browsers = directory.Engine.WorldManager.FocusedWorld.GetGloballyRegisteredComponents<InventoryBrowser>();
                    var browser = browsers.FirstOrDefault(b => b.CurrentDirectory?.OwnerId == LOCAL_OWNER);

                    if (browser == null)
                        return;

                    // Try to access the search bar data from our own system
                    var searchBarsField = AccessTools.Field(typeof(InventorySearchSystem), "_searchBars");
                    var searchBars = searchBarsField.GetValue(null) as Dictionary<InventoryBrowser, InventorySearchSystem.SearchBarData>;

                    if (searchBars == null || !searchBars.TryGetValue(browser, out var searchBarData))
                        return;

                    var searchText = searchBarData.CurrentSearchText;

                    if (string.IsNullOrEmpty(searchText))
                        return;

                    if (!searchBarData.IsActive)
                        return;

                    // Now we can filter the items based on the search criteria
                    var subdirectories = AccessTools.Field(typeof(RecordDirectory), "subdirectories").GetValue(directory) as List<RecordDirectory>;
                    var records = AccessTools.Field(typeof(RecordDirectory), "records").GetValue(directory) as List<FrooxEngine.Store.Record>;

                    // Check if we need to filter folders
                    if (searchBarData.CurrentSearchScope != InventorySearchSystem.SearchScope.ItemsOnly)
                    {
                        // For folders, we keep only those whose names match the search
                        for (int i = subdirectories.Count - 1; i >= 0; i--)
                        {
                            var subdir = subdirectories[i];
                            bool isMatch = false;

                            // Check directory name
                            string name = subdir.Name;
                            if (name != null)
                            {
                                StringComparison comparison = _config.GetValue(Mod.CASE_SENSITIVE)
                                    ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase;

                                isMatch = name.IndexOf(searchText, comparison) >= 0;
                            }

                            // Remove if no match
                            if (!isMatch)
                            {
                                subdirectories.RemoveAt(i);
                            }
                        }
                    }
                    else
                    {
                        // If ItemsOnly, clear all directories
                        subdirectories.Clear();
                    }

                    // Check if we need to filter items
                    if (searchBarData.CurrentSearchScope != InventorySearchSystem.SearchScope.FoldersOnly)
                    {
                        // For items, use our SearchLocalItems method to find matches
                        var matchedRecords = SearchLocalItems(searchText, _config.GetValue(Mod.CASE_SENSITIVE), searchBarData.CurrentSearchScope.ToString());

                        // Replace the records with only those that match
                        records.Clear();
                        foreach (var record in matchedRecords)
                        {
                            // Only include records from the current directory
                            if (record.Path == directory.Path)
                            {
                                records.Add(record);
                            }
                        }
                    }
                    else
                    {
                        // If FoldersOnly, clear all items
                        records.Clear();
                    }
                }
                catch (Exception e)
                {
                    Error($"Error applying search filter: {e.Message}");
                }
            }

           [HarmonyPatch("AddItem")]
[HarmonyPrefix]
public static bool AddItem(string name, Uri objectData, Uri thumbnail, IEnumerable<string> tags, ref FrooxEngine.Store.Record __result, RecordDirectory __instance)
{
    if (__instance.OwnerId == LOCAL_OWNER)
    {
        var fixedPath = __instance.ChildRecordPath.Replace('\\', '/');
        
        // Ensure path is always under Inventory folder
        if (!fixedPath.StartsWith("Inventory/") && !fixedPath.Equals("Inventory"))
        {
            fixedPath = Path.Combine("Inventory", fixedPath).Replace('\\', '/');
            Msg($"Normalized path from {__instance.ChildRecordPath} to {fixedPath}");
        }

        // Sanitize the item name
        string sanitizedName = SanitizeFileName(name);

        // Create a unique folder for this item
        string itemFolderName = sanitizedName + "_" + Guid.NewGuid().ToString().Substring(0, 8);
        string itemPath = Path.Combine(fixedPath, itemFolderName).Replace('\\', '/');

        // Check and create parent directories if they don't exist
        EnsureDirectoryStructure(itemPath);

        var savePath = Path.Combine(DATA_PATH, itemPath, sanitizedName);
        var dataPath = savePath + ".json";
        var thumbPath = thumbnail != null
            ? savePath + Path.GetExtension(thumbnail.ToString())
            : null;

        // Ensure data directory structure exists
        EnsureDirectoryExists(dataPath);

        var dataTask = __instance.Engine.LocalDB.TryOpenAsset(objectData);
        dataTask.Wait();
        var dataStream = dataTask.Result;

        var tree = DataTreeConverter.LoadAuto(dataStream);
        using (var fs = File.CreateText(dataPath))
        {
            var wr = new JsonTextWriter(fs);
            wr.Indentation = 2;
            wr.Formatting = Formatting.Indented;
            var writeFunc = AccessTools.Method(typeof(DataTreeConverter), "Write");
            writeFunc.Invoke(null, new object[] { tree, wr });
        }

        // Store thumbnails locally if asset variant storage is enabled
        string thumbLocalPath = null;
        if (thumbnail != null)
        {
            if (_config.GetValue(Mod.STORE_ASSET_VARIANTS))
            {
                // Ensure thumbnail directory structure exists
                EnsureDirectoryExists(thumbPath);

                var thumbTask = __instance.Engine.LocalDB.TryOpenAsset(thumbnail);
                thumbTask.Wait();
                var thumbStream = thumbTask.Result;

                using (var thumbFile = File.Create(thumbPath))
                {
                    thumbStream.CopyTo(thumbFile);
                }

                thumbLocalPath = "lstore:///" + itemPath + "/" + sanitizedName + Path.GetExtension(thumbnail.ToString());
            }
            else
            {
                // Use the original thumbnail URI if not storing locally
                thumbLocalPath = thumbnail.ToString();
            }
        }

        var fileLocalPath = "lstore:///" + itemPath + "/" + sanitizedName + ".json";

        var rec = RecordHelper.CreateForObject<FrooxEngine.Store.Record>(name, __instance.OwnerId, fileLocalPath, thumbLocalPath);
        rec.Path = itemPath; // Store in item-specific path instead of shared directory
        if (tags != null)
        {
            rec.Tags = new HashSet<string>(tags);
        }

        // Ensure record directory structure exists
        var recPath = Path.Combine(REC_PATH, itemPath, sanitizedName + ".json");
        EnsureDirectoryExists(recPath);

        using (var fs = File.CreateText(recPath))
        {
            JsonSerializer serializer = new JsonSerializer();
            serializer.Formatting = Formatting.Indented;
            serializer.Serialize(fs, rec);
        }

        (AccessTools.Field(typeof(RecordDirectory), "records").GetValue(__instance) as List<FrooxEngine.Store.Record>).Add(rec);
        __result = rec;

        // Update search index with the new item if search integration is enabled
        if (_config.GetValue(Mod.ENABLE_SEARCH_INTEGRATION))
        {
            IndexLocalItem(recPath);
        }

        return false;
    }
    return true;
}

            [HarmonyPatch("AddSubdirectory")]
            [HarmonyPrefix]
            public static bool AddSubdirectory(string name, bool dummyOnly, RecordDirectory __instance, ref RecordDirectory __result)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    // Sanitize directory name
                    string sanitizedName = SanitizeFileName(name);

                    var fixedPath = __instance.ChildRecordPath.Replace('\\', '/');

                    // Ensure path is always under Inventory folder
                    if (!fixedPath.StartsWith("Inventory/") && !fixedPath.Equals("Inventory"))
                    {
                        fixedPath = Path.Combine("Inventory", fixedPath).Replace('\\', '/');
                        Msg($"Normalized path from {__instance.ChildRecordPath} to {fixedPath}");
                    }

                    var dirLoc = Path.Combine(REC_PATH, fixedPath, sanitizedName);
                    if (Directory.Exists(dirLoc))
                    {
                        throw new Exception("Subdirectory with name '" + sanitizedName + "' already exists.");
                    }

                    var rec = RecordHelper.CreateForDirectory<FrooxEngine.Store.Record>(LOCAL_OWNER, fixedPath, sanitizedName);

                    if (!dummyOnly)
                    {
                        // Create the necessary directory structure
                        EnsureDirectoryStructure(Path.Combine(fixedPath, sanitizedName));
                    }

                    var ret = new RecordDirectory(rec, __instance, __instance.Engine);
                    (AccessTools.Field(typeof(RecordDirectory), "subdirectories").GetValue(__instance) as List<RecordDirectory>).Add(ret);
                    __result = ret;
                    return false;
                }
                return true;
            }

            [HarmonyPatch("AddLinkAsync")]
            [HarmonyPrefix]
            public static bool AddLinkAsync(string name, Uri target, RecordDirectory __instance, ref Task<FrooxEngine.Store.Record> __result)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    // Sanitize the link name
                    string sanitizedName = SanitizeFileName(name);

                    var fixedPath = __instance.ChildRecordPath.Replace('\\', '/');

                    // Check and create parent directories if they don't exist
                    EnsureDirectoryStructure(fixedPath);

                    var record = RecordHelper.CreateForLink<FrooxEngine.Store.Record>(sanitizedName, __instance.OwnerId, target.ToString(), null);
                    record.Path = __instance.ChildRecordPath;

                    var recPath = Path.Combine(REC_PATH, fixedPath, sanitizedName + ".json");

                    // Ensure directory exists
                    EnsureDirectoryExists(recPath);

                    using (var fs = File.CreateText(recPath))
                    {
                        JsonSerializer serializer = new JsonSerializer();
                        serializer.Formatting = Formatting.Indented;
                        serializer.Serialize(fs, record);
                    }

                    RecordDirectory item = new RecordDirectory(record, __instance, __instance.Engine);
                    (AccessTools.Field(typeof(RecordDirectory), "subdirectories").GetValue(__instance) as List<RecordDirectory>).Add(item);
                    __result = Task<FrooxEngine.Store.Record>.Run(() => record);
                    return false;
                }
                return true;
            }

            [HarmonyPatch("SetPublicRecursively")]
            [HarmonyPrefix]
            public static bool SetPublicRecursively(RecordDirectory __instance)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    throw new Exception("You cannot set public on a local directory.");
                }
                return true;
            }


            [HarmonyPatch("DeleteItem")]
            [HarmonyPrefix]
            public static bool DeleteItem(FrooxEngine.Store.Record record, RecordDirectory __instance, ref bool __result)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    var test = (AccessTools.Field(typeof(RecordDirectory), "records").GetValue(__instance) as List<FrooxEngine.Store.Record>).Remove(record);
                    if (test)
                    {
                        // First handle the asset files
                        if (!string.IsNullOrEmpty(record.AssetURI))
                        {
                            try
                            {
                                Uri asset = new Uri(record.AssetURI);
                                if (asset.Scheme == "lstore")
                                {
                                    // Delete the primary asset file
                                    var file = ResolveLstore(asset);
                                    if (file != null && File.Exists(file))
                                    {
                                        File.Delete(file);
                                        Msg($"Deleted asset file: {file}");
                                    }

                                    // Clean up the associated directory for this asset if it exists and is empty
                                    string assetDir = Path.GetDirectoryName(file);
                                    if (Directory.Exists(assetDir))
                                    {
                                        // Delete all files in the asset directory (thumbnails, etc.)
                                        foreach (var extraFile in Directory.GetFiles(assetDir))
                                        {
                                            try
                                            {
                                                File.Delete(extraFile);
                                                Msg($"Deleted associated file: {extraFile}");
                                            }
                                            catch (Exception e)
                                            {
                                                Error($"Failed to delete associated file {extraFile}: {e.Message}");
                                            }
                                        }

                                        // Delete the directory if it's empty
                                        if (!Directory.EnumerateFileSystemEntries(assetDir).Any())
                                        {
                                            Directory.Delete(assetDir);
                                            Msg($"Deleted empty asset directory: {assetDir}");
                                        }
                                    }

                                    // Clean up any variants if they exist
                                    if (_config.GetValue(Mod.STORE_ASSET_VARIANTS))
                                    {
                                        try
                                        {
                                            // Get the path relative to the storage root
                                            string relativePath = asset.AbsolutePath.TrimStart('/');
                                            string variantBasePath = Path.Combine(VARIANT_PATH, relativePath);
                                            string variantDir = Path.GetDirectoryName(variantBasePath);

                                            // If the directory exists, clean it up thoroughly
                                            if (Directory.Exists(variantDir))
                                            {
                                                // First delete all files
                                                foreach (var variantFile in Directory.GetFiles(variantDir))
                                                {
                                                    File.Delete(variantFile);
                                                    Msg($"Deleted variant file: {variantFile}");
                                                }

                                                // Then delete the directory if empty
                                                if (!Directory.EnumerateFileSystemEntries(variantDir).Any())
                                                {
                                                    Directory.Delete(variantDir);
                                                    Msg($"Deleted empty variant directory: {variantDir}");
                                                }
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            Error($"Failed to clean up asset variants: {e.Message}");
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Error($"Error processing asset URI during deletion: {e.Message}");
                            }
                        }

                        // Handle thumbnail file if it exists separately from the asset
                        if (!string.IsNullOrEmpty(record.ThumbnailURI))
                        {
                            try
                            {
                                Uri thumbnailUri = new Uri(record.ThumbnailURI);
                                if (thumbnailUri.Scheme == "lstore")
                                {
                                    var thumbFile = ResolveLstore(thumbnailUri);
                                    if (thumbFile != null && File.Exists(thumbFile))
                                    {
                                        File.Delete(thumbFile);
                                        Msg($"Deleted thumbnail file: {thumbFile}");
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Error($"Failed to delete thumbnail: {e.Message}");
                            }
                        }

                        // Delete the record file
                        try
                        {
                            var recordPath = Path.Combine(REC_PATH, record.Path.Replace('\\', '/'), record.Name + ".json");
                            if (File.Exists(recordPath))
                            {
                                File.Delete(recordPath);
                                Msg($"Deleted record file: {recordPath}");
                            }

                            // Try to delete the item's directory if it's empty
                            string itemDir = Path.Combine(REC_PATH, record.Path.Replace('\\', '/'));
                            if (Directory.Exists(itemDir) && !Directory.EnumerateFileSystemEntries(itemDir).Any())
                            {
                                Directory.Delete(itemDir);
                                Msg($"Deleted empty record directory: {itemDir}");
                            }

                            // Same for the data directory
                            string dataDir = Path.Combine(DATA_PATH, record.Path.Replace('\\', '/'));
                            if (Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).Any())
                            {
                                Directory.Delete(dataDir);
                                Msg($"Deleted empty data directory: {dataDir}");
                            }
                        }
                        catch (Exception e)
                        {
                            Error($"Failed to delete record file or directory: {e.Message}");
                        }

                        // Update search index if integration is enabled
                        if (_config.GetValue(Mod.ENABLE_SEARCH_INTEGRATION))
                        {
                            // Remove from search index if it exists
                            localItemsSearchIndex.Remove(record.RecordId);
                        }
                    }
                    __result = test;
                    return false;
                }
                return true;
            }

            [HarmonyPatch("DeleteSubdirectory")]
            [HarmonyPrefix]
            public static bool DeleteSubdirectory(RecordDirectory directory, RecordDirectory __instance)
            {
                if (__instance.OwnerId == LOCAL_OWNER)
                {
                    var subs = (AccessTools.Field(typeof(RecordDirectory), "subdirectories").GetValue(__instance) as List<RecordDirectory>);
                    var test = subs.Remove(directory);
                    if (!test)
                    {
                        throw new Exception("Directory doesn't contain given subdirectory");
                    }
                    _ = RecursiveDelete(directory);
                    return false;
                }
                return true;
            }

            public static async Task RecursiveDelete(RecordDirectory dir)
            {
                if (dir.CanWrite && !dir.IsLink)
                {
                    await dir.EnsureFullyLoaded();
                    foreach (var subdir in dir.Subdirectories.ToList())
                    {
                        await RecursiveDelete(subdir);
                    }
                    foreach (var rec in dir.Records.ToList())
                    {
                        dir.DeleteItem(rec);
                    }
                    if (dir.DirectoryRecord != null)
                    {
                        try
                        {
                            string dirPath = Path.Combine(REC_PATH, dir.Path.Replace('\\', '/'));
                            if (Directory.Exists(dirPath))
                            {
                                Directory.Delete(dirPath);
                            }
                        }
                        catch (Exception e)
                        {
                            Error($"Failed to delete record directory: {e.Message}");
                        }

                        try
                        {
                            string dataPath = Path.Combine(DATA_PATH, dir.Path.Replace('\\', '/'));
                            if (Directory.Exists(dataPath))
                            {
                                Directory.Delete(dataPath);
                            }
                        }
                        catch (Exception e)
                        {
                            Error($"Failed to delete data directory: {e.Message}");
                        }

                        // Clean up variant directory if it exists
                        if (_config.GetValue(Mod.STORE_ASSET_VARIANTS))
                        {
                            try
                            {
                                string variantPath = Path.Combine(VARIANT_PATH, dir.Path.Replace('\\', '/'));
                                if (Directory.Exists(variantPath))
                                {
                                    Directory.Delete(variantPath, true);
                                }
                            }
                            catch (Exception e)
                            {
                                Error($"Failed to delete variant directory: {e.Message}");
                            }
                        }
                    }
                }
                if (dir.LinkRecord != null)
                {
                    try
                    {
                        string linkPath = Path.Combine(REC_PATH, dir.LinkRecord.Path.Replace('\\', '/'), dir.LinkRecord.Name + ".json");
                        if (File.Exists(linkPath))
                        {
                            File.Delete(linkPath);
                        }
                    }
                    catch (Exception e)
                    {
                        Error($"Failed to delete link record file: {e.Message}");
                    }
                }
            }
        }

        [HarmonyPatch(typeof(AssetManager))]
        public static class AssetManagerPatch
        {
            [HarmonyPatch("GatherAssetFile")]
            [HarmonyPrefix]
            public static bool RequestGather(Uri assetURL, float priority, DB_Endpoint? overrideEndpoint, ref ValueTask<string> __result)
            {
                if (assetURL.Scheme == "lstore")
                {
                    __result = new ValueTask<string>(ResolveLstore(assetURL));
                    return false;
                }

                // Handle local variants if asset variant storage is enabled
                if (_config.GetValue(Mod.STORE_ASSET_VARIANTS) && assetURL.Scheme == "lstore" && !string.IsNullOrEmpty(assetURL.Query))
                {
                    __result = new ValueTask<string>(ResolveLvariant(assetURL));
                    return false;
                }

                return true;
            }
        }
   

        [HarmonyPatch(typeof(DataTreeConverter), nameof(DataTreeConverter.Load), new Type[] { typeof(string), typeof(string) })]
        public static class JsonSupportAdding
        {
            public static bool Prefix(string file, string ext, ref DataTreeDictionary __result)
            {
                try
                {
                    UniLog.Log($"[LocalStorage-Debug] Attempting to load file: {file}");
                    UniLog.Log($"[LocalStorage-Debug] Original Extension: {ext}");

                    // If ext is null, derive extension from file path
                    if (string.IsNullOrEmpty(ext))
                    {
                        ext = Path.GetExtension(file)?.TrimStart('.').ToLowerInvariant();
                    }

                    UniLog.Log($"[LocalStorage-Debug] Normalized Extension: {ext}");

                    // Explicitly check for json variants
                    if (ext == "json" || ext.StartsWith("json"))
                    {
                        if (!File.Exists(file))
                        {
                            UniLog.Error($"[LocalStorage-Error] File does not exist: {file}");
                            return true;
                        }

                        // Read entire file contents for debugging
                        string fileContents = File.ReadAllText(file);
                        UniLog.Log($"[LocalStorage-Debug] File Contents (first 500 chars): {fileContents.Substring(0, Math.Min(500, fileContents.Length))}");

                        using (var fileReader = File.OpenText(file))
                        using (var jsonReader = new JsonTextReader(fileReader))
                        {
                            var readFunc = AccessTools.Method(typeof(DataTreeConverter), "Read");
                            if (readFunc == null)
                            {
                                UniLog.Error("[LocalStorage-Error] Could not find Read method via reflection");
                                return true;
                            }

                            __result = (DataTreeDictionary)readFunc.Invoke(null, new object[] { jsonReader });
                            UniLog.Log($"[LocalStorage-Debug] Successfully loaded JSON file: {file}");
                        }
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    UniLog.Error($"[LocalStorage-Error] Comprehensive error in JSON loading: {ex}");
                    UniLog.Error($"[LocalStorage-Error] Stack Trace: {ex.StackTrace}");
                    return true;
                }
            }
        }
    }
}