namespace Template.AgenticETL.Models.ConfigBindings;

public class RedisOptions
{
    public required string Host { get; set; }
    public int Port { get; set; } = 6379;
    public required string Password { get; set; }
    public bool Ssl { get; set; } = false;
}
