using System;
using System.Diagnostics;
using System.Windows;

namespace BoltFetch
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void Window_Deactivated(object sender, System.EventArgs e)
        {
            // Prevent accidental closures during drag operation
        }
    }
}
