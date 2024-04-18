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
using System.Windows.Controls;
using System.Xml.Linq;

namespace MultiPlayer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private PreviewWindow1 previewWindow1;
        private PreviewWindow2 previewWindow2;
        private BackgroundWorker worker;

        private List<VideoCapture> players;
        private List<(int index, Mat mat)> mats;

        private int videoIndex = 0;
        private int playbackSleepTime = 20;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < Screen.AllScreens.Count(); i++)
            {
                Window1ScreenCombo.Items.Add(i.ToString());
                Window2ScreenCombo.Items.Add(i.ToString());
            }
            Window1ScreenCombo.SelectedIndex = 0;
            Window2ScreenCombo.SelectedIndex = 0;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            worker?.CancelAsync();
            previewWindow1?.Hide();
            previewWindow1?.Close();
        }

        private void SelectPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog()
            {
                CheckFileExists = true,
                DefaultExt = ".xml",
                InitialDirectory = "D:\\4",
                Filter = "XML files (.xml)|*.xml",
                Title = "Select playlist"
            };

            // Show open file dialog box
            if (openFileDialog.ShowDialog() == true)
            {
                Playlist.Text = openFileDialog.FileName;
                CreatePlaylist(openFileDialog.FileName);
                PlayButton.IsEnabled = players.Count > 0;
            }
        }

        /// <summary>
        /// Create a playlsist from XML file
        /// </summary>
        /// <param name="xmlFilePath"></param>
        /// <returns></returns>
        private void CreatePlaylist(string xmlFilePath)
        {
            var fileNames = new List<(string FileName, TimeSpan Duration)>();

            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(xmlFilePath);
                var fileNodes = xmlDoc.SelectNodes("//File");
                if (fileNodes != null && fileNodes.Count > 0)
                {
                    // Special fix for misstype in the "Duration" attribute name
                    var durationName = fileNodes.Item(0).SelectSingleNode("Duration") == null ? "Duraion" : "Duration";

                    foreach (XmlNode fileNode in fileNodes)
                    {
                        var fileName = fileNode.SelectSingleNode("Filename")?.InnerText;

                        // Create full path based on file name and file directory
                        var mediaPath = Path.GetDirectoryName(xmlFilePath);
                        fileName = Path.GetFileName(fileName);
                        fileName = Path.Combine(mediaPath, fileName);

                        var durationStr = fileNode.SelectSingleNode(durationName)?.InnerText;
                        if (File.Exists(fileName) && TimeSpan.TryParseExact(durationStr, @"mm\:ss", null, out TimeSpan duration))
                        {
                            // Zero seconds duration means duration == video length
                            if (duration.TotalSeconds == 0) duration = TimeSpan.MaxValue;
                            var fi = new FileInfo(fileName);
                            if (fi.Length > 1024 * 1024) fileNames.Add((fileName, duration));
                        }
                    }

                    // Get list of unique media files
                    var mediaFiles = fileNames.Select(n => n.FileName).Distinct().ToList();

                    // Create players list
                    players = new List<VideoCapture>();
                    foreach (var mediaFile in mediaFiles)
                    {
                        var capture = new VideoCapture(mediaFile);
                        if (capture.IsOpened()) players.Add(capture);
                    }

                    mats = new List<(int index, Mat mat)>();
                    foreach(var fileName in fileNames)
                    {
                        var index = mediaFiles.IndexOf(fileName.FileName);
                        mats.Add((index, null));
                    }

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error: " + ex.Message);
            }
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

            double previewWidth, previewHeight1, previewHeight2 = 0;

            // First, create, position and maximize output window for specified screen
            previewWindow1 = new PreviewWindow1();
            var screen = Screen.AllScreens.ElementAt(Window1ScreenCombo.SelectedIndex);
            previewWindow1.Left = screen.Bounds.Left + int.Parse(Window1Left.Text);
            previewWindow1.Top = screen.Bounds.Top + int.Parse(Window1Top.Text);
            previewWindow1.Width = previewWidth = int.Parse(WindowsWidth.Text);
            previewWindow1.Height = previewHeight1 = int.Parse(Window1Height.Text);
            previewWindow1.PreviewImage1.Width = previewWidth;
            previewWindow1.PreviewImage1.Height = previewHeight1;
            previewWindow1.Show();

            if (ShowWindow2.IsChecked == true)
            {
                previewWindow2 = new PreviewWindow2();
                screen = Screen.AllScreens.ElementAt(Window2ScreenCombo.SelectedIndex);
                previewWindow2.Left = screen.Bounds.Left + int.Parse(Window2Left.Text);
                previewWindow2.Top = screen.Bounds.Top + int.Parse(Window2Top.Text);
                previewWindow2.Width = previewWidth;
                previewWindow2.Height = previewHeight2 = int.Parse(Window2Height.Text);
                previewWindow2.PreviewImage2.Width = previewWidth;
                previewWindow2.PreviewImage2.Height = previewHeight1;
                previewWindow2.Show();
            }

            // We should scale width & height by used scale factor
            //previewWidth = previewWidth / screen.ScaleFactor;
            //previewHeight = previewHeight / screen.ScaleFactor;

            // Create a video playback thread 
            videoIndex = 0;
            worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            worker.DoWork += (object worker, DoWorkEventArgs ea) =>
            {
                bool isCancelled = false, isVideoEnded = false;

                playbackSleepTime = (int)(1000 / players.First().Fps);
                int frameHeight = -1;

                while (true)
                {
                    var videoStartTime = DateTime.Now;
                    isVideoEnded = false;
                    Mat frame = null;

                    while (!(isCancelled || isVideoEnded))
                    {
                        // Process all unique media files and fill Mat list
                        for (int i = 0; i < players.Count; i++)
                        {
                            frame = players[i].RetrieveMat();

                            // Rewind player by the end of video
                            if (frame.Empty())
                            {
                                players[i].Set(VideoCaptureProperties.PosFrames, 0);
                                frame = players[i].RetrieveMat();
                            }

                            if (frame != null && !frame.Empty())
                            {
                                if (frameHeight < 0) frameHeight = frame.Height;

                                for (int j = 0; j < mats.Count; j++)
                                    if (mats[j].index == i)
                                        mats[j] = (i, frame);
                            }
                        }

                        Dispatcher.Invoke(() =>
                        {
                            if (previewWindow1 != null && previewWindow1.IsVisible)
                            {
                                // Create a large "off screen" Mat. It should be higher than screen height
                                var resultMat = new Mat((int)(previewHeight1 + previewHeight2 + frameHeight * 2), (int)previewWidth, frame.Type());

                                // Fill "off screen" Mat by captured video frames
                                FillMat(resultMat, mats.Select(m => m.mat).ToList());

                                // Copy filled "off screen" Mat to resulting Image
                                if (ShowWindow2.IsChecked == false)
                                {
                                    previewWindow1.PreviewImage1.Source = resultMat.ToWriteableBitmap();
                                }
                                else
                                {
                                    // If two window out selected, split result Mat for two images
                                    previewWindow1.PreviewImage1.Source = resultMat.SubMat(0, (int)previewHeight1, 0, (int)previewWidth).ToWriteableBitmap();
                                    if (previewWindow2 != null)
                                        previewWindow2.PreviewImage2.Source = resultMat.SubMat((int)previewHeight1 + 1, resultMat.Height - 1, 0, (int)previewWidth).ToWriteableBitmap();
                                }
                            }
                        });

                        Thread.Sleep(playbackSleepTime);
                    }

                    // Exit playback if thread is cancelled
                    if (isCancelled) break;
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

            previewWindow1.Hide();
            previewWindow1.Close();

            previewWindow2?.Hide();
            previewWindow2?.Close();

            PlayButton.IsEnabled = players.Count > 0;
            StopButton.IsEnabled = false;
        }

        /// <summary>
        /// Fills large Mat by content of the small Mat with overlapping
        /// </summary>
        /// <param name="largeMat">Large resulting Mat</param>
        /// <param name="smallMat">Video frame</param>
        public static void FillMat(Mat largeMat, List<Mat> smallMats)
        {
            if (smallMats[0] == null) return;

            // Get dimensions of the large and small Mats
            int largeWidth = largeMat.Width;
            int largeHeight = largeMat.Height;
            int smallMatIndex = 0;
            int startX = 0;
            int smallHeight = smallMats[0].Height;
            int rowHeight = smallHeight;

            // Loop through large Mat in rows
            for (int y = 0; y < largeHeight - smallHeight * 2; y += smallHeight)
            {
                // Loop through large Mat in columns
                for (int x = startX; x < largeWidth; x += smallMats[smallMatIndex].Width)
                {
                    int smallWidth = smallMats[smallMatIndex].Width;

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
                    Mat smallSubMat = smallMats[smallMatIndex].SubMat(smallRoi);
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
                            smallSubMat = smallMats[smallMatIndex].SubMat(0, rowHeight, copyWidth, smallWidth);
                            smallSubMat.CopyTo(wrapSubMat);
                        }
                    }

                    // Update the start X position for the next column
                    startX = x + colWidth;
                    if (startX >= largeWidth) startX = wrapWidth;

                    // Switch to next Mat 
                    smallMatIndex++;
                    if (smallMatIndex >= smallMats.Count) smallMatIndex = 0;
                }
            }
        }
    }
}
