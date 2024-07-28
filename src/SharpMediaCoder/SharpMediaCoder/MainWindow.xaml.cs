﻿using SharpMediaFoundation.WPF;
using System.Windows;

namespace SharpMediaFoundation
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public IVideoSource Source { get { return new ScreenSource(); } }
        //public IVideoSource Source { get { return new CameraSource(); } }
        //public IVideoSource Source { get { return new FileSource("frag_bunny.mp4"); } }
        //public IVideoSource Source { get { return new RtspSource("rtsp://127.0.0.1:8554", "admin", "password"); } }

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
        }
    }
}