using System;
using System.Windows;
using System.Windows.Input;

namespace Butterfly.Views
{
    public partial class AddAccountDialog : Window
    {
        public string AccountUsername { get; private set; } = string.Empty;
        public string AccountPassword { get; private set; } = string.Empty;
        public string AccountCharacter { get; private set; } = string.Empty;

        public AddAccountDialog()
        {
            InitializeComponent();
            UsernameBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) ||
                string.IsNullOrWhiteSpace(PasswordBox.Text) ||
                string.IsNullOrWhiteSpace(CharacterBox.Text))
            {
                MessageBox.Show(
                    Butterfly.Services.LocalizationManager.GetString("Dialog_AddAccount_Warning"),
                    Butterfly.Services.LocalizationManager.GetString("Dialog_AddAccount_WarningTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AccountUsername = UsernameBox.Text.Trim();
            AccountPassword = PasswordBox.Text.Trim();
            AccountCharacter = CharacterBox.Text.Trim();

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
