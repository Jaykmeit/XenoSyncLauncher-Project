using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XenoSyncLauncher.Settings;

public partial class QrLoginWindow : Window
{
    private readonly Action _onCancel;

    /// <summary>Vertical pixel size of one row (one console line = one module row).</summary>
    private const int ModuleHeight = 8;

    /// <summary>
    /// Horizontal pixel size per SOURCE CHARACTER (not per module). Console
    /// QR renderers commonly draw each square module as two characters wide
    /// by one tall, to compensate for console character cells not being
    /// square - so two source columns make up one true module. Using half
    /// of ModuleHeight here keeps the final image properly square instead of
    /// coming out stretched twice as wide as it should be.
    /// </summary>
    private const int ModuleWidthPerChar = ModuleHeight / 2;

    /// <summary>The character DepotDownloader's block-art decodes to for a dark module (see DepotDownloaderService remarks).</summary>
    private const char DarkModuleChar = '\u00DB'; // 'Û', same value as QrDarkModuleChar

    public QrLoginWindow(string[] qrAsciiLines, Action onCancel)
    {
        InitializeComponent();
        _onCancel = onCancel;

        SetQrAsciiBlock(qrAsciiLines);
    }

    /// <summary>
    /// Re-renders the QR image. DepotDownloader may issue a fresh challenge
    /// (a new block of ASCII art) if the previous one expires before it's
    /// scanned, so the caller can call this again on the same window instead
    /// of opening a new one.
    /// </summary>
    public void SetQrAsciiBlock(string[] qrAsciiLines)
    {
        QrImage.Source = RenderAsciiQr(qrAsciiLines);
        StatusText.Text = "Waiting for confirmation...";
    }

    public void SetStatus(string status) => StatusText.Text = status;

    /// <summary>
    /// Builds a real black-and-white bitmap from DepotDownloader's console
    /// QR block-art: each source character becomes a ModuleWidthPerChar x
    /// ModuleHeight rectangle (two characters combining into one square
    /// module), dark where the line has DarkModuleChar, white everywhere
    /// else (including short lines/padding).
    /// </summary>
    private static BitmapSource RenderAsciiQr(string[] lines)
    {
        int rows = lines.Length;
        int cols = lines.Length == 0 ? 0 : lines.Max(l => l.Length);

        int pixelWidth = Math.Max(1, cols * ModuleWidthPerChar);
        int pixelHeight = Math.Max(1, rows * ModuleHeight);

        var pixels = new byte[pixelWidth * pixelHeight]; // Gray8: 1 byte/pixel
        Array.Fill(pixels, (byte)255); // white background

        for (int row = 0; row < rows; row++)
        {
            var line = lines[row];
            for (int col = 0; col < line.Length; col++)
            {
                if (line[col] != DarkModuleChar) continue;

                for (int dy = 0; dy < ModuleHeight; dy++)
                {
                    int y = row * ModuleHeight + dy;
                    int rowStart = y * pixelWidth + col * ModuleWidthPerChar;
                    for (int dx = 0; dx < ModuleWidthPerChar; dx++)
                        pixels[rowStart + dx] = 0; // black
                }
            }
        }

        var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Gray8, null, pixels, pixelWidth);
        bitmap.Freeze();
        return bitmap;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _onCancel();
        Close();
    }

    /// <summary>Botón X de la barra de título personalizada: mismo comportamiento que Cancel (dispara _onCancel para detener el login/descarga en curso).</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => CancelButton_Click(sender, e);
}