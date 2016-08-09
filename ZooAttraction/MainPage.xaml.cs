using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Emotion.Contract;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace ZooAttraction
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {

        //keys need to be changed
        private const string FaceApiKey = "2698749f4e5340a3a6e2531ac29d4960";  
        private const string EmotionApiKey = "35e54071335b4f0c8ee654205aa2de5c";

        private const int ControlLoopDelayMilliseconds = 3000; // Countdown
        private static readonly FaceServiceClient faceServiceClient = new FaceServiceClient(FaceApiKey);
        private static readonly EmotionServiceClient emotionServiceClient = new EmotionServiceClient(EmotionApiKey);

        private MediaCapture mediaCapture;

        public MainPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Window.Current.SizeChanged += Current_SizeChanged;

#if !DEBUG
            try
            {
#endif
            await StartPreviewAsync();
            await RunControlLoopAsync();
#if !DEBUG
            }
            catch (Exception ex)
            {
            await new MessageDialog(ex.ToString()).ShowAsync();
            Application.Current.Exit();
            }
#endif
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Window.Current.SizeChanged -= Current_SizeChanged;
        }

        private void Current_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            // If the window is resized, delete any face rectangles.
            // TODO Recalculate face rectangle positions instead of just deleting them.
            FaceResultsGrid.Children.Clear();
        }

        private async Task StartPreviewAsync()
        {
            await UpdateStatusAsync("Đang khởi động video xem trước...");

            // Attempt to get the front camera if one is available, but use any camera device if not
            DeviceInformation cameraDevice;
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
            cameraDevice = desiredDevice ?? allVideoDevices.FirstOrDefault();
            if (cameraDevice == null) throw new Exception("Không tìm thấy camera trên thiết bị của ban.");

            // Create MediaCapture and its settings, then start the view preview
            mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id, StreamingCaptureMode = StreamingCaptureMode.Video };
            await mediaCapture.InitializeAsync(settings);
            PreviewCaptureElement.Source = mediaCapture;
            await mediaCapture.StartPreviewAsync();
        }

        /// <summary>
        /// This is an infinite loop which
        /// takes a picture with the attached camera,
        /// displays it,
        /// sends it for recognition to Azure Cognitive Services,
        /// displays recognition results overlaid on the picture,
        /// waits for 3 seconds to allow the result to be examined,
        /// starts over.
        /// </summary>-+-
        private async Task RunControlLoopAsync()
        {
            while (true)
            {
                // Take camera picture
                await UpdateStatusAsync("Hãy chụp lấy một bức ảnh...");

                FaceResultsGrid.Children.Clear();
                CountdownProgressBar.Value = 100;
                CameraFlashStoryboard.Begin();

                using (var stream = new InMemoryRandomAccessStream())
                {
                    var imageEncodingProperties = ImageEncodingProperties.CreateJpeg();
                    imageEncodingProperties.Width = 640;
                    imageEncodingProperties.Height = 480;
                    await mediaCapture.CapturePhotoToStreamAsync(imageEncodingProperties, stream);

                    // Display camera picture
                    await UpdateStatusAsync("Hiển thị ảnh xem trước...");

                    stream.Seek(0);
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(stream);
                    ResultImage.Source = bitmapImage;

                    // Send picture for recognition
                    // We need to encode the raw image as a JPEG to make sure the service can recognize it.
                    await UpdateStatusAsync("Đang upload hình ảnh lên Azure Cognitive Services...");
                    stream.Seek(0);

                    //cloning the image to upload for emotion API
                    var emotionStream = stream.CloneStream();

                    var recognizedFaces = await GetFaces(stream.AsStreamForRead());
                    var adults = recognizedFaces.Count(f => f.FaceAttributes.Age >= 18);
                    var children = recognizedFaces.Count(f => f.FaceAttributes.Age < 18);
                    
                    var emotions = await GetEmotions(emotionStream.AsStreamForRead());

                    // Display recognition results
                    // Wait a few seconds seconds to give viewers a chance to appreciate all we've done
                    await UpdateStatusAsync($"Nhận thấy có {adults} người lớn, {children} trẻ em");

                    // The face rectangles received from Face API are measured in pixels of the raw image.
                    // We need to calculate the extra scaling and displacement that results from the raw image
                    // being displayed in a larger container.
                    // We use the FaceResultsGrid as a basis for the calculation, because the ResultImage control's ActualHeight and ActualWidth
                    // properties have the same aspect ratio as the image, and not the aspect ratio of the screen.
                    double widthScaleFactor = FaceResultsGrid.ActualWidth / bitmapImage.PixelWidth;
                    double heightScaleFactor = FaceResultsGrid.ActualHeight / bitmapImage.PixelHeight;
                    double scaleFactor = Math.Min(widthScaleFactor, heightScaleFactor);

                    bool isTheBlackSpaceOnTheLeft = widthScaleFactor > heightScaleFactor;
                    double extraLeftNeeded = 0;
                    double extraTopNeeded = 0;
                    if (isTheBlackSpaceOnTheLeft) extraLeftNeeded = (FaceResultsGrid.ActualWidth - scaleFactor * bitmapImage.PixelWidth) / 2;
                    else extraTopNeeded = (FaceResultsGrid.ActualHeight - scaleFactor * bitmapImage.PixelHeight) / 2;

                    //merging the faces and emotions in a single list    
                    var ageemotions = recognizedFaces.Zip(emotions, (a, e) => new { face = a, emotion = e });
                    
                    foreach (var ageemotion in ageemotions)
                    {
                        var faceOutlineRectangleLeft = extraLeftNeeded + scaleFactor * ageemotion.face.FaceRectangle.Left;
                        var faceOutlineRectangleTop = extraTopNeeded + scaleFactor * ageemotion.face.FaceRectangle.Top;
                        var faceOutlineRectangleHeight = scaleFactor * ageemotion.face.FaceRectangle.Height;
                        var faceOutlineRectangleWidth = scaleFactor * ageemotion.face.FaceRectangle.Width;

                        Rectangle faceOutlineRectangle = new Rectangle();
                        faceOutlineRectangle.Stroke = new SolidColorBrush(Colors.OrangeRed);
                        faceOutlineRectangle.StrokeThickness = 3;
                        faceOutlineRectangle.HorizontalAlignment = HorizontalAlignment.Left;
                        faceOutlineRectangle.VerticalAlignment = VerticalAlignment.Top;
                        faceOutlineRectangle.Margin = new Thickness(faceOutlineRectangleLeft, faceOutlineRectangleTop, 0, 0);
                        faceOutlineRectangle.Height = faceOutlineRectangleHeight;
                        faceOutlineRectangle.Width = faceOutlineRectangleWidth;
                        FaceResultsGrid.Children.Add(faceOutlineRectangle);

                        TextBlock faceInfoTextBlock = new TextBlock();
                        faceInfoTextBlock.Foreground = new SolidColorBrush(Colors.White);
                        faceInfoTextBlock.FontSize = 30;
                        faceInfoTextBlock.Text = $"{GetGenderString(ageemotion.face.FaceAttributes.Gender)}, {Math.Floor(ageemotion.face.FaceAttributes.Age)}";
                        Border faceInfoBorder = new Border();
                        faceInfoBorder.Background = new SolidColorBrush(Colors.Black);
                        faceInfoBorder.Padding = new Thickness(5);
                        faceInfoBorder.Child = faceInfoTextBlock;
                        faceInfoBorder.HorizontalAlignment = HorizontalAlignment.Left;
                        faceInfoBorder.VerticalAlignment = VerticalAlignment.Top;
                        faceInfoBorder.Margin = new Thickness(faceOutlineRectangleLeft, faceOutlineRectangleTop - 50, 0, 0);
                        FaceResultsGrid.Children.Add(faceInfoBorder);

                        TextBlock recommendationInfoTextBlock = new TextBlock();
                        recommendationInfoTextBlock.Foreground = new SolidColorBrush(Colors.White);
                        recommendationInfoTextBlock.FontSize = 10;
                        recommendationInfoTextBlock.Text = BuildEmotionList(ageemotion.emotion.Scores.ToRankedList());
                        Border recommendationInfoBorder = new Border();
                        recommendationInfoBorder.Background = new SolidColorBrush(Colors.OrangeRed);
                        recommendationInfoBorder.Padding = new Thickness(1);
                        recommendationInfoBorder.Child = recommendationInfoTextBlock;
                        recommendationInfoBorder.HorizontalAlignment = HorizontalAlignment.Left;
                        recommendationInfoBorder.VerticalAlignment = VerticalAlignment.Top;
                        recommendationInfoBorder.Margin = new Thickness(faceOutlineRectangleLeft, faceOutlineRectangleTop + faceOutlineRectangleHeight, 0, 0);
                        FaceResultsGrid.Children.Add(recommendationInfoBorder);
                    }
                }

                CountdownStoryboard.Begin();
                await Task.Delay(ControlLoopDelayMilliseconds);
            }
        }


        /*
         * Calling the Face API and getting back the coordinate for the detected faces, genders and age
         */
        private static async Task<Face[]> GetFaces(Stream stream)
        {
            //implemting local variable to get face attributes
            var requiredFaceAttributes = new FaceAttributeType[] {
                FaceAttributeType.Age,
                FaceAttributeType.Gender,
                FaceAttributeType.Glasses
            };

            var result = await faceServiceClient.DetectAsync(stream, true, true, returnFaceAttributes: requiredFaceAttributes);
            return result;
        }

        private string GetGenderString(string originalValue)
        {
            if (originalValue == "male")
                return "Man";
            else
                return "Woman";
        }


        /*
         * Method to to update the layout with the ongoing operation
         */
        private async Task UpdateStatusAsync(string message)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                () =>
                {
                    StatusTextBlock.Text = message;
                });
        }

        /*
         * Calling the Emotion API and getting back the emotion scores for the dected faces
         */
        private static async Task<Emotion[]> GetEmotions(Stream stream)
        {

            var result = await emotionServiceClient.RecognizeAsync(stream);
            return result;
        }
        /*
         * Taking the list emotion scores and building a string with all the scores to be displayed
         * The list is sorted from the highest to the lowest score
         */
        private string BuildEmotionList(IEnumerable<KeyValuePair<string, float>> scores)
        {

            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            foreach (var score in scores)
            {
                sb.AppendLine(score.Key + " : " + score.Value);
            }

            return sb.ToString();
        }

    }
}
