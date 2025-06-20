using System.ComponentModel;
using System.Globalization;

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
    private DateTime _createdDate = DateTime.Now;
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
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 100) trimmedValue = trimmedValue.Substring(0, 100);
            
            if (_name != trimmedValue)
            {
                _name = trimmedValue;
                OnPropertyChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 500) trimmedValue = trimmedValue.Substring(0, 500);
            
            if (_description != trimmedValue)
            {
                _description = trimmedValue;
                OnPropertyChanged();
            }
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            var clampedValue = Math.Max(1, Math.Min(10, value));
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
            if (value.HasValue && value.Value.Year > 2100)
                value = new DateTime(2100, 12, 31);
            
            if (_dueDate != value)
            {
                _dueDate = value;
                OnPropertyChanged();
            }
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            var trimmedValue = value?.Trim() ?? "General";
            if (trimmedValue.Length > 50) trimmedValue = trimmedValue.Substring(0, 50);
            if (string.IsNullOrWhiteSpace(trimmedValue)) trimmedValue = "General";
            
            if (_category != trimmedValue)
            {
                _category = trimmedValue;
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
        
        var now = DateTime.Now;
        var timeSinceLastNotification = LastNotificationTime.HasValue 
            ? now - LastNotificationTime.Value 
            : TimeSpan.MaxValue;
        
        return DueDate.Value.Date <= now.Date && timeSinceLastNotification >= TimeSpan.FromHours(1);
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
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 100) trimmedValue = trimmedValue.Substring(0, 100);
            
            if (_name != trimmedValue)
            {
                _name = trimmedValue;
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