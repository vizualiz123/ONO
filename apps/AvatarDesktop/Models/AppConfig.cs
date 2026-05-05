namespace AvatarDesktop.Models;

public sealed class AppConfig
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string Model { get; set; } = "local-model";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 256;

    public AppConfig Clone()
    {
        return new AppConfig
        {
            BaseUrl = BaseUrl,
            Model = Model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
        };
    }
}
