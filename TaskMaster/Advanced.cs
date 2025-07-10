using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace TaskMaster;

public class AdvancedSearchService : IDisposable
{
    private readonly ConcurrentDictionary<string, SearchResult> _searchCache;
    private readonly Timer _cacheCleanupTimer;
    private readonly ReaderWriterLockSlim _cacheLock;
    private volatile bool _disposed = false;

    private const int MAX_CACHE_SIZE = 1000;
    private const int CACHE_CLEANUP_INTERVAL_MINUTES = 5;
    private const int CACHE_EXPIRY_MINUTES = 30;

    public AdvancedSearchService()
    {
        _searchCache = new ConcurrentDictionary<string, SearchResult>();
        _cacheLock = new ReaderWriterLockSlim();
        
        _cacheCleanupTimer = new Timer(CleanupExpiredCache, null, 
            TimeSpan.FromMinutes(CACHE_CLEANUP_INTERVAL_MINUTES), 
            TimeSpan.FromMinutes(CACHE_CLEANUP_INTERVAL_MINUTES));
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, IEnumerable<TaskItem> tasks, IEnumerable<RoutineItem> routines)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedSearchService));
        
        var cacheKey = GenerateCacheKey(query);
        
        if (_searchCache.TryGetValue(cacheKey, out var cachedResult) && !cachedResult.IsExpired)
        {
            return cachedResult;
        }

        var result = await PerformSearchAsync(query, tasks, routines);
        
        CacheSearchResult(cacheKey, result);
        
        return result;
    }

    private async Task<SearchResult> PerformSearchAsync(SearchQuery query, IEnumerable<TaskItem> tasks, IEnumerable<RoutineItem> routines)
    {
        var result = new SearchResult
        {
            Query = query,
            SearchTime = DateTime.Now,
            Success = true
        };

        try
        {
            var tasksList = tasks.ToList();
            var routinesList = routines.ToList();

            if (query.SearchTasks)
            {
                result.Tasks = await SearchTasksAsync(query, tasksList);
            }

            if (query.SearchRoutines)
            {
                result.Routines = await SearchRoutinesAsync(query, routinesList);
            }

            result.Tasks = ApplyTaskSorting(result.Tasks, query.SortBy, query.SortDescending);
            result.Routines = ApplyRoutineSorting(result.Routines, query.SortBy, query.SortDescending);

            if (query.PageSize > 0)
            {
                var taskOffset = query.PageIndex * query.PageSize;
                var routineOffset = query.PageIndex * query.PageSize;
                
                result.Tasks = result.Tasks.Skip(taskOffset).Take(query.PageSize).ToList();
                result.Routines = result.Routines.Skip(routineOffset).Take(query.PageSize).ToList();
            }

            result.TotalTasksFound = result.Tasks.Count;
            result.TotalRoutinesFound = result.Routines.Count;
            result.TotalItemsFound = result.TotalTasksFound + result.TotalRoutinesFound;

            result.Statistics = GenerateSearchStatistics(result.Tasks, result.Routines, query);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<List<TaskItem>> SearchTasksAsync(SearchQuery query, List<TaskItem> tasks)
    {
        return await Task.Run(() =>
        {
            var filteredTasks = tasks.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                filteredTasks = ApplyTextSearch(filteredTasks, query.SearchText, query.SearchOptions);
            }

            if (query.Categories?.Any() == true)
            {
                filteredTasks = filteredTasks.Where(t => query.Categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase));
            }

            if (query.MinPriority.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.Priority >= query.MinPriority.Value);
            }
            
            if (query.MaxPriority.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.Priority <= query.MaxPriority.Value);
            }

            if (query.CreatedAfter.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.CreatedDate >= query.CreatedAfter.Value);
            }
            
            if (query.CreatedBefore.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.CreatedDate <= query.CreatedBefore.Value);
            }

            if (query.DueAfter.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.DueDate >= query.DueAfter.Value);
            }
            
            if (query.DueBefore.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.DueDate <= query.DueBefore.Value);
            }

            if (query.CompletionStatus.HasValue)
            {
                filteredTasks = filteredTasks.Where(t => t.IsCompleted == query.CompletionStatus.Value);
            }

            if (query.ShowOverdueOnly)
            {
                var today = TimezoneService.Instance.Today;
                filteredTasks = filteredTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date < today && !t.IsCompleted);
            }

            if (query.ShowDueTodayOnly)
            {
                var today = TimezoneService.Instance.Today;
                filteredTasks = filteredTasks.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date == today);
            }

            if (query.HasDueDate.HasValue)
            {
                filteredTasks = query.HasDueDate.Value 
                    ? filteredTasks.Where(t => t.DueDate.HasValue)
                    : filteredTasks.Where(t => !t.DueDate.HasValue);
            }

            if (query.HasDescription.HasValue)
            {
                filteredTasks = query.HasDescription.Value
                    ? filteredTasks.Where(t => !string.IsNullOrWhiteSpace(t.Description))
                    : filteredTasks.Where(t => string.IsNullOrWhiteSpace(t.Description));
            }

            if (query.CustomFilter != null)
            {
                filteredTasks = filteredTasks.Where(query.CustomFilter.Compile());
            }

            return filteredTasks.ToList();
        });
    }

    private async Task<List<RoutineItem>> SearchRoutinesAsync(SearchQuery query, List<RoutineItem> routines)
    {
        return await Task.Run(() =>
        {
            var filteredRoutines = routines.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                filteredRoutines = ApplyTextSearchRoutines(filteredRoutines, query.SearchText, query.SearchOptions);
            }

            if (query.CompletionStatus.HasValue)
            {
                filteredRoutines = filteredRoutines.Where(r => r.IsCompleted == query.CompletionStatus.Value);
            }

            if (query.ShowCompletedTodayOnly)
            {
                var today = TimezoneService.Instance.Today;
                filteredRoutines = filteredRoutines.Where(r => r.IsCompleted && r.LastCompletedDate.Date == today);
            }

            if (query.CustomRoutineFilter != null)
            {
                filteredRoutines = filteredRoutines.Where(query.CustomRoutineFilter.Compile());
            }

            return filteredRoutines.ToList();
        });
    }

    private IEnumerable<TaskItem> ApplyTextSearch(IEnumerable<TaskItem> tasks, string searchText, SearchOptions options)
    {
        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        
        if (options.UseRegex)
        {
            try
            {
                var regexOptions = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(searchText, regexOptions);
                
                return tasks.Where(t => 
                    regex.IsMatch(t.Name) || 
                    regex.IsMatch(t.Description) || 
                    regex.IsMatch(t.Category));
            }
            catch (ArgumentException)
            {
                options.UseRegex = false;
            }
        }

        if (options.WholeWordOnly)
        {
            return tasks.Where(t => 
                ContainsWholeWord(t.Name, searchText, comparison) ||
                ContainsWholeWord(t.Description, searchText, comparison) ||
                ContainsWholeWord(t.Category, searchText, comparison));
        }

        if (options.ExactMatch)
        {
            return tasks.Where(t => 
                t.Name.Equals(searchText, comparison) ||
                t.Description.Equals(searchText, comparison) ||
                t.Category.Equals(searchText, comparison));
        }

        if (options.FuzzySearch)
        {
            return tasks.Where(t => 
                CalculateLevenshteinDistance(t.Name, searchText) <= options.FuzzyThreshold ||
                CalculateLevenshteinDistance(t.Description, searchText) <= options.FuzzyThreshold ||
                CalculateLevenshteinDistance(t.Category, searchText) <= options.FuzzyThreshold);
        }

        return tasks.Where(t => 
            t.Name.Contains(searchText, comparison) ||
            t.Description.Contains(searchText, comparison) ||
            t.Category.Contains(searchText, comparison));
    }

    private IEnumerable<RoutineItem> ApplyTextSearchRoutines(IEnumerable<RoutineItem> routines, string searchText, SearchOptions options)
    {
        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        
        if (options.UseRegex)
        {
            try
            {
                var regexOptions = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(searchText, regexOptions);
                
                return routines.Where(r => regex.IsMatch(r.Name));
            }
            catch (ArgumentException)
            {
                options.UseRegex = false;
            }
        }

        if (options.WholeWordOnly)
        {
            return routines.Where(r => ContainsWholeWord(r.Name, searchText, comparison));
        }

        if (options.ExactMatch)
        {
            return routines.Where(r => r.Name.Equals(searchText, comparison));
        }

        if (options.FuzzySearch)
        {
            return routines.Where(r => CalculateLevenshteinDistance(r.Name, searchText) <= options.FuzzyThreshold);
        }

        return routines.Where(r => r.Name.Contains(searchText, comparison));
    }

    private bool ContainsWholeWord(string text, string word, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
            return false;

        var words = text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.Equals(word, comparison));
    }

    private int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return source.Length;

        var sourceLength = source.Length;
        var targetLength = target.Length;
        var matrix = new int[sourceLength + 1, targetLength + 1];

        for (int i = 0; i <= sourceLength; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= targetLength; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= sourceLength; i++)
        {
            for (int j = 1; j <= targetLength; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[sourceLength, targetLength];
    }

    private List<TaskItem> ApplyTaskSorting(List<TaskItem> tasks, string sortBy, bool descending)
    {
        if (string.IsNullOrEmpty(sortBy))
            return tasks;

        IEnumerable<TaskItem> sortedTasks = sortBy.ToLower() switch
        {
            "name" => descending ? tasks.OrderByDescending(t => t.Name, StringComparer.OrdinalIgnoreCase) : tasks.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            "priority" => descending ? tasks.OrderByDescending(t => t.Priority) : tasks.OrderBy(t => t.Priority),
            "duedate" => descending ? tasks.OrderByDescending(t => t.DueDate ?? DateTime.MaxValue) : tasks.OrderBy(t => t.DueDate ?? DateTime.MaxValue),
            "category" => descending ? tasks.OrderByDescending(t => t.Category, StringComparer.OrdinalIgnoreCase) : tasks.OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase),
            "createddate" => descending ? tasks.OrderByDescending(t => t.CreatedDate) : tasks.OrderBy(t => t.CreatedDate),
            "completed" => descending ? tasks.OrderByDescending(t => t.IsCompleted) : tasks.OrderBy(t => t.IsCompleted),
            _ => tasks
        };

        return sortedTasks.ToList();
    }

    private List<RoutineItem> ApplyRoutineSorting(List<RoutineItem> routines, string sortBy, bool descending)
    {
        if (string.IsNullOrEmpty(sortBy))
            return routines;

        IEnumerable<RoutineItem> sortedRoutines = sortBy.ToLower() switch
        {
            "name" => descending ? routines.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : routines.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "completed" => descending ? routines.OrderByDescending(r => r.IsCompleted) : routines.OrderBy(r => r.IsCompleted),
            "lastcompleted" => descending ? routines.OrderByDescending(r => r.LastCompletedDate) : routines.OrderBy(r => r.LastCompletedDate),
            _ => routines
        };

        return sortedRoutines.ToList();
    }

    private SearchStatistics GenerateSearchStatistics(List<TaskItem> tasks, List<RoutineItem> routines, SearchQuery query)
    {
        var stats = new SearchStatistics();

        if (tasks.Any())
        {
            stats.TaskStatistics = new TaskSearchStatistics
            {
                TotalTasks = tasks.Count,
                CompletedTasks = tasks.Count(t => t.IsCompleted),
                PendingTasks = tasks.Count(t => !t.IsCompleted),
                OverdueTasks = tasks.Count(t => t.DueDate.HasValue && TimezoneService.Instance.IsPastDue(t.DueDate.Value) && !t.IsCompleted),
                DueTodayTasks = tasks.Count(t => t.DueDate.HasValue && TimezoneService.Instance.IsDueToday(t.DueDate.Value)),
                CategoriesFound = tasks.Select(t => t.Category).Distinct().ToList(),
                AveragePriority = tasks.Average(t => t.Priority),
                PriorityDistribution = tasks.GroupBy(t => t.Priority).ToDictionary(g => g.Key, g => g.Count())
            };
        }

        if (routines.Any())
        {
            stats.RoutineStatistics = new RoutineSearchStatistics
            {
                TotalRoutines = routines.Count,
                CompletedRoutines = routines.Count(r => r.IsCompleted),
                PendingRoutines = routines.Count(r => !r.IsCompleted),
                CompletedTodayRoutines = routines.Count(r => r.IsCompleted && TimezoneService.Instance.IsToday(r.LastCompletedDate))
            };
        }

        return stats;
    }

    private string GenerateCacheKey(SearchQuery query)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(query.SearchText ?? "");
        keyBuilder.Append("|");
        keyBuilder.Append(string.Join(",", query.Categories ?? Enumerable.Empty<string>()));
        keyBuilder.Append("|");
        keyBuilder.Append(query.MinPriority);
        keyBuilder.Append("|");
        keyBuilder.Append(query.MaxPriority);
        keyBuilder.Append("|");
        keyBuilder.Append(query.CreatedAfter?.Ticks);
        keyBuilder.Append("|");
        keyBuilder.Append(query.CreatedBefore?.Ticks);
        keyBuilder.Append("|");
        keyBuilder.Append(query.DueAfter?.Ticks);
        keyBuilder.Append("|");
        keyBuilder.Append(query.DueBefore?.Ticks);
        keyBuilder.Append("|");
        keyBuilder.Append(query.CompletionStatus);
        keyBuilder.Append("|");
        keyBuilder.Append(query.ShowOverdueOnly);
        keyBuilder.Append("|");
        keyBuilder.Append(query.ShowDueTodayOnly);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SearchOptions?.CaseSensitive);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SearchOptions?.UseRegex);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SearchOptions?.WholeWordOnly);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SearchOptions?.ExactMatch);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SearchOptions?.FuzzySearch);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SortBy);
        keyBuilder.Append("|");
        keyBuilder.Append(query.SortDescending);

        return keyBuilder.ToString();
    }

    private void CacheSearchResult(string key, SearchResult result)
    {
        if (_searchCache.Count >= MAX_CACHE_SIZE)
        {
            CleanupExpiredCache(null);
        }

        _searchCache.TryAdd(key, result);
    }

    private void CleanupExpiredCache(object? state)
    {
        if (_disposed) return;

        _cacheLock.EnterWriteLock();
        try
        {
            var keysToRemove = _searchCache
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _searchCache.TryRemove(key, out _);
            }
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    public void ClearCache()
    {
        _cacheLock.EnterWriteLock();
        try
        {
            _searchCache.Clear();
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    public SearchCacheStatistics GetCacheStatistics()
    {
        _cacheLock.EnterReadLock();
        try
        {
            return new SearchCacheStatistics
            {
                TotalCachedSearches = _searchCache.Count,
                ExpiredCachedSearches = _searchCache.Count(kvp => kvp.Value.IsExpired),
                CacheHitRate = 0.0,
                MemoryUsage = _searchCache.Sum(kvp => EstimateMemoryUsage(kvp.Value))
            };
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    private long EstimateMemoryUsage(SearchResult result)
    {
        var baseSize = 1000;
        var tasksSize = result.Tasks.Count * 500;
        var routinesSize = result.Routines.Count * 200;
        
        return baseSize + tasksSize + routinesSize;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        _cacheCleanupTimer?.Dispose();
        _cacheLock?.Dispose();
        _searchCache?.Clear();
    }
}

public class PerformanceOptimizationService : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly SemaphoreSlim _processSemaphore;
    private readonly ConcurrentQueue<Func<Task>> _backgroundQueue;
    private readonly Timer _cleanupTimer;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private volatile bool _disposed = false;

    public PerformanceOptimizationService()
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 1000,
            CompactionPercentage = 0.1
        });
        
        _processSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        _backgroundQueue = new ConcurrentQueue<Func<Task>>();
        _cancellationTokenSource = new CancellationTokenSource();
        
        _cleanupTimer = new Timer(PerformCleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        
        Task.Run(ProcessBackgroundQueue, _cancellationTokenSource.Token);
    }

    public async Task<T> QueueBackgroundTask<T>(Func<T> operation)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PerformanceOptimizationService));

        var tcs = new TaskCompletionSource<T>();
        
        _backgroundQueue.Enqueue(async () =>
        {
            try
            {
                await _processSemaphore.WaitAsync(_cancellationTokenSource.Token);
                try
                {
                    var result = await Task.Run(operation, _cancellationTokenSource.Token);
                    tcs.SetResult(result);
                }
                finally
                {
                    _processSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return await tcs.Task;
    }

    public T GetOrCache<T>(string key, Func<T> factory, TimeSpan? expiration = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PerformanceOptimizationService));

        if (_cache.TryGetValue(key, out T cachedValue))
        {
            return cachedValue;
        }

        var value = factory();
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(30),
            Size = 1
        };

        _cache.Set(key, value, options);
        return value;
    }

    public void InvalidateCache(string key)
    {
        _cache.Remove(key);
    }

    public void ClearCache()
    {
        if (_cache is MemoryCache mc)
        {
            mc.Compact(1.0);
        }
    }

    public IDisposable StartOperationTiming(string operationName)
    {
        return new OperationTimer(operationName);
    }

    public T GetCachedData<T>(string key, Func<T> factory, TimeSpan expiration)
    {
        return GetOrCache(key, factory, expiration);
    }

    public void OptimizeForLargeDataSets() 
    {
    }

    public void BatchUpdateCollection<T>(IEnumerable<T> items, Action<T> updateAction)
    {
        foreach (var item in items)
        {
            updateAction(item);
        }
    }

    public void EndOperationTiming(IDisposable timerOperation)
    {
        timerOperation?.Dispose();
    }

    private async Task ProcessBackgroundQueue()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (_backgroundQueue.TryDequeue(out var task))
                {
                    await task();
                }
                else
                {
                    await Task.Delay(100, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
            }
        }
    }

    private void PerformCleanup(object? state)
    {
        if (_disposed) return;

        try
        {
            GC.Collect(0, GCCollectionMode.Optimized);
            
            if (_cache is MemoryCache mc)
            {
                mc.Compact(0.1);
            }
        }
        catch (Exception)
        {
        }
    }

    public PerformanceMetrics GetMetrics()
    {
        return new PerformanceMetrics
        {
            QueuedTasks = _backgroundQueue.Count,
            AvailableThreads = _processSemaphore.CurrentCount,
            CacheSize = (_cache as MemoryCache)?.Count ?? 0,
            MemoryUsage = GC.GetTotalMemory(false)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        _cancellationTokenSource?.Cancel();
        _cleanupTimer?.Dispose();
        _processSemaphore?.Dispose();
        _cache?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}

public class SearchQuery
{
    public string? SearchText { get; set; }
    public List<string>? Categories { get; set; }
    public int? MinPriority { get; set; }
    public int? MaxPriority { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public DateTime? DueAfter { get; set; }
    public DateTime? DueBefore { get; set; }
    public bool? CompletionStatus { get; set; }
    public bool ShowOverdueOnly { get; set; }
    public bool ShowDueTodayOnly { get; set; }
    public bool ShowCompletedTodayOnly { get; set; }
    public bool? HasDueDate { get; set; }
    public bool? HasDescription { get; set; }
    public bool SearchTasks { get; set; } = true;
    public bool SearchRoutines { get; set; } = true;
    public string SortBy { get; set; } = "name";
    public bool SortDescending { get; set; } = false;
    public int PageIndex { get; set; } = 0;
    public int PageSize { get; set; } = 0;
    public SearchOptions SearchOptions { get; set; } = new();
    public Expression<Func<TaskItem, bool>>? CustomFilter { get; set; }
    public Expression<Func<RoutineItem, bool>>? CustomRoutineFilter { get; set; }
}

public class SearchOptions
{
    public bool CaseSensitive { get; set; } = false;
    public bool UseRegex { get; set; } = false;
    public bool WholeWordOnly { get; set; } = false;
    public bool ExactMatch { get; set; } = false;
    public bool FuzzySearch { get; set; } = false;
    public int FuzzyThreshold { get; set; } = 2;
}

public class SearchResult
{
    public SearchQuery Query { get; set; } = new();
    public List<TaskItem> Tasks { get; set; } = new();
    public List<RoutineItem> Routines { get; set; } = new();
    public int TotalTasksFound { get; set; }
    public int TotalRoutinesFound { get; set; }
    public int TotalItemsFound { get; set; }
    public DateTime SearchTime { get; set; }
    public TimeSpan SearchDuration { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public SearchStatistics? Statistics { get; set; }
    
    public bool IsExpired => DateTime.Now - SearchTime > TimeSpan.FromMinutes(30);
}

public class SearchStatistics
{
    public TaskSearchStatistics? TaskStatistics { get; set; }
    public RoutineSearchStatistics? RoutineStatistics { get; set; }
}

public class TaskSearchStatistics
{
    public int TotalTasks { get; set; }
    public int CompletedTasks { get; set; }
    public int PendingTasks { get; set; }
    public int OverdueTasks { get; set; }
    public int DueTodayTasks { get; set; }
    public List<string> CategoriesFound { get; set; } = new();
    public double AveragePriority { get; set; }
    public Dictionary<int, int> PriorityDistribution { get; set; } = new();
}

public class RoutineSearchStatistics
{
    public int TotalRoutines { get; set; }
    public int CompletedRoutines { get; set; }
    public int PendingRoutines { get; set; }
    public int CompletedTodayRoutines { get; set; }
}

public class SearchCacheStatistics
{
    public int TotalCachedSearches { get; set; }
    public int ExpiredCachedSearches { get; set; }
    public double CacheHitRate { get; set; }
    public long MemoryUsage { get; set; }
}

public class PerformanceMetrics
{
    public int QueuedTasks { get; set; }
    public int AvailableThreads { get; set; }
    public int CacheSize { get; set; }
    public long MemoryUsage { get; set; }
}

public class OperationTimer : IDisposable
{
    private readonly string _operationName;
    private readonly DateTime _startTime;
    private bool _disposed = false;

    public OperationTimer(string operationName)
    {
        _operationName = operationName;
        _startTime = DateTime.Now;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            var duration = DateTime.Now - _startTime;
            System.Diagnostics.Debug.WriteLine($"Operation '{_operationName}' completed in {duration.TotalMilliseconds:F2}ms");
            _disposed = true;
        }
    }
}