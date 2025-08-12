using System.Windows;

namespace AdbExplorer
{
    public partial class InputDialog : Window
    {
        public string ResponseText { get; private set; } = "";

        public InputDialog(string title, string prompt, string defaultText = "")
        {
            InitializeComponent();
            Title = title;
            PromptLabel.Content = prompt;
            ResponseTextBox.Text = defaultText;
            ResponseTextBox.SelectAll();  // Select all text for easy replacement
            ResponseTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = ResponseTextBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
