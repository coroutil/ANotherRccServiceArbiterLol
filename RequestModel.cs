public sealed class RequestModel
{
    public string Type { get; set; } = string.Empty;
    public long Id { get; set; }

    // 67,string,true
    public string Arguments { get; set; } = string.Empty;
}