using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Elements.Core;
using Newtonsoft.Json;
using System.Linq;
using ResoniteModLoader;

namespace AdvancedInventoryWithStorage
{
    /// <summary>
    /// Service for storing and retrieving files from a remote WebDAV/NextCloud server
    /// </summary>
    public class FileServerService
    {
        // Config reference
        private ModConfiguration _config;

        // HTTP client for server communication
        private HttpClient _client;

        // Credentials and server info
        private string _serverUrl;
        private string _username;
        private string _password;
        private string _remotePath;

        // Constants
        private const string WEBDAV_PATH = "/remote.php/dav/files/";
        private const int MAX_RETRIES = 3;
        private const int RETRY_DELAY_MS = 1000;

        public FileServerService(ModConfiguration config)
        {
            _config = config;
            LoadConfiguration();
            InitializeHttpClient();
        }

        private void LoadConfiguration()
        {
            _serverUrl = _config.GetValue(Mod.FILESERVER_URL);
            _username = _config.GetValue(Mod.FILESERVER_USERNAME);
            _password = _config.GetValue(Mod.FILESERVER_PASSWORD);
            _remotePath = _config.GetValue(Mod.FILESERVER_REMOTE_PATH);

            // Normalize paths
            _serverUrl = _serverUrl.TrimEnd('/');
            _remotePath = _remotePath.Trim('/');
        }

        private void InitializeHttpClient()
        {
            _client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });

            // Set up authentication
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            // Set timeout
            _client.Timeout = TimeSpan.FromMinutes(5);
        }

        #region File Server Operations

        /// <summary>
        /// Tests the connection to the file server
        /// </summary>
        public async Task<bool> TestConnection()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_serverUrl}/status.php");
                var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    var status = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);

                    Log($"Connected to NextCloud server version: {status["version"]}");
                    return true;
                }

                Error($"Failed to connect to file server: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Error($"File server connection error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a directory on the file server
        /// </summary>
        public async Task<bool> CreateDirectory(string relativePath)
        {
            try
            {
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativePath.TrimStart('/')}";

                // MKCOL is a WebDAV method for creating collections (directories)
                var request = new HttpRequestMessage(new HttpMethod("MKCOL"), fullPath);
                var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    // 405 Method Not Allowed typically means directory already exists
                    return true;
                }

                Error($"Failed to create directory {relativePath}: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Error($"Error creating directory {relativePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Uploads a file to the file server
        /// </summary>
        public async Task<bool> UploadFile(string localFilePath, string relativeRemotePath)
        {
            if (!File.Exists(localFilePath))
            {
                Error($"Local file not found: {localFilePath}");
                return false;
            }

            try
            {
                // Create parent directories if needed
                string parentDir = Path.GetDirectoryName(relativeRemotePath).Replace('\\', '/');
                if (!string.IsNullOrEmpty(parentDir))
                {
                    await CreateDirectory(parentDir);
                }

                // Prepare request
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativeRemotePath.TrimStart('/')}";

                byte[] fileBytes = File.ReadAllBytes(localFilePath);

                // Retry logic
                for (int retry = 0; retry < MAX_RETRIES; retry++)
                {
                    try
                    {
                        var content = new ByteArrayContent(fileBytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        var request = new HttpRequestMessage(HttpMethod.Put, fullPath) { Content = content };
                        var response = await _client.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            Log($"Successfully uploaded {relativeRemotePath}");
                            return true;
                        }

                        // If failed and we have more retries, wait and try again
                        if (retry < MAX_RETRIES - 1)
                        {
                            await Task.Delay(RETRY_DELAY_MS * (retry + 1));
                        }
                        else
                        {
                            Error($"Failed to upload {relativeRemotePath}: {response.StatusCode}");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (retry < MAX_RETRIES - 1)
                        {
                            await Task.Delay(RETRY_DELAY_MS * (retry + 1));
                        }
                        else
                        {
                            Error($"Error uploading {relativeRemotePath}: {ex.Message}");
                            return false;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Error($"Error uploading {relativeRemotePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Downloads a file from the file server
        /// </summary>
        public async Task<bool> DownloadFile(string relativeRemotePath, string localFilePath)
        {
            try
            {
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativeRemotePath.TrimStart('/')}";

                // Create local directory if it doesn't exist
                string localDir = Path.GetDirectoryName(localFilePath);
                if (!Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }

                // Retry logic
                for (int retry = 0; retry < MAX_RETRIES; retry++)
                {
                    try
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, fullPath);
                        var response = await _client.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            byte[] data = await response.Content.ReadAsByteArrayAsync();
                            File.WriteAllBytes(localFilePath, data);

                            Log($"Successfully downloaded {relativeRemotePath}");
                            return true;
                        }

                        // If failed and we have more retries, wait and try again
                        if (retry < MAX_RETRIES - 1)
                        {
                            await Task.Delay(RETRY_DELAY_MS * (retry + 1));
                        }
                        else
                        {
                            Error($"Failed to download {relativeRemotePath}: {response.StatusCode}");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (retry < MAX_RETRIES - 1)
                        {
                            await Task.Delay(RETRY_DELAY_MS * (retry + 1));
                        }
                        else
                        {
                            Error($"Error downloading {relativeRemotePath}: {ex.Message}");
                            return false;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Error($"Error downloading {relativeRemotePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if a file exists on the file server
        /// </summary>
        public async Task<bool> FileExists(string relativeRemotePath)
        {
            try
            {
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativeRemotePath.TrimStart('/')}";

                var request = new HttpRequestMessage(HttpMethod.Head, fullPath);
                var response = await _client.SendAsync(request);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Error($"Error checking if file exists {relativeRemotePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Lists all files in a directory on the file server
        /// </summary>
        public async Task<List<string>> ListFiles(string relativeRemotePath)
        {
            List<string> files = new List<string>();

            try
            {
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativeRemotePath.TrimStart('/')}";

                // Use WebDAV PROPFIND to list directory contents
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), fullPath);
                request.Headers.Add("Depth", "1"); // Only immediate children

                var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();

                    // Very basic XML parsing for file paths
                    foreach (string line in content.Split('\n'))
                    {
                        if (line.Contains("<d:href>"))
                        {
                            string href = line.Split(new[] { "<d:href>", "</d:href>" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                            if (!string.IsNullOrEmpty(href))
                            {
                                // Convert server path to relative path and add to list
                                string serverPath = $"{WEBDAV_PATH}{_username}/{_remotePath}/";
                                if (href.Contains(serverPath) && !href.EndsWith(relativeRemotePath))
                                {
                                    string filePath = href.Substring(href.IndexOf(serverPath) + serverPath.Length);
                                    files.Add(filePath);
                                }
                            }
                        }
                    }

                    return files;
                }

                Error($"Failed to list files in {relativeRemotePath}: {response.StatusCode}");
                return files;
            }
            catch (Exception ex)
            {
                Error($"Error listing files in {relativeRemotePath}: {ex.Message}");
                return files;
            }
        }

        /// <summary>
        /// Deletes a file from the file server
        /// </summary>
        public async Task<bool> DeleteFile(string relativeRemotePath)
        {
            try
            {
                string fullPath = $"{_serverUrl}{WEBDAV_PATH}{_username}/{_remotePath}/{relativeRemotePath.TrimStart('/')}";

                var request = new HttpRequestMessage(HttpMethod.Delete, fullPath);
                var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Log($"Successfully deleted {relativeRemotePath}");
                    return true;
                }

                Error($"Failed to delete {relativeRemotePath}: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Error($"Error deleting {relativeRemotePath}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Storage Operations

        /// <summary>
        /// Uploads the database file to the file server
        /// </summary>
        public async Task<bool> UploadDatabase(string dbPath)
        {
            return await UploadFile(dbPath, "database/custom_db.sqlite");
        }

        /// <summary>
        /// Downloads the database file from the file server
        /// </summary>
        public async Task<bool> DownloadDatabase(string dbPath)
        {
            return await DownloadFile("database/custom_db.sqlite", dbPath);
        }

        /// <summary>
        /// Uploads a record asset to the file server
        /// </summary>
        public async Task<bool> UploadRecordAsset(string localFilePath, string recordId, string extension)
        {
            string remotePath = $"assets/{recordId}{extension}";
            return await UploadFile(localFilePath, remotePath);
        }

        /// <summary>
        /// Downloads a record asset from the file server
        /// </summary>
        public async Task<bool> DownloadRecordAsset(string recordId, string localFilePath, string extension)
        {
            string remotePath = $"assets/{recordId}{extension}";
            return await DownloadFile(remotePath, localFilePath);
        }

        /// <summary>
        /// Initializes the basic folder structure on the file server
        /// </summary>
        public async Task<bool> InitializeFileServerStructure()
        {
            bool success = true;

            // Create database directory
            success &= await CreateDirectory("database");

            // Create assets directory
            success &= await CreateDirectory("assets");

            // Create records directory
            success &= await CreateDirectory("records");

            // Create variants directory if needed
            if (_config.GetValue(Mod.STORE_ASSET_VARIANTS))
            {
                success &= await CreateDirectory("variants");
            }

            return success;
        }

        /// <summary>
        /// Uploads an entire local directory to the file server
        /// </summary>
        public async Task<bool> UploadDirectory(string localDirPath, string relativeRemotePath)
        {
            if (!Directory.Exists(localDirPath))
            {
                Error($"Cannot upload nonexistent directory: {localDirPath}");
                return false;
            }

            try
            {
                // Create remote directory
                bool dirCreated = await CreateDirectory(relativeRemotePath);
                if (!dirCreated)
                {
                    return false;
                }

                bool success = true;

                // Upload all files in this directory
                foreach (string file in Directory.GetFiles(localDirPath))
                {
                    string fileName = Path.GetFileName(file);
                    string remotePath = Path.Combine(relativeRemotePath, fileName).Replace('\\', '/');

                    bool fileSuccess = await UploadFile(file, remotePath);
                    success = success && fileSuccess;
                }

                // Recursively upload subdirectories
                foreach (string dir in Directory.GetDirectories(localDirPath))
                {
                    string dirName = Path.GetFileName(dir);
                    string remotePath = Path.Combine(relativeRemotePath, dirName).Replace('\\', '/');

                    bool dirSuccess = await UploadDirectory(dir, remotePath);
                    success = success && dirSuccess;
                }

                return success;
            }
            catch (Exception ex)
            {
                Error($"Error uploading directory {localDirPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Downloads an entire remote directory to local storage
        /// </summary>
        public async Task<bool> DownloadDirectory(string relativeRemotePath, string localDirPath)
        {
            try
            {
                // Create local directory
                if (!Directory.Exists(localDirPath))
                {
                    Directory.CreateDirectory(localDirPath);
                }

                // Get file list
                var files = await ListFiles(relativeRemotePath);
                bool success = true;

                foreach (string remotePath in files)
                {
                    // Check if this is a file or directory
                    if (remotePath.EndsWith("/"))
                    {
                        // It's a directory, recursively download
                        string dirName = Path.GetFileName(remotePath.TrimEnd('/'));
                        string localSubDir = Path.Combine(localDirPath, dirName);
                        bool dirSuccess = await DownloadDirectory(remotePath, localSubDir);
                        success = success && dirSuccess;
                    }
                    else
                    {
                        // It's a file, download it
                        string fileName = Path.GetFileName(remotePath);
                        string localFile = Path.Combine(localDirPath, fileName);
                        bool fileSuccess = await DownloadFile(remotePath, localFile);
                        success = success && fileSuccess;
                    }
                }

                return success;
            }
            catch (Exception ex)
            {
                Error($"Error downloading directory {relativeRemotePath}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Logging Methods

        private void Log(string message)
        {
            UniLog.Log($"[FileServer] {message}");
        }

        private void Error(string message)
        {
            UniLog.Error($"[FileServer] {message}");
        }

        #endregion
    }
}