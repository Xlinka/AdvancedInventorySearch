using System;
using System.IO;
using Mono.Data.Sqlite;
using System.Collections.Generic;
using System.Threading.Tasks;
using Elements.Core;
using FrooxEngine.Store;
using Newtonsoft.Json;
using System.Data;
using System.Text;
using System.Linq;
using ResoniteModLoader;
using SkyFrost.Base;
using HarmonyLib;
using System.Security.Cryptography;

namespace AdvancedInventoryWithStorage
{
    public class CustomDBStorage
    {
        // Database connection
        private SqliteConnection _dbConnection;
        private readonly string _dbPath;
        private bool _initialized = false;
        private ModConfiguration _config;

        // File server service reference
        private FileServerService _fileServerService;

        // Constants
        private const string DB_VERSION = "1.0";
        private const string LOCAL_OWNER = LocalStorageSystem.LOCAL_OWNER;

        // Track if we're using remote storage
        private bool _useRemoteStorage = false;

        // Thread safety
        private readonly object _dbLock = new object();

        public CustomDBStorage(ModConfiguration config)
        {
            _config = config;
            _dbPath = _config.GetValue(Mod.CUSTOM_DB_PATH);

            // Initialize file server service if enabled
            _useRemoteStorage = _config.GetValue(Mod.USE_REMOTE_STORAGE);
            if (_useRemoteStorage)
            {
                _fileServerService = new FileServerService(_config);
            }
        }

        #region Initialization

        public async Task<bool> InitializeAsync()
        {
            if (_initialized)
                return true;

            try
            {
                Log($"Initializing custom database at {_dbPath}");
                bool dbExists = File.Exists(_dbPath);

                // Create directory if it doesn't exist
                string dbDir = Path.GetDirectoryName(_dbPath);
                if (!Directory.Exists(dbDir))
                {
                    Directory.CreateDirectory(dbDir);
                }

                // If using remote storage and local DB doesn't exist, try downloading it
                if (_useRemoteStorage && !dbExists && _fileServerService != null)
                {
                    UniLog.Log("Checking for database on remote server...");
                    bool remoteDbExists = await _fileServerService.FileExists("database/custom_db.sqlite");

                    if (remoteDbExists)
                    {
                        UniLog.Log("Found remote database, downloading...");
                        bool downloadSuccess = await _fileServerService.DownloadDatabase(_dbPath);

                        if (downloadSuccess)
                        {
                            UniLog.Log("Successfully downloaded database from remote server");
                            dbExists = true;
                        }
                        else
                        {
                            UniLog.Log("Failed to download database, will create a new one");
                        }
                    }
                    else
                    {
                        UniLog.Log("No remote database found, will create a new one");

                        // Initialize remote structure
                        await _fileServerService.InitializeFileServerStructure();
                    }
                }

                // Create connection string
                string connectionString = $"Data Source={_dbPath};Version=3;";

                // Initialize connection
                _dbConnection = new SqliteConnection(connectionString);
                _dbConnection.Open();

                if (!dbExists)
                {
                    // If DB is new, create tables
                    CreateTables();

                    // If using remote storage, upload the new database
                    if (_useRemoteStorage && _fileServerService != null)
                    {
                        await _fileServerService.UploadDatabase(_dbPath);
                    }
                }
                else
                {
                    // Validate existing DB
                    ValidateDatabase();
                }

                _initialized = true;
                UniLog.Log("Custom database initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                UniLog.Log($"Failed to initialize database: {ex.Message}");
                return false;
            }
        }

        // Synchronous version for backward compatibility
        public bool Initialize()
        {
            Task<bool> task = InitializeAsync();
            task.Wait();
            return task.Result;
        }

        private void CreateTables()
        {
            using (var cmd = _dbConnection.CreateCommand())
            {
                // Create meta table for DB info
                cmd.CommandText = @"
                    CREATE TABLE meta (
                        key TEXT PRIMARY KEY,
                        value TEXT
                    )";
                cmd.ExecuteNonQuery();

                // Insert version info
                cmd.CommandText = "INSERT INTO meta VALUES ('version', @version)";
                cmd.Parameters.AddWithValue("@version", DB_VERSION);
                cmd.ExecuteNonQuery();

                // Create records table for inventory items
                cmd.CommandText = @"
                    CREATE TABLE records (
                        record_id TEXT PRIMARY KEY,
                        owner_id TEXT NOT NULL,
                        path TEXT NOT NULL,
                        name TEXT NOT NULL,
                        description TEXT,
                        record_type TEXT NOT NULL,
                        asset_uri TEXT,
                        thumbnail_uri TEXT,
                        is_public INTEGER DEFAULT 0,
                        is_for_patrons INTEGER DEFAULT 0,
                        is_listed INTEGER DEFAULT 0,
                        creation_time TEXT NOT NULL,
                        last_modified_time TEXT NOT NULL,
                        first_publish_time TEXT,
                        visits INTEGER DEFAULT 0,
                        rating REAL DEFAULT 0,
                        random_order INTEGER,
                        json_data TEXT
                    )";
                cmd.ExecuteNonQuery();

                // Create index on path for faster browsing
                cmd.CommandText = "CREATE INDEX idx_records_path ON records (path)";
                cmd.ExecuteNonQuery();

                // Create index on owner_id for filtering
                cmd.CommandText = "CREATE INDEX idx_records_owner ON records (owner_id)";
                cmd.ExecuteNonQuery();

                // Create tags table for record tags
                cmd.CommandText = @"
                    CREATE TABLE tags (
                        record_id TEXT NOT NULL,
                        tag TEXT NOT NULL,
                        PRIMARY KEY (record_id, tag),
                        FOREIGN KEY (record_id) REFERENCES records (record_id) ON DELETE CASCADE
                    )";
                cmd.ExecuteNonQuery();

                // Create index on tags for faster searching
                cmd.CommandText = "CREATE INDEX idx_tags_tag ON tags (tag)";
                cmd.ExecuteNonQuery();

                // Create directories table
                cmd.CommandText = @"
                    CREATE TABLE directories (
                        dir_id TEXT PRIMARY KEY,
                        owner_id TEXT NOT NULL,
                        path TEXT NOT NULL,
                        name TEXT NOT NULL,
                        creation_time TEXT NOT NULL,
                        last_modified_time TEXT NOT NULL
                    )";
                cmd.ExecuteNonQuery();

                // Create index on path for faster browsing
                cmd.CommandText = "CREATE INDEX idx_directories_path ON directories (path)";
                cmd.ExecuteNonQuery();

                // Create sync_state table
                cmd.CommandText = @"
                    CREATE TABLE sync_state (
                        record_id TEXT PRIMARY KEY,
                        needs_sync INTEGER DEFAULT 1,
                        last_synced TEXT,
                        FOREIGN KEY (record_id) REFERENCES records (record_id) ON DELETE CASCADE
                    )";
                cmd.ExecuteNonQuery();
            }

            UniLog.Log("Database tables created successfully");
        }

        private void ValidateDatabase()
        {
            try
            {
                using (var cmd = _dbConnection.CreateCommand())
                {
                    // Check version
                    cmd.CommandText = "SELECT value FROM meta WHERE key = 'version'";
                    string version = (string)cmd.ExecuteScalar();

                    if (version != DB_VERSION)
                    {
                        UniLog.Log($"Database version mismatch: expected {DB_VERSION}, found {version}");
                        // Migration logic would go here for version upgrades
                    }

                    // Add any missing columns or tables that might have been added in newer versions
                    // This is a simplified approach - in a real app, you'd handle migrations more carefully

                    // Check if sync_state table exists and create if needed
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sync_state'";
                    bool syncTableExists = cmd.ExecuteScalar() != null;

                    if (!syncTableExists)
                    {
                        cmd.CommandText = @"
                            CREATE TABLE sync_state (
                                record_id TEXT PRIMARY KEY,
                                needs_sync INTEGER DEFAULT 1,
                                last_synced TEXT,
                                FOREIGN KEY (record_id) REFERENCES records (record_id) ON DELETE CASCADE
                            )";
                        cmd.ExecuteNonQuery();

                        // Initialize all existing records as needing sync
                        cmd.CommandText = @"
                            INSERT INTO sync_state (record_id, needs_sync)
                            SELECT record_id, 1 FROM records";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                UniLog.Log($"Error validating database: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Record Management

        /// <summary>
        /// Saves a record to the database
        /// </summary>
        public bool SaveRecord(FrooxEngine.Store.Record record)
        {
            if (record == null)
                return false;

            if (!_initialized && !Initialize())
                return false;

            // If this is a new record without an ID, generate one
            if (string.IsNullOrEmpty(record.RecordId))
            {
                record.RecordId = GenerateRecordId();
            }

            lock (_dbLock)
            {
                try
                {
                    using (var transaction = _dbConnection.BeginTransaction())
                    {
                        using (var cmd = _dbConnection.CreateCommand())
                        {
                            // Check if record exists
                            cmd.CommandText = "SELECT COUNT(*) FROM records WHERE record_id = @recordId";
                            cmd.Parameters.AddWithValue("@recordId", record.RecordId);
                            int count = Convert.ToInt32(cmd.ExecuteScalar());

                            // Serialize additional data that doesn't fit in specific columns
                            var extraData = new Dictionary<string, object>();
                            if (record.AssetManifest != null)
                                extraData["AssetManifest"] = record.AssetManifest;

                            string jsonData = JsonConvert.SerializeObject(extraData);

                            if (count > 0)
                            {
                                // Update existing record
                                cmd.CommandText = @"
                                    UPDATE records SET 
                                        owner_id = @ownerId,
                                        path = @path,
                                        name = @name,
                                        description = @description,
                                        record_type = @recordType,
                                        asset_uri = @assetUri,
                                        thumbnail_uri = @thumbnailUri,
                                        is_public = @isPublic,
                                        is_for_patrons = @isForPatrons,
                                        is_listed = @isListed,
                                        last_modified_time = @lastModifiedTime,
                                        first_publish_time = @firstPublishTime,
                                        visits = @visits,
                                        rating = @rating,
                                        random_order = @randomOrder,
                                        json_data = @jsonData
                                    WHERE record_id = @recordId";
                            }
                            else
                            {
                                // Insert new record
                                cmd.CommandText = @"
                                    INSERT INTO records (
                                        record_id, owner_id, path, name, description, record_type,
                                        asset_uri, thumbnail_uri, is_public, is_for_patrons, is_listed,
                                        creation_time, last_modified_time, first_publish_time,
                                        visits, rating, random_order, json_data
                                    ) VALUES (
                                        @recordId, @ownerId, @path, @name, @description, @recordType,
                                        @assetUri, @thumbnailUri, @isPublic, @isForPatrons, @isListed,
                                        @creationTime, @lastModifiedTime, @firstPublishTime,
                                        @visits, @rating, @randomOrder, @jsonData
                                    )";

                                // Set creation time for new records
                                string creationTime = DateTime.UtcNow.ToString("o");
                                if (record.CreationTime != null && record.CreationTime != DateTime.MinValue)
                                {
                                    creationTime = record.CreationTime.ToString();
                                }
                                cmd.Parameters.AddWithValue("@creationTime", creationTime);
                            }

                            // Add parameters for insert/update
                            cmd.Parameters.AddWithValue("@recordId", record.RecordId);
                            cmd.Parameters.AddWithValue("@ownerId", record.OwnerId);
                            cmd.Parameters.AddWithValue("@path", record.Path ?? "Inventory");
                            cmd.Parameters.AddWithValue("@name", record.Name);
                            cmd.Parameters.AddWithValue("@description", record.Description ?? "");
                            cmd.Parameters.AddWithValue("@recordType", record.RecordType);
                            cmd.Parameters.AddWithValue("@assetUri", record.AssetURI ?? "");
                            cmd.Parameters.AddWithValue("@thumbnailUri", record.ThumbnailURI ?? "");
                            cmd.Parameters.AddWithValue("@isPublic", record.IsPublic ? 1 : 0);
                            cmd.Parameters.AddWithValue("@isForPatrons", record.IsForPatrons ? 1 : 0);
                            cmd.Parameters.AddWithValue("@isListed", record.IsListed ? 1 : 0);

                            // Handle last modified time
                            string lastModifiedTime = DateTime.UtcNow.ToString("o");
                            if (record.LastModificationTime != null && record.LastModificationTime != DateTime.MinValue)
                            {
                                lastModifiedTime = record.LastModificationTime.ToString("o");
                            }
                            cmd.Parameters.AddWithValue("@lastModifiedTime", lastModifiedTime);

                            // Handle first publish time
                            if (record.FirstPublishTime != null && record.FirstPublishTime != DateTime.MinValue)
                            {
                                cmd.Parameters.AddWithValue("@firstPublishTime", record.FirstPublishTime.ToString());
                            }
                            else
                            {
                                cmd.Parameters.AddWithValue("@firstPublishTime", DBNull.Value);
                            }

                            cmd.Parameters.AddWithValue("@visits", record.Visits);
                            cmd.Parameters.AddWithValue("@rating", record.Rating);
                            cmd.Parameters.AddWithValue("@randomOrder", record.RandomOrder == 0 ? DBNull.Value : record.RandomOrder);
                            cmd.Parameters.AddWithValue("@jsonData", jsonData);

                            cmd.ExecuteNonQuery();

                            // Handle tags
                            if (count > 0)
                            {
                                // Delete existing tags first
                                cmd.CommandText = "DELETE FROM tags WHERE record_id = @recordId";
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@recordId", record.RecordId);
                                cmd.ExecuteNonQuery();
                            }

                            // Insert new tags
                            if (record.Tags != null && record.Tags.Count > 0)
                            {
                                foreach (string tag in record.Tags)
                                {
                                    cmd.CommandText = "INSERT INTO tags (record_id, tag) VALUES (@recordId, @tag)";
                                    cmd.Parameters.Clear();
                                    cmd.Parameters.AddWithValue("@recordId", record.RecordId);
                                    cmd.Parameters.AddWithValue("@tag", tag);
                                    cmd.ExecuteNonQuery();
                                }
                            }

                            // Update sync state
                            cmd.CommandText = @"
                                INSERT OR REPLACE INTO sync_state (record_id, needs_sync, last_synced)
                                VALUES (@recordId, 1, @lastSynced)";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@recordId", record.RecordId);
                            cmd.Parameters.AddWithValue("@lastSynced", DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }

                    // If remote storage is enabled, upload the database
                    if (_useRemoteStorage && _fileServerService != null)
                    {
                        Task.Run(async () => {
                            await _fileServerService.UploadDatabase(_dbPath);

                            // If this record has an asset, upload that too
                            if (!string.IsNullOrEmpty(record.AssetURI) && record.AssetURI.StartsWith("lstore:///"))
                            {
                                string localAssetPath = record.AssetURI.Substring(9); // Remove "lstore:///"
                                string fullLocalPath = Path.Combine(LocalStorageSystem.DATA_PATH, localAssetPath);

                                if (File.Exists(fullLocalPath))
                                {
                                    string extension = Path.GetExtension(fullLocalPath);
                                    await _fileServerService.UploadRecordAsset(fullLocalPath, record.RecordId, extension);
                                }
                            }
                        });
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error saving record {record.RecordId}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Loads a record from the database by ID
        /// </summary>
        public FrooxEngine.Store.Record LoadRecord(string recordId)
        {
            if (string.IsNullOrEmpty(recordId))
                return null;

            if (!_initialized && !Initialize())
                return null;

            lock (_dbLock)
            {
                try
                {
                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT * FROM records WHERE record_id = @recordId";
                        cmd.Parameters.AddWithValue("@recordId", recordId);

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var record = new FrooxEngine.Store.Record
                                {
                                    RecordId = reader["record_id"].ToString(),
                                    OwnerId = reader["owner_id"].ToString(),
                                    Path = reader["path"].ToString(),
                                    Name = reader["name"].ToString(),
                                    Description = reader["description"].ToString(),
                                    RecordType = reader["record_type"].ToString(),
                                    AssetURI = reader["asset_uri"].ToString(),
                                    ThumbnailURI = reader["thumbnail_uri"].ToString(),
                                    IsPublic = Convert.ToBoolean(reader["is_public"]),
                                    IsForPatrons = Convert.ToBoolean(reader["is_for_patrons"]),
                                    IsListed = Convert.ToBoolean(reader["is_listed"]),
                                    Visits = reader.IsDBNull(reader.GetOrdinal("visits")) ? 0 : Convert.ToInt32(reader["visits"]),
                                    Rating = reader.IsDBNull(reader.GetOrdinal("rating")) ? 0 : Convert.ToSingle(reader["rating"]),
                                    RandomOrder = reader.IsDBNull(reader.GetOrdinal("random_order")) ? 0 : Convert.ToInt32(reader["random_order"])
                                };

                                // Parse dates
                                if (!reader.IsDBNull(reader.GetOrdinal("creation_time")))
                                    record.CreationTime = DateTime.Parse(reader["creation_time"].ToString());

                                if (!reader.IsDBNull(reader.GetOrdinal("last_modified_time")))
                                    record.LastModificationTime = DateTime.Parse(reader["last_modified_time"].ToString());

                                if (!reader.IsDBNull(reader.GetOrdinal("first_publish_time")))
                                    record.FirstPublishTime = DateTime.Parse(reader["first_publish_time"].ToString());

                                // Parse extra JSON data
                                if (!reader.IsDBNull(reader.GetOrdinal("json_data")))
                                {
                                    string jsonData = reader["json_data"].ToString();
                                    if (!string.IsNullOrEmpty(jsonData))
                                    {
                                        var extraData = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonData);
                                        if (extraData.ContainsKey("AssetManifest"))
                                        {
                                            var manifestJson = extraData["AssetManifest"]?.ToString();
                                            if (!string.IsNullOrEmpty(manifestJson))
                                            {
                                                try
                                                {
                                                    // If it expects a list of DBAsset objects
                                                    var assetList = JsonConvert.DeserializeObject<List<SkyFrost.Base.DBAsset>>(manifestJson);
                                                    record.AssetManifest = assetList;
                                                }
                                                catch
                                                {
                                                    // If deserialization fails, set to empty list
                                                    record.AssetManifest = new List<SkyFrost.Base.DBAsset>();
                                                }
                                            }
                                            else
                                            {
                                                record.AssetManifest = new List<SkyFrost.Base.DBAsset>();
                                            }
                                        }
                                    }
                                }

                                // Get tags
                                record.Tags = new HashSet<string>();
                                using (var tagCmd = _dbConnection.CreateCommand())
                                {
                                    tagCmd.CommandText = "SELECT tag FROM tags WHERE record_id = @recordId";
                                    tagCmd.Parameters.AddWithValue("@recordId", recordId);

                                    using (var tagReader = tagCmd.ExecuteReader())
                                    {
                                        while (tagReader.Read())
                                        {
                                            record.Tags.Add(tagReader["tag"].ToString());
                                        }
                                    }
                                }

                                return record;
                            }
                        }
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error loading record {recordId}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Deletes a record from the database
        /// </summary>
        public bool DeleteRecord(string recordId)
        {
            if (string.IsNullOrEmpty(recordId))
                return false;

            if (!_initialized && !Initialize())
                return false;

            lock (_dbLock)
            {
                try
                {
                    // First, load the record to get asset info if needed
                    FrooxEngine.Store.Record recordToDelete = null;
                    if (_useRemoteStorage)
                    {
                        recordToDelete = LoadRecord(recordId);
                    }

                    using (var transaction = _dbConnection.BeginTransaction())
                    {
                        using (var cmd = _dbConnection.CreateCommand())
                        {
                            // Delete tags first (foreign key constraint)
                            cmd.CommandText = "DELETE FROM tags WHERE record_id = @recordId";
                            cmd.Parameters.AddWithValue("@recordId", recordId);
                            cmd.ExecuteNonQuery();

                            // Delete sync state
                            cmd.CommandText = "DELETE FROM sync_state WHERE record_id = @recordId";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@recordId", recordId);
                            cmd.ExecuteNonQuery();

                            // Delete record
                            cmd.CommandText = "DELETE FROM records WHERE record_id = @recordId";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@recordId", recordId);
                            int rowsAffected = cmd.ExecuteNonQuery();

                            transaction.Commit();

                            // If remote storage is enabled, upload the database
                            if (_useRemoteStorage && _fileServerService != null && recordToDelete != null)
                            {
                                Task.Run(async () => {
                                    await _fileServerService.UploadDatabase(_dbPath);

                                    // Also try to delete the asset if it exists
                                    if (!string.IsNullOrEmpty(recordToDelete.AssetURI) && recordToDelete.AssetURI.StartsWith("lstore:///"))
                                    {
                                        string remotePath = $"assets/{recordToDelete.RecordId}{Path.GetExtension(recordToDelete.AssetURI)}";
                                        await _fileServerService.DeleteFile(remotePath);
                                    }
                                });
                            }

                            return rowsAffected > 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error deleting record {recordId}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets all records in a directory
        /// </summary>
        public List<FrooxEngine.Store.Record> GetRecordsInDirectory(string ownerId, string path)
        {
            if (string.IsNullOrEmpty(ownerId))
                return new List<FrooxEngine.Store.Record>();

            if (!_initialized && !Initialize())
                return new List<FrooxEngine.Store.Record>();

            // Normalize path
            path = path ?? "Inventory";

            lock (_dbLock)
            {
                try
                {
                    List<FrooxEngine.Store.Record> records = new List<FrooxEngine.Store.Record>();

                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT record_id FROM records WHERE owner_id = @ownerId AND path = @path";
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@path", path);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string recordId = reader["record_id"].ToString();
                                var record = LoadRecord(recordId);
                                if (record != null)
                                {
                                    records.Add(record);
                                }
                            }
                        }
                    }

                    return records;
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error getting records in directory {path}: {ex.Message}");
                    return new List<FrooxEngine.Store.Record>();
                }
            }
        }

        /// <summary>
        /// Gets all subdirectories in a directory
        /// </summary>
        public List<string> GetSubdirectories(string ownerId, string path)
        {
            if (string.IsNullOrEmpty(ownerId))
                return new List<string>();

            if (!_initialized && !Initialize())
                return new List<string>();

            // Normalize path
            path = path ?? "Inventory";

            lock (_dbLock)
            {
                try
                {
                    List<string> directories = new List<string>();

                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT DISTINCT substr(path, length(@path) + 2) as subdir
                            FROM records
                            WHERE owner_id = @ownerId 
                              AND path LIKE @pathPattern
                              AND path != @path
                              AND instr(substr(path, length(@path) + 2), '/') = 0";
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@path", path);
                        cmd.Parameters.AddWithValue("@pathPattern", path + "/%");

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string subdir = reader["subdir"].ToString();
                                if (!string.IsNullOrEmpty(subdir))
                                {
                                    directories.Add(subdir);
                                }
                            }
                        }
                    }

                    // Also check directories table
                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM directories WHERE owner_id = @ownerId AND path = @path";
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@path", path);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string dirName = reader["name"].ToString();
                                if (!string.IsNullOrEmpty(dirName) && !directories.Contains(dirName))
                                {
                                    directories.Add(dirName);
                                }
                            }
                        }
                    }

                    return directories;
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error getting subdirectories in {path}: {ex.Message}");
                    return new List<string>();
                }
            }
        }

        /// <summary>
        /// Creates a directory
        /// </summary>
        public bool CreateDirectory(string ownerId, string path, string name)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(name))
                return false;

            if (!_initialized && !Initialize())
                return false;

            // Normalize path
            path = path ?? "Inventory";

            lock (_dbLock)
            {
                try
                {
                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        string dirId = GenerateDirectoryId();

                        cmd.CommandText = @"
                            INSERT INTO directories (
                                dir_id, owner_id, path, name, creation_time, last_modified_time
                            ) VALUES (
                                @dirId, @ownerId, @path, @name, @creationTime, @lastModifiedTime
                            )";
                        cmd.Parameters.AddWithValue("@dirId", dirId);
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@path", path);
                        cmd.Parameters.AddWithValue("@name", name);
                        cmd.Parameters.AddWithValue("@creationTime", DateTime.UtcNow.ToString("o"));
                        cmd.Parameters.AddWithValue("@lastModifiedTime", DateTime.UtcNow.ToString("o"));

                        int rowsAffected = cmd.ExecuteNonQuery();

                        // If remote storage is enabled, upload the database
                        if (_useRemoteStorage && _fileServerService != null)
                        {
                            Task.Run(async () => {
                                await _fileServerService.UploadDatabase(_dbPath);
                            });
                        }

                        return rowsAffected > 0;
                    }
                }
                catch (Exception ex)
                {
                    UniLog.Log($"Error creating directory {name} in {path}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Deletes a directory and all its contents
        /// </summary>
        public bool DeleteDirectory(string ownerId, string path, string name)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(name))
                return false;

            if (!_initialized && !Initialize())
                return false;

            path = path ?? "Inventory";

            // Full path to the directory to delete
            string fullPath = path.EndsWith("/") ? $"{path}{name}" : $"{path}/{name}";

            lock (_dbLock)
            {
                try
                {
                    using (var transaction = _dbConnection.BeginTransaction())
                    {
                        using (var cmd = _dbConnection.CreateCommand())
                        {
                            // Get all record IDs in this directory or subdirectories
                            cmd.CommandText = @"
                                SELECT record_id 
                                FROM records
                                WHERE owner_id = @ownerId 
                                  AND (path = @fullPath OR path LIKE @pathPattern)";
                            cmd.Parameters.AddWithValue("@ownerId", ownerId);
                            cmd.Parameters.AddWithValue("@fullPath", fullPath);
                            cmd.Parameters.AddWithValue("@pathPattern", fullPath + "/%");

                            List<string> recordIds = new List<string>();
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    recordIds.Add(reader["record_id"].ToString());
                                }
                            }

                            // Delete all tags for these records
                            foreach (string recordId in recordIds)
                            {
                                cmd.CommandText = "DELETE FROM tags WHERE record_id = @recordId";
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@recordId", recordId);
                                cmd.ExecuteNonQuery();

                                // Delete sync state
                                cmd.CommandText = "DELETE FROM sync_state WHERE record_id = @recordId";
                                cmd.Parameters.Clear();
                                cmd.Parameters.AddWithValue("@recordId", recordId);
                                cmd.ExecuteNonQuery();
                            }

                            // Delete all records in the directory and subdirectories
                            cmd.CommandText = @"
                                DELETE FROM records
                                WHERE owner_id = @ownerId 
                                  AND (path = @fullPath OR path LIKE @pathPattern)";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@ownerId", ownerId);
                            cmd.Parameters.AddWithValue("@fullPath", fullPath);
                            cmd.Parameters.AddWithValue("@pathPattern", fullPath + "/%");
                            cmd.ExecuteNonQuery();

                            // Delete subdirectories
                            cmd.CommandText = @"
                                DELETE FROM directories
                                WHERE owner_id = @ownerId 
                                  AND (path = @fullPath OR path LIKE @pathPattern)";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@ownerId", ownerId);
                            cmd.Parameters.AddWithValue("@fullPath", fullPath);
                            cmd.Parameters.AddWithValue("@pathPattern", fullPath + "/%");
                            cmd.ExecuteNonQuery();

                            // Delete the directory itself
                            cmd.CommandText = @"
                                DELETE FROM directories
                                WHERE owner_id = @ownerId 
                                  AND path = @path
                                  AND name = @name";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("@ownerId", ownerId);
                            cmd.Parameters.AddWithValue("@path", path);
                            cmd.Parameters.AddWithValue("@name", name);
                            cmd.ExecuteNonQuery();

                            transaction.Commit();

                            // If remote storage is enabled, upload the database to keep it in sync
                            if (_useRemoteStorage && _fileServerService != null)
                            {
                                Task.Run(async () => {
                                    await _fileServerService.UploadDatabase(_dbPath);
                                });
                            }

                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Error($"Error deleting directory {name} in {path}: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Searches records by text
        /// </summary>
        public List<FrooxEngine.Store.Record> SearchRecords(string ownerId, string searchText, bool caseSensitive = false)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(searchText))
                return new List<FrooxEngine.Store.Record>();

            if (!_initialized && !Initialize())
                return new List<FrooxEngine.Store.Record>();

            lock (_dbLock)
            {
                try
                {
                    List<string> recordIds = new List<string>();

                    using (var cmd = _dbConnection.CreateCommand())
                    {
                        string searchPattern = "%" + searchText + "%";

                        // Adjust SQL query based on case sensitivity
                        string compareOp = caseSensitive ? "LIKE" : "LIKE";
                        string nameField = caseSensitive ? "name" : "LOWER(name)";
                        string descField = caseSensitive ? "description" : "LOWER(description)";
                        string searchVal = caseSensitive ? searchPattern : searchPattern.ToLower();

                        // Search in name and description
                        cmd.CommandText = $@"
                            SELECT DISTINCT record_id 
                            FROM records
                            WHERE owner_id = @ownerId 
                              AND ({nameField} {compareOp} @searchText OR {descField} {compareOp} @searchText)";
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@searchText", searchVal);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                recordIds.Add(reader["record_id"].ToString());
                            }
                        }

                        // Search in tags
                        string tagField = caseSensitive ? "tag" : "LOWER(tag)";
                        cmd.CommandText = $@"
                            SELECT DISTINCT record_id 
                            FROM tags t
                            JOIN records r ON t.record_id = r.record_id
                            WHERE r.owner_id = @ownerId 
                              AND {tagField} {compareOp} @searchText";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@searchText", searchVal);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string recordId = reader["record_id"].ToString();
                                if (!recordIds.Contains(recordId))
                                {
                                    recordIds.Add(recordId);
                                }
                            }
                        }
                    }

                    // Load full records
                    List<FrooxEngine.Store.Record> results = new List<FrooxEngine.Store.Record>();
                    foreach (string recordId in recordIds)
                    {
                        var record = LoadRecord(recordId);
                        if (record != null)
                        {
                            results.Add(record);
                        }
                    }

                    return results;
                }
                catch (Exception ex)
                {
                    Error($"Error searching records: {ex.Message}");
                    return new List<FrooxEngine.Store.Record>();
                }
            }
        }

        #endregion

        #region Helper Methods

        private string GenerateRecordId()
        {
            // Generate a unique ID with a prefix to identify our custom records
            return "CST-" + Guid.NewGuid().ToString();
        }

        private string GenerateDirectoryId()
        {
            // Generate a unique ID with a prefix to identify our custom directories
            return "DIR-" + Guid.NewGuid().ToString();
        }

        #endregion

        #region Logging Methods

        private void Log(string message)
        {
            UniLog.Log($"[CustomDBStorage] {message}");
        }

        private void Error(string message)
        {
            UniLog.Error($"[CustomDBStorage] {message}");
        }

        #endregion
    }
}