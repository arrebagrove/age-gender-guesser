﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Windows.Media.Capture;
using Windows.UI.Core;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Windows.Media.Effects;

using EffectsRuntimeComponent;
using Windows.Graphics.Display;
using Windows.Devices.Enumeration;
using Windows.UI.ViewManagement;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Win2D_Face
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MediaCapture mediaCapture;
        private TaskCompletionSource<object> hasLoaded = new TaskCompletionSource<object>();
        private Face[] lastCapturedFaces;

        bool inCaptureState = true;
        bool processingImage = false;

        // Win2D stuff
        CanvasBitmap photoCanvasBitmap;

        public MainPage()
        {
            this.InitializeComponent();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var action = mediaCapture.StopPreviewAsync();
            mediaCapture.Failed -= mediaCapture_Failed;
        }

        private void mediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            var action = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                progressText.Text = "MediaCapture failed: " + errorEventArgs.Message;
                progressText.Visibility = Visibility.Visible;
            });
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            this.captureElement.Visibility = Visibility.Collapsed;

            await CreateMediaCapture();

            this.captureElement.Visibility = Visibility.Visible;

            hasLoaded.SetResult(null);

            bool isStatusBarAPIPresent = Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar");

            if (isStatusBarAPIPresent)
            {
                await StatusBar.GetForCurrentView().HideAsync();
            }
        }

        private async Task CreateMediaCapture()
        {
            mediaCapture = new MediaCapture();
            mediaCapture.Failed += mediaCapture_Failed;

            var settings = new MediaCaptureInitializationSettings()
            {
                StreamingCaptureMode = StreamingCaptureMode.Video
            };

            // Pick the back camera if one exists
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var device in devices)
            {
                // Check if the device on the requested panel supports Video Profile
                if (device.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back)
                {
                    settings.VideoDeviceId = device.Id;
                    break;
                }
            }

            try
            {
                await mediaCapture.InitializeAsync(settings);
            }
            catch (Exception)
            {
                this.progressText.Text = "No camera is available.";
                return;
            }

            captureElement.Source = mediaCapture;
            await mediaCapture.StartPreviewAsync();

            // Limit the photo capture to be a reasonable size
            var photoStreamProperties = mediaCapture.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.Photo);
            IMediaEncodingProperties mediaEncodingProperties = null;
            foreach (var photoStreamProperty in photoStreamProperties)
            {
                var videoEncodingProperties = (photoStreamProperty as VideoEncodingProperties);
                if (videoEncodingProperties != null)
                {
                    if (videoEncodingProperties.Width * videoEncodingProperties.Height <= 2048 * 1024)
                    {
                        mediaEncodingProperties = photoStreamProperty;
                    }
                }
            }

            await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.Photo, mediaEncodingProperties);

            if (settings.VideoDeviceId == "")
            {
                // If we didn't find a back camera, and the camera we defaulted to is a front camera, then mirror the content
                DeviceInformation info = devices[0];
                if (info.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front)
                {
                    await mediaCapture.AddVideoEffectAsync(new VideoEffectDefinition(typeof(MirrorEffect).FullName, new PropertySet()), MediaStreamType.VideoPreview);
                    await mediaCapture.AddVideoEffectAsync(new VideoEffectDefinition(typeof(MirrorEffect).FullName, new PropertySet()), MediaStreamType.Photo);
                }
            }
        }

        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            if (processingImage)
            {
                // Ignore button presses while processing the image
                return;
            }

            if (inCaptureState)
            {
                processingImage = true;
                inCaptureState = false;

                // Make the 'Processing...' label visible
                canvasControl.Visibility = Visibility.Visible;
                AnalyzeButton.Content = "...";

                canvasControl.Invalidate();

                var originalPhoto = new InMemoryRandomAccessStream();
                var reencodedPhoto = new InMemoryRandomAccessStream();
                await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), originalPhoto);
                await originalPhoto.FlushAsync();
                originalPhoto.Seek(0);

                captureElement.Visibility = Visibility.Collapsed;

                // Store the captured photo as a Win2D type for later use
                photoCanvasBitmap = await CanvasBitmap.LoadAsync(canvasControl, originalPhoto);

                // Send the photo to Project Oxford to detect the faces
                lastCapturedFaces = await faceServiceClient.DetectAsync(originalPhoto.AsStreamForRead(), true, true, true, false);

                // Force the canvasControl to be redrawn now that the photo is available
                canvasControl.Invalidate();

                processingImage = false;
                AnalyzeButton.Content = "Restart";
            }
            else
            {
                canvasControl.Visibility = Visibility.Collapsed;
                captureElement.Visibility = Visibility.Visible;
                AnalyzeButton.Content = "Capture Photo";

                photoCanvasBitmap = null;
                canvasControl.Invalidate();

                inCaptureState = true;
            }
        }

        void canvasControl_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            CanvasTextFormat centeredTextFormat = new CanvasTextFormat();
            centeredTextFormat.HorizontalAlignment = CanvasHorizontalAlignment.Center;
            centeredTextFormat.VerticalAlignment = CanvasVerticalAlignment.Center;
            centeredTextFormat.FontSize = 30;

            if (photoCanvasBitmap != null)
            {
                CanvasRenderTarget tempRenderTarget = new CanvasRenderTarget(sender, photoCanvasBitmap.Size);

                using (CanvasDrawingSession ds = tempRenderTarget.CreateDrawingSession())
                {
                    // Begin by drawing the captured photo into the temporary render target
                    ds.DrawImage(photoCanvasBitmap, new System.Numerics.Vector2(0, 0));

                    foreach (Face face in lastCapturedFaces)
                    {
                        Rect faceRect = new Rect(face.FaceRectangle.Left, face.FaceRectangle.Top, face.FaceRectangle.Width, face.FaceRectangle.Height);
                        ds.DrawRectangle(faceRect, Colors.Red);

                        CanvasPathBuilder pathBuilder = new CanvasPathBuilder(sender);
                        Vector2 startingPoint = new Vector2((float)(faceRect.Left + faceRect.Width / 2), (float)faceRect.Top);
                        pathBuilder.BeginFigure(startingPoint);
                        pathBuilder.AddLine(startingPoint - new Vector2(Math.Max(70.0f, (float)faceRect.Width / 2), 10));
                        pathBuilder.AddLine(startingPoint - new Vector2(Math.Max(70.0f, (float)faceRect.Width / 2), 50));
                        pathBuilder.AddLine(startingPoint + new Vector2(Math.Max(70.0f, (float)faceRect.Width / 2), - 50));
                        pathBuilder.AddLine(startingPoint + new Vector2(Math.Max(70.0f, (float)faceRect.Width / 2), - 10));
                        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

                        // Draw the speech bubble above the face
                        CanvasGeometry geometry = CanvasGeometry.CreatePath(pathBuilder);
                        ds.FillGeometry(geometry, Colors.Yellow);

                        // Draw the person's age and gender above the face
                        String descString = face.Attributes.Gender.ToString() + ", " + face.Attributes.Age.ToString();
                        ds.DrawText(descString, startingPoint - new Vector2(0, 30), Colors.Orange, centeredTextFormat);
                    }
                }

                // End by drawing the rendertarget into the center of the screen
                double imageScale = Math.Min(sender.RenderSize.Width / photoCanvasBitmap.Size.Width, sender.RenderSize.Height / tempRenderTarget.Size.Height);
                double newWidth = imageScale * tempRenderTarget.Size.Width;
                double newHeight = imageScale * tempRenderTarget.Size.Height;
                Rect targetRect = new Rect((sender.RenderSize.Width - newWidth) / 2, (sender.RenderSize.Height - newHeight) / 2, newWidth, newHeight);

                args.DrawingSession.DrawImage(tempRenderTarget, targetRect);
            }
            else
            {
                args.DrawingSession.DrawText("Processing...", (float)(sender.Size.Width / 2), (float)(sender.Size.Height / 2), Colors.White, centeredTextFormat);
            }
        }

        private readonly IFaceServiceClient faceServiceClient =
            new FaceServiceClient("Your subscription key");
    }
}
