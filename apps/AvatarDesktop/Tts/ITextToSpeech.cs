namespace AvatarDesktop.Tts;

public interface ITextToSpeech
{
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}
