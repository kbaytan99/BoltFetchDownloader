using System;
using System.Windows;
using System.Windows.Threading;

namespace BoltFetch
{
    public partial class NotificationWindow : Window
    {
        private DispatcherTimer _closeTimer;

        public NotificationWindow(string title, string message)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;

            // Positioning: Bottom Right
            this.Left = SystemParameters.WorkArea.Width - this.Width - 20;
            this.Top = SystemParameters.WorkArea.Height - this.Height - 20;

            _closeTimer = new DispatcherTimer();
            _closeTimer.Interval = TimeSpan.FromSeconds(4);
            _closeTimer.Tick += (s, e) => {
                _closeTimer.Stop();
                this.Close();
            };
            _closeTimer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            _closeTimer.Stop();
            this.Close();
        }
    }
}
