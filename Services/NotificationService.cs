using System.Windows;

namespace BoltFetch.Services
{
    public class NotificationService
    {
        public void ShowPopup(string title, string message)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var notify = new NotificationWindow(title, message);
                notify.Show();
            });
        }

        public void ShowMessage(string message, string context = null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var errorWin = new GlobalErrorWindow(message, context);
                
                // Set owner if MainWindow is active so it acts as modal
                if (System.Windows.Application.Current.MainWindow != null && System.Windows.Application.Current.MainWindow.IsVisible)
                {
                    errorWin.Owner = System.Windows.Application.Current.MainWindow;
                }
                
                errorWin.ShowDialog();
            });
        }
    }
}
