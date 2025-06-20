using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;
using System.Collections.Concurrent;
using System.Windows;

namespace TaskMaster;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentDictionary<string, bool> _existingTaskNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _existingRoutineNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _validationDebounceTimer;
    private readonly DispatcherTimer _categoryUpdateDebounceTimer;
    private readonly object _categoryUpdateLock = new object();
    private DateTime _lastRoutineResetDate = DateTime.MinValue;
    private bool _disposed = false;
    private bool _categoryUpdatePending = false;

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

    private string _newTaskName = string.Empty;
    public string NewTaskName
    {
        get => _newTaskName;
        set
        {
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 100) trimmedValue = trimmedValue.Substring(0, 100);
            
            if (_newTaskName != trimmedValue)
            {
                _newTaskName = trimmedValue;
                OnPropertyChanged();
                
                _validationDebounceTimer.Stop();
                _validationDebounceTimer.Start();
            }
        }
    }

    private string _newTaskDescription = string.Empty;
    public string NewTaskDescription
    {
        get => _newTaskDescription;
        set
        {
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 500) trimmedValue = trimmedValue.Substring(0, 500);
            
            if (_newTaskDescription != trimmedValue)
            {
                _newTaskDescription = trimmedValue;
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
            var clampedValue = Math.Max(1, Math.Min(10, value));
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
            if (value.HasValue && value.Value.Year > 2100)
                value = new DateTime(2100, 12, 31);
            
            if (_newTaskDueDate != value)
            {
                _newTaskDueDate = value;
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
            var trimmedValue = value?.Trim() ?? "General";
            if (trimmedValue.Length > 50) trimmedValue = trimmedValue.Substring(0, 50);
            if (string.IsNullOrWhiteSpace(trimmedValue)) trimmedValue = "General";
            
            if (_newTaskCategory != trimmedValue)
            {
                _newTaskCategory = trimmedValue;
                OnPropertyChanged();
                
                _categoryUpdatePending = true;
                _categoryUpdateDebounceTimer.Stop();
                _categoryUpdateDebounceTimer.Start();
            }
        }
    }

    private string _newRoutineName = string.Empty;
    public string NewRoutineName
    {
        get => _newRoutineName;
        set
        {
            var trimmedValue = value?.Trim() ?? string.Empty;
            if (trimmedValue.Length > 100) trimmedValue = trimmedValue.Substring(0, 100);
            
            if (_newRoutineName != trimmedValue)
            {
                _newRoutineName = trimmedValue;
                OnPropertyChanged();
                
                _validationDebounceTimer.Stop();
                _validationDebounceTimer.Start();
            }
        }
    }

    private string _selectedSort = "Priority";
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (_selectedSort != value)
            {
                _selectedSort = value ?? "Priority";
                OnPropertyChanged();
                RefreshDisplayedTasks();
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
                _selectedCategory = value ?? "All";
                OnPropertyChanged();
                RefreshDisplayedTasks();
            }
        }
    }

    public List<string> SortOptions { get; }
    
    public bool HasTasks => Tasks.Count > 0;
    public bool HasRoutines => Routines.Count > 0;
    public bool HasDisplayedTasks => DisplayedTasks.Count > 0;
    
    public bool CanAddTaskNow => !string.IsNullOrWhiteSpace(NewTaskName) && !_existingTaskNames.ContainsKey(NewTaskName);
    public bool CanAddRoutineNow => !string.IsNullOrWhiteSpace(NewRoutineName) && !_existingRoutineNames.ContainsKey(NewRoutineName);
    
    public string TaskNameValidationMessage =>
        string.IsNullOrWhiteSpace(NewTaskName) ? "Task name is required" :
        _existingTaskNames.ContainsKey(NewTaskName) ? "A task with this name already exists" :
        string.Empty;
        
    public string RoutineNameValidationMessage =>
        string.IsNullOrWhiteSpace(NewRoutineName) ? "Routine name is required" :
        _existingRoutineNames.ContainsKey(NewRoutineName) ? "A routine with this name already exists" :
        string.Empty;

    public MainViewModel()
    {
        try
        {
            _dataService = new DataService();
            _notificationService = new NotificationService();

            Tasks = new ObservableCollection<TaskItem>();
            Routines = new ObservableCollection<RoutineItem>();
            DisplayedTasks = new ObservableCollection<TaskItem>();
            AllCategories = new ObservableCollection<string> { "All", "Work", "Personal", "Shopping", "Health", "General" };

            SortOptions = new List<string> { "Priority", "Due Date", "Name", "Category", "Created Date" };

            _validationDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _validationDebounceTimer.Tick += OnValidationDebounceTimerTick;

            _categoryUpdateDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _categoryUpdateDebounceTimer.Tick += OnCategoryUpdateDebounceTimerTick;

            AddTaskCommand = new RelayCommand(AddTask, () => CanAddTaskNow);
            DeleteTaskCommand = new RelayCommand<TaskItem>(DeleteTask);
            AddRoutineCommand = new RelayCommand(AddRoutine, () => CanAddRoutineNow);
            DeleteRoutineCommand = new RelayCommand<RoutineItem>(DeleteRoutine);
            ResetRoutinesCommand = new RelayCommand(ResetRoutines);
            ToggleTaskCompletionCommand = new RelayCommand<TaskItem>(ToggleTaskCompletion);
            ToggleRoutineCompletionCommand = new RelayCommand<RoutineItem>(ToggleRoutineCompletion);

            LoadData();
            UpdateCategoriesList();
            RefreshDisplayedTasks();
            UpdateExistingNames();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _lastRoutineResetDate = DateTime.Today;
        }
        catch (Exception ex)
        {
            Dispose();
            
            System.Windows.MessageBox.Show(
                $"Failed to initialize TaskMaster: {ex.Message}",
                "Initialization Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
                
            throw;
        }
    }

    private void LoadData()
    {
        try
        {
            var tasks = _dataService.LoadTasks();
            foreach (var task in tasks)
            {
                Tasks.Add(task);
            }

            var routines = _dataService.LoadRoutines();
            foreach (var routine in routines)
            {
                Routines.Add(routine);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error loading data: {ex.Message}",
                "Data Load Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private readonly HashSet<string> _categoriesSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _predefinedCategories = { "Work", "Personal", "Shopping", "Health", "General" };

    private void UpdateCategoriesList()
    {
        lock (_categoryUpdateLock)
        {
            try
            {
                _categoriesSet.Clear();
                _categoriesSet.Add("All");
                
                foreach (var category in _predefinedCategories)
                {
                    _categoriesSet.Add(category);
                }
                
                TaskItem[] tasksCopy;
                try
                {
                    tasksCopy = Tasks.ToArray();
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                
                foreach (var task in tasksCopy)
                {
                    if (!string.IsNullOrWhiteSpace(task.Category) && task.Category != "All")
                    {
                        _categoriesSet.Add(task.Category);
                    }
                }
                
                var sortedCategories = new List<string>(_categoriesSet.Count);
                sortedCategories.Add("All");
                
                var otherCategories = new List<string>(_categoriesSet.Count - 1);
                foreach (var category in _categoriesSet)
                {
                    if (category != "All")
                    {
                        otherCategories.Add(category);
                    }
                }
                
                otherCategories.Sort(StringComparer.OrdinalIgnoreCase);
                sortedCategories.AddRange(otherCategories);

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    bool needsUpdate = AllCategories.Count != sortedCategories.Count;
                    
                    if (!needsUpdate)
                    {
                        for (int i = 0; i < AllCategories.Count; i++)
                        {
                            if (AllCategories[i] != sortedCategories[i])
                            {
                                needsUpdate = true;
                                break;
                            }
                        }
                    }
                    
                    if (needsUpdate)
                    {
                        AllCategories.Clear();
                        foreach (var category in sortedCategories)
                        {
                            AllCategories.Add(category);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating categories list: {ex.Message}");
            }
        }
    }

    private void UpdateExistingNames()
    {
        try
        {
            _existingTaskNames.Clear();
            _existingRoutineNames.Clear();
            
            TaskItem[] tasksCopy;
            try
            {
                tasksCopy = Tasks.ToArray();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            
            foreach (var task in tasksCopy)
            {
                if (!string.IsNullOrWhiteSpace(task.Name))
                {
                    _existingTaskNames.TryAdd(task.Name, true);
                }
            }
            
            RoutineItem[] routinesCopy;
            try
            {
                routinesCopy = Routines.ToArray();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            
            foreach (var routine in routinesCopy)
            {
                if (!string.IsNullOrWhiteSpace(routine.Name))
                {
                    _existingRoutineNames.TryAdd(routine.Name, true);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating existing names: {ex.Message}");
        }
    }

    private void RefreshDisplayedTasks()
    {
        try
        {
            DisplayedTasks.Clear();

            TaskItem[] tasksCopy;
            try
            {
                tasksCopy = Tasks.ToArray();
            }
            catch (InvalidOperationException)
            {
                OnPropertyChanged(nameof(HasDisplayedTasks));
                return;
            }

            var filteredTasks = tasksCopy.AsEnumerable();

            if (SelectedCategory != "All")
            {
                filteredTasks = filteredTasks.Where(t => 
                    string.Equals(t.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            filteredTasks = SelectedSort switch
            {
                "Priority" => filteredTasks.OrderByDescending(t => t.Priority).ThenBy(t => t.Name),
                "Due Date" => filteredTasks.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Name),
                "Name" => filteredTasks.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
                "Category" => filteredTasks.OrderBy(t => t.Category).ThenBy(t => t.Name),
                "Created Date" => filteredTasks.OrderByDescending(t => t.CreatedDate).ThenBy(t => t.Name),
                _ => filteredTasks.OrderBy(t => t.Name)
            };

            foreach (var task in filteredTasks)
            {
                DisplayedTasks.Add(task);
            }

            OnPropertyChanged(nameof(HasDisplayedTasks));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing displayed tasks: {ex.Message}");
        }
    }

    private void AddTask()
    {
        try
        {
            if (!CanAddTaskNow) return;

            var newTask = new TaskItem
            {
                Name = NewTaskName,
                Description = NewTaskDescription,
                Priority = NewTaskPriority,
                DueDate = NewTaskDueDate,
                Category = NewTaskCategory
            };

            Tasks.Add(newTask);
            _existingTaskNames.TryAdd(newTask.Name, true);
            
            _dataService.SaveTasks(Tasks);
            UpdateCategoriesList();
            RefreshDisplayedTasks();

            ClearTaskForm();
            OnPropertyChanged(nameof(HasTasks));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error adding task: {ex.Message}",
                "Add Task Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void ClearTaskForm()
    {
        NewTaskName = string.Empty;
        NewTaskDescription = string.Empty;
        NewTaskPriority = 5;
        NewTaskDueDate = null;
        NewTaskCategory = "General";
    }

    private void DeleteTask(TaskItem? task)
    {
        try
        {
            if (task == null) return;

            _existingTaskNames.TryRemove(task.Name, out _);
            Tasks.Remove(task);
            
            _dataService.SaveTasks(Tasks);
            UpdateCategoriesList();
            RefreshDisplayedTasks();
            
            OnPropertyChanged(nameof(HasTasks));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error deleting task: {ex.Message}",
                "Delete Task Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void AddRoutine()
    {
        try
        {
            if (!CanAddRoutineNow) return;

            var newRoutine = new RoutineItem
            {
                Name = NewRoutineName
            };

            Routines.Add(newRoutine);
            _existingRoutineNames.TryAdd(newRoutine.Name, true);
            
            _dataService.SaveRoutines(Routines);

            NewRoutineName = string.Empty;
            OnPropertyChanged(nameof(HasRoutines));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error adding routine: {ex.Message}",
                "Add Routine Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void DeleteRoutine(RoutineItem? routine)
    {
        try
        {
            if (routine == null) return;

            _existingRoutineNames.TryRemove(routine.Name, out _);
            Routines.Remove(routine);
            
            _dataService.SaveRoutines(Routines);
            OnPropertyChanged(nameof(HasRoutines));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error deleting routine: {ex.Message}",
                "Delete Routine Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
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
            _lastRoutineResetDate = DateTime.Today;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Error resetting routines: {ex.Message}",
                "Reset Routines Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void ToggleTaskCompletion(TaskItem? task)
    {
        try
        {
            if (task == null) return;

            task.IsCompleted = !task.IsCompleted;
            _dataService.SaveTasks(Tasks);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling task completion: {ex.Message}");
        }
    }

    private void ToggleRoutineCompletion(RoutineItem? routine)
    {
        try
        {
            if (routine == null) return;

            routine.IsCompleted = !routine.IsCompleted;
            routine.LastCompletedDate = routine.IsCompleted ? DateTime.Now : DateTime.MinValue;
            
            _dataService.SaveRoutines(Routines);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling routine completion: {ex.Message}");
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            CheckForDueTasks();
            CheckForNewDay();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Timer tick error: {ex.Message}");
        }
    }

    private void CheckForDueTasks()
    {
        try
        {
            TaskItem[] tasksCopy;
            try
            {
                tasksCopy = Tasks.ToArray();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var tasksNeedingNotification = tasksCopy.Where(t => t.IsDueForNotification()).ToArray();

            foreach (var task in tasksNeedingNotification)
            {
                if (_notificationService.ShowTaskDueNotification(task))
                {
                    _dataService.SaveTasks(Tasks);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking due tasks: {ex.Message}");
        }
    }

    private void CheckForNewDay()
    {
        try
        {
            var today = DateTime.Today;
            
            if (_lastRoutineResetDate < today)
            {
                ResetRoutines();
                _lastRoutineResetDate = today;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking for new day: {ex.Message}");
        }
    }

    private void OnValidationDebounceTimerTick(object? sender, EventArgs e)
    {
        _validationDebounceTimer.Stop();
        OnPropertyChanged(nameof(CanAddTaskNow));
        OnPropertyChanged(nameof(TaskNameValidationMessage));
        OnPropertyChanged(nameof(CanAddRoutineNow));
        OnPropertyChanged(nameof(RoutineNameValidationMessage));
    }

    private void OnCategoryUpdateDebounceTimerTick(object? sender, EventArgs e)
    {
        _categoryUpdateDebounceTimer.Stop();
        if (_categoryUpdatePending)
        {
            _categoryUpdatePending = false;
            UpdateCategoriesList();
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
            
            if (_validationDebounceTimer != null)
            {
                _validationDebounceTimer.Stop();
                _validationDebounceTimer.Tick -= OnValidationDebounceTimerTick;
            }
            
            if (_categoryUpdateDebounceTimer != null)
            {
                _categoryUpdateDebounceTimer.Stop();
                _categoryUpdateDebounceTimer.Tick -= OnCategoryUpdateDebounceTimerTick;
            }
            
            _notificationService?.ClearNotificationHistory();
            _dataService?.Dispose();
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