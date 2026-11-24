using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace RLMapLoader.Linux.Views
{
    public partial class DialogWindow : Window
    {
        public DialogWindow() => InitializeComponent();

        /// <summary>True when the primary button was chosen.</summary>
        public static async Task<bool> ShowAsync(Window? owner, string title, string message, string primary = "OK",
                                                 string? secondary = null, bool isError = false)
        {
            var d = new DialogWindow();
            d.TitleText.Text = title;
            d.MessageText.Text = message;
            d.PrimaryButton.Content = primary;
            if (secondary == null) d.SecondaryButton.IsVisible = false;
            else d.SecondaryButton.Content = secondary;
            d.IconPath.Data = (Geometry)d.FindResource(isError ? "IconAlert" : "IconInfo")!;
            d.IconPath.Foreground = (IBrush)d.FindResource(isError ? "Orange" : "BlueLight")!;

            if (owner is { IsVisible: true })
            {
                d.Topmost = owner.Topmost;
                return await d.ShowDialog<bool>(owner);
            }
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            d.Topmost = true;
            var tcs = new TaskCompletionSource<bool>();
            d.Closed += (_, _) => tcs.TrySetResult(d._result);
            d.Show();
            return await tcs.Task;
        }

        private bool _result;

        private void Primary_Click(object? sender, RoutedEventArgs e)
        {
            _result = true;
            Close(true);
        }

        private void Secondary_Click(object? sender, RoutedEventArgs e) => Close(false);

        private void Body_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }
    }
}
