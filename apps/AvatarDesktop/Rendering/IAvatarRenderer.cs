using System.Windows;

namespace AvatarDesktop.Rendering;

public interface IAvatarRenderer
{
    UIElement View { get; }

    void LoadUsd(string path);
    void SetAnimation(string name);
    void SetBlendshape(string name, double value);
    void Update(TimeSpan dt);
}
