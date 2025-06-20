using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Text;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;

namespace TaskMaster;

public class DataService : IDisposable
{
    private readonly string _dataFolderPath;
    private readonly string _tasksFilePath;
    private readonly string _routinesFilePath;
    private readonly string _tasksBackupFilePath;
    private readonly string _routinesBackupFilePath;
    private readonly JsonSerializerSettings _jsonSettings;

    public DataService()
    {
        _dataFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMaster");
        _tasksFilePath = Path.Combine(_dataFolderPath, "tasks.json");
        _routinesFilePath = Path.Combine(_dataFolderPath, "routines.json");
        _tasksBackupFilePath = Path.Combine(_dataFolderPath, "tasks_backup.json");
        _routinesBackupFilePath = Path.Combine(_dataFolderPath, "routines_backup.json");

        _jsonSettings = new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Local,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        EnsureDataDirectoryExists();
    }

    private void EnsureDataDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_dataFolderPath))
            {
                Directory.CreateDirectory(_dataFolderPath);
            }

            var testFile = Path.Combine(_dataFolderPath, "test_write.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("TaskMaster does not have permission to write to the application data folder. Please run as administrator or check folder permissions.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException("Unable to create data directory. Please check disk space and permissions.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Unable to access data directory: {ex.Message}");
        }
    }

    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private readonly Dictionary<string, Task> _pendingSaves = new();

    public void SaveTasks(IEnumerable<TaskItem> tasks)
    {
        var saveTask = SaveTasksAsync(tasks);
        saveTask.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var ex = t.Exception.GetBaseException();
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show($"Failed to save tasks: {ex.Message}", "Save Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }, TaskScheduler.Default);
    }

    public async Task SaveTasksAsync(IEnumerable<TaskItem> tasks)
    {
        await SaveDataAsync(tasks, _tasksFilePath, _tasksBackupFilePath, "tasks");
    }

    public IEnumerable<TaskItem> LoadTasks()
    {
        return LoadData<TaskItem>(_tasksFilePath, _tasksBackupFilePath, "tasks");
    }

    public void SaveRoutines(IEnumerable<RoutineItem> routines)
    {
        var saveTask = SaveRoutinesAsync(routines);
        saveTask.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var ex = t.Exception.GetBaseException();
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    MessageBox.Show($"Failed to save routines: {ex.Message}", "Save Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }, TaskScheduler.Default);
    }

    public async Task SaveRoutinesAsync(IEnumerable<RoutineItem> routines)
    {
        await SaveDataAsync(routines, _routinesFilePath, _routinesBackupFilePath, "routines");
    }

    public IEnumerable<RoutineItem> LoadRoutines()
    {
        return LoadData<RoutineItem>(_routinesFilePath, _routinesBackupFilePath, "routines");
    }

    private async Task SaveDataAsync<T>(IEnumerable<T> data, string primaryPath, string backupPath, string dataType)
    {
        var saveKey = Path.GetFileName(primaryPath);
        
        await _saveSemaphore.WaitAsync();
        try
        {
            if (_pendingSaves.TryGetValue(saveKey, out var existingTask) && !existingTask.IsCompleted)
            {
                try { await existingTask; } 
                catch (Exception ex) 
                { 
                    System.Diagnostics.Debug.WriteLine($"Previous save operation failed: {ex.Message}");
                }
            }

            var saveTask = PerformSaveAsync(data, primaryPath, backupPath, dataType);
            _pendingSaves[saveKey] = saveTask;
            await saveTask;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private async Task PerformSaveAsync<T>(IEnumerable<T> data, string primaryPath, string backupPath, string dataType)
    {
        var lockFilePath = primaryPath + ".lock";
        var maxRetries = 3;
        var retryDelay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var lockFile = await AcquireFileLockAsync(lockFilePath, TimeSpan.FromSeconds(5));
                
                if (File.Exists(primaryPath))
                {
                    File.Copy(primaryPath, backupPath, true);
                }

                var json = await Task.Run(() => JsonConvert.SerializeObject(data, _jsonSettings));
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new InvalidOperationException("Serialization resulted in empty data");
                }

                ValidateJson(json);

                var tempPath = primaryPath + ".tmp";
                
                var driveInfo = new DriveInfo(Path.GetPathRoot(primaryPath) ?? "C:");
                if (driveInfo.AvailableFreeSpace < json.Length * 2)
                {
                    throw new IOException("Insufficient disk space to save data");
                }

                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                
                File.Move(tempPath, primaryPath);
                
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(retryDelay);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Access denied while saving {dataType}. Please check file permissions: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save {dataType}: {ex.Message}");
            }
        }

        throw new IOException($"Failed to save {dataType} after {maxRetries} attempts due to file access issues");
    }

    private IEnumerable<T> LoadData<T>(string primaryPath, string backupPath, string dataType)
    {
        var lockFilePath = primaryPath + ".lock";

        try
        {
            using var lockFile = AcquireFileLock(lockFilePath, TimeSpan.FromSeconds(2));
            
            var filesToTry = new[] { primaryPath, backupPath };

            foreach (var filePath in filesToTry)
            {
                if (!File.Exists(filePath)) continue;

                try
                {
                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    ValidateJson(json);

                    var result = JsonConvert.DeserializeObject<List<T>>(json, _jsonSettings);
                    
                    if (result != null)
                    {
                        if (filePath == backupPath && File.Exists(primaryPath))
                        {
                            ShowRecoveryMessage(dataType);
                        }
                        
                        return result;
                    }
                }
                catch (JsonException ex)
                {
                    if (filePath == primaryPath && File.Exists(backupPath))
                    {
                        ShowCorruptionMessage(dataType);
                        continue;
                    }
                    
                    throw new InvalidOperationException($"Both {dataType} files are corrupted: {ex.Message}");
                }
                catch (Exception ex)
                {
                    if (filePath == primaryPath && File.Exists(backupPath))
                    {
                        continue;
                    }
                    
                    throw new InvalidOperationException($"Failed to load {dataType}: {ex.Message}");
                }
            }

            return new List<T>();
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Timeout while trying to access {dataType} file. Another instance may be running.");
        }
    }

    private static void ValidateJson(string json)
    {
        if (json.Length > 50 * 1024 * 1024)
        {
            throw new InvalidOperationException("Data file is too large (>50MB)");
        }

        try
        {
            JsonConvert.DeserializeObject(json);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Invalid JSON format: {ex.Message}");
        }
    }

    private static async Task<FileStream> AcquireFileLockAsync(string lockFilePath, TimeSpan timeout)
    {
        var startTime = DateTime.Now;
        
        while (DateTime.Now - startTime < timeout)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(10);
            }
        }
        
        throw new TimeoutException($"Could not acquire file lock within {timeout.TotalSeconds} seconds");
    }

    private static FileStream AcquireFileLock(string lockFilePath, TimeSpan timeout)
    {
        var startTime = DateTime.Now;
        
        while (DateTime.Now - startTime < timeout)
        {
            try
            {
                return new FileStream(lockFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(10);
            }
        }
        
        throw new TimeoutException($"Could not acquire file lock within {timeout.TotalSeconds} seconds");
    }

    private static void ShowCorruptionMessage(string dataType)
    {
        MessageBox.Show(
            $"The {dataType} file was corrupted but has been recovered from backup. Some recent changes may be lost.",
            "Data Recovery",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void ShowRecoveryMessage(string dataType)
    {
        MessageBox.Show(
            $"Data was recovered from backup file for {dataType}.",
            "Data Recovery",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public void Dispose()
    {
        _saveSemaphore?.Dispose();
    }
}

public class NotificationService
{
    private readonly Dictionary<string, DateTime> _lastNotificationTimes = new();
    private readonly TimeSpan _notificationCooldown = TimeSpan.FromHours(1);

    public void ShowNotification(string title, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title)) title = "TaskMaster";
            if (string.IsNullOrWhiteSpace(message)) return;

            if (title.Length > 100) title = title.Substring(0, 97) + "...";
            if (message.Length > 500) message = message.Substring(0, 497) + "...";

            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification error: {ex.Message}");
        }
    }

    public bool ShowTaskDueNotification(TaskItem task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.Name)) return false;

        var notificationKey = $"task_{task.ID}";
        var now = DateTime.Now;

        if (_lastNotificationTimes.TryGetValue(notificationKey, out var lastTime) && 
            now - lastTime < _notificationCooldown)
        {
            return false;
        }

        try
        {
            var message = $"Task '{task.Name}' is due";
            if (task.DueDate.HasValue)
            {
                message += $" on {task.DueDate.Value.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture)}";
            }

            ShowNotification("Task Due", message);
            
            _lastNotificationTimes[notificationKey] = now;
            task.LastNotificationTime = now;
            
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Task notification error: {ex.Message}");
            return false;
        }
    }

    public void ClearNotificationHistory()
    {
        _lastNotificationTimes.Clear();
    }
}