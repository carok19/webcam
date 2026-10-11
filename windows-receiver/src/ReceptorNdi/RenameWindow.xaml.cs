using System.Windows;

namespace ReceptorNdi;

public partial class RenameWindow : Window
{
    public string Result { get; private set; } = "";

    public RenameWindow(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = NameBox.Text.Trim();
        DialogResult = true;
    }
}
