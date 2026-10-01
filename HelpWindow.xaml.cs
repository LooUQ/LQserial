using System.Windows;
using System.Windows.Input;

namespace LQserial;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
