namespace Template.AgenticETL.Models.ConfigBindings;

public class DocumentProcessingOptions
{
    public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan LockRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);
}
