using Microsoft.AspNetCore.Mvc;
[ApiController]
[Route("api/[controller]")]
public class TasksController : ControllerBase
{
    private readonly ITaskProcessor _taskProcessor;
    private readonly ILogger<TasksController> _logger;

    public TasksController(ITaskProcessor taskProcessor, ILogger<TasksController> logger)
    {
        _taskProcessor = taskProcessor;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTask([FromBody] TaskRequest request)
    {
        try
        {
            if (request == null)
            {
                return BadRequest("Request body is required");
            }

            if (string.IsNullOrWhiteSpace(request.Data))
            {
                return BadRequest("Data field is required");
            }

            if (request.ProcessingTimeSeconds <= 0 || request.ProcessingTimeSeconds > 300)
            {
                return BadRequest("Processing time must be between 1 and 300 seconds");
            }

            var taskId = _taskProcessor.SubmitTask(request);
            
            _logger.LogInformation("Created task {TaskId} with processing time {Seconds}s", 
                taskId, request.ProcessingTimeSeconds);

            return Accepted(new
            {
                TaskId = taskId,
                Status = "Pending",
                Message = "Task submitted for processing",
                EstimatedProcessingTime = $"{request.ProcessingTimeSeconds} seconds",
                CreatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating task");
            return StatusCode(500, new { Error = "Internal server error", Details = ex.Message });
        }
    }

    [HttpGet("{taskId}")]
    public IActionResult GetTaskStatus(Guid taskId)
    {
        try
        {
            var status = _taskProcessor.GetTaskStatus(taskId);
            
            if (status == null)
            {
                return NotFound(new { Error = $"Task with ID {taskId} not found" });
            }

            var response = new
            {
                status.Id,
                status.Status,
                status.Progress,
                status.Result,
                status.Data,
                status.ProcessingTimeSeconds,
                status.CreatedAt,
                status.CompletedAt,
                Duration = status.CompletedAt.HasValue 
                    ? (status.CompletedAt - status.CreatedAt)?.TotalSeconds 
                    : null
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task status for {TaskId}", taskId);
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpGet]
    public IActionResult GetAllTasks()
    {
        try
        {
            var tasks = _taskProcessor.GetAllTasks();
            var response = tasks.Select(t => new
            {
                t.Id,
                t.Status,
                t.Progress,
                t.Data,
                t.ProcessingTimeSeconds,
                t.CreatedAt,
                t.CompletedAt,
                Duration = t.CompletedAt.HasValue 
                    ? (t.CompletedAt - t.CreatedAt)?.TotalSeconds 
                    : null
            });

            return Ok(new { Tasks = response, TotalCount = response.Count() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all tasks");
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpDelete("{taskId}")]
    public IActionResult CancelTask(Guid taskId)
    {
        try
        {
            var success = _taskProcessor.CancelTask(taskId);
            
            if (!success)
            {
                return NotFound(new { Error = $"Task with ID {taskId} not found or cannot be cancelled" });
            }

            return Ok(new { Message = $"Task {taskId} was cancelled successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling task {TaskId}", taskId);
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }
}