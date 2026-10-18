using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SharpMediaFoundationInterop.WPF
{
    public abstract partial class MediaControlBase
    {
        #region Full screen

        /// <summary>
        /// Shows the video on the whole of its screen, or back where it was: see <see cref="IsFullScreen"/>. The F or F11 key,
        /// or a double click over the video; Escape leaves it.
        /// </summary>
        public static RoutedUICommand ToggleFullScreenCommand { get; } =
            new RoutedUICommand("Full screen", "ToggleFullScreen", typeof(MediaControlBase));

        /// <summary>
        /// Whether the control is shown on the whole of the screen it is on: moved, as it plays, into a window of no border
        /// over the screen, and back where it was - its parent, its place among its siblings, its size - as this is false
        /// again. Of a parent it cannot be moved out of - a content control, a decorator or a panel it can - it stays false.
        /// </summary>
        public bool IsFullScreen
        {
            get { return (bool)GetValue(IsFullScreenProperty); }
            set { SetValue(IsFullScreenProperty, value); }
        }

        public static readonly DependencyProperty IsFullScreenProperty =
            DependencyProperty.Register("IsFullScreen", typeof(bool), typeof(MediaControlBase),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsFullScreenChanged));

        /// <summary>The control went to the whole of the screen, or back where it was.</summary>
        public event RoutedPropertyChangedEventHandler<bool> FullScreenChanged;

        /// <summary>Goes to the whole of the screen, or back where it was.</summary>
        public void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

        // the window over the screen, and where the control was before it
        private Window _fullScreenWindow;
        private DependencyObject _homeParent;
        private int _homeIndex;
        private bool _ownerClosing;
        private Window _owner;
        private readonly (DependencyProperty Property, object Value)[] _homeLayout = new (DependencyProperty, object)[LayoutProperties.Length];

        // what the control's place gave it, which the screen is not to: its size, its margin, its alignment
        private static readonly DependencyProperty[] LayoutProperties =
        {
            WidthProperty, HeightProperty, MinWidthProperty, MinHeightProperty, MaxWidthProperty, MaxHeightProperty,
            MarginProperty, HorizontalAlignmentProperty, VerticalAlignmentProperty,
        };

        private static void OnIsFullScreenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (MediaControlBase)d;
            bool fullScreen = (bool)e.NewValue;
            if (fullScreen ? control.EnterFullScreen() : control.LeaveFullScreen())
                control.FullScreenChanged?.Invoke(control, new RoutedPropertyChangedEventArgs<bool>(!fullScreen, fullScreen));
        }

        private bool EnterFullScreen()
        {
            if (_fullScreenWindow != null)
                return false;
            var owner = Window.GetWindow(this);
            var source = PresentationSource.FromVisual(this);
            var parent = LogicalTreeHelper.GetParent(this) ?? VisualTreeHelper.GetParent(this);
            if (owner == null || source?.CompositionTarget == null || parent == null || !CanLeave(parent))
            {
                // of no window, or of a parent it cannot leave: where it is
                SetCurrentValue(IsFullScreenProperty, false);
                return false;
            }

            // the screen of the control's middle: the window, maximized there, covers it - its taskbar too, of no border
            // of the device pixels PointToScreen gives, into the device independent units a window is placed in
            Point middle = source.CompositionTarget.TransformFromDevice.Transform(PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2)));

            Leave(parent);
            for (int i = 0; i < LayoutProperties.Length; i++)
            {
                _homeLayout[i] = (LayoutProperties[i], ReadLocalValue(LayoutProperties[i]));
                ClearValue(LayoutProperties[i]);
            }

            _owner = owner;
            _ownerClosing = false;
            owner.Closing += Owner_Closing;
            _fullScreenWindow = new Window
            {
                Title = owner.Title,
                Icon = owner.Icon,
                Owner = owner,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = middle.X - 50,
                Top = middle.Y - 50,
                Width = 100,
                Height = 100,
                Background = Brushes.Black,
                ShowInTaskbar = false,
                Content = this,
            };
            _fullScreenWindow.WindowState = WindowState.Maximized;
            _fullScreenWindow.Closing += FullScreenWindow_Closing;
            _fullScreenWindow.Show();
            _fullScreenWindow.Activate();
            Focus();
            ShowControlsForAWhile();
            return true;
        }

        private bool LeaveFullScreen()
        {
            var window = _fullScreenWindow;
            if (window == null)
                return false;
            _fullScreenWindow = null;
            window.Closing -= FullScreenWindow_Closing;
            GoHome(window);
            window.Close();
            _owner?.Activate();
            Focus();
            return true;
        }

        // back to its place: out of the window over the screen, of the size and alignment it had
        private void GoHome(Window window)
        {
            window.Content = null;
            foreach (var (property, value) in _homeLayout)
            {
                if (property == null)
                    continue;
                if (value == DependencyProperty.UnsetValue)
                    ClearValue(property);
                else
                    SetValue(property, value);
            }
            Return(_homeParent, _homeIndex);
            _homeParent = null;
            if (_owner != null)
                _owner.Closing -= Owner_Closing;
        }

        // the window over the screen closed of itself - Alt+F4 - back where it was; of its owner closing, as the owner is
        private void FullScreenWindow_Closing(object sender, CancelEventArgs e)
        {
            var window = (Window)sender;
            window.Closing -= FullScreenWindow_Closing;
            _fullScreenWindow = null;
            if (!_ownerClosing)
                GoHome(window);
            // of the window gone already, the property's change leaves nothing to do: told of here
            SetCurrentValue(IsFullScreenProperty, false);
            FullScreenChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<bool>(true, false));
        }

        private void Owner_Closing(object sender, CancelEventArgs e)
        {
            // the owner's own window closes the one over the screen with it: the control is to stop, not to go back
            if (!e.Cancel)
                _ownerClosing = true;
        }

        private bool CanLeave(DependencyObject parent)
            => parent is ContentControl content && content.Content == this
            || parent is Decorator decorator && decorator.Child == this
            || parent is Panel panel && panel.Children.Contains(this) && !panel.IsItemsHost;

        private void Leave(DependencyObject parent)
        {
            _homeParent = parent;
            _homeIndex = -1;
            if (parent is ContentControl content)
            {
                content.Content = null;
            }
            else if (parent is Decorator decorator)
            {
                decorator.Child = null;
            }
            else if (parent is Panel panel)
            {
                _homeIndex = panel.Children.IndexOf(this);
                panel.Children.RemoveAt(_homeIndex);
            }
        }

        private void Return(DependencyObject parent, int index)
        {
            if (parent is ContentControl content)
                content.Content = this;
            else if (parent is Decorator decorator)
                decorator.Child = this;
            else if (parent is Panel panel)
                panel.Children.Insert(System.Math.Min(index, panel.Children.Count), this);
        }

        #endregion
    }
}
