using System.Windows;

namespace Pipopu;

internal sealed record SetupChoices(bool OpenOneDrive, bool Move, bool StartAtSignIn);

internal partial class SetupWizard : Window
{
    private readonly Action<SetupChoices> finish;
    private int step;

    internal bool Completed { get; private set; }

    internal SetupWizard(bool canMove, bool installed, bool startsAtSignIn, Action<SetupChoices> finish)
    {
        this.finish = finish;
        InitializeComponent();
        MoveChoice.IsEnabled = canMove;
        MoveChoice.IsChecked = installed;
        MoveDetail.Text = canMove
            ? "Moves this executable to your Windows user folder and restarts Pipopu."
            : installed ? "Pipopu already runs from Local AppData."
                : "This option is available in the single-file Windows download.";
        StartupChoice.IsChecked = startsAtSignIn;
        UpdatePage();
    }

    private void UpdatePage()
    {
        StepText.Text = $"{step + 1} of 3";
        (HeadingText.Text, DescriptionText.Text) = step switch
        {
            0 => ("Send something nearby",
                "Open Pipopu on both devices. Choose some text or a file, pick a device, and compare the pairing code the first time you send."),
            1 => ("Keep transfers going with OneDrive",
                "OneDrive can transfer encrypted files when your devices are on different networks. You can also use it to save received files."),
            _ => ("Make Pipopu yours",
                "Choose where to keep Pipopu and when you want it to start.")
        };
        CloudChoice.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DesktopChoices.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step == 2 ? "Finish" : "Continue";
        ProblemText.Visibility = Visibility.Collapsed;
    }

    private void NextClick(object sender, RoutedEventArgs e)
    {
        if (step < 2)
        {
            step++;
            UpdatePage();
            return;
        }
        try
        {
            Completed = true;
            finish(new SetupChoices(CloudChoice.IsChecked == true, MoveChoice.IsEnabled && MoveChoice.IsChecked == true,
                StartupChoice.IsChecked == true));
            if (IsLoaded)
                Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            Completed = false;
            ProblemText.Text = "Pipopu couldn’t apply these choices. " + error.Message;
            ProblemText.Visibility = Visibility.Visible;
        }
    }

    private void BackClick(object sender, RoutedEventArgs e)
    {
        step--;
        UpdatePage();
    }

    private void SkipClick(object sender, RoutedEventArgs e) => Close();
}
