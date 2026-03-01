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

        private void Window_Deactivated(object sender, System.EventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
