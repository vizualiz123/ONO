using System.Windows;
using System.Windows.Input;
using AvatarDesktop.Rendering;

namespace AvatarDesktop;

public partial class WidgetWindow : Window
{
    public IAvatarRenderer Renderer { get; }

    public WidgetWindow(IAvatarRenderer renderer)
    {
        InitializeComponent();
        Renderer = renderer;
        WidgetViewportHost.Content = renderer.View;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || (Left == 0 && Top == 0))
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag exceptions caused by rapid input.
        }
    }
}
