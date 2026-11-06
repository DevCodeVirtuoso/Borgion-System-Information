using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace BorgionSystemInfo
{
    public partial class MainWindow : Window
    {
        List<InfoSection> _sections = new List<InfoSection>();

        public MainWindow()
        {
            InitializeComponent();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}  ·  F5 = vernieuwen  ·  Ctrl+Shift+C = alles kopiëren";
            Loaded += async (_, __) => await LoadAsync();
        }

        async Task LoadAsync()
        {
            RefreshButton.IsEnabled = CopyAllButton.IsEnabled = false;
            LoadingPanel.Visibility = Visibility.Visible;
            Scroller.Opacity = 0.35;
            Mouse.OverrideCursor = Cursors.AppStarting;
            try
            {
                _sections = await RunSta(SystemInfoCollector.Collect);
                Sections.ItemsSource = _sections;
                var user = _sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Label == "Aangemeld als")?.Value;
                SubText.Text = Environment.MachineName + (user != null ? "  ·  " + user : "")
                               + "  ·  opgehaald om " + DateTime.Now.ToString("HH:mm");
            }
            catch (Exception ex)
            {
                SubText.Text = "Ophalen mislukt: " + ex.Message;
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                Scroller.Opacity = 1;
                Mouse.OverrideCursor = null;
                RefreshButton.IsEnabled = true;
                CopyAllButton.IsEnabled = _sections.Count > 0;
            }
        }

        /// <summary>Voert werk uit op een aparte STA-thread (nodig voor Shell-COM), zodat het venster niet bevriest.</summary>
        static Task<T> RunSta<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return tcs.Task;
        }

        // ------------------------------------------------------------------ Kopiëren

        void CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();

        void CopyAll_Executed(object sender, ExecutedRoutedEventArgs e) => CopyAll();

        void CopyAll()
        {
            if (_sections.Count == 0) return;
            if (SetClipboard(SystemInfoCollector.ToText(_sections)))
                ShowToast("Alle systeeminformatie is gekopieerd naar het klembord");
        }

        void CopySection_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is InfoSection sec)) return;
            if (SetClipboard(SystemInfoCollector.ToText(new[] { sec }, header: false)))
                ShowToast("Sectie \"" + sec.Title + "\" gekopieerd");
        }

        void CopyItem_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is InfoItem item)) return;
            if (SetClipboard(item.Value))
                ShowToast(item.Label + " gekopieerd");
        }

        bool SetClipboard(string text)
        {
            // Het klembord kan kort bezet zijn door een ander programma: een paar keer opnieuw proberen.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (COMException) { Thread.Sleep(80); }
                catch (ExternalException) { Thread.Sleep(80); }
            }
            MessageBox.Show(this, "Het klembord is bezet door een ander programma. Probeer het opnieuw.",
                "Borgion System Information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        void ShowToast(string text)
        {
            ToastText.Text = text;
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2900))));
            Toast.BeginAnimation(OpacityProperty, anim);
        }

        // ------------------------------------------------------------------ Vernieuwen

        async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        async void Refresh_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (RefreshButton.IsEnabled) await LoadAsync();
        }
    }
}
