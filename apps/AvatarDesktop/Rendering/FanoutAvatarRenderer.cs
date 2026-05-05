using System.Windows;

namespace AvatarDesktop.Rendering;

public sealed class FanoutAvatarRenderer : IAvatarRenderer
{
    private readonly List<IAvatarRenderer> _renderers = new();

    public FanoutAvatarRenderer(IAvatarRenderer primaryRenderer)
    {
        _renderers.Add(primaryRenderer);
    }

    public UIElement View => _renderers[0].View;

    public void AddRenderer(IAvatarRenderer renderer)
    {
        if (_renderers.Contains(renderer))
        {
            return;
        }

        _renderers.Add(renderer);
    }

    public void RemoveRenderer(IAvatarRenderer renderer)
    {
        if (_renderers.Count <= 1)
        {
            return;
        }

        _renderers.Remove(renderer);
    }

    public void LoadUsd(string path)
    {
        foreach (var renderer in _renderers.ToArray())
        {
            renderer.LoadUsd(path);
        }
    }

    public void SetAnimation(string name)
    {
        foreach (var renderer in _renderers.ToArray())
        {
            renderer.SetAnimation(name);
        }
    }

    public void SetBlendshape(string name, double value)
    {
        foreach (var renderer in _renderers.ToArray())
        {
            renderer.SetBlendshape(name, value);
        }
    }

    public void Update(TimeSpan dt)
    {
        foreach (var renderer in _renderers.ToArray())
        {
            renderer.Update(dt);
        }
    }
}
