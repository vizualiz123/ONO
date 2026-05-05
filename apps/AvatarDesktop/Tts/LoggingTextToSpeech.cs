namespace AvatarDesktop.Tts;

public sealed class LoggingTextToSpeech : ITextToSpeech
{
    private readonly Action<string> _log;

    public LoggingTextToSpeech(Action<string> log)
    {
        _log = log;
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        _log($"(TTS) {text}");
        return Task.CompletedTask;
    }
}
