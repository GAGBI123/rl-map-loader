using System.Windows;
using System.Windows.Input;

namespace RLMapLoader.Views
{
    public partial class DialogWindow : Window
    {
        private bool _primary;

        private DialogWindow()
        {
            InitializeComponent();
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        /// <summary>Returns true when the primary button was chosen.</summary>
        public static bool Show(Window? owner, string title, string message, string primary = "OK",
                                string? secondary = null, bool isError = false)
        {
            var d = new DialogWindow();
            d.TitleText.Text = title;
            d.MessageText.Text = message;
            d.PrimaryButton.Content = primary;
            if (secondary == null) d.SecondaryButton.Visibility = Visibility.Collapsed;
            else d.SecondaryButton.Content = secondary;
            d.IconText.Text = isError ? "" : "";
            d.IconText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[isError ? "Orange" : "BlueLight"];
            if (owner != null && owner.IsVisible)
            {
                d.Owner = owner;
                d.Topmost = owner.Topmost;
            }
            else
            {
                d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.Topmost = true;
            }
            d.ShowDialog();
            return d._primary;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            _primary = true;
            Close();
        }

        private void Secondary_Click(object sender, RoutedEventArgs e) => Close();
    }
}
