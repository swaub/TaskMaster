using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Text;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TaskMaster;

public class DataService : IDisposable
{
    private readonly string _dataFolderPath;
    private readonly string _tasksFilePath;
    private readonly string _routinesFilePath;
    private readonly string _tasksBackupFilePath;
    private readonly string _routinesBackupFilePath;
    private readonly string _tasksTransactionLogPath;
    private readonly string _routinesTransactionLogPath;
    private readonly JsonSerializerSettings _jsonSettings;

    public DataService()
    {
        _dataFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMaster");
        _tasksFilePath = Path.Combine(_dataFolderPath, "tasks.json");
        _routinesFilePath = Path.Combine(_dataFolderPath, "routines.json");
        _tasksBackupFilePath = Path.Combine(_dataFolderPath, "tasks_backup.json");
        _routinesBackupFilePath = Path.Combine(_dataFolderPath, "routines_backup.json");
        _tasksTransactionLogPath = Path.Combine(_dataFolderPath, "tasks_transaction.log");
        _routinesTransactionLogPath = Path.Combine(_dataFolderPath, "routines_transaction.log");

        _jsonSettings = new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            Converters = { }
        };

        EnsureDataDirectoryExists();
        RecoverFromIncompleteTransactions();
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

    private void RecoverFromIncompleteTransactions()
    {
        try
        {
            RecoverTransaction(_tasksTransactionLogPath, _tasksFilePath, _tasksBackupFilePath);
            RecoverTransaction(_routinesTransactionLogPath, _routinesFilePath, _routinesBackupFilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during transaction recovery: {ex.Message}");
        }
    }

    private void RecoverTransaction(string transactionLogPath, string primaryPath, string backupPath)
    {
        if (!File.Exists(transactionLogPath))
            return;

        try
        {
            var logContent = File.ReadAllText(transactionLogPath);
            var logLines = logContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            if (logLines.Length < 2)
            {
                File.Delete(transactionLogPath);
                return;
            }

            var operation = logLines[0].Trim();
            var timestamp = logLines[1].Trim();

            if (operation == "SAVE_START")
            {
                var tempPath = primaryPath + ".tmp";
                
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                
                File.Delete(transactionLogPath);
                
                ShowCorruptionMessage(Path.GetFileNameWithoutExtension(primaryPath));
            }
            else if (operation == "SAVE_COMPLETE")
            {
                File.Delete(transactionLogPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error recovering transaction from {transactionLogPath}: {ex.Message}");
            try
            {
                File.Delete(transactionLogPath);
            }
            catch { }
        }
    }

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
        await SaveDataAtomicAsync(tasks, _tasksFilePath, _tasksBackupFilePath, _tasksTransactionLogPath, "tasks");
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
        await SaveDataAtomicAsync(routines, _routinesFilePath, _routinesBackupFilePath, _routinesTransactionLogPath, "routines");
    }

    public IEnumerable<RoutineItem> LoadRoutines()
    {
        return LoadData<RoutineItem>(_routinesFilePath, _routinesBackupFilePath, "routines");
    }

    private async Task SaveDataAtomicAsync<T>(IEnumerable<T> data, string primaryPath, string backupPath, string transactionLogPath, string dataType)
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

            var saveTask = PerformAtomicSaveAsync(data, primaryPath, backupPath, transactionLogPath, dataType);
            _pendingSaves[saveKey] = saveTask;
            await saveTask;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private async Task PerformAtomicSaveAsync<T>(IEnumerable<T> data, string primaryPath, string backupPath, string transactionLogPath, string dataType)
    {
        var lockFilePath = primaryPath + ".lock";
        var tempPath = primaryPath + ".tmp";
        var maxRetries = 3;
        var retryDelay = TimeSpan.FromMilliseconds(100);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            FileStream? lockFile = null;
            bool transactionStarted = false;
            
            try
            {
                lockFile = await AcquireFileLockAsync(lockFilePath, TimeSpan.FromSeconds(5));
                
                await WriteTransactionLogAsync(transactionLogPath, "SAVE_START");
                transactionStarted = true;
                
                if (File.Exists(primaryPath))
                {
                    var backupTempPath = backupPath + ".tmp";
                    File.Copy(primaryPath, backupTempPath, true);
                    
                    if (await ValidateFileIntegrityAsync(backupTempPath, dataType))
                    {
                        if (File.Exists(backupPath))
                        {
                            File.Delete(backupPath);
                        }
                        File.Move(backupTempPath, backupPath);
                    }
                    else
                    {
                        File.Delete(backupTempPath);
                        throw new InvalidOperationException("Failed to create valid backup");
                    }
                }

                var json = await Task.Run(() => JsonConvert.SerializeObject(data, _jsonSettings));
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new InvalidOperationException("Serialization resulted in empty data");
                }

                ValidateJson(json);
                ValidateDataIntegrity(data, dataType);

                var driveInfo = new DriveInfo(Path.GetPathRoot(primaryPath) ?? "C:");
                var requiredSpace = json.Length * 3;
                if (driveInfo.AvailableFreeSpace < requiredSpace)
                {
                    throw new IOException($"Insufficient disk space to save data. Required: {requiredSpace}, Available: {driveInfo.AvailableFreeSpace}");
                }

                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                
                if (!await ValidateFileIntegrityAsync(tempPath, dataType))
                {
                    throw new InvalidOperationException("Written file failed integrity validation");
                }

                if (File.Exists(primaryPath))
                {
                    File.Delete(primaryPath);
                }
                
                File.Move(tempPath, primaryPath);
                
                await WriteTransactionLogAsync(transactionLogPath, "SAVE_COMPLETE");
                
                File.Delete(transactionLogPath);
                
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
                
                await Task.Delay(retryDelay);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Access denied while saving {dataType}. Please check file permissions: {ex.Message}");
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch { }
                }
                
                throw new InvalidOperationException($"Failed to save {dataType}: {ex.Message}");
            }
            finally
            {
                lockFile?.Dispose();
            }
        }

        throw new IOException($"Failed to save {dataType} after {maxRetries} attempts due to file access issues");
    }

    private async Task WriteTransactionLogAsync(string transactionLogPath, string operation)
    {
        var logContent = $"{operation}\n{TimezoneService.Instance.UtcNow:yyyy-MM-dd HH:mm:ss.fff}\n";
        await File.WriteAllTextAsync(transactionLogPath, logContent, Encoding.UTF8);
    }

    private async Task<bool> ValidateFileIntegrityAsync(string filePath, string dataType)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            ValidateJson(content);
            
            if (dataType == "tasks")
            {
                var tasks = JsonConvert.DeserializeObject<List<TaskItem>>(content, _jsonSettings);
                return tasks != null;
            }
            else if (dataType == "routines")
            {
                var routines = JsonConvert.DeserializeObject<List<RoutineItem>>(content, _jsonSettings);
                return routines != null;
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ValidateDataIntegrity<T>(IEnumerable<T> data, string dataType)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        
        var dataList = data.ToList();
        
        if (dataType == "tasks")
        {
            var tasks = dataList.Cast<TaskItem>().ToList();
            var ids = new HashSet<string>();
            
            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.ID))
                    throw new InvalidOperationException("Task found with empty ID");
                
                if (!ids.Add(task.ID))
                    throw new InvalidOperationException($"Duplicate task ID found: {task.ID}");
                
                if (string.IsNullOrWhiteSpace(task.Name))
                    throw new InvalidOperationException("Task found with empty name");
                
                if (!ValidationUtils.IsValidPriority(task.Priority))
                    throw new InvalidOperationException($"Task has invalid priority: {task.Priority}");
            }
        }
        else if (dataType == "routines")
        {
            var routines = dataList.Cast<RoutineItem>().ToList();
            var ids = new HashSet<string>();
            
            foreach (var routine in routines)
            {
                if (string.IsNullOrWhiteSpace(routine.ID))
                    throw new InvalidOperationException("Routine found with empty ID");
                
                if (!ids.Add(routine.ID))
                    throw new InvalidOperationException($"Duplicate routine ID found: {routine.ID}");
                
                if (string.IsNullOrWhiteSpace(routine.Name))
                    throw new InvalidOperationException("Routine found with empty name");
            }
        }
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
                catch (Newtonsoft.Json.JsonException ex)
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
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new Newtonsoft.Json.JsonException($"Invalid JSON format: {ex.Message}");
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
        var now = TimezoneService.Instance.Now;

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
                var relativeDateText = TimezoneService.Instance.GetRelativeDateString(task.DueDate.Value);
                message += $" {relativeDateText}";
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

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    Performance = 6
}

public class AdvancedLoggingService : IDisposable
{
    private readonly string _logDirectory;
    private readonly string _currentLogFile;
    private readonly ConcurrentQueue<LogEntry> _logQueue;
    private readonly AutoResetEvent _logEvent;
    private readonly Thread _logThread;
    private readonly object _fileLock = new();
    private readonly Timer _rotationTimer;
    private volatile bool _disposed = false;
    private long _currentFileSize = 0;
    private int _logSequence = 0;
    
    private const long MAX_LOG_FILE_SIZE = 10 * 1024 * 1024;
    private const int MAX_LOG_FILES = 10;
    private const int LOG_QUEUE_CAPACITY = 10000;

    public AdvancedLoggingService()
    {
        _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMaster", "Logs");
        Directory.CreateDirectory(_logDirectory);
        
        _currentLogFile = Path.Combine(_logDirectory, $"TaskMaster_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        _logQueue = new ConcurrentQueue<LogEntry>();
        _logEvent = new AutoResetEvent(false);
        
        _logThread = new Thread(ProcessLogQueue)
        {
            Name = "LoggingThread",
            IsBackground = true
        };
        _logThread.Start();
        
        _rotationTimer = new Timer(CheckLogRotation, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        
        LogInfo("AdvancedLoggingService initialized", "System");
    }

    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
    public bool EnableConsoleOutput { get; set; } = true;
    public bool EnableFileOutput { get; set; } = true;
    public bool EnablePerformanceLogging { get; set; } = true;

    public void LogTrace(string message, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Trace, message, caller, filePath, lineNumber);
    }

    public void LogDebug(string message, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Debug, message, caller, filePath, lineNumber);
    }

    public void LogInfo(string message, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Information, message, caller, filePath, lineNumber);
    }

    public void LogWarning(string message, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Warning, message, caller, filePath, lineNumber);
    }

    public void LogError(string message, Exception? exception = null, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Error, message, caller, filePath, lineNumber, exception);
    }

    public void LogCritical(string message, Exception? exception = null, [CallerMemberName] string caller = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
    {
        Log(LogLevel.Critical, message, caller, filePath, lineNumber, exception);
    }

    public void LogPerformance(string operation, TimeSpan duration, [CallerMemberName] string caller = "")
    {
        if (EnablePerformanceLogging)
        {
            Log(LogLevel.Performance, $"PERF: {operation} completed in {duration.TotalMilliseconds:F2}ms", caller);
        }
    }

    public IDisposable BeginScope(string scopeName)
    {
        return new LogScope(this, scopeName);
    }

    public void LogStructured<T>(LogLevel level, string message, T data, [CallerMemberName] string caller = "")
    {
        try
        {
            var serializedData = System.Text.Json.JsonSerializer.Serialize(data);
            Log(level, $"{message} | Data: {serializedData}", caller);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Failed to serialize structured log data: {ex.Message}", caller);
        }
    }

    private void Log(LogLevel level, string message, string caller, string filePath = "", int lineNumber = 0, Exception? exception = null)
    {
        if (level < MinimumLogLevel || _disposed)
            return;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var logEntry = new LogEntry
        {
            Timestamp = TimezoneService.Instance.Now,
            Level = level,
            Message = message,
            Caller = caller,
            FileName = fileName,
            LineNumber = lineNumber,
            Exception = exception,
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            Sequence = Interlocked.Increment(ref _logSequence)
        };

        if (_logQueue.Count >= LOG_QUEUE_CAPACITY)
        {
            for (int i = 0; i < 1000; i++)
            {
                _logQueue.TryDequeue(out _);
            }
        }

        _logQueue.Enqueue(logEntry);
        _logEvent.Set();
    }

    private void ProcessLogQueue()
    {
        var buffer = new List<LogEntry>();
        
        while (!_disposed)
        {
            try
            {
                _logEvent.WaitOne(1000);
                
                if (_disposed) break;

                buffer.Clear();
                while (_logQueue.TryDequeue(out var entry) && buffer.Count < 100)
                {
                    buffer.Add(entry);
                }

                if (buffer.Count > 0)
                {
                    ProcessLogEntries(buffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in logging thread: {ex.Message}");
            }
        }
    }

    private void ProcessLogEntries(List<LogEntry> entries)
    {
        foreach (var entry in entries)
        {
            var logLine = FormatLogEntry(entry);
            
            if (EnableConsoleOutput)
            {
                WriteToConsole(entry, logLine);
            }
            
            if (EnableFileOutput)
            {
                WriteToFile(logLine);
            }
        }
    }

    private string FormatLogEntry(LogEntry entry)
    {
        var sb = new StringBuilder();
        
        sb.Append($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} ");
        sb.Append($"[{entry.Level}] ");
        sb.Append($"[T{entry.ThreadId:D3}] ");
        sb.Append($"[{entry.Sequence:D8}] ");
        
        if (!string.IsNullOrEmpty(entry.FileName))
        {
            sb.Append($"{entry.FileName}::{entry.Caller}:{entry.LineNumber} ");
        }
        else
        {
            sb.Append($"{entry.Caller} ");
        }
        
        sb.Append($"- {entry.Message}");
        
        if (entry.Exception != null)
        {
            sb.AppendLine();
            sb.Append($"Exception: {entry.Exception}");
        }
        
        return sb.ToString();
    }

    private void WriteToConsole(LogEntry entry, string logLine)
    {
        try
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetConsoleColor(entry.Level);
            Console.WriteLine(logLine);
            Console.ForegroundColor = originalColor;
        }
        catch
        {
        }
    }

    private ConsoleColor GetConsoleColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Critical => ConsoleColor.Magenta,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Information => ConsoleColor.White,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Performance => ConsoleColor.Cyan,
            _ => ConsoleColor.White
        };
    }

    private void WriteToFile(string logLine)
    {
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_currentLogFile, logLine + Environment.NewLine, Encoding.UTF8);
                _currentFileSize += Encoding.UTF8.GetByteCount(logLine + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write to log file: {ex.Message}");
        }
    }

    private void CheckLogRotation(object? state)
    {
        if (_disposed) return;

        try
        {
            lock (_fileLock)
            {
                if (_currentFileSize > MAX_LOG_FILE_SIZE)
                {
                    RotateLogFile();
                }
                
                CleanupOldLogFiles();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during log rotation: {ex.Message}");
        }
    }

    private void RotateLogFile()
    {
        try
        {
            var rotatedFileName = Path.Combine(_logDirectory, $"TaskMaster_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            
            if (File.Exists(_currentLogFile))
            {
                File.Move(_currentLogFile, rotatedFileName);
            }
            
            _currentFileSize = 0;
            LogInfo("Log file rotated", "AdvancedLoggingService");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to rotate log file: {ex.Message}");
        }
    }

    private void CleanupOldLogFiles()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, "TaskMaster_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Skip(MAX_LOG_FILES)
                .ToList();

            foreach (var file in logFiles)
            {
                try
                {
                    file.Delete();
                    LogDebug($"Deleted old log file: {file.Name}", "AdvancedLoggingService");
                }
                catch (Exception ex)
                {
                    LogWarning($"Failed to delete old log file {file.Name}: {ex.Message}", "AdvancedLoggingService");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up old log files: {ex.Message}");
        }
    }

    public LoggingStatistics GetStatistics()
    {
        return new LoggingStatistics
        {
            QueueSize = _logQueue.Count,
            CurrentLogFileSize = _currentFileSize,
            TotalLogFiles = Directory.GetFiles(_logDirectory, "TaskMaster_*.log").Length,
            LogDirectory = _logDirectory,
            CurrentLogFile = _currentLogFile,
            IsLoggingThreadAlive = _logThread.IsAlive
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        _logEvent.Set();
        _logThread?.Join(TimeSpan.FromSeconds(5));
        
        _rotationTimer?.Dispose();
        _logEvent?.Dispose();
        
        LogInfo("AdvancedLoggingService disposed", "System");
    }
}

public class LogScope : IDisposable
{
    private readonly AdvancedLoggingService _logger;
    private readonly string _scopeName;
    private readonly Stopwatch _stopwatch;

    public LogScope(AdvancedLoggingService logger, string scopeName)
    {
        _logger = logger;
        _scopeName = scopeName;
        _stopwatch = Stopwatch.StartNew();
        _logger.LogDebug($"Entering scope: {_scopeName}");
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _logger.LogDebug($"Exiting scope: {_scopeName} (Duration: {_stopwatch.Elapsed.TotalMilliseconds:F2}ms)");
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Caller { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public Exception? Exception { get; set; }
    public int ThreadId { get; set; }
    public int Sequence { get; set; }
}

public class LoggingStatistics
{
    public int QueueSize { get; set; }
    public long CurrentLogFileSize { get; set; }
    public int TotalLogFiles { get; set; }
    public string LogDirectory { get; set; } = string.Empty;
    public string CurrentLogFile { get; set; } = string.Empty;
    public bool IsLoggingThreadAlive { get; set; }
}

public class ConfigurationService : INotifyPropertyChanged, IDisposable
{
    private readonly string _configDirectory;
    private readonly string _userConfigFile;
    private readonly string _systemConfigFile;
    private readonly FileSystemWatcher? _fileWatcher;
    private readonly ConcurrentDictionary<string, object> _configCache;
    private readonly object _fileLock = new();
    private readonly Timer _saveTimer;
    private readonly JsonSerializerOptions _jsonOptions;
    private volatile bool _disposed = false;
    private volatile bool _hasUnsavedChanges = false;

    public ConfigurationService()
    {
        _configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMaster", "Config");
        Directory.CreateDirectory(_configDirectory);
        
        _userConfigFile = Path.Combine(_configDirectory, "user.config.json");
        _systemConfigFile = Path.Combine(_configDirectory, "system.config.json");
        
        _configCache = new ConcurrentDictionary<string, object>();
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        
        _saveTimer = new Timer(SavePendingChanges, null, Timeout.Infinite, Timeout.Infinite);
        
        _fileWatcher = InitializeFileWatcher();
        LoadConfiguration();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    private FileSystemWatcher? InitializeFileWatcher()
    {
        try
        {
            var watcher = new FileSystemWatcher(_configDirectory, "*.config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            
            watcher.Changed += OnConfigFileChanged;
            watcher.Created += OnConfigFileChanged;
            return watcher;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize configuration file watcher: {ex.Message}");
            return null;
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            Thread.Sleep(100);
            
            if (e.FullPath.Equals(_userConfigFile, StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.Equals(_systemConfigFile, StringComparison.OrdinalIgnoreCase))
            {
                LoadConfiguration();
                ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(e.FullPath));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling configuration file change: {ex.Message}");
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            lock (_fileLock)
            {
                LoadConfigurationFile(_systemConfigFile);
                LoadConfigurationFile(_userConfigFile);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading configuration: {ex.Message}");
        }
    }

    private void LoadConfigurationFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            InitializeDefaultConfig(filePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var configData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _jsonOptions);
            
            if (configData != null)
            {
                foreach (var kvp in configData)
                {
                    _configCache[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading configuration file {filePath}: {ex.Message}");
            InitializeDefaultConfig(filePath);
        }
    }

    private void InitializeDefaultConfig(string filePath)
    {
        try
        {
            var defaultConfig = GetDefaultConfiguration(filePath);
            var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, _jsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing default configuration: {ex.Message}");
        }
    }

    private Dictionary<string, object> GetDefaultConfiguration(string filePath)
    {
        if (filePath.Equals(_userConfigFile, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object>
            {
                ["UI.Theme"] = "Light",
                ["UI.Language"] = "English",
                ["UI.StartupView"] = "List",
                ["UI.ShowCompletedTasks"] = false,
                ["UI.AutoSave"] = true,
                ["UI.ConfirmDeletions"] = true,
                ["UI.ShowNotifications"] = true,
                ["UI.NotificationSound"] = true,
                ["UI.MinimizeToTray"] = false,
                ["UI.StartWithWindows"] = false,
                ["Data.AutoBackup"] = true,
                ["Data.BackupInterval"] = 24,
                ["Data.MaxBackups"] = 7,
                ["Data.BackupLocation"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TaskMaster Backups"),
                ["Performance.EnableCaching"] = true,
                ["Performance.CacheSize"] = 1000,
                ["Performance.BackgroundProcessing"] = true,
                ["Performance.VirtualizationThreshold"] = 500,
                ["Logging.Level"] = LogLevel.Information,
                ["Logging.EnableFileLogging"] = true,
                ["Logging.EnableConsoleLogging"] = false,
                ["Logging.EnablePerformanceLogging"] = true
            };
        }
        else
        {
            return new Dictionary<string, object>
            {
                ["System.Version"] = "1.0.0",
                ["System.InstallDate"] = DateTime.Now,
                ["System.LastUpdateCheck"] = DateTime.Now,
                ["System.UsageStatistics"] = true,
                ["System.ErrorReporting"] = true,
                ["System.MaxLogFiles"] = 10,
                ["System.MaxLogSizeMB"] = 10,
                ["System.DatabaseVersion"] = "1.0.0",
                ["System.FeatureFlags"] = new Dictionary<string, bool>
                {
                    ["AdvancedSearch"] = true,
                    ["TaskTemplates"] = true,
                    ["RecurringTasks"] = true,
                    ["ExportImport"] = true,
                    ["PluginSupport"] = false
                }
            };
        }
    }

    public T GetValue<T>(string key, T defaultValue = default!)
    {
        try
        {
            if (_configCache.TryGetValue(key, out var value))
            {
                if (value is JsonElement jsonElement)
                {
                    return System.Text.Json.JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), _jsonOptions) ?? defaultValue;
                }
                
                if (value is T directValue)
                {
                    return directValue;
                }
                
                return (T)Convert.ChangeType(value, typeof(T)) ?? defaultValue;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting configuration value for key '{key}': {ex.Message}");
        }
        
        return defaultValue;
    }

    public void SetValue<T>(string key, T value)
    {
        try
        {
            _configCache[key] = value ?? throw new ArgumentNullException(nameof(value));
            _hasUnsavedChanges = true;
            
            _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            
            OnPropertyChanged(key);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting configuration value for key '{key}': {ex.Message}");
        }
    }

    public bool HasValue(string key)
    {
        return _configCache.ContainsKey(key);
    }

    public void RemoveValue(string key)
    {
        if (_configCache.TryRemove(key, out _))
        {
            _hasUnsavedChanges = true;
            _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            OnPropertyChanged(key);
        }
    }

    public Dictionary<string, object> GetAllValues()
    {
        return _configCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void SetValues(Dictionary<string, object> values)
    {
        foreach (var kvp in values)
        {
            _configCache[kvp.Key] = kvp.Value;
        }
        
        _hasUnsavedChanges = true;
        _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        OnPropertyChanged();
    }

    public void SaveImmediately()
    {
        SavePendingChanges(null);
    }

    private void SavePendingChanges(object? state)
    {
        if (!_hasUnsavedChanges || _disposed) return;

        try
        {
            lock (_fileLock)
            {
                var userConfig = new Dictionary<string, object>();
                var systemConfig = new Dictionary<string, object>();
                
                foreach (var kvp in _configCache)
                {
                    if (kvp.Key.StartsWith("System."))
                    {
                        systemConfig[kvp.Key] = kvp.Value;
                    }
                    else
                    {
                        userConfig[kvp.Key] = kvp.Value;
                    }
                }
                
                SaveConfigurationFile(_userConfigFile, userConfig);
                SaveConfigurationFile(_systemConfigFile, systemConfig);
                
                _hasUnsavedChanges = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving configuration: {ex.Message}");
        }
    }

    private void SaveConfigurationFile(string filePath, Dictionary<string, object> config)
    {
        try
        {
            var tempFile = filePath + ".tmp";
            var json = System.Text.Json.JsonSerializer.Serialize(config, _jsonOptions);
            
            File.WriteAllText(tempFile, json);
            
            if (File.Exists(filePath))
            {
                File.Replace(tempFile, filePath, null);
            }
            else
            {
                File.Move(tempFile, filePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving configuration file {filePath}: {ex.Message}");
        }
    }

    public void ResetToDefaults()
    {
        try
        {
            lock (_fileLock)
            {
                _configCache.Clear();
                
                if (File.Exists(_userConfigFile))
                {
                    File.Delete(_userConfigFile);
                }
                
                if (File.Exists(_systemConfigFile))
                {
                    File.Delete(_systemConfigFile);
                }
                
                InitializeDefaultConfig(_userConfigFile);
                InitializeDefaultConfig(_systemConfigFile);
                
                LoadConfiguration();
                
                _hasUnsavedChanges = false;
                OnPropertyChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error resetting configuration to defaults: {ex.Message}");
        }
    }

    public void ExportConfiguration(string exportPath)
    {
        try
        {
            var exportData = new
            {
                ExportDate = DateTime.Now,
                Version = GetValue<string>("System.Version", "1.0.0"),
                Configuration = GetAllValues()
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(exportData, _jsonOptions);
            File.WriteAllText(exportPath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to export configuration: {ex.Message}", ex);
        }
    }

    public void ImportConfiguration(string importPath)
    {
        try
        {
            var json = File.ReadAllText(importPath);
            var importData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);
            
            if (importData.TryGetProperty("Configuration", out var configElement))
            {
                var configData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configElement.GetRawText(), _jsonOptions);
                
                if (configData != null)
                {
                    foreach (var kvp in configData)
                    {
                        _configCache[kvp.Key] = kvp.Value;
                    }
                    
                    _hasUnsavedChanges = true;
                    SaveImmediately();
                    OnPropertyChanged();
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to import configuration: {ex.Message}", ex);
        }
    }

    public ConfigurationStatistics GetStatistics()
    {
        return new ConfigurationStatistics
        {
            TotalSettings = _configCache.Count,
            UserConfigFile = _userConfigFile,
            SystemConfigFile = _systemConfigFile,
            ConfigDirectory = _configDirectory,
            HasUnsavedChanges = _hasUnsavedChanges,
            LastModified = File.Exists(_userConfigFile) ? File.GetLastWriteTime(_userConfigFile) : DateTime.MinValue
        };
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        _saveTimer?.Dispose();
        _fileWatcher?.Dispose();
        
        if (_hasUnsavedChanges)
        {
            SavePendingChanges(null);
        }
    }
}

public class ConfigurationChangedEventArgs : EventArgs
{
    public string FilePath { get; }
    public DateTime Timestamp { get; }

    public ConfigurationChangedEventArgs(string filePath)
    {
        FilePath = filePath;
        Timestamp = DateTime.Now;
    }
}

public class ConfigurationStatistics
{
    public int TotalSettings { get; set; }
    public string UserConfigFile { get; set; } = string.Empty;
    public string SystemConfigFile { get; set; } = string.Empty;
    public string ConfigDirectory { get; set; } = string.Empty;
    public bool HasUnsavedChanges { get; set; }
    public DateTime LastModified { get; set; }
}

public static class ConfigurationExtensions
{
    public static void BindToConfiguration<T>(this T obj, ConfigurationService config, string keyPrefix = "") where T : INotifyPropertyChanged
    {
        obj.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != null)
            {
                var key = string.IsNullOrEmpty(keyPrefix) ? e.PropertyName : $"{keyPrefix}.{e.PropertyName}";
                var propertyInfo = typeof(T).GetProperty(e.PropertyName);
                
                if (propertyInfo != null)
                {
                    var value = propertyInfo.GetValue(obj);
                    if (value != null)
                    {
                        config.SetValue(key, value);
                    }
                }
            }
        };
    }
}