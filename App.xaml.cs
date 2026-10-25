using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace PdfMangaViewer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.Activate();

        // コマンドライン引数に PDF が渡されていればそのまま開く
        var pdfPath = FindPdfArgument();
        if (pdfPath is not null)
        {
            window.ScheduleOpen(pdfPath);
        }
    }

    private static string? FindPdfArgument()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
            {
                return arg;
            }
        }

        return null;
    }
}
