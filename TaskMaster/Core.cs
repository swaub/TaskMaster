using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace TaskMaster;

public class TaskItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    private string _name = string.Empty;
    private string _description = string.Empty;
    private int _priority = 5;
    private DateTime? _dueDate;
    private string _category = "General";
    private bool _isCompleted = false;
    private DateTime _createdDate = TimezoneService.Instance.Now;
    private DateTime? _lastNotificationTime;

    public string ID
    {
        get => _id;
        set
        {
            if (_id != value)
            {
                _id = value;
                OnPropertyChanged();
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeTaskName(value);
            
            if (_name != sanitizedValue)
            {
                _name = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeDescription(value);
            
            if (_description != sanitizedValue)
            {
                _description = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            var clampedValue = ValidationUtils.ClampPriority(value);
            if (_priority != clampedValue)
            {
                _priority = clampedValue;
                OnPropertyChanged();
            }
        }
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set
        {
            var validatedValue = ValidationUtils.ValidateAndClampDate(value);
            
            if (_dueDate != validatedValue)
            {
                _dueDate = validatedValue;
                OnPropertyChanged();
            }
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeCategory(value);
            
            if (_category != sanitizedValue)
            {
                _category = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted != value)
            {
                _isCompleted = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime CreatedDate
    {
        get => _createdDate;
        set
        {
            if (_createdDate != value)
            {
                _createdDate = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime? LastNotificationTime
    {
        get => _lastNotificationTime;
        set
        {
            if (_lastNotificationTime != value)
            {
                _lastNotificationTime = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDueForNotification()
    {
        if (!DueDate.HasValue || IsCompleted) return false;
        
        var now = TimezoneService.Instance.Now;
        var timeSinceLastNotification = LastNotificationTime.HasValue 
            ? now - LastNotificationTime.Value 
            : TimeSpan.MaxValue;
        
        return TimezoneService.Instance.IsDueToday(DueDate.Value) && timeSinceLastNotification >= TimeSpan.FromHours(1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RoutineItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    private string _name = string.Empty;
    private bool _isCompleted = false;
    private DateTime _lastCompletedDate = DateTime.MinValue;

    public string ID
    {
        get => _id;
        set
        {
            if (_id != value)
            {
                _id = value;
                OnPropertyChanged();
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeTaskName(value);
            
            if (_name != sanitizedValue)
            {
                _name = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted != value)
            {
                _isCompleted = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime LastCompletedDate
    {
        get => _lastCompletedDate;
        set
        {
            if (_lastCompletedDate != value)
            {
                _lastCompletedDate = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class ValidationUtils
{
    private static readonly Regex _controlCharRegex = new(@"[\x00-\x1F\x7F-\x9F]", RegexOptions.Compiled);
    private static readonly Regex _dangerousUnicodeRegex = new(@"[\u202A-\u202E\u2066-\u2069\u061C\u200E\u200F]", RegexOptions.Compiled);
    private static readonly Regex _invalidFileNameChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    
    private static readonly char[] _nullOrControlChars = 
    {
        '\0', '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07', '\x08', '\x0B', '\x0C', 
        '\x0E', '\x0F', '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17', '\x18', 
        '\x19', '\x1A', '\x1B', '\x1C', '\x1D', '\x1E', '\x1F', '\x7F'
    };

    public static string SanitizeText(string? input, int maxLength = int.MaxValue, bool allowNewlines = true)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sanitized = input;

        sanitized = _controlCharRegex.Replace(sanitized, match =>
        {
            char c = match.Value[0];
            if (allowNewlines && (c == '\r' || c == '\n' || c == '\t'))
                return match.Value;
            return string.Empty;
        });

        sanitized = _dangerousUnicodeRegex.Replace(sanitized, string.Empty);

        if (sanitized.IndexOfAny(_nullOrControlChars) >= 0)
        {
            var sb = new StringBuilder(sanitized.Length);
            foreach (char c in sanitized)
            {
                if (!_nullOrControlChars.Contains(c))
                {
                    sb.Append(c);
                }
            }
            sanitized = sb.ToString();
        }

        sanitized = NormalizeWhitespace(sanitized, allowNewlines);

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength);
        }

        sanitized = sanitized.Trim();

        return sanitized;
    }

    public static string SanitizeFileName(string? input, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sanitized = SanitizeText(input, maxLength, false);
        sanitized = _invalidFileNameChars.Replace(sanitized, string.Empty);

        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reservedNames.Contains(sanitized.ToUpperInvariant()))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }

    public static string NormalizeWhitespace(string input, bool allowNewlines = true)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        bool lastWasWhitespace = false;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace)
                {
                    if (allowNewlines && (c == '\r' || c == '\n'))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(' ');
                    }
                    lastWasWhitespace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasWhitespace = false;
            }
        }

        return sb.ToString();
    }

    public static bool IsValidPriority(int priority)
    {
        return priority >= 1 && priority <= 10;
    }

    public static int ClampPriority(int priority)
    {
        return Math.Max(1, Math.Min(10, priority));
    }

    public static bool IsValidDate(DateTime? date)
    {
        if (!date.HasValue)
            return true;

        var dateValue = date.Value;
        
        if (dateValue.Year < 1900 || dateValue.Year > 2100)
            return false;

        if (dateValue < DateTime.Today)
            return false;

        try
        {
            var testDate = new DateTime(dateValue.Year, dateValue.Month, dateValue.Day);
            return testDate == dateValue.Date;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static DateTime? ValidateAndClampDate(DateTime? date)
    {
        if (!date.HasValue)
            return null;

        var dateValue = date.Value;

        if (dateValue.Year < 1900)
            return new DateTime(1900, 1, 1);

        if (dateValue.Year > 2100)
            return new DateTime(2100, 12, 31);

        if (dateValue.Date < DateTime.Today)
            return DateTime.Today;

        try
        {
            if (dateValue.Month == 2 && dateValue.Day == 29)
            {
                if (!DateTime.IsLeapYear(dateValue.Year))
                {
                    return new DateTime(dateValue.Year, 2, 28);
                }
            }

            return new DateTime(dateValue.Year, dateValue.Month, dateValue.Day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.Today;
        }
    }

    public static bool IsValidTaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var sanitized = SanitizeText(name, 100, false);
        return !string.IsNullOrWhiteSpace(sanitized) && sanitized.Length <= 100;
    }

    public static bool IsValidDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return true;

        var sanitized = SanitizeText(description, 500, true);
        return sanitized.Length <= 500;
    }

    public static bool IsValidCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return false;

        var sanitized = SanitizeText(category, 50, false);
        return !string.IsNullOrWhiteSpace(sanitized) && sanitized.Length <= 50;
    }

    public static string ValidateAndSanitizeTaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return SanitizeText(name, 100, false);
    }

    public static string ValidateAndSanitizeDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return string.Empty;

        return SanitizeText(description, 500, true);
    }

    public static string ValidateAndSanitizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return "General";

        var sanitized = SanitizeText(category, 50, false);
        return string.IsNullOrWhiteSpace(sanitized) ? "General" : sanitized;
    }

    public static bool ContainsOnlyValidCharacters(string input)
    {
        if (string.IsNullOrEmpty(input))
            return true;

        foreach (char c in input)
        {
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                return false;

            if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return false;
        }

        return true;
    }

    public static ValidationResult ValidateTaskInput(string? name, string? description, int priority, DateTime? dueDate, string? category)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(name))
        {
            result.AddError("Task name is required");
        }
        else
        {
            var sanitizedName = SanitizeText(name, 100, false);
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                result.AddError("Task name contains only invalid characters");
            }
            else if (sanitizedName.Length > 100)
            {
                result.AddError("Task name is too long (maximum 100 characters)");
            }
        }

        if (!string.IsNullOrEmpty(description))
        {
            var sanitizedDescription = SanitizeText(description, 500, true);
            if (sanitizedDescription.Length > 500)
            {
                result.AddError("Description is too long (maximum 500 characters)");
            }
        }

        if (!IsValidPriority(priority))
        {
            result.AddError("Priority must be between 1 and 10");
        }

        if (!IsValidDate(dueDate))
        {
            result.AddError("Due date must be a valid future date");
        }

        if (!string.IsNullOrEmpty(category))
        {
            var sanitizedCategory = SanitizeText(category, 50, false);
            if (sanitizedCategory.Length > 50)
            {
                result.AddError("Category name is too long (maximum 50 characters)");
            }
        }

        return result;
    }

    public static ValidationResult ValidateRoutineInput(string? name)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(name))
        {
            result.AddError("Routine name is required");
        }
        else
        {
            var sanitizedName = SanitizeText(name, 100, false);
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                result.AddError("Routine name contains only invalid characters");
            }
            else if (sanitizedName.Length > 100)
            {
                result.AddError("Routine name is too long (maximum 100 characters)");
            }
        }

        return result;
    }
}

public class ValidationResult
{
    private readonly List<string> _errors = new();

    public bool IsValid => _errors.Count == 0;
    public IReadOnlyList<string> Errors => _errors;

    public void AddError(string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _errors.Add(error);
        }
    }

    public string GetErrorMessage()
    {
        return string.Join(Environment.NewLine, _errors);
    }
}

public sealed class TimezoneService
{
    private static readonly Lazy<TimezoneService> _instance = new(() => new TimezoneService());
    public static TimezoneService Instance => _instance.Value;

    private readonly TimeZoneInfo _timeZone;

    private TimezoneService()
    {
        _timeZone = TimeZoneInfo.Local;
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime Today => Now.Date;

    public bool IsDueToday(DateTime dueDate)
    {
        return dueDate.Date == Today;
    }

    public bool IsPastDue(DateTime dueDate)
    {
        return dueDate.Date < Today;
    }

    public string FormatDateTime(DateTime dateTime)
    {
        var daysDifference = (int)(dateTime.Date - Today).TotalDays;
        
        return daysDifference switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            > 0 and <= 7 => dateTime.ToString("dddd"),
            _ => dateTime.ToString("MMM dd, yyyy")
        };
    }

    public string GetRelativeDateString(DateTime dateTime)
    {
        return FormatDateTime(dateTime);
    }

    public bool IsToday(DateTime dateTime)
    {
        return dateTime.Date == Today;
    }

    public DateTime ConvertToLocal(DateTime utcDateTime)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, _timeZone);
    }

    public DateTime ConvertToUtc(DateTime localDateTime)
    {
        return TimeZoneInfo.ConvertTimeToUtc(localDateTime, _timeZone);
    }
}

public class SingleInstanceManager
{
    private static Mutex? _mutex;
    private static bool _isOwned;
    private const string MutexName = "TaskMaster_SingleInstance_Mutex";

    public static bool IsAnotherInstanceRunning()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out _isOwned);
            return !_isOwned;
        }
        catch (AbandonedMutexException)
        {
            _isOwned = true;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void ReleaseMutex()
    {
        if (_mutex != null && _isOwned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception)
            {
            }
            finally
            {
                _mutex.Dispose();
                _mutex = null;
                _isOwned = false;
            }
        }
    }
}

public class StartupValidationService
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public bool IsValid => _errors.Count == 0;
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;

    public void ValidateEnvironment()
    {
        ValidateOperatingSystem();
        ValidatePermissions();
        ValidateStorage();
        ValidateMemory();
        ValidateSystemRequirements();
    }

    private void ValidateOperatingSystem()
    {
        if (!Environment.OSVersion.Platform.ToString().Contains("Win"))
        {
            _errors.Add("TaskMaster requires Windows operating system");
        }

        if (Environment.OSVersion.Version.Major < 10)
        {
            _warnings.Add("TaskMaster is optimized for Windows 10 or later");
        }
    }

    private void ValidatePermissions()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var taskMasterPath = Path.Combine(appDataPath, "TaskMaster");

        try
        {
            if (!Directory.Exists(taskMasterPath))
            {
                Directory.CreateDirectory(taskMasterPath);
            }

            var testFile = Path.Combine(taskMasterPath, "test.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (Exception)
        {
            _errors.Add("Insufficient permissions to access application data folder");
        }
    }

    private void ValidateStorage()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var drive = new DriveInfo(Path.GetPathRoot(appDataPath)!);

        if (drive.AvailableFreeSpace < 100 * 1024 * 1024)
        {
            _warnings.Add("Low disk space available (less than 100 MB)");
        }
    }

    private void ValidateMemory()
    {
        var process = Process.GetCurrentProcess();
        var availableMemory = GC.GetTotalMemory(false);

        if (availableMemory > 512 * 1024 * 1024)
        {
            _warnings.Add("High memory usage detected");
        }
    }

    private void ValidateSystemRequirements()
    {
        var dotnetVersion = Environment.Version;
        if (dotnetVersion.Major < 8)
        {
            _errors.Add(".NET 8.0 or later is required");
        }

        if (Environment.ProcessorCount < 2)
        {
            _warnings.Add("Single-core processor detected - performance may be limited");
        }
    }

    public string GetValidationReport()
    {
        var report = new StringBuilder();

        if (_errors.Count > 0)
        {
            report.AppendLine("ERRORS:");
            foreach (var error in _errors)
            {
                report.AppendLine($"  ❌ {error}");
            }
        }

        if (_warnings.Count > 0)
        {
            if (report.Length > 0) report.AppendLine();
            report.AppendLine("WARNINGS:");
            foreach (var warning in _warnings)
            {
                report.AppendLine($"  ⚠️ {warning}");
            }
        }

        if (_errors.Count == 0 && _warnings.Count == 0)
        {
            report.AppendLine("✅ All system requirements validated successfully");
        }

        return report.ToString();
    }
}