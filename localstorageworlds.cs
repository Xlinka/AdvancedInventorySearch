using System;
using System.Linq;
using FrooxEngine.Store;
using System.Threading.Tasks;
using System.Collections.Generic;
using FrooxEngine;
using Elements.Core;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using SkyFrost.Base;
using System.Reflection;

namespace AdvancedInventoryWithStorage
{
    public static partial class WorldSavingPatches
    {
        // Reference to the main LocalStorageSystem
        public const string LOCAL_OWNER = LocalStorageSystem.LOCAL_OWNER;

        // Reference to the custom DB storage
        private static CustomDBStorage _customDBStorage;

        // Flag to indicate if we're using custom DB
        private static bool _useCustomDB = false;

        // File server service reference
        private static FileServerService _fileServerService;

        // Flag to indicate if we're using remote storage
        private static bool _useRemoteStorage = false;

        // This will be called from the Mod class
        public static void Initialize(Harmony harmony)
        {
            // Don't call harmony.PatchAll() here - let the main mod handle it
            Msg("World saving patches initialized");
        }

        // This will be called from the Mod class to set up custom DB integration
        public static void SetCustomDBStorage(CustomDBStorage customDBStorage)
        {
            _customDBStorage = customDBStorage;
            _useCustomDB = customDBStorage != null;
            Msg("Custom DB storage integration enabled");
        }

        // This will be called from the Mod class to set up remote storage integration
        public static void SetFileServerService(FileServerService fileServerService, bool useRemoteStorage)
        {
            _fileServerService = fileServerService;
            _useRemoteStorage = useRemoteStorage;
            if (_useRemoteStorage)
            {
                Msg("Remote storage integration enabled");
            }
        }

        private static void Msg(string message)
        {
            UniLog.Log($"[LocalStorage-WorldSaving] {message}");
        }

        private static void Error(string message)
        {
            UniLog.Error($"[LocalStorage-WorldSaving] {message}");
        }

        private static void Debug(string message)
        {
            UniLog.Log($"[LocalStorage-WorldSaving-Debug] {message}");
        }

        [HarmonyPatch(typeof(FrooxEngine.Userspace), "SaveWorldTaskIntern")]
        public static class SaveWorldTaskInternPatch
        {
            public static bool Prefix(World world, FrooxEngine.Store.Record record, RecordOwnerTransferer transferer, ref Task<FrooxEngine.Store.Record> __result, Userspace __instance)
            {
                if (record?.OwnerId != LOCAL_OWNER)
                    return true;

                Msg($"Taking over local storage world save: {world.Name}");

                __result = Task.Run(() =>
                {
                    if (record == null)
                    {
                        throw new Exception("World record is null, cannot perform save");
                    }

                    TaskCompletionSource<SavedGraph> completionSource = new TaskCompletionSource<SavedGraph>();
                    string _name = null;
                    string _description = null;
                    HashSet<string> _tags = null;

                    world.RunSynchronously(delegate
                    {
                        try
                        {
                            int numMaterials = MaterialOptimizer.DeduplicateMaterials(world);
                            int numProviders = WorldOptimizer.DeduplicateStaticProviders(world);
                            int numAssets = WorldOptimizer.CleanupAssets(world);
                            Msg($"World Optimized! Deduplicated Materials: {numMaterials}, Deduplicated Static Providers: {numProviders}, Cleaned Up Assets: {numAssets}");

                            // Create a SavedGraph
                            SavedGraph savedGraph = world.SaveWorld();
                            completionSource.SetResult(savedGraph);

                            _name = world.Name;
                            _description = world.Description;
                            _tags = new HashSet<string>();
                            foreach (string tag in world.Tags)
                            {
                                if (!string.IsNullOrWhiteSpace(tag))
                                {
                                    _tags.Add(tag);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            completionSource.SetException(ex);
                        }
                    });

                    // Wait for the task without using await
                    var t0 = completionSource.Task;
                    t0.Wait();
                    SavedGraph savedGraph = t0.Result;

                    FrooxEngine.Store.Record result;
                    try
                    {
                        if (transferer == null)
                        {
                            transferer = new RecordOwnerTransferer(__instance.Engine, record.OwnerId);
                        }

                        // Process without using await
                        var t1 = transferer.EnsureOwnerId(savedGraph);
                        t1.Wait();

                        // Get sanitized name for file system
                        string sanitizedName = LocalStorageSystem.SanitizeFileName(_name);

                        // Ensure worlds are always saved in Inventory/Worlds
                        string worldFolder = string.IsNullOrEmpty(record.Path)
                            || !record.Path.StartsWith("Inventory/")
                            ? "Inventory/Worlds"
                            : record.Path;

                        // If already in Inventory but not in Inventory/Worlds, put in Inventory/Worlds
                        if (worldFolder.StartsWith("Inventory/") && !worldFolder.StartsWith("Inventory/Worlds") && !worldFolder.Equals("Inventory/Worlds"))
                        {
                            worldFolder = "Inventory/Worlds";
                        }

                        // Set the path in the record to reflect this structure
                        record.Path = worldFolder;

                        // Ensure the folder structure exists
                        Directory.CreateDirectory(Path.Combine(LocalStorageSystem.DATA_PATH, worldFolder));
                        Directory.CreateDirectory(Path.Combine(LocalStorageSystem.REC_PATH, worldFolder));

                        // Use DataTreeSaver to save the world data
                        Msg("Saving world data using DataTreeSaver...");
                        DataTreeSaver dataTreeSaver = new DataTreeSaver(__instance.Engine);
                        var t3 = dataTreeSaver.SaveLocally(savedGraph, world.SourceLink?.URL);
                        t3.Wait();
                        Uri uri = t3.Result;

                        // Update record metadata
                        record.Name = _name;
                        record.Description = _description;
                        record.Tags = _tags;
                        record.AssetURI = uri.ToString(); // This will reference the native Resonite saved file
                        record.RecordType = "world";

                        // If using custom DB, save there instead of JSON file
                        if (_useCustomDB && _customDBStorage != null)
                        {
                            bool saved = _customDBStorage.SaveRecord(record);
                            if (saved)
                            {
                                Msg($"Successfully saved world record to custom database: {_name}");

                                // If using remote storage, upload the world data file
                                if (_useRemoteStorage && _fileServerService != null)
                                {
                                    Task.Run(async () => {
                                        string localAssetPath = uri.ToString().Replace("res:///", "");
                                        string fullLocalPath = Path.Combine(__instance.Engine.LocalDB.AssetStoragePath, localAssetPath);
                                        if (File.Exists(fullLocalPath))
                                        {
                                            bool uploaded = await _fileServerService.UploadFile(fullLocalPath, $"assets/worlds/{record.RecordId}{Path.GetExtension(fullLocalPath)}");
                                            if (uploaded)
                                            {
                                                Msg($"Uploaded world data to remote storage: {_name}");
                                            }
                                        }
                                    });
                                }
                            }
                            else
                            {
                                Error($"Failed to save world record to custom database: {_name}");
                            }
                        }
                        else
                        {
                            // Save the record metadata as JSON file (original behavior)
                            string recPath = Path.Combine(LocalStorageSystem.REC_PATH, worldFolder, sanitizedName + ".json");
                            using (var fs = File.CreateText(recPath))
                            {
                                JsonSerializer serializer = new JsonSerializer();
                                serializer.Formatting = Formatting.Indented;
                                serializer.Serialize(fs, record);
                            }

                            // If using remote storage, upload the JSON file
                            if (_useRemoteStorage && _fileServerService != null)
                            {
                                Task.Run(async () => {
                                    await _fileServerService.UploadFile(recPath, $"records/worlds/{sanitizedName}.json");
                                });
                            }
                        }

                        Msg($"Successfully saved world: {_name} as WorldOrb with URI: {uri}");

                        result = record;
                    }
                    catch (Exception ex)
                    {
                        string tempFilePath = __instance.Engine.LocalDB.GetTempFilePath(".json");
                        DataTreeConverter.Save(savedGraph.Root, tempFilePath, DataTreeConverter.Compression.LZ4);
                        Error($"Exception in the save process for {_name}!\nDumping raw save data to: {tempFilePath}\n{ex}");
                        result = null;
                    }

                    return result;
                });

                return false;
            }
        }

        // Add this if you need to patch RecordOwnerTransferer behavior
        [HarmonyPatch(typeof(RecordOwnerTransferer))]
        public static class TransererPatch
        {
            [HarmonyPatch("ShouldProcess")]
            [HarmonyPrefix]
            public static bool ShouldTransfer(string ownerId, string recordId, RecordOwnerTransferer __instance)
            {
                Debug(ownerId);
                Debug(__instance.TargetOwnerID);
                if (__instance.SourceRootOwnerID != null)
                    Debug(__instance.SourceRootOwnerID);
                return true;
            }
        }

        [HarmonyPatch(typeof(RecordManager), "SaveRecord")]
        public static class RecordManagerSaveRecordPatch
        {
            public static bool Prefix(FrooxEngine.Store.Record record, SavedGraph loadedGraph, ref Task<RecordManager.RecordSaveResult> __result, RecordManager __instance)
            {
                if (record?.OwnerId != LOCAL_OWNER)
                    return true;

                Msg($"Intercepting record save for local storage record: {record.Name} (Type: {record.RecordType})");

                // If using custom DB, use that instead
                if (_useCustomDB && _customDBStorage != null)
                {
                    Msg($"Using custom DB to save record: {record.Name} (Type: {record.RecordType})");

                    __result = Task.Run(() => {
                        try
                        {
                            // For world data, we'll use the normal Resonite DataTreeSaver for compatibility
                            if (loadedGraph != null)
                            {
                                // Use the standard DataTreeSaver like Resonite does
                                DataTreeSaver dataTreeSaver = new DataTreeSaver(__instance.Engine);
                                Uri dataUri = dataTreeSaver.SaveLocally(loadedGraph, null).Result;

                                // Update the record to point to the properly saved world data
                                record.AssetURI = dataUri.ToString();

                                Msg($"Saved record data using native Resonite format: {dataUri}");

                                // If using remote storage, upload the asset file
                                if (_useRemoteStorage && _fileServerService != null)
                                {
                                    Task.Run(async () => {
                                        string localAssetPath = dataUri.ToString().Replace("res:///", "");
                                        string fullLocalPath = Path.Combine(__instance.Engine.LocalDB.AssetStoragePath, localAssetPath);
                                        if (File.Exists(fullLocalPath))
                                        {
                                            bool uploaded = await _fileServerService.UploadFile(fullLocalPath, $"assets/{record.RecordId}{Path.GetExtension(fullLocalPath)}");
                                            if (uploaded)
                                            {
                                                Msg($"Uploaded record data to remote storage: {record.Name}");
                                            }
                                        }
                                    });
                                }
                            }

                            // Save the record to our custom database
                            bool success = _customDBStorage.SaveRecord(record);

                            if (success)
                            {
                                Msg($"Successfully saved record to custom DB: {record.Name}");
                                return new RecordManager.RecordSaveResult(true, null);
                            }
                            else
                            {
                                Error($"Failed to save record to custom DB: {record.Name}");
                                return new RecordManager.RecordSaveResult(false, null);
                            }
                        }
                        catch (Exception ex)
                        {
                            Error($"Error saving record to custom DB: {ex.Message}");
                            return new RecordManager.RecordSaveResult(false, null);
                        }
                    });

                    return false;
                }

                // Otherwise, use the original JSON-based storage
                __result = Task.Run(() =>
                {
                    try
                    {
                        // Get sanitized name for file system
                        string sanitizedName = LocalStorageSystem.SanitizeFileName(record.Name);

                        // Determine appropriate storage path based on record type
                        string recordPath;

                        if (record.RecordType == "world")
                        {
                            // Worlds go in Inventory/Worlds
                            recordPath = "Inventory/Worlds";
                        }
                        else
                        {
                            // Default path for other types (should already be set correctly)
                            recordPath = string.IsNullOrEmpty(record.Path) ? "Inventory" : record.Path;

                            // Ensure path is under Inventory
                            if (!recordPath.StartsWith("Inventory/") && !recordPath.Equals("Inventory"))
                            {
                                recordPath = "Inventory/" + recordPath;
                            }
                        }

                        // Update the record's path
                        record.Path = recordPath;

                        // Ensure directories exist
                        Directory.CreateDirectory(Path.Combine(LocalStorageSystem.REC_PATH, recordPath));
                        Directory.CreateDirectory(Path.Combine(LocalStorageSystem.DATA_PATH, recordPath));

                        // For world data, we'll use the normal Resonite DataTreeSaver for compatibility
                        if (loadedGraph != null)
                        {
                            // Use the standard DataTreeSaver like Resonite does
                            DataTreeSaver dataTreeSaver = new DataTreeSaver(__instance.Engine);
                            Uri dataUri = dataTreeSaver.SaveLocally(loadedGraph, null).Result;

                            // Update the record to point to the properly saved world data
                            record.AssetURI = dataUri.ToString();

                            Msg($"Saved record data using native Resonite format: {dataUri}");

                            // If using remote storage, upload the asset file
                            if (_useRemoteStorage && _fileServerService != null)
                            {
                                Task.Run(async () => {
                                    string localAssetPath = dataUri.ToString().Replace("res:///", "");
                                    string fullLocalPath = Path.Combine(__instance.Engine.LocalDB.AssetStoragePath, localAssetPath);
                                    if (File.Exists(fullLocalPath))
                                    {
                                        bool uploaded = await _fileServerService.UploadFile(fullLocalPath, $"assets/{record.RecordId}{Path.GetExtension(fullLocalPath)}");
                                        if (uploaded)
                                        {
                                            Msg($"Uploaded record data to remote storage: {record.Name}");
                                        }
                                    }
                                });
                            }
                        }

                        // Save the record metadata separately
                        string recFilePath = Path.Combine(LocalStorageSystem.REC_PATH, recordPath, sanitizedName + ".json");
                        using (var fs = File.CreateText(recFilePath))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            serializer.Formatting = Formatting.Indented;
                            serializer.Serialize(fs, record);
                        }

                        // If using remote storage, upload the record JSON
                        if (_useRemoteStorage && _fileServerService != null)
                        {
                            Task.Run(async () => {
                                await _fileServerService.UploadFile(recFilePath, $"records/{recordPath}/{sanitizedName}.json");
                            });
                        }

                        Msg($"Successfully saved record metadata: {record.Name} to {recFilePath}");

                        // Skip creating the upload task entirely for local storage
                        return new RecordManager.RecordSaveResult(true, null);
                    }
                    catch (Exception ex)
                    {
                        Error($"Error saving local storage record: {ex}");
                        return new RecordManager.RecordSaveResult(false, null);
                    }
                });

                return false;
            }
        }

        // Patch ID generation for local storage worlds
        [HarmonyPatch(typeof(IdUtil), "GetOwnerType")]
        public static class IdUtilGetOwnerTypePatch
        {
            public static bool Prefix(string id, ref OwnerType __result)
            {
                if (id == LOCAL_OWNER)
                {
                    // Treat our LOCAL_OWNER as a valid Machine owner type
                    __result = OwnerType.Machine;
                    return false;
                }
                return true;
            }
        }
    }
}