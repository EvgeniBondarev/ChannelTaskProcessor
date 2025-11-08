public class TaskRequest
{
    public string Data { get; set; }
    public int ProcessingTimeSeconds { get; set; } = 5;
}

public class TaskStatus
{
    public Guid Id { get; set; }
    public string Status { get; set; } // "Pending", "Processing", "Completed", "Failed", "Cancelled"
    public int Progress { get; set; }
    public string Result { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Data { get; set; }
    public int ProcessingTimeSeconds { get; set; }
}