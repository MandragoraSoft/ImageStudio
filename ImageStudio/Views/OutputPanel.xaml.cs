using ImageStudio.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ImageStudio.Views;

// Panel wyniku: obraz albo wideo z paskiem sterowania. Odtwarzaniem steruje kod widoku (MediaElement nie ma poleceń do wiązania):
// wideo startuje po wczytaniu, a pasek pozwala je zatrzymać, przewinąć, odtworzyć od początku i zapętlić
public partial class OutputPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _playing;
    private bool _dragging;

    public OutputPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
    }

    private void Play()
    {
        Player.Play();
        _playing = true;
        PlayPauseButton.Content = Loc.T("out.pause");
        _timer.Start();
    }

    private void Pause()
    {
        Player.Pause();
        _playing = false;
        PlayPauseButton.Content = Loc.T("out.play");
        _timer.Stop();
    }

    private void Restart()
    {
        Player.Position = TimeSpan.Zero;
        Seek.Value = 0;
        Play();
    }

    private bool AtEnd => Seek.Maximum > 0 && Seek.Value >= Seek.Maximum - 0.05;

    private void UpdateProgress()
    {
        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = _dragging ? TimeSpan.FromSeconds(Seek.Value) : Player.Position;
        if (!_dragging) Seek.Value = Math.Min(position.TotalSeconds, Seek.Maximum);
        TimeText.Text = $"{position:mm\\:ss} / {duration:mm\\:ss}";
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Seek.Maximum = Player.NaturalDuration.HasTimeSpan ? Math.Max(1, Player.NaturalDuration.TimeSpan.TotalSeconds) : 1;
        Seek.Value = 0;
        UpdateProgress();
        Play();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (LoopBox.IsChecked == true)
        {
            Restart();
            return;
        }

        // Po końcu klatka zostaje, a przycisk pozwala odtworzyć wideo jeszcze raz
        _playing = false;
        _timer.Stop();
        PlayPauseButton.Content = Loc.T("out.replay");
        UpdateProgress();
        Seek.Value = Seek.Maximum;  // suwak na końcu: po tym wiemy, że "Odtwórz" ma zacząć od początku
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playing = false;
        _timer.Stop();
        PlayPauseButton.Content = Loc.T("out.play");
        TimeText.Text = Loc.T("out.cannotPlay");
    }

    // Ukryty podgląd (np. wynik zmienił się na obraz) nie gra w tle
    private void Player_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && _playing) Pause();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause();
        else if (AtEnd) Restart();
        else Play();
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => Restart();

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        UpdateProgress();
    }

    // Kliknięcie w tor (bez przeciągania) też przewija
    private void Seek_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragging) return;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
        UpdateProgress();
    }
}
