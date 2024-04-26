using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private PreviewWindow1 previewWindow1;
        private PreviewWindow2 previewWindow2;
        private BackgroundWorker worker;

        private List<string> mediaFiles = new List<string>();
        private List<VideoCapture> players;
        private List<(int index, Mat mat)> mats;

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
                mediaFiles.Clear();
                CreatePlaylist(openFileDialog.FileName);
                PlayButton.IsEnabled = mediaFiles.Count > 0;
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
                    mediaFiles = fileNames.Select(n => n.FileName).Distinct().ToList();

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

            // Create players list
            players = new List<VideoCapture>();
            foreach (var mediaFile in mediaFiles)
            {
                var capture = new VideoCapture(mediaFile);
                if (capture.IsOpened()) players.Add(capture);
            }

            // Create a video playback thread 
            worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            worker.DoWork += (object _, DoWorkEventArgs ea) =>
            {
                bool isVideoEnded = false;

                playbackSleepTime = (int)(1000 / players.First().Fps) / 4;
                int frameHeight = -1;

                while (!worker.CancellationPending)
                {
                    var videoStartTime = DateTime.Now;
                    isVideoEnded = false;
                    Mat frame = null;

                    while (!(worker.CancellationPending || isVideoEnded))
                    {
                        // Process all unique media files and fill Mat list
                        for (int i = 0; i < players.Count; i++)
                        {
                            if (worker.CancellationPending) break;

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
                                var resultMat = new Mat((int)(previewHeight1 + previewHeight2 + frameHeight * 20), (int)previewWidth, frame.Type());

                                // Combine all rendered mats to one, very wide mat
                                Mat wideMat = CombineMats(mats.Select(m => m.mat).ToList());

                                // Fill "off screen" Mat by captured video frames
                                FillRectangularMat(resultMat, wideMat);

                                wideMat.Dispose();

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

                        //Thread.Sleep(playbackSleepTime);
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
            foreach (var player in players) player.Dispose();

            previewWindow1.Hide();
            previewWindow1.Close();

            previewWindow2?.Hide();
            previewWindow2?.Close();

            PlayButton.IsEnabled = players.Count > 0;
            StopButton.IsEnabled = false;
        }

        private Mat CombineMats(List<Mat> frames)
        {
            // Ensure that all frames have the same height
            int height = frames[0].Height;
            // Calculate the total width of the combined image
            int totalWidth = frames.Sum(f =>  f.Width);
            // Create a new Mat with the combined width and the height of the first frame
            Mat combinedImage = new Mat(height, totalWidth, frames[0].Type());

            // Copy each frame to the combined image
            int x_offset = 0;
            foreach (Mat frame in frames)
            {
                // Copy the current frame to the combined image
                Rect roi = new Rect(x_offset, 0, frame.Width, frame.Height);
                frame.CopyTo(combinedImage.SubMat(roi));

                // Update the x offset for the next frame
                x_offset += frame.Width;
            }

            return combinedImage;
        }

        private void FillRectangularMat(Mat destMat, Mat wideMat)
        {
            // Calculate the number of strips needed
            int stripWidth = destMat.Width;
            int numStrips = (int)Math.Ceiling((double)wideMat.Width / stripWidth);

            // Loop through each strip
            for (int i = 0; i < numStrips; i++)
            {
                // Calculate the ROI for the current strip
                Rect roi = new Rect(i * stripWidth, 0, stripWidth, wideMat.Height);

                // Calculate the width of the strip to copy
                int stripCopyWidth = Math.Min(stripWidth, wideMat.Width - i * stripWidth);

                // Copy the strip from the wideMat to the destMat
                wideMat.SubMat(0, wideMat.Height, i * stripWidth, i * stripWidth + stripCopyWidth)
                    .CopyTo(destMat.SubMat(i * wideMat.Height, (i + 1) * wideMat.Height, 0, stripCopyWidth));
            }

            // Fill remaining area of destMat with black
            if (wideMat.Width < destMat.Width)
            {
                Mat blackStrip = new Mat(destMat.Height, destMat.Width - wideMat.Width, MatType.CV_8UC3, Scalar.Black);
                blackStrip.CopyTo(destMat.SubMat(0, destMat.Rows, wideMat.Width, destMat.Width));
            }
        }
    }
}
