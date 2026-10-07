using System.Windows;
using System.Windows.Controls;
using ZapretGui.Core;

namespace ZapretGui.Views;

public partial class ConnectionAssistantView : UserControl
{
    public ConnectionAssistantView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is AppState state) state.Assistant.RefreshContext(); };
    }

    private void OnGoalClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppState state && sender is RadioButton { Tag: string tag } &&
            Enum.TryParse<ConnectionGoal>(tag, out var goal)) state.Assistant.SelectGoal(goal);
    }
}
