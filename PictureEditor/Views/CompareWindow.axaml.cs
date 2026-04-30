using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace PictureEditor.Views;

public partial class CompareWindow : Window
{
    public CompareWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    public void ShowImageForCompare(string fileNameAndPath)
    {
        DisposeImage();

        var bmp = new Bitmap(fileNameAndPath);
        imgCompare.Source = bmp;

        int screenWidth = (int)(Screens.Primary?.Bounds.Width ?? 1920) - 15;
        int screenHeight = (int)(Screens.Primary?.Bounds.Height ?? 1080) - 15;
        int picWidth = bmp.PixelSize.Width;
        int picHeight = bmp.PixelSize.Height;

        float ratioWidth = (float)screenWidth / picWidth;
        float ratioHeight = (float)screenHeight / picHeight;
        float ratioToUse = Math.Min(ratioWidth, ratioHeight);

        Width = (int)Math.Floor(picWidth * ratioToUse);
        Height = (int)Math.Floor(picHeight * ratioToUse);
    }

    public void DisposeImage()
    {
        if (imgCompare.Source is Bitmap oldBmp)
        {
            imgCompare.Source = null;
            oldBmp.Dispose();
        }
    }
}
