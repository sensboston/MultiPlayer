using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Xml;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfScreenHelper;
using Window = System.Windows.Window;
using Rect = OpenCvSharp.Rect;

namespace MultiPlayer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private PreviewWindow previewWindow;
        private BackgroundWorker worker;
        private List<(string FileName, TimeSpan Duration)> playlist;
        private int videoIndex = 0;
        private int playbackSleepTime = 20;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i< Screen.AllScreens.Count(); i++)
                ScreenCombo.Items.Add(i.ToString());
            ScreenCombo.SelectedIndex = 0;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            worker?.CancelAsync();
            previewWindow?.Hide();
            previewWindow?.Close();
        }

        private void SelectPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog()
            {
                CheckFileExists = true,
                DefaultExt = ".xml",
                InitialDirectory = "D:\\3",
                Filter = "XML files (.xml)|*.xml",
                Title = "Select playlist"
            };

            // Show open file dialog box
            if (ofd.ShowDialog() == true)
            {
                Playlist.Text = ofd.FileName;
                playlist = GetFileNamesFromXml(ofd.FileName);
                PlayButton.IsEnabled = playlist.Count > 0;
            }
        }

        /// <summary>
        /// Create a playlsist from XML file
        /// </summary>
        /// <param name="xmlFilePath"></param>
        /// <returns></returns>
        private List<(string FileName, TimeSpan Duration)> GetFileNamesFromXml(string xmlFilePath)
        {
            var fileNames = new List<(string FileName, TimeSpan Duration)>();

            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(xmlFilePath);
                var fileNodes = xmlDoc.SelectNodes("//File");
                foreach (XmlNode fileNode in fileNodes)
                {
                    var fileName = fileNode.SelectSingleNode("Filename")?.InnerText;
                    var durationStr = fileNode.SelectSingleNode("Duration")?.InnerText;
                    if (File.Exists(fileName) && TimeSpan.TryParseExact(durationStr, @"mm\:ss", null, out TimeSpan duration))
                    {
                        // Zero seconds duration means duration == video length
                        if (duration.TotalSeconds == 0) duration = TimeSpan.MaxValue;
                        var fi = new FileInfo(fileName);
                        if (fi.Length > 1024 * 1024) fileNames.Add((fileName, duration));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error: " + ex.Message);
            }

            return fileNames;
        }

        /// <summary>
        /// Starts playlist playback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            PlayButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            double previewWidth, previewHeight;

            // First, create, position and maximize output window for specified screen
            previewWindow = new PreviewWindow();
            var screen = Screen.AllScreens.ElementAt(ScreenCombo.SelectedIndex);
            previewWindow.Left = screen.Bounds.Left;
            previewWindow.Top = screen.Bounds.Top;
            previewWindow.Width = previewWidth = screen.Bounds.Width;
            previewWindow.Height = previewHeight = screen.Bounds.Height;
            previewWindow.PreviewImage.Width = previewWidth;
            previewWindow.PreviewImage.Height = previewHeight;
            previewWindow.Show();
            previewWindow.WindowState = WindowState.Maximized;

            // We should scale width & height by used scale factor
            previewWidth = previewWidth / screen.ScaleFactor;
            previewHeight = previewHeight / screen.ScaleFactor;

            // Create a video playback thread 
            videoIndex = 0;
            worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            worker.DoWork += (object worker, DoWorkEventArgs ea) =>
            {
                bool isCancelled = false, isVideoEnded = false;

                while (true)
                { 
                    // Create a video player. Only one player will be used for playback
                    var videoPlayer = new VideoCapture(playlist[videoIndex].FileName);
                    
                    if (videoPlayer.IsOpened())
                    {
                        var videoStartTime = DateTime.Now;
                        isVideoEnded = false;
                        playbackSleepTime = (int)(1000 / videoPlayer.Fps);

                        Mat frame = null;

                        while (!(isCancelled || isVideoEnded))
                        {
                            try
                            {
                                frame = videoPlayer.IsOpened() ? videoPlayer.RetrieveMat() : null;

                                if (frame != null)
                                {
                                    // Are we reached end of the video or end of desired duration?
                                    if (frame.Empty() ||
                                        DateTime.Now - videoStartTime > playlist[videoIndex].Duration)
                                    {
                                        isVideoEnded = true;
                                        continue;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            // Create a large "off screen" Mat. It should be higher than screen height
                                            var resultMat = new Mat((int) (previewHeight + frame.Height*2), (int)previewWidth, frame.Type());

                                            // Fill "off screen" Mat by video frame
                                            FillMat(resultMat, frame);

                                            // Copy filled "off screen" Mat to resulting Image
                                            previewWindow.PreviewImage.Source = resultMat.ToWriteableBitmap();
                                        });
                                    }
                                    Thread.Sleep(playbackSleepTime);
                                }
                            }
                            catch { break; }
                        }
                        // Rewind playlist
                        if (++videoIndex >= playlist.Count) videoIndex = 0;

                        // Exit playback if thread is cancelled
                        if (isCancelled) break;
                    }
                }
            };
            worker.RunWorkerAsync();
        }

        /// <summary>
        /// Stops playback and close window
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            worker.CancelAsync();
            previewWindow.Hide();
            previewWindow.Close();
            PlayButton.IsEnabled = playlist.Count > 0;
            StopButton.IsEnabled = false;
        }

        /// <summary>
        /// Fills large Mat by content of the small Mat with overlapping
        /// </summary>
        /// <param name="largeMat">Large resulting Mat</param>
        /// <param name="smallMat">Video frame</param>
        public static void FillMat(Mat largeMat, Mat smallMat)
        {
            // Get dimensions of the large and small Mats
            int largeWidth = largeMat.Width;
            int largeHeight = largeMat.Height;
            int smallWidth = smallMat.Width;
            int smallHeight = smallMat.Height;
            int rowHeight = smallHeight;
            int startX = 0;

            // Loop through large Mat in rows
            for (int y = 0; y < largeHeight - smallHeight*2; y += smallHeight)
            {
                // Loop through large Mat in columns
                for (int x = startX; x < largeWidth; x += smallWidth)
                {
                    // Calculate the width of the current column
                    int colWidth = Math.Min(smallWidth, largeWidth - x);

                    // Define the region of interest (ROI) in the large Mat
                    Rect roi = new Rect(x, y, colWidth, rowHeight);

                    // Calculate the actual size of the small Mat to copy
                    int copyWidth = Math.Min(smallWidth, largeWidth - x);
                    int copyHeight = Math.Min(smallHeight, largeHeight - y);

                    // If we are at the right or bottom edge, adjust the copy size
                    if (x + smallWidth > largeWidth) copyWidth = largeWidth - x;
                    if (y + smallHeight > largeHeight) copyHeight = largeHeight - y;

                    // Define the region of interest (ROI) in the small Mat
                    Rect smallRoi = new Rect(0, 0, copyWidth, copyHeight);

                    // Copy the data from the small Mat to the large Mat
                    Mat smallSubMat = smallMat.SubMat(smallRoi);
                    Mat largeSubMat = largeMat.SubMat(roi);
                    smallSubMat.CopyTo(largeSubMat);

                    int wrapWidth = smallWidth - copyWidth;

                    // If the entire small Mat did not fit in the row, wrap the remaining part to the next row
                    if (copyWidth < smallWidth)
                    {
                        int nextRowX = 0;
                        int nextRowY = y + rowHeight;

                        // Ensure the next row exists within the boundaries of the large Mat
                        if (nextRowY < largeHeight)
                        {
                            Rect wrapRoi = new Rect(nextRowX, nextRowY, wrapWidth, rowHeight);
                            Mat wrapSubMat = largeMat.SubMat(wrapRoi);
                            smallSubMat = smallMat.SubMat(0, rowHeight, copyWidth, smallWidth);
                            smallSubMat.CopyTo(wrapSubMat);
                        }
                    }

                    // Update the start X position for the next column
                    startX = x + colWidth;
                    if (startX >= largeWidth) startX = wrapWidth;
                }
            }
        }
    }
}
