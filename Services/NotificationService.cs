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

        public void ShowMessage(string message)
        {
            System.Windows.MessageBox.Show(message);
        }
    }
}
