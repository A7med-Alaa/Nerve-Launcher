using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nerve
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        private const int windowVisibilityHotKeyID = 8000;
        HotKeyManager toggler;

        // Observable collection for efficient data binding
        private ObservableCollection<AppInfo> _apps = new ObservableCollection<AppInfo>();
        public ObservableCollection<AppInfo> Apps
        {
            get => _apps;
            set
            {
                _apps = value;
                OnPropertyChanged(nameof(Apps));
            }
        }

        // For ICollectionView filtering
        private ICollectionView _appsView;
        private string _searchText = string.Empty;

        // Debounce timer for search
        private DispatcherTimer _searchDebounceTimer;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Setup debounce timer (delays filter refresh until user stops typing)
            _searchDebounceTimer = new DispatcherTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(10);
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            this.Hide();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            const int MOD_ALT = 0x0001;
            const int VK_SPACE = 0x20;
            base.OnSourceInitialized(e);
            toggler = new HotKeyManager(this, windowVisibilityHotKeyID);
            toggler.Register(MOD_ALT, VK_SPACE, toggleWindowVisibility);
        }

        private void toggleWindowVisibility()
        {
            if (this.Visibility == Visibility.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
                this.Focus();
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            }
        }

        private void LaunchApp(AppInfo app)
        {
            try
            {
                if (app.IsUWP && !string.IsNullOrEmpty(app.PackageFamilyName))
                {
                    // Launch UWP app using shell:AppsFolder protocol
                    string aumid = $"{app.PackageFamilyName}!App";

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"shell:AppsFolder\\{aumid}",
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(app.ShortcutPath))
                {
                    // Launch via shortcut
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app.ShortcutPath,
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(app.ExecutablePath))
                {
                    // Launch directly via executable
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app.ExecutablePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch {app.Name}: {ex.Message}");
            }

            this.Hide();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Focus();

            // Get the CollectionView for filtering
            _appsView = CollectionViewSource.GetDefaultView(Apps);

            // First, try to load from cache for fast startup
            var cachedApps = await Task.Run(() => AppCache.LoadFromCache());

            if (cachedApps != null && cachedApps.Count > 0)
            {
                // Load icons from cache in parallel (fast)
                var loadedApps = await Task.Run(() => AppCache.LoadIconsFromCache(cachedApps));

                // Update collection on UI thread
                foreach (var app in loadedApps)
                {
                    Apps.Add(app);
                }

                // Refresh apps in background
                _ = RefreshAppsInBackgroundAsync();
            }
            else
            {
                // No cache, load fresh (first run)
                var freshApps = await Task.Run(() => InstalledAppsHelper.GetAllInstalledAppsAsync());

                foreach (var app in freshApps)
                {
                    Apps.Add(app);
                }

                // Save to cache for next time
                _ = Task.Run(() => AppCache.SaveToCache(freshApps.ToList()));
            }

            // Select first item if available
            if (AppsListBox.Items.Count > 0)
            {
                AppsListBox.SelectedIndex = 0;
            }
        }

        private async Task RefreshAppsInBackgroundAsync()
        {
            // Wait a bit before refreshing
            await Task.Delay(2000);

            var freshApps = await Task.Run(() => InstalledAppsHelper.GetAllInstalledAppsAsync());

            // Check if there are changes
            var currentAppNames = Apps.Select(a => a.Name).OrderBy(n => n).ToList();
            var freshAppNames = freshApps.Select(a => a.Name).OrderBy(n => n).ToList();

            if (!currentAppNames.SequenceEqual(freshAppNames))
            {
                // Update collection on UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    Apps.Clear();
                    foreach (var app in freshApps)
                    {
                        Apps.Add(app);
                    }
                });
            }

            // Save updated cache
            await Task.Run(() => AppCache.SaveToCache(freshApps.ToList()));
        }

        /// <summary>
        /// Filter handler for CollectionViewSource - very efficient, no UI recreation
        /// </summary>
        private void AppsViewSource_Filter(object sender, FilterEventArgs e)
        {
            if (e.Item is AppInfo app)
            {
                if (string.IsNullOrWhiteSpace(_searchText))
                {
                    e.Accepted = true;
                }
                else
                {
                    e.Accepted = app.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && AppsListBox.Items.Count > 0)
            {
                AppsListBox.Focus();
                AppsListBox.SelectedIndex = 0;
                var container = AppsListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                container?.Focus();
                e.Handled = true;
            }

            if (e.Key == Key.Enter && AppsListBox.Items.Count > 0)
            {
                // Launch first visible item
                if (AppsListBox.Items[0] is AppInfo app)
                {
                    LaunchApp(app);
                }
                e.Handled = true;
            }

            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
            }
        }

        private void AppsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && AppsListBox.SelectedItem is AppInfo app)
            {
                LaunchApp(app);
                e.Handled = true;
            }

            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
            }

            // Go back to search box on typing
            if (e.Key >= Key.A && e.Key <= Key.Z)
            {
                SearchTextBox.Focus();
            }
        }

        private void AppsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AppsListBox.SelectedItem is AppInfo app)
            {
                LaunchApp(app);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            toggler.Unregister();
            base.OnClosed(e);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Debounce: restart timer on each keystroke
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounceTimer_Tick(object sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();

            // Update search text and refresh filter
            _searchText = SearchTextBox.Text;

            // Refresh the filter - this is MUCH faster than recreating controls
            var view = (CollectionViewSource)FindResource("AppsViewSource");
            view.View?.Refresh();

            // Select first result
            if (AppsListBox.Items.Count > 0)
            {
                AppsListBox.SelectedIndex = 0;
            }
        }
    }
}