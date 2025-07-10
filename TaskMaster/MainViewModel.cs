using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Windows;

namespace TaskMaster;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly AdvancedLoggingService _logger;
    private readonly ConfigurationService _configService;
    private readonly AdvancedSearchService _searchService;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentDictionary<string, bool> _existingTaskNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _existingRoutineNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed = false;

    public ObservableCollection<TaskItem> Tasks { get; }
    public ObservableCollection<RoutineItem> Routines { get; }
    public ObservableCollection<TaskItem> DisplayedTasks { get; }
    public ObservableCollection<string> AllCategories { get; }

    public ICommand AddTaskCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand AddRoutineCommand { get; }
    public ICommand DeleteRoutineCommand { get; }
    public ICommand ResetRoutinesCommand { get; }
    public ICommand ToggleTaskCompletionCommand { get; }
    public ICommand ToggleRoutineCompletionCommand { get; }
    public ICommand ClearAllTasksCommand { get; }
    public ICommand SearchTasksCommand { get; }

    private string _newTaskName = string.Empty;
    public string NewTaskName
    {
        get => _newTaskName;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeTaskName(value);
            if (_newTaskName != sanitizedValue)
            {
                _newTaskName = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    private string _newTaskDescription = string.Empty;
    public string NewTaskDescription
    {
        get => _newTaskDescription;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeDescription(value);
            if (_newTaskDescription != sanitizedValue)
            {
                _newTaskDescription = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    private int _newTaskPriority = 5;
    public int NewTaskPriority
    {
        get => _newTaskPriority;
        set
        {
            var clampedValue = ValidationUtils.ClampPriority(value);
            if (_newTaskPriority != clampedValue)
            {
                _newTaskPriority = clampedValue;
                OnPropertyChanged();
            }
        }
    }

    private DateTime? _newTaskDueDate;
    public DateTime? NewTaskDueDate
    {
        get => _newTaskDueDate;
        set
        {
            var validatedValue = ValidationUtils.ValidateAndClampDate(value);
            if (_newTaskDueDate != validatedValue)
            {
                _newTaskDueDate = validatedValue;
                OnPropertyChanged();
            }
        }
    }

    private string _newTaskCategory = "General";
    public string NewTaskCategory
    {
        get => _newTaskCategory;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeCategory(value);
            if (_newTaskCategory != sanitizedValue)
            {
                _newTaskCategory = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    private string _newRoutineName = string.Empty;
    public string NewRoutineName
    {
        get => _newRoutineName;
        set
        {
            var sanitizedValue = ValidationUtils.ValidateAndSanitizeTaskName(value);
            if (_newRoutineName != sanitizedValue)
            {
                _newRoutineName = sanitizedValue;
                OnPropertyChanged();
            }
        }
    }

    private string _selectedCategory = "All";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory != value)
            {
                _selectedCategory = value;
                OnPropertyChanged();
                RefreshDisplayedTasks();
            }
        }
    }

    public bool CanAddTaskNow => !string.IsNullOrWhiteSpace(NewTaskName) && !_existingTaskNames.ContainsKey(NewTaskName);
    public bool CanAddRoutineNow => !string.IsNullOrWhiteSpace(NewRoutineName) && !_existingRoutineNames.ContainsKey(NewRoutineName);
    public bool HasTasks => Tasks.Any();

    public MainViewModel()
    {
        try
        {
            _logger = new AdvancedLoggingService();
            _configService = new ConfigurationService();
            _dataService = new DataService();
            _notificationService = new NotificationService();
            _searchService = new AdvancedSearchService();

            _logger.LogInfo("MainViewModel initialization started");

            Tasks = new ObservableCollection<TaskItem>();
            Routines = new ObservableCollection<RoutineItem>();
            DisplayedTasks = new ObservableCollection<TaskItem>();
            AllCategories = new ObservableCollection<string> { "All", "Work", "Personal", "Shopping", "Health", "General" };

            AddTaskCommand = new RelayCommand(AddTask, () => CanAddTaskNow);
            DeleteTaskCommand = new RelayCommand<TaskItem>(DeleteTask);
            AddRoutineCommand = new RelayCommand(AddRoutine, () => CanAddRoutineNow);
            DeleteRoutineCommand = new RelayCommand<RoutineItem>(DeleteRoutine);
            ResetRoutinesCommand = new RelayCommand(ResetRoutines);
            ToggleTaskCompletionCommand = new RelayCommand<TaskItem>(ToggleTaskCompletion);
            ToggleRoutineCompletionCommand = new RelayCommand<RoutineItem>(ToggleRoutineCompletion);
            ClearAllTasksCommand = new RelayCommand(ClearAllTasks, () => HasTasks);
            SearchTasksCommand = new RelayCommand(PerformAdvancedSearch);

            LoadData();
            UpdateCategoriesList();
            RefreshDisplayedTasks();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _logger.LogInfo("MainViewModel initialization completed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical($"MainViewModel initialization failed: {ex.Message}", ex);
            throw;
        }
    }

    private void LoadData()
    {
        try
        {
            var tasks = _dataService.LoadTasks().ToList();
            var routines = _dataService.LoadRoutines().ToList();

            foreach (var task in tasks)
            {
                Tasks.Add(task);
                _existingTaskNames.TryAdd(task.Name, true);
            }

            foreach (var routine in routines)
            {
                Routines.Add(routine);
                _existingRoutineNames.TryAdd(routine.Name, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading data: {ex.Message}", ex);
            MessageBox.Show($"Error loading data: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddTask()
    {
        try
        {
            var newTask = new TaskItem
            {
                Name = NewTaskName,
                Description = NewTaskDescription,
                Priority = NewTaskPriority,
                DueDate = NewTaskDueDate,
                Category = NewTaskCategory,
                CreatedDate = TimezoneService.Instance.Now
            };

            Tasks.Add(newTask);
            _existingTaskNames.TryAdd(newTask.Name, true);

            _dataService.SaveTasks(Tasks);
            UpdateCategoriesList();
            RefreshDisplayedTasks();

            NewTaskName = string.Empty;
            NewTaskDescription = string.Empty;
            NewTaskPriority = 5;
            NewTaskDueDate = null;
            NewTaskCategory = "General";

            _logger.LogInfo($"Task added: {newTask.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error adding task: {ex.Message}", ex);
            MessageBox.Show($"Error adding task: {ex.Message}", "Add Task Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteTask(TaskItem task)
    {
        if (task == null) return;

        try
        {
            Tasks.Remove(task);
            _existingTaskNames.TryRemove(task.Name, out _);

            _dataService.SaveTasks(Tasks);
            RefreshDisplayedTasks();

            _logger.LogInfo($"Task deleted: {task.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error deleting task: {ex.Message}", ex);
            MessageBox.Show($"Error deleting task: {ex.Message}", "Delete Task Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddRoutine()
    {
        try
        {
            var newRoutine = new RoutineItem
            {
                Name = NewRoutineName
            };

            Routines.Add(newRoutine);
            _existingRoutineNames.TryAdd(newRoutine.Name, true);

            _dataService.SaveRoutines(Routines);

            NewRoutineName = string.Empty;

            _logger.LogInfo($"Routine added: {newRoutine.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error adding routine: {ex.Message}", ex);
            MessageBox.Show($"Error adding routine: {ex.Message}", "Add Routine Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteRoutine(RoutineItem routine)
    {
        if (routine == null) return;

        try
        {
            Routines.Remove(routine);
            _existingRoutineNames.TryRemove(routine.Name, out _);

            _dataService.SaveRoutines(Routines);

            _logger.LogInfo($"Routine deleted: {routine.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error deleting routine: {ex.Message}", ex);
            MessageBox.Show($"Error deleting routine: {ex.Message}", "Delete Routine Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetRoutines()
    {
        try
        {
            foreach (var routine in Routines)
            {
                routine.IsCompleted = false;
                routine.LastCompletedDate = DateTime.MinValue;
            }

            _dataService.SaveRoutines(Routines);

            _logger.LogInfo("All routines reset");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error resetting routines: {ex.Message}", ex);
            MessageBox.Show($"Error resetting routines: {ex.Message}", "Reset Routines Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleTaskCompletion(TaskItem task)
    {
        if (task == null) return;

        try
        {
            task.IsCompleted = !task.IsCompleted;
            _dataService.SaveTasks(Tasks);

            _logger.LogInfo($"Task completion toggled: {task.Name} - {task.IsCompleted}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error toggling task completion: {ex.Message}", ex);
            MessageBox.Show($"Error toggling task completion: {ex.Message}", "Toggle Task Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleRoutineCompletion(RoutineItem routine)
    {
        if (routine == null) return;

        try
        {
            routine.IsCompleted = !routine.IsCompleted;
            if (routine.IsCompleted)
            {
                routine.LastCompletedDate = TimezoneService.Instance.Now;
            }

            _dataService.SaveRoutines(Routines);

            _logger.LogInfo($"Routine completion toggled: {routine.Name} - {routine.IsCompleted}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error toggling routine completion: {ex.Message}", ex);
            MessageBox.Show($"Error toggling routine completion: {ex.Message}", "Toggle Routine Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearAllTasks()
    {
        try
        {
            if (MessageBox.Show("Are you sure you want to clear all tasks?", "Clear All Tasks", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Tasks.Clear();
                _existingTaskNames.Clear();
                _dataService.SaveTasks(Tasks);
                RefreshDisplayedTasks();

                _logger.LogInfo("All tasks cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error clearing tasks: {ex.Message}", ex);
            MessageBox.Show($"Error clearing tasks: {ex.Message}", "Clear Tasks Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateCategoriesList()
    {
        try
        {
            var categories = new HashSet<string> { "All", "Work", "Personal", "Shopping", "Health", "General" };
            
            foreach (var task in Tasks)
            {
                if (!string.IsNullOrEmpty(task.Category))
                {
                    categories.Add(task.Category);
                }
            }

            AllCategories.Clear();
            foreach (var category in categories.OrderBy(c => c))
            {
                AllCategories.Add(category);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error updating categories: {ex.Message}", ex);
        }
    }

    private void RefreshDisplayedTasks()
    {
        try
        {
            DisplayedTasks.Clear();

            var filteredTasks = SelectedCategory == "All" 
                ? Tasks 
                : Tasks.Where(t => t.Category == SelectedCategory);

            foreach (var task in filteredTasks.OrderBy(t => t.Priority).ThenBy(t => t.DueDate))
            {
                DisplayedTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error refreshing displayed tasks: {ex.Message}", ex);
        }
    }

    private void PerformAdvancedSearch()
    {
        try
        {
            _logger.LogInfo("Advanced search functionality available");
            MessageBox.Show("Advanced search functionality is available.", "Search", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error performing search: {ex.Message}", ex);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var now = TimezoneService.Instance.Now;
            
            foreach (var task in Tasks.Where(t => !t.IsCompleted && t.DueDate.HasValue))
            {
                if (task.IsDueForNotification())
                {
                    _notificationService.ShowTaskDueNotification(task);
                }
            }

            foreach (var routine in Routines.Where(r => r.IsCompleted))
            {
                if (routine.LastCompletedDate.Date < now.Date)
                {
                    routine.IsCompleted = false;
                    routine.LastCompletedDate = DateTime.MinValue;
                }
            }

            _dataService.SaveRoutines(Routines);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in timer tick: {ex.Message}", ex);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
            }

            _dataService?.Dispose();
            _logger?.Dispose();
            _configService?.Dispose();
            _searchService?.Dispose();

            _disposed = true;
        }
    }

    ~MainViewModel()
    {
        Dispose(false);
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

    public void Execute(object? parameter) => _execute((T?)parameter);
}