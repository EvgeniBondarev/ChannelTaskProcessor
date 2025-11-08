using System.Collections.Concurrent;
using System.Threading.Channels;

public interface ITaskProcessor
{
    Guid SubmitTask(TaskRequest request);
    TaskStatus GetTaskStatus(Guid taskId);
    IEnumerable<TaskStatus> GetAllTasks();
    bool CancelTask(Guid taskId);
}
public class TaskProcessor : ITaskProcessor, IHostedService, IDisposable
{
    private readonly Channel<(Guid taskId, TaskRequest request)> _channel;
    private readonly ConcurrentDictionary<Guid, TaskStatus> _taskStatuses;
    private readonly ILogger<TaskProcessor> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _semaphore;
    private Task _processingTask;
    private readonly int _maxConcurrentTasks = 50;

    public TaskProcessor(ILogger<TaskProcessor> logger)
    {
        _logger = logger;
        _taskStatuses = new ConcurrentDictionary<Guid, TaskStatus>();
        _cancellationTokenSource = new CancellationTokenSource();
        _semaphore = new SemaphoreSlim(_maxConcurrentTasks, _maxConcurrentTasks);
        
        // Создаем ограниченный канал
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        };
        _channel = Channel.CreateBounded<(Guid, TaskRequest)>(options);
    }

    public Guid SubmitTask(TaskRequest request)
    {
        if (request.ProcessingTimeSeconds <= 0 || request.ProcessingTimeSeconds > 300)
        {
            throw new ArgumentException("Processing time must be between 1 and 300 seconds");
        }

        var taskId = Guid.NewGuid();
        var taskStatus = new TaskStatus
        {
            Id = taskId,
            Status = "Pending",
            Progress = 0,
            CreatedAt = DateTime.UtcNow,
            Data = request.Data,
            ProcessingTimeSeconds = request.ProcessingTimeSeconds
        };

        if (_taskStatuses.TryAdd(taskId, taskStatus))
        {
            if (_channel.Writer.TryWrite((taskId, request)))
            {
                _logger.LogInformation("Task {TaskId} submitted successfully. Processing time: {Seconds}s", 
                    taskId, request.ProcessingTimeSeconds);
                return taskId;
            }
            else
            {
                _taskStatuses.TryRemove(taskId, out _);
                throw new InvalidOperationException("Channel is full or closed. Cannot submit task.");
            }
        }

        throw new InvalidOperationException("Failed to submit task due to conflict.");
    }

    public TaskStatus GetTaskStatus(Guid taskId)
    {
        return _taskStatuses.TryGetValue(taskId, out var status) ? status : null;
    }

    public IEnumerable<TaskStatus> GetAllTasks()
    {
        return _taskStatuses.Values.OrderByDescending(t => t.CreatedAt);
    }

    public bool CancelTask(Guid taskId)
    {
        if (_taskStatuses.TryGetValue(taskId, out var status) && 
            (status.Status == "Pending" || status.Status == "Processing"))
        {
            status.Status = "Cancelled";
            status.Result = "Task was cancelled by user";
            status.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Task {TaskId} was cancelled", taskId);
            return true;
        }
        return false;
    }

    private async Task ProcessTasksAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Task processor started");

        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    var (taskId, request) = item;
                    
                    // Ограничиваем количество одновременно выполняемых задач
                    await _semaphore.WaitAsync(cancellationToken);
                    
                    // Запускаем обработку задачи в фоне
                    _ = ProcessSingleTaskAsync(taskId, request, cancellationToken)
                        .ContinueWith(t => 
                        {
                            _semaphore.Release();
                            if (t.IsFaulted)
                            {
                                _logger.LogError(t.Exception, "Unhandled error in task {TaskId}", taskId);
                            }
                        }, 
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task processor stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in task processor");
        }
        finally
        {
            _logger.LogInformation("Task processor stopped");
        }
    }

    private async Task ProcessSingleTaskAsync(Guid taskId, TaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting processing of task {TaskId}", taskId);

            // Проверяем, не была ли задача отменена перед началом обработки
            if (!_taskStatuses.TryGetValue(taskId, out var status) || status.Status == "Cancelled")
            {
                _logger.LogInformation("Task {TaskId} was cancelled before processing", taskId);
                return;
            }

            // Обновляем статус на "Processing"
            status.Status = "Processing";
            status.Progress = 10;

            // Искусственная задержка с имитацией прогресса
            var totalSteps = 10;
            var delayPerStep = request.ProcessingTimeSeconds * 1000 / totalSteps;
            
            for (int step = 1; step <= totalSteps; step++)
            {
                // Проверяем отмену перед каждым шагом
                cancellationToken.ThrowIfCancellationRequested();
                
                // Проверяем, не отменили ли задачу через API
                if (_taskStatuses.TryGetValue(taskId, out var currentStatus) && currentStatus.Status == "Cancelled")
                {
                    _logger.LogInformation("Task {TaskId} was cancelled during processing", taskId);
                    return;
                }

                // Имитация обработки
                await Task.Delay(delayPerStep, cancellationToken);
                
                // Обновляем прогресс
                var progress = 10 + (step * 90 / totalSteps);
                UpdateProgress(taskId, progress);
                
                _logger.LogDebug("Task {TaskId} progress: {Progress}%", taskId, progress);
            }
            
            // Завершаем задачу
            CompleteTask(taskId, request);
            _logger.LogInformation("Task {TaskId} completed successfully", taskId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task {TaskId} processing was cancelled", taskId);
            CancelTask(taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing task {TaskId}", taskId);
            FailTask(taskId, ex.Message);
        }
    }

    private void UpdateProgress(Guid taskId, int progress)
    {
        if (_taskStatuses.TryGetValue(taskId, out var status))
        {
            status.Progress = progress;
        }
    }

    private void CompleteTask(Guid taskId, TaskRequest request)
    {
        if (_taskStatuses.TryGetValue(taskId, out var status))
        {
            status.Status = "Completed";
            status.Progress = 100;
            status.Result = $"Successfully processed: '{request.Data}'";
            status.CompletedAt = DateTime.UtcNow;
        }
    }

    private void FailTask(Guid taskId, string error)
    {
        if (_taskStatuses.TryGetValue(taskId, out var status))
        {
            status.Status = "Failed";
            status.Result = $"Error: {error}";
            status.CompletedAt = DateTime.UtcNow;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting task processor service");
        _processingTask = ProcessTasksAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping task processor service...");
        
        // Завершаем канал - новые задачи не принимаются
        _channel.Writer.Complete();
        
        // Отменяем обработку
        _cancellationTokenSource.Cancel();

        try
        {
            // Ждем завершения обработки текущих задач
            if (_processingTask != null)
            {
                await _processingTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout while waiting for task processor to stop gracefully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Task processor stop operation was cancelled");
        }

        _logger.LogInformation("Task processor service stopped");
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _semaphore?.Dispose();
        _logger.LogInformation("Task processor disposed");
    }
}