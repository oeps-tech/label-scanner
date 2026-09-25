using System.Windows;
using Scanner.Server.Services;

namespace Scanner.Server;

public partial class LabelVersionsWindow : Window
{
    public IReadOnlyList<LabelVersionReference> Versions => LabelVersionReference.All;

    public LabelVersionsWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void CloseReference(object sender, RoutedEventArgs e) => Close();
}
