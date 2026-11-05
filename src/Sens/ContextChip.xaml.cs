using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Sens;

public partial class ContextChip : Window
{
    public ContextChip()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Win32.NoFocus(new WindowInteropHelper(this).Handle);
    }

    internal nint Handle => new WindowInteropHelper(this).Handle;

    internal void ShowAnimated()
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(85);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(.48, 1, duration) { EasingFunction = ease });
            Rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(3, 0, duration) { EasingFunction = ease });
        }
        Show();
    }
}
